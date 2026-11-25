using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlaxisProMax.Modeller;

namespace PlaxisProMax.Tools;

/// <summary>
/// The chat's tools for the Modeller and Viewer: open them, read and change the Modeller's model (no Plaxis, no licence),
/// check and save it, Calculate in Plaxis (the one step that uses Plaxis and its licence), and read the results file.
/// </summary>
internal abstract class ModellerTool(string name, string description, string properties, string[] required, bool modifies) : IPlaxisTool
{
    public string Name => name;
    public string Description => description;
    public string PropertiesJson => properties;
    public string[] Required => required;
    public bool ModifiesModel => modifies;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        try { return Task.FromResult(Run(args)); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or JsonException or KeyNotFoundException)
        {
            return Task.FromResult(ToolResult.Error(ex.Message));
        }
    }

    protected abstract ToolResult Run(ToolArgs args);

    protected static StudioSession Session => Studio.Server.Session;

    protected static JsonNode? Node(ToolArgs args, string key) => args.Has(key) ? JsonNode.Parse(args.Raw[key].GetRawText()) : null;

    protected static JsonObject Brief(StudioSession.Snapshot s) => new()
    {
        ["rev"] = s.Rev, ["path"] = s.Path, ["unsaved"] = s.Dirty,
        ["summary"] = s.Model is null ? "No model is open." : ModelFile.Summary(s.Model),
    };
}

internal sealed class OpenModellerTool() : ModellerTool("open_modeller",
    "Opens the Modeller: Plaxis ProMax's own window for building a Plaxis model (soil, structures, mesh, water, staged construction) with no Plaxis " +
    "open and no licence used. Optionally opens a model file (.ppm), a new blank model (product 2D or 3D), or a sample by name.",
    """{"path":{"type":"string","description":"A model file (.ppm) to open"},"new":{"type":"string","enum":["2D","3D"]},"title":{"type":"string"},"sample":{"type":"string"},"show":{"type":"boolean","description":"Open the window (default true)"}}""",
    [], false)
{
    protected override ToolResult Run(ToolArgs a)
    {
        var s = Session;
        if (a.OptString("path") is { } path) s.Open(path);
        else if (a.OptString("sample") is { } sample) s.New("2D", sample: sample);
        else if (a.OptString("new") is { } product) s.New(product, a.OptString("title"));
        if (a.OptBool("show", true)) Studio.OpenModeller();
        var brief = Brief(s.Get());
        brief["samples"] = new JsonArray(ModelFile.Samples().Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray());
        return new ToolResult(brief.ToJsonString());
    }
}

internal sealed class ModellerModelTool() : ModellerTool("modeller_model",
    "The model open in the Modeller as JSON (the .ppm format: product, contour, materials, boreholes with layers, structures, waterlevels, mesh, " +
    "phases with their active objects and loads), or one part of it by path such as \"materials\" or \"phases/id=p2\".",
    """{"path":{"type":"string"}}""", [], false)
{
    protected override ToolResult Run(ToolArgs a)
    {
        var snap = Session.Get();
        if (snap.Model is null) return ToolResult.Error("No model is open in the Modeller. Use open_modeller first.");
        var brief = Brief(snap);
        brief["model"] = a.OptString("path") is { Length: > 0 } p ? ModelPath.Get(snap.Model, p)?.DeepClone() : snap.Model;
        return new ToolResult(brief.ToJsonString());
    }
}

internal sealed class ModellerEditTool() : ModellerTool("modeller_edit",
    "Changes the Modeller's model (shown in its window at once; no Plaxis or licence). Either a whole model (model), or a list of changes, each " +
    "{op: set|add|remove, path, value}. Paths go through objects by key and lists by index or id: \"materials/id=clay/E\", \"boreholes/0/layers/1/top\", " +
    "\"phases/id=p2/active/anc1\". add appends value to the list at path; remove deletes the item at path. All changes are one step the user can undo.",
    """{"model":{"type":"object"},"changes":{"type":"array","items":{"type":"object","properties":{"op":{"type":"string","enum":["set","add","remove"]},"path":{"type":"string"},"value":{}},"required":["op","path"]}}}""",
    [], false)
{
    protected override ToolResult Run(ToolArgs a)
    {
        var s = Session;
        if (Node(a, "model") is JsonObject whole) s.Put(whole);
        if (Node(a, "changes") is JsonArray changes)
            s.Change(m => { foreach (var c in changes.OfType<JsonObject>()) ModelPath.Apply(m, c["op"]?.ToString() ?? "set", c["path"]?.ToString() ?? "", c["value"]?.DeepClone()); });
        var snap = s.Get();
        var brief = Brief(snap);
        if (snap.Model is not null) brief["findings"] = StudioServer.Findings(snap.Model);
        return new ToolResult(brief.ToJsonString());
    }
}

internal sealed class ModellerCheckTool() : ModellerTool("modeller_check",
    "Checks the Modeller's model before it is calculated: materials, layers, structures, water levels and phases. Errors stop a calculation.",
    "{}", [], false)
{
    protected override ToolResult Run(ToolArgs a)
    {
        var model = Session.Get().Model ?? throw new InvalidOperationException("No model is open in the Modeller.");
        return new ToolResult(new JsonObject { ["findings"] = StudioServer.Findings(model) }.ToJsonString());
    }
}

internal sealed class ModellerSaveTool() : ModellerTool("modeller_save",
    "Saves the Modeller's model (.ppm): to its own file, or under a name and folder. A new model goes to Documents\\Plaxis ProMax\\Models. " +
    "With script true, also writes the Plaxis script that builds it in Plaxis Input (Expert > Python > Run script) beside it.",
    """{"name":{"type":"string"},"folder":{"type":"string"},"script":{"type":"boolean"}}""", [], false)
{
    protected override ToolResult Run(ToolArgs a)
    {
        var s = Session;
        var path = s.Save(a.OptString("name"), a.OptString("folder"));
        var answer = new JsonObject { ["saved"] = path };
        if (a.OptBool("script"))
        {
            var script = Path.ChangeExtension(path, null) + " (Plaxis).py";
            File.WriteAllText(script, StudioFiles.Script(s.Get().Model!));
            answer["script"] = script;
        }
        return new ToolResult(answer.ToJsonString());
    }
}

internal sealed class CalculateInPlaxisTool() : ModellerTool("calculate_in_plaxis",
    "Calculate in Plaxis: the one step that uses Plaxis and its licence. Saves the Modeller's model, starts PLAXIS Input, builds the model in a new " +
    "project, meshes and calculates every phase, copies the results into a results file (.ppr beside the model) and closes Plaxis, so the licence is " +
    "given back. action start (default) begins it; status tells how it is going; stop ends it and closes Plaxis. It takes minutes to hours: start it, " +
    "then ask for status now and then.",
    """{"action":{"type":"string","enum":["start","status","stop"]},"stored_steps":{"type":"integer"},"one_by_one":{"type":"boolean"},"keep_open":{"type":"boolean"},"plaxis":{"type":"string","description":"Plaxis Input program to use (default: the newest found)"},"keep_phases":{"type":"array","items":{"type":"string"},"description":"Phases (names or ids) to keep in the results file; default all"},"keep_results":{"type":"array","items":{"type":"string","enum":["displacements","stresses","pore_pressures","plastic_points","structures"]},"description":"Kinds of results to keep; default all. Leaving some out keeps a big model's results file small"}}""",
    [], true)
{
    protected override ToolResult Run(ToolArgs a)
    {
        var s = Session;
        switch (a.OptString("action") ?? "start")
        {
            case "status":
                return new ToolResult((s.Run?.Status() ?? new JsonObject { ["state"] = "none" }).ToJsonString());
            case "stop":
                s.Run?.Stop();
                return new ToolResult((s.Run?.Status() ?? new JsonObject { ["state"] = "none" }).ToJsonString());
            default:
                var errors = ModelChecks.Check(s.Get().Model ?? throw new InvalidOperationException("No model is open in the Modeller.")).Where(f => f.Level == "error").ToList();
                if (errors.Count > 0) return ToolResult.Error("The model has errors to fix first: " + string.Join("; ", errors.Select(e => e.Text)));
                var run = s.StartRun(new PlaxisRun.Options(a.OptString("plaxis"), (int)Math.Clamp(a.OptLong("stored_steps") ?? 1, 1, 1000), a.OptBool("one_by_one", true), a.OptBool("keep_open"),
                    StudioServer.Keep(new JsonObject
                    {
                        ["phases"] = a.Has("keep_phases") ? new JsonArray(a.StringList("keep_phases").Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) : null,
                        ["results"] = a.Has("keep_results") ? new JsonArray(a.StringList("keep_results").Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) : null,
                    })));
                return new ToolResult(run.Status().ToJsonString());
        }
    }
}

internal sealed class OpenResultsViewerTool() : ModellerTool("open_results_viewer",
    "Opens the results Viewer (Plaxis Output's views: deformed mesh, displacements, stresses, pore pressures, plastic points, structure forces, cross " +
    "sections, curves, tables) on a results file (.ppr), by default the Modeller model's. No Plaxis or licence is used.",
    """{"path":{"type":"string"}}""", [], false)
{
    protected override ToolResult Run(ToolArgs a)
    {
        var path = a.OptString("path") ?? (Session.Get().Path is { } mp ? ModelFile.ResultsPath(mp) : null)
            ?? throw new InvalidOperationException("Name the results file (.ppr), or open its model in the Modeller.");
        if (!File.Exists(path)) throw new InvalidOperationException("There is no results file at " + path + ". Calculate the model in Plaxis first.");
        StudioFiles.OpenWindow(Studio.Server.ViewerUrl(path));
        return new ToolResult(new JsonObject { ["opened"] = path }.ToJsonString());
    }
}

internal sealed class ReadResultsTool() : ModellerTool("read_results",
    "Reads a results file (.ppr) with no Plaxis: for each phase how it ended and the largest displacements, and the largest structure forces " +
    "(plates M, Q, N; anchors), for a report. By default the Modeller model's results.",
    """{"path":{"type":"string"}}""", [], false)
{
    protected override ToolResult Run(ToolArgs a)
    {
        var path = a.OptString("path") ?? (Session.Get().Path is { } mp ? ModelFile.ResultsPath(mp) : null)
            ?? throw new InvalidOperationException("Name the results file (.ppr).");
        return new ToolResult(ResultsSummary.Of(StudioFiles.ReadResults(path)).ToJsonString());
    }
}

/// <summary>The figures of a results file, phase by phase.</summary>
internal static class ResultsSummary
{
    public static JsonObject Of(JsonObject r)
    {
        var phases = new JsonArray();
        foreach (var p in (r["phases"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var o = new JsonObject { ["name"] = p["name"]?.DeepClone(), ["calc"] = p["calc"]?.DeepClone(), ["ended"] = p["ended"]?.DeepClone() };
            if (p["SumMsf"] is { } msf) o["SumMsf"] = msf.DeepClone();
            if (p["node"] is JsonObject nodes)
                foreach (var key in new[] { "Utot", "Ux", "Uy", "Uz" })
                    if (MaxAbs(nodes[key]) is { } m) o["max_" + key] = m;
            foreach (var list in new[] { "plates", "embedded" })
            {
                if (p[list] is not JsonArray items || items.Count == 0) continue;
                var outList = new JsonArray();
                foreach (var pl in items.OfType<JsonObject>())
                {
                    var po = new JsonObject { ["name"] = pl["name"]?.DeepClone(), ["struct"] = pl["struct"]?.DeepClone() };
                    foreach (var key in new[] { "M", "Q", "N", "M11", "M22", "N1", "N2", "Ux", "Utot" })
                        if (MaxAbs(pl[key]) is { } m) po["max_" + key] = m;
                    outList.Add(po);
                }
                o[list] = outList;
            }
            if (p["anchors"] is JsonArray anchors && anchors.Count > 0)
                o["anchors"] = new JsonArray(anchors.OfType<JsonObject>().Select(x => (JsonNode)new JsonObject { ["struct"] = x["struct"]?.DeepClone(), ["kind"] = x["kind"]?.DeepClone(), ["F"] = x["F"]?.DeepClone() }).ToArray());
            phases.Add(o);
        }
        return new JsonObject
        {
            ["title"] = r["title"]?.DeepClone(), ["product"] = r["product"]?.DeepClone(), ["plaxis"] = r["plaxis"]?.DeepClone(),
            ["sample"] = r["sample"]?.DeepClone(), ["units"] = r["units"]?.DeepClone(), ["phases"] = phases, ["missing"] = r["missing"]?.DeepClone(),
        };
    }

    private static double? MaxAbs(JsonNode? values)
    {
        if (values is not JsonArray a || a.Count == 0) return null;
        double best = 0;
        foreach (var v in a)
            if (v is JsonValue jv && jv.TryGetValue<double>(out var d) && Math.Abs(d) > Math.Abs(best)) best = d;
        return Math.Round(best, 6);
    }
}

/// <summary>Paths into a model: keys of objects, and list items by index or by id ("materials/id=clay/E").</summary>
internal static class ModelPath
{
    public static JsonNode? Get(JsonNode root, string path)
    {
        JsonNode? at = root;
        foreach (var part in Parts(path)) at = Step(at, part, path);
        return at;
    }

    public static void Apply(JsonObject root, string op, string path, JsonNode? value)
    {
        var parts = Parts(path).ToList();
        if (parts.Count == 0) throw new ArgumentException("A change needs a path.");
        if (op == "add")
        {
            var list = Get(root, path) as JsonArray ?? throw new ArgumentException(path + " is not a list.");
            list.Add(value);
            return;
        }
        JsonNode parent = root;
        foreach (var part in parts.Take(parts.Count - 1))
        {
            var next = Step(parent, part, path, create: op == "set");
            parent = next ?? throw new ArgumentException("Nothing at " + path + ".");
        }
        var last = parts[^1];
        switch (parent)
        {
            case JsonObject o when op == "remove":
                if (!o.Remove(last)) throw new ArgumentException("Nothing at " + path + ".");
                break;
            case JsonObject o:
                o[last] = value;
                break;
            case JsonArray a:
                var i = Index(a, last, path);
                if (op == "remove") a.RemoveAt(i);
                else a[i] = value;
                break;
            default:
                throw new ArgumentException("Nothing at " + path + ".");
        }
    }

    private static IEnumerable<string> Parts(string path) => path.Split(new[] { '/', '.' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .SelectMany(p => p.Contains('[') ? p.Replace("]", "").Split('[', StringSplitOptions.RemoveEmptyEntries) : [p]);

    private static JsonNode? Step(JsonNode? at, string part, string path, bool create = false) => at switch
    {
        JsonObject o when o[part] is { } child => child,
        JsonObject o when create => o[part] = new JsonObject(),
        JsonArray a => a[Index(a, part, path)],
        _ => throw new ArgumentException("Nothing at " + path + " (no '" + part + "')."),
    };

    private static int Index(JsonArray a, string part, string path)
    {
        if (int.TryParse(part, out var i))
        {
            if (i < 0) i += a.Count;
            if (i < 0 || i >= a.Count) throw new ArgumentException("No item " + part + " in " + path + ": the list has " + a.Count + ".");
            return i;
        }
        var id = part.StartsWith("id=", StringComparison.Ordinal) ? part[3..] : part;
        for (var k = 0; k < a.Count; k++)
            if (a[k] is JsonObject o && o["id"]?.ToString() == id) return k;
        throw new ArgumentException("No item with id '" + id + "' in " + path + ".");
    }
}
