using System.IO;
using System.Text;
using System.Text.Json;

namespace PlaxisProMax.Tools;

/// <summary>
/// Every tool call, one JSON line each, in %APPDATA%\PlaxisProMax\logs\&lt;date&gt;.jsonl: when, which project, which tool,
/// whether it changes the project, whether it worked, its arguments and the start of its answer. Sketch ProMax's activity
/// log in the same shape, so the bug reports, the chats-and-bugs export and get_activity_log read it the same way.
/// </summary>
internal static class ActivityLog
{
    private static readonly object Gate = new();

    /// <summary>Set only by the tests, so their lines go to a temporary folder.</summary>
    internal static string? RootOverride;

    public static string Folder => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlaxisProMax", "logs");

    public static string FileFor(string date) => Path.Combine(Folder, date + ".jsonl");

    public static void Write(string project, string tool, bool changes, IReadOnlyDictionary<string, JsonElement> args, bool ok, string result, long ms)
    {
        try
        {
            var argText = JsonSerializer.Serialize(args);
            var line = JsonSerializer.Serialize(new
            {
                time = DateTime.Now.ToString("s"),
                model = project,
                tool,
                changes,
                ok,
                ms,
                arguments = argText.Length > 4000 ? argText.Substring(0, 4000) + "…" : argText,
                result = result.Length > 500 ? result.Substring(0, 500) + "…" : result,
            });
            Append(DateTime.Now, line);
        }
        catch (Exception) { /* logging must never break a tool call */ }
    }

    /// <summary>A line that is not a tool call (the window opening, an update), in the same shape.</summary>
    public static void Note(string what, string text)
    {
        try
        {
            var now = DateTime.Now;
            Append(now, JsonSerializer.Serialize(new { time = now.ToString("s"), model = "", tool = what, changes = false, ok = true, ms = 0, arguments = "", result = text }));
        }
        catch (Exception) { /* best effort */ }
    }

    private static void Append(DateTime when, string line)
    {
        var file = FileFor(when.ToString("yyyy-MM-dd"));
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    /// <summary>The last <paramref name="max"/> lines across the daily files, oldest first (Sketch ProMax's BugReport.Activity).</summary>
    public static List<string> Last(int max)
    {
        var lines = new List<string>();
        try
        {
            if (!Directory.Exists(Folder)) return lines;
            foreach (var day in Directory.GetFiles(Folder, "*.jsonl").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Take(30))
            {
                var text = ReadLines(day);
                if (text is null) continue;
                lines.InsertRange(0, text.Skip(Math.Max(0, text.Length - (max - lines.Count))));
                if (lines.Count >= max) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return lines;
    }

    /// <summary>A day's lines, read while the window may still be writing to it; null when it can't be read.</summary>
    public static string[]? ReadLines(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd().Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return null; }
    }
}
