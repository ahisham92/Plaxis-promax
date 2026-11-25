using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PlaxisProMax.Chat;
using PlaxisProMax.Mcp;

namespace PlaxisProMax.Tools;

/// <summary>
/// The self-test (Sketch ProMax's, for Plaxis): checks this PC's set-up and calls every tool that only reads once on the
/// open project, and reports which steps fail. Nothing in the project changes: the tools that change it are listed as
/// not run. The files it makes go to a temporary folder that is deleted afterwards. The report is kept in
/// Documents\Plaxis ProMax\Self-test, with the steps to try by hand on real Plaxis.
/// </summary>
internal static class SelfTest
{
    public sealed record Outcome(string Step, string Status, long Ms, string Message);

    public sealed class Report
    {
        public DateTime Ran { get; init; } = DateTime.Now;
        public string Version { get; init; } = "";
        public string Project { get; set; } = "";
        public List<Outcome> Results { get; } = [];
        public int Passed => Results.Count(r => r.Status == "pass");
        public int Failed => Results.Count(r => r.Status == "fail");
        public int Skipped => Results.Count(r => r.Status == "skip");
        public string? File { get; set; }

        public string Summary()
        {
            var sb = new StringBuilder($"{Passed} passed, {Failed} failed, {Skipped} skipped (of {Results.Count} steps).");
            foreach (var r in Results.Where(r => r.Status == "fail")) sb.Append($"\n• {r.Step}: {Short(r.Message, 220)}");
            return sb.ToString();
        }
    }

    private sealed class Skip(string reason) : Exception(reason);

    /// <summary>The last report of this session (null before one ran).</summary>
    public static Report? Last { get; private set; }

    /// <summary>Where the reports are kept.</summary>
    public static string Folder => Path.Combine(ChatMcp.OutputFolder, "Self-test");

    /// <summary>The steps to try by hand on real Plaxis, which no self-test can do for the user.</summary>
    public static readonly string[] ByHand =
    [
        "Start Plaxis ProMax from Plaxis (Expert > Python > Run script > \"Plaxis ProMax.py\"): the window opens and its header names the project.",
        "Ask \"What is in this project?\": the answer names the units, layers, structures and phases of the project that is open.",
        "Attach a borehole log (PDF or Excel) and ask for the soil model: it lists the layers it read, asks before it builds, and the boreholes appear in Plaxis.",
        "Attach a DWG from AutoCAD and ask what is on its layers: the layer names and a known dimension come back right.",
        "Attach a report of more than 100 pages or 18 MB and ask for a value from it: the chat says it read it in parts and gives the page.",
        "Ask for a snapshot, change a material parameter in Plaxis by hand, then ask what changed: the parameter is listed with before and after.",
        "Ask for a change (a new phase): a brief card shows first; Run this makes the change and Plaxis shows the phase.",
        "Decline an approval card once: nothing changes in Plaxis and the chat asks what you want instead.",
        "Ask for two separate things at once in a chat With coordinator: two job cards show, and their steps in Plaxis take turns.",
        "Calculate a small project one phase at a time: each phase's result is reported and the project is saved between phases.",
        "Ask for results into Excel and a Word report: both files open from their cards in the chat.",
        "Switch the Phone on and send a message from the phone: it reaches this project's chat, and the reply reaches the phone.",
        "Press Shrink chat in a long chat, then ask a follow-up: the chat still knows what was done.",
        "Close the window while a chat is open and start again: the chat is in the history and carries on.",
    ];

    public static async Task<Report> RunAsync(ToolContext context, ToolRegistry registry, string version, CancellationToken ct, Action<string>? progress = null)
    {
        var report = new Report { Version = version };
        var temp = Path.Combine(Path.GetTempPath(), "ppm-selftest-" + Guid.NewGuid().ToString("N"));

        async Task Step(string name, Func<Task<string>> work)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(name);
            var clock = Stopwatch.StartNew();
            try
            {
                var said = await work().ConfigureAwait(false);
                report.Results.Add(new Outcome(name, "pass", clock.ElapsedMilliseconds, said));
            }
            catch (Skip s) { report.Results.Add(new Outcome(name, "skip", clock.ElapsedMilliseconds, s.Message)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { report.Results.Add(new Outcome(name, "fail", clock.ElapsedMilliseconds, ex.Message)); }
        }
        Task Check(string name, Func<string> work) => Step(name, () => Task.FromResult(work()));

        try
        {
            Directory.CreateDirectory(temp);

            // ---- This PC ------------------------------------------------------------------------------------------------
            await Check("License", () => Licensing.LicenseState.Allowed ? "This PC may run the tools." : throw new InvalidOperationException("This PC has no valid key: Settings > License."));
            await Check("Output folder", () =>
            {
                Directory.CreateDirectory(ChatMcp.OutputFolder);
                var probe = Path.Combine(ChatMcp.OutputFolder, ".selftest-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return ChatMcp.OutputFolder;
            });
            ClaudeCli.Install? claude = null;
            await Check("Claude Code is installed", () =>
            {
                claude = ClaudeCli.Find();
                return claude is null ? throw new InvalidOperationException("Claude Code wasn't found on this PC: the window's first card installs it.") : claude.Path;
            });
            await Check("Signed in to Claude", () =>
            {
                if (claude is null) throw new Skip("Claude Code isn't installed.");
                var (state, method) = ClaudeCli.CheckSignIn(claude);
                return state switch
                {
                    ClaudeCli.SignIn.SignedIn => method.Length > 0 ? "Signed in (" + method + ")." : "Signed in.",
                    ClaudeCli.SignIn.SignedOut => throw new InvalidOperationException("Not signed in: Settings > Claude > Sign in."),
                    _ => throw new Skip("Claude Code didn't say whether it is signed in."),
                };
            });
            await Check("Tools are registered", () =>
            {
                var all = registry.All.ToList();
                var bad = all.Where(t => !ValidJson("{" + "\"p\":" + (t.PropertiesJson.Trim().Length == 0 ? "{}" : t.PropertiesJson) + "}")).Select(t => t.Name).ToList();
                if (bad.Count > 0) throw new InvalidOperationException("These tools' inputs aren't valid: " + string.Join(", ", bad));
                return $"{all.Count} tools, {all.Count(t => t.ModifiesModel)} of them change the project.";
            });
            await Check("Kept code", () => $"{context.Library.All.Count} kept in {context.Library.Folder}.");
            await Check("Phone", () => !Phone.PhoneLink.Connected ? throw new Skip("No phone is connected.") : Phone.PhoneLink.On ? "Connected and on." : "Connected, switched off.");

            // ---- Plaxis, reading only -----------------------------------------------------------------------------------
            var connected = context.Link.Connected;
            await Step("Plaxis is connected", async () =>
            {
                if (!connected) throw new InvalidOperationException("Plaxis isn't connected: start Plaxis ProMax from Plaxis (Expert > Python > Run script), or press Connect in the window.");
                var status = await Call(registry, context, "status", "{}", ct).ConfigureAwait(false);
                report.Project = Short(status, 300);
                return report.Project;
            });
            foreach (var tool in registry.All.OfType<WorkerTool>().Where(t => !t.ModifiesModel && t.Required.Length == 0 && t.Name != "status").OrderBy(t => t.Name))
                await Step("Reads: " + tool.Name, async () =>
                {
                    if (!connected) throw new Skip("Plaxis isn't connected.");
                    return Short(await Call(registry, context, tool.Name, "{}", ct).ConfigureAwait(false), 200);
                });
            await Step("Reads: find_kept_code", async () => Short(await Call(registry, context, "find_kept_code", """{"what":"results of a phase","touches":["g_o.getresults"]}""", ct).ConfigureAwait(false), 200));

            // ---- Files --------------------------------------------------------------------------------------------------
            string? excel = null, dxf = null;
            foreach (var format in new[] { "excel", "word", "pdf", "powerpoint", "csv" })
                await Check("Makes a file: " + format, () =>
                {
                    var args = Args($$"""{"format":"{{format}}","title":"Self-test","content":"# Self-test\n\nA line of text.\n\n| Layer | Top |\n|---|---|\n| Clay | -1.8 |"}""");
                    var made = MakeDocumentTool.Make(args, temp);
                    if (made.IsError) throw new InvalidOperationException(made.Text);
                    var file = FileIn(made.Text);
                    if (file is null || !File.Exists(file) || new FileInfo(file).Length == 0) throw new InvalidOperationException("No file was written: " + Short(made.Text, 200));
                    if (format == "excel") excel = file;
                    return Path.GetFileName(file) + ", " + new FileInfo(file).Length + " bytes";
                });
            await Check("Reads a file back", () =>
            {
                if (excel is null) throw new Skip("The Excel file wasn't made.");
                var read = ReadDocumentTextTool.Read(excel, 4000);
                return read.IsError || !read.Text.Contains("Clay", StringComparison.Ordinal) ? throw new InvalidOperationException("The text read back isn't what was written: " + Short(read.Text, 200)) : "The table was read back.";
            });
            await Check("Makes a drawing (DXF)", () =>
            {
                var made = MakeDrawingTool.Make(Args("""{"title":"Self-test","entities":[{"kind":"line","points":[[0,0],[10,0]],"layer":"WALL"},{"kind":"text","points":[[0,1]],"text":"Self-test","layer":"NOTES"}]}"""), temp);
                if (made.IsError) throw new InvalidOperationException(made.Text);
                dxf = FileIn(made.Text);
                return dxf is null || !File.Exists(dxf) ? throw new InvalidOperationException("No drawing was written.") : Path.GetFileName(dxf);
            });
            await Check("Reads a drawing back", () =>
            {
                if (dxf is null) throw new Skip("The drawing wasn't made.");
                var read = ReadDrawingFileTool.Read(Args(JsonSerializer.Serialize(new { path = dxf })));
                return read.IsError || !read.Text.Contains("WALL", StringComparison.Ordinal) ? throw new InvalidOperationException("The drawing read back has no WALL layer: " + Short(read.Text, 200)) : "Its layers were read back.";
            });

await Check("Makes and reads a drawing (DWG)", () =>
            {
                var made = MakeDrawingTool.Make(Args("""{"title":"Self-test","format":"dwg","entities":[{"kind":"line","points":[[0,0],[10,0]],"layer":"WALL"}]}"""), temp);
                if (made.IsError) throw new InvalidOperationException(made.Text);
                var dwg = FileIn(made.Text);
                if (dwg is null || !File.Exists(dwg)) throw new InvalidOperationException("No drawing was written.");
                var read = ReadDrawingFileTool.Read(Args(JsonSerializer.Serialize(new { path = dwg })));
                return read.IsError || !read.Text.Contains("WALL", StringComparison.Ordinal) ? throw new InvalidOperationException("The DWG read back has no WALL layer: " + Short(read.Text, 200)) : "Written and read back.";
            });
            await Check("Splits a long PDF", () =>
            {
                var pdf = Path.Combine(temp, "Long.pdf");
                File.WriteAllBytes(pdf, PdfFiles.Sample(130));
                var parts = PdfFiles.Split(pdf, 18L * 1024 * 1024, Path.Combine(temp, "parts"), maxPages: 100);
                return parts.Count == 2 && parts[1].LastPage == 130 ? "130 pages in 2 parts." : throw new InvalidOperationException($"130 pages gave {parts.Count} parts.");
            });

                        // ---- Not run ------------------------------------------------------------------------------------------------
            var changing = registry.All.Where(t => t.ModifiesModel).Select(t => t.Name).OrderBy(n => n).ToList();
            report.Results.Add(new Outcome("Tools that change the project", "skip", 0, "Not run, so your project stays as it is: " + string.Join(", ", changing) + ". Try them by hand with the list below."));
        }
        catch (OperationCanceledException) { report.Results.Add(new Outcome("Stopped", "skip", 0, "The self-test was stopped before the end.")); }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        report.File = Save(report);
        Last = report;
        ActivityLog.Note("self_test", $"{report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped");
        return report;
    }

    private static async Task<string> Call(ToolRegistry registry, ToolContext context, string name, string json, CancellationToken ct)
    {
        var tool = registry.Find(name) ?? throw new InvalidOperationException("There is no tool named " + name + ".");
        var result = await tool.ExecuteAsync(context, Args(json), ct).ConfigureAwait(false);
        return result.IsError ? throw new InvalidOperationException(Short(result.Text, 400)) : result.Text;
    }

    /// <summary>The report as Markdown: what ran, each step, and the steps to try by hand.</summary>
    public static string Render(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Plaxis ProMax self-test");
        sb.AppendLine();
        sb.AppendLine($"{report.Ran:yyyy-MM-dd HH:mm} · version {report.Version}");
        if (report.Project.Length > 0) sb.AppendLine().AppendLine("Project: " + report.Project);
        sb.AppendLine().AppendLine($"**{report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped** (of {report.Results.Count} steps). Nothing in the project was changed.");
        sb.AppendLine().AppendLine("| Step | Result | Time | What it said |").AppendLine("|---|---|---|---|");
        foreach (var r in report.Results)
            sb.AppendLine($"| {Cell(r.Step)} | {r.Status} | {r.Ms} ms | {Cell(Short(r.Message, 300))} |");
        sb.AppendLine().AppendLine("## To try by hand on real Plaxis").AppendLine();
        for (var i = 0; i < ByHand.Length; i++) sb.AppendLine($"{i + 1}. [ ] {ByHand[i]}");
        sb.AppendLine().AppendLine("If a step fails, Settings > Report bug sends this PC's details and this chat to the developer.");
        return sb.ToString();
    }

    private static string? Save(Report report)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, $"Self-test {report.Ran:yyyy-MM-dd HHmm}.md");
            File.WriteAllText(file, Render(report));
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Cell(string text) => text.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");

    internal static string Short(string text, int max)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return t.Length <= max ? t : t.Substring(0, max - 1) + "…";
    }

    private static bool ValidJson(string json)
    {
        try { using var _ = JsonDocument.Parse(json); return true; }
        catch (JsonException) { return false; }
    }

    /// <summary>The "file" a tool's result names (make_document and make_drawing say it as JSON).</summary>
    private static string? FileIn(string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result);
            foreach (var key in new[] { "file", "path" })
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static ToolArgs Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ToolArgs(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
    }
}

/// <summary>run_self_test: the self-test from the chat.</summary>
internal sealed class RunSelfTestTool : IPlaxisTool
{
    public const string ToolName = "run_self_test";

    /// <summary>The registry and version the test runs against (the program sets them at start).</summary>
    internal static ToolRegistry? Registry;
    internal static string Version = "";
    /// <summary>The context of the running window, so the menu's Run self-test checks the same link and kept code.</summary>
    internal static ToolContext? Context;

    public string Name => ToolName;
    public string Description =>
        "Checks Plaxis ProMax on this PC and reports what fails: the license, Claude Code and its sign-in, the connection to Plaxis, every tool " +
        "that only reads called once on the open project, and the files it can make (Excel, Word, PDF, PowerPoint, CSV, DXF) read back. " +
        "Nothing in the project changes; the tools that change it are listed as not run. Takes a minute or two. The report is kept in " +
        "Documents\\Plaxis ProMax\\Self-test with the steps to try by hand. Use it when the user asks whether everything works, or after an update.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public async Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var report = await SelfTest.RunAsync(context, Registry ?? ToolRegistry.CreateDefault(), Version, ct).ConfigureAwait(false);
        return ToolResult.Ok(new
        {
            passed = report.Passed, failed = report.Failed, skipped = report.Skipped,
            failures = report.Results.Where(r => r.Status == "fail").Select(r => new { step = r.Step, why = SelfTest.Short(r.Message, 300) }).ToList(),
            skipped_steps = report.Results.Where(r => r.Status == "skip").Select(r => new { step = r.Step, why = SelfTest.Short(r.Message, 200) }).ToList(),
            file = report.File,
            next = "Tell the user the counts and each failure with its one next step, in a few lines. The report file has the steps to try by hand on real Plaxis.",
        });
    }
}
