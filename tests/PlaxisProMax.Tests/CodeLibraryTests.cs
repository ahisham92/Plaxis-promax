using PlaxisProMax.Tools;

/// <summary>
/// The kept-code library, against Sketch ProMax's lessons: found by what code touches (not two shared words, bug 136),
/// near repeats held once, adapted code replaces its entry (bug 98), and same code counts a use.
/// </summary>
internal static class CodeLibraryTests
{
    private const string Results = """
        s_o, g_o = output(g_i.Phases[-1])
        phase = [p for p in g_o.Phases if label(p) == inputs["phase"]][0]
        ux = g_o.getresults(phase, g_o.ResultTypes.Soil.Ux, "node")
        uy = g_o.getresults(phase, g_o.ResultTypes.Soil.Uy, "node")
        result = max(abs(v) for v in ux)
        """;

    public static void Run(Action<string, bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ppm-kept-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ids = CodeLibrary.Identifiers(Results + "\ns_i.call_and_handle_command(\"mesh 0.06\")");
            check("kept code: identifiers are what the code touches in Plaxis",
                ids.Contains("g_o.getresults") && ids.Contains("resulttypes.soil.ux") && ids.Contains("g_i.phases") && ids.Contains("cmd:mesh") && !ids.Any(i => i.Contains("label")),
                string.Join(",", ids));
            check("kept code: inputs are read from the code", CodeLibrary.InputsOf(Results).SequenceEqual(["phase"]), string.Join(",", CodeLibrary.InputsOf(Results)));

            var lib = new CodeLibrary(folder);
            var k1 = lib.Keep(Results, "Max horizontal displacement of a phase", null, "PLAXIS 2D", "2024.2", allowed: true);
            check("kept code: the first entry is K1, kept on disk with one use", k1.Id == "K1" && k1.Uses == 1 && File.Exists(Path.Combine(folder, "K1.json")), k1.Id);
            var again = lib.Keep(Results + "\n", "whatever", null, "PLAXIS 2D", "2024.2", allowed: true);
            check("kept code: the same code again (trailing space or newline) is a use, not a copy", again.Id == "K1" && again.Uses == 2 && lib.All.Count == 1, $"{again.Id} {again.Uses}");

            var nearly = Results.Replace("max(abs(v) for v in ux)", "min(ux)");
            var close = lib.Closest(nearly);
            check("kept code: new code that touches the same things is matched to the kept entry", close?.Id == "K1", close?.Id ?? "none");
            check("kept code: unrelated code isn't matched", lib.Closest("g_i.gotosoil()\ng_i.borehole(0)\ng_i.soillayer(3)") is null, "");
            check("kept code: code touching under three things is never held", lib.Closest("g_i.Phases") is null, "");
            check("kept code: an offer is made once per chat", lib.OfferOnce("chat1", k1) && !lib.OfferOnce("chat1", k1) && lib.OfferOnce("chat2", k1), "");

            var adapted = lib.Keep(nearly, "Min horizontal displacement of a phase", "K1", "PLAXIS 2D", "2024.2", allowed: true);
            check("kept code: adapted code replaces the entry it was based on (no near copies, bug 98)",
                adapted.Id == "K1" && adapted.Revision == 1 && lib.All.Count == 1 && lib.Get("K1")!.Code == nearly, $"{adapted.Id} r{adapted.Revision} n{lib.All.Count}");

            lib.Keep("g_i.gotosoil()\nbh = g_i.borehole(0)\ng_i.soillayer(3)\ng_i.setsoillayerlevel(bh, 0, 0)", "Borehole with one layer", null, "PLAXIS 2D", "2024.2", true);
            var byTouch = lib.Find("", ["borehole", "soillayer"]);
            check("kept code: found by the commands it would use", byTouch.Count == 1 && byTouch[0].Entry.Id == "K2", string.Join(",", byTouch.Select(f => f.Entry.Id)));
            var byWords = lib.Find("horizontal displacement phase", []);
            check("kept code: found by most of the words of what it does", byWords.FirstOrDefault().Entry?.Id == "K1", string.Join(",", byWords.Select(f => f.Entry.Id)));
            check("kept code: two shared words of a long description aren't enough (bug 136)",
                lib.Find("phase displacement of anchors in the excavation with groundwater drawdown", []).Count == 0, "");

            var reread = new CodeLibrary(folder);
            check("kept code: entries are read back from disk", reread.All.Count == 2 && reread.Get("k1")?.Revision == 1, reread.All.Count.ToString());
            check("kept code: the catalog lists the most used first, with inputs", reread.Catalog().StartsWith("- K1: Min horizontal displacement of a phase (inputs: phase) [used 3x]"), reread.Catalog());
            check("kept code: delete removes the file", reread.Delete("K2") && !File.Exists(Path.Combine(folder, "K2.json")) && new CodeLibrary(folder).All.Count == 1, "");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
