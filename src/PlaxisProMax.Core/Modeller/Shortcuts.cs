using System.IO;
using System.Runtime.InteropServices;

namespace PlaxisProMax.Modeller;

/// <summary>
/// The Modeller and Viewer shortcuts, on the desktop and in the Start menu (Plaxis ProMax folder), as ETABS ProMax makes
/// its Modeler and Viewer ones. Made at the first start with no question asked, and pointed at the new version after an
/// update; a shortcut the user deleted stays deleted. The Modeller's runs this program with --modeller (no Plaxis, so no
/// licence), the Viewer's runs it with --viewer, which only opens the Viewer page (no Plaxis, no licence, no chat).
/// </summary>
public static class Shortcuts
{
    public const string ModellerName = "Plaxis ProMax Modeller", ViewerName = "Plaxis ProMax Viewer";

    /// <summary>Added to the mark when the shortcuts themselves change, so they are made again once.</summary>
    internal const string Revision = "|2";

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlaxisProMax");
    private static string Mark => Path.Combine(Root, "shortcuts.txt");

    public static IEnumerable<string> Folders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Plaxis ProMax");
    }

    /// <summary>True when the shortcuts were last made for another program file (an update moved it) or an older kind.</summary>
    internal static bool Due(string? marked, string program) => !string.Equals(marked?.Trim(), program + Revision, StringComparison.OrdinalIgnoreCase);

    /// <summary>Called at every start, off the window's thread. Never throws: a PC that forbids shortcuts just has none.</summary>
    public static void Ensure()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var program = Environment.ProcessPath ?? "";
            if (program.Length == 0) return;
            _ = StudioFiles.ViewerPage; // kept the same as this version's
            var first = !File.Exists(Mark);
            if (!first && !Due(File.ReadAllText(Mark), program)) return;
            Make(program, onlyExisting: !first);
            Directory.CreateDirectory(Root);
            File.WriteAllText(Mark, program + Revision);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidOperationException or NotSupportedException) { }
    }

    /// <summary>Makes all of them again, whatever was deleted. Gives back the files made.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> MakeAll()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Shortcuts are made on Windows only.");
        var program = Environment.ProcessPath ?? throw new InvalidOperationException("This program's own path is not known.");
        var made = Make(program, onlyExisting: false);
        Directory.CreateDirectory(Root);
        File.WriteAllText(Mark, program + Revision);
        return made;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> Make(string program, bool onlyExisting)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows' shortcut maker is not available on this PC.");
        var made = new List<string>();
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            foreach (var folder in Folders())
            {
                if (folder.Length == 0) continue;
                Directory.CreateDirectory(folder);
                var modeller = Path.Combine(folder, ModellerName + ".lnk");
                if (!onlyExisting || File.Exists(modeller))
                {
                    dynamic link = shell.CreateShortcut(modeller);
                    link.TargetPath = program;
                    link.Arguments = "--modeller";
                    link.WorkingDirectory = Path.GetDirectoryName(program) ?? "";
                    link.Description = "Plaxis ProMax Modeller: build Plaxis models with no Plaxis open and no licence";
                    link.IconLocation = program + ",0";
                    link.Save();
                    made.Add(modeller);
                }
                var viewer = Path.Combine(folder, ViewerName + ".lnk");
                if (!onlyExisting || File.Exists(viewer))
                {
                    dynamic link = shell.CreateShortcut(viewer);
                    // Through the program, so the page it opens is always this version's (it needs no Plaxis and no chat).
                    link.TargetPath = program;
                    link.Arguments = "--viewer";
                    link.WorkingDirectory = Path.GetDirectoryName(program) ?? "";
                    link.Description = "Plaxis ProMax Viewer: open Plaxis results (.ppr) with no Plaxis and no licence";
                    link.IconLocation = program + ",0";
                    link.Save();
                    made.Add(viewer);
                }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return made;
    }
}
