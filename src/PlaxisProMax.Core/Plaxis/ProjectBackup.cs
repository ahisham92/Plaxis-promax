namespace PlaxisProMax.Plaxis;

/// <summary>
/// A backup copy of a Plaxis project before an unattended run changes it (Sketch ProMax saves a copy of the model in a
/// Backup folder beside it). A Plaxis project is its file (.p2dx, .p3dx) and a data folder of the same name with "dat"
/// added; both are copied, into Backup\&lt;name&gt; &lt;date time&gt;\ beside the project.
/// </summary>
internal static class ProjectBackup
{
    /// <summary>The backups kept for one project; older ones are removed.</summary>
    public const int Keep = 5;

    /// <summary>The project's data folder (it may not exist for a project that was never calculated).</summary>
    public static string DataFolder(string projectFile) => projectFile + "dat";

    /// <summary>Copies the project; the copy's project file. Throws IOException or UnauthorizedAccessException.</summary>
    public static string Copy(string projectFile, DateTime now)
    {
        if (!File.Exists(projectFile)) throw new FileNotFoundException("The project's file isn't there.", projectFile);
        var name = Path.GetFileNameWithoutExtension(projectFile);
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectFile))!, "Backup");
        var folder = Path.Combine(root, $"{name} {now:yyyy-MM-dd HHmmss}");
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(projectFile));
        File.Copy(projectFile, copy, overwrite: true);
        var data = DataFolder(projectFile);
        if (Directory.Exists(data)) CopyFolder(data, DataFolder(copy));
        Prune(root, name);
        return copy;
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(from)) CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>Only the newest <see cref="Keep"/> backups of this project stay (the names sort by time).</summary>
    private static void Prune(string root, string name)
    {
        var old = Directory.GetDirectories(root, name + " ????-??-?? ??????").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).Skip(Keep);
        foreach (var dir in old)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
