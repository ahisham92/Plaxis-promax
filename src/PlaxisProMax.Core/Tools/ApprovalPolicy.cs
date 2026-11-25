using System.Text.Json;

namespace PlaxisProMax.Tools;

/// <summary>How often Plaxis ProMax asks before Claude changes something (Settings, and the chat's menu). From Sketch ProMax.</summary>
public enum ApprovalLevel
{
    /// <summary>Normal model changes run without a dialog (they are one Undo away); risky ones are still asked. The default.</summary>
    RiskyOnly,
    /// <summary>The first change of a chat task asks once; the rest of that task runs. Risky ones are still asked.</summary>
    OncePerTask,
    /// <summary>Every model change is asked (how it worked before the levels).</summary>
    EveryChange,
    /// <summary>Nothing is asked, risky changes included (code, new or opened projects over unsaved work, deletes, overwrites).</summary>
    AlwaysAllow,
}

/// <summary>Why a call is asked about: a normal change (can be silenced), risky (asked each time), or always asked (whatever the setting).</summary>
internal enum AskKind { None, Change, Risky, Always }

/// <summary>
/// What the server found out about a call before deciding, that needs Plaxis or the disk: code the user has not approved
/// yet (a learned tool or a recipe), unsaved work a new or opened project would close, and a file it would overwrite.
/// </summary>
internal sealed record ApprovalFacts
{
    /// <summary>A learned tool or recipe whose code (or steps) has not been approved on this PC, or changed since.</summary>
    public bool NewCode { get; init; }
    /// <summary>The open project has changes that aren't saved, and the call would close it (new_project, open_project).</summary>
    public bool ClosesUnsaved { get; init; }
    /// <summary>run_plaxis_code runs a kept script the user allowed before, unchanged.</summary>
    public bool KeptCode { get; init; }
    /// <summary>A file the call replaces that the path checks can't see (e.g. a recipe saved under an existing name).</summary>
    public string? Overwrites { get; init; }
}

/// <summary>The answer: ask or not, why (one plain line), and whether the dialog may offer "don't ask again" and "allow the rest of this task".</summary>
internal sealed record ApprovalDecision(bool Ask, AskKind Kind, string Reason)
{
    public static readonly ApprovalDecision No = new(false, AskKind.None, "");
    /// <summary>Only a normal change can be silenced; risky and always-asked calls ask every time.</summary>
    public bool CanSilence => Kind == AskKind.Change;
}

/// <summary>Whether a path a tool writes to exists, and what it is.</summary>
internal enum PathState { Missing, EmptyFolder, Folder, File }

/// <summary>
/// The one place that decides whether a tool call is asked about. Pure (no Plaxis, no settings file): the server
/// passes the level, the tools the user said not to ask about again, whether the chat task is already approved (its
/// brief was run, or "Allow the rest of this task" was pressed) and a probe for paths, so the tests check every rule.
/// </summary>
internal static class ApprovalPolicy
{
    /// <summary>Arguments that name a file or folder a tool writes to (template_path is read, so it is not here).</summary>
    private static readonly HashSet<string> WriteKeys = new(StringComparer.Ordinal)
    {
        "folder", "output_folder", "report_folder", "file_name", "copy_path", "csv_path",
    };

    /// <summary>Tools whose "path" argument is where they write.</summary>
    private static readonly HashSet<string> WritesToPath = new(StringComparer.Ordinal) { "save_project", "export_results", "results_to_excel" };

    /// <summary>Tools that run code Claude wrote in this call: from the chat, always asked (the chat has no prompts of its own).</summary>
    private static readonly HashSet<string> RunsNewCode = new(StringComparer.Ordinal) { "run_plaxis_code" };

    /// <summary>Plaxis commands that delete, or close the project, when sent through run_commands.</summary>
    private static readonly string[] RiskyCommands = ["delete", "new", "open", "close", "recover", "undo", "redo"];

    /// <summary>Tools that run a saved recipe: asked when its steps are new to this PC (<see cref="ApprovalFacts.NewCode"/>).</summary>
    private static readonly HashSet<string> RunsRecipe = new(StringComparer.Ordinal) { "run_recipe", "batch_run_recipe" };

    /// <summary>
    /// Ask or not, and why. <paramref name="remembered"/>: tools the user said not to ask about again.
    /// <paramref name="taskApproved"/>: the chat task this call belongs to is approved (only for calls from the chat).
    /// <paramref name="outputRoots"/>: Plaxis ProMax's own folders; writing inside them never asks.
    /// </summary>
    public static ApprovalDecision Decide(
        string tool, bool modifiesModel, bool isLearned, IReadOnlyDictionary<string, JsonElement> args, bool fromChat, ApprovalFacts facts,
        ApprovalLevel level, ICollection<string> remembered, bool taskApproved, Func<string, PathState> probe, IReadOnlyList<string> outputRoots)
    {
        if (level == ApprovalLevel.AlwaysAllow) return ApprovalDecision.No;

        var risks = new List<string>();
        // Kept code the user allowed before, unchanged, runs without asking again: reusing it is the easy path (Sketch ProMax bugs 127 to 136).
        if (fromChat && RunsNewCode.Contains(tool) && !facts.KeptCode)
            risks.Add("The chat wrote Python to run in Plaxis. Read it in the details before you allow it.");
        if (facts.NewCode && isLearned)
            risks.Add("This learned tool's code has not been allowed on this PC yet, or it changed since. Read it in the details.");
        if (facts.NewCode && RunsRecipe.Contains(tool))
            risks.Add("This recipe's steps have not been allowed on this PC yet, or they changed since. Read them in the details.");
        if (facts.ClosesUnsaved)
            risks.Add("It closes the open project, which has changes that aren't saved.");
        if (tool == "run_commands" && RiskyCommandIn(args) is { } command)
            risks.Add($"It sends the Plaxis command \"{command}\", which deletes objects or closes the project.");
        if (facts.Overwrites is { Length: > 0 } over)
            risks.Add($"It replaces {over}, which can't be undone.");
        foreach (var path in WritePaths(tool, args))
        {
            if (Inside(path, outputRoots)) continue;
            switch (probe(path))
            {
                case PathState.File: risks.Add($"It overwrites the file {path}, which can't be undone."); break;
                case PathState.Folder: risks.Add($"It writes into {path}, which already has files that may be overwritten."); break;
            }
        }
        if (risks.Count > 0) return new(true, AskKind.Risky, string.Join(" ", risks));

        // Reads never ask. Normal changes are one undo away (the undo tool, or Plaxis's own Undo).
        if (!modifiesModel) return ApprovalDecision.No;
        if (level == ApprovalLevel.RiskyOnly || remembered.Contains(tool) || fromChat && taskApproved) return ApprovalDecision.No;
        return new(true, AskKind.Change, "It changes the Plaxis project. Undo puts it back.");
    }

    /// <summary>The first command in run_commands' list that deletes or closes the project, or null.</summary>
    internal static string? RiskyCommandIn(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("commands", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var line = item.GetString()!.Trim();
            var word = new string(line.TakeWhile(c => char.IsLetter(c) || c == '_').ToArray()).ToLowerInvariant();
            if (RiskyCommands.Contains(word)) return line;
            // g_i.delete(...) / s_i.new() written as Python by mistake: the method name decides.
            var dot = line.IndexOf('.');
            if (dot > 0)
            {
                var method = new string(line.Substring(dot + 1).TakeWhile(c => char.IsLetter(c) || c == '_').ToArray()).ToLowerInvariant();
                if (RiskyCommands.Contains(method)) return line;
            }
        }
        return null;
    }

    /// <summary>The full paths a call writes to (empty values are the tool's default folder, which is ours).</summary>
    public static List<string> WritePaths(string tool, IReadOnlyDictionary<string, JsonElement> args) =>
        args.Where(a => (WriteKeys.Contains(a.Key) || a.Key == "path" && WritesToPath.Contains(tool))
                        && a.Value.ValueKind == JsonValueKind.String && a.Value.GetString() is { Length: > 0 })
            .Select(a => FullPath(a.Value.GetString()!)).ToList();

    /// <summary>True when <paramref name="path"/> is one of <paramref name="roots"/> or inside one (case-insensitive, as Windows).</summary>
    public static bool Inside(string path, IReadOnlyList<string> roots)
    {
        var p = Normal(path);
        foreach (var root in roots)
        {
            var r = Normal(root);
            if (r.Length == 0) continue;
            if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
            if (p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Normal(string path)
    {
        // Resolved as Windows will ("..\" included), so a path that lands outside the root is outside.
        return FullPath(path).Replace('/', '\\').TrimEnd('\\');
    }

    /// <summary>A path as Windows resolves it ("..\\" included); left as given when it can't be resolved.</summary>
    internal static string FullPath(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return path; }
    }

    /// <summary>
    /// How many dialogs a chat task of these calls shows when the user answers Allow each time (the field test and the
    /// tests use it; at "Ask once per task" the first Allow approves the rest). <paramref name="briefRun"/>: the task
    /// started from a brief the user ran. Paths are taken as new (nothing is overwritten).
    /// </summary>
    public static int CountAsks(IEnumerable<(string Tool, bool Modifies, IReadOnlyDictionary<string, JsonElement> Args)> calls, ApprovalLevel level, bool briefRun)
    {
        var approved = briefRun;
        var asks = 0;
        foreach (var (tool, modifies, args) in calls)
        {
            var d = Decide(tool, modifies, false, args, true, new ApprovalFacts(), level, Array.Empty<string>(), approved, _ => PathState.Missing, []);
            if (!d.Ask) continue;
            asks++;
            if (d.CanSilence && level == ApprovalLevel.OncePerTask) approved = true;
        }
        return asks;
    }

    // ---- Names and the setting -----------------------------------------------------------------------------------------

    /// <summary>"create_soil_material" → "Create soil material": how a tool is named in the card and in Settings.</summary>
    public static string Friendly(string tool)
    {
        var text = tool.Replace('_', ' ').Trim();
        return text.Length == 0 ? tool : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    /// <summary>
    /// The level from settings.json: the stored one, or for settings written before levels existed the new default
    /// (whatever the old on/off switch said: on was the default nobody chose, and off asked about less than this).
    /// </summary>
    public static ApprovalLevel Parse(string? stored) => stored?.Trim().ToLowerInvariant() switch
    {
        "once_per_task" or "oncepertask" => ApprovalLevel.OncePerTask,
        "every_change" or "everychange" => ApprovalLevel.EveryChange,
        "always_allow" or "alwaysallow" => ApprovalLevel.AlwaysAllow,
        _ => ApprovalLevel.RiskyOnly,
    };

    public static string Store(ApprovalLevel level) => level switch
    {
        ApprovalLevel.OncePerTask => "once_per_task",
        ApprovalLevel.EveryChange => "every_change",
        ApprovalLevel.AlwaysAllow => "always_allow",
        _ => "risky_only",
    };

    public static string Label(ApprovalLevel level) => level switch
    {
        ApprovalLevel.OncePerTask => "Ask once per task",
        ApprovalLevel.EveryChange => "Ask for every change",
        ApprovalLevel.AlwaysAllow => "Always allow (never ask)",
        _ => "Ask only for risky changes",
    };

    public static string Hint(ApprovalLevel level) => level switch
    {
        ApprovalLevel.OncePerTask => "The first change of a chat task asks; the rest run",
        ApprovalLevel.EveryChange => "A dialog before every change",
        ApprovalLevel.AlwaysAllow => "Nothing asks, risky changes and code included",
        _ => "Changes just run (undo puts them back); risky ones still ask",
    };

    public static readonly ApprovalLevel[] Levels = [ApprovalLevel.RiskyOnly, ApprovalLevel.OncePerTask, ApprovalLevel.EveryChange, ApprovalLevel.AlwaysAllow];
}
