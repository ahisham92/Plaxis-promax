using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlaxisProMax.Modeller;

/// <summary>
/// The Modeller's own model file (.ppm): JSON with the project, materials, boreholes, structures, water levels, mesh
/// settings and phases (the format is described in docs/MODELLER.md). Plaxis has no text file it can import, so the
/// Modeller keeps its model here and builds it in Plaxis only when it calculates. Results go beside it as .ppr.
/// </summary>
public static class ModelFile
{
    public const string Format = "ppm";
    public const int Version = 1;

    internal static string? FolderOverride { get; set; }

    /// <summary>Where models are saved unless the user picks another folder: Documents\Plaxis ProMax\Models.</summary>
    public static string Folder => FolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Plaxis ProMax", "Models");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Reads a model file. Throws <see cref="InvalidDataException"/> with a plain reason when it isn't one.</summary>
    public static JsonObject Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("There is no model at " + path + ".");
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)); }
        catch (JsonException ex) { throw new InvalidDataException("This is not a Plaxis ProMax model (" + ex.Message + ")."); }
        return Validate(node);
    }

    /// <summary>Checks the format and version of a model given as JSON; returns it as an object.</summary>
    public static JsonObject Validate(JsonNode? node)
    {
        if (node is not JsonObject model || model["format"]?.GetValue<string>() != Format)
            throw new InvalidDataException("This is not a Plaxis ProMax model (.ppm).");
        var version = model["version"]?.GetValue<int>() ?? 0;
        if (version > Version) throw new InvalidDataException($"This model was saved by a newer Plaxis ProMax (model version {version}). Update Plaxis ProMax to open it.");
        var product = model["product"]?.GetValue<string>();
        if (product is not ("2D" or "3D")) throw new InvalidDataException("The model's product is neither 2D nor 3D.");
        foreach (var list in new[] { "materials", "boreholes", "structures", "waterlevels", "phases" })
            if (model[list] is not JsonArray) model[list] = new JsonArray();
        return model;
    }

    /// <summary>Saves a model (written whole to a temporary file first, so a failed save leaves the old file as it was).</summary>
    public static void Save(JsonObject model, string path)
    {
        Validate(model);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        Settings.SafeFile.Write(path, model.ToJsonString(Indented));
    }

    /// <summary>A file name from a title: what Windows does not allow is replaced.</summary>
    public static string SafeName(string? title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "Model" : title.Trim();
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })) name = name.Replace(c, '-');
        return name.Length > 120 ? name[..120] : name;
    }

    /// <summary>The results file of a model: the same name with .ppr.</summary>
    public static string ResultsPath(string modelPath) => Path.ChangeExtension(modelPath, ".ppr");

    /// <summary>Where Calculate in Plaxis saves the Plaxis project: a folder beside the model.</summary>
    public static string PlaxisProjectPath(string modelPath, string product)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(modelPath);
        return Path.Combine(dir, name + " (Plaxis)", name + (product == "3D" ? ".p3d" : ".p2dx"));
    }

    public sealed record Listed(string Name, string Path, string Folder, DateTime Saved);

    /// <summary>The models in the usual folder (and its subfolders), newest first.</summary>
    public static IReadOnlyList<Listed> List(int most = 200)
    {
        var folder = Folder;
        if (!Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder, "*.ppm", SearchOption.AllDirectories)
                .Select(f => new Listed(System.IO.Path.GetFileNameWithoutExtension(f), f, System.IO.Path.GetDirectoryName(f) ?? "", File.GetLastWriteTimeUtc(f)))
                .OrderByDescending(f => f.Saved).Take(most).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>A new, empty model, as the Modeller's File > New makes it.</summary>
    public static JsonObject Blank(string product, string title = "New project")
    {
        var d3 = product == "3D";
        return new JsonObject
        {
            ["format"] = Format, ["version"] = Version, ["title"] = title, ["product"] = d3 ? "3D" : "2D", ["model"] = "plane strain", ["elements"] = 15,
            ["units"] = new JsonObject { ["length"] = "m", ["force"] = "kN", ["time"] = "day" },
            ["contour"] = d3 ? new JsonObject { ["xmin"] = 0, ["xmax"] = 50, ["ymin"] = 0, ["ymax"] = 50, ["zmin"] = -25 } : new JsonObject { ["xmin"] = 0, ["xmax"] = 50, ["ymin"] = -25, ["ymax"] = 0 },
            ["materials"] = new JsonArray(), ["boreholes"] = new JsonArray(), ["structures"] = new JsonArray(), ["waterlevels"] = new JsonArray(),
            ["mesh"] = new JsonObject { ["coarseness"] = "Medium", ["refine"] = new JsonArray() },
            ["phases"] = new JsonArray(new JsonObject { ["id"] = "p0", ["name"] = "Initial phase", ["calc"] = "K0 procedure", ["parent"] = null, ["active"] = new JsonObject() }),
        };
    }

    /// <summary>The sample models that ship inside the program (File > Open sample).</summary>
    public static IReadOnlyDictionary<string, JsonObject> Samples()
    {
        var asm = typeof(ModelFile).Assembly;
        var out_ = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("PlaxisProMax.sample.", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) continue;
            using var r = new StreamReader(s, Encoding.UTF8);
            if (JsonNode.Parse(r.ReadToEnd()) is JsonObject m) out_[name["PlaxisProMax.sample.".Length..].Replace(".ppm", "")] = m;
        }
        return out_;
    }

    /// <summary>One line on what is in a model, for the chat and the log.</summary>
    public static string Summary(JsonObject m)
    {
        int Count(string k) => (m[k] as JsonArray)?.Count ?? 0;
        var layers = (m["boreholes"] as JsonArray)?.OfType<JsonObject>().Select(b => (b["layers"] as JsonArray)?.Count ?? 0).DefaultIfEmpty(0).Max() ?? 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"PLAXIS {m["product"]} model '{m["title"]}': {Count("boreholes")} boreholes, {layers} soil layers, {Count("materials")} materials, {Count("structures")} structures, {Count("waterlevels")} water levels, {Count("phases")} phases");
    }

    internal static string Resource(string logicalName)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException("Missing from the program: " + logicalName);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
