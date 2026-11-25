using System.IO;

namespace PlaxisProMax.Plaxis;

/// <summary>
/// Where Plaxis keeps its own Python, and how the window was started. Started from Plaxis's Expert menu, the launcher
/// runs on that Python and passes it on (--python), with the scripting server's port and password; started from the
/// Start menu, the settings or the usual install folders give the Python, and the settings the port and password.
/// </summary>
public static class PlaxisInstall
{
    /// <summary>Plaxis's Python candidates, most likely first: the folders Bentley installs its Python distribution to.</summary>
    public static IEnumerable<string> PythonCandidates(string? programData = null, string? programFiles = null)
    {
        programData ??= Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        programFiles ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var roots = new[]
        {
            Path.Combine(programData, "Bentley", "Geotechnical"),
            Path.Combine(programFiles, "Bentley", "Geotechnical"),
            Path.Combine(programData, "Plaxis"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root, "*Python*"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            // The newest distribution first ("PLAXIS Python Distribution V2" before "V1").
            foreach (var dir in dirs.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                foreach (var exe in new[] { Path.Combine(dir, "python", "python.exe"), Path.Combine(dir, "python.exe") })
                    if (File.Exists(exe)) yield return exe;
        }
    }

    /// <summary>The Python to run the worker on: the one passed at start, then the settings', then the first found.</summary>
    public static string? FindPython(string? given, string? saved, Func<IEnumerable<string>>? candidates = null)
    {
        if (!string.IsNullOrWhiteSpace(given) && File.Exists(given)) return given;
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved)) return saved;
        return (candidates ?? (() => PythonCandidates()))().FirstOrDefault();
    }

    /// <summary>What the window was started with: --port, --python and --project; the password comes in PLAXISPROMAX_PASSWORD.</summary>
    public sealed record StartArgs(int? Port, string? Python, string? Password, bool FromPlaxis);

    /// <summary>
    /// Reads the command line. The password is never on the command line (other programs can read that); the launcher
    /// puts it in the environment of this process only.
    /// </summary>
    public static StartArgs Parse(IReadOnlyList<string> args, Func<string, string?> env)
    {
        int? port = null;
        string? python = null;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536) port = p;
            if (args[i] == "--python") python = args[i + 1];
        }
        var password = env("PLAXISPROMAX_PASSWORD");
        return new StartArgs(port, python, password, port is not null);
    }
}
