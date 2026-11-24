using System.Text.Json;
using PlaxisProMax.Tools;

/// <summary>The approval policy's rules for Plaxis: what asks at each level, and what never asks.</summary>
internal static class ApprovalTests
{
    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static ApprovalDecision Decide(string tool, bool modifies, string json = "{}", ApprovalLevel level = ApprovalLevel.RiskyOnly,
        ApprovalFacts? facts = null, bool taskApproved = false, ICollection<string>? remembered = null, Func<string, PathState>? probe = null) =>
        ApprovalPolicy.Decide(tool, modifies, false, Args(json), true, facts ?? new ApprovalFacts(), level, remembered ?? [], taskApproved,
            probe ?? (_ => PathState.Missing), ["C:\\Users\\me\\Documents\\Plaxis ProMax"]);

    public static void Run(Action<string, bool, string> check)
    {
        check("approvals: reads never ask, at any level", !Decide("status", false, level: ApprovalLevel.EveryChange).Ask && !Decide("get_results", false).Ask, "");
        check("approvals: normal changes don't ask by default (risky only), and ask at every change",
            !Decide("create_boreholes", true).Ask && Decide("create_boreholes", true, level: ApprovalLevel.EveryChange) is { Ask: true, CanSilence: true }, "");
        check("approvals: once per task, an approved task doesn't ask again",
            Decide("generate_mesh", true, level: ApprovalLevel.OncePerTask).Ask && !Decide("generate_mesh", true, level: ApprovalLevel.OncePerTask, taskApproved: true).Ask, "");
        check("approvals: don't ask again silences a normal change", !Decide("add_phase", true, level: ApprovalLevel.EveryChange, remembered: ["add_phase"]).Ask, "");
        var code = Decide("run_plaxis_code", true, """{"code":"g_i.mesh()"}""");
        check("approvals: new code always asks, says why, and can't be silenced", code is { Ask: true, CanSilence: false, Kind: AskKind.Risky } && code.Reason.Contains("Python"), code.Reason);
        check("approvals: kept code the user allowed runs without asking (reuse is the easy path)",
            !Decide("run_plaxis_code", true, """{"kept_id":"K3"}""", facts: new ApprovalFacts { KeptCode = true }).Ask, "");
        check("approvals: code asks even when run_plaxis_code was silenced", Decide("run_plaxis_code", true, """{"code":"x"}""", remembered: ["run_plaxis_code"]).Ask, "");
        check("approvals: a new or opened project over unsaved work asks",
            Decide("new_project", true, facts: new ApprovalFacts { ClosesUnsaved = true }).Ask && !Decide("new_project", true).Ask, "");
        var delete = Decide("run_commands", true, """{"commands":["point 0 0","delete Point_1"]}""");
        check("approvals: run_commands with a delete asks, naming the command", delete.Ask && delete.Reason.Contains("delete Point_1"), delete.Reason);
        check("approvals: run_commands with Python-style g_i.delete(...) or s_i.new() asks too",
            Decide("run_commands", true, """{"commands":["g_i.delete(g_i.Point_1)"]}""").Ask && Decide("run_commands", true, """{"commands":["s_i.new()"]}""").Ask, "");
        check("approvals: run_commands with ordinary commands doesn't ask", !Decide("run_commands", true, """{"commands":["point 0 0","line (0 0) (1 0)","newmaterial"]}""").Ask, "");
        var overwrite = Decide("save_project", true, """{"path":"C:\\Work\\wall.p2dx"}""", probe: _ => PathState.File);
        check("approvals: saving over an existing file asks", overwrite.Ask && overwrite.Reason.Contains("overwrites"), overwrite.Reason);
        check("approvals: a CSV inside Plaxis ProMax's own folder never asks",
            !Decide("get_results", false, """{"csv_path":"C:\\Users\\me\\Documents\\Plaxis ProMax\\u.csv"}""", probe: _ => PathState.File).Ask, "");
        check("approvals: always allow asks for nothing, code included",
            !Decide("run_plaxis_code", true, """{"code":"x"}""", ApprovalLevel.AlwaysAllow).Ask
            && !Decide("run_commands", true, """{"commands":["delete x"]}""", ApprovalLevel.AlwaysAllow).Ask, "");
        check("approvals: levels round-trip through settings, unknown is the default",
            ApprovalPolicy.Levels.All(l => ApprovalPolicy.Parse(ApprovalPolicy.Store(l)) == l) && ApprovalPolicy.Parse("weird") == ApprovalLevel.RiskyOnly, "");
        check("approvals: tool names read as words", ApprovalPolicy.Friendly("create_soil_material") == "Create soil material", "");
    }
}
