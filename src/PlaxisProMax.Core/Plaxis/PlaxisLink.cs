using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlaxisProMax.Chat;

namespace PlaxisProMax.Plaxis;

/// <summary>
/// The one way to Plaxis: a worker on Plaxis's own Python (plaxis_worker.py) that uses Bentley's plxscripting, reached
/// by one JSON line per request over stdin/stdout. Plays the part Sketch ProMax's RevitDispatcher played: calls take
/// turns (Plaxis's scripting server answers one at a time), and every call has a time limit. A call that runs past it
/// ends the worker, which is started again and reconnected for the next call (Sketch ProMax bugs 81, 82, 87: calls with
/// no limit held Revit for minutes and blocked every later call).
/// </summary>
internal sealed class PlaxisLink : IDisposable
{
    private readonly Func<ProcessStartInfo> _start;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly object _gate = new();
    private ChildProcess? _process;
    private long _nextId;
    private JsonObject? _connectArgs;
    private bool _disposed;

    /// <param name="start">How to start the worker: Plaxis's Python running plaxis_worker.py (<see cref="WorkerStart"/>).</param>
    public PlaxisLink(Func<ProcessStartInfo> start) => _start = start;

    /// <summary>The worker's start info: <paramref name="python"/> runs the worker script unbuffered, in isolated mode.</summary>
    public static ProcessStartInfo WorkerStart(string python, string workerScript) => new(python,
        // -I: the worker's own folder and the current folder are not on the import path, so nothing beside it is loaded
        // by accident; -u: unbuffered, each answer goes out at once. One Arguments string: ChildProcess builds the
        // Windows command line from it (ArgumentList would be dropped).
        "-I -u " + ClaudeCli.Quote(workerScript))
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        WorkingDirectory = Path.GetDirectoryName(workerScript) ?? "",
    };

    /// <summary>The last status Plaxis gave (after connect, or a status call): product, version, project.</summary>
    public JsonObject? LastStatus { get; private set; }

    /// <summary>True once connect succeeded and no later call found the connection gone.</summary>
    public bool Connected { get; private set; }

    /// <summary>Raised (on a worker thread) when Connected changes.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// Connects to Plaxis Input on this PC, or on another PC of the office network (<paramref name="host"/>: its name or
    /// address; Plaxis there must have its remote scripting server on and the ports open). Remembered, so a restarted
    /// worker reconnects by itself.
    /// </summary>
    public async Task<WorkerAnswer> ConnectAsync(int inputPort, string password, CancellationToken ct, int timeoutMs = 30000, string host = "localhost")
    {
        var args = new JsonObject { ["input_port"] = inputPort, ["password"] = password, ["address"] = string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim() };
        lock (_gate) _connectArgs = args;
        var answer = await CallAsync("connect", (JsonObject)args.DeepClone(), timeoutMs, ct).ConfigureAwait(false);
        if (answer.Ok && answer.Result is JsonObject status) LastStatus = status;
        SetConnected(answer.Ok);
        return answer;
    }

    /// <summary>
    /// One request to the worker: waits its turn, then up to <paramref name="timeoutMs"/> for the answer. Never throws for
    /// Plaxis's own errors: they come back as a failed answer with Plaxis's message.
    /// </summary>
    public async Task<WorkerAnswer> CallAsync(string op, JsonObject args, int timeoutMs, CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var process = await EnsureWorkerAsync(op != "connect", ct).ConfigureAwait(false);
            var id = Interlocked.Increment(ref _nextId);
            var line = new JsonObject { ["id"] = id, ["op"] = op, ["args"] = args }.ToJsonString();
            var reading = ReadAnswerAsync(process, id);
            try
            {
                await process.StandardInput!.WriteLineAsync(line).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                Restart();
                return WorkerAnswer.Fail("The Plaxis worker stopped. It starts again with the next call.");
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeoutMs);
            var finished = await Task.WhenAny(reading, Task.Delay(Timeout.Infinite, limit.Token)).ConfigureAwait(false);
            if (finished != reading)
            {
                // The worker may be stuck inside a Plaxis call: end it, so the next call starts clean.
                Restart();
                return ct.IsCancellationRequested
                    ? WorkerAnswer.Fail("Stopped.")
                    : WorkerAnswer.Fail($"Plaxis didn't answer within {timeoutMs / 1000} s, so the call was stopped. Plaxis may still be busy with it; check Plaxis before trying again.");
            }
            var answer = await reading.ConfigureAwait(false);
            if (!answer.Ok && answer.Error.StartsWith("Lost the connection to Plaxis", StringComparison.Ordinal)) SetConnected(false);
            else if (answer.Ok && op is "status" or "connect") { LastStatus = answer.Result as JsonObject; SetConnected(true); }
            return answer;
        }
        finally { _turn.Release(); }
    }

    private void SetConnected(bool connected)
    {
        if (Connected == connected) return;
        Connected = connected;
        try { ConnectionChanged?.Invoke(connected); } catch (Exception) { /* a listener's problem must not stop a call */ }
    }

    private static async Task<WorkerAnswer> ReadAnswerAsync(ChildProcess process, long id)
    {
        while (true)
        {
            var line = await process.StandardOutput!.ReadLineAsync().ConfigureAwait(false);
            if (line is null) return WorkerAnswer.Fail("The Plaxis worker stopped. " + LastErrors(process));
            if (line.Length == 0) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            if (node is not JsonObject o || o["id"] is not JsonValue v || !v.TryGetValue<long>(out var got) || got != id) continue;
            var ok = o["ok"] is JsonValue okValue && okValue.TryGetValue<bool>(out var b) && b;
            return ok ? new WorkerAnswer(true, o["result"]?.DeepClone(), "") : WorkerAnswer.Fail(o["error"]?.ToString() ?? "Plaxis refused the call.");
        }
    }

    private static string LastErrors(ChildProcess process)
    {
        try { return process.Process.HasExited ? process.StandardError!.ReadToEnd().Trim() : ""; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return ""; }
    }

    /// <summary>The running worker, or a new one that said it is ready (and reconnected, when it was connected before).</summary>
    private async Task<ChildProcess> EnsureWorkerAsync(bool reconnect, CancellationToken ct)
    {
        ChildProcess? process;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlaxisLink));
            process = _process;
        }
        if (process is not null && !HasExited(process)) return process;
        process = ChildProcess.Start(_start());
        var ready = await process.StandardOutput!.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (ready is null || !ready.Contains("\"ready\"", StringComparison.Ordinal))
        {
            var errors = LastErrors(process);
            ClaudeCli.KillTree(process.Process);
            throw new InvalidOperationException("Plaxis's Python didn't start the worker. " + errors);
        }
        lock (_gate) _process = process;
        JsonObject? connect;
        lock (_gate) connect = _connectArgs;
        if (reconnect && connect is not null)
        {
            var id = Interlocked.Increment(ref _nextId);
            await process.StandardInput!.WriteLineAsync(new JsonObject { ["id"] = id, ["op"] = "connect", ["args"] = connect.DeepClone() }.ToJsonString()).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            var answer = await ReadAnswerAsync(process, id).WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            SetConnected(answer.Ok);
        }
        return process;
    }

    private static bool HasExited(ChildProcess process)
    {
        try { return process.Process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private void Restart()
    {
        ChildProcess? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        if (process is null) return;
        ClaudeCli.KillTree(process.Process);
        process.Dispose();
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Restart();
        _turn.Dispose();
    }
}

/// <summary>The worker's answer: ok with a result, or Plaxis's (or the link's) message.</summary>
internal sealed record WorkerAnswer(bool Ok, JsonNode? Result, string Error)
{
    public static WorkerAnswer Fail(string error) => new(false, null, error);
}
