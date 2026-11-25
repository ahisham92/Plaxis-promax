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
}
