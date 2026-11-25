using System.Text.Json;
using PlaxisProMax.Tools;


/// <summary>DXF drawings in and out (read_drawing_file, make_drawing) and CSV tables, with no CAD program.</summary>
internal static class DrawingTests
{
    private static ToolArgs Args(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!);

    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ppm-dxf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var made = MakeDrawingTool.Make(Args("""
                {"title": "Section A: wall", "entities": [
                  {"kind": "polyline", "layer": "SOIL", "points": [[0,0],[40,0],[40,-20],[0,-20]], "closed": true},
                  {"kind": "line", "layer": "WALL", "points": [[20,2.5],[20,-14]]},
                  {"kind": "circle", "layer": "ANCHOR", "points": [[20,-2]], "radius": 0.3},
                  {"kind": "text", "layer": "NOTES", "points": [[21,-14]], "text": "Toe -14.0", "height": 0.5},
                  {"kind": "point", "layer": "RESULTS", "points": [[20,-6,1.5]]}
                ]}
                """), dir);
            var file = Directory.GetFiles(dir, "*.dxf").FirstOrDefault() ?? "";
            check("make_drawing: writes a DXF named after the title", !made.IsError && Path.GetFileName(file) == "Section A wall.dxf", made.Text);
            var again = MakeDrawingTool.Make(Args("""{"title": "Section A: wall", "entities": [{"kind": "line", "points": [[0,0],[1,1]]}]}"""), dir);
            check("make_drawing: never replaces a file", !again.IsError && Directory.GetFiles(dir, "*.dxf").Length == 2, again.Text);

            var layers = ReadDrawingFileTool.Read(Args(JsonSerializer.Serialize(new { path = file })));
            check("read_drawing_file: the layers with what is on them, and the extents",
                !layers.IsError && layers.Text.Contains("\"SOIL\"") && layers.Text.Contains("\"WALL\"") && layers.Text.Contains("\"polyline\":1") && layers.Text.Contains("\"min\":[0,-20]") && layers.Text.Contains("\"max\":[40,2.5]"), layers.Text);
            var soil = ReadDrawingFileTool.Read(Args(JsonSerializer.Serialize(new { path = file, layer = "soil" })));
            check("read_drawing_file: a layer's polyline comes back closed with its corners",
                !soil.IsError && soil.Text.Contains("\"closed\":true") && soil.Text.Contains("[40,-20]") && soil.Text.Contains("\"count\":1"), soil.Text);
            var notes = ReadDrawingFileTool.Read(Args(JsonSerializer.Serialize(new { path = file, layer = "NOTES" })));
            var point = ReadDrawingFileTool.Read(Args(JsonSerializer.Serialize(new { path = file, layer = "RESULTS" })));
            check("read_drawing_file: texts and 3D points read back", notes.Text.Contains("Toe -14.0") && point.Text.Contains("[20,-6,1.5]"), notes.Text + point.Text);

            // What AutoCAD writes: LWPOLYLINE with the vertices in one record, and MTEXT.
            var lw = "0\nSECTION\n2\nENTITIES\n0\nLWPOLYLINE\n8\nEXC\n90\n3\n70\n0\n10\n0\n20\n0\n10\n5000\n20\n0\n10\n5000\n20\n-2000\n0\nMTEXT\n8\nEXC\n10\n1\n20\n2\n1\nStage 1\\Pto -2 m\n0\nARC\n8\nEXC\n10\n0\n20\n0\n40\n3\n50\n0\n51\n90\n0\nENDSEC\n0\nEOF\n";
            var cad = Path.Combine(dir, "cad.dxf");
            File.WriteAllText(cad, lw.Replace("\n", "\r\n"));
            var exc = ReadDrawingFileTool.Read(Args(JsonSerializer.Serialize(new { path = cad, layer = "EXC" })));
            check("read_drawing_file: AutoCAD's light polylines, multi-line text and arcs",
                !exc.IsError && exc.Text.Contains("[5000,-2000]") && exc.Text.Contains("Stage 1 to -2 m") && exc.Text.Contains("\"angles\":[0,90]"), exc.Text);
            var only = ReadDrawingFileTool.Read(Args(JsonSerializer.Serialize(new { path = cad, layer = "EXC", kinds = new[] { "arc" } })));
            check("read_drawing_file: only the kinds asked for", only.Text.Contains("\"count\":1"), only.Text);

            var dwg = Path.Combine(dir, "plan.dwg");
            File.WriteAllBytes(dwg, [0x41, 0x43, 0x31, 0x30]);
            var refused = ReadDrawingFileTool.Read(Args(JsonSerializer.Serialize(new { path = dwg })));
            check("read_drawing_file: a DWG is refused with what to do", refused.IsError && refused.Text.Contains("save it as DXF"), refused.Text);
            var junk = Path.Combine(dir, "junk.dxf");
            File.WriteAllText(junk, "AutoCAD Binary DXF\r\n\u001a\0");
            check("read_drawing_file: a file that is not a text DXF says so", ReadDrawingFileTool.Read(Args(JsonSerializer.Serialize(new { path = junk }))).IsError, "");
            check("make_drawing: a line with one point is refused", MakeDrawingTool.Make(Args("""{"title": "x", "entities": [{"kind": "line", "points": [[0,0]]}]}"""), dir).IsError, "");

            var csv = MakeDocumentTool.Make(Args("""
                {"format": "csv", "title": "Wall moments", "sheets": [{"columns": ["Phase", "M max (kNm/m)", "Note"],
                 "rows": [["Excavate to -2 m", 85.4, "ok, small"], ["Excavate to -6 m", 412.75, "@check"]]}]}
                """), dir);
            var csvFile = Path.Combine(dir, "Wall moments.csv");
            var text = File.Exists(csvFile) ? File.ReadAllText(csvFile) : "";
            check("make_document: a CSV table with quoted commas and no live formulas",
                !csv.IsError && text.Contains("Phase,M max (kNm/m),Note") && text.Contains("Excavate to -2 m,85.4,\"ok, small\"") && text.Contains("412.75,'@check"), csv.Text + text);
            var word = MakeDocumentTool.Make(Args("""{"format": "word", "title": "GIR summary", "content": "# Ground\n\nSoft clay to -7.5 m, cu = 25 kPa.\n\n| Layer | Top |\n|---|---|\n| Clay | -1.8 |"}"""), dir);
            var docx = Path.Combine(dir, "GIR summary.docx");
            var note = PlaxisProMax.Chat.Attachments.Note([docx, file, dwg, Path.Combine(dir, "gone.pdf"), csvFile], Path.Combine(dir, "read"));
            check("attachments: a Word file's text comes with the message, a DXF and a DWG say how to read them",
                !word.IsError && note.Contains("Word document; its text is below") && note.Contains("cu = 25 kPa") && note.Contains("read_drawing_file")
                && note.Contains("save it as DXF") && note.Contains("not found on this PC") && note.Contains("Wall moments.csv (text; read it with the Read tool)"), note);
            var longDoc = MakeDocumentTool.Make(Args(JsonSerializer.Serialize(new { format = "word", title = "Long report", content = string.Join("\n\n", Enumerable.Range(1, 900).Select(i => $"Paragraph {i} about the ground at borehole {i}.")) })), dir);
            var longNote = PlaxisProMax.Chat.Attachments.Note([Path.Combine(dir, "Long report.docx")], Path.Combine(dir, "read"));
            check("attachments: a long document's text goes to a file the note names",
                !longDoc.IsError && longNote.Contains("its text is long") && Directory.GetFiles(Path.Combine(dir, "read"), "Long report*text.txt").Length == 1 && !longNote.Contains("Paragraph 500"), longNote.Substring(0, Math.Min(400, longNote.Length)));
            check("chat files: a DXF and a CSV show as cards", PlaxisProMax.Chat.ChatFiles.Find(made.Text).Count == 1 && PlaxisProMax.Chat.ChatFiles.Find(csv.Text).Count == 1, "");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
