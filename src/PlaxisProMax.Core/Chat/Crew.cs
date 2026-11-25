using System.IO;
using System.Text.RegularExpressions;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

namespace PlaxisProMax.Chat;

/// <summary>What the crew needs from the window that shows it (all called on the window's thread).</summary>
internal sealed class CrewHost
{
    /// <summary>A job changed: its card, the job view and the crew's line are drawn again.</summary>
    public Action Changed = () => { };
    /// <summary>A line in the main chat.</summary>
    public Action<string> Note = _ => { };
    /// <summary>True while the main chat answers (reports wait for it to finish).</summary>
    public Func<bool> MainBusy = () => false;
    /// <summary>Sends text as the main chat's next turn (the jobs' reports; quiet: a nudge that shows no note).</summary>
    public Action<string, bool> MainTurn = (_, _) => { };
    /// <summary>The chat is saved soon.</summary>
    public Action Save = () => { };
    /// <summary>Claude Code reported the plan's usage with a job's answer.</summary>
    public Action<PlanUsage> Usage = _ => { };
    /// <summary>The sign-in was lost while a job ran.</summary>
    public Action SignInLost = () => { };
    /// <summary>An item for the main chat that isn't a plain note (the plan's Approve line, the autopilot's report).</summary>
    public Action<ChatItem> AddItem = _ => { };
    /// <summary>Ends the main chat's answer now, whatever it is doing (its process is ended); the note goes in the chat.</summary>
    public Action<string> AbortMainTurn = _ => { };
    /// <summary>When the main chat's Claude Code last said anything, and whether one of its steps runs (for a stuck answer).</summary>
    public Func<DateTime> MainLastActivity = () => default;
    public Func<bool> MainStepRunning = () => false;
    /// <summary>Saves the project and copies it into a Backup folder beside it: the copy's path ("" when none).</summary>
    public Func<Task<string>> Backup = () => Task.FromResult("");
}

/// <summary>
/// The crew of one chat: its job board and the hidden Claude Code session of each running job (Sketch ProMax's
/// ChatPane.Jobs, without its window). Jobs start while fewer than the Settings' number run, what they say and do lands
/// on the board, a finished job is checked by a teammate (Settings > Chat) and its report goes back to the coordinator as
/// the main chat's next turn. Everything here runs on the window's thread (<c>ui</c> posts to it); the MCP server reaches
/// the board through <see cref="ChatJobs"/> from its own threads. Needs no WPF (the unit tests compile this file).
/// </summary>
internal sealed partial class Crew : IDisposable
{
    /// <summary>One job's run: its Claude Code process and what the run has said so far.</summary>
    private sealed class JobRun
    {
        public ChatJob Job = null!;
        public ChatEngine Engine = null!;
        /// <summary>The message the run started with (sent again from the task when its session is gone).</summary>
        public JobMessage Message = new("", []);
        public bool Resumed, Retried;
        public string LastText = "";
        public readonly Dictionary<string, ChatItem> Steps = new();
        public DateTime RunStart = DateTime.UtcNow;
        public double CostSeen;
        public readonly Dictionary<string, string> PlanInputs = new();
        public readonly AnswerTiming Timing = new(DateTime.UtcNow);
    }

    private sealed class ParkedEngine
    {
        public ChatEngine Engine = null!;
        public string Model = "", Effort = "", Address = "";
        public Timer? Timer;
    }

    internal const string JobSignInError = "Your sign-in expired. Sign in again in Settings > Claude, then send this job a message to carry on.";

    /// <summary>How long a finished job's process stays up for a message to it (a review's fix, the next part).</summary>
    internal static readonly TimeSpan ParkFor = TimeSpan.FromMinutes(10);

    /// <summary>At most this many finished jobs' processes stay up (each holds memory).</summary>
    internal const int MaxParked = 2;

    /// <summary>The longest a kept step result is.</summary>
    internal const int MaxResult = 4000;

    private readonly Action<Action> _ui;
    private readonly Conversation _conv;
    private readonly Func<ClaudeCli.Install?> _install;
    private readonly CrewHost _host;
    private readonly Dictionary<string, JobRun> _runs = new();
    private readonly Dictionary<string, ParkedEngine> _parked = new();
    private readonly List<int> _reports = [];
    private readonly HashSet<int> _shareOffered = [];
    private readonly string _chatKey;
    private Timer? _reportTimer, _clock;
    private bool _queued, _stopped;
    private double? _fiveHourSeen;
    private DateTimeOffset? _fiveHourResets;

    /// <summary>For the checks: what starts a job's process instead of Claude Code.</summary>
    internal Func<System.Diagnostics.ProcessStartInfo>? StartWith;

    /// <summary>For the checks: where the jobs' session ids are kept instead of the chat folder.</summary>
    internal static string? FolderOverride;

    public JobBoard Board { get; }

    /// <summary>The chat is With coordinator (ChatTypes): only then do jobs start.</summary>
    public bool IsCoordinator => ChatTypes.Of(_conv) == ChatTypes.Coordinator;

    /// <summary>True while any of the chat's jobs runs or waits to start.</summary>
    public bool JobsOpen => Board.OpenCount > 0 || _runs.Count > 0;

    /// <summary>How many jobs have a process at work now.</summary>
    public int Running => _runs.Count;

    public static string JobsFolder(Conversation conv) => Path.Combine(FolderOverride ?? ClaudeCli.Folder, "jobs", conv.Id.Length > 0 ? conv.Id : "chat");

    /// <summary>
    /// The crew of <paramref name="conv"/>, reachable by the MCP server from now on. A chat opened from disk has no job
    /// running (those saved running are stopped, JobBoard.AfterReload).
    /// </summary>
    public Crew(Action<Action> ui, Conversation conv, Func<ClaudeCli.Install?> install, CrewHost host)
    {
        _ui = ui;
        _conv = conv;
        _install = install;
        _host = host;
        conv.Jobs ??= [];
        conv.Questions ??= [];
        conv.Type = ChatTypes.Of(conv);
        Board = new JobBoard(conv.Jobs, conv.Questions);
        Board.AfterReload(DateTime.UtcNow);
        _chatKey = JobBoard.ChatKey(conv.Id);
        Hook();
        HookAutopilot();
    }

    /// <summary>The MCP server reaches this chat's jobs (again after the chat's kind changed).</summary>
    public void Hook()
    {
        ChatJobs.Board = Board;
        ChatJobs.Coordinator = IsCoordinator;
        ChatJobs.Shown = _chatKey;
        ChatJobs.Changed = () => _ui(JobsChanged);
        ChatJobs.Boards.Set(_chatKey, Board, IsCoordinator, () => _ui(JobsChanged));
    }

    /// <summary>The chat is left or the window closes: every job's process ends, and the MCP server no longer reaches the board.</summary>
    public void Dispose()
    {
        if (_stopped) return;
        EndRuns("The chat was left, so this job stopped. Open the chat again and send it a message to carry on.", now: true);
        _stopped = true;
        UnhookAutopilot();
        _reportTimer?.Dispose();
        _clock?.Dispose();
        ChatJobs.Boards.Remove(_chatKey);
        if (ReferenceEquals(ChatJobs.Board, Board))
        {
            ChatJobs.Board = null;
            ChatJobs.Changed = null;
        }
    }

    // ---- Timers on the window's thread ------------------------------------------------------------------------------

    private Timer Every(TimeSpan period, Action tick) => new(_ => _ui(() => { if (!_stopped) tick(); }), null, period, period);

    private Timer Once(TimeSpan wait, Action tick) => new(_ => _ui(() => { if (!_stopped) tick(); }), null, wait, Timeout.InfiniteTimeSpan);

    // ---- Warm engines: a finished job's Claude Code stays up a while, for the fix or next part sent to it ------------

    private bool Park(JobRun run)
    {
        if (!run.Engine.IsAlive || _stopped) return false;
        DropParked(run.Job.Key);
        while (_parked.Count >= MaxParked) DropParked(_parked.First().Key);
        var parked = new ParkedEngine { Engine = run.Engine, Model = run.Engine.Model, Effort = run.Engine.Effort, Address = run.Engine.McpAddress };
        var key = run.Job.Key;
        parked.Timer = Once(ParkFor, () => { if (_parked.TryGetValue(key, out var p) && p == parked) DropParked(key); });
        _parked[key] = parked;
        return true;
    }

    private ChatEngine? TakeParked(string key, string model, string effort)
    {
        if (!_parked.TryGetValue(key, out var p)) return null;
        _parked.Remove(key);
        p.Timer?.Dispose();
        if (p.Engine.IsAlive && p.Model == model && p.Effort == effort && p.Address == ClaudeCli.McpAddress) return p.Engine;
        p.Engine.Dispose();
        return null;
    }

    private void DropParked(string key)
    {
        if (!_parked.TryGetValue(key, out var p)) return;
        _parked.Remove(key);
        p.Timer?.Dispose();
        p.Engine.Dispose();
    }

    /// <summary>Ends every running job's process (the chat is left, the window closes).</summary>
    public void EndRuns(string note, bool now = false)
    {
        foreach (var key in _parked.Keys.ToList()) DropParked(key);
        foreach (var run in _runs.Values.ToList())
        {
            Board.Stop(run.Job.Number, DateTime.UtcNow);
            if (note.Length > 0) Board.AddNote(run.Job, note, DateTime.UtcNow);
            if (now) run.Engine.KillNow();
            else run.Engine.Kill();
        }
        _runs.Clear();
        _clock?.Dispose();
        _clock = null;
    }

    // ---- Starting and stopping --------------------------------------------------------------------------------------

    /// <summary>Something changed (a chat_jobs call, a turn waiting for Plaxis, Stop): start, stop and redraw, once per moment.</summary>
    public void JobsChanged()
    {
        if (_queued || _stopped) return;
        _queued = true;
        _ui(() =>
        {
            _queued = false;
            if (_stopped) return;
            // Stopped from the main chat or a card: its process ends now.
            foreach (var run in _runs.Values.Where(r => r.Job.State == JobBoard.Stopped).ToList()) EndRun(run, kill: true);
            PumpJobs();
            _host.Changed();
            _host.Save();
        });
    }

    /// <summary>Starts the jobs waiting to start, while fewer than the Settings' number run (never in a Simple chat).</summary>
    public void PumpJobs()
    {
        if (_stopped || _install() is null || !IsCoordinator || JobPause.On) return;
        if (Run is { State: AutopilotRun.Paused }) return; // an autopilot paused at a usage limit starts nothing new
        if (Run is { State: AutopilotRun.Planning or AutopilotRun.Approval }) Board.ReadOnlyUntilStarted(); // reading before the plan is approved
        foreach (var (job, message) in Board.ToStart(ChatJobs.Limit, DateTime.UtcNow)) _ = StartRun(job, message);
        if (_runs.Count > 0) _clock ??= Every(TimeSpan.FromSeconds(30), WatchJobs);
    }

    /// <summary>The stream-json line for a job's message: its text, and its attached files by path (the job reads them).</summary>
    internal static string JobLine(JobMessage message)
    {
        var files = message.Files.Where(File.Exists).ToList();
        var text = files.Count == 0 ? message.Text : message.Text + "\n\nAttached files:\n" + string.Join("\n", files.Select(f => "- " + f));
        return StreamInput.UserMessage(text, []);
    }

    private async Task StartRun(ChatJob job, JobMessage message, bool fresh = false)
    {
        var folder = JobsFolder(_conv);
        JobPause.TookRefusal(job.Key); // a refusal from an earlier run doesn't count for this one
        var resume = job.HasSession && !fresh ? JobSessions.Get(folder, job.Key) : null;
        if (job.HasSession && resume is null) message = Board.Restart(job, message);
        string? session = null;
        if (resume is null)
        {
            // A new session: it gets the chat's agreed rules with its task, the same rules its check judges by.
            if (job.ReviewOf == 0 && JobReview.RulesForJob(_conv.Recall) is { Length: > 0 } rules) message = message with { Text = message.Text + rules };
            session = Guid.NewGuid().ToString();
            JobSessions.Set(folder, job.Key, session);
            job.HasSession = true;
        }
        if (job.Model.Length == 0) ChatJobs.Assign(job, null);
        var install = _install()!;
        var model = job.Model;
        var effort = ClaudeCli.EffortRefused ? "" : job.Effort;
        var key = job.Key;
        // A job that finished a few minutes ago still has its process (Park): the message goes straight in.
        var warm = resume is not null ? TakeParked(key, model, effort) : null;
        if (warm is null) DropParked(key);
        var engine = warm ?? new ChatEngine(_ui, install, resume, model, StartWith ?? (() => ClaudeCli.JobStart(install, key, resume, session, model, effort)), effort: effort, job: key);
        var run = new JobRun { Job = job, Engine = engine, Message = message, Resumed = resume is not null };
        _runs[key] = run;
        engine.Event += e => { if (_runs.TryGetValue(key, out var r) && r == run) OnJobEvent(run, e); };
        engine.Exited += tail => { if (_runs.TryGetValue(key, out var r) && r == run) OnJobExited(run, tail); };
        _host.Changed();
        try
        {
            var line = JobLine(message);
            if (warm is not null) await engine.SendAsync(line).ConfigureAwait(false);
            else await engine.StartAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ui(() =>
            {
                if (!_runs.TryGetValue(key, out var r) || r != run) return;
                if (ex is IOException && engine.WaitForExit(500)) return; // Exited says why
                EndRun(run, kill: true);
                Board.RunEnded(job, false, $"It couldn't start ({ex.Message}). Send it a message to try again.", DateTime.UtcNow);
                _host.Changed();
            });
        }
    }

    private static string Pretty(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (System.Text.Json.JsonException) { return json; }
    }

    private void OnJobEvent(JobRun run, ChatEvent e)
    {
        var now = DateTime.UtcNow;
        switch (e)
        {
            case InitEvent init:
                if (init.SessionId.Length > 0) JobSessions.Set(JobsFolder(_conv), run.Job.Key, init.SessionId);
                var server = init.Servers.FirstOrDefault(s => s.Name == ClaudeCli.ServerName);
                if (server.Name is null) Board.AddNote(run.Job, ToolsNote.Missing(inside: run.Engine.McpAddress.Length > 0, job: true), now);
                else if (server.Status is not ("connected" or "pending" or ""))
                    Board.AddNote(run.Job, $"The Plaxis tools didn't connect for this job ({server.Status}). Close and open Plaxis ProMax again, then send the job a message.", now);
                break;
            case AssistantTextEvent text when text.Text.Trim().Length > 0:
                run.LastText = text.Text.Trim();
                Board.AddItem(run.Job, new ChatItem { Kind = "assistant", Text = run.LastText }, now);
                break;
            case ToolUseEvent tool when !run.Steps.ContainsKey(tool.Id):
            {
                var name = StreamParser.ShortName(tool.Name);
                var summary = StepNames.Describe(name, tool.InputJson);
                run.Steps[tool.Id] = Board.AddItem(run.Job, new ChatItem
                {
                    Kind = "tool", Name = name, State = "running", Text = summary, Input = tool.InputJson.Length > 0 ? Pretty(tool.InputJson) : "",
                }, now);
                run.Job.Now = JobBoard.OneLine(StepNames.Line(name, summary), 120);
                run.Timing.ToolStarted(tool.Id, name, now);
                if (JobPlan.IsPlanTool(name)) run.PlanInputs[tool.Id] = tool.InputJson;
                break;
            }
            case ToolResultEvent result when run.Steps.TryGetValue(result.ToolUseId, out var step):
            {
                run.Timing.ToolEnded(result.ToolUseId, now);
                step.State = result.IsError ? "failed" : "done";
                if (result.Text.Length > 0) step.Result = result.Text.Length > MaxResult ? result.Text.Substring(0, MaxResult) + "…" : result.Text;
                // The plan moved on: the worker's progress follows.
                if (run.PlanInputs.TryGetValue(result.ToolUseId, out var planInput))
                {
                    run.PlanInputs.Remove(result.ToolUseId);
                    if (!result.IsError) Board.UpdatePlan(run.Job, step.Name, planInput, result.Text);
                }
                break;
            }
            case ResultEvent end:
                OnJobResult(run, end);
                return;
            case UsageEvent usage:
                OnUsage(usage.Usage, run.Job.Key); // a job's answers report the plan's usage too, and count to its budget
                return;
            default:
                return;
        }
        _host.Changed();
        _host.Save();
    }

    /// <summary>The plan's usage as Claude Code reported it (a job's answer, or the main chat's with no <paramref name="job"/>).</summary>
    public void OnUsage(PlanUsage reported, string? job = null)
    {
        // The rise of the 5-hour figure since the last report counts to the job that made the request (JobBudget), shared
        // with the others working meanwhile.
        var five = reported.Windows.FirstOrDefault(w => w.Key == PlanUsage.FiveHour);
        var rise = JobBudget.Rise(ref _fiveHourSeen, five?.Used, JobBudget.NewWindow(ref _fiveHourResets, five?.ResetsAt));
        if (job is not null && rise > 0) ChargeJob(job, JobBudget.ShareOf(rise, _runs.Count + (_host.MainBusy() ? 1 : 0)));
        _host.Usage(reported);
        AutopilotUsage(reported);
    }

    private void OnJobResult(JobRun run, ResultEvent end)
    {
        var now = DateTime.UtcNow;
        if (end.SessionId.Length > 0) JobSessions.Set(JobsFolder(_conv), run.Job.Key, end.SessionId);
        // The worker's numbers: this run's time, tokens and dollars.
        CrewStats.Add(new CrewRecord
        {
            Start = run.RunStart, End = now, Role = CrewRecord.Worker, Slot = run.Job.Worker, Chat = _conv.Id, Job = run.Job.Number,
            In = end.InputTokens, Out = end.OutputTokens, Usd = CrewStats.Rise(ref run.CostSeen, end.CostUsd), Ok = !end.IsError,
        });
        run.RunStart = now;
        ActivityLog.Note("answer_time", run.Timing.Line($"Job {run.Job.Number}", now, run.Engine.ThinkingMs(end.ApiMs)));
        run.Timing.Restart(now);
        var message = end.Text.Trim().Length > 0 ? end.Text.Trim() : string.Join(" ", end.Errors);
        if (end.IsError && run.Resumed && !run.Retried && message.IndexOf("No conversation found", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Restart(run);
            return;
        }
        string summary;
        if (!end.IsError) summary = message.Length > 0 ? message : run.LastText;
        else if (Regex.IsMatch(message, @"usage limit|rate limit|limit reached|out of (extra )?usage", RegexOptions.IgnoreCase))
            summary = $"Your plan's usage limit is reached: {message}. Send it a message to carry on when the limit resets.";
        else if (SignInHealth.LooksBlocked(message)) summary = SignInHealth.JobNetBlocked(message);
        else if (Autopilot.LostAccess(message))
        {
            _host.SignInLost();
            summary = JobSignInError;
        }
        else summary = (message.Length > 0 ? message : "It reported an error.") + " Send it a message to try again.";
        // It stopped because Pause all refused its call: set aside as paused, not finished, so Continue sends it on.
        // Continue already pressed meanwhile: it carries on now.
        if (!end.IsError && JobPause.TookRefusal(run.Job.Key) && Board.Pause(run.Job.Number, now))
        {
            ActivityLog.Note("job_paused_at_stop", $"job {run.Job.Number}");
            EndRun(run, kill: false, park: true);
            if (!JobPause.On)
            {
                Board.Message(run.Job.Number, JobPause.CarryOn, now);
                JobsChanged();
            }
            _host.Save();
            return;
        }
        // Messages sent while it ran: it carries on with them in the same process.
        if (Board.RunEnded(run.Job, !end.IsError, summary, now) is { } next)
        {
            run.Steps.Clear();
            run.Message = next;
            _ = run.Engine.SendAsync(JobLine(next)).ContinueWith(_ => { }, TaskScheduler.Default);
            _host.Changed();
            _host.Save();
            return;
        }
        if (end.IsError) Board.AddItem(run.Job, new ChatItem { Kind = "error", Text = summary }, now);
        if (end.IsError && Autopilot.LostAccess(message)) AutopilotLostAccess(message);
        EndRun(run, kill: false, park: !end.IsError);
        Report(run.Job);
    }

    private static string Last(string text, int max)
    {
        var line = text.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        return line.Length > max ? line.Substring(line.Length - max) : line;
    }

    private void OnJobExited(JobRun run, string tail)
    {
        if (!run.Engine.Initialized && run.Resumed && !run.Retried)
        {
            Restart(run); // its session couldn't be reopened (deleted, or from another PC)
            return;
        }
        EndRun(run, kill: false);
        if (run.Job.State == JobBoard.Stopped) return;
        if (!run.Engine.Initialized && run.Engine.Effort.Length > 0 && ChatAuto.IsEffortRefusal(tail))
        {
            // An older Claude Code without --effort: the same run again without it.
            ClaudeCli.EffortRefused = true;
            Board.AddNote(run.Job, "This Claude Code doesn't take an effort setting, so the job runs at its default effort.", DateTime.UtcNow);
            _ = StartRun(run.Job, run.Message, fresh: !run.Resumed);
            return;
        }
        var detail = Last(tail, 300);
        var lost = Autopilot.LostAccess(tail);
        if (lost) _host.SignInLost();
        var why = lost ? JobSignInError
            : SignInHealth.LooksBlocked(tail) ? SignInHealth.JobNetBlocked(tail)
            : "It stopped unexpectedly." + (detail.Length > 0 ? " " + detail : "") + " Send it a message to try again.";
        Board.RunEnded(run.Job, false, why, DateTime.UtcNow);
        Board.AddItem(run.Job, new ChatItem { Kind = "error", Text = why }, DateTime.UtcNow);
        if (lost) AutopilotLostAccess(tail);
        Report(run.Job);
    }

    /// <summary>The job's session is gone: the same run starts again from its task in a new session.</summary>
    private void Restart(JobRun run)
    {
        EndRun(run, kill: true);
        Board.AddNote(run.Job, "Its earlier session couldn't be reopened, so it starts again from its task.", DateTime.UtcNow);
        _ = StartRun(run.Job, run.Message, fresh: true);
        if (_runs.TryGetValue(run.Job.Key, out var again)) again.Retried = true;
    }

    /// <summary>The run is over (its process ends): the next waiting job may start.</summary>
    private void EndRun(JobRun run, bool kill, bool park = false)
    {
        if (_runs.TryGetValue(run.Job.Key, out var r) && r == run) _runs.Remove(run.Job.Key);
        if (kill) run.Engine.Kill();
        else if (!park || !Park(run)) run.Engine.Dispose();
        // Steps still turning when the run ended: they won't finish now.
        foreach (var step in run.Steps.Values.Where(s => s.State == "running")) step.State = "stopped";
        _host.Changed();
        _host.Save();
        if (_runs.Count == 0)
        {
            _clock?.Dispose();
            _clock = null;
        }
        _ui(PumpJobs);
    }

    /// <summary>The 5-hour limit rose with job <paramref name="key"/>'s request: counted to it, and over its budget it pauses (JobBudget).</summary>
    private void ChargeJob(string key, double rise)
    {
        double budget;
        // An autopilot's jobs have their own budget (Settings > Autopilot).
        try { budget = JobBudget.Share(Run is { Active: true } ? AgentSettings.Load().AutopilotJobBudget : AgentSettings.Load().ChatJobBudget); }
        catch (Exception) { budget = JobBudget.Share(JobBudget.DefaultPercent); }
        if (Board.Charge(key, rise, budget, DateTime.UtcNow) is not { } job) return;
        if (_runs.TryGetValue(key, out var run)) EndRun(run, kill: true);
        _host.Note($"Job {job.Number} paused: it used {JobBudget.Percent(job.Spent - job.BudgetFrom)} of your 5-hour limit, its budget (Settings > Chat). " +
                   "Send it a message to let it carry on.");
        Report(job);
    }

    // ---- Pause all / Continue ---------------------------------------------------------------------------------------

    /// <summary>Pause all: each job stops as soon as its current Plaxis step is done, and nothing new starts.</summary>
    public void PauseAll()
    {
        JobPause.Pause(JobPause.ForUser, DateTime.UtcNow);
        if (!JobPause.On) return;
        _host.Note("Pausing all jobs: each stops as soon as its current step in Plaxis is done, and nothing new starts. " +
                   "Plaxis is yours meanwhile; press Continue to carry on from where each stopped.");
        ActivityLog.Note("jobs_paused", JobPause.ForUser);
        PauseTick();
        _host.Save();
    }

    /// <summary>Continue: every paused job picks up from where it stopped.</summary>
    public void ContinueAll()
    {
        JobPause.Continue();
        var now = DateTime.UtcNow;
        var count = 0;
        foreach (var job in Board.PausedJobs())
        {
            Board.Message(job.Number, JobPause.CarryOn, now);
            count++;
        }
        ActivityLog.Note("jobs_continued", $"by the user: {count} job(s)");
        _host.Note(count == 0 ? "Carrying on: jobs start again." : $"Carrying on: {count} job{(count == 1 ? "" : "s")} pick up from where {(count == 1 ? "it" : "they")} stopped.");
        JobsChanged();
        _host.Save();
    }

    /// <summary>While paused: a job with no call in Plaxis is set aside (its process ends; its session is kept).</summary>
    private void PauseTick()
    {
        if (!JobPause.On) return;
        var now = DateTime.UtcNow;
        var ended = false;
        foreach (var run in _runs.Values.ToList())
        {
            if (JobPause.Calling(run.Job.Key)) continue;
            EndRun(run, kill: true);
            JobPause.TookRefusal(run.Job.Key); // set aside here: its refusal is spent
            Board.Pause(run.Job.Number, now);
            ended = true;
        }
        if (!ended) return;
        _host.Changed();
        _host.Save();
    }

    // ---- Reporting back to the main chat ----------------------------------------------------------------------------

    /// <summary>A job finished (or failed): its summary goes to the main chat soon, several at once when they end together.</summary>
    private void Report(ChatJob job)
    {
        var now = DateTime.UtcNow;
        if (job.State == JobBoard.Done) job.NetRetries = 0;
        // The network blocked it (an office web filter, a dropped connection): the sign-in is fine, so it waits a little
        // and carries on by itself, a few times, instead of stopping for a sign-in that never expired.
        if (job.State == JobBoard.Failed && SignInHealth.IsNetBlocked(job.Summary) && job.NetRetries < SignInHealth.NetRetries)
        {
            job.NetRetries++;
            var wait = SignInHealth.NetRetryWait(job.NetRetries);
            var number = job.Number;
            ActivityLog.Note("job_net_retry", $"job {number} try {job.NetRetries} in {wait.TotalSeconds:0} s: {JobBoard.OneLine(job.Summary, 200)}");
            Board.AddNote(job, $"The network blocked it, so it tries again by itself in {(wait.TotalMinutes >= 1 ? $"{wait.TotalMinutes:0} min" : $"{wait.TotalSeconds:0} s")} " +
                               $"(try {job.NetRetries} of {SignInHealth.NetRetries}).", now);
            Timer? once = null;
            once = Once(wait, () =>
            {
                once?.Dispose();
                if (job.State != JobBoard.Failed) return; // sent a message meanwhile, or stopped
                Board.Message(number, SignInHealth.NetCarryOn, DateTime.UtcNow);
                JobsChanged();
            });
            _host.Changed();
            _host.Save();
            return;
        }
        // During a run nobody is there to press "try again": a job that ended with a problem is started again by itself
        // (twice at most) before it counts as failed.
        if (Run is { State: AutopilotRun.Running } && Autopilot.RetryMessage(job) is { } again)
        {
            job.AutoRetries++;
            ActivityLog.Note("job_retry", $"job {job.Number} try {job.AutoRetries}: {JobBoard.OneLine(job.Summary, 200)}");
            Board.Message(job.Number, again, now);
            JobsChanged();
            return;
        }
        // A goal reviewer's report goes to the run (GoalReview), not to the coordinator's chat.
        if (job.GoalCheck > 0)
        {
            if (!job.Open) GoalCheckEnded(job);
            return;
        }
        // A teammate's check ended: the job it checked is reported now, with the verdict.
        if (job.ReviewOf > 0)
        {
            if (Board.ReviewEnded(job, now) is { } checkedJob)
            {
                // Parts found not done: the job goes back to finish exactly those by itself, and is checked again.
                if (JobReview.SendBack(checkedJob))
                {
                    ActivityLog.Note("job_sent_back", $"job {checkedJob.Number} after check {checkedJob.Reviews}: {JobBoard.OneLine(checkedJob.Review, 200)}");
                    Board.Message(checkedJob.Number, JobReview.FixMessage(checkedJob), now);
                    JobsChanged();
                }
                else Report(checkedJob);
            }
            return;
        }
        if (job.State is not (JobBoard.Done or JobBoard.Failed) && !job.OverBudget || job.Review == JobReview.Pending) return;
        // Finished: a second worker checks the work in the project first (Settings > Chat).
        if (job.State == JobBoard.Done && JobReview.Wanted(AgentSettings.Load().ChatJobReview, Run is { Active: true }, job)
            && Board.StartReview(job.Number, now, AskedWords(job.Created), _conv.Recall) is { } check)
        {
            ChatJobs.Assign(check, ChatAuto.Everyday);
            _host.Changed();
            PumpJobs();
            _host.Save();
            return;
        }
        RoutineEnded(job);
        if (!_reports.Contains(job.Number)) _reports.Add(job.Number);
        _reportTimer?.Dispose();
        _reportTimer = Once(TimeSpan.FromSeconds(1.5), FlushReports);
    }

    /// <summary>
    /// What the user asked, in their own words, for a check (JobReview.Task): their last three messages before the job was
    /// made (its task was written from those).
    /// </summary>
    private string AskedWords(DateTime before)
    {
        var said = _conv.Items.Where(i => i.Kind == "user" && i.Text.Trim().Length > 0 && !i.Text.TrimStart().StartsWith("[", StringComparison.Ordinal)
                && (before == default || i.Time == default || i.Time <= before.AddSeconds(5)))
            .Select(i => i.Text.Trim()).ToList();
        var text = string.Join("\n", said.Skip(Math.Max(0, said.Count - 3)));
        return text.Length > 3000 ? text.Substring(text.Length - 3000) : text;
    }

    /// <summary>Sends the finished jobs' reports as the main chat's next turn, once it is free (the window calls this again when a turn ends).</summary>
    public void FlushReports()
    {
        if (_reports.Count == 0 || _stopped || _host.MainBusy()) return;
        if (Run is { State: AutopilotRun.Paused }) return; // reviewed when the run carries on
        var jobs = Board.Snapshot();
        var done = _reports.Select(n => jobs.FirstOrDefault(j => j.Number == n)).OfType<ChatJob>().Where(j => j.State is JobBoard.Done or JobBoard.Failed || j.OverBudget).ToList();
        _reports.Clear();
        if (done.Count == 0) return;
        var open = jobs.Count(j => j.Active || j.State == JobBoard.Queued);
        _host.Note(done.Count == 1 ? $"Job {done[0].Number} finished and reported back." : $"Jobs {string.Join(", ", done.Select(j => j.Number))} finished and reported back.");
        var report = JobBoard.Report(done, open);
        // An autopilot reviews the reports and carries on instead of telling the user.
        if (Run is { State: AutopilotRun.Running } run && report.LastIndexOf("Tell the user", StringComparison.Ordinal) is var cut and > 0)
        {
            report = report.Substring(0, cut) + Autopilot.Review(run);
            run.Idle = 0;
        }
        // Reading before the plan: the plan comes once every reading job has reported.
        else if (Run is { State: AutopilotRun.Planning } && report.LastIndexOf("Tell the user", StringComparison.Ordinal) is var at and > 0)
            report = report.Substring(0, at) + Autopilot.PlanReports(open);
        _host.MainTurn(report, false);
    }

    /// <summary>
    /// Every 30 s while jobs run (JobWatch): a job that wrote its report but never ended is finished with it, and one with
    /// no step for 20 minutes is stopped and reported, so the coordinator hands its work out again and nothing waits on it.
    /// </summary>
    private void WatchJobs()
    {
        PauseTick();
        ShareWithFreeWorkers();
        foreach (var run in _runs.Values.ToList())
        {
            var now = DateTime.UtcNow;
            var verdict = JobWatch.Judge(run.Job, now);
            if (verdict == JobWatch.Fine) continue;
            EndRun(run, kill: true);
            if (verdict == JobWatch.Finished)
            {
                var report = run.Job.Items.Last(i => i.Kind == "assistant").Text.Trim();
                Board.AddNote(run.Job, JobWatch.FinishedNote, now);
                Board.RunEnded(run.Job, true, report, now);
            }
            else
            {
                Board.RunEnded(run.Job, false, JobWatch.StuckReport(run.Job), now);
                Board.AddItem(run.Job, new ChatItem { Kind = "error", Text = $"No progress for {JobWatch.Stuck.TotalMinutes:0} minutes, so it was stopped." }, now);
            }
            ActivityLog.Note("job_watch", $"job {run.Job.Number} {verdict}");
            Report(run.Job);
        }
    }

    /// <summary>
    /// A worker is free while a running job has steps it hasn't started (FreeWorkers): the coordinator is told to hand
    /// some to the free worker, once per job. Not while the coordinator is answering.
    /// </summary>
    private void ShareWithFreeWorkers()
    {
        if (_host.MainBusy() || _reports.Count > 0 || _stopped || !IsCoordinator || JobPause.On) return;
        if (Run is { State: not AutopilotRun.Running and not AutopilotRun.Done and not AutopilotRun.Stopped }) return;
        var jobs = Board.Snapshot();
        if (FreeWorkers.ToShare(jobs, ChatJobs.Limit, _shareOffered) is not { } job) return;
        _shareOffered.Add(job.Number);
        _host.MainTurn(FreeWorkers.Nudge(job, FreeWorkers.Free(jobs, ChatJobs.Limit)), true);
    }

    // ---- From the window: a message to a job, Stop ------------------------------------------------------------------

    /// <summary>The user writes to job <paramref name="number"/> from its view: it carries on with it (or gets it at its next step).</summary>
    public JobBoard.Delivery SendToJob(int number, string text, IReadOnlyList<string>? files = null)
    {
        var delivery = Board.Message(number, text, DateTime.UtcNow, fromUser: true, files: files);
        JobsChanged();
        return delivery;
    }

    /// <summary>Stop on a job's card: its process ends and it is marked stopped. False when there was nothing to stop.</summary>
    public bool StopJob(int number)
    {
        if (Board.Stop(number, DateTime.UtcNow) is null) return false;
        if (_runs.Values.FirstOrDefault(r => r.Job.Number == number) is { } run) EndRun(run, kill: true);
        // A stopped check: the job it checked is reported without it.
        if (Board.Find(number) is { ReviewOf: > 0 } stopped && Board.ReviewEnded(stopped, DateTime.UtcNow) is { } checkedJob) Report(checkedJob);
        // A stopped goal reviewer: its round is judged without it (it counts as not able to tell).
        if (Board.Find(number) is { GoalCheck: > 0 } reviewer) GoalCheckEnded(reviewer);
        if (Board.Find(number) is { Routine.Length: > 0 } routine) RoutineEnded(routine);
        JobsChanged();
        return true;
    }
}
