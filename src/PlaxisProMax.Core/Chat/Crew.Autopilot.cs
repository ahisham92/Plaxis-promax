using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

namespace PlaxisProMax.Chat;

/// <summary>
/// Autopilot in a chat With coordinator (Sketch ProMax's ChatPane.Autopilot, without its window): the user types a goal,
/// the coordinator reads with read-only jobs and answers with a plan, and once the plan is approved he and the workers
/// carry on without asking anything until the goal is met, a usage limit pauses them (they carry on when it resets) or
/// the user presses Stop. A review team checks the goal in the project before the run ends. A backup copy of the project
/// is saved first. Everything here runs on the window's thread.
/// </summary>
internal sealed partial class Crew
{
    /// <summary>The name of the note that carries Approve while the plan waits for it.</summary>
    public const string ApproveNote = "autopilot-approve";

    private DateTime _autoIdleSince;
    private bool _gapsWaiting, _reviewAgain;
    private int _stalls;
    private Timer? _autoClock;
    private PlanUsage? _usage;
    private double? _weekSeen;
    private DateTimeOffset? _weekResets;

    /// <summary>The chat's run (null: not an autopilot chat).</summary>
    public AutopilotRun? Run => _conv.Autopilot;

    private string LastAnswer => _conv.Items.LastOrDefault(i => i.Kind == "assistant")?.Text ?? "";

    private void AutoNote(string text) => _host.Note(text);

    /// <summary>Something answered or allowed for the user while away: kept in the run's log for the report.</summary>
    public void AutopilotLog(string kind, string text)
    {
        if (Run is not { } run) return;
        run.Log.Add(new AutopilotEntry { Time = DateTime.UtcNow, Kind = kind, Text = text });
        if (run.Log.Count > 500) run.Log.RemoveRange(0, run.Log.Count - 500);
        _host.Save();
    }

    /// <summary>The banner follows the run, approvals are answered for the user while it works, and its clock ticks.</summary>
    private void ShowAutopilot()
    {
        var on = Run is { Active: true } && !_stopped;
        Autopilot.Here = on;
        if (on) _autoClock ??= Every(TimeSpan.FromSeconds(30), AutopilotTick);
        else
        {
            _autoClock?.Dispose();
            _autoClock = null;
        }
        _host.Changed();
    }

    private void HookAutopilot()
    {
        Autopilot.Logged += OnLogged;
        // A run saved while working (the window was closed): it waits, paused, for Resume.
        if (Run is { State: AutopilotRun.Running } left)
        {
            left.State = AutopilotRun.Paused;
            left.PausedUntil = null;
            left.PauseWhy = "Plaxis ProMax was closed while the run worked.";
        }
        if (Run is { Active: true } run) Autopilot.UserWords = Autopilot.Words(run);
        ShowAutopilot();
    }

    private void UnhookAutopilot()
    {
        Autopilot.Logged -= OnLogged;
        _autoClock?.Dispose();
        _autoClock = null;
        if (Run is { Active: true }) Autopilot.Here = false;
    }

    private void OnLogged(string kind, string text) => _ui(() => { if (!_stopped) AutopilotLog(kind, text); });

    /// <summary>The banner's line (null: no run in this chat).</summary>
    public string? Banner()
    {
        if (Run is not { } run) return null;
        var done = Board.Snapshot().Count(j => j.Number > run.JobsBefore && j.State == JobBoard.Done);
        return Autopilot.Banner(run, done, DateTimeOffset.UtcNow);
    }

    /// <summary>New autopilot chat: the chat waits for the goal.</summary>
    public void StartAutopilotChat()
    {
        _conv.Autopilot = new AutopilotRun();
        AutoNote("Autopilot: type the goal and send. The coordinator answers with a plan; once you approve it, he and the " +
                 "workers carry on without asking you anything until the goal is met, a usage limit pauses them (they carry on " +
                 "when it resets) or you press Stop. A backup copy of the project is saved first, in a Backup folder beside it.");
        _host.Save();
        ShowAutopilot();
    }

    /// <summary>
    /// Send in an autopilot chat. The text to send as the coordinator's turn instead of the user's own (the goal to plan),
    /// or null to send the message as usual; <paramref name="handled"/>: nothing more to send (the run stopped).
    /// </summary>
    public string? AutopilotSend(string text, out bool handled)
    {
        handled = false;
        if (Run is not { } run || text.Length == 0) return null;
        // "stop" typed to a running autopilot stops it now.
        if (run.Active && Autopilot.SaysStop(text))
        {
            AutopilotLog(AutopilotEntry.Asked, $"You wrote: {text}");
            AutopilotStop($"you wrote \"{text}\"");
            handled = true;
            return null;
        }
        // A run that started keeps what the user writes with its goal: every later prompt, every job and the review team see it.
        if (run.Started != default && run.State is not (AutopilotRun.Planning or AutopilotRun.Approval))
        {
            Autopilot.Heard(run, text);
            Autopilot.UserWords = Autopilot.Words(run);
            _host.Save();
        }
        // A paused run: whatever the user types carries it on, with their message as the coordinator's next turn.
        if (run.State == AutopilotRun.Paused)
        {
            AutopilotResume("You said to carry on.", turn: false);
            return null;
        }
        // A run that ended: writing in its chat starts it again.
        if (run.State is AutopilotRun.Done or AutopilotRun.Stopped && run.Started != default)
        {
            AutopilotRestart(run);
            return null;
        }
        if (run.State is not (AutopilotRun.Planning or AutopilotRun.Approval)) return null;
        run.Goal = run.State == AutopilotRun.Approval && run.Goal.Length > 0 ? run.Goal + "\nChange from the user: " + text : text;
        run.State = AutopilotRun.Planning;
        _host.Save();
        ShowAutopilot();
        return Autopilot.PlanRequest(run.Goal);
    }

    /// <summary>A coordinator turn ended: the plan waits for approval, the run carries on, or its goal is reviewed.</summary>
    public void AutopilotTurnEnded(bool stopped, bool ok)
    {
        if (Run is not { } run) return;
        if (!stopped && ok) _stalls = 0;
        var last = LastAnswer;
        if (run.State == AutopilotRun.Planning)
        {
            // Reading jobs still out: their reports bring the next turn, and the plan comes once they all reported.
            if (JobsOpen || _reports.Count > 0) return;
            if (stopped || !ok || last.Trim().Length == 0) return;
            run.Plan = last.Trim();
            run.State = AutopilotRun.Approval;
            _host.AddItem(new ChatItem { Kind = "note", Name = ApproveNote, Text = "Approve this plan to start the autopilot. To change it, type what to change and send.", Time = DateTime.UtcNow });
            _host.Save();
            ShowAutopilot();
            return;
        }
        if (run.State != AutopilotRun.Running) return;
        if (last.Contains(Autopilot.DoneMark))
        {
            // Said to be met: the review team checks it in the project and files before the run ends.
            if (!JobsOpen) AutopilotReview(last);
            return;
        }
        _autoIdleSince = DateTime.UtcNow;
        _ui(AutopilotNudgeIfIdle);
    }

    /// <summary>Nothing open, nothing to report, no final report: tell the coordinator to carry on (a few times at most).</summary>
    private void AutopilotNudgeIfIdle()
    {
        if (Run is not { State: AutopilotRun.Running } run || _host.MainBusy() || JobsOpen || _reports.Count > 0 || _stopped) return;
        run.Idle++;
        if (run.Idle > Autopilot.MaxIdle)
        {
            // The coordinator stopped short: the review team says what is still to do, and it goes back to him.
            if (GoalReview.MoreRounds(run)) AutopilotReview(LastAnswer);
            else AutopilotFinish(AutopilotRun.Stopped, "the coordinator stopped making progress without a final report");
            return;
        }
        _host.MainTurn(Autopilot.Nudge(run), false);
    }

    /// <summary>Approve on the plan: a backup copy of the project in a Backup folder beside it, then the run starts.</summary>
    public async Task ApproveAutopilot()
    {
        if (Run is not { State: AutopilotRun.Approval } run) return;
        if (_host.MainBusy())
        {
            AutoNote("Wait for the coordinator to finish answering, then approve.");
            return;
        }
        run.State = AutopilotRun.Running;
        run.Started = DateTime.UtcNow;
        run.Idle = 0;
        // The reading jobs before approval stay the run's (their findings are what it builds on), and may change now.
        run.JobsBefore = 0;
        Board.AllowChanges();
        ShowAutopilot();
        string copy;
        try { copy = await _host.Backup().ConfigureAwait(true); }
        catch (Exception) { copy = ""; }
        if (_stopped || !ReferenceEquals(Run, run) || run.State != AutopilotRun.Running) return;
        run.Backup = copy;
        Autopilot.UserWords = Autopilot.Words(run);
        AutoNote(run.Backup.Length > 0 ? $"Autopilot started. Backup copy of the project: {run.Backup}"
            : "Autopilot started. No backup copy could be saved (the project has never been saved to a file).");
        _host.Save();
        ShowAutopilot();
        _host.MainTurn(Autopilot.Approved(run), false);
    }

    /// <summary>Every 30 s: carry on after a pause whose limit has reset; nudge a run that went quiet; end a stuck answer.</summary>
    private void AutopilotTick()
    {
        if (Run is not { Active: true } run) return;
        var now = DateTimeOffset.UtcNow;
        if (Autopilot.UserWords.Length == 0) Autopilot.UserWords = Autopilot.Words(run);
        AutopilotStallCheck(run);
        if (run.State == AutopilotRun.Paused)
        {
            // Paused for the sign-in: no retry while it is known not to work; signing in again carries the run on.
            if (run.AccessLost && run.PausedUntil is { } due && due <= now && SignInHealth.Broken) run.PausedUntil = now + Autopilot.AccessRetry;
            if (run.PausedUntil is { } until && until <= now)
                AutopilotResume(run.AccessLost ? "Trying again after Plaxis ProMax lost access to your plan." : "The usage limit has reset.");
            ShowAutopilot();
            return;
        }
        if (_gapsWaiting && !_host.MainBusy() && _reports.Count == 0) SendGaps(run);
        if (!_host.MainBusy() && !JobsOpen && _reports.Count == 0 && DateTime.UtcNow - _autoIdleSince > TimeSpan.FromMinutes(2))
        {
            _autoIdleSince = DateTime.UtcNow;
            AutopilotNudgeIfIdle();
        }
        ShowAutopilot();
    }

    /// <summary>The usage changed: near a limit the run pauses (running jobs finish their step; no new ones start).</summary>
    private void AutopilotUsage(PlanUsage reported)
    {
        var week = reported.Windows.FirstOrDefault(w => w.Key == PlanUsage.Weekly);
        var rise = JobBudget.Rise(ref _weekSeen, week?.Used, JobBudget.NewWindow(ref _weekResets, week?.ResetsAt));
        if (rise > 0 && Run is { Active: true } active) active.WeekUsed += rise;
        _usage = PlanUsage.Merge(_usage, reported);
        if (Run is not { State: AutopilotRun.Running } run) return;
        AgentSettings settings;
        try { settings = AgentSettings.Load(); } catch (Exception) { settings = new AgentSettings(); }
        var (pause, until, why, weekCap) = Autopilot.ShouldPause(_usage, DateTimeOffset.UtcNow, settings.AutopilotResumeWeekly,
            Math.Min(1, Math.Max(0.1, settings.AutopilotFiveHourMax / 100.0)), JobBudget.Share(settings.AutopilotWeekShare), run.WeekUsed);
        if (!pause) return;
        run.State = AutopilotRun.Paused;
        run.PausedUntil = until;
        run.PauseWhy = why;
        run.WeekCap = weekCap;
        AutopilotLog(AutopilotEntry.Paused, why + (until is { } u ? $" Paused until {u.ToLocalTime():ddd HH:mm}." : " Paused until you press Resume or type \"continue\"."));
        AutoNote("Autopilot paused: " + why + (until is { } at ? $" It carries on at {at.ToLocalTime():ddd HH:mm}, when the limit resets." : " Press Resume, or type \"continue\", to carry on."));
        _host.Save();
        ShowAutopilot();
    }

    /// <summary>
    /// A turn or job failed because Claude Code can't use the plan: the run pauses now and says so (the tick tries again
    /// every 20 minutes). A review round cut off by it is run again after, without using a round.
    /// </summary>
    public void AutopilotLostAccess(string message)
    {
        if (Run is not { State: AutopilotRun.Running } run) return;
        run.State = AutopilotRun.Paused;
        run.AccessLost = true;
        run.PausedUntil = DateTimeOffset.UtcNow + Autopilot.AccessRetry;
        run.PauseWhy = "Plaxis ProMax can't use your Claude plan (sign in again).";
        var text = Autopilot.AccessNote(message);
        AutopilotLog(AutopilotEntry.Paused, text);
        AutoNote(text);
        _host.Save();
        ShowAutopilot();
    }

    /// <summary>Resume on the banner, a limit that reset, or the user's word: the run carries on.</summary>
    public void AutopilotResume(string why, bool turn = true)
    {
        if (Run is not { State: AutopilotRun.Paused } run) return;
        run.State = AutopilotRun.Running;
        run.AccessLost = false;
        run.PausedUntil = null;
        run.Offline = "";
        run.ModelGone = false;
        run.Idle = 0;
        // A pause for the run's share of the week: carrying on starts its share again (a new week, or the user's say).
        if (run.WeekCap) run.WeekUsed = 0;
        run.WeekCap = false;
        AutopilotLog(AutopilotEntry.Resumed, why);
        _host.Save();
        ShowAutopilot();
        PumpJobs();
        if (_reviewAgain && !Board.Snapshot().Any(j => j.GoalCheck > 0 && j.Open))
        {
            // The review round lost access part-way: it runs again in full, and the lost one doesn't count.
            _reviewAgain = false;
            run.ReviewRound = Math.Max(0, run.ReviewRound - 1);
            AutopilotReview(LastAnswer);
            return;
        }
        if (turn && !_host.MainBusy()) _host.MainTurn(Autopilot.Resume(run, why), false);
    }

    /// <summary>
    /// Every 30 s while a run works: an answer of the coordinator stuck mid-stream is ended and he is told to carry on;
    /// after a few in a row the run pauses for the user.
    /// </summary>
    private void AutopilotStallCheck(AutopilotRun run)
    {
        if (run.State != AutopilotRun.Running || !_host.MainBusy()) return;
        var now = DateTime.UtcNow;
        var last = _host.MainLastActivity();
        if (last == default || !Autopilot.Stalled(last, now, _host.MainStepRunning(), default)) return;
        var quiet = now - last;
        ActivityLog.Note("autopilot", $"coordinator answer stuck for {quiet.TotalMinutes:0} min: ended");
        _stalls++;
        if (_stalls >= Autopilot.MaxStalls)
        {
            // It keeps getting stuck: the run waits for the user instead of trying all night.
            _stalls = 0;
            run.State = AutopilotRun.Paused;
            run.PausedUntil = null;
            run.PauseWhy = Autopilot.StalledWhy;
            _host.AbortMainTurn("");
            var why = $"Autopilot paused: {Autopilot.StalledWhy}. Press Resume, or type \"continue\", to carry on, or Stop to end it.";
            AutopilotLog(AutopilotEntry.Paused, why);
            AutoNote(why);
            _host.Save();
            ShowAutopilot();
            return;
        }
        var text = Autopilot.StalledNote(quiet);
        AutopilotLog(AutopilotEntry.Note, text);
        _host.AbortMainTurn(text);
        // Reports waiting go to him first (FlushReports), and bring the next turn themselves.
        if (Run is { State: AutopilotRun.Running } still && ReferenceEquals(still, run) && !_host.MainBusy() && _reports.Count == 0)
            _host.MainTurn(Autopilot.Resume(run, "Your last answer got stuck (no word for a long time) and was ended."), false);
    }

    /// <summary>
    /// The coordinator says the goal is met (or stopped short): the review team, read-only jobs side by side, checks every
    /// item the user asked for in the project and files (GoalReview). Past the last round the run stops, listing what is
    /// still not done.
    /// </summary>
    private void AutopilotReview(string claims)
    {
        if (Run is not { State: AutopilotRun.Running } run) return;
        if (!GoalReview.MoreRounds(run))
        {
            AutopilotFinish(AutopilotRun.Stopped, $"the review team still found work not done after {run.ReviewRound} rounds, the last {run.StuckRounds} with no fewer items left");
            return;
        }
        run.ReviewRound++;
        run.Idle = 0;
        var now = DateTime.UtcNow;
        for (var part = 0; part < GoalReview.Parts.Length; part++)
        {
            var reviewer = Board.StartGoalCheck(GoalReview.Parts[part].Title, GoalReview.Task(run, part, claims), run.ReviewRound, now);
            ChatJobs.Assign(reviewer, ChatAuto.Everyday);
        }
        var text = $"The coordinator says the goal is met. The review team ({GoalReview.Parts.Length} reviewers, round {run.ReviewRound}) " +
                   "is checking every item you asked for in the project and files before the run ends; anything not done goes back to the coordinator.";
        AutoNote(text);
        AutopilotLog(AutopilotEntry.Note, text);
        _host.Save();
        PumpJobs();
        ShowAutopilot();
    }

    /// <summary>A reviewer finished: once the whole round has, the run ends (all met) or the gaps go back to the coordinator.</summary>
    private void GoalCheckEnded(ChatJob reviewer)
    {
        if (Run is not { State: AutopilotRun.Running or AutopilotRun.Paused } run || reviewer.GoalCheck != run.ReviewRound) return;
        var round = Board.Snapshot().Where(j => j.GoalCheck == run.ReviewRound).ToList();
        if (round.Any(j => j.Open)) return;
        // Cut off because the plan couldn't be used: not a verdict; the round runs again once access is back.
        if (round.Any(j => j.State == JobBoard.Failed && Autopilot.LostAccess(j.Summary)))
        {
            _reviewAgain = true;
            AutopilotLostAccess(round.First(j => Autopilot.LostAccess(j.Summary)).Summary);
            return;
        }
        var (met, gaps) = GoalReview.Combine(round);
        if (met)
        {
            run.Gaps = "";
            AutopilotLog(AutopilotEntry.Note, $"The review team checked the goal (round {run.ReviewRound}): met.");
            AutopilotFinish(AutopilotRun.Done, "");
            return;
        }
        run.Gaps = gaps;
        var count = Math.Max(1, gaps.Split('\n').Count(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)));
        GoalReview.Counted(run, count);
        AutopilotLog(AutopilotEntry.Note, $"The review team found the goal not met yet (round {run.ReviewRound}): {count} item(s) went back to the coordinator.");
        AutoNote($"The review team found {count} item(s) not done yet. They went back to the coordinator, who carries on.");
        _host.Save();
        _gapsWaiting = true;
        SendGaps(run);
    }

    /// <summary>What the review team found goes to the coordinator as soon as the chat is free (or at the next tick).</summary>
    private void SendGaps(AutopilotRun run)
    {
        if (run.State != AutopilotRun.Running || _host.MainBusy()) return;
        _gapsWaiting = false;
        run.Idle = 0;
        _host.MainTurn(GoalReview.SendBack(run, run.Gaps), false);
    }

    /// <summary>The user wrote in the chat of a run that ended: it runs again, with what they wrote as the next turn.</summary>
    private void AutopilotRestart(AutopilotRun run)
    {
        run.State = AutopilotRun.Running;
        run.Ended = default;
        run.PauseWhy = "";
        run.PausedUntil = null;
        run.Idle = 0;
        run.ReviewRound = 0;
        Autopilot.UserWords = Autopilot.Words(run);
        var text = "Autopilot started again with your message: the coordinator and workers carry on without asking you, and the review team checks the goal before it ends.";
        AutopilotLog(AutopilotEntry.Resumed, text);
        AutoNote(text);
        _host.Save();
        ShowAutopilot();
    }

    /// <summary>
    /// Stop on the banner or "stop" typed: the open jobs stop, the coordinator's answer ends now (its Claude Code is ended,
    /// also when it hangs), and the report is written.
    /// </summary>
    public void AutopilotStop(string why)
    {
        if (Run is not { Active: true } run) return;
        run.State = AutopilotRun.Stopped; // first: the stopped reviewers must not send anything back
        foreach (var job in Board.Snapshot().Where(j => j.Active || j.State == JobBoard.Queued).ToList()) StopJob(job.Number);
        _reports.Clear();
        _stalls = 0;
        _host.AbortMainTurn("");
        AutopilotFinish(AutopilotRun.Stopped, why);
    }

    private void AutopilotFinish(string state, string why)
    {
        if (Run is not { } run) return;
        run.State = state;
        run.PauseWhy = why;
        run.Ended = DateTime.UtcNow;
        Autopilot.Here = false;
        Autopilot.UserWords = "";
        _host.AddItem(new ChatItem { Kind = "assistant", Text = Autopilot.Report(run, Board.Snapshot(), DateTime.UtcNow), Time = DateTime.UtcNow });
        _host.Save();
        ShowAutopilot();
    }
}
