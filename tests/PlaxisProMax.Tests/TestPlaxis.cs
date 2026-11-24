using System.Diagnostics;
using System.Text;

/// <summary>The stand-in Plaxis (tests/fake_plaxis) and the real worker script, run on this machine's Python.</summary>
internal static class TestPlaxis
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "PLAN.md"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Run the tests from inside the repository.");
        }
    }

    public static string Worker => Path.Combine(Root, "src", "PlaxisProMax.Core", "Plaxis", "plaxis_worker.py");
    public static string Fake => Path.Combine(Root, "tests", "fake_plaxis");

    public static string Python => OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>
    /// The worker as the window starts it, but with the stand-in plxscripting on its path: -I ignores PYTHONPATH, so the
    /// path is added in a small bootstrap that then runs the worker script itself.
    /// </summary>
    public static ProcessStartInfo WorkerStart()
    {
        var boot = $"import sys, runpy; sys.path.insert(0, r'{Fake}'); sys.argv = [r'{Worker}']; runpy.run_path(r'{Worker}', run_name='__main__')";
        var info = new ProcessStartInfo(Python, "-u -c \"" + boot.Replace("\"", "\\\"") + "\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        return info;
    }
}
