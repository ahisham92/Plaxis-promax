using System.Net;
using System.Text;
using System.Text.Json;
using PlaxisProMax.Licensing;
using PlaxisProMax.Mcp;
using PlaxisProMax.Plaxis;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

/// <summary>
/// The whole tool path as Claude Code uses it: MCP over HTTP on 127.0.0.1 with the token, the tool list, a call through
/// the approval card to the stand-in Plaxis, a declined call, and kept code held once and then run without asking.
/// </summary>
internal static class ChatMcpTests
{
    private sealed class Asker : IApprovalAsker
    {
        public readonly List<ApprovalRequest> Asked = [];
        public ApprovalAnswer Next = ApprovalAnswer.Allow;

        public Task<ApprovalAnswer> AskAsync(ApprovalRequest request, CancellationToken ct)
        {
            lock (Asked) Asked.Add(request);
            return Task.FromResult(Next);
        }
    }

    public static void Run(Action<string, bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ppm-chat-" + Guid.NewGuid().ToString("N"));
        using var link = new PlaxisLink(TestPlaxis.WorkerStart);
        await link.ConnectAsync(10000, "secret", CancellationToken.None);
        var library = new CodeLibrary(folder);
        var asker = new Asker();
        var settings = new AgentSettings { CallTimeoutSeconds = 30 };
        using var mcp = new ChatMcp(ToolRegistry.CreateDefault(), new ToolContext(link, library, () => settings), asker, "0.1.0");
        mcp.Start();
        var server = mcp.Server;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            string? session = null;
            async Task<JsonElement> Rpc(string method, object? @params = null, bool token = true)
            {
                var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params });
                var m = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (token) m.Headers.TryAddWithoutValidation("Authorization", "Bearer " + server.Token);
                if (session is not null) m.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
                m.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                var answer = await http.SendAsync(m);
                if (answer.StatusCode != HttpStatusCode.OK) return JsonDocument.Parse($"{{\"status\":{(int)answer.StatusCode}}}").RootElement.Clone();
                if (answer.Headers.TryGetValues("Mcp-Session-Id", out var ids)) session = ids.First();
                var text = await answer.Content.ReadAsStringAsync();
                // A tool call answers as an event stream: the last data line is the JSON-RPC answer.
                if (answer.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    text = text.Split('\n').Where(l => l.StartsWith("data:")).Last().Substring(5).Trim();
                return JsonDocument.Parse(text).RootElement.Clone();
            }
            async Task<(string Text, bool Error)> Call(string name, object args)
            {
                var r = await Rpc("tools/call", new { name, arguments = args });
                var result = r.GetProperty("result");
                return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
            }

            var refused = await Rpc("initialize", new { protocolVersion = "2025-06-18" }, token: false);
            check("chat mcp: no token is refused", refused.TryGetProperty("status", out var st) && st.GetInt32() == 401, refused.ToString());
            var init = await Rpc("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } });
            check("chat mcp: initialize names the server and says no code is kept yet",
                init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "plaxis-promax"
                && init.GetProperty("result").GetProperty("instructions").GetString()!.Contains("No kept code yet"), init.ToString());
            var tools = (await Rpc("tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            check("chat mcp: the tool list has the Plaxis tools", tools.Contains("status") && tools.Contains("create_boreholes") && tools.Contains("run_plaxis_code")
                && tools.Contains("find_kept_code") && tools.Count == ToolRegistry.CreateDefault().All.Count, string.Join(",", tools));

            // A PC without a valid key runs no tools; the owner's PC (as for the rest of these checks) runs them.
            var owner = LicenseState.Override;
            LicenseState.Override = () => new LicenseState.Status(LicenseState.Kind.Locked, "PPM-TEST-0000-ABCD", null, LicenseKey.Check.Invalid, null);
            var locked = await Call("status", new { });
            LicenseState.Override = owner;
            check("chat mcp: no tools run on a PC without a key", locked.Error && locked.Text.Contains("isn't licensed") && asker.Asked.Count == 0, locked.Text);
            var status = await Call("status", new { });
            check("chat mcp: status reaches Plaxis and asks nothing", !status.Error && status.Text.Contains("PLAXIS 2D") && asker.Asked.Count == 0, status.Text);

            asker.Next = ApprovalAnswer.Deny;
            var code = "s_o, g_o = output(g_i.Phases[-1])\nux = g_o.getresults(g_o.Phases[-1], g_o.ResultTypes.Soil.Ux, 'node')\nresult = max(ux)";
            var denied = await Call("run_plaxis_code", new { code, description = "Largest horizontal displacement of the last phase" });
            check("chat mcp: new code shows a card with the code, and a decline stops it",
                denied.Error && denied.Text.Contains("declined") && asker.Asked.Last().Details == code && !asker.Asked.Last().CanSilence, denied.Text);

            // Away mode: nothing pops up, so a step that needs the user's OK is left and listed for them.
            PlaxisProMax.Chat.Away.Reset();
            PlaxisProMax.Chat.Away.Set(true, DateTime.UtcNow);
            var asksAway = asker.Asked.Count;
            var whileAway = await Call("run_plaxis_code", new { code, description = "Largest horizontal displacement of the last phase" });
            var waited = PlaxisProMax.Chat.Away.Take();
            PlaxisProMax.Chat.Away.Reset();
            check("chat mcp: while away no card is shown; the step waits and is listed",
                whileAway.Error && whileAway.Text.Contains("is away") && asker.Asked.Count == asksAway && waited.Contains("needs your OK"), whileAway.Text + " | " + waited);

            asker.Next = ApprovalAnswer.Allow;
            await Call("calculate", new { });
            var ran = await Call("run_plaxis_code", new { code, description = "Largest horizontal displacement of the last phase" });
            check("chat mcp: allowed code runs and is kept", !ran.Error && ran.Text.Contains("\"result\":0.02") && ran.Text.Contains("\"kept_as\":\"K1\""), ran.Text);

            // Big models: one phase at a time with a save after each, and every result to one Excel file in one call.
            var slow = await Call("calculate", new { one_by_one = true, stored_steps = 1 });
            check("chat mcp: calculate one phase at a time says how each phase ended", !slow.Error && slow.Text.Contains("\"one_by_one\":[") && slow.Text.Contains("\"stored_steps\":{"), slow.Text);
            var xlsx = Path.Combine(Path.GetTempPath(), "ppm-xlsx-" + Guid.NewGuid().ToString("N"), "results.xlsx");
            var excel = await Call("results_to_excel", new { path = xlsx, results = new object[] { new { type = "Soil.Utot" }, new { type = "Plate.M2D" } } });
            var sheets = new List<string>();
            if (File.Exists(xlsx))
                using (var zip = System.IO.Compression.ZipFile.OpenRead(xlsx)) sheets = zip.Entries.Select(e => e.FullName).ToList();
            check("chat mcp: results_to_excel writes every phase's results to one workbook, with a summary",
                !excel.Error && excel.Text.Contains("\"max_abs\":42.5") && sheets.Contains("xl/workbook.xml") && sheets.Count(n => n.StartsWith("xl/worksheets/")) >= 2, excel.Text);
            check("chat mcp: results_to_excel is offered with calculate's big-model options",
                ToolRegistry.CreateDefault().All.Any(t => t is { Name: "results_to_excel", ModifiesModel: false }) && ToolRegistry.CreateDefault().All.First(t => t.Name == "calculate").PropertiesJson.Contains("one_by_one"), "");
            try { Directory.Delete(Path.GetDirectoryName(xlsx)!, true); } catch (Exception) { }

            var asksBefore = asker.Asked.Count;
            var near = code.Replace("max(ux)", "min(ux)");
            var held = await Call("run_plaxis_code", new { code = near, description = "Smallest horizontal displacement of the last phase" });
            check("chat mcp: near-repeat code is held once with the kept code, before any card",
                !held.Error && held.Text.Contains("\"held\":true") && held.Text.Contains("kept_id=K1") && asker.Asked.Count == asksBefore, held.Text);
            var second = await Call("run_plaxis_code", new { code = near, description = "Smallest horizontal displacement of the last phase", based_on = "K1" });
            check("chat mcp: adapted code (based_on) runs and replaces its entry", !second.Error && second.Text.Contains("\"kept_as\":\"K1\"") && library.All.Count == 1, second.Text);

            asksBefore = asker.Asked.Count;
            var rerun = await Call("run_plaxis_code", new { kept_id = "K1" });
            check("chat mcp: kept code run by id doesn't ask again, and says it has run more than once",
                !rerun.Error && asker.Asked.Count == asksBefore && rerun.Text.Contains("\"kept_id\":\"K1\"") && rerun.Text.Contains("run 3 times"), rerun.Text);

            var commands = await Call("run_commands", new { commands = new[] { "point 0 0", "delete Point_1" } });
            check("chat mcp: a delete command asks, naming it", asker.Asked.Last().Reason.Contains("delete Point_1") && !commands.Error, asker.Asked.Last().Reason);

            var missing = await Call("no_such_tool", new { });
            check("chat mcp: an unknown tool is an error result", missing.Error, missing.Text);

            // Every call that ran is in the activity log (for bug reports and the chats-and-bugs export); declined ones didn't run.
            var log = ActivityLog.Last(200);
            check("chat mcp: calls that ran are logged with whether they worked and how long they took",
                log.Any(l => l.Contains("\"tool\":\"status\"") && l.Contains("\"model\":\"test.p2dx\"") && l.Contains("\"ok\":true") && l.Contains("\"ms\":"))
                && log.Count(l => l.Contains("\"tool\":\"run_plaxis_code\"")) == 3, string.Join("\n", log));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
