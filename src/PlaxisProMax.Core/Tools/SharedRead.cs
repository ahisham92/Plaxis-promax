using System.Globalization;
using System.IO;

namespace PlaxisProMax.Tools;

/// <summary>
/// Reads a file another program has open. Excel and Word keep the file they show open for writing, and
/// File.ReadAllBytes refuses it then ("being used by another process", 2026-10-28: a drawing list attached from the
/// project folder while it was open in Excel). Sharing read and write lets us read the last saved copy.
/// </summary>
internal static class SharedRead
{
    public static byte[] AllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>What Sketch ProMax's Xlsx helper gives the document writers.</summary>
internal static class Xlsx
{
    /// <summary>
    /// Text a spreadsheet would read as a formula: it starts with =, +, -, @ (or a tab or return before one). A plain
    /// number such as -12.5 is not.
    /// </summary>
    public static bool FormulaLike(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var t = s!.TrimStart('\t', '\r', '\n', ' ');
        if (t.Length == 0 || "=+-@".IndexOf(t[0]) < 0) return false;
        return !(t.Length == s.Length && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
    }

    /// <summary>
    /// The cells of one sheet (default the first, in the workbook's tab order) as text, row by row: shared strings, inline
    /// strings, numbers and the cached value of formulas. Gaps are filled with empty cells (rows Excel left out as empty
    /// are not returned). Throws InvalidDataException when the file is not a workbook that can be read.
    /// </summary>
    public static List<List<string>> Read(string path, int sheet = 1)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var shared = new List<string>();
            if (Entry(zip, "xl/sharedStrings.xml") is { } ss)
                using (var st = ss.Open())
                    shared = System.Xml.Linq.XDocument.Load(st).Root!.Elements(ns + "si")
                        .Select(si => string.Concat(si.Descendants(ns + "t").Where(x => !x.Ancestors(ns + "rPh").Any()).Select(x => x.Value))).ToList();
            var entry = SheetEntry(zip, sheet) ?? throw new InvalidDataException($"The workbook has no sheet {sheet}.");
            using var stream = entry.Open();
            var rows = new List<List<string>>();
            foreach (var row in System.Xml.Linq.XDocument.Load(stream).Descendants(ns + "row"))
            {
                var cells = new List<string>();
                foreach (var c in row.Elements(ns + "c"))
                {
                    var col = 0;
                    foreach (var ch in ((string?)c.Attribute("r") ?? "").ToUpperInvariant().TakeWhile(ch => ch is >= 'A' and <= 'Z'))
                        col = Math.Min(MaxColumns + 1, col * 26 + (ch - 'A' + 1));
                    if (col > MaxColumns) continue; // past Excel's last column: not a real cell
                    while (col > 0 && cells.Count < col - 1) cells.Add("");
                    var value = c.Element(ns + "v")?.Value ?? string.Concat(c.Descendants(ns + "t").Where(x => !x.Ancestors(ns + "rPh").Any()).Select(x => x.Value));
                    if ((string?)c.Attribute("t") == "s")
                        value = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "";
                    if (col > 0 && cells.Count >= col) cells[col - 1] = value; // a repeated reference: the last one wins
                    else cells.Add(value);
                }
                rows.Add(cells);
            }
            return rows;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or NullReferenceException or NotSupportedException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} can't be read as an Excel workbook ({ex.Message}). Save it again from Excel as .xlsx, or as CSV.", ex);
        }
    }

    private const int MaxColumns = 16384;

    private static System.IO.Compression.ZipArchiveEntry? Entry(System.IO.Compression.ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => e.FullName.TrimStart('/').Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The worksheet part of the nth sheet in tab order (workbook.xml and its relationships), else sheetN.xml.</summary>
    private static System.IO.Compression.ZipArchiveEntry? SheetEntry(System.IO.Compression.ZipArchive zip, int sheet)
    {
        try
        {
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            System.Xml.Linq.XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            System.Xml.Linq.XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
            if (Entry(zip, "xl/workbook.xml") is { } wb && Entry(zip, "xl/_rels/workbook.xml.rels") is { } rels)
            {
                string? id;
                using (var st = wb.Open())
                    id = (string?)System.Xml.Linq.XDocument.Load(st).Descendants(ns + "sheet").Skip(sheet - 1).FirstOrDefault()?.Attribute(rel + "id");
                if (id is not null)
                {
                    string? target;
                    using (var st = rels.Open())
                        target = (string?)System.Xml.Linq.XDocument.Load(st).Root?.Elements(pkg + "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == id)?.Attribute("Target");
                    if (target is not null)
                    {
                        var name = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target;
                        if (Entry(zip, name.Replace("/./", "/")) is { } found) return found;
                    }
                }
            }
        }
        catch (System.Xml.XmlException) { /* fall back to the usual part name */ }
        return Entry(zip, $"xl/worksheets/sheet{sheet}.xml");
    }
}
