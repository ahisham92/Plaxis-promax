using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PlaxisProMax.Mcp;

namespace PlaxisProMax.Modeller;

/// <summary>
/// The model open in the Modeller, shared by its page and the chat: either can change it, the other sees the change (the
/// page asks every second and a half). Held here, not in Plaxis: nothing in this class uses Plaxis or its licence, except
/// <see cref="StartRun"/>, which is Calculate in Plaxis.
/// </summary>
public sealed class StudioSession
{
    private readonly object _gate = new();
    private JsonObject? _model;
    private int _rev;
    private string? _path;
    private bool _dirty;
    private PlaxisRun? _run;

    public StudioSession(Func<string?> python, string workerScript)
    {
        Python = python;
        WorkerScript = workerScript;
    }

    /// <summary>Plaxis's Python (only Calculate in Plaxis needs it; null when this PC has none found).</summary>
    public Func<string?> Python { get; }

    public string WorkerScript { get; }

    public sealed record Snapshot(JsonObject? Model, int Rev, string? Path, bool Dirty);

    public Snapshot Get()
    {
        lock (_gate) return new((JsonObject?)_model?.DeepClone(), _rev, _path, _dirty);
    }

    /// <summary>
    /// Puts a whole model (the page's change, or the chat's). With <paramref name="basedOn"/>, refused when the model
    /// changed since that revision, so a change from the chat is never overwritten by a page that had not seen it yet.
    /// </summary>
    public int Put(JsonObject model, int? basedOn = null, bool dirty = true)
    {
        var valid = ModelFile.Validate((JsonObject)model.DeepClone());
        lock (_gate)
        {
            if (basedOn is { } b && _model is not null && b != _rev)
                throw new InvalidOperationException("The model was changed from the chat at the same time; that change is shown now, so make yours again.");
            _model = valid;
            _dirty = dirty;
            return ++_rev;
        }
    }

    /// <summary>Changes the model in place (the chat's edits), as one revision.</summary>
    public int Change(Action<JsonObject> change)
    {
        lock (_gate)
        {
            var copy = (JsonObject?)_model?.DeepClone() ?? throw new InvalidOperationException("No model is open in the Modeller. Open or make one first.");
            change(copy);
            _model = ModelFile.Validate(copy);
            _dirty = true;
            return ++_rev;
        }
    }

    public Snapshot Open(string path)
    {
        var model = ModelFile.Load(path);
        lock (_gate)
        {
            _model = model;
            _path = Path.GetFullPath(path);
            _dirty = false;
            _rev++;
        }
        return Get();
    }

    /// <summary>A new model of the program's own blank kind, or a sample by its name.</summary>
    public Snapshot New(string product, string? title = null, string? sample = null)
    {
        JsonObject model;
        if (sample is not null)
        {
            var samples = ModelFile.Samples();
            if (!samples.TryGetValue(sample, out var s)) throw new InvalidOperationException("There is no sample called '" + sample + "'. Samples: " + string.Join(", ", samples.Keys) + ".");
            model = (JsonObject)s.DeepClone();
        }
        else model = ModelFile.Blank(product, title ?? "New project");
        lock (_gate)
        {
            _model = ModelFile.Validate(model);
            _path = null;
            _dirty = true;
            _rev++;
        }
        return Get();
    }

    /// <summary>
    /// Saves the model: to its own file, or under a name and folder (Save as). With neither, a new model goes to the
    /// usual folder under its title.
    /// </summary>
    public string Save(string? name = null, string? folder = null)
    {
        lock (_gate)
        {
            var model = _model ?? throw new InvalidOperationException("No model is open in the Modeller.");
            var path = _path;
            if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(folder) || path is null)
            {
                var file = ModelFile.SafeName(string.IsNullOrWhiteSpace(name) ? model["title"]?.ToString() : name);
                if (!file.EndsWith(".ppm", StringComparison.OrdinalIgnoreCase)) file += ".ppm";
                var dir = string.IsNullOrWhiteSpace(folder) ? (path is null ? ModelFile.Folder : Path.GetDirectoryName(path)!) : folder.Trim();
                path = Path.Combine(dir, file);
            }
            ModelFile.Save(model, path);
            _path = Path.GetFullPath(path);
            _dirty = false;
            _rev++;
            return _path;
        }
    }

    public PlaxisRun? Run { get { lock (_gate) return _run; } }

    /// <summary>
    /// Calculate in Plaxis: saves the model first (the results file goes beside it), then starts the run. Only one at a
    /// time, because each one takes a licence.
    /// </summary>
    public PlaxisRun StartRun(PlaxisRun.Options options)
    {
        var python = Python() ?? throw new InvalidOperationException(
            "Plaxis's own Python was not found on this PC, so Plaxis ProMax can't reach Plaxis to calculate. Start Plaxis ProMax once from Plaxis " +
            "(Expert > Python > Run script, \"Plaxis ProMax.py\") so it learns where it is; the Modeller and Viewer work without it.");
        lock (_gate)
        {
            if (_run is { State: "running" }) throw new InvalidOperationException("A calculation is already running. Stop it first, or wait for it to end.");
        }
        var path = Save();
        var model = Get().Model!;
        var run = PlaxisRun.Start(model, path, options, python, WorkerScript);
        lock (_gate) _run = run;
        return run;
    }
}

/// <summary>
/// Serves the Modeller and Viewer pages and the Modeller's calls on this PC's own address (127.0.0.1, a free port), under
/// a secret path made at start, so no other program or web page can reach the model. The pages open in a window of
/// their own (Edge in app mode). One request per connection, as the chat's MCP server does it.
/// </summary>
public sealed class StudioServer : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, string> _results = new(StringComparer.Ordinal);
    private TcpListener? _listener;

    public StudioServer(StudioSession session)
    {
        Session = session;
        Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    public StudioSession Session { get; }
    public string Secret { get; }
    public int Port { get; private set; }

    /// <summary>The address all pages and calls are under (ends with a slash).</summary>
    public string Base => $"http://127.0.0.1:{Port}/s/{Secret}/";

    public string ModellerUrl => Base + "modeller";

    /// <summary>The Viewer page showing one results file (.ppr).</summary>
    public string ViewerUrl(string resultsPath)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(resultsPath).ToLowerInvariant())))[..16].ToLowerInvariant();
        _results[key] = Path.GetFullPath(resultsPath);
        return Base + "viewer?pack=results/" + key + "&name=" + Uri.EscapeDataString(Path.GetFileName(resultsPath));
    }

    public void Start(int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoop(listener, _stop.Token));
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (Exception) { }
        try { _listener?.Stop(); } catch (Exception) { }
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            catch (Exception)
            {
                try { await Task.Delay(500, stop).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            _ = Task.Run(() => ServeAsync(client, stop));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint { } remote || !IPAddress.IsLoopback(remote.Address)) return;
                var stream = client.GetStream();
                HttpRequest? request;
                using (var head = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    head.CancelAfter(30000);
                    try { request = await HttpRequest.ReadAsync(stream, head.Token).ConfigureAwait(false); }
                    catch (HttpRequest.Refused refused) { await Write(stream, Json(refused.Status, new JsonObject { ["error"] = refused.Message })).ConfigureAwait(false); return; }
                }
                if (request is null) return;
                byte[] answer;
                try { answer = Route(request); }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or FileNotFoundException or System.Text.Json.JsonException or ArgumentException or FormatException)
                {
                    answer = Json(ex is InvalidOperationException && ex.Message.StartsWith("The model was changed", StringComparison.Ordinal) ? 409 : 400, new JsonObject { ["error"] = ex.Message });
                }
                await Write(stream, answer).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException) { }
            catch (Exception) { }
        }
    }

    private static async Task Write(Stream stream, byte[] bytes) { await stream.WriteAsync(bytes).ConfigureAwait(false); await stream.FlushAsync().ConfigureAwait(false); }

    private static readonly Dictionary<string, string> NoStore = new() { ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff" };

    internal static byte[] Json(int status, JsonNode body) => HttpRequest.Response(status, Encoding.UTF8.GetBytes(body.ToJsonString()), "application/json; charset=utf-8", NoStore);

    private static byte[] Ok(JsonNode body) => Json(200, body);

    private static JsonObject Body(HttpRequest r) =>
        r.Body.Length == 0 ? new JsonObject() : JsonNode.Parse(r.Body) as JsonObject ?? throw new FormatException("The request is not a JSON object.");

    private static string? Query(HttpRequest r, string key)
    {
        foreach (var part in r.Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var k = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            if (k == key) return eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    /// <summary>One request to its answer. Internal so the tests can call it with no socket.</summary>
    internal byte[] Route(HttpRequest r)
    {
        var prefix = "/s/" + Secret + "/";
        if (!r.Path.StartsWith(prefix, StringComparison.Ordinal)) return Json(404, new JsonObject { ["error"] = "not found" });
        // A page from anywhere else (it would send its own Origin) is turned away; ours sends this server's.
        if (r.Header("Origin") is { Length: > 0 } origin && !string.Equals(origin, $"http://127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase))
            return Json(403, new JsonObject { ["error"] = "forbidden" });
        var path = r.Path[prefix.Length..];
        var s = Session;
        switch (r.Method, path)
        {
            case ("GET", "modeller" or ""):
                return HttpRequest.Response(200, Encoding.UTF8.GetBytes(StudioFiles.Page("modeller")), "text/html; charset=utf-8", NoStore);
            case ("GET", "viewer"):
                return HttpRequest.Response(200, Encoding.UTF8.GetBytes(StudioFiles.Page("viewer")), "text/html; charset=utf-8", NoStore);
            case ("GET", _) when path.StartsWith("results/", StringComparison.Ordinal):
                {
                    if (!_results.TryGetValue(path["results/".Length..], out var file) || !File.Exists(file)) return Json(404, new JsonObject { ["error"] = "That results file is not there any more." });
                    return HttpRequest.Response(200, File.ReadAllBytes(file), "application/octet-stream", NoStore);
                }
            case ("GET", "api/model"):
                {
                    var snap = s.Get();
                    var since = Query(r, "rev");
                    var same = since is not null && int.TryParse(since, out var rev) && rev == snap.Rev;
                    return Ok(new JsonObject { ["rev"] = snap.Rev, ["path"] = snap.Path, ["dirty"] = snap.Dirty, ["model"] = same ? null : snap.Model });
                }
            case ("PUT", "api/model"):
                {
                    var body = Body(r);
                    var model = body["model"] as JsonObject ?? throw new FormatException("No model was sent.");
                    int? basedOn = body["rev"] is JsonValue v && v.TryGetValue<int>(out var b) ? b : null;
                    return Ok(new JsonObject { ["rev"] = s.Put(model, basedOn) });
                }
            case ("POST", "api/save"):
                {
                    var body = Body(r);
                    var saved = s.Save(body["name"]?.ToString(), body["folder"]?.ToString());
                    return Ok(new JsonObject { ["path"] = saved, ["rev"] = s.Get().Rev });
                }
            case ("POST", "api/open"):
                {
                    var file = Body(r)["path"]?.ToString() ?? throw new FormatException("No file was named.");
                    var snap = s.Open(file);
                    return Ok(new JsonObject { ["rev"] = snap.Rev, ["path"] = snap.Path, ["model"] = snap.Model });
                }
            case ("GET", "api/models"):
                return Ok(new JsonObject
                {
                    ["folder"] = ModelFile.Folder,
                    ["models"] = new JsonArray(ModelFile.List().Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["path"] = m.Path, ["folder"] = m.Folder, ["saved"] = m.Saved.ToString("o") }).ToArray()),
                });
            case ("GET", "api/samples"):
                {
                    var o = new JsonObject();
                    foreach (var (name, model) in ModelFile.Samples()) o[name] = model;
                    return Ok(new JsonObject { ["samples"] = o });
                }
            case ("GET", "api/check"):
                {
                    var model = s.Get().Model ?? throw new InvalidOperationException("No model is open.");
                    return Ok(new JsonObject { ["findings"] = Findings(model) });
                }
            case ("POST", "api/script"):
                {
                    var model = Body(r)["model"] as JsonObject ?? s.Get().Model ?? throw new InvalidOperationException("No model is open.");
                    return Ok(new JsonObject { ["script"] = StudioFiles.Script(ModelFile.Validate((JsonObject)model.DeepClone())) });
                }
            case ("GET", "api/plaxis"):
                return Ok(new JsonObject
                {
                    ["installs"] = new JsonArray(PlaxisRun.FindInputs().Select(i => (JsonNode)new JsonObject { ["product"] = i.Product, ["name"] = i.Name, ["path"] = i.Path }).ToArray()),
                    ["python"] = s.Python() is not null,
                });
            case ("POST", "api/run"):
                {
                    var body = Body(r);
                    var run = s.StartRun(new PlaxisRun.Options(
                        Plaxis: body["plaxis"]?.ToString() is { Length: > 0 } p ? p : null,
                        StoredSteps: body["stored_steps"] is JsonValue st && st.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 1000) : 1,
                        OneByOne: body["one_by_one"]?.GetValue<bool>() ?? true,
                        KeepOpen: body["keep_open"]?.GetValue<bool>() ?? false,
                        Keep: Keep(body["keep"] as JsonObject)));
                    return Ok(run.Status());
                }
            case ("GET", "api/run"):
                return Ok(s.Run?.Status() ?? new JsonObject { ["state"] = "none" });
            case ("DELETE", "api/run"):
                s.Run?.Stop();
                return Ok(s.Run?.Status() ?? new JsonObject { ["state"] = "none" });
            case ("GET", "api/promax"):
                return Ok(ChatBridge.Read(long.TryParse(Query(r, "since"), out var seen) ? seen : 0));
            case ("POST", "api/promax"):
                {
                    var text = Body(r)["text"]?.ToString() ?? "";
                    if (text.Trim().Length == 0) throw new InvalidOperationException("Type what ProMax should do first.");
                    return Ok(new JsonObject { ["sent"] = ChatBridge.Send(text) });
                }
            case ("POST", "api/promax/focus"):
                ChatBridge.Focus();
                return Ok(new JsonObject { ["ok"] = true });
            case ("POST", "api/promax/answer"):
                {
                    var b = Body(r);
                    ChatBridge.Answer((long?)b["id"] ?? 0, (bool?)b["allow"] ?? false);
                    return Ok(new JsonObject { ["ok"] = true });
                }
            case ("POST", "api/viewer"):
                {
                    var results = Body(r)["results"]?.ToString();
                    if (string.IsNullOrEmpty(results) && s.Get().Path is { } mp) results = ModelFile.ResultsPath(mp);
                    if (string.IsNullOrEmpty(results) || !File.Exists(results)) throw new InvalidOperationException("There are no results for this model yet: calculate it first.");
                    StudioFiles.OpenWindow(ViewerUrl(results));
                    return Ok(new JsonObject { ["opened"] = results });
                }
            default:
                return Json(404, new JsonObject { ["error"] = "not found" });
        }
    }

    /// <summary>A "keep" choice as the run takes it: only known groups, and null when it keeps everything.</summary>
    internal static JsonObject? Keep(JsonObject? keep)
    {
        if (keep is null) return null;
        var phases = (keep["phases"] as JsonArray)?.Select(p => p?.ToString()).Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
        var results = (keep["results"] as JsonArray)?.Select(p => p?.ToString()).Where(p => p is not null && PlaxisRun.ResultGroups.Contains(p)).ToList() ?? [];
        if (keep["results"] is JsonArray && results.Count == 0) throw new InvalidOperationException("Keep at least one kind of result in the results file.");
        if (keep["phases"] is JsonArray && phases.Count == 0) throw new InvalidOperationException("Keep at least one phase in the results file.");
        var o = new JsonObject();
        if (phases.Count > 0) o["phases"] = new JsonArray(phases.Select(p => (JsonNode)JsonValue.Create(p!)!).ToArray());
        if (results.Count > 0 && results.Count < PlaxisRun.ResultGroups.Length) o["results"] = new JsonArray(results.Select(p => (JsonNode)JsonValue.Create(p!)!).ToArray());
        return o.Count == 0 ? null : o;
    }

    internal static JsonArray Findings(JsonObject model) =>
        new(ModelChecks.Check(model).Select(f => (JsonNode)new JsonObject { ["level"] = f.Level, ["text"] = f.Text, ["object"] = f.Object }).ToArray());
}

/// <summary>The one Modeller of this program: made when first asked for (by the shortcut or the chat), then kept.</summary>
public static class Studio
{
    private static readonly object Gate = new();
    private static StudioServer? _server;
    private static Func<string?> _python = () => null;
    private static string _worker = Path.Combine(AppContext.BaseDirectory, "plaxis_worker.py");

    /// <summary>Where Plaxis's Python is (for Calculate in Plaxis) and the worker that talks to Plaxis.</summary>
    public static void Configure(Func<string?> python, string workerScript)
    {
        _python = python;
        _worker = workerScript;
    }

    public static StudioServer Server
    {
        get
        {
            lock (Gate)
            {
                if (_server is not null) return _server;
                var server = new StudioServer(new StudioSession(() => _python(), _worker));
                server.Start();
                return _server = server;
            }
        }
    }

    public static bool Started { get { lock (Gate) return _server is not null; } }

    public static void OpenModeller() => StudioFiles.OpenWindow(Server.ModellerUrl);

    public static void Stop()
    {
        lock (Gate) { _server?.Dispose(); _server = null; }
    }
}
