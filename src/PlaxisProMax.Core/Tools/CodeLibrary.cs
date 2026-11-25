using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlaxisProMax.Settings;

namespace PlaxisProMax.Tools;

/// <summary>One script that ran in Plaxis and worked, kept to be run again instead of written again.</summary>
public sealed class KeptCode
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Code { get; set; } = "";
    /// <summary>What the code touches in Plaxis (g_i.borehole, g_o.getresults, ResultTypes.Soil.Utot...): how it is found.</summary>
    public List<string> Identifiers { get; set; } = [];
    /// <summary>The inputs it reads from `inputs` (inputs["phase"] → phase), so a rerun knows what to pass.</summary>
    public List<string> Inputs { get; set; } = [];
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime LastUsed { get; set; }
    public int Uses { get; set; }
    /// <summary>How many times it was adapted (based_on) rather than kept as a near copy (Sketch ProMax bug 98).</summary>
    public int Revision { get; set; }
    /// <summary>The user allowed it to run (or nothing asked): run again unchanged, it doesn't ask again.</summary>
    public bool Allowed { get; set; }
    public string Hash { get; set; } = "";
}

/// <summary>
/// The kept-code library: every script that worked, one file each, found by what it does in Plaxis. This is the
/// answer to Sketch ProMax's biggest lesson (bugs 98, 127 to 136): its jobs wrote new code about 1,500 times a day and
/// reused working code 42 times, because kept code was offered on shared words of a description (bug 136) and copies
/// that changed a little were kept as new entries (bug 98). Here:
/// <list type="bullet">
/// <item>entries are matched on the Plaxis objects and commands the code uses, not on words;</item>
/// <item>new code that nearly matches a kept entry is held once, with that entry, before it runs (<see cref="Closest"/>);</item>
/// <item>adapted code replaces the entry it was based on, as a new revision, instead of adding a near copy;</item>
/// <item>kept code the user allowed runs again without asking, so reuse is the easy path.</item>
/// </list>
/// </summary>
public sealed class CodeLibrary
{
    private readonly string _folder;
    private readonly object _gate = new();
    private readonly HashSet<string> _offered = new(StringComparer.Ordinal);
    private List<KeptCode>? _entries;

    public CodeLibrary(string? folder = null) =>
        _folder = folder ?? Path.Combine(AgentSettings.Folder, "kept-code");

    public string Folder => _folder;

    public IReadOnlyList<KeptCode> All
    {
        get
        {
            lock (_gate) return Entries().ToList();
        }
    }

    public KeptCode? Get(string id)
    {
        lock (_gate) return Entries().FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Matching -------------------------------------------------------------------------------------------------

    private static readonly Regex Member = new(@"\b(g_i|g_o|s_i|s_o)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
    private static readonly Regex ResultType = new(@"ResultTypes\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)(?:\s*\.\s*([A-Za-z_][A-Za-z0-9_]*))?", RegexOptions.CultureInvariant);
    private static readonly Regex Command = new(@"call_and_handle_commands?\s*\(\s*[rbuf]*[""']\s*([A-Za-z_]+)", RegexOptions.CultureInvariant);
    private static readonly Regex Input = new(@"\binputs\s*(?:\[\s*[""']([A-Za-z_][A-Za-z0-9_]*)[""']\s*\]|\.get\(\s*[""']([A-Za-z_][A-Za-z0-9_]*)[""'])", RegexOptions.CultureInvariant);

    /// <summary>What a script touches in Plaxis, lower case: "g_i.borehole", "resulttypes.soil.utot", "cmd:mesh".</summary>
    public static List<string> Identifiers(string code)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Member.Matches(code))
            ids.Add((m.Groups[1].Value + "." + m.Groups[2].Value).ToLowerInvariant());
        foreach (Match m in ResultType.Matches(code))
            ids.Add(("resulttypes." + m.Groups[1].Value + (m.Groups[2].Success ? "." + m.Groups[2].Value : "")).ToLowerInvariant());
        foreach (Match m in Command.Matches(code))
            ids.Add("cmd:" + m.Groups[1].Value.ToLowerInvariant());
        return ids.ToList();
    }

    public static List<string> InputsOf(string code) =>
        Input.Matches(code).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Distinct(StringComparer.Ordinal).ToList();

    private static double Jaccard(ICollection<string> a, ICollection<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    /// <summary>How much alike two scripts are, by what they touch in Plaxis (0 to 1).</summary>
    public static double Likeness(string codeA, string codeB) => Jaccard(Identifiers(codeA), Identifiers(codeB));

    /// <summary>Close enough to hold new code for: most of what both touch is shared, and at least three things.</summary>
    internal const double HoldLikeness = 0.6;

    /// <summary>
    /// The kept entry that new <paramref name="code"/> nearly repeats, or null. Only when the code itself looks like it
    /// (Sketch ProMax 0.35.117: offers made on a description's words were ignored, 47 offers and one taken).
    /// </summary>
    public KeptCode? Closest(string code)
    {
        var ids = Identifiers(code);
        if (ids.Count < 3) return null;
        var hash = HashOf(code);
        lock (_gate)
            return Entries()
                .Where(e => e.Hash != hash)
                .Select(e => (Entry: e, Score: Jaccard(ids, e.Identifiers), Shared: ids.Count(e.Identifiers.Contains)))
                .Where(x => x.Score >= HoldLikeness && x.Shared >= 3)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.Entry.Uses)
                .Select(x => x.Entry).FirstOrDefault();
    }

    /// <summary>True the first time <paramref name="entry"/> is offered in <paramref name="chat"/>: each offer is made once.</summary>
    public bool OfferOnce(string chat, KeptCode entry)
    {
        lock (_gate) return _offered.Add(chat + "\u0001" + entry.Id);
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "into", "that", "this", "each", "all", "every", "of", "to", "in", "on", "a", "an",
        "plaxis", "model", "code", "script", "get", "set", "make", "read", "list", "show",
    };

    private static HashSet<string> Words(string text) =>
        new(Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value).Where(w => w.Length >= 3 && !Stop.Contains(w)), StringComparer.Ordinal);

    /// <summary>
    /// Kept code for a task: by the Plaxis objects and commands it would touch (<paramref name="touches"/>, the strongest
    /// signal), and by most of the words of <paramref name="what"/> (never by one or two shared words, bug 136).
    /// </summary>
    public List<(KeptCode Entry, double Score)> Find(string what, IReadOnlyCollection<string> touches, int limit = 8)
    {
        var words = Words(what);
        var ids = touches.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToList();
        lock (_gate)
            return Entries()
                .Select(e =>
                {
                    var idScore = ids.Count == 0 ? 0 : ids.Count(id => e.Identifiers.Any(x => x == id || x.EndsWith("." + id, StringComparison.Ordinal))) / (double)ids.Count;
                    var entryWords = Words(e.Description);
                    var wordScore = words.Count == 0 ? 0 : words.Count(entryWords.Contains) / (double)words.Count;
                    var score = ids.Count > 0 ? 0.7 * idScore + 0.3 * wordScore : wordScore;
                    var enough = ids.Count > 0 ? idScore >= 0.5 : wordScore >= 0.5 && words.Count(entryWords.Contains) >= 2;
                    return (Entry: e, Score: enough ? score : 0);
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.Entry.Uses)
                .Take(limit).ToList();
    }

    // ---- Keeping --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Keeps code that worked. The same code again counts a use; code adapted from an entry (<paramref name="basedOn"/>)
    /// replaces that entry as its next revision; anything else is a new entry.
    /// </summary>
    public KeptCode Keep(string code, string description, string? basedOn, string product, string version, bool allowed)
    {
        var hash = HashOf(code);
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var same = Entries().FirstOrDefault(e => e.Hash == hash);
            if (same is not null)
            {
                same.Uses++;
                same.LastUsed = now;
                same.Allowed |= allowed;
                Save(same);
                return same;
            }
            var entry = basedOn is { Length: > 0 } ? Entries().FirstOrDefault(e => e.Id.Equals(basedOn, StringComparison.OrdinalIgnoreCase)) : null;
            if (entry is not null)
            {
                entry.Code = code;
                entry.Hash = hash;
                entry.Revision++;
                entry.Uses++;
                entry.LastUsed = now;
                entry.Allowed = allowed;
                if (description.Length > 0) entry.Description = description;
            }
            else
            {
                entry = new KeptCode { Id = NewId(), Description = description, Code = code, Hash = hash, Created = now, LastUsed = now, Uses = 1, Allowed = allowed };
                Entries().Add(entry);
            }
            entry.Identifiers = Identifiers(code);
            entry.Inputs = InputsOf(code);
            entry.Product = product;
            entry.Version = version;
            Save(entry);
            return entry;
        }
    }

    /// <summary>One more run of an entry, unchanged.</summary>
    public void Used(KeptCode entry)
    {
        lock (_gate)
        {
            entry.Uses++;
            entry.LastUsed = DateTime.UtcNow;
            Save(entry);
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var entry = Entries().FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return false;
            Entries().Remove(entry);
            try { File.Delete(FileOf(entry)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return true;
        }
    }

    /// <summary>The most used entries, one line each, for the chat's instructions (so every chat sees them, bug 128).</summary>
    public string Catalog(int limit = 25)
    {
        var top = All.OrderByDescending(e => e.Uses).ThenByDescending(e => e.LastUsed).Take(limit).ToList();
        if (top.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var e in top)
            sb.Append("- ").Append(e.Id).Append(": ").Append(e.Description)
              .Append(e.Inputs.Count > 0 ? " (inputs: " + string.Join(", ", e.Inputs) + ")" : "")
              .Append(" [used ").Append(e.Uses).Append("x]\n");
        return sb.ToString();
    }

    internal static string HashOf(string code)
    {
        var normal = Regex.Replace(code.Replace("\r\n", "\n"), @"[ \t]+\n", "\n").Trim();
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(normal)).Take(12).Select(b => b.ToString("x2")));
    }

    private string NewId()
    {
        var n = Entries().Select(e => e.Id.StartsWith("K", StringComparison.Ordinal) && int.TryParse(e.Id.AsSpan(1), out var i) ? i : 0).DefaultIfEmpty(0).Max();
        return "K" + (n + 1);
    }

    private string FileOf(KeptCode entry) => Path.Combine(_folder, entry.Id + ".json");

    private void Save(KeptCode entry)
    {
        Directory.CreateDirectory(_folder);
        SafeFile.Write(FileOf(entry), JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
    }

    private List<KeptCode> Entries()
    {
        if (_entries is not null) return _entries;
        var list = new List<KeptCode>();
        if (Directory.Exists(_folder))
            foreach (var file in Directory.GetFiles(_folder, "K*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<KeptCode>(File.ReadAllText(file)) is { Id.Length: > 0 } e) list.Add(e);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
        return _entries = list;
    }
}
