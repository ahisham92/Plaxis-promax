using System.Text.Json;
using PlaxisProMax.Settings;

namespace PlaxisProMax.Tools;

/// <summary>
/// Recipes (from Sketch ProMax): a saved list of tool calls that worked, replayed later step by step without writing
/// them again. Stored as JSON in %APPDATA%\PlaxisProMax\recipes, one file each, so a recipe can be copied to another PC.
/// Values written as {placeholder} in the saved arguments are filled in when the recipe runs.
/// </summary>
internal static class Recipes
{
    /// <summary>Set only by the tests.</summary>
    internal static string? FolderOverride;

    public static string Folder => FolderOverride ?? Path.Combine(AgentSettings.Folder, "recipes");

    public static string PathFor(string name)
    {
        var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c));
        return Path.Combine(Folder, safe + ".json");
    }

    /// <summary>The recipe's steps as plain lines, for the approval card (the code or commands of each step in full).</summary>
    public static string Describe(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var lines = new List<string>();
            var i = 0;
            foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray())
            {
                i++;
                var arguments = step.TryGetProperty("arguments", out var a) ? a.GetRawText() : "{}";
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String) arguments = "\n" + code.GetString();
                lines.Add($"{i}. {step.GetProperty("tool").GetString()}: {arguments}");
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException) { return ""; }
    }
}
internal static class Placeholders
{
    private static readonly System.Text.RegularExpressions.Regex Whole = new(@"^\{([A-Za-z_][A-Za-z0-9_ ]*)\}$");
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    private static readonly Dictionary<string, JsonElement> NoValues = new();

    public static string Text(string s, IReadOnlyDictionary<string, JsonElement> values)
    {
        foreach (var kv in values)
            s = s.Replace("{" + kv.Key + "}", kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.GetRawText());
        return s;
    }

    public static Dictionary<string, JsonElement> Fill(Dictionary<string, JsonElement> args, string propertiesJson, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (values.Count == 0) return args;
        JsonElement? properties = null;
        properties = TryParse(propertiesJson);
        var result = new Dictionary<string, JsonElement>();
        foreach (var kv in args)
            result[kv.Key] = Element(w => Write(w, kv.Value, Property(properties, kv.Key), values));
        return result;
    }

    internal static JsonElement Element(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) write(w);
        // Parsed from a string, as Sketch ProMax does.
        using var parsed = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        return parsed.RootElement.Clone();
    }

    private static JsonElement? Property(JsonElement? objectProperties, string key) =>
        objectProperties is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(key, out var s) ? s : null;

    private static string? TypeOf(JsonElement? schema)
    {
        if (schema is not { ValueKind: JsonValueKind.Object } s || !s.TryGetProperty("type", out var t)) return null;
        if (t.ValueKind == JsonValueKind.String) return t.GetString();
        if (t.ValueKind != JsonValueKind.Array) return null;
        var types = t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList();
        return types.Contains("string") ? "string" : types.FirstOrDefault(x => x != "null");
    }

    private static void Write(Utf8JsonWriter w, JsonElement value, JsonElement? schema, IReadOnlyDictionary<string, JsonElement> values)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = value.GetString() ?? "";
                var m = Whole.Match(s);
                if (m.Success && values.TryGetValue(m.Groups[1].Value, out var given)) Coerce(w, given, schema, values);
                else w.WriteStringValue(Text(s, values));
                break;
            }
            case JsonValueKind.Array:
            {
                var items = schema is { ValueKind: JsonValueKind.Object } sa && sa.TryGetProperty("items", out var it) ? it : (JsonElement?)null;
                w.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(w, item, items, values);
                w.WriteEndArray();
                break;
            }
            case JsonValueKind.Object:
            {
                JsonElement? props = null, extra = null;
                if (schema is { ValueKind: JsonValueKind.Object } so)
                {
                    if (so.TryGetProperty("properties", out var pp)) props = pp;
                    if (so.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind == JsonValueKind.Object) extra = ap;
                }
                w.WriteStartObject();
                foreach (var p in value.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    Write(w, p.Value, Property(props, p.Name) ?? extra, values);
                }
                w.WriteEndObject();
                break;
            }
            default:
                value.WriteTo(w);
                break;
        }
    }

    /// <summary>Writes a supplied value as the schema type; anything that doesn't convert is written as given.</summary>
    private static void Coerce(Utf8JsonWriter w, JsonElement given, JsonElement? schema, IReadOnlyDictionary<string, JsonElement> values)
    {
        var type = TypeOf(schema);
        if (given.ValueKind != JsonValueKind.String)
        {
            if (type == "string") w.WriteStringValue(given.GetRawText());
            else if (type == "array" && given.ValueKind != JsonValueKind.Array) { w.WriteStartArray(); Coerce(w, given, Items(schema), values); w.WriteEndArray(); }
            else given.WriteTo(w);
            return;
        }
        var s = (given.GetString() ?? "").Trim();
        switch (type)
        {
            case "integer" or "number" when double.TryParse(s, System.Globalization.NumberStyles.Float, Inv, out var d):
                if (type == "integer" && d == Math.Floor(d) && Math.Abs(d) < 9e15) w.WriteNumberValue((long)d);
                else w.WriteNumberValue(d);
                return;
            case "boolean" when Bool(s) is { } b:
                w.WriteBooleanValue(b);
                return;
            case "array":
            {
                if (s.StartsWith("[", StringComparison.Ordinal) && TryParse(s) is { ValueKind: JsonValueKind.Array } parsed)
                {
                    Write(w, parsed, schema, NoValues);
                    return;
                }
                // A comma-separated list: "1, 2, 3" -> [1, 2, 3] (each item as the schema's item type).
                w.WriteStartArray();
                foreach (var part in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    Coerce(w, Element(x => x.WriteStringValue(part.Trim())), Items(schema), values);
                w.WriteEndArray();
                return;
            }
            case "object" when s.StartsWith("{", StringComparison.Ordinal) && TryParse(s) is { ValueKind: JsonValueKind.Object } obj:
                Write(w, obj, schema, NoValues);
                return;
        }
        w.WriteStringValue(given.GetString());
    }

    private static JsonElement? Items(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("items", out var it) ? it : null;

    private static bool? Bool(string s) => s.ToLowerInvariant() switch
    {
        "true" or "yes" or "1" => true,
        "false" or "no" or "0" => false,
        _ => null,
    };

    private static JsonElement? TryParse(string s)
    {
        try
        {
            using var d = JsonDocument.Parse(s);
            return d.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>save_recipe: a sequence of tool calls saved under a name.</summary>
internal sealed class SaveRecipeTool(Func<ToolRegistry> registry) : IPlaxisTool
{
    public string Name => "save_recipe";
    public string Description =>
        "Save a sequence of tool calls as a named recipe to run again later (run_recipe), on this project or another. Use it after a " +
        "run the user was happy with, or when they ask to save a workflow (for example: boreholes, materials, mesh, phases, calculate, " +
        "results to Excel). Put {placeholders} in argument values that should change between runs (e.g. {final_level}). A step can be " +
        "run_plaxis_code with kept_id to reuse kept code.";
    public string PropertiesJson => """
    {
      "name": {"type": "string"},
      "description": {"type": "string"},
      "steps": {"type": "array", "items": {"type": "object", "properties": {"tool": {"type": "string"}, "arguments": {"type": "object"}}, "required": ["tool"]}}
    }
    """;
    public string[] Required => ["name", "steps"];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var steps = args.Array("steps").ToList();
        if (steps.Count == 0) return Task.FromResult(ToolResult.Error("A recipe needs at least one step."));
        var names = steps.Select(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "").ToList();
        var unknown = names.Where(n => registry().Find(n) is null).ToList();
        if (unknown.Count > 0) return Task.FromResult(ToolResult.Error($"Unknown tools: {string.Join(", ", unknown)}"));
        if (names.Any(n => n is "run_recipe" or "save_recipe" or "delete_recipe" or RedoStepsTool.ToolName))
            return Task.FromResult(ToolResult.Error("A recipe can't run, save or delete a recipe, or redo steps."));
        Directory.CreateDirectory(Recipes.Folder);
        var path = Recipes.PathFor(args.String("name"));
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            name = args.String("name"),
            description = args.OptString("description"),
            saved = DateTime.Now,
            steps,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return Task.FromResult(ToolResult.Ok(new { saved = path, steps = steps.Count }));
    }
}

/// <summary>list_recipes: the saved recipes with their steps and placeholders.</summary>
internal sealed class ListRecipesTool : IPlaxisTool
{
    public string Name => "list_recipes";
    public string Description => "List saved recipes with their descriptions, steps and placeholders. Call it before writing a workflow again that may be saved.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var list = new List<object>();
        if (Directory.Exists(Recipes.Folder))
            foreach (var file in Directory.GetFiles(Recipes.Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    var text = root.GetProperty("steps").GetRawText();
                    var placeholders = System.Text.RegularExpressions.Regex.Matches(text, @"\{([A-Za-z_][A-Za-z0-9_ ]*)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
                    list.Add(new
                    {
                        name = root.GetProperty("name").GetString(),
                        description = root.TryGetProperty("description", out var d) ? d.GetString() : null,
                        steps = root.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("tool").GetString()).ToList(),
                        placeholders,
                    });
                }
                catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException) { /* a damaged file is left out */ }
            }
        return Task.FromResult(ToolResult.Ok(new { recipes = list, folder = Recipes.Folder }));
    }
}

/// <summary>delete_recipe: removes a saved recipe the user no longer wants.</summary>
internal sealed class DeleteRecipeTool : IPlaxisTool
{
    public string Name => "delete_recipe";
    public string Description => "Delete a saved recipe the user no longer wants. Only when the user asks.";
    public string PropertiesJson => """{"name": {"type": "string"}}""";
    public string[] Required => ["name"];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var path = Recipes.PathFor(args.String("name"));
        if (!File.Exists(path)) return Task.FromResult(ToolResult.Error($"No recipe named '{args.String("name")}'. Call list_recipes."));
        File.Delete(path);
        return Task.FromResult(ToolResult.Ok(new { deleted = args.String("name") }));
    }
}

/// <summary>run_recipe: runs a saved recipe step by step in the open project.</summary>
internal sealed class RunRecipeTool(Func<ToolRegistry> registry) : IPlaxisTool
{
    public const string ToolName = "run_recipe";
    public string Name => ToolName;
    public string Description =>
        "Run a saved recipe step by step in the open project, filling {placeholders} from 'values' (converted to the number, boolean or " +
        "list the step's tool expects). Stops at the first failing step unless continue_on_error is set. The user is asked once, with " +
        "every step shown. Each step that changes the project is kept for redo_steps.";
    public string PropertiesJson => """
    {
      "name": {"type": "string"},
      "values": {"type": "object", "additionalProperties": {"type": "string"}},
      "continue_on_error": {"type": "boolean"}
    }
    """;
    public string[] Required => ["name"];
    public bool ModifiesModel => true;

    public async Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var path = Recipes.PathFor(args.String("name"));
        if (!File.Exists(path)) return ToolResult.Error($"No recipe named '{args.String("name")}'. Call list_recipes.");
        var fill = new Dictionary<string, JsonElement>();
        if (args.Has("values") && args.Raw["values"] is { ValueKind: JsonValueKind.Object } v)
            foreach (var p in v.EnumerateObject()) fill[p.Name] = p.Value.Clone();
        var keepGoing = args.OptBool("continue_on_error");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var results = new List<object>();
        var index = 0;
        var failed = false;
        foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            index++;
            var name = Placeholders.Text(step.GetProperty("tool").GetString() ?? "", fill);
            if (name is ToolName or "save_recipe" or "delete_recipe" or RedoStepsTool.ToolName)
            {
                results.Add(new { step = index, tool = name, error = "a recipe can't run this tool" });
                failed = true;
                if (!keepGoing) break;
                continue;
            }
            var tool = registry().Find(name);
            if (tool is null)
            {
                results.Add(new { step = index, tool = name, error = "unknown tool" });
                failed = true;
                if (!keepGoing) break;
                continue;
            }
            var stepArgs = step.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>();
            // Placeholders take the type the tool's schema asks for: "{level}" becomes -6.0, not "-6.0".
            stepArgs = Placeholders.Fill(stepArgs, tool.PropertiesJson, fill);
            var left = System.Text.RegularExpressions.Regex.Matches(JsonSerializer.Serialize(stepArgs), @"\{([A-Za-z_][A-Za-z0-9_ ]*)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
            if (left.Count > 0 && name != RunPlaxisCodeTool.ToolName)
            {
                results.Add(new { step = index, tool = name, error = "no value was given for: " + string.Join(", ", left) });
                failed = true;
                if (!keepGoing) break;
                continue;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            ToolResult result;
            try { result = await tool.ExecuteAsync(context, new ToolArgs(stepArgs), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { result = ToolResult.Error(ex.Message); }
            var project = RedoLog.ProjectOf(context);
            ActivityLog.Write(project, name, tool.ModifiesModel, stepArgs, !result.IsError, result.Text, clock.ElapsedMilliseconds);
            if (tool.ModifiesModel && !result.IsError) RedoLog.Write(project, name, stepArgs, result.Text, "recipe " + args.String("name"));
            results.Add(new { step = index, tool = name, ok = !result.IsError, result = result.Text.Length > 800 ? result.Text.Substring(0, 800) + "…" : result.Text });
            if (result.IsError)
            {
                failed = true;
                if (!keepGoing) break;
            }
        }
        var text = JsonSerializer.Serialize(new { recipe = args.String("name"), steps = results });
        return new ToolResult(text, IsError: failed && !keepGoing);
    }
}
