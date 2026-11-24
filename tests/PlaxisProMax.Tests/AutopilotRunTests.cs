using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using PlaxisProMax.Chat;
using PlaxisProMax.Plaxis;

/// <summary>
/// An autopilot run from goal to report with a stand-in Claude Code: the goal becomes a request for a plan, the plan
/// waits for Approve, a backup copy is saved, the coordinator's "done" brings the review team, and their verdict ends
/// the run. Also Stop, a lost sign-in, and the backup copy of a Plaxis project.
/// </summary>
internal static class AutopilotRunTests
{
    private const string FakeReviewer = """
        import json, sys
        print(json.dumps({"type": "system", "subtype": "init", "session_id": "review-session", "model": "test", "mcp_servers": [{"name": "plaxis-promax", "status": "connected"}]}), flush=True)
        for line in sys.stdin:
            msg = json.loads(line)
            if msg.get("type") != "user":
                continue
            print(json.dumps({"type": "assistant", "message": {"id": "m1", "content": [{"type": "text", "text": "Checked 4 items in the project.\nGOAL MET"}]}}), flush=True)
            print(json.dumps({"type": "result", "subtype": "success", "is_error": False, "result": "Checked 4 items in the project.\nGOAL MET", "session_id": "review-session", "usage": {"input_tokens": 10, "output_tokens": 20}}), flush=True)
        """;

    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ppm-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "fake-claude.py");
        File.WriteAllText(script, FakeReviewer);
        Backup(dir, check);

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
            var conv = ChatStore.New("plaxis:c:\\jobs\\quay wall.p2dx", "Quay wall", ChatTypes.Coordinator);
            var turns = new List<string>();
            var notes = new List<string>();
            var aborted = 0;
            var host = new CrewHost
            {
                MainTurn = (text, _) => turns.Add(text), Note = notes.Add, AddItem = conv.Items.Add, AbortMainTurn = _ => aborted++,
                Backup = () => Task.FromResult(@"C:\Jobs\Backup\Quay wall 2026-11-25 010000\Quay wall.p2dx"),
            };
            Crew Make(Conversation c) => new(Ui, c, () => new ClaudeCli.Install("claude", "2.0.0"), host)
            {
                StartWith = () => new ProcessStartInfo(TestPlaxis.Python, "-u " + ClaudeCli.Quote(script))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                },
            };
            crew = On(() => Make(conv));
            var c = crew;
            void Answer(string text) => On(() => { conv.Items.Add(new ChatItem { Kind = "assistant", Text = text, Time = DateTime.UtcNow }); c.AutopilotTurnEnded(stopped: false, ok: true); return 0; });

            check("autopilot run: a chat without a run has no banner and sends as usual", On(() => c.Banner() is null && c.AutopilotSend("hello", out var h) is null && !h), "");
            On(() => { c.StartAutopilotChat(); return 0; });
            check("autopilot run: a new autopilot chat waits for the goal and says how it works", conv.Autopilot is { State: AutopilotRun.Planning } && notes.Any(n => n.Contains("type the goal")) && On(() => c.Banner())!.Contains("type the goal"), string.Join(" | ", notes));

            var plan = On(() => c.AutopilotSend("Build the quay wall model and run all six phases", out _));
            check("autopilot run: the goal goes to the coordinator as a request for a plan", plan is not null && plan.Contains("Build the quay wall model and run all six phases") && conv.Autopilot!.State == AutopilotRun.Planning, plan ?? "null");
            Answer("Plan: 1. soil 2. wall 3. phases 4. calculate.\n## When the goal is met\nEvery phase ended OK.");
            check("autopilot run: the plan waits for Approve", conv.Autopilot!.State == AutopilotRun.Approval && conv.Autopilot.Plan.StartsWith("Plan: 1.") && conv.Items.Last() is { Kind: "note", Name: Crew.ApproveNote }, conv.Autopilot.State);
            check("autopilot run: nothing is allowed for the user before the plan is approved", !Autopilot.Here && !Autopilot.Unattended, "");

            var changed = On(() => c.AutopilotSend("Also two anchor rows", out _));
            check("autopilot run: a change typed before approving goes back for a new plan, with the goal", changed is not null && changed.Contains("Change from the user: Also two anchor rows") && conv.Autopilot.State == AutopilotRun.Planning, changed ?? "null");
            Answer("Plan: 1. soil 2. wall and anchors 3. phases 4. calculate.");

            On(() => c.ApproveAutopilot()).GetAwaiter().GetResult();
            check("autopilot run: Approve saves the backup copy and starts the run", conv.Autopilot is { State: AutopilotRun.Running } && conv.Autopilot.Backup.EndsWith("Quay wall.p2dx") && conv.Autopilot.Started != default
                && notes.Any(n => n.StartsWith("Autopilot started. Backup copy of the project:")) && turns.Count == 1, conv.Autopilot.State + " " + string.Join(" | ", notes));
            check("autopilot run: while it works, changes are allowed for the user and the jobs hear the goal", Autopilot.Here && Autopilot.Unattended && Autopilot.UserWords.Contains("Build the quay wall model")
                && On(() => c.Banner())!.Contains("working since"), Autopilot.UserWords);

            // An answer without the final report, and nothing open: the coordinator is told to carry on.
            Answer("The soil is in.");
            check("autopilot run: an answer that isn't the final report is nudged on", Wait(() => turns.Count == 2) && conv.Autopilot.Idle == 1, turns.Count.ToString());

            // What the user writes during the run is kept with the goal.
            var during = On(() => c.AutopilotSend("Use HS small for the sand", out var h) is null && !h);
            check("autopilot run: what the user writes during the run is kept with the goal", during && Autopilot.UserWords.Contains("Use HS small for the sand"), Autopilot.UserWords);

            // The coordinator says the goal is met: the review team checks it, and their verdict ends the run.
            Answer("All six phases ended OK and the report is written.\n" + Autopilot.DoneMark);
            check("autopilot run: \"done\" brings the review team, read-only, one reviewer per part", On(() => conv.Jobs!.Count(j => j.GoalCheck == 1)) == GoalReview.Parts.Length
                && conv.Autopilot.ReviewRound == 1 && notes.Any(n => n.Contains("review team")), string.Join(" | ", conv.Jobs!.Select(j => j.Number + ":" + j.Title)));
            check("autopilot run: the review team found the goal met, and the run ended with its report", Wait(() => conv.Autopilot.State == AutopilotRun.Done)
                && conv.Items.Last() is { Kind: "assistant" } report && report.Text.Contains("Autopilot report") && conv.Autopilot.Ended != default, conv.Autopilot.State + " " + conv.Items.Last().Text);
            check("autopilot run: after it ended nothing is allowed for the user any more", !Autopilot.Here && Autopilot.UserWords.Length == 0 && On(() => c.Banner())!.Contains("goal is met"), "");
            check("autopilot run: what was done for the user is in the run's log", conv.Autopilot.Log.Any(e => e.Text.Contains("review team")), string.Join(" | ", conv.Autopilot.Log.Select(e => e.Text)));
            Wait(() => !c.JobsOpen);

            // Writing in the chat of a run that ended starts it again.
            var again = On(() => c.AutopilotSend("Add a seventh phase: surcharge 20 kPa", out var h) is null && !h);
            check("autopilot run: a message after the end starts the run again", again && conv.Autopilot.State == AutopilotRun.Running && Autopilot.Here && conv.Autopilot.ReviewRound == 0, conv.Autopilot.State);

            // The sign-in is lost: the run pauses and says so; Resume carries it on with a turn.
            var before = turns.Count;
            On(() => { c.AutopilotLostAccess("401 authentication_error: OAuth token has expired"); return 0; });
            check("autopilot run: a lost sign-in pauses the run until it works again", conv.Autopilot is { State: AutopilotRun.Paused, AccessLost: true } && conv.Autopilot.PausedUntil is not null, conv.Autopilot.State);
            On(() => { c.AutopilotResume("You pressed Resume."); return 0; });
            check("autopilot run: Resume carries it on with a turn for the coordinator", conv.Autopilot is { State: AutopilotRun.Running, AccessLost: false } && turns.Count == before + 1 && turns.Last().Contains("You pressed Resume."), turns.Last());

            // "stop" typed ends it now.
            var stopped = On(() => { c.AutopilotSend("stop", out var h); return h; });
            check("autopilot run: \"stop\" typed ends the run, the coordinator's answer and writes the report", stopped && conv.Autopilot.State == AutopilotRun.Stopped && aborted == 1 && !Autopilot.Here
                && conv.Items.Last().Kind == "assistant", conv.Autopilot.State + " aborted " + aborted);

            // A run saved while it worked (the window was closed): it waits, paused, for Resume.
            On(() => { c.Dispose(); return 0; });
            conv.Autopilot.State = AutopilotRun.Running;
            crew = On(() => Make(conv));
            check("autopilot run: a run left working when the window closed waits, paused, for Resume", conv.Autopilot is { State: AutopilotRun.Paused, PausedUntil: null } && conv.Autopilot.PauseWhy.Contains("closed"), conv.Autopilot.PauseWhy);
        }
        finally
        {
            if (crew is not null) On(() => { crew.Dispose(); return 0; });
            Autopilot.Here = false;
            Autopilot.UserWords = "";
            Crew.FolderOverride = beforeFolder;
            CrewStats.PathOverride = beforeStats;
            CrewStats.Reset();
            queue.CompleteAdding();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    private static void Backup(string dir, Action<string, bool, string> check)
    {
        var project = Path.Combine(dir, "job", "Quay wall.p2dx");
        Directory.CreateDirectory(ProjectBackup.DataFolder(project));
        File.WriteAllText(project, "project");
        File.WriteAllText(Path.Combine(ProjectBackup.DataFolder(project), "data.h5"), "data");
        Directory.CreateDirectory(Path.Combine(ProjectBackup.DataFolder(project), "mesh"));
        File.WriteAllText(Path.Combine(ProjectBackup.DataFolder(project), "mesh", "mesh.bin"), "mesh");
        var at = new DateTime(2026, 11, 25, 1, 0, 0);
        var copy = ProjectBackup.Copy(project, at);
        check("backup: the project's file and its data folder are copied into Backup beside it", copy == Path.Combine(dir, "job", "Backup", "Quay wall 2026-11-25 010000", "Quay wall.p2dx")
            && File.ReadAllText(copy) == "project" && File.ReadAllText(Path.Combine(copy + "dat", "mesh", "mesh.bin")) == "mesh" && File.Exists(Path.Combine(copy + "dat", "data.h5")), copy);
        for (var i = 1; i <= ProjectBackup.Keep + 1; i++) ProjectBackup.Copy(project, at.AddMinutes(i));
        var kept = Directory.GetDirectories(Path.Combine(dir, "job", "Backup"));
        check("backup: only the newest few backups of a project are kept", kept.Length == ProjectBackup.Keep && !Directory.Exists(Path.GetDirectoryName(copy)) && File.Exists(project), kept.Length.ToString());
        var missing = false;
        try { ProjectBackup.Copy(Path.Combine(dir, "job", "none.p2dx"), at); } catch (FileNotFoundException) { missing = true; }
        check("backup: a project that was never saved to a file is not copied", missing, "");
    }
}
