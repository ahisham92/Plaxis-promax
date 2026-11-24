using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PlaxisProMax.Chat;
using PlaxisProMax.Tools;

namespace PlaxisProMax.Bugs;

/// <summary>
/// "Export chats &amp; bugs": one zip in Documents\Plaxis ProMax\Bugs\Exports for learning from how Plaxis ProMax is used:
/// the chats of every project updated in the period (readable Markdown with tool calls and errors), every bug report zip
/// (sent or not), the activity log for the period and the versions. Sketch ProMax's export, without Revit. Doesn't change
/// the project.
/// </summary>
internal sealed class ExportChatsAndBugsTool : IPlaxisTool
{
    public string Name => "export_chats_and_bugs";
    public string Description =>
        "Export chats & bugs: write ONE zip to Documents\\Plaxis ProMax\\Bugs\\Exports with the chats of every Plaxis project " +
        "updated in the period (Markdown, tool calls and errors included), all bug report zips (sent and not sent), the " +
        "activity log for the period (every tool call and whether it worked), Bug log.md and the versions. For the user to " +
        "send to the developer so both the chats and the bugs can be learned from. Nothing is sent anywhere; doesn't change the project.";
    public string PropertiesJson => """
    {
      "period": {"type": "string", "enum": ["day", "week", "all"], "description": "Chats and activity of the last day, the last week (default) or everything."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct) =>
        Task.Run(() => ToolResult.Ok(ChatsAndBugsExport.Export(args.OptString("period") ?? "week", ct)), ct);
}

internal static class ChatsAndBugsExport
{
    private const long BugReportsCap = 40_000_000;

    public static string Folder => Path.Combine(BugLog.Root, "Exports");

    /// <summary>The start of the period, or null for everything.</summary>
    internal static DateTime? Since(string period, DateTime now) => period.Trim().ToLowerInvariant() switch
    {
        "day" => now.AddDays(-1),
        "week" => now.AddDays(-7),
        "all" => null,
        _ => throw new ArgumentException("period is day, week or all."),
    };

    private const int ChatFileCap = 400_000;

    public static object Export(string period, CancellationToken ct = default)
    {
        void Step(string text) => ct.ThrowIfCancellationRequested();
        var now = DateTime.Now;
        var since = Since(period, now);
        var files = new List<(string Name, byte[] Data)>();
        var notes = new List<string>();
        var utf8 = new UTF8Encoding(false);

        Step("chats");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chats = ChatStore.All()
            .Where(c => c.Items.Count > 0 && (since is null || c.Updated.ToLocalTime() >= since))
            .OrderByDescending(c => c.Updated).ToList();
        foreach (var conv in chats)
        {
            if (string.IsNullOrEmpty(conv.Title)) conv.Title = Path.GetFileNameWithoutExtension(conv.Key);
            var name = Safe(conv.Title.Length > 0 ? conv.Title : "chat");
            var unique = name;
            for (var i = 2; !used.Add(unique); i++) unique = $"{name} ({i})";
            files.Add(($"chats/{unique}.md", utf8.GetBytes(RenderChat(conv))));
        }

        Step("bug reports");
        long bugBytes = 0;
        var reports = BugLog.Reports().OrderByDescending(r => r.Created).ToList();
        var unsent = new HashSet<string>(BugLog.UnsentReports().Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        var bugCount = 0;
        foreach (var report in reports)
        {
            byte[] data;
            try { data = File.ReadAllBytes(report.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add($"{Path.GetFileName(report.Path)} could not be read: {ex.Message}"); continue; }
            if (bugBytes + data.Length > BugReportsCap) { notes.Add($"{Path.GetFileName(report.Path)} and older reports left out to keep the zip small; send them from Bugs\\Reports."); break; }
            bugBytes += data.Length;
            bugCount++;
            files.Add(($"bugs/{Path.GetFileName(report.Path)}", data));
        }
        BugLog.RewriteLocal();
        try { if (File.Exists(BugLog.LocalLog)) files.Add(("Bug log.md", File.ReadAllBytes(BugLog.LocalLog))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        Step("activity log");
        var (activityFiles, calls, failed) = Activity(since);
        files.AddRange(activityFiles);

        Step("versions");
        files.Add(("versions.md", utf8.GetBytes(BugReport.Versions())));

        var periodText = since is null ? "everything" : $"since {since:yyyy-MM-dd HH:mm}";
        var readme = new StringBuilder();
        readme.AppendLine($"# Plaxis ProMax chats and bugs, {now:yyyy-MM-dd HH:mm}");
        readme.AppendLine();
        readme.AppendLine($"- Period: {periodText} (local time)");
        readme.AppendLine($"- Plaxis ProMax {Update.Updater.CurrentText}");
        readme.AppendLine();
        readme.AppendLine("| Part | What it is |");
        readme.AppendLine("|---|---|");
        readme.AppendLine($"| chats/ | {chats.Count} chat(s) updated in the period, one per Plaxis project, oldest message first, tool calls and errors included |");
        readme.AppendLine($"| bugs/ | {bugCount} bug report zip(s), sent and not sent ({reports.Count(r => unsent.Contains(r.Path))} not in the published log yet); each has report.md, a screenshot, the last tool calls and the versions |");
        readme.AppendLine("| Bug log.md | The published bug log with this PC's reports not sent yet |");
        readme.AppendLine($"| activity/ | {calls} tool call(s) in the period, one JSON line each ({failed} did not work) |");
        readme.AppendLine("| versions.md | Plaxis ProMax, Plaxis, Windows and .NET versions |");
        if (notes.Count > 0)
        {
            readme.AppendLine();
            readme.AppendLine("## Notes");
            readme.AppendLine();
            foreach (var n in notes) readme.AppendLine("- " + n);
        }

        Step("writing the zip");
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, $"Chats and bugs {now:yyyy-MM-dd HHmm}.zip");
        if (File.Exists(file)) file = Path.Combine(Folder, $"Chats and bugs {now:yyyy-MM-dd HHmmss}.zip");
        var partial = file + ".partial";
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Add(string name, byte[] data)
                {
                    var entry = zip.CreateEntry(name, name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using var s = entry.Open();
                    s.Write(data, 0, data.Length);
                }
                Add("README.md", utf8.GetBytes(readme.ToString()));
                foreach (var (name, data) in files) Add(name, data);
            }
            File.Move(partial, file);
        }
        catch
        {
            try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        var size = new FileInfo(file).Length;
        return new
        {
            file, size = (size / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB", period = periodText,
            chats = chats.Count, bug_reports = bugCount, tool_calls = calls, failed_calls = failed, notes,
            next = "Tell the user the zip is saved (the folder opens with it selected) and that they can send it to the developer.",
        };
    }

    /// <summary>The activity-log lines in the period, one file per day.</summary>
    private static (List<(string, byte[])> Files, int Calls, int Failed) Activity(DateTime? since)
    {
        var files = new List<(string, byte[])>();
        int calls = 0, failed = 0;
        var folder = ActivityLog.Folder;
        if (!Directory.Exists(folder)) return (files, 0, 0);
        foreach (var day in Directory.GetFiles(folder, "*.jsonl").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            var stem = Path.GetFileNameWithoutExtension(day);
            if (since is { } s && DateTime.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < s.Date) continue;
            if (ActivityLog.ReadLines(day) is not { } lines) continue;
            var kept = lines.Where(l => since is null || Time(l) is not { } t || t >= since).ToList();
            if (kept.Count == 0) continue;
            calls += kept.Count;
            failed += kept.Count(l => l.Contains("\"ok\":false"));
            files.Add(($"activity/{stem}.jsonl", Encoding.UTF8.GetBytes(string.Join("\n", kept) + "\n")));
        }
        return (files, calls, failed);
    }

    private static DateTime? Time(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("time", out var t) && DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A file-name-safe version of a title.</summary>
    internal static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var safe = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length > 80) safe = safe.Substring(0, 80).Trim();
        return safe.Length == 0 ? "chat" : safe;
    }

    /// <summary>A saved chat as readable Markdown, tool calls and errors included; a long one keeps its end (Sketch ProMax's ProjectPack.RenderChat).</summary>
    internal static string RenderChat(Conversation conv)
    {
        var sb = new StringBuilder();
        sb.AppendLine(conv.Name.Length > 0 ? $"# Plaxis ProMax chat: {conv.Name} ({conv.Title})" : $"# Plaxis ProMax chat: {conv.Title}");
        if (conv.Updated != default) sb.AppendLine($"Last updated {conv.Updated.ToLocalTime():yyyy-MM-dd HH:mm}");
        foreach (var item in conv.Items)
        {
            sb.AppendLine();
            switch (item.Kind)
            {
                case "user":
                    sb.AppendLine("## User");
                    if (item.Name.Length > 0) sb.AppendLine($"(attached: {item.Name})");
                    sb.AppendLine(item.Text);
                    break;
                case "assistant":
                    sb.AppendLine("## Plaxis ProMax");
                    sb.AppendLine(item.Text);
                    break;
                case "tool":
                    sb.AppendLine($"- Tool {item.Name}: {(item.State.Length > 0 ? item.State : "run")}" + (item.Text.Length > 0 ? $" ({item.Text})" : ""));
                    break;
                default:
                    sb.AppendLine($"_{item.Kind}: {item.Text}_");
                    break;
            }
        }
        var text = sb.ToString();
        if (text.Length > ChatFileCap)
        {
            var tail = text.Substring(text.Length - ChatFileCap);
            var nl = tail.IndexOf('\n');
            text = "… (earlier part left out to keep the zip small)\n" + (nl >= 0 ? tail.Substring(nl + 1) : tail);
        }
        return text;
    }
}
