using System.Security.Cryptography;
using PlaxisProMax.Licensing;

/// <summary>
/// Sketch ProMax's license tests, run on the copied key code: the real key made by the owner's License manager in
/// Sketch ProMax must be valid here too. Plus the gates on the chat and the tools.
/// </summary>
internal static class LicenseTests
{
    // Made with the owner's real signing secret (not in either repository) for a made-up PC code, in Sketch ProMax.
    private const string RealKey = "SPM1.MXxNTTlGN0Y4OHxTUE0tVEVTVC0wMDAwLUFCQ0R8VGVzdCBQZXJzb258MjAyNi0xMC0yM3wyMDMwLTAxLTMx.LwwEbUCSRLPxG2IVG4VLn19xmOxoYVhhgg-KDulOIeyN5isyiKYwHARik7LkOnIvexx52SWlgL99rpfKB_H0Sw";

    private static string Pad(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return s + new string('=', (4 - s.Length % 4) % 4);
    }

    public static void Run(string root, Action<string, bool, string> check)
    {
        var today = new DateTime(2026, 10, 23);
        var code = LicenseKey.PcCode("36953C6F-979D-44BC-88FA-E3F96DB72BBB");
        check("license: PC code form", System.Text.RegularExpressions.Regex.IsMatch(code, "^SPM-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$"), code);
        check("license: PC code ignores case and differs per PC", code == LicenseKey.PcCode("36953c6f-979d-44bc-88fa-e3f96db72bbb")
            && code != LicenseKey.PcCode("36953c6f-979d-44bc-88fa-e3f96db72bbc") && LicenseKey.PcCode("") == "", code);
        check("license: typed codes are forgiven", LicenseKey.NormalizeCode(" spm 7k2q-9xw4-m3td ") == "SPM-7K2Q-9XW4-M3TD"
            && LicenseKey.NormalizeCode("7K2Q9XW4M3TO") == "SPM-7K2Q-9XW4-M3T0" && LicenseKey.NormalizeCode("SPM-7K2Q") is null
            && LicenseKey.NormalizeCode("SPM-7K2Q-9XW4-M3T!") is null, LicenseKey.NormalizeCode(" spm 7k2q-9xw4-m3td ") ?? "null");
        check("license: the owner's PC is recognised, others are not", LicenseKey.IsOwner("36953c6f-979d-44bc-88fa-e3f96db72bbb")
            && LicenseKey.IsOwner("36953C6F-979D-44BC-88FA-E3F96DB72BBB") && !LicenseKey.IsOwner("36953c6f-979d-44bc-88fa-e3f96db72bbc") && !LicenseKey.IsOwner(""), "");

        var real = LicenseKey.Read(RealKey);
        check("license: a key from the owner's signing secret is valid on its PC", real is { Name: "Test Person", PcCode: "SPM-TEST-0000-ABCD" }
            && real.Ends == new DateTime(2030, 1, 31) && LicenseKey.Verify(RealKey, "SPM-TEST-0000-ABCD", today) == LicenseKey.Check.Valid, real?.ToString() ?? "null");
        check("license: the same key on another PC fails", LicenseKey.Verify(RealKey, code, today) == LicenseKey.Check.WrongPc, "");
        check("license: the key ends after its end date", LicenseKey.Verify(RealKey, "SPM-TEST-0000-ABCD", new DateTime(2030, 1, 31)) == LicenseKey.Check.Valid
            && LicenseKey.Verify(RealKey, "SPM-TEST-0000-ABCD", new DateTime(2030, 2, 1)) == LicenseKey.Check.Expired, "");
        check("license: pasted with spaces and line breaks", LicenseKey.Verify(" " + RealKey.Insert(40, "\r\n ") + "\n", "SPM-TEST-0000-ABCD", today) == LicenseKey.Check.Valid, "");
        var payloadAt = LicenseKey.Prefix.Length + 30;
        var tampered = RealKey.Substring(0, payloadAt) + (RealKey[payloadAt] == 'A' ? 'B' : 'A') + RealKey.Substring(payloadAt + 1);
        check("license: an edited key is refused", LicenseKey.Verify(tampered, "SPM-TEST-0000-ABCD", today) == LicenseKey.Check.Invalid
            && LicenseKey.Verify("hello", code, today) == LicenseKey.Check.Invalid && LicenseKey.Verify("", code, today) == LicenseKey.Check.Invalid, "");

        // Someone else's signing key can't make keys this add-in takes.
        using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var forged = LicenseKey.Sign(new LicenseKey.License("ABCDEFGH", code, "X", today, null), other);
            check("license: a key signed by anyone else is refused", LicenseKey.Verify(forged, code, today) == LicenseKey.Check.Invalid
                && LicenseKey.Verify(forged, code, today, verifier: other) == LicenseKey.Check.Valid, "");
            var p = other.ExportParameters(true);
            var secret = $"SPMSIGN1:{Convert.ToBase64String(p.D!)}:{Convert.ToBase64String(p.Q.X!)}:{Convert.ToBase64String(p.Q.Y!)}";
            check("license: only the matching secret is accepted as the owner's", LicenseKey.Signer(secret) is not null && !LicenseKey.MatchesBuiltIn(secret)
                && LicenseKey.Signer("nonsense") is null, "");

            // The owner's changes, published online by key id.
            var changes = LicenseChanges.Parse("# comment\nABCDEFGH ends 2026-11-01\nZZZZZZZZ cancelled\nbad line\nABCDEFGH ends 2026-12-01\n");
            var l = new LicenseKey.License("ABCDEFGH", code, "X", today, new DateTime(2027, 1, 1));
            check("license: an earlier end date published online applies", LicenseKey.Verify(l, code, new DateTime(2026, 11, 2), changes) == LicenseKey.Check.Expired
                && LicenseKey.Verify(l, code, new DateTime(2026, 11, 1), changes) == LicenseKey.Check.Valid && LicenseKey.EndsOf(l, changes) == new DateTime(2026, 11, 1), "");
            check("license: a later date online never lengthens a key", LicenseKey.EndsOf(l, LicenseChanges.Parse("ABCDEFGH ends 2030-01-01")) == new DateTime(2027, 1, 1), "");
            check("license: a cancelled key stops", LicenseKey.Verify(l with { Id = "ZZZZZZZZ" }, code, today, changes) == LicenseKey.Check.Cancelled, "");
            check("license: change lines", LicenseChanges.Line("ABCDEFGH", new DateTime(2026, 11, 1)) == "ABCDEFGH ends 2026-11-01"
                && LicenseChanges.Line("ABCDEFGH", null) == "ABCDEFGH cancelled", "");

            // A separate key per program (the owner, 24 Nov 2026): old keys are Sketch ProMax's only; a Plaxis key names Plaxis.
            check("license programs: a key made before is for Sketch ProMax only", real!.For.SequenceEqual([LicenseKey.Programs.Sketch])
                && LicenseKey.Verify(RealKey, "SPM-TEST-0000-ABCD", today, program: LicenseKey.Programs.Plaxis) == LicenseKey.Check.OtherProgram, "");
            var sketchOnly = LicenseKey.Sign(new LicenseKey.License("SKETCH01", code, "S", today, null, [LicenseKey.Programs.Sketch]), other);
            check("license programs: a Sketch-only key is still made the old way (older versions read it)",
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(sketchOnly.Split('.')[1]))).StartsWith("1|SKETCH01|"), sketchOnly);
            var plaxisOnly = LicenseKey.Sign(new LicenseKey.License("PLAXIS01", code, "P", today, null, [LicenseKey.Programs.Plaxis]), other);
            check("license programs: a Plaxis key unlocks Plaxis ProMax, not Sketch ProMax",
                LicenseKey.Verify(plaxisOnly, code, today, verifier: other, program: LicenseKey.Programs.Plaxis) == LicenseKey.Check.Valid
                && LicenseKey.Verify(plaxisOnly, code, today, verifier: other) == LicenseKey.Check.OtherProgram, "");
            var both = LicenseKey.Sign(new LicenseKey.License("BOTH0001", code, "B", today, null, [LicenseKey.Programs.Plaxis, LicenseKey.Programs.Sketch]), other);
            check("license programs: a key for both unlocks both", LicenseKey.Verify(both, code, today, verifier: other) == LicenseKey.Check.Valid
                && LicenseKey.Verify(both, code, today, verifier: other, program: LicenseKey.Programs.Plaxis) == LicenseKey.Check.Valid
                && LicenseKey.Read(both, other)!.For.SequenceEqual([LicenseKey.Programs.Sketch, LicenseKey.Programs.Plaxis]), "");

            // Keys from Sketch ProMax's License manager since 0.35.119/0.35.120 end with the library key (25 Nov 2026).
            {
                string Signed(string payload) => LicenseKey.Prefix + B64(System.Text.Encoding.UTF8.GetBytes(payload)) + "." +
                    B64(other.SignData(System.Text.Encoding.UTF8.GetBytes(payload), System.Security.Cryptography.HashAlgorithmName.SHA256));
                var all3 = Signed($"2|ALL30001|{code}|Test Friend|2026-11-25|-|sketch,csi,plaxis|LIBRARYKEYTEXT");
                check("license programs: a key for all three apps with the library key unlocks Plaxis ProMax",
                    LicenseKey.Verify(all3, code, today, verifier: other, program: LicenseKey.Programs.Plaxis) == LicenseKey.Check.Valid
                    && LicenseKey.Read(all3, other)!.For.SequenceEqual(LicenseKey.Programs.All), all3);
                var sketchLib = Signed($"1|SKLIB001|{code}|Test Friend|2026-11-25|-|LIBRARYKEYTEXT");
                check("license programs: a Sketch-only key with the library key reads, and isn't for Plaxis ProMax",
                    LicenseKey.Read(sketchLib, other) is not null
                    && LicenseKey.Verify(sketchLib, code, today, verifier: other, program: LicenseKey.Programs.Plaxis) == LicenseKey.Check.OtherProgram, "");
            }
            check("license programs: still bound to its PC", LicenseKey.Verify(both, "SPM-TEST-0000-ABCD", today, verifier: other, program: LicenseKey.Programs.Plaxis) == LicenseKey.Check.WrongPc, "");

            check("plaxis license: Plaxis ProMax checks keys for Plaxis ProMax", LicenseState.Program == LicenseKey.Programs.Plaxis
                && LicenseKey.Verify(RealKey, "SPM-TEST-0000-ABCD", today, program: LicenseState.Program) == LicenseKey.Check.OtherProgram
                && LicenseState.Explain(LicenseKey.Check.OtherProgram).Contains("includes Plaxis ProMax"), "");
        }

        // Wiring: the gates on the chat and the tools, the License button, Sketch ProMax's changes list, no signing secret here.
        string Read(string rel) => File.ReadAllText(Path.Combine(root, rel));
        check("license: tools are off without a key", Read("src/PlaxisProMax.Core/Mcp/ChatMcp.cs").Contains("LicenseState.Allowed"), "");
        var window = Read("src/PlaxisProMax/ChatWindow.cs");
        check("license: the chat sends nothing without a key, and the window opens at start", window.Split("LicenseWindow.BlockIfLocked()").Length == 3, "");
        check("license: Settings has the License button", Read("src/PlaxisProMax/SettingsWindow.cs").Contains("LicenseWindow.Command()"), "");
        check("license: cancelled keys come from Sketch ProMax's list", LicenseState.ChangesUrl == "https://raw.githubusercontent.com/ahisham92/sketch-promax/main/licenses/changes.txt", LicenseState.ChangesUrl);
        var leaked = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && new FileInfo(f).Length < 2_000_000)
            .Where(f => { try { return File.ReadAllText(f).Contains("SPMSIGN1:" + "6"); } catch (IOException) { return false; } })
            .ToList();
        check("license: the owner's signing secret is not in the repository", leaked.Count == 0, string.Join(", ", leaked));
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
