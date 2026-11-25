using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PlaxisProMax.Chat;
using PlaxisProMax.Tools;

/// <summary>
/// Notes (memory_note), earlier chats (recall_chats) and routines as the chat reaches them: the tools need nothing from
/// Plaxis, notes are kept per project, and a routine whose time has come runs as a job whose outcome its history keeps.
/// </summary>
internal static class MoreTests
{
    private const string FakeClaude = """
        import json, sys
        print(json.dumps({"type": "system", "subtype": "init", "session_id": "routine-session", "model": "test", "mcp_servers": [{"name": "plaxis-promax", "status": "connected"}]}), flush=True)
        for line in sys.stdin:
            msg = json.loads(line)
            if msg.get("type") != "user":
                continue
            print(json.dumps({"type": "assistant", "message": {"id": "m1", "content": [{"type": "text", "text": "All six phases calculated; none failed."}]}}), flush=True)
            print(json.dumps({"type": "result", "subtype": "success", "is_error": False, "result": "All six phases calculated; none failed.", "session_id": "routine-session", "usage": {"input_tokens": 10, "output_tokens": 20}}), flush=True)
        """;

    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ppm-more-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var beforeNotes = MemoryNotes.FileOverride;
        var (beforeKey, beforeTitle, beforeChat) = (ChatScope.ProjectKey, ChatScope.ProjectTitle, ChatScope.ChatId);
        MemoryNotes.FileOverride = Path.Combine(dir, "memory.md");
        try
        {
            Notes(check);
            Recall(check);
            RoutineRun(dir, check);
        }
        finally
        {
            MemoryNotes.FileOverride = beforeNotes;
            ChatScope.Set(beforeKey, beforeTitle, beforeChat);
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    private static void Notes(Action<string, bool, string> check)
    {
        var registry = ToolRegistry.CreateDefault();
        check("notes: memory_note and recall_chats are offered, and neither changes the project", registry.All.Any(t => t is { Name: "memory_note", ModifiesModel: false })
            && registry.All.Any(t => t is { Name: "recall_chats", ModifiesModel: false }) && StepNames.ReadOnly("memory_note") && StepNames.ReadOnly("recall_chats"), "");

        // A project with no file yet: only global notes.
        ChatScope.Set(ChatScope.Unsaved, "Unsaved project", "c0");
        var refused = MemoryNoteTool.Answer(Args("""{"action":"remember","note":"The wall toe is at -14 m"}"""));
        check("notes: a project with no file yet keeps no notes of its own", refused.IsError && refused.Text.Contains("scope=global"), refused.Text);
        var global = MemoryNoteTool.Answer(Args("""{"action":"remember","note":"Reports in kN and m","scope":"global"}"""));
        check("notes: a global note is kept", !global.IsError && global.Text.Contains("\"added\":true") && File.ReadAllText(MemoryNotes.File).Contains("- Reports in kN and m"), global.Text);

        ChatScope.Set("plaxis:c:\\jobs\\quay wall.p2dx", "Quay wall", "c1");
        var mine = MemoryNoteTool.Answer(Args("""{"action":"remember","note":"The wall toe is at -14 m"}"""));
        check("notes: a note for the open project is kept under that project", !mine.IsError && mine.Text.Contains("for this project (Quay wall)")
            && File.ReadAllText(MemoryNotes.File).Contains("## Project: plaxis:c:\\jobs\\quay wall.p2dx"), mine.Text + "\n" + File.ReadAllText(MemoryNotes.File));
        var block = MemoryNotes.PromptFor(ChatScope.NotesKey);
        check("notes: a chat starts with the global notes and its project's", block.Contains("Reports in kN and m") && block.Contains("For this project only:") && block.Contains("The wall toe is at -14 m"), block);

        ChatScope.Set("plaxis:c:\\jobs\\tunnel.p3dx", "Tunnel", "c2");
        var other = MemoryNotes.PromptFor(ChatScope.NotesKey);
        check("notes: another project's chat doesn't get them", other.Contains("Reports in kN and m") && !other.Contains("-14 m"), other);

        ChatScope.Set("plaxis:c:\\jobs\\quay wall.p2dx", "Quay wall", "c1");
        var corrected = MemoryNoteTool.Answer(Args("""{"action":"remember","note":"The wall toe is at -15.5 m","replaces":"wall toe"}"""));
        var listed = MemoryNoteTool.Answer(Args("""{"action":"list"}"""));
        check("notes: a corrected note takes the place of the earlier one", !corrected.IsError && listed.Text.Contains("-15.5 m") && !listed.Text.Contains("-14 m") && listed.Text.Contains("Reports in kN and m"), listed.Text);
        var forgot = MemoryNoteTool.Answer(Args("""{"action":"forget","note":"wall toe"}"""));
        var none = MemoryNoteTool.Answer(Args("""{"action":"forget","note":"anchors"}"""));
        check("notes: forget takes one note out by its words, and says when none matches", !forgot.IsError && forgot.Text.Contains("-15.5 m") && none.IsError && none.Text.Contains("No note"), forgot.Text + " | " + none.Text);
    }

    private static void Recall(Action<string, bool, string> check)
    {
        var search = RecallChatsTool.Answer(Args("""{"action":"search","query":"zzqx never said"}"""));
        check("earlier chats: a search with no match says so", !search.IsError && search.Text.Contains("No earlier chat matches"), search.Text);
        var open = RecallChatsTool.Answer(Args("""{"action":"open","chat":"nope"}"""));
        check("earlier chats: an unknown chat id is refused with what to do", open.IsError && open.Text.Contains("action=search"), open.Text);
        var list = RecallChatsTool.Answer(Args("""{"action":"list"}"""));
        check("earlier chats: list names the open project", !list.IsError && list.Text.Contains("\"project\":\"Quay wall\""), list.Text);
    }

    private static void RoutineRun(string dir, Action<string, bool, string> check)
    {
        var at = new Routine { Name = "Nightly calculation", Task = "Calculate every phase.", Model = "Quay wall.p2dx" };
        check("routines: one for a project runs on that project only, whatever its extension",
            Crew.RoutineFits(at, "Quay wall") && Crew.RoutineFits(at, "quay wall.p2dx") && !Crew.RoutineFits(at, "Tunnel") && Crew.RoutineFits(new Routine { Name = "x", Task = "y" }, "Tunnel"), "");

        var script = Path.Combine(dir, "fake-claude.py");
        File.WriteAllText(script, FakeClaude);
        var queue = new BlockingCollection<Action>();
        var pump = new Thread(() => { foreach (var a in queue.GetConsumingEnumerable()) { try { a(); } catch (Exception) { } } }) { IsBackground = true };
        pump.Start();
        void Ui(Action a) { try { queue.Add(a); } catch (InvalidOperationException) { } }
        T On<T>(Func<T> f) { var done = new TaskCompletionSource<T>(); Ui(() => { try { done.SetResult(f()); } catch (Exception ex) { done.SetException(ex); } }); return done.Task.GetAwaiter().GetResult(); }
        bool Wait(Func<bool> done, int seconds = 30) { var until = DateTime.UtcNow.AddSeconds(seconds); while (DateTime.UtcNow < until) { if (On(done)) return true; Thread.Sleep(50); } return false; }

        var beforeFolder = Crew.FolderOverride;
        var beforeStats = CrewStats.PathOverride;
        Crew.FolderOverride = dir;
        CrewStats.PathOverride = Path.Combine(dir, "crew-stats.json");
        CrewStats.Reset();
        Crew? crew = null;
        try
        {
            var now = DateTime.Now;
            var routine = new Routine { Id = "r1", Name = "Nightly calculation", Task = "Calculate every phase and say how each ended.", Time = now.AddMinutes(-2).ToString("HH:mm"), Created = now.AddDays(-2).ToUniversalTime() };
            var list = new List<Routine> { routine };
            Routines.Save(dir, list);

            var notes = new List<string>();
            var host = new CrewHost { Note = notes.Add };
            Crew Make(Conversation c) => new(Ui, c, () => new ClaudeCli.Install("claude", "2.0.0"), host)
            {
                StartWith = () => new ProcessStartInfo(TestPlaxis.Python, "-u " + ClaudeCli.Quote(script))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                },
            };
            var simple = ChatStore.New("plaxis:c:\\jobs\\quay wall.p2dx", "Quay wall", ChatTypes.Simple);
            var plain = On(() => Make(simple));
            check("routines: a Simple chat doesn't take a routine", !On(() => plain.RoutineTick(list, now, "Quay wall")) && routine.Runs.Count == 0, "");
            On(() => { plain.Dispose(); return 0; });

            var conv = ChatStore.New("plaxis:c:\\jobs\\quay wall.p2dx", "Quay wall", ChatTypes.Coordinator);
            crew = On(() => Make(conv));
            var c = crew;
            var started = On(() => c.RoutineTick(list, now, "Quay wall"));
            if (started) Routines.Save(dir, list);
            check("routines: one whose time has come starts as a job of the coordinator's chat", started && conv.Jobs!.Count == 1 && conv.Jobs[0].Routine == routine.Id && conv.Jobs[0].Title == "Routine: Nightly calculation"
                && routine.Runs.Count == 1 && notes.Any(n => n.Contains("Routine \"Nightly calculation\" started as job 1")), string.Join(" | ", notes));
            check("routines: it doesn't start twice for the same time", !On(() => c.RoutineTick(list, now, "Quay wall")) && conv.Jobs!.Count(j => j.Routine.Length > 0) == 1, "");
            var ended = Wait(() => conv.Jobs![0].State == JobBoard.Done) && Wait(() => !c.JobsOpen);
            On(() => c.RoutineSaved).GetAwaiter().GetResult();
            var saved = Routines.Load(dir).First().Runs.FirstOrDefault();
            check("routines: its history keeps how the run ended", ended && saved is not null && saved.State == JobBoard.Done && saved.Summary.Contains("All six phases calculated"), saved is null ? "no run" : saved.State + " " + saved.Summary);
        }
        finally
        {
            if (crew is not null) On(() => { crew.Dispose(); return 0; });
            Crew.FolderOverride = beforeFolder;
            CrewStats.PathOverride = beforeStats;
            CrewStats.Reset();
            queue.CompleteAdding();
        }
    }

    private static ToolArgs Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ToolArgs(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
    }
}
