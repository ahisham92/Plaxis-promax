using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PlaxisProMax.Plaxis;

namespace PlaxisProMax.Modeller;

/// <summary>
/// Calculate in Plaxis: the one step of the Modeller that uses Plaxis, and so a Plaxis licence. It starts PLAXIS Input
/// with its remote scripting server on (a free port and a new password), builds the model in a new project, meshes and
/// calculates it, copies the results into a results file (.ppr) and closes Plaxis again, so the licence is given back
/// as soon as the run ends (unless the user asked to keep Plaxis open). Only Plaxis's own Python talks to Plaxis.
/// </summary>
public sealed class PlaxisRun
{
    public sealed record Install(string Product, string Name, string Path);

    public sealed class Step
    {
        public string Name { get; init; } = "";
        public string State { get; set; } = "waiting"; // waiting, running, done, failed
        public string Detail { get; set; } = "";
        public double? Seconds { get; set; }
    }

    /// <param name="Keep">What the results file keeps: {"phases": [names or ids], "results": [displacements, stresses,
    /// pore_pressures, plastic_points, structures]}; null keeps everything.</param>
    public sealed record Options(string? Plaxis, int StoredSteps = 1, bool OneByOne = true, bool KeepOpen = false, JsonObject? Keep = null);

    /// <summary>The groups of results a run can keep or leave out of the results file.</summary>
    public static readonly string[] ResultGroups = ["displacements", "stresses", "pore_pressures", "plastic_points", "structures"];

    private readonly List<string> _log = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Process? _plaxis;

    public List<Step> Steps { get; } =
    [
        new() { Name = "Start Plaxis Input (the licence is taken here)" },
        new() { Name = "Connect to Plaxis" },
        new() { Name = "Build the model and mesh it" },
        new() { Name = "Calculate the phases" },
        new() { Name = "Read the results into the results file" },
        new() { Name = "Close Plaxis (the licence is given back)" },
    ];

    public string State { get; private set; } = "running";
    public string? Error { get; private set; }

    /// <summary>
    /// Set when the run finished but not every phase ended OK: the results file holds only the phases that did, and the
    /// page says so instead of a plain "Calculated" (CSI ProMax field test, 25 Nov 2026: a check said "pass" for a
    /// model whose analysis had not run).
    /// </summary>
    public string? Warning { get; private set; }
    public string? ResultsPath { get; private set; }
    public string? ProjectPath { get; private set; }
    public bool LicenceInUse { get; private set; }
    public JsonObject? BuildAnswer { get; private set; }
    public JsonObject? CalcAnswer { get; private set; }
    public JsonObject? ExportAnswer { get; private set; }
    public Task Done { get; private set; } = Task.CompletedTask;

    public IReadOnlyList<string> Log { get { lock (_gate) return _log.ToList(); } }

    private void Say(string line) { lock (_gate) _log.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line); }

    /// <summary>Plaxis Input programs on this PC: PLAXIS 2D and 3D, Bentley's and Seequent's folders, newest first.</summary>
    public static IReadOnlyList<Install> FindInputs(IEnumerable<string>? roots = null)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        roots ??= [Path.Combine(pf, "Bentley", "Geotechnical"), Path.Combine(pf, "Seequent"), Path.Combine(pf, "Plaxis"), pf];
        var found = new List<Install>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root, "*PLAXIS*"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var dir in dirs)
            {
                foreach (var (exe, product) in new[] { ("Plaxis2DXInput.exe", "2D"), ("Plaxis2DInput.exe", "2D"), ("Plaxis3DInput.exe", "3D") })
                {
                    var path = Path.Combine(dir, exe);
                    if (File.Exists(path) && !found.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                        found.Add(new Install(product, Path.GetFileName(dir), path));
                }
            }
        }
        return found.OrderByDescending(f => Release(f.Name)).ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// How new a Plaxis folder's release is, from its name: "PLAXIS 2D 2024.2" is 2024.2, "CONNECT Edition V22 Update 1"
    /// is 2022.1 (V22 came out in 2022), and a name with neither comes last.
    /// </summary>
    internal static double Release(string name)
    {
        var year = System.Text.RegularExpressions.Regex.Match(name, @"\b(20\d\d)(?:\.(\d+))?\b");
        if (year.Success) return int.Parse(year.Groups[1].Value, CultureInfo.InvariantCulture) + (year.Groups[2].Success ? int.Parse(year.Groups[2].Value, CultureInfo.InvariantCulture) / 10.0 : 0);
        var v = System.Text.RegularExpressions.Regex.Match(name, @"\bV(\d\d)\b(?:.*?Update\s*(\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (v.Success) return 2000 + int.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture) + (v.Groups[2].Success ? int.Parse(v.Groups[2].Value, CultureInfo.InvariantCulture) / 10.0 : 0);
        return 0;
    }

    /// <summary>Plaxis Input's command line with its scripting server on: the port and password are its own options.</summary>
    public static string ServerArguments(int port, string password) => $"--AppServerPort={port.ToString(CultureInfo.InvariantCulture)} --AppServerPassword={password}";

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Starts the run in the background; <see cref="Done"/> ends when it has.</summary>
    public static PlaxisRun Start(JsonObject model, string modelPath, Options options, string python, string workerScript)
    {
        var run = new PlaxisRun();
        run.Done = Task.Run(() => run.RunAsync((JsonObject)model.DeepClone(), modelPath, options, python, workerScript));
        return run;
    }

    /// <summary>Stops the run: Plaxis is closed, so the licence is given back.</summary>
    public void Stop()
    {
        Say("Stopped by you.");
        _stop.Cancel();
        KillPlaxis();
    }

    private async Task RunAsync(JsonObject model, string modelPath, Options o, string python, string workerScript)
    {
        var product = model["product"]?.ToString() ?? "2D";
        var ct = _stop.Token;
        Step? current = null;
        var watch = Stopwatch.StartNew();
        void Begin(int i) { current = Steps[i]; current.State = "running"; watch.Restart(); Say(current.Name); }
        void End(string detail = "") { if (current is null) return; current.State = "done"; current.Detail = detail; current.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1); }
        PlaxisLink? link = null;
        try
        {
            var exe = o.Plaxis;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                exe = FindInputs().FirstOrDefault(i => i.Product == product)?.Path ?? throw new InvalidOperationException($"No PLAXIS {product} Input was found on this PC.");
            ResultsPath = ModelFile.ResultsPath(modelPath);
            ProjectPath = ModelFile.PlaxisProjectPath(modelPath, product);
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectPath)!);

            Begin(0);
            var port = FreePort();
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            _plaxis = Process.Start(new ProcessStartInfo(exe, ServerArguments(port, password)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" })
                ?? throw new InvalidOperationException("Plaxis Input did not start.");
            LicenceInUse = true;
            End(Path.GetFileName(exe) + ", scripting port " + port.ToString(CultureInfo.InvariantCulture));

            Begin(1);
            link = new PlaxisLink(() => PlaxisLink.WorkerStart(python, workerScript));
            var deadline = DateTime.UtcNow.AddMinutes(4);
            WorkerAnswer? connected = null;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (_plaxis.HasExited) throw new InvalidOperationException("Plaxis Input closed before it could be reached (no licence free, or it was closed).");
                connected = await link.ConnectAsync(port, password, ct, 20000).ConfigureAwait(false);
                if (connected.Ok) break;
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
            if (connected is not { Ok: true }) throw new InvalidOperationException("Plaxis Input did not answer in 4 minutes: " + connected?.Error);
            End(connected.Result?["server"]?.ToString() ?? "");

            Begin(2);
            var build = await Call(link, "build_model", new JsonObject { ["model"] = model, ["save_as"] = ProjectPath }, 3600, ct).ConfigureAwait(false);
            BuildAnswer = build;
            var failed = (build["failed"] as JsonArray)?.Count ?? 0;
            var refused = (build["refused"] as JsonArray)?.Count ?? 0;
            foreach (var x in (build["failed"] as JsonArray)?.OfType<JsonObject>() ?? []) Say("  failed: " + x["step"] + ": " + x["why"]);
            foreach (var x in (build["refused"] as JsonArray)?.OfType<JsonObject>().Take(20) ?? []) Say("  not set: " + x["object"] + " " + x["value"] + ": " + x["why"]);
            if ((build["failed"] as JsonArray)?.OfType<JsonObject>().Any(x => x["step"]?.ToString() == "mesh") == true)
                throw new InvalidOperationException("Plaxis could not mesh the model. See the steps above.");
            End($"{(build["made"] as JsonArray)?.Count ?? 0} made, {refused} values not taken, {failed} steps failed");

            Begin(3);
            var calc = await Call(link, "calculate", new JsonObject { ["one_by_one"] = o.OneByOne, ["stored_steps"] = o.StoredSteps, ["save"] = true }, 24 * 3600, ct).ConfigureAwait(false);
            CalcAnswer = calc;
            var phases = (calc["phases"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var ok = phases.Count(p => p["CalculationResult"]?.ToString() is "OK" or "1" or "ok");
            if (calc["stopped_at"] is { } stopped) Say("  stopped at " + stopped);
            if (ok < phases.Count)
            {
                var notOk = phases.Where(p => p["CalculationResult"]?.ToString() is not ("OK" or "1" or "ok"))
                    .Select(p => (p["Identification"] ?? p["label"])?.ToString()).Where(n => !string.IsNullOrEmpty(n)).Take(8).ToList();
                Warning = ok == 0
                    ? "No phase ended OK, so there are no results."
                    : $"Only {ok} of {phases.Count} phases ended OK" + (calc["stopped_at"] is { } at ? $" (stopped at {at})" : "") +
                      ". The results file has only those; these have no results: " + string.Join(", ", notOk) + ".";
                Say("  " + Warning);
            }
            End($"{ok} of {phases.Count} phases ended OK" + (calc["stopped_at"] is { } where ? $", stopped at {where}" : ""));

            Begin(4);
            var export = await Call(link, "export_results", new JsonObject { ["model"] = model, ["save_as"] = ResultsPath, ["keep"] = o.Keep?.DeepClone() }, 2 * 3600, ct).ConfigureAwait(false);
            ExportAnswer = export;
            var bytes = export["bytes"] is JsonValue b && b.TryGetValue<long>(out var size) ? size : 0;
            End($"{(export["phases"] as JsonArray)?.Count ?? 0} phases, {export["nodes"]} nodes, {Size(bytes)}");

            Begin(5);
            if (o.KeepOpen) End("Kept open, as asked: the licence stays in use until you close Plaxis.");
            else
            {
                await Close(link).ConfigureAwait(false);
                End("Plaxis closed");
            }
            State = "done";
            Say("Done. Results: " + ResultsPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex is OperationCanceledException ? "Stopped." : ex.Message;
            if (current is not null) { current.State = "failed"; current.Detail = Error; current.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1); }
            Say("Stopped: " + Error);
            State = "failed";
            if (!o.KeepOpen && link is not null)
            {
                try { await Close(link).ConfigureAwait(false); } catch (Exception) { }
                Steps[5].State = "done"; Steps[5].Detail = "Plaxis closed after the failure";
            }
            if (!o.KeepOpen) KillPlaxis();
        }
        finally
        {
            link?.Dispose();
        }
    }

    private async Task Close(PlaxisLink link)
    {
        try { await link.CallAsync("close_project", new JsonObject { ["save"] = true }, 120000, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
        if (_plaxis is { HasExited: false } p)
        {
            try { p.CloseMainWindow(); } catch (InvalidOperationException) { }
            if (!p.WaitForExit(20000)) KillPlaxis();
        }
        LicenceInUse = false;
    }

    private void KillPlaxis()
    {
        try { if (_plaxis is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        LicenceInUse = false;
    }

    internal static string Size(long bytes) => bytes switch
    {
        <= 0 => "size not known",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
    };

    private static async Task<JsonObject> Call(PlaxisLink link, string op, JsonObject args, int seconds, CancellationToken ct)
    {
        var a = await link.CallAsync(op, args, seconds * 1000, ct).ConfigureAwait(false);
        if (!a.Ok) throw new InvalidOperationException(op.Replace('_', ' ') + ": " + a.Error);
        return a.Result as JsonObject ?? new JsonObject();
    }

    /// <summary>The run as the Modeller page shows it.</summary>
    public JsonObject Status() => new()
    {
        ["state"] = State,
        ["error"] = Error,
        ["warning"] = Warning,
        ["licence"] = LicenceInUse ? "in use" : "free",
        ["results"] = State == "done" ? ResultsPath : null,
        ["project"] = ProjectPath,
        ["steps"] = new JsonArray(Steps.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["state"] = s.State, ["detail"] = s.Detail, ["seconds"] = s.Seconds }).ToArray()),
        ["log"] = new JsonArray(Log.TakeLast(200).Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
    };
}
