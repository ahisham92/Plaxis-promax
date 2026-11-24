using System.Text.Json.Nodes;
using PlaxisProMax.Plaxis;

/// <summary>The link to Plaxis: the worker started, connected, calls answered in turn, and a call past its limit stopped and recovered.</summary>
internal static class PlaxisLinkTests
{
    public static void Run(Action<string, bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool, string> check)
    {
        using var link = new PlaxisLink(TestPlaxis.WorkerStart);
        var changes = new List<bool>();
        link.ConnectionChanged += c => { lock (changes) changes.Add(c); };
        var bad = await link.ConnectAsync(10000, "wrong", CancellationToken.None);
        check("plaxis link: a wrong password fails with Plaxis's dialog named", !bad.Ok && bad.Error.Contains("remote scripting server") && !link.Connected, bad.Error);
        var ok = await link.ConnectAsync(10000, "secret", CancellationToken.None);
        check("plaxis link: connect gives the product and version", ok.Ok && link.Connected && link.LastStatus?["product"]?.ToString() == "PLAXIS 2D"
            && link.LastStatus?["version"]?.ToString() == "2024.2", ok.Error + ok.Result?.ToJsonString());

        var calls = Enumerable.Range(0, 5).Select(i => link.CallAsync("run_code", new JsonObject { ["code"] = $"result = {i} * 10" }, 20000, CancellationToken.None)).ToList();
        var answers = await Task.WhenAll(calls);
        check("plaxis link: calls made at once take turns and each gets its own answer",
            answers.Select(a => a.Result?["result"]?.GetValue<int>()).SequenceEqual([0, 10, 20, 30, 40]), string.Join(",", answers.Select(a => a.Result?.ToJsonString() ?? a.Error)));

        var started = DateTime.UtcNow;
        var slow = await link.CallAsync("run_commands", new JsonObject { ["commands"] = new JsonArray("slow 10") }, 1500, CancellationToken.None);
        check("plaxis link: a call past its limit is stopped and says Plaxis may still be busy",
            !slow.Ok && slow.Error.Contains("didn't answer within 1 s") && DateTime.UtcNow - started < TimeSpan.FromSeconds(6), slow.Error);
        var after = await link.CallAsync("status", new JsonObject(), 20000, CancellationToken.None);
        check("plaxis link: the next call starts a new worker, reconnected by itself", after.Ok && link.Connected, after.Error);

        using var cancel = new CancellationTokenSource(500);
        var stopped = await link.CallAsync("run_commands", new JsonObject { ["commands"] = new JsonArray("slow 10") }, 20000, cancel.Token);
        check("plaxis link: Stop ends a running call", !stopped.Ok && stopped.Error == "Stopped.", stopped.Error);
        var refused = await link.CallAsync("inspect", new JsonObject { ["path"] = "Nope" }, 20000, CancellationToken.None);
        check("plaxis link: Plaxis's own refusal comes back as a failed answer, not an exception", !refused.Ok && refused.Error.Contains("has no 'Nope'"), refused.Error);
        lock (changes)
            check("plaxis link: connection changes are reported", changes.Contains(true), string.Join(",", changes));
    }
}
