using System.Text.Json;
using System.Text.Json.Nodes;
using PlaxisProMax.Plaxis;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

/// <summary>model_snapshot: take, list, find by words and dates, and compare (added, deleted, changed), against the stand-in Plaxis.</summary>
internal static class SnapshotTests
{
    private static ToolArgs Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ToolArgs(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
    }

    public static void Run(Action<string, bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ppm-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        ModelSnapshots.FolderOverride = Path.Combine(temp, "snapshots");
        try
        {
            // Compare, on its own.
            var before = JsonNode.Parse("""
                {"Materials": {"Clay": {"gammaUnsat": 16, "cref": "5.0", "Colour": 1}, "Sand": {"phi": 32}},
                 "Plates": {"Wall": {"EA": "12000", "Material": "AZ26"}},
                 "Phases": {"Initial phase": {"DeformCalcType": "K0 procedure", "CalculationResult": 0}}}
                """)!.AsObject();
            var after = JsonNode.Parse("""
                {"Materials": {"Clay": {"gammaUnsat": 16.0, "cref": "8.0", "Colour": 1}, "Fill": {"phi": 30}},
                 "Plates": {"Wall": {"EA": "1.2E4", "Material": "AZ36"}},
                 "Phases": {"Initial phase": {"DeformCalcType": "K0 procedure", "CalculationResult": 1}, "Excavate to -3": {"DeformCalcType": "Plastic"}}}
                """)!.AsObject();
            var changes = ModelSnapshots.Compare(before, after);
            string Line(ModelSnapshots.Change c) => $"{c.Kind}:{c.Collection}:{c.Name}:{c.Property}:{c.Before}>{c.After}";
            var lines = changes.Select(Line).ToList();
            check("compare: an added material and an added phase", lines.Contains("added:Materials:Fill::>") && lines.Contains("added:Phases:Excavate to -3::>"), string.Join(" | ", lines));
            check("compare: a deleted material", lines.Contains("deleted:Materials:Sand::>"), string.Join(" | ", lines));
            check("compare: a changed parameter with before and after", lines.Contains("changed:Materials:Clay:cref:5.0>8.0") && lines.Contains("changed:Plates:Wall:Material:AZ26>AZ36"), string.Join(" | ", lines));
            check("compare: the same number written differently is no change", !lines.Any(l => l.Contains("gammaUnsat") || l.Contains(":EA:")), string.Join(" | ", lines));
            check("compare: what a calculation changes by itself is left out, unless asked", !lines.Any(l => l.Contains("CalculationResult")) && ModelSnapshots.Compare(before, after, results: true).Any(c => c.Property == "CalculationResult"), string.Join(" | ", lines));
            check("compare: nothing differs from itself", ModelSnapshots.Compare(before, before).Count == 0, "");
            var csv = ModelSnapshots.Csv(changes);
            check("compare: the CSV has a header and a line per change", csv.StartsWith("Change,Collection,Object,Property,Before,After") && csv.Split('\n').Length == changes.Count + 2, csv);

            // Saved snapshots are found by words, numbers and dates.
            var t0 = new DateTime(2026, 11, 20, 9, 0, 0);
            ModelSnapshots.Save("Quay.p2dx", before, "issue A", t0);
            ModelSnapshots.Save("Quay.p2dx", after, "after the redesign", t0.AddDays(2));
            ModelSnapshots.Save("Quay.p2dx", after, "", t0.AddDays(4));
            var list = ModelSnapshots.List("Quay.p2dx");
            check("snapshots are listed newest first with their object counts", list.Count == 3 && list[0].Taken == t0.AddDays(4) && list[2].Objects == 4, string.Join(",", list.Select(s => s.Name)));
            check("find: latest, previous, first, a number", ModelSnapshots.Find(list, "") == list[0] && ModelSnapshots.Find(list, "previous") == list[1] && ModelSnapshots.Find(list, "first") == list[2] && ModelSnapshots.Find(list, "2") == list[1], "");
            check("find: words of the label", ModelSnapshots.Find(list, "issue a") == list[2] && ModelSnapshots.Find(list, "redesign") == list[1] && ModelSnapshots.Find(list, "nothing like it") is null, "");
            check("find: a date gives the last one on or before that day", ModelSnapshots.Find(list, "2026-11-21") == list[2] && ModelSnapshots.Find(list, "2026-11-22") == list[1] && ModelSnapshots.Find(list, "2026-11-01") is null, "");
            check("another project has its own snapshots", ModelSnapshots.List("Other.p2dx").Count == 0, "");

            // The tool against the stand-in: take, change the project, compare with now and between two snapshots.
            using var link = new PlaxisLink(TestPlaxis.WorkerStart);
            link.ConnectAsync(10000, "secret", CancellationToken.None).GetAwaiter().GetResult();
            var registry = PlaxisProMax.Tools.ToolRegistry.CreateDefault();
            var settings = new AgentSettings();
            var context = new ToolContext(link, new CodeLibrary(Path.Combine(temp, "kept")), () => settings);
            ToolResult Call(string tool, string json) => registry.Find(tool)!.ExecuteAsync(context, Args(json), CancellationToken.None).GetAwaiter().GetResult();

            check("model_snapshot: no snapshot yet says to take one", Call("model_snapshot", """{"action":"compare"}""") is { IsError: true } none && none.Text.Contains("Take one first"), "");
            var taken = Call("model_snapshot", """{"action":"take","label":"before the stages"}""");
            check("model_snapshot take saves the phases and materials", !taken.IsError && taken.Text.Contains("before the stages") && taken.Text.Contains("\"Phases\":1"), taken.Text);
            check("model_snapshot: nothing changed yet", Call("model_snapshot", """{"action":"compare"}""").Text.Contains("\"added\":0,\"deleted\":0,\"changed\":0"), "");
            Call("add_phase", """{"name":"Excavate to -3"}""");
            Call("create_soil_material", """{"name":"Clay","properties":{"gammaUnsat":16}}""");
            var now = Call("model_snapshot", """{"action":"compare"}""");
            check("model_snapshot compare with now lists the new phase and material", !now.IsError && now.Text.Contains("\"added\":2") && now.Text.Contains("Excavate to -3") && now.Text.Contains("Clay") && now.Text.Contains("the project now"), now.Text);
            check("model_snapshot compare writes the changes to a file", now.Text.Contains(".csv"), now.Text);
            var onlyPhases = Call("model_snapshot", """{"action":"compare","collections":["Phases"]}""");
            check("model_snapshot compare: only the collections asked for", onlyPhases.Text.Contains("\"added\":1") && !onlyPhases.Text.Contains("Clay"), onlyPhases.Text);
            System.Threading.Thread.Sleep(1100); // a second apart, so the two files have their own names
            Call("model_snapshot", """{"action":"take","label":"with the stages"}""");
            var listed = Call("model_snapshot", """{"action":"list"}""");
            check("model_snapshot list shows both, newest first", listed.Text.IndexOf("with the stages", StringComparison.Ordinal) is var a && a > 0 && a < listed.Text.IndexOf("before the stages", StringComparison.Ordinal), listed.Text);
            var between = Call("model_snapshot", """{"action":"compare","from":"with the stages","to":"before the stages"}""");
            check("model_snapshot compare of two snapshots goes from the older to the newer", between.Text.Contains("\"added\":2") && between.Text.Contains("\"deleted\":0"), between.Text);
            check("model_snapshot: an unknown snapshot says so", Call("model_snapshot", """{"action":"compare","from":"no such"}""").IsError, "");
            check("model_snapshot changes nothing and has a plain name", !registry.Find("model_snapshot")!.ModifiesModel && PlaxisProMax.Chat.StepNames.ReadOnly("model_snapshot"), "");
        }
        finally
        {
            ModelSnapshots.FolderOverride = null;
            try { Directory.Delete(temp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
