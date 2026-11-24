using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.Win32;

using PlaxisProMax.Settings;

namespace PlaxisProMax.Licensing;

/// <summary>
/// Whether this PC may use Plaxis ProMax (ported from Sketch ProMax, with the same keys): the owner's PCs always may; any
/// other PC needs a key from Sketch ProMax's License manager for its PC code (UI/LicenseWindow). Keys name the programs
/// they unlock; a Sketch ProMax key on this PC that includes Plaxis ProMax is used without typing it again. Files live in %APPDATA%\PlaxisProMax\license: key.txt
/// (a key typed here) and changes.txt (the owner's later changes, last read online from Sketch ProMax's list).
/// </summary>
internal static class LicenseState
{
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlaxisProMax", "license");
    private static string KeyFile => Path.Combine(Folder, "key.txt");
    private static string ChangesFile => Path.Combine(Folder, "changes.txt");
    /// <summary>Sketch ProMax's own key on this PC, used when none was typed here.</summary>
    private static string SketchKeyFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "license", "key.txt");

    /// <summary>Where the owner's changes are published: Sketch ProMax's list (licenses/changes.txt on its main), the same keys.</summary>
    internal const string ChangesUrl = "https://raw.githubusercontent.com/ahisham92/sketch-promax/main/licenses/changes.txt";

    /// <summary>The program keys are checked for: a key from the License manager must include Plaxis ProMax.</summary>
    internal const string Program = LicenseKey.Programs.Plaxis;

    internal enum Kind { Owner, Licensed, Locked }

    internal sealed record Status(Kind Kind, string PcCode, LicenseKey.License? License, LicenseKey.Check Check, DateTime? Ends)
    {
        public bool Allowed => Kind != Kind.Locked;
    }

    private static Status? _current;
    private static readonly object Gate = new();

    /// <summary>Tests set this to stand in for the PC's own state.</summary>
    internal static Func<Status>? Override { get; set; }

    public static Status Current
    {
        get
        {
            if (Override is { } o) return o();
            lock (Gate) return _current ??= Evaluate();
        }
    }

    public static bool Allowed => Current.Allowed;

    public static bool IsOwnerPc => Current.Kind == Kind.Owner;

    /// <summary>Reads everything again (after a key is entered or the changes list arrives).</summary>
    public static Status Refresh()
    {
        if (Override is { } o) return o();
        lock (Gate) return _current = Evaluate();
    }

    private static Status Evaluate()
    {
        var id = MachineId();
        var code = LicenseKey.PcCode(id);
        if (LicenseKey.IsOwner(id)) return new Status(Kind.Owner, code, null, LicenseKey.Check.Valid, null);
        // A key typed here, else Sketch ProMax's key on this PC when it includes Plaxis ProMax.
        var key = ReadText(KeyFile);
        if (key is null && ReadText(SketchKeyFile) is { } sketch && LicenseKey.Read(sketch)?.Covers(LicenseKey.Programs.Plaxis) == true) key = sketch;
        var changes = LicenseChanges.Parse(ReadText(ChangesFile));
        var license = LicenseKey.Read(key);
        var check = code.Length == 0 ? LicenseKey.Check.WrongPc : LicenseKey.Verify(license, code, DateTime.Today, changes, Program);
        var ends = license is null ? null : LicenseKey.EndsOf(license, changes);
        return new Status(check == LicenseKey.Check.Valid ? Kind.Licensed : Kind.Locked, code, license, check, ends);
    }

    /// <summary>Checks <paramref name="key"/> for this PC; saves it and unlocks when it is good. Returns the check.</summary>
    public static LicenseKey.Check Activate(string key)
    {
        var code = Current.PcCode;
        var changes = LicenseChanges.Parse(ReadText(ChangesFile));
        var check = LicenseKey.Verify(key, code, DateTime.Today, changes, program: Program);
        if (check == LicenseKey.Check.Valid)
        {
            Directory.CreateDirectory(Folder);
            SafeFile.Write(KeyFile, new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray()));
            Refresh();
        }
        return check;
    }

    /// <summary>What a check means, in the user's words.</summary>
    public static string Explain(LicenseKey.Check check, DateTime? ends = null) => check switch
    {
        LicenseKey.Check.Valid => "The key is valid for this PC.",
        LicenseKey.Check.WrongPc => "This key belongs to another PC. Send your PC code (above) to whoever gave you Plaxis ProMax (a key for this PC that includes Plaxis ProMax) to get a key for this PC.",
        LicenseKey.Check.Expired => ends is { } e ? $"This key ended on {e:d MMM yyyy}. Ask for a new key." : "This key has ended. Ask for a new key.",
        LicenseKey.Check.OtherProgram => "This key is for Sketch ProMax only. Ask whoever gave it to you for a key that includes Plaxis ProMax (your PC code stays the same).",
        LicenseKey.Check.Cancelled => "This key was cancelled. Ask whoever gave you Plaxis ProMax for a new key.",
        _ => "That isn't a Plaxis ProMax key. Paste the whole key, starting with SPM1.",
    };

    /// <summary>The Windows machine ID (HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid), or "" when it can't be read.</summary>
    internal static string MachineId()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string ?? "";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ""; }
    }

    /// <summary>
    /// Reads the owner's changes online and keeps them for offline days; a key cancelled or shortened there stops working
    /// at once. Owner PCs skip it. Silent on failure: the last list read stays in force.
    /// </summary>
    public static async Task RefreshChangesAsync(CancellationToken ct)
    {
        if (IsOwnerPc) return;
        try
        {
#if NETFRAMEWORK
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.GetAsync(ChangesUrl + "?t=" + DateTime.UtcNow.Ticks, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (text.Length > 1_000_000) return;
            if (text == ReadText(ChangesFile)) return;
            Directory.CreateDirectory(Folder);
            SafeFile.Write(ChangesFile, text);
            Refresh();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException) { }
    }

    private static string? ReadText(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
