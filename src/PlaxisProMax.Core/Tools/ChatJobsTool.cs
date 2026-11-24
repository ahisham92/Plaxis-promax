using System.Text.Json;
using PlaxisProMax.Chat;

namespace PlaxisProMax.Tools;

/// <summary>
/// chat_jobs: the chat as coordinator (Sketch ProMax's tool). It splits a request into jobs that work side by side, each
/// its own hidden session with a card in the window (Chat.JobBoard, Chat.Crew), sends a follow-up to the job it belongs to,
/// lists and stops them. Answered by the MCP server itself, never by Plaxis, so it works while a job's call runs; only
/// the main chat may start or stop jobs, a job may list, ask and message.
/// </summary>
internal sealed class ChatJobsTool : IPlaxisTool
{
    public const string ToolName = "chat_jobs";

    public string Name => ToolName;
    public string Description =>
        "Jobs that work side by side in the Plaxis ProMax window, each its own helper with a card the user sees. action=start opens a job: " +
        "title (a few words), task (everything it needs, in full: it doesn't see this chat) and done_when (how the job can check in the project that it " +
        "is finished; it checks that before it reports). To divide work across the free workers, " +
        "give parts instead (one {title, task, kind} per part, which don't overlap) and facts (what you read that every part needs): they " +
        "all start at once. action=list shows every job with its " +
        "state and report. action=message sends a follow-up to job (its number): a running job gets it when its current run ends; a " +
        "finished or stopped one carries on with it. action=stop stops job. Before starting a job, list the jobs: a follow-up that " +
        "belongs to an existing job goes there with action=message. start refuses a request that reads as a follow-up of an existing " +
        "job; call it again with new=true when it really is separate work. A job may call it too, to work with its teammates: action=list " +
        "shows them, action=ask with job and question gets what that job has found so far (forward=true also passes the question on, and it " +
        "messages you back), action=message with job and text tells it something it needs. The jobs think side by side; their calls to Plaxis " +
        "take turns, one at a time (Plaxis runs one command at a time). Not a project change.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["start", "list", "message", "stop", "ask"], "description": "Default list. A job (not the main chat) may use list, ask and message."},
      "question": {"type": "string", "description": "ask (from a job): what you need from that job, e.g. 'the ids of the grids you read'."},
      "forward": {"type": "boolean", "description": "ask: true to also pass the question on when the job is still working; it messages you the answer when its current run ends."},
      "title": {"type": "string", "description": "start: a few words the user sees on the card, e.g. 'Soil layers from BH-1 to BH-4'."},
      "task": {"type": "string", "description": "start: the whole task for the job, with every detail it needs (names, levels, materials, phases, settings)."},
      "done_when": {"type": "string", "description": "start (and each part): the finish line, one to three conditions the job can check in the project, e.g. 'boreholes BH-1 to BH-4 exist with five layers each and every layer has a material; list_model shows them'. The job checks them before it reports and says which held."},
      "job": {"type": "integer", "description": "message, stop and ask: the job's number."},
      "text": {"type": "string", "description": "message: what to tell the job, in full."},
      "parts": {"type": "array", "description": "start: divide the work, one job per part, all started at once (each part's task in full; the parts must not overlap, e.g. results of phases 1-3 and of phases 4-6).", "items": {"type": "object", "properties": {"title": {"type": "string"}, "task": {"type": "string"}, "done_when": {"type": "string"}, "kind": {"type": "string", "enum": ["simple", "everyday", "hard"]}}, "required": ["title", "task"]}},
      "facts": {"type": "string", "description": "start with parts: what you already read from the project that every part needs (units, layers, materials, structures, phase names); each job gets it with its task."},
      "single": {"type": "string", "description": "start without parts while two or more workers are free: why this work can't be divided (one material, one phase, one short answer). Without it such a start is sent back to be divided."},
      "new": {"type": "boolean", "description": "start: true when it is separate work even though it resembles an existing job."},
      "kind": {"type": "string", "enum": ["simple", "everyday", "hard"], "description": "start: the kind of work, which picks the job's model and effort when the chat is on Automatic. simple: mechanical work (rename, list, read values, short summaries); everyday: most Plaxis work (boreholes, materials, structures, mesh, phases, results, checks); hard: hard reasoning (soil parameters from a site investigation report, a design basis against the model, why a calculation fails, staged construction logic). Left out, it is judged from the task's words."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    /// <summary>A job's own calls: ask a teammate, or message one; it doesn't start or stop jobs.</summary>
    private static ToolResult Teamwork(JobBoard board, ChatJob me, string action, ToolArgs args, string jobKey)
    {
        var number = Number(args);
        if (action is not ("ask" or "message"))
            return ToolResult.Error("A job doesn't start or stop jobs: say in your report what else is needed, and the main chat hands it out. You may list, ask and message.");
        if (number is null || number == me.Number) return ToolResult.Error("Give another job's number in 'job' (action=list shows your teammates).");
        var now = DateTime.UtcNow;
        if (action == "ask")
        {
            var answer = board.Ask(me.Key, number.Value, (Str(args, "question") ?? Str(args, "text") ?? "").Trim(), Bool(args, "forward"), now);
            if (answer is null) return ToolResult.Error($"There is no job {number}. " + board.Describe(ChatJobs.Limit));
            ChatJobs.ChangedFor(jobKey);
            return ToolResult.Ok(new { job = number, answer, next = "Use what fits and carry on with your task; read the model only for what is still missing." });
        }
        var text = (Str(args, "text") ?? "").Trim();
        if (text.Length == 0) return ToolResult.Error("Give what to tell the job in 'text'.");
        var sent = board.Tell(me.Key, number.Value, text, now);
        if (sent == JobBoard.Delivery.NotFound) return ToolResult.Error($"There is no job {number}. " + board.Describe(ChatJobs.Limit));
        ChatJobs.ChangedFor(jobKey);
        return ToolResult.Ok(new { job = number, sent = true, state = sent == JobBoard.Delivery.Reopened ? "it carries on with this message" : sent == JobBoard.Delivery.Soon ? "it gets this with its next step" : "it gets this when its current run ends" });
    }

    /// <summary>Not reached from the chat (the MCP server answers chat_jobs itself, Mcp.ChatMcp): lists, never starts or changes a job.</summary>
    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct) => Task.FromResult(Answer(args, coordinator: false));

    /// <summary>
    /// The call's answer. <paramref name="coordinator"/>: the main chat of the pane called it (the bridge says so); only
    /// then may it start, message or stop jobs.
    /// </summary>
    internal static ToolResult Answer(ToolArgs args, bool coordinator, string? jobKey = null)
    {
        var action = (Str(args, "action") ?? "list").Trim().ToLowerInvariant();
        // The calling chat's own board: the shown chat's, or that of a chat working in the background (its chat key).
        var board = ChatJobs.BoardFor(jobKey);
        var limit = ChatJobs.Limit;
        if (action == "list")
            return board is null ? ToolResult.Ok(new { jobs = "No chat is open in Plaxis ProMax, so there are no jobs." }) : new ToolResult(board.Describe(limit));
        if (action is not ("start" or "message" or "stop" or "ask")) return ToolResult.Error("action is start, list, message, stop or ask.");
        // A job working with its teammates: it asks one for what it found, or tells one something.
        if (jobKey is not null && !JobBoard.IsChatKey(jobKey) && board?.FindKey(jobKey) is { } me) return Teamwork(board, me, action, args, jobKey);
        if (action == "ask") return ToolResult.Error("ask is for a job asking another job; the main chat reads a job's report with action=list.");
        if (!coordinator || board is null)
            return ToolResult.Error("Jobs run only from the main chat of the Plaxis ProMax window. Do the work here yourself instead.");
        if (!ChatJobs.CoordinatorFor(jobKey))
            return ToolResult.Error(SimpleChatRefusal);
        var now = DateTime.UtcNow;
        switch (action)
        {
            case "start" when args.Raw.TryGetValue("parts", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0:
                return StartParts(board, parts, (Str(args, "facts") ?? "").Trim(), limit, now, jobKey);
            case "start":
            {
                var task = (Str(args, "task") ?? "").Trim();
                var title = (Str(args, "title") ?? "").Trim();
                if (task.Length == 0) return ToolResult.Error("Give the job's whole task in 'task' (and a short 'title').");
                // Free workers: the work goes out divided, not to one worker (unless it can't be divided, and the coordinator says why).
                var idle = Math.Max(0, JobBoard.ClampLimit(limit) - board.OpenCount);
                if (JobBoard.MustDivide(idle, Str(args, "single")))
                    return ToolResult.Ok(new
                    {
                        started = false, free_workers = idle,
                        next = $"Not started: {idle} workers are free, so divide this work between them. Call start again with parts (one per worker, " +
                               "not overlapping: reading the documents, working out parameters, building in Plaxis, reading results per phase or structure, writing the report; only one part changes the project at a time) and facts (what you already read " +
                               "that every part needs). Only if it truly can't be divided, call start again with the same task and single = why.",
                    });
                var job = board.Start(title, JobBoard.WithDoneWhen(task, Str(args, "done_when")), now, Bool(args, "new"), out var similar);
                if (job is null)
                    return ToolResult.Ok(new
                    {
                        started = false,
                        similar_job = similar!.Number, similar_title = similar.Title, state = JobBoard.StateLabel(similar),
                        next = $"Not started: this reads as a follow-up of job {similar.Number} (\"{similar.Title}\"). Send it there with " +
                               $"action=message, job={similar.Number}. If it really is separate work, call start again with new=true.",
                    });
                var kind = (Str(args, "kind") ?? "").Trim().ToLowerInvariant();
                lock (board.Gate)
                {
                    ChatJobs.Assign(job, kind is ChatAuto.Simple or ChatAuto.Everyday or ChatAuto.Hard ? kind : null);
                }
                ChatJobs.ChangedFor(jobKey);
                var waiting = board.OpenCount > JobBoard.ClampLimit(limit);
                var free = Math.Max(0, JobBoard.ClampLimit(limit) - board.OpenCount);
                return ToolResult.Ok(new
                {
                    started = true, job = job.Number, title = job.Title, model = ChatAuto.ModelLabel(job.Model), effort = job.Effort,
                    state = waiting ? "waiting to start (others are running)" : "starting",
                    free_workers = free,
                    other_jobs = JobTruth.Line(board.Snapshot(), job.Number),
                    next = free > 0
                        ? $"{free} worker(s) are still free. If this job's work can be divided (documents, parameters, results per phase or structure, the report), divide it now: " +
                          $"message job {job.Number} to keep only its share, and start the other shares with parts. Then tell the user in one line which jobs started and end your turn."
                        : "The user sees its card. Tell them in one line which jobs started, then end your turn: each job reports back when it finishes.",
                });
            }
            case "message":
            {
                var number = Number(args);
                var text = (Str(args, "text") ?? "").Trim();
                if (number is null || text.Length == 0) return ToolResult.Error("Give the job's number in 'job' and what to tell it in 'text'.");
                var sent = board.Message(number.Value, text, now, fromUser: false);
                if (sent == JobBoard.Delivery.NotFound) return ToolResult.Error($"There is no job {number}. " + board.Describe(limit));
                ChatJobs.ChangedFor(jobKey);
                return ToolResult.Ok(new
                {
                    job = number, sent = true,
                    state = sent == JobBoard.Delivery.Reopened ? "it carries on with this message" : "it gets this when its current run ends",
                    other_jobs = JobTruth.Line(board.Snapshot(), number.Value),
                    next = "Tell the user in one line that the job has it. It reports back when it finishes. Mention other jobs only as other_jobs says, or not at all.",
                });
            }
            default:
            {
                var number = Number(args);
                if (number is null) return ToolResult.Error("Give the job's number in 'job'.");
                var was = board.Stop(number.Value, now);
                if (was is null) return ToolResult.Error($"There is no job {number}. " + board.Describe(limit));
                ChatJobs.ChangedFor(jobKey);
                return ToolResult.Ok(new
                {
                    job = number,
                    stopped = was is JobBoard.Running or JobBoard.Waiting or JobBoard.Queued,
                    note = was is JobBoard.Running or JobBoard.Waiting
                        ? "Stopped. A Plaxis call it had already started finishes on its own."
                        : was == JobBoard.Queued ? "Stopped before it started." : "It wasn't running.",
                });
            }
        }
    }

    /// <summary>start with parts: one job per part, all at once (they are separate work, so none is refused as a follow-up).</summary>
    private static ToolResult StartParts(JobBoard board, JsonElement parts, string facts, int limit, DateTime now, string? jobKey)
    {
        var list = parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(p => (
            Title: Prop(p, "title"), Task: JobBoard.WithDoneWhen(Prop(p, "task"), Prop(p, "done_when")), Kind: Prop(p, "kind").ToLowerInvariant()))
            .Where(p => p.Task.Length > 0).Take(12).ToList();
        if (list.Count == 0) return ToolResult.Error("Each part needs its whole task in 'task' (and a short 'title').");
        var titles = list.Select(p => p.Title.Length > 0 ? p.Title : JobBoard.OneLine(p.Task, 40)).ToList();
        var started = new List<object>();
        for (var i = 0; i < list.Count; i++)
        {
            var (title, task, kind) = list[i];
            // Each part knows what was read for all of them and what the other parts are, so it leaves them alone.
            var others = string.Join("; ", titles.Where((_, k) => k != i));
            var full = task + (facts.Length > 0 ? "\n\nFacts the coordinator already read (use them; don't read them again):\n" + facts : "")
                + (others.Length > 0 ? "\n\nThe other parts, done by other jobs at the same time (leave them alone): " + others + "." : "");
            var job = board.Start(titles[i], full, now, force: true, out _);
            if (job is null) continue;
            lock (board.Gate)
            {
                ChatJobs.Assign(job, kind is ChatAuto.Simple or ChatAuto.Everyday or ChatAuto.Hard ? kind : null);
            }
            started.Add(new { job = job.Number, title = job.Title, model = ChatAuto.ModelLabel(job.Model) });
        }
        ChatJobs.ChangedFor(jobKey);
        var waiting = Math.Max(0, board.OpenCount - JobBoard.ClampLimit(limit));
        return ToolResult.Ok(new
        {
            started,
            state = waiting > 0 ? $"{waiting} wait to start until a worker is free" : "all starting",
            next = "Tell the user in one line how you divided the work, then end your turn: each job reports back when it finishes.",
        });
    }

    private static string Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    /// <summary>What start, message and stop say in a Simple chat (ChatTypes).</summary>
    internal const string SimpleChatRefusal =
        "This is a simple chat, so it has no jobs: do the work here yourself. (The user can switch the chat to With coordinator in the chat's header.)";

    private static string? Str(ToolArgs args, string key) =>
        args.Raw.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(ToolArgs args, string key) =>
        args.Raw.TryGetValue(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>The job's number, given as a number or as text ("2", "job 2").</summary>
    private static int? Number(ToolArgs args)
    {
        if (!args.Raw.TryGetValue("job", out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && System.Text.RegularExpressions.Regex.Match(v.GetString() ?? "", @"\d+") is { Success: true } m
            && int.TryParse(m.Value, out var parsed)) return parsed;
        return null;
    }
}
