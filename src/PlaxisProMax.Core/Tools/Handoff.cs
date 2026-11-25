using System.Text.Json;
using PlaxisProMax.Chat;

namespace PlaxisProMax.Tools;

/// <summary>
/// Hand-offs ("Over to you", Sketch ProMax's Tools/Handoff over the same HandoffCore): a step only the user can do in
/// Plaxis (close a window Plaxis has open, pick something on screen, start Output's remote scripting, a licence prompt)
/// shows as a card in the chat with Continue, Skip this step and Stop, and the call waits for the answer. While the user
/// is away (Away, or an autopilot) nothing waits: the step is noted and the work carries on without it.
/// </summary>
internal static class Handoff
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HandoffRequest> Live = new(StringComparer.Ordinal);
    private static readonly List<HandoffOutcome> RecentList = [];

    /// <summary>Shows a request as a card in the open chat (the window sets it); false when no chat can show it.</summary>
    public static Func<HandoffRequest, string?, bool>? Presenter { get; set; }

    /// <summary>For the tests: answers instead of a card.</summary>
    public static Func<HandoffStep, string?, HandoffChoice>? TestResponder { get; set; }

    /// <summary>How the last hand-offs ended (newest last).</summary>
    public static IReadOnlyList<HandoffOutcome> Recent { get { lock (Gate) return RecentList.ToList(); } }

    /// <summary>The open request with this id (a card drawn again finds it), or null once it has closed.</summary>
    public static HandoffRequest? Find(string id)
    {
        lock (Gate) return Live.TryGetValue(id, out var r) ? r : null;
    }

    /// <summary>Asks the user to do <paramref name="step"/> and waits. <paramref name="jobKey"/>: the job that asks, null for the main chat.</summary>
    public static async Task<HandoffOutcome> AskAsync(HandoffStep step, string? jobKey, CancellationToken ct)
    {
        var label = step.Item ?? step.What;
        if (TestResponder is null && (Autopilot.Unattended || Away.On))
        {
            var line = "A step only you can do was left for you (" + JobBoard.OneLine(step.Text, 200) + ")";
            if (Autopilot.Unattended) Autopilot.Log(AutopilotEntry.Note, line);
            else Away.Note(line);
            return new HandoffOutcome(label, step.What, "away", false, "the user is away", null);
        }
        if (TestResponder is null && Presenter is null) return new HandoffOutcome(label, step.What, "nobody", false, "no chat is open to show it", null);
        var shownOk = true;
        void Show(HandoffRequest request)
        {
            lock (Gate) Live[request.Id] = request;
            request.Changed += r => { if (r.State == HandoffState.Closed) lock (Gate) Live.Remove(r.Id); };
            shownOk = Presenter?.Invoke(request, jobKey) ?? false;
            if (!shownOk) request.Answer(HandoffChoice.Skip);
        }
        var outcomes = await HandoffLoop.RunAsync([new HandoffItem(step, null)], Show, TestResponder,
            work => Task.FromResult(work()), () => ct.IsCancellationRequested).ConfigureAwait(false);
        var outcome = shownOk ? outcomes[0] : new HandoffOutcome(label, step.What, "nobody", false, "no chat is open to show it", null);
        lock (Gate)
        {
            RecentList.Add(outcome);
            if (RecentList.Count > 20) RecentList.RemoveAt(0);
        }
        ActivityLog.Note("handoff", $"{outcome.Choice}: {JobBoard.OneLine(step.What, 160)}");
        return outcome;
    }
}

/// <summary>
/// over_to_you: the chat hands one step to the user and waits for Continue, Skip or Stop on the "Over to you" card.
/// </summary>
internal sealed class OverToYouTool : IPlaxisTool
{
    public const string ToolName = "over_to_you";

    public string Name => ToolName;
    public string Description =>
        "Hands one step to the user that only they can do in Plaxis or on this PC, and waits: a window Plaxis has open that must be closed, " +
        "a licence or password prompt, something to pick on screen, starting Output's remote scripting server, opening a file they must choose. " +
        "The chat shows an \"Over to you\" card with what (the step, as an instruction), where (the menu or window in Plaxis) and why (what it " +
        "unblocks), and the buttons Continue, Skip this step and Stop. The result says which they pressed: continued (check that it is done, " +
        "then carry on), skipped (carry on without it and say what was left), stopped (stop the task and report). While the user is away the " +
        "step is noted for them and the call returns at once. Not for questions (chat_questions) and not for steps a tool can do. Not a project change.";
    public string PropertiesJson => """
    {
      "what": {"type": "string", "description": "The step, as one plain instruction: \"Close the Materials window in Plaxis\"."},
      "where": {"type": "string", "description": "Where in Plaxis or on the PC: the menu, window or button."},
      "why": {"type": "string", "description": "What it unblocks, in one line."}
    }
    """;
    public string[] Required => ["what"];
    public bool ModifiesModel => false;

    public async Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct)
    {
        var what = Str(args, "what").Trim();
        if (what.Length == 0) return ToolResult.Error("Give the step in 'what', as one plain instruction.");
        var step = new HandoffStep { What = what.TrimEnd('.'), Where = Str(args, "where").Trim(), Why = Str(args, "why").Trim() };
        var outcome = await Handoff.AskAsync(step, context.Job, ct).ConfigureAwait(false);
        return ToolResult.Ok(new
        {
            choice = outcome.Choice,
            done = outcome.Done,
            next = outcome.Choice switch
            {
                "continued" => "The user says it is done. Check it in the project (status or inspect), then carry on.",
                "skipped" => "The user skipped this step. Carry on with what doesn't need it and say in your answer what was left.",
                "stopped" => "The user stopped the task here. Stop, and say in a few lines what is done and what is not.",
                "away" => "The user is away, so nobody can do this now. It is noted for them. Carry on with what doesn't need it and say in your answer what waits for them.",
                _ => "No chat is open to show the step. Say the step in your answer instead.",
            },
        });
    }

    private static string Str(ToolArgs args, string key) =>
        args.Raw.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
