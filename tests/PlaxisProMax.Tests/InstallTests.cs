using PlaxisProMax.Plaxis;

/// <summary>How the window finds Plaxis's Python and reads what the launcher passed.</summary>
internal static class InstallTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var env = new Dictionary<string, string?> { ["PLAXISPROMAX_PASSWORD"] = "pw" };
        var a = PlaxisInstall.Parse(["--python", @"C:\ProgramData\Bentley\python.exe", "--port", "10000"], k => env.GetValueOrDefault(k));
        check("install: the launcher's port, Python and password are read", a is { Port: 10000, Password: "pw", FromPlaxis: true } && a.Python!.EndsWith("python.exe"), a.ToString());
        var b = PlaxisInstall.Parse(["--port", "99999"], _ => null);
        check("install: a bad port is ignored, and the Start menu start isn't from Plaxis", b is { Port: null, FromPlaxis: false, Password: null }, b.ToString());

        var root = Path.Combine(Path.GetTempPath(), "ppm-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            var v1 = Path.Combine(root, "Bentley", "Geotechnical", "PLAXIS Python Distribution V1", "python");
            var v2 = Path.Combine(root, "Bentley", "Geotechnical", "PLAXIS Python Distribution V2", "python");
            Directory.CreateDirectory(v1);
            Directory.CreateDirectory(v2);
            File.WriteAllText(Path.Combine(v1, "python.exe"), "");
            File.WriteAllText(Path.Combine(v2, "python.exe"), "");
            var found = PlaxisInstall.PythonCandidates(root, Path.Combine(root, "none")).ToList();
            check("install: Bentley's Python distributions are found, the newest first", found.Count == 2 && found[0].Contains("V2"), string.Join(" | ", found));
            var saved = Path.Combine(v1, "python.exe");
            check("install: the Python passed at start wins, then the saved one, then the first found",
                PlaxisInstall.FindPython(saved, null, () => found) == saved
                && PlaxisInstall.FindPython(null, saved, () => found) == saved
                && PlaxisInstall.FindPython("C:/missing.exe", null, () => found) == found[0]
                && PlaxisInstall.FindPython(null, null, () => []) is null, "");
        }
        finally { Directory.Delete(root, true); }
    }
}
