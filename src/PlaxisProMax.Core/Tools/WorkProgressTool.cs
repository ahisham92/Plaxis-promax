using System.Text.Json;
using PlaxisProMax.Settings;

namespace PlaxisProMax.Tools;

/// <summary>
/// work_progress (from Sketch ProMax): the plan and progress of a long job, kept per project in
/// %APPDATA%\PlaxisProMax\progress, so it is picked up after "continue", in a new chat, or after Plaxis was restarted.
/// </summary>
internal sealed class WorkProgressTool : IPlaxisTool
{
    /// <summary>Set only by the tests.</summary>
    internal static string? FolderOverride;

    public string Name => "work_progress";
    public string Description =>
        "Keep the plan and progress of a long job (e.g. borehole logs to a calculated model with a report) saved for this project, " +
        "so it can be picked up after 'continue', in a new chat, or after Plaxis was restarted. 'set' saves the goal and steps, " +
        "'update' marks a step done/failed/skipped with a note (names of what was made, the phase reached, files written), " +
        "'get' reads it back. Read it first when the user says continue.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["get", "set", "update", "clear"]},
      "goal": {"type": "string", "description": "set: the job in one line."},
      "steps": {"type": "array", "items": {"type": "string"}, "description": "set: the steps in order."},
      "step": {"type": "integer", "description": "update: step number (1-based)."},
      "status": {"type": "string", "enum": ["pending", "in_progress", "done", "failed", "skipped"]},
      "note": {"type": "string", "description": "update: what was made or what went wrong (names, phases, files), for the next steps."}
    }
    """;
    public string[] Required => ["action"];
    public bool ModifiesModel => false;

    private sealed class Step { public string Text { get; set; } = ""; public string Status { get; set; } = "pending"; public string? Note { get; set; } }
    private sealed class Plan { public string Goal { get; set; } = ""; public List<Step> Steps { get; set; } = []; public string Updated { get; set; } = ""; }

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var folder = FolderOverride ?? Path.Combine(AgentSettings.Folder, "progress");
        var project = RedoLog.ProjectOf(context);
        if (project.Length == 0) project = "unsaved project";
        var file = Path.Combine(folder, string.Concat(project.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c)) + ".json");
        Plan? Load()
        {
            try { return File.Exists(file) ? JsonSerializer.Deserialize<Plan>(File.ReadAllText(file)) : null; }
            catch (Exception ex) when (ex is JsonException or IOException) { return null; }
        }
        void Save(Plan p)
        {
            Directory.CreateDirectory(folder);
            p.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            File.WriteAllText(file, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        }
        object View(Plan p) => new
        {
            project, goal = p.Goal, updated = p.Updated,
            next = p.Steps.FirstOrDefault(s => s.Status is "pending" or "in_progress" or "failed") is { } n ? $"{p.Steps.IndexOf(n) + 1}. {n.Text}" : "all steps finished",
            steps = p.Steps.Select((s, i) => new { n = i + 1, step = s.Text, status = s.Status, note = s.Note }),
        };

        switch (args.String("action"))
        {
            case "get":
                return Task.FromResult(Load() is { } saved ? ToolResult.Ok(View(saved)) : ToolResult.Ok(new { goal = (string?)null, note = "No saved plan for this project." }));
            case "set":
            {
                var steps = args.StringList("steps");
                if (steps.Count == 0) throw new ArgumentException("Give the steps.");
                var plan = new Plan { Goal = args.OptString("goal") ?? "", Steps = steps.Select(s => new Step { Text = s }).ToList() };
                Save(plan);
                return Task.FromResult(ToolResult.Ok(View(plan)));
            }
            case "update":
            {
                var plan = Load() ?? throw new ArgumentException("No saved plan; use action 'set' first.");
                var n = (int)(args.OptLong("step") ?? throw new ArgumentException("Give step."));
                if (n < 1 || n > plan.Steps.Count) throw new ArgumentException($"step must be 1 to {plan.Steps.Count}.");
                plan.Steps[n - 1].Status = args.OptString("status") ?? "done";
                if (args.OptString("note") is { } note) plan.Steps[n - 1].Note = note.Length > 2000 ? note.Substring(0, 2000) : note;
                Save(plan);
                return Task.FromResult(ToolResult.Ok(View(plan)));
            }
            case "clear":
                if (File.Exists(file)) File.Delete(file);
                return Task.FromResult(ToolResult.Ok(new { cleared = true }));
            default:
                throw new ArgumentException("action is get, set, update or clear.");
        }
    }
}

/// <summary>kept_code: lists everything kept, or deletes an entry the user no longer wants (Sketch ProMax's list and delete of learned tools).</summary>
internal sealed class KeptCodeTool : IPlaxisTool
{
    public string Name => "kept_code";
    public string Description =>
        "The kept code as a whole. action=list (default): every kept entry with what it does, its inputs, how often it ran and when it " +
        "was last used, most used first; use it when the user asks what Plaxis ProMax has learned or kept. action=delete with id removes " +
        "an entry; only when the user asks. To find code for a task use find_kept_code.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["list", "delete"], "description": "Default list."},
      "id": {"type": "string", "description": "delete: the kept code's id."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        if ((args.OptString("action") ?? "list") == "delete")
        {
            var id = args.OptString("id") ?? throw new ArgumentException("delete needs the id.");
            return Task.FromResult(context.Library.Delete(id) ? ToolResult.Ok(new { deleted = id }) : ToolResult.Error($"No kept code {id}. Call kept_code to list them."));
        }
        var all = context.Library.All.OrderByDescending(e => e.Uses).ThenByDescending(e => e.LastUsed).ToList();
        return Task.FromResult(ToolResult.Ok(new
        {
            kept = all.Count,
            folder = context.Library.Folder,
            entries = all.Take(200).Select(e => new { e.Id, e.Description, e.Inputs, e.Uses, e.Revision, last_used = e.LastUsed.ToString("yyyy-MM-dd"), runs_without_asking = e.Allowed }),
        }));
    }
}
