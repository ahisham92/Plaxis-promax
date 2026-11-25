using System.Text.RegularExpressions;
using PlaxisProMax.Settings;

namespace PlaxisProMax.Update;

/// <summary>
/// What's new (from Sketch ProMax's update notice): the release notes as plain text, and the one line the window says
/// the first time a newer version starts. The version last seen is kept in %APPDATA%\PlaxisProMax\seen-version.txt.
/// </summary>
internal static class UpdateNotes
{
    /// <summary>Set only by the tests.</summary>
    internal static string? FileOverride;

    private static string SeenFile => FileOverride ?? Path.Combine(AgentSettings.Folder, "seen-version.txt");

    /// <summary>Markdown notes as plain text: headings and bullets kept as lines, marks removed.</summary>
    public static string Plain(string notes)
    {
        var lines = (notes ?? "").Replace("\r", "").Split('\n').Select(l =>
        {
            var t = Regex.Replace(l, @"^\s{0,3}#{1,6}\s*", "");
            t = Regex.Replace(t, @"^(\s*)[-*]\s+", "$1• ");
            t = Regex.Replace(t, @"\*\*(.+?)\*\*|__(.+?)__", m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            t = Regex.Replace(t, @"`([^`]*)`", "$1");
            return Regex.Replace(t, @"\[([^\]]+)\]\([^)]+\)", "$1").TrimEnd();
        });
        return Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>The first few lines that say something (no title, no blank lines), for the note in the chat.</summary>
    public static string Top(string notes, int lines = 4) =>
        string.Join("\n", Plain(notes).Split('\n').Where(l => l.Trim().Length > 0 && !l.StartsWith("Plaxis ProMax", StringComparison.OrdinalIgnoreCase)).Take(lines));

    /// <summary>
    /// The line to say when this start is the first of a newer version than the one last seen; null on a first install,
    /// the same version, or an older one. Remembers <paramref name="current"/> either way.
    /// </summary>
    public static string? TakeUpdated(string current, Func<string, string?>? notesFor = null)
    {
        try
        {
            var seen = File.Exists(SeenFile) ? File.ReadAllText(SeenFile).Trim() : null;
            if (seen == current) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(SeenFile)!);
            File.WriteAllText(SeenFile, current);
            if (seen is null || !Version.TryParse(seen, out var was) || !Version.TryParse(current, out var now) || now <= was) return null;
            var notes = (notesFor ?? Updater.NotesFor)(current);
            return $"Updated to Plaxis ProMax {current}." + (string.IsNullOrWhiteSpace(notes) ? "" : " What's new:\n" + Top(notes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
