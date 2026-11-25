using System.IO;

namespace PlaxisProMax.Tools;

/// <summary>
/// Checks on file paths Claude passes to the tools (see ApprovalPolicy for the approval rules).
/// </summary>
internal static class OutputPaths
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Why a caller-supplied output path is refused, or null when it is fine: network shares (\\server\share),
    /// device paths (\\?\, \\.\, CON, NUL, COM1...) and alternate data streams (file.txt:stream). Local and mapped
    /// drive paths and relative names pass.
    /// </summary>
    public static string? Problem(string? path)
    {
        if (path is null) return null;
        var p = path.Trim().Trim('"');
        if (p.Length == 0) return null;
        if (p.Length >= 2 && p[0] is '\\' or '/' && p[1] is '\\' or '/')
            return "network shares (\\\\server\\...) and device paths (\\\\?\\, \\\\.\\) are not allowed; use a folder on a drive letter";
        var colon = p.IndexOf(':');
        if (colon >= 0 && !(colon == 1 && char.IsLetter(p[0]) && p.IndexOf(':', 2) < 0))
            return "a ':' is only allowed after a drive letter";
        foreach (var segment in p.Split('\\', '/'))
        {
            var name = segment.TrimEnd(' ', '.');
            var dot = name.IndexOf('.');
            if (dot >= 0) name = name.Substring(0, dot);
            if (DeviceNames.Contains(name.TrimEnd(' '))) return $"'{segment}' is a Windows device name, not a file or folder";
        }
        return null;
    }

    /// <summary>The path unchanged when it is fine (null stays null); otherwise an ArgumentException saying why.</summary>
    public static string? Checked(string? path) =>
        Problem(path) is { } why ? throw new ArgumentException($"Can't use '{path}': {why}.") : path;

    /// <summary>The path as Windows will resolve it, so the dialog shows where a relative name really lands; unchanged when it can't be resolved.</summary>
    public static string FullPath(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return path; }
    }
}
