using PlaxisProMax.Settings;

namespace PlaxisProMax.Chat;

/// <summary>
/// Where the bridge reaches the chats' parallel jobs (chat_jobs calls and a job's change waiting its turn), from its own
/// threads: the shown chat's (the pane sets it when a chat is shown) and, through <see cref="Boards"/>, those of the chats
/// still working in the background, found by the caller's key.
/// </summary>
internal static class ChatJobs
{
    static ChatJobs()
    {
        // The coordinator knows its crew by the names the user sees (Settings > Chat), in list_jobs and job reports.
        JobBoard.WorkerName = slot => Names.Worker(slot);
        JobBoard.CrewLine = () => Names.Line();
    }


    private static CrewNames? _names;

    /// <summary>The crew's names from Settings (read once, again after Settings is saved: <see cref="ReloadNames"/>).</summary>
    public static CrewNames Names
    {
        get
        {
            if (_names is { } have) return have;
            try
            {
                var s = AgentSettings.Load();
                _names = CrewNames.From(s.ChatCrewYou, s.ChatCrewCoordinator, s.ChatCrewWorkers, s.ChatCrewApplier, Environment.UserName);
            }
            catch (Exception) { _names = CrewNames.From(null, null, null, null, Environment.UserName); }
            return _names;
        }
    }

    public static void ReloadNames() => _names = null;

    private static volatile JobBoard? _board;
    private static volatile Action? _changed;
    private static volatile bool _coordinator = true;

    /// <summary>The key (JobBoard.ChatKey) the shown chat's Claude Code passes with its calls: those are the main chat's.</summary>
    public static volatile string Shown = "";

    /// <summary>Every working chat's board by its chat key (the shown chat's too): a chat in the background keeps its own.</summary>
    public static readonly ChatBoards Boards = new();

    /// <summary>The board, kind and change call a call reaches: the shown chat's for no key or its jobs, else by <see cref="Boards"/>.</summary>
    private static (JobBoard? Board, bool Coordinator, Action? Changed) Route(string? key)
    {
        if (string.IsNullOrEmpty(key) || key == Shown || (!JobBoard.IsChatKey(key) && _board?.FindKey(key) is not null)) return (_board, _coordinator, _changed);
        return Boards.For(key, "") is { } entry ? (entry.Board, entry.Coordinator, entry.Changed) : (null, false, null);
    }

    /// <summary>The board of the chat that made a call (<paramref name="key"/>: the bridge's caller key, null for the shown chat).</summary>
    public static JobBoard? BoardFor(string? key) => Route(key).Board;

    /// <summary>The calling chat is With coordinator (only then may it start or message jobs).</summary>
    public static bool CoordinatorFor(string? key) => Route(key).Coordinator;

    /// <summary>After the bridge changed a job of the calling chat: its pane session starts, stops and redraws.</summary>
    public static void ChangedFor(string? key) => Route(key).Changed?.Invoke();

    /// <summary>The open chat is With coordinator (ChatTypes): only then may its main chat start or message jobs.</summary>
    public static bool Coordinator { get => _coordinator; set => _coordinator = value; }

    /// <summary>The open chat's jobs (null before the pane has a chat).</summary>
    public static JobBoard? Board { get => _board; set => _board = value; }

    /// <summary>Called after the bridge changed a job (any thread): the pane starts, stops and redraws on its own thread.</summary>
    public static Action? Changed { get => _changed; set => _changed = value; }

    /// <summary>True when jobs can run: a chat is open and the connector is as new as this add-in.</summary>
    public static bool Available => _board is not null;

    /// <summary>How many jobs run at once (Settings, Chat tab).</summary>
    public static int Limit => JobBoard.ClampLimit(AgentSettings.Load().ChatJobLimit);

    /// <summary>The open chat's own model and effort choice (ChatAuto: "auto" or what was picked by hand), for new jobs.</summary>
    public static volatile string ChatModel = ChatAuto.Automatic, ChatEffort = ChatAuto.Automatic;

    /// <summary>Automatic's model and effort for each kind of work, from Settings > Chat.</summary>
    public static (Dictionary<string, string> Models, Dictionary<string, string> Efforts) AutoMaps()
    {
        var s = AgentSettings.Load();
        return (new() { [ChatAuto.Simple] = s.ChatModelSimple, [ChatAuto.Everyday] = s.ChatModelEveryday, [ChatAuto.Hard] = s.ChatModelHard, [ChatAuto.Coordinating] = s.ChatModelCoordinator },
                new() { [ChatAuto.Simple] = s.ChatEffortSimple, [ChatAuto.Everyday] = s.ChatEffortEveryday, [ChatAuto.Hard] = s.ChatEffortHard, [ChatAuto.Coordinating] = s.ChatEffortCoordinator });
    }

    /// <summary>The model and effort for a request or job (ChatAuto.Pick with the Settings' maps).</summary>
    public static AutoPick Pick(string text, string? chatModel, string? chatEffort, string? kind = null, bool documents = false, ICollection<string>? refused = null)
    {
        var (models, efforts) = AutoMaps();
        return ChatAuto.Pick(text, chatModel, chatEffort, models, efforts, kind, documents, refused);
    }

    /// <summary>A new job gets its kind, model and effort: the chat's own choice, or Automatic's for its task.</summary>
    public static void Assign(ChatJob job, string? kind)
    {
        var pick = Pick(job.Title + "\n" + job.Task, ChatModel, ChatEffort, kind);
        job.Kind = pick.Kind;
        job.Model = pick.Model;
        job.Effort = pick.Effort;
    }

    /// <summary>The bridge: job <paramref name="key"/>'s call waits its turn (<paramref name="ahead"/>: its place, 1 = next) or got it (null).</summary>
    public static void SetWaiting(string key, int? ahead)
    {
        if (BoardFor(key)?.SetWaiting(key, ahead) == true) ChangedFor(key);
    }

    /// <summary>The bridge, after a job's call: the messages sent to it while it worked, to add to the result (null when none).</summary>
    public static string? TakeAsides(string key, string tool)
    {
        var block = BoardFor(key)?.TakeAsides(key, tool, DateTime.UtcNow);
        if (block is not null) ChangedFor(key);
        return block;
    }

    /// <summary>The bridge: job <paramref name="key"/> (not a chat) calls a tool that changes the model (JobBoard.MarkChanged).</summary>
    public static void ChangesModel(string key)
    {
        if (!JobBoard.IsChatKey(key)) BoardFor(key)?.MarkChanged(key);
    }

    /// <summary>
    /// The bridge: true when job <paramref name="key"/> may only read: it checks another job's work (JobReview), reviews an
    /// autopilot's goal (GoalReview) or reads before an autopilot's plan is approved. Its changes are refused.
    /// </summary>
    public static bool IsCheck(string key) => BoardFor(key)?.FindKey(key) is { ReviewOf: > 0 } or { ReadOnly: true };

    /// <summary>The pane: the project chat on the phone of the chat or job <paramref name="key"/> (null: the main chat), for ask_user.</summary>
    public static Func<string?, string>? TopicFor { get; set; }

    /// <summary>Who asks the user on their phone (ask_user): "Job 7: Existing model", or the coordinator for the main chat.</summary>
    public static string AskerName(string? key) => key is not null && BoardFor(key)?.FindKey(key) is { } job ? $"Job {job.Number}: {job.Title}" : "the coordinator";

    /// <summary>The bridge: what job <paramref name="key"/> does in Plaxis now (JobBoard.SetActivity), for its card and animation.</summary>
    public static void SetActivity(string key, string activity, int ahead = 0)
    {
        if (BoardFor(key)?.SetActivity(key, activity, ahead) == true) ChangedFor(key);
    }
}
