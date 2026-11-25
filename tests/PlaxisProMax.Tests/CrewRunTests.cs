using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PlaxisProMax.Chat;
using PlaxisProMax.Tools;

/// <summary>
/// The crew at work with a stand-in Claude Code: the coordinator's chat_jobs call opens a job, the job runs in its own
/// hidden session, a teammate checks it, and the report goes back to the main chat as its next turn. A Simple chat
/// starts no jobs.
/// </summary>
internal static class CrewRunTests
{
    private const string FakeClaude = """
        import json, sys
        print(json.dumps({"type": "system", "subtype": "init", "session_id": "job-session", "model": "test", "mcp_servers": [{"name": "plaxis-promax", "status": "connected"}]}), flush=True)
        for line in sys.stdin:
            msg = json.loads(line)
            if msg.get("type") != "user":
                continue
            print(json.dumps({"type": "assistant", "message": {"id": "m1", "content": [{"type": "tool_use", "id": "t1", "name": "mcp__plaxis-promax__list_phases", "input": {}}]}}), flush=True)
            print(json.dumps({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "t1", "content": "6 phases"}]}}), flush=True)
            print(json.dumps({"type": "assistant", "message": {"id": "m2", "content": [{"type": "text", "text": "All six phases ended OK.\n**PASSED**"}]}}), flush=True)
            print(json.dumps({"type": "result", "subtype": "success", "is_error": False, "result": "All six phases ended OK.\n**PASSED**", "session_id": "job-session", "usage": {"input_tokens": 10, "output_tokens": 20}}), flush=True)
        """;

    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ppm-crew-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "fake-claude.py");
        File.WriteAllText(script, FakeClaude);
        // The window's thread: one queue, pumped by one thread.
        var queue = new BlockingCollection<Action>();
        var pump = new Thread(() => { foreach (var a in queue.GetConsumingEnumerable()) { try { a(); } catch (Exception) { } } }) { IsBackground = true };
        pump.Start();
        void Ui(Action a) { try { queue.Add(a); } catch (InvalidOperationException) { } }
        T On<T>(Func<T> f) { var done = new TaskCompletionSource<T>(); Ui(() => done.SetResult(f())); return done.Task.GetAwaiter().GetResult(); }
        bool Wait(Func<bool> done, int seconds = 30) { var until = DateTime.UtcNow.AddSeconds(seconds); while (DateTime.UtcNow < until) { if (On(done)) return true; Thread.Sleep(50); } return false; }

        var beforeFolder = Crew.FolderOverride;
        var beforeStats = CrewStats.PathOverride;
        Crew.FolderOverride = dir;
        CrewStats.PathOverride = Path.Combine(dir, "crew-stats.json");
        CrewStats.Reset();
        Crew? crew = null;
        try
        {
            var conv = ChatStore.New("plaxis:c:\\jobs\\quay wall.p2dx", "Quay wall", ChatTypes.Coordinator);
            conv.Items.Add(new ChatItem { Kind = "user", Text = "Check that every phase calculated", Time = DateTime.UtcNow });
            var turns = new List<string>();
            var notes = new List<string>();
            var host = new CrewHost { MainTurn = (text, _) => turns.Add(text), Note = notes.Add };
            crew = On(() => new Crew(Ui, conv, () => new ClaudeCli.Install("claude", "2.0.0"), host)
            {
                StartWith = () => new ProcessStartInfo(TestPlaxis.Python, "-u " + ClaudeCli.Quote(script))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                },
            });
            check("crew run: the MCP server reaches the chat's board by its key", ChatJobs.Shown == JobBoard.ChatKey(conv.Id) && ChatJobs.Board == crew.Board && ChatJobs.Coordinator, ChatJobs.Shown);

            // A job may not start jobs; the coordinator may.
            var refused = ChatJobsTool.Answer(Args("""{"action":"start","title":"Phases","task":"x","single":"one check"}"""), coordinator: false, "abcdefabcdef");
            check("crew run: a job can't start jobs", refused.IsError && conv.Jobs!.Count == 0, refused.Text);
            var started = ChatJobsTool.Answer(Args("""{"action":"start","title":"Phases ended OK","task":"Read list_phases and say how each phase ended.","done_when":"every phase is listed with how it ended","single":"one short check","kind":"simple"}"""), coordinator: true);
            check("crew run: the coordinator's chat_jobs start opens job 1", !started.IsError && conv.Jobs!.Count == 1 && conv.Jobs[0] is { Number: 1, Kind: ChatAuto.Simple }, started.Text);
            ChatJobs.ChangedFor(null);

            check("crew run: the job ran in its own session and finished with its report", Wait(() => conv.Jobs![0].State == JobBoard.Done)
                && conv.Jobs![0].Summary.Contains("All six phases ended OK") && JobSessions.Get(Crew.JobsFolder(conv), conv.Jobs[0].Key) == "job-session", conv.Jobs![0].State + " " + conv.Jobs[0].Summary);
            var items = On(() => crew.Board.ItemsOf(1));
            check("crew run: its steps and its answer are on its card", items.Any(i => i is { Kind: "tool", Name: "list_phases", State: "done", Result: "6 phases" }) && items.Any(i => i.Kind == "assistant"),
                string.Join(", ", items.Select(i => i.Kind + ":" + i.Name + ":" + i.State)));
            check("crew run: a teammate checked it, and the report went to the main chat as its next turn", Wait(() => turns.Count == 1)
                && conv.Jobs!.Count == 2 && conv.Jobs[1].ReviewOf == 1 && turns[0].Contains("Job 1") && turns[0].Contains("it passed") && notes.Any(n => n.Contains("Job 1 finished and reported back")),
                turns.Count > 0 ? turns[0] : string.Join(" | ", conv.Jobs!.Select(j => j.Number + ":" + j.State)));
            check("crew run: nothing is left running, and the worker's numbers are kept", Wait(() => !crew.JobsOpen) && CrewStats.All().Count(r => r.Role == CrewRecord.Worker && r.Out == 20) == 2, "");

            // The user writes to the finished job: it carries on (its process was kept warm).
            var delivery = On(() => crew.SendToJob(1, "Also say which phase took longest."));
            check("crew run: a message to a finished job reopens it, and it reports again", delivery == JobBoard.Delivery.Reopened && Wait(() => turns.Count == 2), delivery.ToString());

            // Pause all: nothing new starts until Continue.
            On(() => { crew.PauseAll(); return 0; });
            ChatJobsTool.Answer(Args("""{"action":"start","title":"Wall forces","task":"Read the plate forces of the last phase.","single":"one read","new":true}"""), coordinator: true);
            ChatJobs.ChangedFor(null);
            Thread.Sleep(400);
            var held = On(() => conv.Jobs!.Last().State);
            On(() => { crew.ContinueAll(); return 0; });
            check("crew run: Pause all holds new jobs back; Continue starts them", held == JobBoard.Queued && Wait(() => conv.Jobs!.Last(j => j.ReviewOf == 0).State == JobBoard.Done), held);
            Wait(() => !crew.JobsOpen);
            On(() => { crew.Dispose(); return 0; });
            check("crew run: a chat that is left is no longer reached", ChatJobs.Board is null && ChatJobs.Boards.For(JobBoard.ChatKey(conv.Id), "") is null, "");

            // A Simple chat starts no jobs.
            var simple = ChatStore.New("plaxis:unsaved", "Unsaved", ChatTypes.Simple);
            var quiet = On(() => new Crew(Ui, simple, () => new ClaudeCli.Install("claude", "2.0.0"), new CrewHost()) { StartWith = () => throw new InvalidOperationException("no job may start") });
            var no = ChatJobsTool.Answer(Args("""{"action":"start","title":"x","task":"y","single":"z"}"""), coordinator: ChatJobs.Coordinator);
            check("crew run: a Simple chat has no jobs", !ChatJobs.Coordinator && no.IsError && simple.Jobs!.Count == 0, no.Text);
            On(() => { quiet.Dispose(); return 0; });
        }
        finally
        {
            if (crew is not null) On(() => { crew.Dispose(); return 0; });
            JobPause.Continue();
            Crew.FolderOverride = beforeFolder;
            CrewStats.PathOverride = beforeStats;
            CrewStats.Reset();
            queue.CompleteAdding();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    private static ToolArgs Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ToolArgs(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
    }
}
