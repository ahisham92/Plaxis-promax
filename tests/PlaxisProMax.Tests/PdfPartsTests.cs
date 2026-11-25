using PlaxisProMax.Chat;
using PlaxisProMax.Tools;

/// <summary>Large PDFs: page count, text page by page, splitting by size and by pages, and the attachment note that lists the parts.</summary>
internal static class PdfPartsTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ppm-pdfparts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A long report: 230 pages is more than a chat takes whole, so it is split by pages.
            var longPdf = Path.Combine(dir, "Ground report.pdf");
            File.WriteAllBytes(longPdf, PdfFiles.Sample(230));
            check("pdf: the pages are counted", PdfFiles.PageCount(File.ReadAllBytes(longPdf)) == 230, "");
            var parts = PdfFiles.Split(longPdf, 18L * 1024 * 1024, Path.Combine(dir, "split"), maxPages: 100);
            check("pdf: 230 pages become three parts of at most 100 pages", parts.Count == 3 && parts[0].FirstPage == 1 && parts[0].LastPage == 100 && parts[2].LastPage == 230, string.Join(",", parts.Select(p => $"{p.FirstPage}-{p.LastPage}")));
            check("pdf: each part is a PDF with its own pages", parts.All(p => PdfFiles.PageCount(File.ReadAllBytes(p.File)) == p.LastPage - p.FirstPage + 1), "");
            check("pdf: parts are named by their pages", Path.GetFileName(parts[1].File) == "Ground report - part 2 (pages 101-200).pdf", Path.GetFileName(parts[1].File));

            var textFile = Path.Combine(dir, "text.txt");
            var text = PdfFiles.WriteText(longPdf, textFile);
            var written = File.ReadAllText(textFile);
            check("pdf: the text is written page by page", text.Error is null && text.Pages == 230 && written.Contains("=== Page 230 of 230 ===") && written.Contains("Sample page 17"), text.Error ?? written.Substring(0, Math.Min(300, written.Length)));

            // Splitting by size: a file of noisy pictures is cut into parts under the limit.
            var bigPdf = Path.Combine(dir, "Scans.pdf");
            File.WriteAllBytes(bigPdf, PdfFiles.Sample(12, noise: 420));
            var size = new FileInfo(bigPdf).Length;
            var bySize = PdfFiles.Split(bigPdf, 2L * 1024 * 1024, Path.Combine(dir, "split2"));
            check("pdf: a big file is cut into parts under the size limit", size > 4 * 1024 * 1024 && bySize.Count >= 3 && bySize.All(p => p.Bytes <= 2L * 1024 * 1024) && bySize[^1].LastPage == 12, $"{size} bytes, {bySize.Count} parts");

            // Attached to a chat: the note lists the parts and how to ask them; the same file again uses the same parts.
            var folder = Path.Combine(dir, "attachments");
            var note = Attachments.Note([longPdf], folder);
            check("attachments: a long PDF is split and the note lists its parts", note.Contains("split it into 3 parts") && note.Contains("Part 2: pages 101-200") && note.Contains("read_pdf_part") && note.Contains("=== Page N of M ==="), note);
            var partsFolder = Directory.GetDirectories(folder).Single();
            var stamp = File.GetLastWriteTimeUtc(Path.Combine(partsFolder, "parts.json"));
            var again = Attachments.Note([longPdf], folder);
            check("attachments: the same PDF again uses the parts it has", again.Contains("Part 3: pages 201-230") && Directory.GetDirectories(folder).Length == 1 && File.GetLastWriteTimeUtc(Path.Combine(partsFolder, "parts.json")) == stamp, again);

            var small = Path.Combine(dir, "Short.pdf");
            File.WriteAllBytes(small, PdfFiles.Sample(6));
            check("attachments: a short PDF is read whole", Attachments.Note([small], folder).Contains("PDF of 6 pages; read it with the Read tool"), Attachments.Note([small], folder));
            var locked = Path.Combine(dir, "Locked.pdf");
            File.WriteAllBytes(locked, PdfAttachment.Sample(3, locked: true));
            check("attachments: a locked PDF says so", Attachments.Note([locked], folder).Contains("password"), Attachments.Note([locked], folder));

            // read_pdf_part: its checks before any helper starts.
            var tool = new PlaxisProMax.Mcp.ReadPdfPartTool();
            check("read_pdf_part is a reading tool with a plain name", !tool.ModifiesModel && StepNames.ReadOnly("read_pdf_part") && StepNames.Title("read_pdf_part") == "Read a part of a large PDF", "");
            var missing = PlaxisProMax.Mcp.PdfPartReader.CallAsync(new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["file"] = System.Text.Json.JsonDocument.Parse("\"C:\\\\nowhere\\\\part.pdf\"").RootElement.Clone(),
                ["question"] = System.Text.Json.JsonDocument.Parse("\"cu of the clay?\"").RootElement.Clone(),
            }, null, null, CancellationToken.None).GetAwaiter().GetResult();
            check("read_pdf_part: a part that isn't there says so", missing.IsError && missing.Text.Contains("was not found"), missing.Text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
