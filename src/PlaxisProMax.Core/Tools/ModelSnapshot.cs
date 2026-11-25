using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlaxisProMax.Settings;

namespace PlaxisProMax.Tools;

/// <summary>
/// Track changes (Sketch ProMax's model_snapshot, for Plaxis): a snapshot keeps every object of the project with its
/// properties (materials and their parameters, boreholes and layers, structures, loads, water levels, phases and their
/// settings); comparing two snapshots, or a snapshot with the project as it is now, lists what was added, deleted and
/// changed, property by property. Kept per project in %APPDATA%\PlaxisProMax\snapshots.
/// </summary>
internal static class ModelSnapshots
{
    /// <summary>Set only by the tests.</summary>
    internal static string? FolderOverride;

    public static string Root => FolderOverride ?? Path.Combine(AgentSettings.Folder, "snapshots");

    internal sealed record Change(string Kind, string Collection, string Name, string Property, string Before, string After);

    internal sealed record Saved(string File, string Name, DateTime Taken, int Objects);

    public static string FolderFor(string project)
    {
        var name = project.Trim().Length == 0 ? "unsaved project" : project.Trim();
        return Path.Combine(Root, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c)));
    }

    /// <summary>Saves what the worker read as a snapshot; the file is named by its time and label.</summary>
    public static Saved Save(string project, JsonObject collections, string label, DateTime now, JsonNode? status = null)
    {
        var folder = FolderFor(project);
        Directory.CreateDirectory(folder);
        var clean = string.Concat(label.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c));
        if (clean.Length > 60) clean = clean.Substring(0, 60);
        var name = now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + (clean.Length > 0 ? " " + clean : "");
        var file = Path.Combine(folder, name + ".json");
        var doc = new JsonObject
        {
            ["project"] = project, ["label"] = label.Trim(), ["taken"] = now.ToString("s", CultureInfo.InvariantCulture),
            ["status"] = status?.DeepClone(), ["collections"] = collections.DeepClone(),
        };
        File.WriteAllText(file, doc.ToJsonString(), new UTF8Encoding(false));
        return new Saved(file, name, now, Count(collections));
    }

    public static int Count(JsonObject collections) => collections.Sum(c => (c.Value as JsonObject)?.Count ?? 0);

    /// <summary>A project's snapshots, newest first.</summary>
    public static List<Saved> List(string project)
    {
        var folder = FolderFor(project);
        var found = new List<Saved>();
        if (!Directory.Exists(folder)) return found;
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                var taken = DateTime.Parse(doc["taken"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                found.Add(new Saved(file, Path.GetFileNameWithoutExtension(file), taken, Count(doc["collections"]!.AsObject())));
            }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException or NullReferenceException or InvalidOperationException) { /* a damaged file is left out */ }
        }
        return found.OrderByDescending(s => s.Taken).ToList();
    }

    /// <summary>
    /// The snapshot the words name: "latest" (or empty), "previous", "first", its number in the list (1 is the newest), a
    /// date ("2026-11-20": the last one on or before that day), or words of its name or label.
    /// </summary>
    public static Saved? Find(IReadOnlyList<Saved> list, string? which)
    {
        if (list.Count == 0) return null;
        var w = (which ?? "").Trim();
        if (w.Length == 0 || w.Equals("latest", StringComparison.OrdinalIgnoreCase) || w.Equals("last", StringComparison.OrdinalIgnoreCase)) return list[0];
        if (w.Equals("previous", StringComparison.OrdinalIgnoreCase)) return list.Count > 1 ? list[1] : null;
        if (w.Equals("first", StringComparison.OrdinalIgnoreCase) || w.Equals("oldest", StringComparison.OrdinalIgnoreCase)) return list[^1];
        if (int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n >= 1 && n <= list.Count ? list[n - 1] : null;
        if (DateTime.TryParseExact(w, ["yyyy-MM-dd", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var until = w.Length <= 10 ? day.Date.AddDays(1) : day.AddMinutes(1);
            return list.FirstOrDefault(s => s.Taken < until);
        }
        var words = w.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return list.FirstOrDefault(s => s.Name.Equals(w, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault(s => words.All(x => s.Name.Contains(x, StringComparison.OrdinalIgnoreCase)));
    }

    public static JsonObject Load(string file) => JsonNode.Parse(File.ReadAllText(file))!["collections"]!.AsObject();

    // Properties that change by themselves (a calculation ran, a file was saved) and say nothing about the design.
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "CalculationResult", "ShouldCalculate", "LogInfo", "Reached", "ReachedTime", "ReachedMstage", "ReachedSumMsf", "ReachedSumMstage", "ElapsedTime", "CalculationTime",
    };

    /// <summary>What differs between two snapshots: objects added and deleted, and each property that changed.</summary>
    public static List<Change> Compare(JsonObject before, JsonObject after, bool results = false)
    {
        var changes = new List<Change>();
        foreach (var collection in before.Select(c => c.Key).Union(after.Select(c => c.Key)).OrderBy(k => k, StringComparer.Ordinal))
        {
            var a = before[collection] as JsonObject ?? [];
            var b = after[collection] as JsonObject ?? [];
            foreach (var (name, _) in b.Where(o => !a.ContainsKey(o.Key))) changes.Add(new Change("added", collection, name, "", "", ""));
            foreach (var (name, _) in a.Where(o => !b.ContainsKey(o.Key))) changes.Add(new Change("deleted", collection, name, "", "", ""));
            foreach (var (name, was) in a)
            {
                if (b[name] is not JsonObject now || was is not JsonObject old) continue;
                foreach (var property in old.Select(p => p.Key).Union(now.Select(p => p.Key)).OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!results && Noise.Contains(property)) continue;
                    var x = Text(old[property]);
                    var y = Text(now[property]);
                    if (!Same(x, y)) changes.Add(new Change("changed", collection, name, property, x, y));
                }
            }
        }
        return changes;
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    /// <summary>Equal as text, or as numbers within a part in a million (12000 and 12000.0, 1E4 and 10000).</summary>
    internal static bool Same(string x, string y)
    {
        if (string.Equals(x, y, StringComparison.Ordinal)) return true;
        if (!double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) || !double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return false;
        return Math.Abs(a - b) <= 1e-6 * Math.Max(Math.Abs(a), Math.Abs(b));
    }

    public static string Csv(IEnumerable<Change> changes)
    {
        static string Q(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder("Change,Collection,Object,Property,Before,After\r\n");
        foreach (var c in changes) sb.Append(string.Join(",", new[] { c.Kind, c.Collection, c.Name, c.Property, c.Before, c.After }.Select(Q))).Append("\r\n");
        return sb.ToString();
    }
}

/// <summary>model_snapshot: take, list and compare snapshots of the open project.</summary>
internal sealed class ModelSnapshotTool : IPlaxisTool
{
    public const string ToolName = "model_snapshot";
    public string Name => ToolName;
    public string Description =>
        "Track changes in the project. action=take saves a snapshot of the open project: every material with its parameters, boreholes " +
        "and soil layers, structures, loads, water levels, and the phases with their settings (label it, e.g. 'before the redesign'). " +
        "action=compare lists what was added, deleted and changed, property by property, between two snapshots, or between a snapshot " +
        "and the project as it is now (the default: 'from' the latest snapshot, 'to' now); the full list goes to a CSV file. " +
        "action=list shows the saved snapshots, newest first. A snapshot is named by 'latest', 'previous', 'first', its number in the " +
        "list, a date (2026-11-20), or words of its label. Take one before and after a design change, or at each issue of the model. " +
        "On a big model a snapshot can take a few minutes. Not a change to the project.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["take", "compare", "list"]},
      "label": {"type": "string", "description": "take: a few words that say what this state is."},
      "from": {"type": "string", "description": "compare: the earlier snapshot (default latest)."},
      "to": {"type": "string", "description": "compare: the later snapshot, or 'now' for the open project (default now)."},
      "collections": {"type": "array", "items": {"type": "string"}, "description": "Only these collections (Materials, Phases, Plates...). Default all."},
      "include_results": {"type": "boolean", "description": "compare: also list what a calculation changes by itself (how each phase ended). Default false."}
    }
    """;
    public string[] Required => ["action"];
    public bool ModifiesModel => false;

    public async Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var action = args.String("action").Trim().ToLowerInvariant();
        var project = RedoLog.ProjectOf(context);
        var list = ModelSnapshots.List(project);
        if (action == "list")
            return ToolResult.Ok(new
            {
                project = project.Length == 0 ? "(unsaved project)" : project,
                snapshots = list.Select((s, i) => new { n = i + 1, name = s.Name, taken = s.Taken.ToString("yyyy-MM-dd HH:mm"), objects = s.Objects }).ToList(),
                folder = ModelSnapshots.FolderFor(project),
                next = list.Count == 0 ? "No snapshot of this project yet: action=take saves one." : null,
            });

        async Task<(JsonObject? Collections, JsonNode? Cut, string? Error)> ReadNow()
        {
            var request = new JsonObject();
            if (args.Has("collections")) request["collections"] = new JsonArray(args.StringList("collections").Select(c => (JsonNode)c).ToArray());
            var answer = await context.Link.CallAsync("snapshot", request, 30 * 60 * 1000, ct).ConfigureAwait(false);
            if (!answer.Ok) return (null, null, answer.Error);
            return (answer.Result?["collections"] as JsonObject ?? [], answer.Result?["cut"], null);
        }

        if (action == "take")
        {
            var (collections, cut, error) = await ReadNow().ConfigureAwait(false);
            if (collections is null) return ToolResult.Error(error ?? "The project couldn't be read.");
            var saved = ModelSnapshots.Save(project, collections, args.OptString("label") ?? "", DateTime.Now, context.Link.LastStatus);
            return ToolResult.Ok(new
            {
                saved = saved.Name, objects = saved.Objects,
                collections = collections.ToDictionary(c => c.Key, c => (c.Value as JsonObject)?.Count ?? 0),
                cut,
                earlier = list.Count,
                next = "Say in a line that the snapshot is kept, and that it can be compared with the project later (model_snapshot compare).",
            });
        }
        if (action != "compare") return ToolResult.Error("action is take, compare or list.");

        if (list.Count == 0) return ToolResult.Error("There is no snapshot of this project to compare with. Take one first (action=take); changes are seen from then on.");
        var from = ModelSnapshots.Find(list, args.OptString("from"));
        if (from is null) return ToolResult.Error($"No snapshot like '{args.OptString("from")}'. Call model_snapshot with action=list.");
        var toWords = (args.OptString("to") ?? "now").Trim();
        JsonObject before, after;
        string toName;
        try
        {
            before = ModelSnapshots.Load(from.File);
            if (toWords.Equals("now", StringComparison.OrdinalIgnoreCase) || toWords.Length == 0)
            {
                var (collections, _, error) = await ReadNow().ConfigureAwait(false);
                if (collections is null) return ToolResult.Error(error ?? "The project couldn't be read.");
                after = collections;
                toName = "the project now";
            }
            else
            {
                var to = ModelSnapshots.Find(list, toWords);
                if (to is null) return ToolResult.Error($"No snapshot like '{toWords}'. Call model_snapshot with action=list.");
                after = ModelSnapshots.Load(to.File);
                toName = to.Name;
                if (to.Taken < from.Taken) (before, after, toName, from) = (after, before, from.Name, to); // always older to newer
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or NullReferenceException or InvalidOperationException) { return ToolResult.Error("A snapshot couldn't be read: " + ex.Message); }
        if (args.Has("collections"))
        {
            var only = args.StringList("collections").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in before.Select(c => c.Key).Where(k => !only.Contains(k)).ToList()) before.Remove(key);
            foreach (var key in after.Select(c => c.Key).Where(k => !only.Contains(k)).ToList()) after.Remove(key);
        }
        var changes = ModelSnapshots.Compare(before, after, args.OptBool("include_results"));
        string? csv = null;
        if (changes.Count > 0)
        {
            try
            {
                var folder = Path.Combine(Mcp.ChatMcp.OutputFolder, "Snapshots");
                Directory.CreateDirectory(folder);
                csv = OfficeDocs.FreePath(folder, OfficeDocs.SafeName($"Changes {(project.Length > 0 ? Path.GetFileNameWithoutExtension(project) : "project")} {DateTime.Now:yyyy-MM-dd HHmm}", "Changes"), ".csv");
                File.WriteAllText(csv, ModelSnapshots.Csv(changes), new UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { csv = null; }
        }
        return ToolResult.Ok(new
        {
            from = from.Name, to = toName,
            added = changes.Count(c => c.Kind == "added"), deleted = changes.Count(c => c.Kind == "deleted"), changed = changes.Count(c => c.Kind == "changed"),
            by_collection = changes.GroupBy(c => c.Collection).ToDictionary(g => g.Key, g => g.Count()),
            changes = changes.Take(150).Select(c => c.Kind == "changed"
                ? (object)new { change = c.Kind, collection = c.Collection, name = c.Name, property = c.Property, before = SelfTest.Short(c.Before, 120), after = SelfTest.Short(c.After, 120) }
                : new { change = c.Kind, collection = c.Collection, name = c.Name }).ToList(),
            more = changes.Count > 150 ? $"{changes.Count - 150} more are in the file." : null,
            file = csv,
            next = changes.Count == 0 ? "Nothing differs. Say so in one line."
                : "Tell the user what changed in a short table, grouped by what it is (materials, structures, phases), the changes that matter for the design first. The file has every change; the chat shows it as a card.",
        });
    }
}
