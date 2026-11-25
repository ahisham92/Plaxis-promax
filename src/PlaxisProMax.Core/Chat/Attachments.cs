using System.Text;

namespace PlaxisProMax.Chat;

/// <summary>
/// What the chat is told about the files attached to a message (Sketch ProMax's BuildMessage, without Revit): each
/// file's full path with what it is and how to read it, and the text of Word, PowerPoint and Excel files, which
/// Claude Code's Read tool can't open: short text goes into the message, long text into a file beside the attachments
/// that the note names. Pictures and PDFs are read with the Read tool from their path. No WPF, so the tests run it.
/// </summary>
internal static class Attachments
{
    /// <summary>Office text up to this length goes into the message itself.</summary>
    public const int MaxInlineText = 20_000;

    private static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    public static bool IsPicture(string path) => Pictures.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The text added under the user's message for these files ("" for none). Text files it writes go into <paramref name="folder"/>.</summary>
    public static string Note(IReadOnlyList<string> files, string folder)
    {
        if (files.Count == 0) return "";
        var notes = new StringBuilder();
        var contents = new StringBuilder();
        foreach (var path in files)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            string kind;
            if (!File.Exists(path)) kind = "not found on this PC any more";
            else if (OfficeText.IsOffice(path)) kind = Office(path, folder, contents);
            else kind = ext switch
            {
                ".pdf" => Pdf(path),
                ".dxf" => "CAD drawing (DXF): read its layers and geometry with read_drawing_file",
                ".dwg" => "CAD drawing (DWG), which can't be read as it is: ask the user to save it as DXF (AutoCAD: Save As > DXF) and attach that, or bring it into Plaxis with Plaxis's own CAD import",
                ".doc" or ".ppt" => "older Word or PowerPoint file, which can't be read here. Ask the user to save it as .docx or .pptx (File > Save As) and attach that",
                ".xls" or ".xlsb" => "older or binary Excel file, which can't be read here. Ask the user to save it as .xlsx (File > Save As) and attach that",
                ".csv" or ".txt" or ".md" or ".log" or ".json" or ".xml" or ".ags" => "text; read it with the Read tool",
                ".p2dx" or ".p3dx" or ".p2d" or ".p3d" => "Plaxis project; open it with open_project if the user asks for that",
                _ when IsPicture(path) => "picture; look at it with the Read tool",
                _ => "file; read it with the Read tool if it is text",
            };
            notes.Append("- ").Append(path).Append(" (").Append(kind).Append(")\n");
        }
        return "\n\nAttached files (full paths on this PC):\n" + notes.ToString().TrimEnd() + contents.ToString().TrimEnd();
    }

    private static string Pdf(string path)
    {
        try
        {
            var check = PdfAttachment.Assess(Tools.SharedRead.AllBytes(path));
            var pages = check.Pages > 0 ? $" of {check.Pages} page{(check.Pages == 1 ? "" : "s")}" : "";
            return check.Verdict == PdfAttachment.Verdict.NotPdf
                ? "named .pdf but not a PDF file: ask the user to export it again"
                : $"PDF{pages}; read it with the Read tool, a range of pages at a time when it is long (pages: \"1-20\")";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "PDF that couldn't be opened: " + ex.Message; }
    }

    /// <summary>A Word, PowerPoint or Excel file: its text in the message, or in a file when long; its pictures as files to look at.</summary>
    private static string Office(string path, string folder, StringBuilder contents)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var workbook = OfficeText.IsWorkbook(path);
        var what = ext == ".docx" ? "Word document" : workbook ? "Excel workbook" : "PowerPoint file";
        var name = Path.GetFileName(path);
        OfficeText.Content content;
        try { content = OfficeText.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return $"{what} that couldn't be read ({ex.Message}). Ask the user to save it again as {(workbook ? ".xlsx" : ".docx or .pptx")}, or to export it to PDF and attach that";
        }
        string where;
        if (content.Text.Length == 0) where = "it has no text";
        else if (content.Text.Length <= MaxInlineText)
        {
            contents.Append($"\n\nText of {name}:\n<file name=\"{name}\">\n{content.Text}\n</file>");
            where = "its text is below";
        }
        else
        {
            var target = ReadFileName(path, folder, "text.txt");
            File.WriteAllText(target, content.Text, new UTF8Encoding(false));
            where = $"its text is long, so it is in {target}: search it with Grep and read the parts you need with the Read tool";
        }
        var saved = new List<string>();
        foreach (var (pictureName, bytes) in content.Pictures.Take(8))
        {
            try
            {
                var target = ReadFileName(path, folder, pictureName);
                File.WriteAllBytes(target, bytes);
                saved.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        var pictures = saved.Count == 0 ? "" : $"; its picture{(saved.Count == 1 ? " is" : "s are")} saved to look at with the Read tool: {string.Join(", ", saved)}";
        var sheets = workbook ? "; each sheet is a table headed by its column letters, each row led by its row number, so name cells like B12. " +
                                "Values are what Excel last showed (formula results)" : "";
        return $"{what}; {where}{pictures}{sheets}";
    }

    /// <summary>A file in <paramref name="folder"/> for what was read from <paramref name="source"/>: "name stamp - text.txt".</summary>
    private static string ReadFileName(string source, string folder, string what)
    {
        Directory.CreateDirectory(folder);
        var stem = Tools.OfficeDocs.SafeName(Path.GetFileNameWithoutExtension(source), "file");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return Tools.OfficeDocs.FreePath(folder, $"{stem} {stamp} - {Path.GetFileNameWithoutExtension(what)}", Path.GetExtension(what));
    }
}
