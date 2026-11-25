using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using PlaxisProMax.Tools;

namespace PlaxisProMax.Bugs;

/// <summary>
/// Writes one small bug report zip to Documents\Plaxis ProMax\Bugs\Reports: the user's one-line description, a picture of
/// the Plaxis ProMax window, the versions (Plaxis ProMax, Plaxis, Claude Code, Windows), the open project's name, and the
/// last activity-log entries (failed calls included). Sketch ProMax's BugReport with Revit's parts replaced: the window
/// gives the picture and what Plaxis is open through <see cref="Screenshot"/> and <see cref="Situation"/>. Used by
/// Settings > Report bug (which asks for the description) and by the report_bug tool (Claude writes it).
/// </summary>
internal static class BugReport
{
    private const int MaxActivity = 50, MaxDescription = 8000;

    /// <summary>Set by the window: a JPEG of itself, or null with the reason. Called on any thread.</summary>
    internal static Func<(byte[]? Jpeg, string? Error)>? Screenshot;

    /// <summary>Set by the window: the versions and what is open (product, version, project), one "- name: value" line each.</summary>
    internal static Func<IReadOnlyList<string>>? Situation;

    public static (byte[]? Jpeg, string? Error) TakeScreenshot()
    {
        if (Screenshot is not { } take) return (null, "no window to picture");
        try { return take(); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>The versions, for the report and the export.</summary>
    public static string Versions()
    {
        var md = new StringBuilder();
        md.AppendLine("## Versions");
        md.AppendLine();
        md.AppendLine($"- Plaxis ProMax: {Update.Updater.CurrentText}");
        try
        {
            foreach (var line in Situation?.Invoke() ?? []) md.AppendLine(line);
        }
        catch (Exception ex) { md.AppendLine("- (what Plaxis has open could not be read: " + ex.Message + ")"); }
        md.AppendLine($"- Windows: {Environment.OSVersion.VersionString}");
        md.AppendLine($"- .NET: {Environment.Version}");
        return md.ToString();
    }

    /// <summary>Writes the zip and refreshes Bug log.md. Returns the zip's path.</summary>
    public static string Create(string description, (byte[]? Jpeg, string? Error) shot, string from, string? details = null)
    {
        description = string.Join(" ", (description ?? "").Replace("\r", " ").Replace("\n", " ").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        if (description.Length == 0) throw new ArgumentException("Say in one line what went wrong.");
        // Kept whole (Sketch ProMax bug 72: a report cut at 500 characters lost the cause); the Bug log list shortens it itself.
        if (description.Length > MaxDescription) description = description.Substring(0, MaxDescription) + "…";

        var now = DateTime.Now;
        Directory.CreateDirectory(BugLog.ReportsFolder);
        var (name, id) = BugLog.NameFor(now);
        var file = Path.Combine(BugLog.ReportsFolder, name);
        if (File.Exists(file))
        {
            (name, id) = BugLog.NameFor(now, seconds: true);
            file = Path.Combine(BugLog.ReportsFolder, name);
        }

        var activity = ActivityLog.Last(MaxActivity);
        var failed = activity.Count(l => l.Contains("\"ok\":false"));
        var md = new StringBuilder();
        md.AppendLine($"# Bug report {id}");
        md.AppendLine();
        md.AppendLine($"**What happens:** {description}");
        md.AppendLine();
        md.AppendLine($"- Reported: {now:yyyy-MM-dd HH:mm} (local time), from {from}");
        md.AppendLine($"- Screenshot: {(shot.Jpeg is not null ? "screenshot.jpg (the Plaxis ProMax window)" : "none (" + shot.Error + ")")}");
        md.AppendLine($"- Activity: the last {activity.Count} tool calls in activity.jsonl ({failed} did not work), oldest first");
        md.AppendLine();
        md.Append(Versions());

        var partial = file + ".partial";
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Add(string entryName, byte[] data, bool compress = true)
                {
                    var entry = zip.CreateEntry(entryName, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                    using var s = entry.Open();
                    s.Write(data, 0, data.Length);
                }
                var utf8 = new UTF8Encoding(false);
                Add("description.txt", utf8.GetBytes(description));
                Add("report.md", utf8.GetBytes(md.ToString()));
                Add("activity.jsonl", utf8.GetBytes(string.Join("\n", activity) + "\n"));
                if (shot.Jpeg is not null) Add("screenshot.jpg", shot.Jpeg, compress: false);
                if (details is not null) Add("error.txt", utf8.GetBytes(details));
            }
            File.Move(partial, file);
        }
        catch
        {
            try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        BugLog.RewriteLocal();
        return file;
    }

    public static string Size(string file) => (new FileInfo(file).Length / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
}

/// <summary>Files a bug report when the user says something is a bug.</summary>
internal sealed class ReportBugTool : IPlaxisTool
{
    public string Name => "report_bug";
    public string Description =>
        "File a Plaxis ProMax bug report when the user says something is a bug (\"that's a bug\", \"report this\"). Writes ONE " +
        "small zip to Documents\\Plaxis ProMax\\Bugs\\Reports with the description, a picture of the Plaxis ProMax window, the " +
        "Plaxis ProMax, Plaxis and Windows versions, the project name and the last 50 tool calls with their errors. Nothing is " +
        "sent anywhere; the user sends the zip. Write the description yourself from the conversation: one plain line saying " +
        "what the user did, what happened and what they expected, with the tool name. Doesn't change the project.";
    public string PropertiesJson => """
    {
      "description": {"type": "string", "description": "One line: what happened, with the tool involved and what was expected."}
    }
    """;
    public string[] Required => ["description"];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var file = BugReport.Create(args.String("description"), BugReport.TakeScreenshot(), "the chat (report_bug)");
        return Task.FromResult(ToolResult.Ok(new
        {
            file, size = BugReport.Size(file), folder = BugLog.Root,
            next = "Tell the user the report is saved and listed under \"Not sent yet\" in Bug log.md in that folder (Settings > Open bug folder), " +
                   "and that they can send the zip to the developer.",
        }));
    }
}

/// <summary>The last tool calls and whether they worked, from the activity log (Sketch ProMax's get_activity_log).</summary>
internal sealed class GetActivityLogTool : IPlaxisTool
{
    public string Name => "get_activity_log";
    public string Description =>
        "The last tool calls on this PC, newest last: time, project, tool, whether it changed the project, whether it worked, " +
        "how long it took, its arguments and the start of its answer. Use it to see what was done or what failed earlier.";
    public string PropertiesJson => """
    {
      "count": {"type": "integer", "description": "How many calls (default 30, at most 200)."},
      "failed_only": {"type": "boolean", "description": "Only the calls that did not work."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var count = (int)Math.Clamp(args.OptLong("count") ?? 30, 1, 200);
        var lines = ActivityLog.Last(args.OptBool("failed_only") ? 2000 : count);
        if (args.OptBool("failed_only")) lines = lines.Where(l => l.Contains("\"ok\":false")).TakeLast(count).ToList();
        return Task.FromResult(new ToolResult(lines.Count == 0 ? "No tool calls logged yet." : string.Join("\n", lines)));
    }
}
