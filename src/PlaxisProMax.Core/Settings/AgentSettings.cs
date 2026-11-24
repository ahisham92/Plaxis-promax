using System.IO;
using System.Text.Json;

namespace PlaxisProMax.Settings;

/// <summary>
/// Per-user settings stored in %APPDATA%\PlaxisProMax\settings.json, edited from the window's Settings. The same file
/// layout and approval levels as Sketch ProMax; only what Plaxis ProMax uses is kept.
/// </summary>
public sealed class AgentSettings
{
    /// <summary>
    /// How often Plaxis ProMax asks before Claude changes something: risky_only (the default), once_per_task,
    /// every_change or always_allow. Empty reads as the default (Tools.ApprovalPolicy.Parse).
    /// </summary>
    public string? ApprovalLevel { get; set; }

    /// <summary>Tools the user said not to ask about again ("Don't ask again" on the card); cleared in Settings.</summary>
    public List<string>? NoAskTools { get; set; }

    /// <summary>The approval level as an enum.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Tools.ApprovalLevel Approvals
    {
        get => Tools.ApprovalPolicy.Parse(ApprovalLevel);
        set => ApprovalLevel = Tools.ApprovalPolicy.Store(value);
    }

    /// <summary>
    /// The user's standing rules in plain English (soil models they use, mesh and phase habits, report layout...), given
    /// to every chat. Sketch ProMax bug 115: rules that lived in one chat didn't reach the next, so they live here.
    /// </summary>
    public string ProjectRules { get; set; } = "";

    /// <summary>Download new versions from GitHub; they take effect the next time the window opens.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>The Plaxis Input remote scripting server's port (Expert > Configure remote scripting server). 10000 by default.</summary>
    public int InputPort { get; set; } = 10000;

    /// <summary>
    /// The remote scripting server's password, as typed in Plaxis's dialog. Kept only for a window started outside Plaxis:
    /// started from Plaxis's Expert menu, Plaxis hands the launcher its own port and password.
    /// </summary>
    public string ServerPassword { get; set; } = "";

    /// <summary>Plaxis's own Python (python.exe), found by the installer. Empty: looked for again at start.</summary>
    public string PlaxisPython { get; set; } = "";

    /// <summary>The model and effort a new chat starts on: "" for Claude Code's defaults.</summary>
    public string ChatModel { get; set; } = "";
    public string ChatEffort { get; set; } = "";

    /// <summary>How long one call to Plaxis may take before it is stopped, in seconds (a calculation runs as a job instead).</summary>
    public int CallTimeoutSeconds { get; set; } = 120;

    // ---- The crew (as in Sketch ProMax's Settings > Chat) ----------------------------------------------------------

    /// <summary>How much the chat moves: full (the default), reduced or off (Chat.MotionLevels).</summary>
    public string? ChatAnimations { get; set; }

    /// <summary>
    /// How many of a chat's parallel jobs run at once (each is a session on the user's plan): 3 by default, 1 to 6
    /// (Chat.JobBoard). More wait to start until one finishes.
    /// </summary>
    public int ChatJobLimit { get; set; } = 3;

    /// <summary>A job pauses itself after using this percent of the plan's 5-hour limit (Chat.JobBudget; 0: never).</summary>
    public int ChatJobBudget { get; set; } = 15;

    /// <summary>Which finished jobs a teammate checks before they are reported (Chat.JobReview: always, autopilot or off).</summary>
    public string ChatJobReview { get; set; } = "always";

    /// <summary>
    /// The crew's names (Chat.CrewNames): you, the coordinator, the workers (comma separated, one per job at once) and the
    /// one who applies changes in Plaxis. Empty: the defaults (you: your Windows account's name).
    /// </summary>
    public string? ChatCrewYou { get; set; }
    public string? ChatCrewCoordinator { get; set; }
    public string? ChatCrewWorkers { get; set; }
    public string? ChatCrewApplier { get; set; }

    /// <summary>The type a new chat starts as (Chat.ChatTypes): "coordinator" (it hands work to parallel jobs) or "simple".</summary>
    public string ChatTypeDefault { get; set; } = "coordinator";

    /// <summary>The model and effort a new chat starts on (Chat.ChatAuto): "auto" picks per request and per job by the kind of work.</summary>
    public string ChatModelDefault { get; set; } = "auto";
    public string ChatEffortDefault { get; set; } = "auto";

    /// <summary>What Automatic gives each kind of work (Chat.ChatAuto): simple, everyday and hard.</summary>
    public string ChatModelSimple { get; set; } = "haiku";
    public string ChatModelEveryday { get; set; } = "sonnet";
    public string ChatModelHard { get; set; } = "opus";
    public string ChatEffortSimple { get; set; } = "low";
    public string ChatEffortEveryday { get; set; } = "medium";
    public string ChatEffortHard { get; set; } = "high";

    /// <summary>The coordinator's own turns in a chat With coordinator: light, the jobs do the heavy work.</summary>
    public string ChatModelCoordinator { get; set; } = "sonnet";
    public string ChatEffortCoordinator { get; set; } = "low";

    /// <summary>An autopilot paused at a weekly limit carries on by itself when it resets (the 5-hour limit always does).</summary>
    public bool AutopilotResumeWeekly { get; set; } = true;

    /// <summary>During an autopilot, a job pauses after this percent of the 5-hour limit (Chat.JobBudget; 0: never).</summary>
    public int AutopilotJobBudget { get; set; } = 15;

    /// <summary>An autopilot pauses when the 5-hour limit reaches this percent, and carries on when it resets.</summary>
    public int AutopilotFiveHourMax { get; set; } = 90;

    /// <summary>One autopilot run may use this percent of the weekly limit, then pauses (0: no cap of its own).</summary>
    public int AutopilotWeekShare { get; set; } = 25;

    /// <summary>During an autopilot, a job that needs the user asks on their phone and waits this many minutes (0: nothing is asked).</summary>
    public int AutopilotAskWaitMinutes { get; set; } = 10;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlaxisProMax");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AgentSettings Load()
    {
        try
        {
            if (SafeFile.Read(FilePath) is { } text)
                return JsonSerializer.Deserialize<AgentSettings>(text) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        SafeFile.Write(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The user's names for projects on the phone, "HBR = Harbour, TWR = Tower" (Phone/PhoneProjects.Renamed, as in Sketch
    /// ProMax): each project has its own chat there.
    /// </summary>
    public string PhoneProjectNames { get; set; } = "";
}
