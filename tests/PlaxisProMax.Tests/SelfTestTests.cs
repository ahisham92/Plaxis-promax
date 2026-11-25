using PlaxisProMax.Bugs;
using PlaxisProMax.Plaxis;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

/// <summary>The self-test against the stand-in Plaxis, the crash guard's files, and the Open folders list.</summary>
internal static class SelfTestTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ppm-selftest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        BugLog.RootOverride = Path.Combine(temp, "Bugs");
        try
        {
            using var link = new PlaxisLink(TestPlaxis.WorkerStart);
            var library = new CodeLibrary(Path.Combine(temp, "kept"));
            var settings = new AgentSettings();
            var registry = PlaxisProMax.Tools.ToolRegistry.CreateDefault();
            var context = new ToolContext(link, library, () => settings);

            // Not connected: the reading steps are skipped, the file steps still run, nothing throws.
            var offline = SelfTest.RunAsync(context, registry, "9.9.9", CancellationToken.None).GetAwaiter().GetResult();
            check("self-test offline: Plaxis step fails with what to do", offline.Results.Any(r => r.Step == "Plaxis is connected" && r.Status == "fail" && r.Message.Contains("Expert")), Dump(offline));
            check("self-test offline: reading steps are skipped", offline.Results.Where(r => r.Step.StartsWith("Reads: ") && r.Step != "Reads: find_kept_code").All(r => r.Status == "skip"), Dump(offline));
            foreach (var format in new[] { "excel", "word", "pdf", "powerpoint", "csv" })
                check("self-test makes " + format, offline.Results.Any(r => r.Step == "Makes a file: " + format && r.Status == "pass"), Dump(offline));
            check("self-test reads the Excel back", offline.Results.Any(r => r.Step == "Reads a file back" && r.Status == "pass"), Dump(offline));
            check("self-test makes and reads a DXF", offline.Results.Count(r => r.Step.Contains("drawing") && r.Status == "pass") == 2, Dump(offline));
            check("self-test lists the changing tools as not run", offline.Results.Any(r => r.Step == "Tools that change the project" && r.Status == "skip" && r.Message.Contains("run_plaxis_code")), Dump(offline));
            check("self-test keeps the last report", ReferenceEquals(SelfTest.Last, offline), "");

            var text = SelfTest.Render(offline);
            check("self-test report has the counts", text.Contains($"{offline.Passed} passed, {offline.Failed} failed"), text);
            check("self-test report lists the steps by hand", text.Contains("To try by hand on real Plaxis") && text.Contains("1. [ ]"), text);
            check("self-test report names the version", text.Contains("version 9.9.9"), text);

            // The tool is registered, reads only, and has a plain name.
            var tool = registry.Find(RunSelfTestTool.ToolName);
            check("run_self_test is registered and changes nothing", tool is { ModifiesModel: false }, "");
            check("run_self_test has a plain step name", PlaxisProMax.Chat.StepNames.Title(RunSelfTestTool.ToolName) != RunSelfTestTool.ToolName, PlaxisProMax.Chat.StepNames.Title(RunSelfTestTool.ToolName));

            // Crash guard: a window error is written, handled, and told once; out of memory is not swallowed.
            var said = new List<string>();
            Action<string> hear = said.Add;
            CrashGuard.Caught += hear;
            try
            {
                check("crash guard handles a window error", CrashGuard.OnUi(new InvalidOperationException("bad click")), "");
                check("crash guard tells the window", said.Count == 1 && said[0].Contains("bad click") && said[0].Contains("carried on"), string.Join("|", said));
                check("crash guard writes the error", Directory.Exists(CrashGuard.Folder) && Directory.GetFiles(CrashGuard.Folder, "error-*.txt").Any(f => File.ReadAllText(f).Contains("bad click")), CrashGuard.Folder);
                check("crash guard doesn't repeat within seconds", CrashGuard.OnUi(new InvalidOperationException("again")) && said.Count == 1, string.Join("|", said));
                check("crash guard lets out of memory through", !CrashGuard.OnUi(new OutOfMemoryException()), "");
                check("no crash last time: nothing to say", CrashGuard.TakeLastCrash() == "", "");
            }
            finally { CrashGuard.Caught -= hear; }

            // Open folders: every folder has a title, a tip and a full path, and the titles are distinct.
            var folders = UserFolders.List(library);
            check("folders: bugs, kept code, chats and your files are listed", new[] { "Your files", "Bugs", "Kept code", "Chats", "Notes", "Settings" }.All(t => folders.Any(f => f.Title == t)), string.Join(",", folders.Select(f => f.Title)));
            check("folders: each has a rooted path and a tip", folders.All(f => (Path.IsPathRooted(f.Path) || !OperatingSystem.IsWindows()) && f.Tip.Length > 10), string.Join(",", folders.Select(f => f.Path)));
            check("folders: titles are distinct", folders.Select(f => f.Title).Distinct().Count() == folders.Count, "");
            check("folders: kept code is the library's own folder", folders.First(f => f.Title == "Kept code").Path == library.Folder, "");
            check("folders: bugs is the bug log's folder", folders.First(f => f.Title == "Bugs").Path == BugLog.Root, "");
        }
        finally
        {
            BugLog.RootOverride = null;
            try { Directory.Delete(temp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Dump(SelfTest.Report report) => string.Join("; ", report.Results.Select(r => r.Step + "=" + r.Status + ":" + SelfTest.Short(r.Message, 80)));
}
