using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlaxisProMax.Mcp;
using PlaxisProMax.Modeller;
using PlaxisProMax.Tools;

/// <summary>
/// The Modeller and Viewer: the model file, its checks, the pages (one file each, nothing beside them), the exported
/// script, the local server the pages call, the chat's model edits, and Calculate in Plaxis finding Plaxis. None of it
/// uses Plaxis: that is the point of them.
/// </summary>
internal static class ModellerTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "ppm-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        ModelFile.FolderOverride = Path.Combine(scratch, "Models");
        try { Body(check, scratch); }
        finally
        {
            ModelFile.FolderOverride = null;
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }

    private static void Body(Action<string, bool, string> check, string scratch)
    {
        // Model file
        var blank = ModelFile.Blank("2D", "Test");
        check("modeller: a blank model is valid and has an initial phase", ModelFile.Validate(blank)["phases"]!.AsArray().Count == 1, "");
        var path = Path.Combine(scratch, "a", "Test.ppm");
        ModelFile.Save(blank, path);
        var loaded = ModelFile.Load(path);
        check("modeller: a saved model loads back the same", loaded.ToJsonString() == blank.ToJsonString(), "");
        check("modeller: results and Plaxis project go beside the model",
            ModelFile.ResultsPath(path).EndsWith("Test.ppr") && ModelFile.PlaxisProjectPath(path, "3D").EndsWith(Path.Combine("Test (Plaxis)", "Test.p3d")), "");
        var refused = false;
        try { ModelFile.Validate(JsonNode.Parse("""{"format":"ppm","version":99,"product":"2D"}""")); } catch (InvalidDataException ex) { refused = ex.Message.Contains("newer"); }
        check("modeller: a model from a newer version is refused with a plain reason", refused, "");
        check("modeller: file names lose what Windows forbids", ModelFile.SafeName("a/b:c?") == "a-b-c-", ModelFile.SafeName("a/b:c?"));

        // Samples and checks
        var samples = ModelFile.Samples();
        check("modeller: the two samples ship inside the program", samples.ContainsKey("excavation-2d") && samples.ContainsKey("pit-3d"), string.Join(",", samples.Keys));
        foreach (var (name, sample) in samples)
        {
            var errors = ModelChecks.Check(sample).Where(f => f.Level == "error").ToList();
            check("modeller: sample " + name + " has no errors", errors.Count == 0, string.Join("; ", errors.Select(e => e.Text)));
        }
        var bad = ModelFile.Blank("2D");
        bad["materials"]!.AsArray().Add(new JsonObject { ["id"] = "m1", ["kind"] = "soil", ["name"] = "Clay", ["model"] = "Mohr-Coulomb", ["gammaUnsat"] = 18, ["gammaSat"] = 19, ["E"] = -5, ["nu"] = 0.7, ["c"] = 5, ["phi"] = 20, ["psi"] = 30 });
        var found = ModelChecks.Check(bad);
        check("modeller: checks find bad soil values (E, nu, psi > phi) and a model with no borehole",
            found.Count(f => f.Level == "error") >= 3, string.Join("; ", found.Select(f => f.Level + ": " + f.Text)));

        // Pages
        var modeller = StudioFiles.Page("modeller");
        var viewer = StudioFiles.Page("viewer");
        check("modeller: the pages have their style and code inside (nothing beside them)",
            !modeller.Contains("href=\"common.css\"") && !modeller.Contains("src=\"common.js\"") && modeller.Contains("window.PP") && viewer.Contains("window.PP"), "");
        check("modeller: the pages load nothing from the internet", !Regex(modeller + viewer, "(src|href)=\"https?://"), "");
        var packed = StudioFiles.ViewerWith([1, 2, 3]);
        check("modeller: a Viewer page can carry its results inside", packed.Contains("id=\"ppr-data\"") && packed.Contains(Convert.ToBase64String(new byte[] { 1, 2, 3 })), "");

        // Script
        var script = StudioFiles.Script(samples["excavation-2d"]);
        check("modeller: the exported script holds the builder and the model, and runs the builder",
            script.Contains("def build(") && script.Contains("standalone_main(globals(), _MODEL)") && script.Contains("b64decode"), "");

        // Finding Plaxis
        var pf = Path.Combine(scratch, "pf");
        Directory.CreateDirectory(Path.Combine(pf, "Bentley", "PLAXIS 2D CONNECT Edition V22"));
        Directory.CreateDirectory(Path.Combine(pf, "Bentley", "PLAXIS 2D 2024.2"));
        Directory.CreateDirectory(Path.Combine(pf, "Bentley", "PLAXIS 3D 2024.2"));
        File.WriteAllText(Path.Combine(pf, "Bentley", "PLAXIS 2D CONNECT Edition V22", "Plaxis2DXInput.exe"), "");
        File.WriteAllText(Path.Combine(pf, "Bentley", "PLAXIS 2D 2024.2", "Plaxis2DXInput.exe"), "");
        File.WriteAllText(Path.Combine(pf, "Bentley", "PLAXIS 3D 2024.2", "Plaxis3DInput.exe"), "");
        var inputs = PlaxisRun.FindInputs([Path.Combine(pf, "Bentley")]);
        check("modeller: Plaxis 2D and 3D Input are found, newest first",
            inputs.Count == 3 && inputs.Count(i => i.Product == "3D") == 1 && inputs.First(i => i.Product == "2D").Name == "PLAXIS 2D 2024.2"
                && PlaxisRun.Release("PLAXIS 2D CONNECT Edition V22 Update 2") > PlaxisRun.Release("PLAXIS 2D CONNECT Edition V21"), string.Join(", ", inputs.Select(i => i.Name)));
        check("modeller: Plaxis starts with its scripting server on a given port and password",
            PlaxisRun.ServerArguments(10000, "ab12") == "--AppServerPort=10000 --AppServerPassword=ab12", "");

        // Server
        var server = new StudioServer(new StudioSession(() => null, "worker.py"));
        server.Start();
        try
        {
            JsonObject Call(string method, string route, JsonObject? body = null, int expect = 200, string? origin = null)
            {
                var raw = Send(server, method, "/s/" + server.Secret + "/" + route, body, origin);
                var (status, text) = Split(raw);
                if (status != expect) throw new InvalidOperationException($"{method} {route}: {status} {text}");
                return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            }
            check("server: a wrong secret is not found", Split(Send(server, "GET", "/s/nope/api/model", null, null)).Status == 404, "");
            check("server: a page from another site is turned away", Split(Send(server, "GET", "/s/" + server.Secret + "/api/model", null, "https://evil.example")).Status == 403, "");
            var page = Split(Send(server, "GET", "/s/" + server.Secret + "/modeller", null, null));
            check("server: serves the Modeller page", page.Status == 200 && page.Text.Contains("Plaxis ProMax"), page.Status.ToString());
            var first = Call("GET", "api/model");
            check("server: no model at first", first["model"] is null, first.ToJsonString());
            var put = Call("PUT", "api/model", new JsonObject { ["rev"] = first["rev"]!.GetValue<int>(), ["model"] = samples["excavation-2d"].DeepClone() });
            var rev = put["rev"]!.GetValue<int>();
            var same = Call("GET", "api/model?rev=" + rev);
            check("server: asking with the current revision sends no model again", same["model"] is null && same["dirty"]!.GetValue<bool>(), same.ToJsonString());
            var stale = Split(Send(server, "PUT", "/s/" + server.Secret + "/api/model", new JsonObject { ["rev"] = rev - 1, ["model"] = ModelFile.Blank("2D") }, null));
            check("server: a page that had not seen a newer change can't overwrite it", stale.Status == 409, stale.Text);
            var saved = Call("POST", "api/save", new JsonObject());
            check("server: a new model is saved in the usual folder under its title",
                saved["path"]!.ToString().StartsWith(ModelFile.Folder) && File.Exists(saved["path"]!.ToString()), saved.ToJsonString());
            var listed = Call("GET", "api/models");
            check("server: lists the saved models", listed["models"]!.AsArray().Count == 1, listed.ToJsonString());
            var checks = Call("GET", "api/check");
            check("server: checks the model", checks["findings"] is JsonArray, "");
            var scriptAnswer = Call("POST", "api/script", new JsonObject());
            check("server: exports the Plaxis script", scriptAnswer["script"]!.ToString().Contains("standalone_main"), "");
            var plaxis = Call("GET", "api/plaxis");
            check("server: says whether Plaxis's Python is there", plaxis["python"]!.GetValue<bool>() == false && plaxis["installs"] is JsonArray, "");
            var run = Split(Send(server, "POST", "/s/" + server.Secret + "/api/run", new JsonObject(), null));
            check("server: Calculate with no Plaxis Python says so plainly", run.Status == 400 && run.Text.Contains("Python"), run.Text);
            check("server: no run yet", Call("GET", "api/run")["state"]!.ToString() == "none", "");

            // Results file served to the Viewer
            var ppr = Path.Combine(scratch, "r.ppr");
            using (var f = File.Create(ppr)) using (var gz = new GZipStream(f, CompressionLevel.Fastest)) gz.Write(Encoding.UTF8.GetBytes("""{"format":"ppr","phases":[{"name":"Phase 1","ended":"OK","node":{"Utot":[0.001,-0.03,0.02]},"plates":[{"name":"P","struct":"wall","M":[10,-250,40]}],"anchors":[{"struct":"anc1","kind":"NodeToNodeAnchor","F":-177}]}]}"""));
            var url = server.ViewerUrl(ppr);
            var pack = new Uri(url).Query.Split('&')[0].Split('=')[1];
            var bytes = Split(Send(server, "GET", "/s/" + server.Secret + "/" + pack, null, null));
            check("server: serves a results file to the Viewer only when it was named", bytes.Status == 200 && Split(Send(server, "GET", "/s/" + server.Secret + "/results/0000", null, null)).Status == 404, url);
            var summary = ResultsSummary.Of(StudioFiles.ReadResults(ppr));
            var p0 = summary["phases"]![0]!;
            check("results: the summary has the largest displacement and wall moment, with its sign",
                p0["max_Utot"]!.GetValue<double>() == -0.03 && p0["plates"]![0]!["max_M"]!.GetValue<double>() == -250 && p0["anchors"]![0]!["F"]!.GetValue<double>() == -177, summary.ToJsonString());

            // Version 2 results: the numbers in byte planes after the header, shared lists written once
            var v2 = Path.Combine(scratch, "r2.ppr");
            WriteV2(v2, """{"format":"ppr","version":2,"phases":[{"name":"P1","nodes":{"x":{"$f":[0,3]}},"node":{"Utot":{"$f":[12,3]}}},{"name":"P2","nodes":{"x":{"$f":[0,3]}}}]}""",
                [0f, 12.5f, 60f], [0.001f, float.NaN, -0.0296f]);
            var v2q = Path.Combine(scratch, "r2q.ppr");
            using (var f = File.Create(v2q))
            using (var gz = new GZipStream(f, CompressionLevel.Fastest))
            {
                var text = Encoding.UTF8.GetBytes("""{"format":"ppr","phases":[{"name":"P1","node":{"Utot":{"$q":[0,3,-0.5,0.25]}}}]}""");
                gz.Write("PPR2"u8); gz.Write(BitConverter.GetBytes(text.Length)); gz.Write(text);
                gz.Write(new byte[] { 0, 2, 0xFF, 0, 0, 0xFF }); // low bytes, then high bytes: 0, 2, 65535
            }
            var stepped = StudioFiles.ReadResults(v2q)["phases"]![0]!["node"]!["Utot"]!.AsArray();
            check("results: soil results in 2-byte steps read back, with a missing value as null",
                stepped[0]!.GetValue<double>() == -0.5 && stepped[1]!.GetValue<double>() == 0 && stepped[2] is null, stepped.ToJsonString());
            var read2 = StudioFiles.ReadResults(v2);
            var u = read2["phases"]![0]!["node"]!["Utot"]!.AsArray();
            check("results: version 2 files read back, with a missing value as null and a shared list in both phases",
                u[0]!.GetValue<double>() == 0.001 && u[1] is null && u[2]!.GetValue<double>() == -0.0296 && read2["phases"]![1]!["nodes"]!["x"]![1]!.GetValue<double>() == 12.5, read2.ToJsonString());
            var keepAll = StudioServer.Keep(new JsonObject { ["results"] = new JsonArray(PlaxisRun.ResultGroups.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()) });
            var keepSome = StudioServer.Keep(new JsonObject { ["results"] = new JsonArray("displacements", "bogus"), ["phases"] = new JsonArray("p5") });
            var none = false;
            try { StudioServer.Keep(new JsonObject { ["results"] = new JsonArray() }); } catch (InvalidOperationException) { none = true; }
            check("results: keeping everything sends no choice, unknown groups are dropped, and keeping nothing is refused",
                keepAll is null && keepSome!["results"]!.AsArray().Count == 1 && keepSome["phases"]![0]!.ToString() == "p5" && none, keepSome?.ToJsonString() ?? "");

            // The chat's edits
            var session = server.Session;
            session.Change(m => ModelPath.Apply(m, "set", "materials/id=" + m["materials"]![0]!["id"] + "/E", JsonValue.Create(12345)));
            var e = ModelPath.Get(session.Get().Model!, "materials/0/E")!.GetValue<int>();
            check("edits: set by id", e == 12345, e.ToString());
            session.Change(m => ModelPath.Apply(m, "add", "waterlevels", new JsonObject { ["id"] = "wl9", ["points"] = new JsonArray() }));
            session.Change(m => ModelPath.Apply(m, "remove", "waterlevels/id=wl9", null));
            check("edits: add then remove", session.Get().Model!["waterlevels"]!.AsArray().All(w => w!["id"]!.ToString() != "wl9"), "");
            var missing = false;
            try { session.Change(m => ModelPath.Apply(m, "set", "materials/id=nothing/E", JsonValue.Create(1))); } catch (ArgumentException) { missing = true; }
            check("edits: a wrong id is refused and the model kept", missing && ModelPath.Get(session.Get().Model!, "materials/0/E")!.GetValue<int>() == 12345, "");
            var newer = session.Get().Rev;
            var pageStale = Split(Send(server, "PUT", "/s/" + server.Secret + "/api/model", new JsonObject { ["rev"] = rev, ["model"] = ModelFile.Blank("2D") }, null));
            check("edits: the page's older copy does not overwrite the chat's edit", pageStale.Status == 409 && session.Get().Rev == newer, pageStale.Text);
        }
        finally { server.Dispose(); }

        // Tools are registered, and Calculate in Plaxis always asks (it takes a licence)
        var registry = ToolRegistry.CreateDefault();
        check("tools: the Modeller tools are offered to the chat",
            new[] { "open_modeller", "modeller_model", "modeller_edit", "modeller_check", "modeller_save", "calculate_in_plaxis", "open_results_viewer", "read_results" }.All(n => registry.Find(n) is not null), "");
        var ask = ApprovalPolicy.Decide("calculate_in_plaxis", true, false, new Dictionary<string, JsonElement>(), true, new ApprovalFacts(), ApprovalLevel.RiskyOnly, [], false, _ => PathState.Missing, []);
        var status = ApprovalPolicy.Decide("calculate_in_plaxis", true, false, JsonDocument.Parse("""{"action":"status"}""").RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), true, new ApprovalFacts(), ApprovalLevel.RiskyOnly, [], false, _ => PathState.Missing, []);
        check("tools: starting a calculation always asks (licence), asking how it goes does not", ask.Ask && ask.Reason.Contains("licence") && !status.Ask, ask.Reason);
        check("shortcuts: made again after an update moves the program, not otherwise",
            Shortcuts.Due(null, "C:\\a\\PlaxisProMax.exe") && Shortcuts.Due("C:\\old\\PlaxisProMax.exe" + Shortcuts.Revision, "C:\\a\\PlaxisProMax.exe") && !Shortcuts.Due("C:\\a\\PlaxisProMax.exe" + Shortcuts.Revision, "C:\\a\\PlaxisProMax.exe"), "");
    }

    private static void WriteV2(string path, string head, params float[][] lists)
    {
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Fastest);
        var text = Encoding.UTF8.GetBytes(head);
        gz.Write("PPR2"u8);
        gz.Write(BitConverter.GetBytes(text.Length));
        gz.Write(text);
        foreach (var list in lists)
        {
            var raw = new byte[list.Length * 4];
            Buffer.BlockCopy(list, 0, raw, 0, raw.Length);
            for (var p = 0; p < 4; p++)
                for (var i = 0; i < list.Length; i++) gz.WriteByte(raw[4 * i + p]);
        }
    }

    private static bool Regex(string text, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(text, pattern);

    private static string Send(StudioServer server, string method, string path, JsonObject? body, string? origin)
    {
        using var client = new System.Net.Sockets.TcpClient("127.0.0.1", server.Port);
        var stream = client.GetStream();
        var data = body is null ? [] : Encoding.UTF8.GetBytes(body.ToJsonString());
        var head = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: {data.Length}\r\n");
        if (origin is not null) head.Append("Origin: ").Append(origin).Append("\r\n");
        head.Append("\r\n");
        stream.Write(Encoding.ASCII.GetBytes(head.ToString()));
        stream.Write(data);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static (int Status, string Text) Split(string raw)
    {
        var status = int.Parse(raw.Split(' ')[1]);
        var at = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (status, at < 0 ? "" : raw[(at + 4)..]);
    }
}
