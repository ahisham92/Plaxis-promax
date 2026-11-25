using System.Net;
using System.Text;
using System.Text.Json;
using PlaxisProMax.Chat;
using PlaxisProMax.Mcp;
using PlaxisProMax.Plaxis;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

/// <summary>
/// A job's calls as its hidden Claude Code makes them: its key in the header, its approval card named after it, its
/// change counted on its card, a check that may only read, and chat_jobs answered by who calls (the coordinator
/// starts jobs, a job may only list, ask and message).
/// </summary>
internal static class ChatMcpJobTests
{
    private sealed class Asker : IApprovalAsker
    {
        public readonly List<ApprovalRequest> Asked = [];
        public Task<ApprovalAnswer> AskAsync(ApprovalRequest request, CancellationToken ct)
        {
            lock (Asked) Asked.Add(request);
            return Task.FromResult(ApprovalAnswer.Allow);
        }
    }

    public static void Run(Action<string, bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ppm-jobs-" + Guid.NewGuid().ToString("N"));
        using var link = new PlaxisLink(TestPlaxis.WorkerStart);
        await link.ConnectAsync(10000, "secret", CancellationToken.None);
        var asker = new Asker();
        var settings = new AgentSettings { CallTimeoutSeconds = 30 };
        using var mcp = new ChatMcp(ToolRegistry.CreateDefault(), new ToolContext(link, new CodeLibrary(folder), () => settings), asker, "0.1.0");
        mcp.Start();
        var now = DateTime.UtcNow;
        var jobs = new List<ChatJob>();
        var board = new JobBoard(jobs);
        var builder = board.Start("Soil layers", "Create the boreholes BH-1 and BH-2.", now, true, out _)!;
        board.ToStart(3, now);
        board.RunEnded(builder, true, "Made.", now);
        board.MarkChanged(builder.Key);
        var checker = board.StartReview(builder.Number, now)!;
        board.Message(builder.Number, "carry on", now);
        board.ToStart(3, now);
        var before = (ChatJobs.Board, ChatJobs.Coordinator, ChatJobs.Shown);
        ChatJobs.Board = board;
        ChatJobs.Coordinator = true;
        ChatJobs.Shown = JobBoard.ChatKey("testchat0001");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var sessions = new Dictionary<string, string>();
            async Task<(string Text, bool Error)> Call(string? job, string name, object args)
            {
                async Task<JsonElement> Rpc(string method, object? @params)
                {
                    var m = new HttpRequestMessage(HttpMethod.Post, mcp.Server.Url) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params }), Encoding.UTF8, "application/json") };
                    m.Headers.TryAddWithoutValidation("Authorization", "Bearer " + mcp.Server.Token);
                    m.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                    if (job is not null) m.Headers.TryAddWithoutValidation(McpHttpServer.JobHeader, job);
                    if (sessions.TryGetValue(job ?? "", out var id)) m.Headers.TryAddWithoutValidation("Mcp-Session-Id", id);
                    var answer = await http.SendAsync(m);
                    if (answer.Headers.TryGetValues("Mcp-Session-Id", out var ids)) sessions[job ?? ""] = ids.First();
                    var text = await answer.Content.ReadAsStringAsync();
                    if (answer.Content.Headers.ContentType?.MediaType == "text/event-stream")
                        text = text.Split('\n').Where(l => l.StartsWith("data:")).Last().Substring(5).Trim();
                    return JsonDocument.Parse(text).RootElement.Clone();
                }
                if (!sessions.ContainsKey(job ?? "")) await Rpc("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } });
                var result = (await Rpc("tools/call", new { name, arguments = args })).GetProperty("result");
                return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
            }

            var made = await Call(builder.Key, "run_commands", new { commands = new[] { "point 0 0", "delete Point_1" } });
            check("job calls: a job's change asks on a card named after the job, runs, and is counted on its card",
                !made.Error && asker.Asked.Count == 1 && asker.Asked[0].Title.StartsWith("Job 1: Soil layers") && builder.ChangedModel && builder.Changes == 2 && JobBoard.ActivityOf(builder) != JobBoard.Applying,
                $"{made.Text} | {(asker.Asked.Count > 0 ? asker.Asked[0].Title : "")} | {builder.Changes}");
            var readOnly = await Call(checker.Key, "run_commands", new { commands = new[] { "point 1 1" } });
            var read = await Call(checker.Key, "status", new { });
            check("job calls: a check may read but not change the project", readOnly.Error && readOnly.Text.Contains("only reads") && !read.Error && asker.Asked.Count == 1, readOnly.Text);

            // An autopilot run: nobody is there to press Allow, so the change goes through and is kept for the report.
            var logged = new List<string>();
            void OnLogged(string kind, string text) { lock (logged) logged.Add(kind + ": " + text); }
            Autopilot.Logged += OnLogged;
            Autopilot.Here = true;
            try
            {
                var away = await Call(builder.Key, "run_commands", new { commands = new[] { "point 2 2", "delete Point_2" } });
                check("job calls: during an autopilot run a change is allowed without a card, and logged for the report",
                    !away.Error && asker.Asked.Count == 1 && logged.Count == 1 && logged[0].StartsWith(AutopilotEntry.Allowed + ": ") && logged[0].Contains("allowed while you were away"), away.Text + " | " + string.Join(" | ", logged));
            }
            finally
            {
                Autopilot.Here = false;
                Autopilot.Logged -= OnLogged;
            }

            var fromJob = await Call(builder.Key, "chat_jobs", new { action = "start", title = "More", task = "x", single = "y" });
            var listed = await Call(builder.Key, "chat_jobs", new { action = "list" });
            check("job calls: a job may list its teammates but not start jobs", fromJob.Error && jobs.Count == 2 && !listed.Error && listed.Text.Contains("Soil layers"), fromJob.Text);
            var fromMain = await Call(null, "chat_jobs", new { action = "start", title = "Wall forces per phase", task = "Read the plate forces of every phase into a CSV file.", single = "one read", @new = true });
            check("job calls: the coordinator starts a job, with its kind, model and effort picked", !fromMain.Error && jobs.Count == 3 && jobs[2] is { Title: "Wall forces per phase", Model.Length: > 0, Effort.Length: > 0 }, fromMain.Text);

            JobPause.Pause(JobPause.ForUser, DateTime.UtcNow);
            var paused = await Call(builder.Key, "status", new { });
            var main = await Call(null, "status", new { });
            check("job calls: while everything is paused a job's call is refused, the main chat's is not", paused.Error && !main.Error && JobPause.TookRefusal(builder.Key), paused.Text);
        }
        finally
        {
            JobPause.Continue();
            (ChatJobs.Board, ChatJobs.Coordinator, ChatJobs.Shown) = before;
            try { Directory.Delete(folder, true); } catch (Exception) { }
        }
    }
}
