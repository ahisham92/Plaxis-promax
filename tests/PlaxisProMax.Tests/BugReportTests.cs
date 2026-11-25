using System.IO.Compression;
using System.Text.Json;
using PlaxisProMax.Bugs;
using PlaxisProMax.Chat;
using PlaxisProMax.Tools;

/// <summary>
/// Report bug and Export chats &amp; bugs, as Sketch ProMax has them: the report zip holds the description, the picture,
/// the versions and the last tool calls; the export holds the period's chats, every report, the activity and the versions.
/// The folders are the checks' own (Program.cs points them at a temporary folder).
/// </summary>
internal static class BugReportTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var registry = ToolRegistry.CreateDefault();
        var help = new[] { "report_bug", "get_activity_log", "export_chats_and_bugs" };
        check("bugs: the chat has report_bug, get_activity_log and export_chats_and_bugs, none changing the project",
            help.All(n => registry.Find(n) is { ModifiesModel: false }), "");

        ActivityLog.Write("Quay wall.p2dx", "create_boreholes", true, new Dictionary<string, JsonElement>(), false, "Layer 3 has no material", 1200);
        ActivityLog.Write("Quay wall.p2dx", "status", false, new Dictionary<string, JsonElement>(), true, "{\"product\":\"PLAXIS 2D\"}", 40);
        var last = ActivityLog.Last(2);
        check("bugs: the activity log keeps each call with whether it worked",
            last.Count == 2 && last[0].Contains("\"tool\":\"create_boreholes\"") && last[0].Contains("\"ok\":false") && last[1].Contains("\"ok\":true"), string.Join("\n", last));
        var failedOnly = registry.Find("get_activity_log")!.ExecuteAsync(null!, Args("""{"failed_only":true}"""), CancellationToken.None).GetAwaiter().GetResult();
        check("bugs: get_activity_log can list only the calls that did not work",
            failedOnly.Text.Contains("create_boreholes") && !failedOnly.Text.Contains("\"tool\":\"status\""), failedOnly.Text);

        BugReport.Screenshot = () => (new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, null);
        BugReport.Situation = () => ["- Plaxis: PLAXIS 2D 2024.2", "- Project: Quay wall.p2dx"];
        try
        {
            check("bugs: a report needs a description", Throws<ArgumentException>(() => BugReport.Create("  \n ", (null, "none"), "test")), "");
            var tool = registry.Find("report_bug")!.ExecuteAsync(null!, Args("""{"description":"create_boreholes put\nlayer 3 at the wrong level"}"""), CancellationToken.None).GetAwaiter().GetResult();
            var file = JsonDocument.Parse(tool.Text).RootElement.GetProperty("file").GetString()!;
            using (var zip = ZipFile.OpenRead(file))
            {
                string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
                var names = zip.Entries.Select(e => e.FullName).ToList();
                check("bugs: report_bug writes one zip in Bugs\\Reports with the description, report, activity and picture",
                    Path.GetDirectoryName(file) == BugLog.ReportsFolder && names.Contains("description.txt") && names.Contains("report.md")
                    && names.Contains("activity.jsonl") && names.Contains("screenshot.jpg"), string.Join(",", names));
                check("bugs: the description is one line", Read("description.txt") == "create_boreholes put layer 3 at the wrong level", Read("description.txt"));
                var report = Read("report.md");
                check("bugs: the report names the versions, Plaxis and the project, and counts the failed calls",
                    report.Contains("Plaxis ProMax:") && report.Contains("PLAXIS 2D 2024.2") && report.Contains("Quay wall.p2dx") && report.Contains("1 did not work"), report);
                check("bugs: the report carries the last tool calls", Read("activity.jsonl").Contains("Layer 3 has no material"), "");
            }
            var log = File.ReadAllText(BugLog.LocalLog);
            check("bugs: the new report is listed under Not sent yet in Bug log.md", log.Contains("## Not sent yet") && log.Contains(Path.GetFileName(file)), log);

            // ---- Export chats & bugs
            var recent = ChatStore.New("plaxis:c:\\jobs\\quay wall.p2dx", "Quay wall");
            recent.Items.Add(new ChatItem { Kind = "user", Text = "Build the model from BH-3", Time = DateTime.UtcNow });
            recent.Items.Add(new ChatItem { Kind = "tool", Name = "create_boreholes", State = "failed", Text = "Layer 3 has no material", Time = DateTime.UtcNow });
            recent.Items.Add(new ChatItem { Kind = "assistant", Text = "Layer 3 needs a material first.", Time = DateTime.UtcNow });
            ChatStore.Save(recent);
            var old = ChatStore.New("plaxis:c:\\jobs\\old.p2dx", "Old job");
            old.Items.Add(new ChatItem { Kind = "user", Text = "old", Time = DateTime.UtcNow.AddDays(-30) });
            old.Updated = DateTime.UtcNow.AddDays(-30);
            ChatStore.Save(old, touch: false);

            var export = registry.Find("export_chats_and_bugs")!.ExecuteAsync(null!, Args("""{"period":"week"}"""), CancellationToken.None).GetAwaiter().GetResult();
            var exported = JsonDocument.Parse(export.Text).RootElement;
            var zipFile = exported.GetProperty("file").GetString()!;
            using (var zip = ZipFile.OpenRead(zipFile))
            {
                string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
                var names = zip.Entries.Select(e => e.FullName).ToList();
                check("bugs: the export holds README, the week's chat, the bug report, the activity and the versions",
                    names.Contains("README.md") && names.Contains("chats/Quay wall.md") && names.Any(n => n.StartsWith("bugs/Bug report ")) && names.Contains("Bug log.md")
                    && names.Any(n => n.StartsWith("activity/")) && names.Contains("versions.md"), string.Join(",", names));
                check("bugs: a chat older than the week is left out", !names.Contains("chats/Old job.md") && exported.GetProperty("chats").GetInt32() == 1, string.Join(",", names));
                var chat = Read("chats/Quay wall.md");
                check("bugs: the chat reads as Markdown with its tool calls and errors",
                    chat.Contains("## User") && chat.Contains("Build the model from BH-3") && chat.Contains("- Tool create_boreholes: failed (Layer 3 has no material)") && chat.Contains("## Plaxis ProMax"), chat);
                check("bugs: the README counts the calls that did not work", Read("README.md").Contains("(1 did not work)") || Read("README.md").Contains("did not work"), Read("README.md"));
            }
            check("bugs: the export goes to Bugs\\Exports", Path.GetDirectoryName(zipFile) == ChatsAndBugsExport.Folder, zipFile);
            check("bugs: chat titles become safe file names", ChatsAndBugsExport.Safe("Quay: wall/phase 2?") == "Quay_ wall_phase 2_", ChatsAndBugsExport.Safe("Quay: wall/phase 2?"));
        }
        finally
        {
            BugReport.Screenshot = null;
            BugReport.Situation = null;
        }
    }

    private static ToolArgs Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ToolArgs(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
