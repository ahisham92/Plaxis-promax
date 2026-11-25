using System.Text.Json;
using PlaxisProMax.Chat;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

namespace PlaxisProMax.Mcp;

/// <summary>What the window answers on an approval card.</summary>
public enum ApprovalAnswer { Deny, Allow, AllowRestOfTask, DontAskAgain }

/// <summary>One approval card: the tool in plain words, the one-line reason, and the details folded away (the code, the commands).</summary>
public sealed record ApprovalRequest(string Tool, string Title, string Reason, string Details, bool CanSilence);

/// <summary>Shows an approval card and waits for the answer. The window implements it; it must never block its own UI thread.</summary>
public interface IApprovalAsker
{
    Task<ApprovalAnswer> AskAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>
/// The chat's MCP server, inside the window: <see cref="McpHttpServer"/> (from Sketch ProMax) serving the Plaxis tools,
/// with the approval policy in front of every call that changes the project. One per window.
/// </summary>
internal sealed class ChatMcp : IDisposable
{
    /// <summary>The window's running server, which ClaudeCli writes into the chat's MCP config.</summary>
    public static McpHttpServer? Current { get; private set; }

    private readonly ToolRegistry _registry;
    private readonly ToolContext _context;
    private readonly IApprovalAsker _asker;
    private readonly McpHttpServer _server;
    private volatile bool _taskApproved;

    public ChatMcp(ToolRegistry registry, ToolContext context, IApprovalAsker asker, string version)
    {
        _registry = registry;
        _context = context;
        _asker = asker;
        var endpoint = new McpEndpoint("plaxis-promax", version, Instructions, _ => Tools(), CallAsync);
        _server = new McpHttpServer(endpoint);
    }

    public McpHttpServer Server => _server;

    public void Start()
    {
        _server.Start();
        Current = _server;
    }

    /// <summary>A new user message: "Allow the rest of this task" no longer covers what follows.</summary>
    public void NewTask() => _taskApproved = false;

    /// <summary>Sent to Claude Code at initialize: what is kept, so every chat sees the kept code (Sketch ProMax bug 128).</summary>
    private string Instructions()
    {
        var catalog = _context.Library.Catalog();
        return catalog.Length == 0
            ? "Plaxis ProMax tools. No kept code yet: code that works is kept by run_plaxis_code."
            : "Plaxis ProMax tools. Kept code (run with run_plaxis_code kept_id; find more with find_kept_code):\n" + catalog;
    }

    private IReadOnlyList<McpTool> Tools() =>
        _registry.All.Select(t => new McpTool(t.Name, t.Description, ToolRegistry.Schema(t))).ToList();

    private async Task<McpResult> CallAsync(McpSession session, string name, IDictionary<string, JsonElement>? raw, CancellationToken ct)
    {
        var tool = _registry.Find(name);
        if (tool is null) return new McpResult($"No tool named {name}.", true);
        // A PC without a valid key runs no tools (Licensing/LicenseState, as in Sketch ProMax).
        if (!Licensing.LicenseState.Allowed)
            return new McpResult("Plaxis ProMax isn't licensed on this PC, so its tools are off. Tell the user to open Settings > License " +
                                 "and send their PC code to get a key.", true);
        // Who calls (Sketch ProMax's bridge): null for the main chat shown in the window, else a parallel job's key.
        var jobKey = JobBoard.Caller(session.Job, ChatJobs.Shown);
        var isJob = jobKey is not null && !JobBoard.IsChatKey(jobKey);
        // chat_jobs is answered here, never by Plaxis, so it works while a job's call runs.
        if (name == ChatJobsTool.ToolName)
        {
            var answer = ChatJobsTool.Answer(new ToolArgs(new Dictionary<string, JsonElement>(raw ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal)), coordinator: !isJob, session.Job);
            return new McpResult(answer.Text, answer.IsError);
        }
        // The Questions tab is answered here too: the coordinator's and the jobs' questions to the user.
        if (name == ChatQuestionsTool.ToolName)
        {
            var answer = ChatQuestionsTool.Answer(new ToolArgs(new Dictionary<string, JsonElement>(raw ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal)), fromChat: true, session.Job);
            return new McpResult(answer.Text, answer.IsError);
        }
        var args = new Dictionary<string, JsonElement>(raw ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal);
        var settings = AgentSettings.Load();
        var context = new ToolContext(_context.Link, _context.Library, _context.Settings) { Chat = session.Id, Job = isJob ? jobKey : null };
        if (name == RunPlaxisCodeTool.ToolName && RunPlaxisCodeTool.Held(context, new ToolArgs(args)) is { } held)
            return new McpResult(held.Text, false);

        var facts = new ApprovalFacts
        {
            KeptCode = name == RunPlaxisCodeTool.ToolName && !args.ContainsKey("code") && args.TryGetValue("kept_id", out var kid)
                       && kid.ValueKind == JsonValueKind.String && _context.Library.Get(kid.GetString()!) is { Allowed: true },
        };
        // A job's call: refused while everything is paused (Pause all), and a check or a reader may not change the project.
        if (isJob && JobPause.Enter(jobKey!) is { } paused) return new McpResult(paused, true);
        try
        {
            if (isJob && tool.ModifiesModel && ChatJobs.IsCheck(jobKey!))
                return new McpResult("This job only reads (it checks work, or reads before a plan is approved), so this change was not made. Report what you found instead.", true);
            return await RunAsync(session, tool, name, args, context, settings, facts, isJob ? jobKey : null, ct).ConfigureAwait(false);
        }
        finally { if (isJob) JobPause.Exit(jobKey!); }
    }

    /// <summary>How long a job's call waits for its turn at Plaxis before it is told to try again.</summary>
    internal static TimeSpan TurnWait = TimeSpan.FromMinutes(30);

    /// <summary>The approval card (when the policy asks), a job's turn at Plaxis, the call itself, and the activity log.</summary>
    private async Task<McpResult> RunAsync(McpSession session, IPlaxisTool tool, string name, Dictionary<string, JsonElement> args, ToolContext context,
        AgentSettings settings, ApprovalFacts facts, string? jobKey, CancellationToken ct)
    {
        var remembered = (ICollection<string>?)settings.NoAskTools ?? [];
        var decision = ApprovalPolicy.Decide(name, tool.ModifiesModel, false, args, true, facts, settings.Approvals, remembered,
            _taskApproved, Probe, [OutputFolder]);
        // An autopilot works with nobody at the computer: the user approved the goal and a backup was saved first, so the
        // change is allowed and kept in the run's log for its report.
        if (decision.Ask && Autopilot.Unattended)
        {
            Autopilot.Log(AutopilotEntry.Allowed, $"{ApprovalPolicy.Friendly(name)}: {Autopilot.Allows(decision.Reason).Why}");
        }
        else if (decision.Ask && Away.On && !Phone.PhoneLink.Active)
        {
            // Away mode without the phone: nothing pops up, so a change that needs the user's OK waits for them.
            Away.Note($"{ApprovalPolicy.Friendly(name)} was not done, it needs your OK ({decision.Reason})");
            return new McpResult("The user is away and this step needs their OK, so it was not done. Leave it, carry on with the work that doesn't " +
                                 "depend on it, and say in your answer that it waits for them.", true);
        }
        else if (decision.Ask)
        {
            // A job's card says whose it is ("Job 2: Soil layers").
            var title = jobKey is null ? ApprovalPolicy.Friendly(name) : $"{ChatJobs.AskerName(jobKey)}: {ApprovalPolicy.Friendly(name)}";
            var answer = await _asker.AskAsync(new ApprovalRequest(name, title, decision.Reason, Details(name, args), decision.CanSilence), ct).ConfigureAwait(false);
            switch (answer)
            {
                case ApprovalAnswer.Deny:
                    return new McpResult("The user declined this step. Ask what they want instead; don't try it another way.", true);
                case ApprovalAnswer.AllowRestOfTask:
                    _taskApproved = true;
                    break;
                case ApprovalAnswer.DontAskAgain when decision.CanSilence:
                    var s = AgentSettings.Load();
                    s.NoAskTools = (s.NoAskTools ?? []).Append(name).Distinct(StringComparer.Ordinal).ToList();
                    s.Save();
                    break;
            }
        }

        // A job's call to Plaxis takes its turn (Plaxis runs one command at a time): its card shows it waiting, then
        // reading or applying; thinking again when the call ends.
        JobTurns.Ticket? turn = null;
        var plaxis = tool is WorkerTool or RunPlaxisCodeTool;
        if (jobKey is not null && plaxis)
        {
            turn = JobTurns.Shared.Enter(jobKey, name);
            if (JobTurns.Shared.Position(turn) > 1)
            {
                void Moved() => ChatJobs.SetActivity(jobKey, JobBoard.WaitingToApply, Math.Max(1, JobTurns.Shared.Position(turn) - 1));
                JobTurns.Shared.Changed += Moved;
                Moved();
                bool got;
                try { got = await JobTurns.Shared.WaitAsync(turn, TurnWait, ct).ConfigureAwait(false); }
                finally { JobTurns.Shared.Changed -= Moved; }
                if (!got)
                {
                    ChatJobs.SetActivity(jobKey, "");
                    return new McpResult("Plaxis was busy with other jobs' calls for too long, so this call did not run. Try it again.", true);
                }
            }
            if (tool.ModifiesModel) ChatJobs.ChangesModel(jobKey);
            ChatJobs.SetActivity(jobKey, tool.ModifiesModel ? JobBoard.Applying : JobBoard.Reading);
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        McpResult reply;
        try
        {
            var result = await tool.ExecuteAsync(context, new ToolArgs(args), ct).ConfigureAwait(false);
            reply = new McpResult(result.Text, result.IsError, result.PngImage is { } png ? Convert.ToBase64String(png) : null);
        }
        catch (ArgumentException ex) { reply = new McpResult(ex.Message, true); }
        catch (InvalidOperationException ex) { reply = new McpResult(ex.Message, true); }
        finally
        {
            if (turn is not null) JobTurns.Shared.Leave(turn);
            if (jobKey is not null && plaxis) ChatJobs.SetActivity(jobKey, "");
        }
        // The applier's numbers (CrewStats): a change put into Plaxis for a job.
        if (jobKey is not null && tool.ModifiesModel && !reply.IsError)
            CrewStats.Add(new CrewRecord { Start = DateTime.UtcNow - clock.Elapsed, End = DateTime.UtcNow, Role = CrewRecord.Applier, Ok = true });
        // Messages sent to the job while it worked reach it with this step, not at the end of its run.
        if (jobKey is not null && !ct.IsCancellationRequested && ChatJobs.TakeAsides(jobKey, name) is { } aside)
            reply = reply with { Text = JobText.Add(reply.Text, null, aside) };
        // Every call, worked or not, for the bug reports and the chats-and-bugs export (as Sketch ProMax logs them).
        ActivityLog.Write(ProjectName(), name, tool.ModifiesModel, args, !reply.IsError, reply.Text, clock.ElapsedMilliseconds);
        return reply;
    }

    /// <summary>The open Plaxis project's file name, for the activity log ("" when none is known).</summary>
    private string ProjectName()
    {
        try { return (_context.Link.LastStatus?["filename"]?.ToString() ?? "").Split('\\', '/').Last(); }
        catch (Exception) { return ""; }
    }

    /// <summary>The code or commands the user should read before allowing, as plain text.</summary>
    internal static string Details(string tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        if (args.TryGetValue("code", out var code) && code.ValueKind == JsonValueKind.String) return code.GetString()!;
        if (args.TryGetValue("commands", out var list) && list.ValueKind == JsonValueKind.Array)
            return string.Join("\n", list.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText()));
        return JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Plaxis ProMax's own output folder (Documents\Plaxis ProMax): writing inside it never asks.</summary>
    public static string OutputFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Plaxis ProMax");

    private static PathState Probe(string path)
    {
        try
        {
            if (File.Exists(path)) return PathState.File;
            if (Directory.Exists(path)) return Directory.EnumerateFileSystemEntries(path).Any() ? PathState.Folder : PathState.EmptyFolder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return PathState.Missing;
    }

    public void Dispose()
    {
        if (Current == _server) Current = null;
        _server.Dispose();
    }
}
