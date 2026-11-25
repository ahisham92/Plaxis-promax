using System.IO.Compression;
using PlaxisProMax.Bugs;

/// <summary>The bug log's "Not sent yet" rule (as in Sketch ProMax).</summary>
internal static class BugLogTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- Bug log
        const string published = """
            # Plaxis ProMax bug log

            Intro mentioning 2030-01-01 outside the table.

            | # | Date | What happens | Version | Status |
            |---|---|---|---|---|
            | 1 | 2026-10-16 | Something | 0.29.0 | Fixed in 0.30 (please check) |
            | 2 | 2026-10-17 | Another, report 2026-10-17 0900 | 0.28.1 | Open |
            """;
        var reports = new[]
        {
            new BugLog.Report("a.zip", "2026-10-16 1000", new DateTime(2026, 10, 16, 10, 0, 0), "older than the log"),
            new BugLog.Report("b.zip", "2026-10-17 0900", new DateTime(2026, 10, 17, 9, 0, 0), "named in the log"),
            new BugLog.Report("c.zip", "2026-10-17 1500", new DateTime(2026, 10, 17, 15, 0, 0), "same day, not named"),
            new BugLog.Report("d.zip", "2026-10-18 0800", new DateTime(2026, 10, 18, 8, 0, 0), "newer"),
        };
        var unsent = BugLog.Unsent(published, reports);
        check("bug log: not sent = not named in a row (older ones too), newest first",
            unsent.Select(r => r.Id).SequenceEqual(["2026-10-18 0800", "2026-10-17 1500", "2026-10-16 1000"]), string.Join(", ", unsent.Select(r => r.Id)));
        check("bug log: an id outside the table doesn't count as sent",
            BugLog.Unsent("# log\n\nSee 2026-10-16 1000.\n\n| # | x |\n|---|---|\n", reports.Take(1)).Count == 1, "");
        check("bug log: nothing downloaded yet lists every report", BugLog.Unsent(null, reports).Count == 4, "");
        var local = BugLog.Compose(published, unsent, new DateTime(2026, 10, 18, 9, 30, 0));
        check("bug log: local file keeps the published log and adds Not sent yet",
            local.StartsWith(BugLog.RewrittenNote) && local.Contains("# Plaxis ProMax bug log") && local.Contains("## Not sent yet") && local.Contains("| c.zip | same day, not named |")
            && local.IndexOf("d.zip", StringComparison.Ordinal) < local.IndexOf("c.zip", StringComparison.Ordinal), local);
        check("bug log: empty list says how to report", BugLog.Compose(null, [], null).Contains("Report bug"), "");
        check("bug log: error pages are not taken for the log", !BugLog.LooksLikeLog("<html>404</html>") && BugLog.LooksLikeLog(published.Trim()), "");
        var (name, id) = BugLog.NameFor(new DateTime(2026, 10, 18, 8, 5, 9));
        check("bug log: report names carry their time", name == "Bug report 2026-10-18 0805.zip" && id == "2026-10-18 0805", name);

        // Report zips are read back with their description and time.
        var dir = Path.Combine(Path.GetTempPath(), "ppm-bugtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var zip = ZipFile.Open(Path.Combine(dir, name), ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("description.txt").Open())) w.Write("Tags overlap | badly");
            File.WriteAllText(Path.Combine(dir, "Bug report broken.zip"), "not a zip");
            var read = BugLog.Reports(dir).OrderBy(r => r.Id).ToList();
            check("bug log: reports read from zips", read.Count == 2 && read.Any(r => r.Id == id && r.Description == "Tags overlap | badly" && r.Created == new DateTime(2026, 10, 18, 8, 5, 0))
                && read.Any(r => r.Description == "(could not be read)"), string.Join("; ", read));
            check("bug log: pipes in descriptions don't break the table", BugLog.Compose(null, read, null).Contains("Tags overlap / badly"), "");
        }
        finally { Directory.Delete(dir, true); }

    }
}
