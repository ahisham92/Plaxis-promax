using System.Text.Json;
using System.Text.Json.Nodes;
using PlaxisProMax.Plaxis;

namespace PlaxisProMax.Tools;

/// <summary>
/// A tool that is one worker op: the schema and approval flag here, the Plaxis work in plaxis_worker.py's op_&lt;name&gt;.
/// Most tools are this; the ones with logic of their own on this side (kept code, rules) are classes below.
/// </summary>
internal sealed class WorkerTool(string name, string description, string properties, string[] required, bool modifies, int timeoutSeconds = 0) : IPlaxisTool
{
    public string Name => name;
    public string Description => description;
    public string PropertiesJson => properties;
    public string[] Required => required;
    public bool ModifiesModel => modifies;

    public async Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var json = new JsonObject();
        foreach (var (key, value) in args.Raw) json[key] = JsonNode.Parse(value.GetRawText());
        var limit = timeoutSeconds > 0 ? timeoutSeconds : Math.Max(10, context.Settings().CallTimeoutSeconds);
        var answer = await context.Link.CallAsync(name, json, limit * 1000, ct).ConfigureAwait(false);
        return answer.Ok ? new ToolResult(answer.Result?.ToJsonString() ?? "{}") : ToolResult.Error(answer.Error);
    }
}

/// <summary>Runs Python in Plaxis: kept code by its id, or new code (held once when kept code nearly does it).</summary>
internal sealed class RunPlaxisCodeTool : IPlaxisTool
{
    public const string ToolName = "run_plaxis_code";
    public string Name => ToolName;

    public string Description =>
        "Runs Python in Plaxis Input with g_i and s_i (and output(phase) -> (s_o, g_o) for results). Use a ready-made tool " +
        "when one fits, then kept code (find_kept_code): pass kept_id (and inputs) to run kept code unchanged, which " +
        "doesn't ask the user again. New code always needs a one-line description and asks the user before it runs; if " +
        "it nearly repeats kept code it is held once with that code, so adapt it (based_on) or say why it is new " +
        "(new_because). Read inputs from the `inputs` dict, print what to report or set `result`. Code that works is kept.";

    public string PropertiesJson => """
        {
          "code": { "type": "string", "description": "New Python. Leave out to run kept code by kept_id." },
          "kept_id": { "type": "string", "description": "A kept code id (K12) to run unchanged." },
          "inputs": { "type": "object", "description": "Values the code reads from inputs[...] (phase names, levels, file paths)." },
          "description": { "type": "string", "description": "What the code does, in one line, by what it does in Plaxis (needed with code)." },
          "based_on": { "type": "string", "description": "The kept code id this code adapts: it replaces that entry instead of adding a near copy." },
          "new_because": { "type": "string", "description": "Why no kept code fits, when the call was held for a close one." }
        }
        """;

    public string[] Required => [];
    public bool ModifiesModel => true;

    /// <summary>
    /// New code that nearly repeats kept code, the first time in this chat: the answer that holds it, with the kept
    /// code. Checked before the approval card too, so the user is never asked about code that then doesn't run.
    /// </summary>
    internal static ToolResult? Held(ToolContext context, ToolArgs args)
    {
        if (args.OptString("code") is not { } code || string.IsNullOrWhiteSpace(code) || args.Has("based_on") || args.Has("new_because")) return null;
        if (context.Library.Closest(code) is not { } close || !context.Library.OfferOnce(context.Chat, close)) return null;
        return ToolResult.Ok(new
        {
            held = true,
            message = $"Not run yet: kept code {close.Id} already does nearly this. Run it with kept_id={close.Id} (and inputs), " +
                      $"adapt it and pass based_on={close.Id}, or pass new_because if it doesn't fit.",
            kept = new { close.Id, close.Description, close.Inputs, close.Uses, close.Code },
        });
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var code = args.OptString("code");
        KeptCode? kept = null;
        if (string.IsNullOrWhiteSpace(code))
        {
            var id = args.OptString("kept_id");
            if (id is null) return ToolResult.Error("Give code, or kept_id to run kept code.");
            kept = context.Library.Get(id);
            if (kept is null) return ToolResult.Error($"No kept code {id}. find_kept_code lists what there is.");
            code = kept.Code;
        }
        else
        {
            var description = args.OptString("description") ?? "";
            if (description.Trim().Length < 8) return ToolResult.Error("New code needs a one-line description of what it does in Plaxis, so it can be found again.");
            if (Held(context, args) is { } held) return held;
        }

        var call = new JsonObject { ["code"] = code };
        if (args.Has("inputs")) call["inputs"] = JsonNode.Parse(args.Raw["inputs"].GetRawText());
        var limit = Math.Max(10, context.Settings().CallTimeoutSeconds) * 1000;
        var answer = await context.Link.CallAsync("run_code", call, limit, ct).ConfigureAwait(false);
        if (!answer.Ok) return ToolResult.Error(answer.Error);
        var result = answer.Result as JsonObject ?? new JsonObject();
        var worked = result["ok"] is JsonValue v && v.TryGetValue<bool>(out var ok) && ok;
        if (!worked) return new ToolResult(result.ToJsonString(), IsError: true);

        var status = context.Link.LastStatus;
        if (kept is not null)
        {
            context.Library.Used(kept);
            result["kept_id"] = kept.Id;
        }
        else
        {
            var entry = context.Library.Keep(code!, args.OptString("description") ?? "", args.OptString("based_on"),
                status?["product"]?.ToString() ?? "", status?["version"]?.ToString() ?? "", allowed: true);
            result["kept_as"] = entry.Id;
            kept = entry;
        }
        if (kept.Uses >= 2) result["note"] = $"{kept.Id} has now run {kept.Uses} times: next time run it by kept_id instead of writing it again.";
        return new ToolResult(result.ToJsonString());
    }
}

/// <summary>Finds kept code by what it touches in Plaxis and what it does.</summary>
internal sealed class FindKeptCodeTool : IPlaxisTool
{
    public string Name => "find_kept_code";

    public string Description =>
        "Finds kept Python that already worked in Plaxis, before you write new code. Search by the Plaxis objects or " +
        "commands it would use (touches: borehole, soilmat, getresults, ResultTypes.Plate.M2D, cmd:mesh) and what it does. " +
        "With id, gives that entry's code. Run a match with run_plaxis_code kept_id.";

    public string PropertiesJson => """
        {
          "what": { "type": "string", "description": "What the code should do, in a few words." },
          "touches": { "type": "array", "items": { "type": "string" }, "description": "Plaxis commands, objects or result types it would use." },
          "id": { "type": "string", "description": "A kept code id, to read its code." }
        }
        """;

    public string[] Required => [];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        if (args.OptString("id") is { } id)
        {
            var e = context.Library.Get(id);
            return Task.FromResult(e is null ? ToolResult.Error($"No kept code {id}.")
                : ToolResult.Ok(new { e.Id, e.Description, e.Inputs, e.Uses, e.Product, e.Version, e.Code }));
        }
        var found = context.Library.Find(args.OptString("what") ?? "", args.StringList("touches"));
        return Task.FromResult(ToolResult.Ok(new
        {
            kept = context.Library.All.Count,
            matches = found.Select(f => new { f.Entry.Id, f.Entry.Description, f.Entry.Inputs, f.Entry.Uses, score = Math.Round(f.Score, 2) }),
            hint = found.Count == 0 ? "Nothing kept fits: write it with run_plaxis_code (it is kept once it works)." : "Read one with id, run it with run_plaxis_code kept_id.",
        }));
    }
}

/// <summary>Keeps one of the user's standing rules, so every later chat follows it (Sketch ProMax bug 115).</summary>
internal sealed class RememberRuleTool : IPlaxisTool
{
    public string Name => "remember_rule";

    public string Description =>
        "Keeps a standing rule the user gave (\"always use HS small for the clay\", \"mesh fine around the wall\", \"reports in " +
        "kN and m\"), so every later chat follows it. replaces: words of an older rule this one corrects.";

    public string PropertiesJson => """
        {
          "rule": { "type": "string", "description": "The rule, in the user's words." },
          "replaces": { "type": "string", "description": "Words of an earlier rule this one replaces." }
        }
        """;

    public string[] Required => ["rule"];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var settings = Settings.AgentSettings.Load();
        var lines = settings.ProjectRules.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
        var removed = 0;
        if (args.OptString("replaces") is { Length: > 0 } old)
            removed = lines.RemoveAll(l => l.Contains(old, StringComparison.OrdinalIgnoreCase));
        var rule = args.String("rule").Trim();
        lines.Add("- " + rule.TrimStart('-', ' '));
        settings.ProjectRules = string.Join("\n", lines);
        settings.Save();
        return Task.FromResult(ToolResult.Ok(new { kept = rule, replaced = removed, rules = lines.Count }));
    }
}

/// <summary>The tools offered to the chat. Add one by implementing <see cref="IPlaxisTool"/> or a worker op, and listing it here.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IPlaxisTool> _tools;

    public ToolRegistry(IEnumerable<IPlaxisTool> tools) => _tools = tools.ToDictionary(t => t.Name);

    public IReadOnlyCollection<IPlaxisTool> All => _tools.Values;

    public IPlaxisTool? Find(string name) => _tools.TryGetValue(name, out var tool) ? tool : null;

    public static ToolRegistry CreateDefault() => new(
    [
        // The crew (answered by the MCP server itself)
        new ChatJobsTool(), new MemoryNoteTool(), new RecallChatsTool(),
        new MakeDocumentTool(), new MakeDrawingTool(), new ReadDocumentTextTool(), new ReadDrawingFileTool(),
        // Look (read-only)
        new WorkerTool("status", "What is open in Plaxis: 2D or 3D, the version, the project, its units and phases. Call it first in a chat.", "{}", [], false, 30),
        new WorkerTool("list_model", "Every collection of the open model (Materials, Boreholes, Plates, Phases...) with its count and names.",
            """{"collections":{"type":"array","items":{"type":"string"},"description":"Only these collections."},"limit":{"type":"integer","description":"Names per collection (30)."}}""", [], false),
        new WorkerTool("inspect", "An object's properties with their values, its commands and its items, by path from g_i: Materials[0], Phases[1].PreviousPhase, " +
            "SoilContour; output:ResultTypes.Plate after get_results. Read the real names here instead of guessing them.",
            """{"path":{"type":"string","description":"Attribute and [index] steps, e.g. Phases[-1]. Empty: the global object."},"limit":{"type":"integer"}}""", [], false),
        new WorkerTool("list_phases", "The phases with their previous phase, calculation type, whether they will be calculated, and how they ended.", "{}", [], false),
        new WorkerTool("get_results", "Results of a calculated phase from Output: figures (count, min, max, mean, max_abs) for each result type, the first values, " +
            "and every value to a CSV file when csv_path is given. Result types are paths under ResultTypes: Soil.Utot, Soil.Ux, Plate.M2D, Plate.Nx2D, " +
            "NodeToNodeAnchor.AnchorForce2D. object: a structure's name for its own results.",
            """{"phase":{"type":"string","description":"Phase name; empty for the last."},"result_types":{"type":"array","items":{"type":"string"}},"location":{"type":"string","enum":["node","stresspoint"]},"object":{"type":"string"},"first":{"type":"integer"},"csv_path":{"type":"string"}}""",
            [], false, 600),
        new WorkerTool("results_to_excel", "Many results in one go, to one Excel file: every asked result type for every asked phase (default: all calculated " +
            "phases), a sheet per phase with a column per result, and a Summary sheet with count, min, max and max abs. Use it for reports and big models " +
            "instead of reading values one at a time. results: result type paths under ResultTypes, each with an optional structure name and location.",
            """{"path":{"type":"string","description":"The .xlsx file to write."},"phases":{"type":"array","items":{"type":"string"},"description":"Phase names; empty for every calculated phase."},"results":{"type":"array","items":{"type":"object","properties":{"type":{"type":"string","description":"e.g. Soil.Utot, Plate.M2D, NodeToNodeAnchor.AnchorForce2D"},"object":{"type":"string","description":"A structure's name for its own results."},"location":{"type":"string","enum":["node","stresspoint"]}},"required":["type"]}}}""",
            ["path", "results"], false, 3600),
        // Build (change the project; each is a Plaxis command, so Plaxis's own undo puts it back)
        new WorkerTool("run_commands", "Plaxis command-line commands in order, as typed in Plaxis's command line (e.g. \"point 0 0\", \"plate (0 0) (0 -10)\"). " +
            "Stops at the first that fails and says which. Prefer a dedicated tool when there is one.",
            """{"commands":{"type":"array","items":{"type":"string"}}}""", ["commands"], true),
        new WorkerTool("go_to_mode", "Switches Plaxis Input to a mode: soil, structures, mesh, flow or stages.",
            """{"mode":{"type":"string","enum":["soil","structures","mesh","flow","stages"]}}""", ["mode"], true, 60),
        new WorkerTool("soil_contour", "Sets the model's soil contour to a rectangle (in the project's length unit).",
            """{"xmin":{"type":"number"},"ymin":{"type":"number"},"xmax":{"type":"number"},"ymax":{"type":"number"}}""", ["xmin", "ymin", "xmax", "ymax"], true),
        new WorkerTool("create_soil_material", "A soil material with its properties by Plaxis's names (inspect an existing material to read them). " +
            "Says which properties Plaxis refused.",
            """{"name":{"type":"string"},"model":{"description":"SoilModel as Plaxis takes it (number or name)."},"properties":{"type":"object","description":"Property name to value, e.g. gammaUnsat, gammaSat, Eref, nu, cref, phi."},"replace_existing":{"type":"boolean"}}""",
            ["name"], true),
        new WorkerTool("create_boreholes", "Boreholes with soil layers from a borehole log: each with x (and y in 3D), top level, water head, and layers top-down " +
            "by bottom level and material name. The first borehole makes the layers when there are none.",
            """{"boreholes":{"type":"array","items":{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"top":{"type":"number"},"head":{"type":"number"},"layers":{"type":"array","items":{"type":"object","properties":{"bottom":{"type":"number"},"material":{"type":"string"}},"required":["bottom"]}}},"required":["x"]}}}""",
            ["boreholes"], true),
        new WorkerTool("generate_mesh", "Generates the mesh, with an optional coarseness factor (smaller is finer).",
            """{"coarseness":{"type":"number"}}""", [], true, 900),
        new WorkerTool("add_phase", "A new phase after another (by name; empty for the last), with its name and settings by Plaxis's names (DeformCalcType, ...).",
            """{"after":{"type":"string"},"name":{"type":"string"},"settings":{"type":"object"}}""", [], true),
        new WorkerTool("calculate", "Calculates the named phases (or every phase set to calculate) and reports how each phase ended. Can take long. " +
            "For a big model or a long run: one_by_one calculates one phase at a time, saves the project after each and stops at the first that fails " +
            "(the rest stay marked, so the next call carries on); stored_steps 1 keeps only each phase's last step, so the files stay small.",
            """{"phases":{"type":"array","items":{"type":"string"}},"one_by_one":{"type":"boolean","description":"One phase at a time, saving after each, stopping at the first failure."},"stored_steps":{"type":"integer","description":"Steps kept per phase (MaxStepsStored); 1 for big models."},"save":{"type":"boolean","description":"Save after calculating (one_by_one saves after each phase unless false)."}}""",
            [], true, 24 * 3600),
        new WorkerTool("undo", "Undoes the last Plaxis steps (Plaxis's own undo).", """{"steps":{"type":"integer"}}""", [], true, 60),
        // Projects
        new WorkerTool("new_project", "Starts a new, empty Plaxis project in Input.", "{}", [], true, 120),
        new WorkerTool("open_project", "Opens a Plaxis project file (.p2dx / .p3d).", """{"path":{"type":"string"}}""", ["path"], true, 600),
        new WorkerTool("save_project", "Saves the project, under path when given.", """{"path":{"type":"string"}}""", [], true, 600),
        // Reuse and rules
        new FindKeptCodeTool(),
        new RunPlaxisCodeTool(),
        new RememberRuleTool(),
        // Help (as in Sketch ProMax): bug reports, the activity log, and the chats-and-bugs export
        new Bugs.ReportBugTool(),
        new Bugs.GetActivityLogTool(),
        new Bugs.ExportChatsAndBugsTool(),
    ]);

    /// <summary>The input schema of a tool, as MCP lists it.</summary>
    internal static JsonElement Schema(IPlaxisTool tool)
    {
        var required = JsonSerializer.Serialize(tool.Required);
        using var doc = JsonDocument.Parse($$"""{"type":"object","properties":{{tool.PropertiesJson}},"required":{{required}}}""");
        return doc.RootElement.Clone();
    }
}
