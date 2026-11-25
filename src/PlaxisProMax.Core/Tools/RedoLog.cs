using System.Text;
using System.Text.Json;
using PlaxisProMax.Settings;

namespace PlaxisProMax.Tools;

/// <summary>
/// Every change the chat made to a project that worked, with its full arguments and code, so it can be done again
/// exactly (Sketch ProMax's RedoLog): after Plaxis closed before the project was saved, or on a fresh copy of the
/// project. One file per project name in %APPDATA%\PlaxisProMax\redo; the activity log can't serve, it cuts long arguments.
/// </summary>
internal static class RedoLog
{
    internal sealed record Step(int Number, DateTime Time, string Tool, Dictionary<string, JsonElement> Arguments, string Result, string From);

    /// <summary>Set only by the tests.</summary>
    internal static string? FolderOverride;

    private static readonly object Gate = new();
    private const int Keep = 600;

    public static string Folder => FolderOverride ?? Path.Combine(AgentSettings.Folder, "redo");

    /// <summary>The open project's file name ("" for a project never saved).</summary>
    public static string ProjectOf(ToolContext context)
    {
        try { return (context.Link.LastStatus?["filename"]?.ToString() ?? "").Split('\\', '/').Last(); }
        catch (Exception) { return ""; }
    }

    private static string FileFor(string project)
    {
        var name = project.Trim().Length == 0 ? "unsaved project" : project.Trim();
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c));
        return Path.Combine(Folder, safe + ".jsonl");
    }

    public static void Write(string project, string tool, IReadOnlyDictionary<string, JsonElement> args, string result, string from = "")
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                time = DateTime.Now.ToString("s"),
                tool,
                arguments = args,
                result = result.Length > 300 ? result.Substring(0, 300) + "…" : result,
                from,
            });
            var file = FileFor(project);
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
                // Kept short: the oldest lines go once the file is well past the limit.
                var all = File.ReadAllLines(file);
                if (all.Length > Keep + 100) File.WriteAllLines(file, all.Skip(all.Length - Keep), new UTF8Encoding(false));
            }
        }
        catch (Exception) { /* keeping the step must never break the call */ }
    }

    /// <summary>The projects that have kept work, by file name.</summary>
    public static List<string> Projects()
    {
        try { return Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.jsonl").Select(f => Path.GetFileNameWithoutExtension(f)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>A project's kept steps, oldest first, numbered from 1 by their place in the file.</summary>
    public static List<Step> Read(string project)
    {
        var steps = new List<Step>();
        string[] lines;
        try
        {
            var file = FileFor(project);
            if (!File.Exists(file)) return steps;
            lock (Gate) lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return steps; }
        var n = 0;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) continue;
            n++;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var args = root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                    : new Dictionary<string, JsonElement>();
                steps.Add(new Step(n, DateTime.Parse(root.GetProperty("time").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    root.GetProperty("tool").GetString() ?? "", args,
                    root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "",
                    root.TryGetProperty("from", out var f) ? f.GetString() ?? "" : ""));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { /* a damaged line is left out; the numbers of the others stay */ }
        }
        return steps;
    }
}

/// <summary>redo_steps: lists the changes the chat made to a project and runs chosen ones again.</summary>
internal sealed class RedoStepsTool(Func<ToolRegistry> registry) : IPlaxisTool
{
    public const string ToolName = "redo_steps";
    public string Name => ToolName;
    public string Description =>
        "Do again exactly what the chat already did to a project (every change that worked is kept with its full arguments and code). " +
        "Use it when the user wants work redone: Plaxis closed or crashed before the project was saved, the work was lost, or the same " +
        "work is wanted on a fresh copy or another project. action=list (default) shows the kept steps, newest last, each with its " +
        "number, time, tool and what it did; since/until narrow it, and project reads another project's steps (its file name or words " +
        "of it). action=replay with steps (the numbers from list), or with from and to, runs them again in order in the project that " +
        "is open now. It can't tell which steps are still in the project: look first (list_model, list_phases) and leave out what is " +
        "already there. Names made again may differ; check each step's result.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["list", "replay"], "description": "Default list."},
      "project": {"type": "string", "description": "Whose steps: a project file name or words of it. Default the open project."},
      "since": {"type": "string", "description": "list: from this time, 'HH:mm' today or 'yyyy-MM-dd HH:mm'."},
      "until": {"type": "string", "description": "list: up to this time."},
      "steps": {"type": "array", "items": {"type": "integer"}, "description": "replay: the step numbers to run again, in order."},
      "from": {"type": "integer", "description": "replay: the first step number (with to)."},
      "to": {"type": "integer", "description": "replay: the last step number."},
      "stop_on_error": {"type": "boolean", "description": "replay: stop at the first step that fails (default true)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    /// <summary>Listing changes nothing, so it isn't asked about.</summary>
    internal static bool IsList(IReadOnlyDictionary<string, JsonElement> args) =>
        !args.TryGetValue("action", out var a) || a.ValueKind != JsonValueKind.String || a.GetString() != "replay";

    public async Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var open = RedoLog.ProjectOf(context);
        var project = open;
        if (args.OptString("project") is { Length: > 0 } wanted)
        {
            var known = RedoLog.Projects();
            var words = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var match = known.FirstOrDefault(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase))
                        ?? known.FirstOrDefault(k => words.All(w => k.Contains(w, StringComparison.OrdinalIgnoreCase)));
            if (match is null) return ToolResult.Error($"No kept steps for a project like '{wanted}'. Projects with kept steps: {(known.Count == 0 ? "none" : string.Join(", ", known))}.");
            project = match;
        }
        var all = RedoLog.Read(project);
        if (IsList(args.Raw))
        {
            var since = Time(args.OptString("since"));
            var until = Time(args.OptString("until"));
            var shown = all.Where(s => (since is null || s.Time >= since) && (until is null || s.Time <= until)).ToList();
            return ToolResult.Ok(new
            {
                project = project.Length == 0 ? "(unsaved project)" : project,
                open_project = open,
                count = shown.Count,
                steps = shown.TakeLast(200).Select(s => new
                {
                    step = s.Number,
                    time = s.Time.ToString("yyyy-MM-dd HH:mm"),
                    tool = s.Tool,
                    what = SelfTest.Short(Mcp.ChatMcp.Details(s.Tool, s.Arguments), 240),
                    result = SelfTest.Short(s.Result, 160),
                    from = s.From.Length > 0 ? s.From : null,
                }).ToList(),
                next = shown.Count == 0
                    ? "Nothing is kept for this project in that time. Other projects with kept steps: " + string.Join(", ", RedoLog.Projects())
                    : "Show the user the steps in a few lines and ask which to do again, then replay with their numbers. Look at the project first so nothing is made twice.",
            });
        }

        var numbers = args.Has("steps")
            ? args.Array("steps").Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToList()
            : args.OptLong("from") is { } from && args.OptLong("to") is { } to && to >= from ? Enumerable.Range((int)from, (int)(to - from) + 1).ToList() : [];
        if (numbers.Count == 0) return ToolResult.Error("Give steps (the numbers from action=list), or from and to.");
        var missing = numbers.Where(n => all.All(s => s.Number != n)).ToList();
        if (missing.Count > 0) return ToolResult.Error($"No kept step {string.Join(", ", missing)} for {project}. Call redo_steps with action=list.");
        var stop = args.OptBool("stop_on_error", true);
        var results = new List<object>();
        var failed = 0;
        foreach (var n in numbers)
        {
            ct.ThrowIfCancellationRequested();
            var step = all.First(s => s.Number == n);
            var tool = registry().Find(step.Tool);
            if (tool is null || step.Tool is ToolName or RunRecipeTool.ToolName)
            {
                results.Add(new { step = n, tool = step.Tool, ok = false, result = "This tool is no longer there." });
                failed++;
                if (stop) break;
                continue;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            ToolResult result;
            try { result = await tool.ExecuteAsync(context, new ToolArgs(step.Arguments), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { result = ToolResult.Error(ex.Message); }
            ActivityLog.Write(open, step.Tool, tool.ModifiesModel, step.Arguments, !result.IsError, result.Text, clock.ElapsedMilliseconds);
            results.Add(new { step = n, tool = step.Tool, ok = !result.IsError, result = SelfTest.Short(result.Text, 600) });
            if (!result.IsError) continue;
            failed++;
            if (stop) break;
        }
        var text = JsonSerializer.Serialize(new
        {
            project = open, ran = results.Count, failed, steps = results,
            next = failed > 0 ? "A step failed: read its result, look at the project, and tell the user what is done and what is not." : "Check the project (list_model, list_phases) and tell the user what was done again. Save the project if they want it kept.",
        });
        return new ToolResult(text, IsError: failed > 0 && results.Count == failed);
    }

    /// <summary>'HH:mm' today, or a full date and time; null when empty or not a time.</summary>
    internal static DateTime? Time(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (DateTime.TryParseExact(text.Trim(), ["HH:mm", "H:mm"], inv, System.Globalization.DateTimeStyles.None, out var today)) return DateTime.Today + today.TimeOfDay;
        return DateTime.TryParse(text.Trim(), inv, System.Globalization.DateTimeStyles.None, out var full) ? full : null;
    }
}
