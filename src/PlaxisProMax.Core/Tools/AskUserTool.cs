using System.Globalization;
using PlaxisProMax.Chat;

namespace PlaxisProMax.Tools;

/// <summary>
/// ask_user (from Sketch ProMax): during an autopilot, a question that truly needs the user goes to their phone, with
/// the answer the job assumes meanwhile. The job carries on with its assumption; action=wait holds for the reply up to
/// the wait in the settings (10 minutes by default), a short call at a time. The window sends the question
/// (<see cref="Sender"/>) and hands back the reply it reads from the phone (<see cref="Answer"/>).
/// </summary>
internal sealed class AskUserTool : IPlaxisTool
{
    public const string ToolName = "ask_user";

    private static readonly object Gate = new();
    private static readonly List<UserQuestion> Questions = [];
    private static readonly Dictionary<int, TaskCompletionSource<string>> Waiters = [];
    private static int _next;

    /// <summary>The window: sends the question to the phone. False when it couldn't (the tool then says nobody can be asked).</summary>
    internal static Func<UserQuestion, bool>? Sender;

    /// <summary>An answer came after the job stopped waiting: the window tells the chat.</summary>
    internal static event Action<UserQuestion>? Late;

    /// <summary>For the checks: stands in for "an autopilot is running and the phone is on".</summary>
    internal static Func<bool>? CanAskOverride;

    public string Name => ToolName;
    public string Description =>
        "Only during an autopilot, while the user is away: ask them a question on their phone when a choice truly needs them (it would " +
        "change the result a lot and the goal doesn't say). action=ask with the question and the answer you will assume. Carry on with " +
        "work that doesn't depend on it, on your assumption. action=wait with its id holds up to 45 s for the reply; call again until " +
        "the wait it names is over, then go on with your assumption and list it under your decisions. Ask rarely: one call with all " +
        "your open questions beats several. Not a project change.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["ask", "wait"], "description": "Default ask."},
      "question": {"type": "string", "description": "ask: the question, complete and short (the user reads it on a phone), with the options if there are some."},
      "assumed": {"type": "string", "description": "ask: the answer you go on with if no reply comes in time."},
      "id": {"type": "integer", "description": "wait: the question's id, from ask."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    private static int WaitMinutes
    {
        get
        {
            try { return Math.Max(0, Settings.AgentSettings.Load().AutopilotAskWaitMinutes); }
            catch (Exception) { return AskRules.DefaultWaitMinutes; }
        }
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var can = CanAskOverride?.Invoke() ?? (Autopilot.Unattended && Phone.PhoneLink.Active);
        if (!can)
            return ToolResult.Error(Autopilot.Unattended
                ? "No phone is connected and on, so nobody can be asked. Make the call a senior would, and list it under your decisions."
                : "ask_user works only during an autopilot, while the user is away. Put the question in the Questions tab with chat_questions instead (it reaches their phone too while the phone is on).");
        var wait = WaitMinutes;
        if (wait <= 0) return ToolResult.Error("Questions to the user are off (the wait is set to never). Make the call a senior would, and list it under your decisions.");
        var action = (args.OptString("action") ?? "ask").Trim().ToLowerInvariant();
        if (action == "ask")
        {
            var question = (args.OptString("question") ?? "").Trim();
            var assumed = (args.OptString("assumed") ?? "").Trim();
            if (question.Length == 0 || assumed.Length == 0) return ToolResult.Error("ask needs the question and the answer you will assume.");
            UserQuestion q;
            lock (Gate)
            {
                q = new UserQuestion { Id = ++_next, JobKey = context.Job ?? "", Asker = ChatJobs.AskerName(context.Job), Question = question, Assumed = assumed, Asked = DateTime.UtcNow, Sent = DateTime.UtcNow };
                Questions.Add(q);
                Waiters[q.Id] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (Sender?.Invoke(q) != true)
            {
                lock (Gate) { Questions.Remove(q); Waiters.Remove(q.Id); if (_next == q.Id) _next--; }
                return ToolResult.Error("The question couldn't be sent to the phone. Make the call a senior would, and list it under your decisions.");
            }
            var until = DateTime.Now.AddMinutes(wait);
            return ToolResult.Ok(new
            {
                id = q.Id,
                wait_until = until.ToString("HH:mm", CultureInfo.InvariantCulture),
                next = $"Q{q.Id} is on the user's phone. Carry on with work that doesn't depend on it, on your assumption. " +
                       $"To wait for the answer, call ask_user action=wait id={q.Id} (up to {AskRules.WaitCallSeconds} s a call) until {until:HH:mm}; then go on with your assumption.",
            });
        }
        if (action != "wait") return ToolResult.Error("action is ask or wait.");
        UserQuestion? asked;
        TaskCompletionSource<string>? waiter;
        lock (Gate)
        {
            asked = args.OptLong("id") is { } id ? Questions.FirstOrDefault(x => x.Id == id) : null;
            waiter = asked is not null && Waiters.TryGetValue(asked.Id, out var w) ? w : null;
        }
        if (asked is null || waiter is null) return ToolResult.Error("wait needs the id ask gave.");
        var over = DateTime.UtcNow >= asked.Sent.AddMinutes(wait);
        if (!waiter.Task.IsCompleted && !over)
        {
            var left = asked.Sent.AddMinutes(wait) - DateTime.UtcNow;
            var hold = TimeSpan.FromSeconds(Math.Min(AskRules.WaitCallSeconds, Math.Max(1, left.TotalSeconds)));
            try { await Task.WhenAny(waiter.Task, Task.Delay(hold, ct)).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            over = DateTime.UtcNow >= asked.Sent.AddMinutes(wait);
        }
        if (waiter.Task.IsCompleted)
        {
            lock (Gate) asked.Delivered = true;
            return ToolResult.Ok(new { id = asked.Id, answered = true, answer = asked.Answer, next = AskRules.AnswerLine(asked) });
        }
        if (over) lock (Gate) asked.Taken = true; // the job goes on with its assumption: a later answer is told to the chat
        return ToolResult.Ok(new
        {
            id = asked.Id,
            answered = false,
            next = over ? AskRules.NoReplyLine(asked, wait)
                : $"No reply yet. Call ask_user action=wait id={asked.Id} again, or carry on with other work, until {asked.Sent.AddMinutes(wait).ToLocalTime():HH:mm}.",
        });
    }

    /// <summary>The window read the user's reply from the phone.</summary>
    internal static void Answer(int id, string answer)
    {
        UserQuestion? q;
        bool late;
        lock (Gate)
        {
            q = Questions.FirstOrDefault(x => x.Id == id);
            if (q is null || q.Answer is not null) return;
            q.Answer = answer;
            q.Answered = DateTime.UtcNow;
            late = q.Taken;
            if (Waiters.TryGetValue(id, out var w)) w.TrySetResult(answer);
        }
        if (late) Late?.Invoke(q);
    }

    /// <summary>For the checks.</summary>
    internal static void Clear()
    {
        lock (Gate) { Questions.Clear(); Waiters.Clear(); _next = 0; }
    }
}
