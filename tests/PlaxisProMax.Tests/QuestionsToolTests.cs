using System.Text.Json;
using PlaxisProMax.Chat;
using PlaxisProMax.Tools;

/// <summary>chat_questions (the Questions tab's tool), over_to_you (hand-offs) and what Away does to them.</summary>
public static class QuestionsToolTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        var board = new JobBoard([]);
        var job = board.Start("Soil parameters", "Work out the parameters of the sand.", now, true, out _)!;
        var before = (ChatJobs.Board, ChatJobs.Coordinator, ChatJobs.Shown);
        ChatJobs.Board = board;
        ChatJobs.Coordinator = true;
        ChatJobs.Shown = JobBoard.ChatKey("questionschat1");
        try
        {
            var asked = ChatQuestionsTool.Answer(Args("""{"questions":[{"question":"Which head for BH-3?","options":["As BH-2","Dry"],"recommended":0},{"question":"Use the SPT correlation?"}]}"""), true, null);
            check("questions tool: the coordinator's questions go in the tab, numbered", !asked.IsError && board.Questions.OpenCount == 2 && asked.Text.Contains("Q1, Q2"), asked.Text);
            var first = board.Questions.Open()[0];
            check("questions tool: options and the suggested one are kept", first.Options.Count == 2 && first.Recommended == 0 && first.From == "Coordinator", first.From);
            var mine = ChatQuestionsTool.Answer(Args("""{"question":"Drained or undrained clay?","options":["Drained","Undrained"]}"""), true, job.Key);
            var third = board.Questions.Open().Last();
            check("questions tool: a job's question shows as from that job", !mine.IsError && third.Job == job.Number && third.From.Contains("Soil parameters") && mine.Text.Contains("Carry on"), third.From + " | " + mine.Text);
            var list = ChatQuestionsTool.Answer(Args("""{"action":"list"}"""), true, null);
            check("questions tool: list shows the open ones", list.Text.Contains("BH-3") && list.Text.Contains("clay"), list.Text);
            var rewritten = ChatQuestionsTool.Answer(Args("""{"action":"update","number":"Q2","question":"May I estimate the sand's stiffness from its blow count?","options":["Yes","No"]}"""), true, null);
            check("questions tool: update rewrites a question", !rewritten.IsError && board.Questions.Find(2)!.Text.StartsWith("May I") && board.Questions.Find(2)!.Options.Count == 2, rewritten.Text);
            var closed = ChatQuestionsTool.Answer(Args("""{"action":"close","number":1}"""), true, null);
            check("questions tool: close withdraws one", !closed.IsError && board.Questions.OpenCount == 2, closed.Text);
            check("questions tool: close of one that isn't open is an error", ChatQuestionsTool.Answer(Args("""{"action":"close","number":1}"""), true, null).IsError, "");
            check("questions tool: nothing to ask is an error", ChatQuestionsTool.Answer(Args("""{"action":"ask"}"""), true, null).IsError, "");
            check("questions tool: outside the chat nobody is asked", ChatQuestionsTool.Answer(Args("""{"question":"x?"}"""), false, null).IsError, "");
            board.Questions.Answer(2, "Yes", now);
            var message = ChatQuestions.Message(board.Questions.TakeAnswers());
            check("questions tool: the answers go back as one message", message.Contains("Q2") && message.Contains("Yes"), message);
        }
        finally { (ChatJobs.Board, ChatJobs.Coordinator, ChatJobs.Shown) = before; }

        // Over to you
        var registry = ToolRegistry.CreateDefault();
        check("hand-off: over_to_you and chat_questions are tools of the chat", registry.Find("over_to_you") is not null && registry.Find("chat_questions") is not null, "");
        check("hand-off: neither is a change to the project", !registry.Find("over_to_you")!.ModifiesModel && StepNames.ReadOnly("mcp__plaxis-promax__over_to_you") && StepNames.ReadOnly("mcp__plaxis-promax__chat_questions"), "");
        var step = new HandoffStep { What = "Start the remote scripting server in Output", Where = "Expert menu", Why = "Results are read through it" };
        Away.Reset();
        try
        {
            Handoff.TestResponder = (_, _) => HandoffChoice.Continue;
            var done = Handoff.AskAsync(step, null, CancellationToken.None).GetAwaiter().GetResult();
            check("hand-off: Continue carries on", done is { Done: true, Choice: "continued" }, done.Choice);
            Handoff.TestResponder = (_, _) => HandoffChoice.Skip;
            var skipped = Handoff.AskAsync(step, null, CancellationToken.None).GetAwaiter().GetResult();
            check("hand-off: Skip leaves the step", skipped is { Done: false, Choice: "skipped" }, skipped.Choice);
            Handoff.TestResponder = (_, _) => HandoffChoice.Stop;
            var stopped = Handoff.AskAsync(step, null, CancellationToken.None).GetAwaiter().GetResult();
            check("hand-off: Stop stops the task", stopped is { Done: false, Choice: "stopped" } && Handoff.Recent.Count >= 3, stopped.Choice);
            Handoff.TestResponder = null;
            var presenter = Handoff.Presenter;
            Handoff.Presenter = null;
            var nobody = Handoff.AskAsync(step, null, CancellationToken.None).GetAwaiter().GetResult();
            check("hand-off: with no chat open the step comes back at once", nobody.Choice == "nobody", nobody.Choice);
            // A card that the user answers: the request is found by its id while it waits, and gone after.
            HandoffRequest? shown = null;
            Handoff.Presenter = (r, _) => { shown = r; return true; };
            var waiting = Handoff.AskAsync(step, null, CancellationToken.None);
            for (var i = 0; i < 200 && (shown is null || shown.State != HandoffState.Waiting); i++) Thread.Sleep(10);
            check("hand-off: the card's request waits and is found by its id", shown is { State: HandoffState.Waiting } && Handoff.Find(shown.Id) == shown, shown?.State.ToString() ?? "none");
            shown!.Answer(HandoffChoice.Continue);
            var answered = waiting.GetAwaiter().GetResult();
            check("hand-off: the card's Continue ends it as done", answered.Done && shown.Outcome == "Done" && Handoff.Find(shown.Id) is null, shown.Outcome ?? "");
            // Away: nothing waits; the step is listed for the user.
            Away.Set(true, DateTime.UtcNow);
            var away = Handoff.AskAsync(step, null, CancellationToken.None).GetAwaiter().GetResult();
            var listed = Away.Take();
            check("hand-off: while away the step is noted, not waited for", away.Choice == "away" && listed.Contains("remote scripting"), listed);
            Handoff.Presenter = presenter;
        }
        finally
        {
            Handoff.TestResponder = null;
            Away.Reset();
        }
    }

    private static ToolArgs Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ToolArgs(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
    }
}
