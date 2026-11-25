using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PlaxisProMax.Tools;

/// <summary>
/// CAD drawings as files, without AutoCAD: DXF is plain text (pairs of a group code and a value), so it is read and
/// written here by hand. Reading gives the layers and the geometry on them (lines, polylines, circles, arcs, points,
/// texts) in the drawing's own units; writing makes an R12 DXF every CAD program opens. DWG files are read and written
/// by <see cref="Dwg"/> into the same entities. No Plaxis or WPF.
/// </summary>
internal static class Dxf
{
    internal sealed record Entity(string Kind, string Layer, List<double[]> Points, bool Closed = false, double Radius = 0, string Text = "", double Start = 0, double End = 0);

    /// <summary>The entities of a DXF's ENTITIES section. Throws InvalidDataException when the text is not a DXF.</summary>
    public static List<Entity> Read(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var pairs = new List<(int Code, string Value)>();
        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                throw new InvalidDataException(pairs.Count == 0 ? "This is not a text DXF (a binary DXF can't be read: save it as a text DXF or a DWG from the CAD program)." : $"Line {i + 1} is not a DXF group code.");
            pairs.Add((code, lines[i + 1].Trim()));
        }
        var start = pairs.FindIndex(p => p.Code == 2 && p.Value == "ENTITIES");
        if (start < 0) throw new InvalidDataException("The DXF has no ENTITIES section.");
        var found = new List<Entity>();
        Entity? poly = null; // an old-style POLYLINE collecting its VERTEX records
        for (var i = start + 1; i < pairs.Count; i++)
        {
            if (pairs[i].Code != 0) continue;
            var kind = pairs[i].Value;
            if (kind == "ENDSEC") break;
            var end = i + 1;
            while (end < pairs.Count && pairs[end].Code != 0) end++;
            var body = pairs.GetRange(i + 1, end - i - 1);
            string S(int code) => body.FirstOrDefault(p => p.Code == code).Value ?? "";
            double D(int code) => double.TryParse(S(code), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            var layer = S(8) is { Length: > 0 } l ? l : "0";
            switch (kind)
            {
                case "LINE": found.Add(new Entity("line", layer, [[D(10), D(20), D(30)], [D(11), D(21), D(31)]])); break;
                case "POINT": found.Add(new Entity("point", layer, [[D(10), D(20), D(30)]])); break;
                case "CIRCLE": found.Add(new Entity("circle", layer, [[D(10), D(20), D(30)]], Radius: D(40))); break;
                case "ARC": found.Add(new Entity("arc", layer, [[D(10), D(20), D(30)]], Radius: D(40), Start: D(50), End: D(51))); break;
                case "TEXT" or "MTEXT":
                    found.Add(new Entity("text", layer, [[D(10), D(20), D(30)]], Text: string.Concat(body.Where(p => p.Code is 3 or 1).OrderBy(p => p.Code == 1 ? 1 : 0).Select(p => p.Value)).Replace("\\P", " ")));
                    break;
                case "LWPOLYLINE":
                {
                    var points = new List<double[]>();
                    var z = D(38);
                    foreach (var p in body)
                    {
                        if (!double.TryParse(p.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                        if (p.Code == 10) points.Add([v, 0, z]);
                        else if (p.Code == 20 && points.Count > 0) points[^1][1] = v;
                    }
                    found.Add(new Entity("polyline", layer, points, Closed: ((int)D(70) & 1) == 1));
                    break;
                }
                case "POLYLINE": poly = new Entity("polyline", layer, [], Closed: ((int)D(70) & 1) == 1); break;
                case "VERTEX": poly?.Points.Add([D(10), D(20), D(30)]); break;
                case "SEQEND": if (poly is not null) { found.Add(poly); poly = null; } break;
            }
            i = end - 1;
        }
        return found;
    }

    /// <summary>An R12 DXF of the entities: layers by name, polylines, lines, circles, points and texts.</summary>
    public static string Write(IReadOnlyList<Entity> entities)
    {
        var sb = new StringBuilder();
        void P(int code, string value) => sb.Append(code.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(value).Append('\n');
        string N(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
        void Xyz(int offset, double[] p) { P(10 + offset, N(p[0])); P(20 + offset, N(p.Length > 1 ? p[1] : 0)); P(30 + offset, N(p.Length > 2 ? p[2] : 0)); }
        var layers = entities.Select(e => Layer(e.Layer)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        P(0, "SECTION"); P(2, "TABLES"); P(0, "TABLE"); P(2, "LAYER"); P(70, layers.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var layer in layers) { P(0, "LAYER"); P(2, layer); P(70, "0"); P(62, "7"); P(6, "CONTINUOUS"); }
        P(0, "ENDTAB"); P(0, "ENDSEC");
        P(0, "SECTION"); P(2, "ENTITIES");
        foreach (var e in entities)
        {
            if (e.Points.Count == 0) continue;
            var layer = Layer(e.Layer);
            switch (e.Kind)
            {
                case "line" when e.Points.Count >= 2: P(0, "LINE"); P(8, layer); Xyz(0, e.Points[0]); Xyz(1, e.Points[1]); break;
                case "point": P(0, "POINT"); P(8, layer); Xyz(0, e.Points[0]); break;
                case "circle": P(0, "CIRCLE"); P(8, layer); Xyz(0, e.Points[0]); P(40, N(e.Radius)); break;
                case "text": P(0, "TEXT"); P(8, layer); Xyz(0, e.Points[0]); P(40, N(e.Radius > 0 ? e.Radius : 0.25)); P(1, e.Text.Replace("\n", " ")); break;
                case "polyline" when e.Points.Count >= 2:
                    P(0, "POLYLINE"); P(8, layer); P(66, "1"); P(70, e.Closed ? "1" : "0");
                    foreach (var p in e.Points) { P(0, "VERTEX"); P(8, layer); Xyz(0, p); }
                    P(0, "SEQEND"); P(8, layer);
                    break;
            }
        }
        P(0, "ENDSEC"); P(0, "EOF");
        return sb.ToString();
    }

    internal static string LayerName(string name) => Layer(name);

    private static string Layer(string name)
    {
        var clean = new string((name ?? "").Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '$' ? ch : '_').ToArray());
        return clean.Length == 0 ? "0" : clean.Length > 31 ? clean.Substring(0, 31) : clean;
    }
}

/// <summary>read_drawing_file: what is in a DWG or DXF drawing, by layer, so its lines can become soil contours, walls or excavation lines.</summary>
internal sealed class ReadDrawingFileTool : IPlaxisTool
{
    public const int DefaultMost = 200, Most = 2000;

    public string Name => "read_drawing_file";
    public string Description =>
        "Reads a CAD drawing on this PC, DWG or DXF: first call it with only the path for the layers (how many lines, polylines, " +
        "circles, arcs, points and texts each has) and the drawing's extents; then with layer for that layer's geometry as " +
        "coordinates in the drawing's own units (check them against a known dimension before using them: drawings are often in mm). " +
        "Use the coordinates for the soil contour, walls, excavation lines or structures. Blocks in a DWG are opened up into " +
        "their own lines at each place they are used. A drawing as PDF or picture: use the Read tool. Not a change to the project.";
    public string PropertiesJson => """
    {
      "path": {"type": "string", "description": "The DWG or DXF file's full path."},
      "layer": {"type": "string", "description": "Only this layer's geometry (the name as listed). Leave out for the list of layers."},
      "kinds": {"type": "array", "items": {"type": "string", "enum": ["line", "polyline", "circle", "arc", "point", "text"]}, "description": "With layer: only these kinds."},
      "most": {"type": "integer", "description": "With layer: at most this many entities (default 200, up to 2000)."}
    }
    """;
    public string[] Required => ["path"];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct) => Task.FromResult(Read(args));

    private static bool IsDwg(string path) => Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase);

    // The last drawing read, so asking for its layers and then for one layer reads a big DWG once.
    private static (string Path, DateTime Written, long Size, List<Dxf.Entity> Entities)? _last;
    private static readonly object LastGate = new();

    private static List<Dxf.Entity> Cached(string path)
    {
        var info = new FileInfo(path);
        lock (LastGate)
            if (_last is { } l && l.Path == info.FullName && l.Written == info.LastWriteTimeUtc && l.Size == info.Length) return l.Entities;
        var read = IsDwg(path) ? Dwg.Read(path) : Dxf.Read(Encoding.UTF8.GetString(SharedRead.AllBytes(path)));
        lock (LastGate) _last = (info.FullName, info.LastWriteTimeUtc, info.Length, read);
        return read;
    }

    internal static ToolResult Read(ToolArgs args)
    {
        var path = (args.OptString("path") ?? "").Trim().Trim('"');
        if (path.Length == 0) return ToolResult.Error("Give the drawing's full path in 'path'.");
        if (!File.Exists(path)) return ToolResult.Error($"There is no file at {path}.");
        List<Dxf.Entity> all;
        try { all = Cached(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return ToolResult.Error($"Couldn't read {Path.GetFileName(path)}: {ex.Message}"); }
        if (all.Count == 0) return ToolResult.Error("The drawing has no lines, polylines, circles, arcs, points or texts in its model space" + (IsDwg(path) ? " (it may be drawn in a layout: copy it to model space and save again)." : " (blocks in a DXF are not read: explode them in the CAD program and save again, or attach the DWG)."));
        var points = all.SelectMany(e => e.Points).ToList();
        var extents = new { min = new[] { points.Min(p => p[0]), points.Min(p => p[1]) }, max = new[] { points.Max(p => p[0]), points.Max(p => p[1]) } };
        var layer = args.OptString("layer");
        if (string.IsNullOrWhiteSpace(layer))
            return ToolResult.Ok(new
            {
                file = Path.GetFileName(path), extents,
                layers = all.GroupBy(e => e.Layer, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count())
                    .Select(g => new { layer = g.Key, entities = g.GroupBy(e => e.Kind).ToDictionary(k => k.Key, k => k.Count()) }).ToList(),
                next = "Call again with layer for the geometry. Check the units against a known dimension first.",
            });
        var kinds = args.Array("kinds").Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = all.Where(e => e.Layer.Equals(layer!.Trim(), StringComparison.OrdinalIgnoreCase) && (kinds.Count == 0 || kinds.Contains(e.Kind))).ToList();
        if (chosen.Count == 0) return ToolResult.Error($"Nothing on layer '{layer}'" + (kinds.Count > 0 ? " of those kinds" : "") + ". Call with only the path for the layers.");
        var most = (int)Math.Max(1, Math.Min(Most, args.OptLong("most") ?? DefaultMost));
        double R(double d) => Math.Round(d, 4);
        return ToolResult.Ok(new
        {
            file = Path.GetFileName(path), layer, count = chosen.Count,
            entities = chosen.Take(most).Select(e => new
            {
                kind = e.Kind,
                points = e.Points.Select(p => p[2] == 0 ? new[] { R(p[0]), R(p[1]) } : new[] { R(p[0]), R(p[1]), R(p[2]) }).ToList(),
                closed = e.Kind == "polyline" ? e.Closed : (bool?)null,
                radius = e.Kind is "circle" or "arc" ? R(e.Radius) : (double?)null,
                angles = e.Kind == "arc" ? new[] { R(e.Start), R(e.End) } : null,
                text = e.Kind == "text" ? e.Text : null,
            }).ToList(),
            cut = chosen.Count > most ? $"Only the first {most} of {chosen.Count}; pass a larger most, or kinds." : null,
        });
    }
}

/// <summary>make_drawing: a DXF or DWG file of lines, polylines, circles, points and texts (a section, a mesh outline, a deflected shape).</summary>
internal sealed class MakeDrawingTool : IPlaxisTool
{
    public string Name => "make_drawing";
    public string Description =>
        "Make a CAD drawing for the user as a DXF file (default; opens in AutoCAD and every CAD program) or a DWG file (format dwg, when the user asks for DWG): the model's geometry, a cross-section, " +
        "the excavation stages, a deflected shape, result points with their values as texts. Give 'entities', each with a kind " +
        "(polyline, line, circle, point, text), a layer name and points in the project's units; polylines can be closed; a text " +
        "has its words in 'text' and its height in 'height'. The file goes to Documents\\Plaxis ProMax\\Made in chat unless folder " +
        "is given, and never replaces a file. The chat then shows it with Open and Show in folder, so don't repeat the path. Not a change to the project.";
    public string PropertiesJson => """
    {
      "title": {"type": "string", "description": "The file name unless file_name is given."},
      "entities": {"type": "array", "items": {"type": "object", "properties": {
        "kind": {"type": "string", "enum": ["polyline", "line", "circle", "point", "text"]},
        "layer": {"type": "string", "description": "The layer's name (letters, digits, _ and -)."},
        "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "[x, y] or [x, y, z] each: two for a line, two or more for a polyline, one for a circle's centre, a point or a text."},
        "closed": {"type": "boolean", "description": "polyline: joins the last point to the first."},
        "radius": {"type": "number", "description": "circle: its radius."},
        "text": {"type": "string"},
        "height": {"type": "number", "description": "text: its height in drawing units (default 0.25)."}
      }, "required": ["kind", "points"]}},
      "format": {"type": "string", "enum": ["dxf", "dwg"], "description": "Default dxf. dwg only when the user asks for a DWG."},
      "file_name": {"type": "string", "description": "The file name without folder (default: the title). The extension is added."},
      "folder": {"type": "string", "description": "Where to save it. Leave it out unless the user asked for a place."}
    }
    """;
    public string[] Required => ["title", "entities"];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct) => Task.FromResult(Make(args, MakeDocumentTool.DefaultFolder));

    internal static ToolResult Make(ToolArgs args, string defaultFolder)
    {
        var entities = new List<Dxf.Entity>();
        foreach (var e in args.Array("entities"))
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var kind = e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()!.Trim().ToLowerInvariant() : "";
            if (kind is not ("polyline" or "line" or "circle" or "point" or "text")) return ToolResult.Error($"An entity's kind must be polyline, line, circle, point or text (got '{kind}').");
            var points = new List<double[]>();
            if (e.TryGetProperty("points", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2 && p.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number))
                    {
                        var v = p.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                        points.Add([v[0], v[1], v.Length > 2 ? v[2] : 0]);
                    }
            var need = kind is "polyline" or "line" ? 2 : 1;
            if (points.Count < need) return ToolResult.Error($"A {kind} needs at least {need} point(s) as [x, y] numbers.");
            double Num(string key) => e.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : 0;
            entities.Add(new Dxf.Entity(kind, e.TryGetProperty("layer", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "0", points,
                Closed: e.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True,
                Radius: kind == "text" ? Num("height") : Num("radius"),
                Text: e.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : ""));
        }
        if (entities.Count == 0) return ToolResult.Error("Give the drawing's 'entities' (polylines, lines, circles, points, texts).");
        var format = (args.OptString("format") ?? "dxf").Trim().TrimStart('.').ToLowerInvariant();
        if (format is not ("dxf" or "dwg")) return ToolResult.Error("format is dxf or dwg.");
        var dwg = format == "dwg";
        string folder;
        try { folder = OutputPaths.Checked(args.OptString("folder")) is { Length: > 0 } f ? f.Trim().Trim('"') : defaultFolder; }
        catch (ArgumentException ex) { return ToolResult.Error(ex.Message); }
        var title = (args.OptString("title") ?? "").Trim();
        var name = OfficeDocs.SafeName(Path.GetFileNameWithoutExtension((args.OptString("file_name") ?? "").Trim()) is { Length: > 0 } n ? n : title, "Drawing");
        try
        {
            Directory.CreateDirectory(folder);
            var path = OfficeDocs.FreePath(folder, name, dwg ? ".dwg" : ".dxf");
            if (dwg) Dwg.Write(path, entities);
            else File.WriteAllText(path, Dxf.Write(entities), new UTF8Encoding(false));
            return ToolResult.Ok(new
            {
                file = path, format = dwg ? "DWG drawing" : "DXF drawing", title, entities = entities.Count,
                layers = entities.Select(e => e.Layer).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                shown = "The chat shows this file as a card with Open, Show in folder and Save a copy. Say in a line what is in it; don't repeat the path.",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ToolResult.Error($"Couldn't write the file in {folder}: {ex.Message}");
        }
    }
}
