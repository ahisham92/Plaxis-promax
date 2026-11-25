using System.Globalization;
using System.Text.Json.Nodes;

namespace PlaxisProMax.Modeller;

/// <summary>
/// Checks a Modeller model before it goes to Plaxis, so a calculation (and the licence time it takes) isn't spent on a
/// model Plaxis would refuse: missing materials, boreholes that disagree, objects outside the soil, phases that point
/// nowhere. Nothing here analyses the model; it only reads it.
/// </summary>
public static class ModelChecks
{
    public sealed record Finding(string Level, string Text, string? Object = null);

    private static readonly string[] Need = ["E"];
    private static readonly Dictionary<string, string[]> SoilNeeds = new(StringComparer.Ordinal)
    {
        ["Linear elastic"] = ["E", "nu"],
        ["Mohr-Coulomb"] = ["E", "nu", "c", "phi"],
        ["Hardening soil"] = ["E50", "Eoed", "Eur", "c", "phi"],
        ["HS small"] = ["E50", "Eoed", "Eur", "c", "phi", "G0", "gamma07"],
        ["Soft soil"] = ["lambda", "kappa", "phi"],
    };
    private static readonly Dictionary<string, string[]> StructNeeds = new(StringComparer.Ordinal)
    {
        ["plate"] = ["EA", "EI"], ["anchor"] = ["EA"], ["geogrid"] = ["EA"], ["embedded"] = ["E", "diameter"],
    };
    private static readonly Dictionary<string, string> MatKind = new(StringComparer.Ordinal)
    {
        ["plate"] = "plate", ["anchor"] = "anchor", ["fixedanchor"] = "anchor", ["geogrid"] = "geogrid", ["embedded"] = "embedded", ["polygon"] = "soil",
    };

    public static IReadOnlyList<Finding> Check(JsonObject m)
    {
        var f = new List<Finding>();
        void Error(string t, string? o = null) => f.Add(new("error", t, o));
        void Warn(string t, string? o = null) => f.Add(new("warning", t, o));
        void Info(string t, string? o = null) => f.Add(new("info", t, o));
        var d3 = Str(m, "product") == "3D";
        var mats = Items(m, "materials");
        var byId = mats.Where(x => Str(x, "id") is not null).GroupBy(x => Str(x, "id")!).ToDictionary(g => g.Key, g => g.First());

        // Contour
        var c = m["contour"] as JsonObject;
        double? xmin = Num(c, "xmin"), xmax = Num(c, "xmax");
        if (c is null || xmin is null || xmax is null || xmax <= xmin) Error("The soil contour has no width: set x min below x max (Project).");
        if (d3 && (Num(c, "ymin") is not { } y0 || Num(c, "ymax") is not { } y1 || y1 <= y0)) Error("The soil contour has no depth in y: set y min below y max (Project).");

        // Materials
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mat in mats)
        {
            var name = Str(mat, "name") ?? Str(mat, "id") ?? "?";
            var kind = Str(mat, "kind") ?? "soil";
            var needs = kind == "soil" ? (SoilNeeds.TryGetValue(Str(mat, "model") ?? "Mohr-Coulomb", out var n) ? n : Need) : StructNeeds.GetValueOrDefault(kind, []);
            if (kind == "plate" && d3) needs = ["E", "d"];
            var missing = needs.Where(k => Num(mat, k) is null).ToList();
            if (missing.Count > 0) Error($"Material '{name}' has no {string.Join(", ", missing)}.", Str(mat, "id"));
            if (kind == "soil")
            {
                if (Num(mat, "gammaUnsat") is null || Num(mat, "gammaSat") is null) Error($"Material '{name}' has no unit weight (γ unsat and γ sat).", Str(mat, "id"));
                else if (Num(mat, "gammaSat") < Num(mat, "gammaUnsat")) Warn($"Material '{name}': γ sat is below γ unsat.", Str(mat, "id"));
                if (Num(mat, "nu") is { } nu && (nu < 0 || nu >= 0.5)) Error($"Material '{name}': ν' must be from 0 to below 0.5.", Str(mat, "id"));
                if (Num(mat, "phi") is { } phi && (phi < 0 || phi > 60)) Warn($"Material '{name}': φ' = {F(phi)}° is unusual.", Str(mat, "id"));
                if (Num(mat, "psi") is { } psi && Num(mat, "phi") is { } phi2 && psi > phi2) Error($"Material '{name}': ψ is larger than φ'.", Str(mat, "id"));
            }
        }

        // Boreholes and layers
        var holes = Items(m, "boreholes");
        if (holes.Count == 0) Error("There is no borehole. Add one in Soil mode: it gives the soil layers and the water head.");
        var counts = holes.Select(b => (b["layers"] as JsonArray)?.Count ?? 0).Distinct().ToList();
        if (counts.Count > 1) Error("The boreholes do not have the same number of layers. Every borehole needs every layer (a layer can be 0 thick).");
        foreach (var b in holes)
        {
            var name = Str(b, "name") ?? "borehole";
            var top = Num(b, "top") ?? 0;
            var layers = (b["layers"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            if (layers.Count == 0) Error($"{name} has no soil layers.", Str(b, "id"));
            var level = top;
            for (var i = 0; i < layers.Count; i++)
            {
                var bottom = Num(layers[i], "bottom");
                if (bottom is null) { Error($"{name}, layer {i + 1}: no bottom level.", Str(b, "id")); continue; }
                if (bottom > level + 1e-9) Error($"{name}, layer {i + 1}: its bottom ({F(bottom.Value)}) is above its top ({F(level)}).", Str(b, "id"));
                level = bottom.Value;
                var mid = Str(layers[i], "material");
                if (mid is null || !byId.TryGetValue(mid, out var lm)) Error($"{name}, layer {i + 1}: no soil material.", Str(b, "id"));
                else { used.Add(mid); if (Str(lm, "kind") is { } k && k != "soil") Error($"{name}, layer {i + 1}: '{Str(lm, "name")}' is not a soil material.", Str(b, "id")); }
            }
            if (xmin is not null && xmax is not null && (Num(b, "x") < xmin || Num(b, "x") > xmax)) Warn($"{name} is outside the soil contour.", Str(b, "id"));
            if (Num(b, "head") is { } head && layers.Count > 0 && head < level) Info($"{name}: the water head is below the bottom of the model, so the soil is dry.", Str(b, "id"));
        }
        if (!d3 && holes.Count > 0 && c is not null && Num(c, "ymin") is { } cy && holes.Any(b => ((b["layers"] as JsonArray)?.OfType<JsonObject>().LastOrDefault() is { } last) && Math.Abs((Num(last, "bottom") ?? cy) - cy) > 1e-6))
            Info("The bottom of the boreholes sets the bottom of the model; the contour's y min is not used.");

        // Structures
        var structs = Items(m, "structures");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in structs)
        {
            var id = Str(s, "id") ?? "";
            ids.Add(id);
            var type = Str(s, "type") ?? "";
            var name = Str(s, "name") ?? id;
            var pts = (s["points"] as JsonArray)?.OfType<JsonArray>().Select(p => p.Select(v => D(v) ?? 0).ToArray()).ToList() ?? [];
            int needPts = type switch { "pointload" or "fixedanchor" => 1, "polygon" or "surfaceload" => 3, _ => 2 };
            if (pts.Count < needPts) { Error($"'{name}' needs {needPts} point{(needPts == 1 ? "" : "s")}, it has {pts.Count}.", id); continue; }
            if (pts.Any(p => p.Length < (d3 ? 3 : 2))) { Error($"'{name}': each point needs {(d3 ? "x, y and z" : "x and y")}.", id); continue; }
            if (needPts == 2 && Dist(pts[0], pts[^1]) < 1e-6) Error($"'{name}' has no length.", id);
            if (type == "polygon" && !d3 && Math.Abs(Area(pts)) < 1e-9) Error($"'{name}' has no area.", id);
            if (MatKind.TryGetValue(type, out var kind))
            {
                var mid = Str(s, "material");
                if (mid is null) { if (type != "polygon") Error($"'{name}' has no material. Pick one in its properties.", id); }
                else if (!byId.TryGetValue(mid, out var sm)) Error($"'{name}': its material is not in the model.", id);
                else { used.Add(mid); if ((Str(sm, "kind") ?? "soil") != kind) Error($"'{name}': '{Str(sm, "name")}' is a {Str(sm, "kind")} material, not a {kind} one.", id); }
            }
            if (xmin is not null && xmax is not null && pts.Any(p => p[0] < xmin - 1e-6 || p[0] > xmax + 1e-6)) Warn($"'{name}' goes outside the soil contour.", id);
            if (d3 && type == "plate" && pts.Count == 2 && (Num(s, "depth") ?? 0) <= 0) Error($"'{name}': a wall plate needs a depth.", id);
            if (d3 && type == "polygon" && (Num(s, "depth") ?? 0) <= 0) Error($"'{name}': a soil volume needs a depth.", id);
            if (type == "lineload" && Num(s, "qx") is null && Num(s, "qy") is null && Num(s, "qz") is null) Warn($"'{name}' has no load value.", id);
        }
        foreach (var w in Items(m, "waterlevels"))
        {
            ids.Add(Str(w, "id") ?? "");
            if (((w["points"] as JsonArray)?.Count ?? 0) < 2) Error($"Water level '{Str(w, "name")}' needs 2 points or more.", Str(w, "id"));
        }

        // Phases
        var phases = Items(m, "phases");
        var phaseIds = phases.Select(p => Str(p, "id") ?? "").ToHashSet(StringComparer.Ordinal);
        if (phases.Count == 0) Error("There is no phase. The initial phase is needed.");
        else
        {
            var calc0 = Str(phases[0], "calc") ?? "K0 procedure";
            if (calc0 is not ("K0 procedure" or "Gravity loading")) Error("The initial phase must be K0 procedure or Gravity loading.", Str(phases[0], "id"));
            if (calc0 == "K0 procedure" && !Level(holes)) Warn("K0 procedure is meant for level ground and horizontal layers; these layers or the ground slope. Plaxis suggests Gravity loading then.", Str(phases[0], "id"));
            if (phases.Count == 1) Warn("Only the initial phase: add phases in Staged construction for the work to calculate.");
        }
        var layerIds = Enumerable.Range(0, counts.DefaultIfEmpty(0).Max()).Select(i => "layer" + i.ToString(CultureInfo.InvariantCulture));
        var known = ids.Concat(layerIds).ToHashSet(StringComparer.Ordinal);
        foreach (var p in phases.Skip(1))
        {
            var name = Str(p, "name") ?? "phase";
            var parent = Str(p, "parent");
            if (parent is not null && !phaseIds.Contains(parent)) Error($"Phase '{name}' starts from a phase that is not there.", Str(p, "id"));
            if (Chain(phases, p) is false) Error($"Phase '{name}' is in a loop of phases that start from each other.", Str(p, "id"));
            if (Str(p, "calc") is "K0 procedure" or "Gravity loading") Error($"Phase '{name}': {Str(p, "calc")} is for the initial phase only.", Str(p, "id"));
            if (Str(p, "calc") == "Consolidation" && (Num(p, "time") ?? 0) <= 0) Warn($"Phase '{name}' is a consolidation with no time interval.", Str(p, "id"));
            if (Str(p, "water") is { } wl && !ids.Contains(wl)) Error($"Phase '{name}' uses a water level that is not there.", Str(p, "id"));
        }
        foreach (var p in phases)
            if (p["active"] is JsonObject active)
                foreach (var kv in active)
                    if (!known.Contains(kv.Key)) Warn($"Phase '{Str(p, "name")}' switches '{kv.Key}', which is not in the model.", Str(p, "id"));
        foreach (var s in structs.Where(s => Str(s, "type") is not "polygon"))
            if (phases.Count > 1 && !phases.Skip(1).Any(p => (p["active"] as JsonObject)?[Str(s, "id") ?? ""]?.GetValue<bool>() == true) && (phases[0]["active"] as JsonObject)?[Str(s, "id") ?? ""]?.GetValue<bool>() != true)
                Info($"'{Str(s, "name")}' is never switched on in a phase.", Str(s, "id"));

        foreach (var mat in mats) if (Str(mat, "id") is { } mid && !used.Contains(mid)) Info($"Material '{Str(mat, "name")}' is not used.", mid);
        return f.OrderBy(x => x.Level switch { "error" => 0, "warning" => 1, _ => 2 }).ToList();
    }

    public static int Errors(IEnumerable<Finding> f) => f.Count(x => x.Level == "error");

    private static bool Level(List<JsonObject> holes)
    {
        if (holes.Count < 2) return true;
        var levels = holes.Select(b => new[] { Num(b, "top") ?? 0 }.Concat((b["layers"] as JsonArray)?.OfType<JsonObject>().Select(l => Num(l, "bottom") ?? 0) ?? []).ToList()).ToList();
        for (var i = 0; i < levels[0].Count; i++)
            if (levels.Any(l => l.Count > i && Math.Abs(l[i] - levels[0][i]) > 0.01)) return false;
        return true;
    }

    private static bool Chain(List<JsonObject> phases, JsonObject p)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur = p;
        while (cur is not null)
        {
            var id = Str(cur, "id") ?? "";
            if (!seen.Add(id)) return false;
            var parent = Str(cur, "parent");
            cur = parent is null ? null : phases.FirstOrDefault(q => Str(q, "id") == parent);
        }
        return true;
    }

    internal static List<JsonObject> Items(JsonObject m, string key) => (m[key] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    internal static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static double? Num(JsonObject? o, string key) => D(o?[key]);

    internal static double? D(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        return null;
    }

    private static double Dist(double[] a, double[] b) => Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());

    private static double Area(List<double[]> p)
    {
        double a = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += (p[j][0] + p[i][0]) * (p[j][1] - p[i][1]);
        return a / 2;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
