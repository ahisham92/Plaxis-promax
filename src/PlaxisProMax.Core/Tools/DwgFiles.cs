using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace PlaxisProMax.Tools;

/// <summary>
/// DWG drawings read and written directly, without AutoCAD (the ACadSharp library, MIT licence). Reading gives the same
/// entities as a DXF (<see cref="Dxf.Entity"/>): lines, polylines, circles, arcs, points and texts of the model space,
/// with block references opened up into their own geometry at the place, scale and rotation of each reference.
/// </summary>
internal static class Dwg
{
    /// <summary>How deep blocks inside blocks are followed.</summary>
    private const int Depth = 6;

    /// <summary>The most entities taken from one drawing (a survey drawing can hold millions).</summary>
    internal const int Most = 400_000;

    /// <summary>The entities of a DWG's model space. Throws InvalidDataException when the file can't be read as a DWG.</summary>
    public static List<Dxf.Entity> Read(string path)
    {
        CadDocument doc;
        try
        {
            using var reader = new DwgReader(path);
            doc = reader.Read();
        }
        catch (Exception ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or OutOfMemoryException))
        {
            throw new InvalidDataException("This DWG couldn't be read (" + ex.Message.Split('\n')[0].Trim() + "). Save it as DXF, or as an older DWG version (AutoCAD: Save As > AutoCAD 2018 Drawing), and attach that.", ex);
        }
        var found = new List<Dxf.Entity>();
        foreach (var e in doc.Entities) Add(found, e, Place.None, null, 0);
        return found;
    }

    /// <summary>Where a block reference puts its block: scale, then rotation, then the insertion point.</summary>
    private readonly record struct Place(double Sx, double Sy, double Sz, double Cos, double Sin, double X, double Y, double Z, bool Any)
    {
        public static readonly Place None = new(1, 1, 1, 1, 0, 0, 0, 0, false);

        public double[] At(double x, double y, double z)
        {
            if (!Any) return [x, y, z];
            x *= Sx; y *= Sy; z *= Sz;
            return [x * Cos - y * Sin + X, x * Sin + y * Cos + Y, z + Z];
        }

        /// <summary>This place inside an outer one (a block inside a block).</summary>
        public Place In(Place outer)
        {
            if (!outer.Any) return this;
            var o = outer.At(X, Y, Z);
            return new Place(Sx * outer.Sx, Sy * outer.Sy, Sz * outer.Sz, Cos * outer.Cos - Sin * outer.Sin, Sin * outer.Cos + Cos * outer.Sin, o[0], o[1], o[2], true);
        }

        public double Scale => Math.Abs(Sx);
        public double Turn => Math.Atan2(Sin, Cos) * 180 / Math.PI;
    }

    private static void Add(List<Dxf.Entity> found, Entity e, Place at, string? blockLayer, int depth)
    {
        if (found.Count >= Most) return;
        // Geometry drawn on layer 0 inside a block shows on the layer of the block reference.
        var layer = e.Layer?.Name is { Length: > 0 } l ? l : "0";
        if (layer == "0" && blockLayer is not null) layer = blockLayer;
        double[] P(XYZ p) => at.At(p.X, p.Y, p.Z);
        switch (e)
        {
            case Line line:
                found.Add(new Dxf.Entity("line", layer, [P(line.StartPoint), P(line.EndPoint)]));
                break;
            case LwPolyline lw:
                if (lw.Vertices.Count >= 2)
                    found.Add(new Dxf.Entity("polyline", layer, lw.Vertices.Select(v => at.At(v.Location.X, v.Location.Y, lw.Elevation)).ToList(), Closed: lw.IsClosed));
                break;
            case Polyline2D p2:
                if (p2.Vertices.Count() >= 2)
                    found.Add(new Dxf.Entity("polyline", layer, p2.Vertices.Select(v => at.At(v.Location.X, v.Location.Y, p2.Elevation)).ToList(), Closed: p2.IsClosed));
                break;
            case Polyline3D p3:
                if (p3.Vertices.Count() >= 2)
                    found.Add(new Dxf.Entity("polyline", layer, p3.Vertices.Select(v => P(v.Location)).ToList(), Closed: p3.IsClosed));
                break;
            case Arc arc: // before Circle: an arc is a circle with two angles
                found.Add(new Dxf.Entity("arc", layer, [P(arc.Center)], Radius: arc.Radius * at.Scale,
                    Start: arc.StartAngle * 180 / Math.PI + at.Turn, End: arc.EndAngle * 180 / Math.PI + at.Turn));
                break;
            case Circle circle:
                found.Add(new Dxf.Entity("circle", layer, [P(circle.Center)], Radius: circle.Radius * at.Scale));
                break;
            case Point point:
                found.Add(new Dxf.Entity("point", layer, [P(point.Location)]));
                break;
            case MText mtext:
                found.Add(new Dxf.Entity("text", layer, [P(mtext.InsertPoint)], Radius: mtext.Height * at.Scale, Text: Plain(mtext.Value)));
                break;
            case TextEntity text: // also attributes
                if (!string.IsNullOrWhiteSpace(text.Value))
                    found.Add(new Dxf.Entity("text", layer, [P(text.InsertPoint)], Radius: text.Height * at.Scale, Text: text.Value));
                break;
            case Insert insert when depth < Depth && insert.Block is { } block:
            {
                var turn = insert.Rotation;
                var place = new Place(insert.XScale, insert.YScale, insert.ZScale, Math.Cos(turn), Math.Sin(turn), insert.InsertPoint.X, insert.InsertPoint.Y, insert.InsertPoint.Z, true).In(at);
                // The block's own base point is where the insertion point lands.
                var origin = block.BlockEntity?.BasePoint ?? XYZ.Zero;
                if (origin.X != 0 || origin.Y != 0 || origin.Z != 0)
                {
                    var shift = place.At(-origin.X, -origin.Y, -origin.Z);
                    place = place with { X = shift[0], Y = shift[1], Z = shift[2] };
                }
                foreach (var inner in block.Entities) Add(found, inner, place, layer, depth + 1);
                foreach (var attribute in insert.Attributes) Add(found, attribute, at, layer, depth + 1);
                break;
            }
        }
    }

    /// <summary>MText without its formatting codes: \P is a new line, {\fArial;...} and \H2; style marks go.</summary>
    internal static string Plain(string? value)
    {
        var t = (value ?? "").Replace("\\P", " ").Replace("\\~", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\\[A-Za-z][^;\\{}]*;", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\\[LlOoKk]", "");
        return t.Replace("{", "").Replace("}", "").Trim();
    }

    /// <summary>A DWG (AutoCAD 2018 format) of the entities: layers by name, polylines, lines, circles, points and texts.</summary>
    public static void Write(string path, IReadOnlyList<Dxf.Entity> entities)
    {
        var doc = new CadDocument();
        var layers = new Dictionary<string, ACadSharp.Tables.Layer>(StringComparer.OrdinalIgnoreCase);
        ACadSharp.Tables.Layer LayerOf(string name)
        {
            name = Dxf.LayerName(name);
            if (name == "0") return doc.Layers["0"];
            if (layers.TryGetValue(name, out var have)) return have;
            var made = new ACadSharp.Tables.Layer(name);
            doc.Layers.Add(made);
            return layers[name] = made;
        }
        static XYZ V(double[] p) => new(p[0], p.Length > 1 ? p[1] : 0, p.Length > 2 ? p[2] : 0);
        foreach (var e in entities)
        {
            if (e.Points.Count == 0) continue;
            Entity? made = e.Kind switch
            {
                "line" when e.Points.Count >= 2 => new Line { StartPoint = V(e.Points[0]), EndPoint = V(e.Points[1]) },
                "point" => new Point { Location = V(e.Points[0]) },
                "circle" => new Circle { Center = V(e.Points[0]), Radius = e.Radius > 0 ? e.Radius : 1 },
                "text" => new TextEntity { InsertPoint = V(e.Points[0]), Height = e.Radius > 0 ? e.Radius : 0.25, Value = e.Text.Replace("\n", " ") },
                "polyline" when e.Points.Count >= 2 => Poly(e),
                _ => null,
            };
            if (made is null) continue;
            made.Layer = LayerOf(e.Layer);
            doc.Entities.Add(made);
        }
        using var writer = new DwgWriter(path, doc);
        writer.Write();

        static LwPolyline Poly(Dxf.Entity e)
        {
            var poly = new LwPolyline { IsClosed = e.Closed, Elevation = e.Points[0].Length > 2 ? e.Points[0][2] : 0 };
            foreach (var p in e.Points) poly.Vertices.Add(new LwPolyline.Vertex(new XY(p[0], p[1])));
            return poly;
        }
    }
}
