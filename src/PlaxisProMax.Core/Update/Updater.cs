using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace PlaxisProMax.Update;

/// <summary>
/// Keeps Plaxis ProMax up to date without reinstalling. Sketch ProMax's updater (the same release lookup, resumable
/// download, checksum and unpack), with the switch-over made for a program of its own rather than a Revit add-in.
///
/// When the window opens and hourly it asks GitHub for the latest release. With automatic updates off it only says a
/// newer one is out. Otherwise it downloads the zip to %LOCALAPPDATA%\PlaxisProMax\updates and unpacks its dist folder
/// into %LOCALAPPDATA%\PlaxisProMax\versions\&lt;version&gt;, marked complete only when whole. Nothing that is running is
/// overwritten. The next time Plaxis ProMax starts (from Plaxis, the Start menu, or the window's Restart to update), the
/// installed program sees the newer complete version and hands over to it (<see cref="HandOver"/>): it starts that
/// version's PlaxisProMax.exe with the same arguments and Plaxis's password, and exits. Settings, chats, keys and kept code
/// live in %APPDATA% and are never touched.
/// </summary>
internal static class Updater
{
    public const string Repository = "ahisham92/plaxis-promax";

    /// <summary>Set by the tests, so their downloads and versions go to a temporary folder.</summary>
    internal static string? RootOverride;

    private static string LocalRoot => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlaxisProMax");

    private static string UpdatesFolder => Path.Combine(LocalRoot, "updates");
    internal static string VersionsFolder => Path.Combine(LocalRoot, "versions");
    private static string StateFile => Path.Combine(LocalRoot, "update-state.json");

    /// <summary>The environment mark a handed-over start carries, so it never hands over again (no loop, whatever the versions say).</summary>
    internal const string HandedOverVariable = "PLAXISPROMAX_HANDED_OVER";

    /// <summary>This program's version. The window sets it from Program.Version at start.</summary>
    public static Version Current { get; private set; } = Normalize(typeof(Updater).Assembly.GetName().Version);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    public static void SetCurrent(string version) => Current = Parse(version);

    /// <summary>A version downloaded and ready for the next start, if newer than this one.</summary>
    public static string? Staged => NewestComplete() is { } v && Parse(v) > Current ? v : null;

    /// <summary>The newest release the last check saw, when it is newer than this version.</summary>
    public static string? Latest => LoadState().Latest is { } l && Parse(l) > Current ? l : null;

    /// <summary>The release notes the last check read for <paramref name="version"/>, or null.</summary>
    public static string? NotesFor(string version) => LoadState() is { Latest: { } l, Notes: { } n } && Parse(l) == Parse(version) ? n : null;

    /// <summary>True when the last check is at least <paramref name="every"/> old, or there was none, or the clock went back.</summary>
    public static bool IsDue(TimeSpan every)
    {
        var last = LoadState().LastCheck;
        var now = DateTime.UtcNow;
        return last is not { } l || l > now || now - l >= every;
    }

    /// <summary>One line for Settings and for Claude.</summary>
    public static string Describe()
    {
        var state = LoadState();
        if (Staged is { } staged)
            return $"Plaxis ProMax {staged} is downloaded. It is used from the next start (or press Restart to update).";
        if (Latest is { } latest)
            return $"Plaxis ProMax {latest} is available (this is {CurrentText}). Press Check for updates to download it.";
        if (state.LastError is { } error)
            return $"Plaxis ProMax {CurrentText}. The last update check failed: {error}";
        return state.LastCheck is { } last
            ? $"Plaxis ProMax {CurrentText} is up to date (checked {last.ToLocalTime():g})."
            : $"Plaxis ProMax {CurrentText}.";
    }

    private static int _running;

    /// <summary>
    /// Checks for and downloads an update, reporting each stage to <paramref name="progress"/>. Returns the version now
    /// ready for the next start, or null when there is nothing new. With <paramref name="install"/> false (automatic
    /// updates off) a newer release is only recorded and reported as <see cref="UpdateStage.Available"/>.
    /// </summary>
    public static async Task<string?> CheckAsync(bool force, UpdateProgress? progress = null, CancellationToken ct = default, bool install = true)
    {
        progress ??= new UpdateProgress();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, progress.Cancellation.Token);
        ct = linked.Token;
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            progress.Report(UpdateStage.Failed, "Already checking for updates in the background. Try again in a minute.");
            return null;
        }
        // One window updates at a time. A lock file, because a mutex can't be released after an await.
        FileStream? gate = null;
        try
        {
            try
            {
                Directory.CreateDirectory(LocalRoot);
                gate = new FileStream(Path.Combine(LocalRoot, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                progress.Report(UpdateStage.Failed, "Another Plaxis ProMax window is updating right now. This one uses its download.");
                return null;
            }

            CleanUp();
            var state = LoadState();
            if (!force && !IsDue(TimeSpan.FromHours(1)))
            {
                progress.Report(UpdateStage.Idle);
                return null;
            }

            progress.Report(UpdateStage.Checking, "Asking GitHub for the latest release…");
            var release = await LatestReleaseAsync(ct).ConfigureAwait(false);
            state.LastCheck = DateTime.UtcNow;
            state.LastError = null;
            state.Latest = release?.Version;
            state.Notes = release?.Notes is { Length: > 4000 } full ? full.Substring(0, 4000) : release?.Notes;
            if (release is null || Parse(release.Version) <= Current)
            {
                SaveState(state);
                progress.Report(UpdateStage.UpToDate, $"Plaxis ProMax {CurrentText} is the latest version.", CurrentText);
                return null;
            }
            var target = Path.Combine(VersionsFolder, release.Version);
            if (UpdateResume.IsComplete(target))
            {
                SaveState(state);
                progress.Report(UpdateStage.RestartNeeded, $"Plaxis ProMax {release.Version} is downloaded. It is used from the next start.", release.Version);
                return release.Version;
            }
            if (!install)
            {
                SaveState(state);
                progress.Report(UpdateStage.Available, $"Plaxis ProMax {release.Version} is available. Automatic updates are off, so nothing was downloaded.", release.Version);
                return null;
            }
            // What was found is kept even if the download then fails: Settings still offers the new version.
            SaveState(state);
            await DownloadAndUnpackAsync(release, progress, ct).ConfigureAwait(false);
            if (!File.Exists(ExeIn(target)))
                throw new InvalidDataException("The update package has no PlaxisProMax.exe.");
            progress.Report(UpdateStage.RestartNeeded, $"Plaxis ProMax {release.Version} is downloaded. It is used from the next start, or press Restart to update.", release.Version);
            Tools.ActivityLog.Note("update", $"{release.Version} downloaded (this is {CurrentText})");
            return release.Version;
        }
        catch (OperationCanceledException) when (progress.Cancellation.IsCancellationRequested)
        {
            progress.Report(UpdateStage.Cancelled, "The update was cancelled. Nothing changed; it is offered again at the next check.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or JsonException or InvalidDataException or OperationCanceledException or InvalidOperationException
                                       or TimeoutException)
        {
            var state = LoadState();
            state.LastCheck = DateTime.UtcNow;
            state.LastError = ex is OperationCanceledException ? "the download took too long" : ex.Message;
            SaveState(state);
            progress.Report(UpdateStage.Failed, $"The update didn't finish: {state.LastError}. Nothing changed. " +
                                                "Try again (or the next automatic check) continues from where it stopped.");
            return null;
        }
        finally
        {
            gate?.Dispose();
            Interlocked.Exchange(ref _running, 0);
        }
    }

    // ---- Handing over to a newer version ----------------------------------------------------------------------------

    /// <summary>The program inside a version folder.</summary>
    internal static string ExeIn(string versionFolder) => Path.Combine(versionFolder, "app", "PlaxisProMax.exe");

    /// <summary>The newest version folder that is completely unpacked and holds the program, or null.</summary>
    internal static string? NewestComplete()
    {
        try
        {
            if (!Directory.Exists(VersionsFolder)) return null;
            return Directory.GetDirectories(VersionsFolder)
                .Select(Path.GetFileName)
                .Where(n => n is not null && Version.TryParse(n, out _))
                .Select(n => n!)
                .Where(n => UpdateResume.IsComplete(Path.Combine(VersionsFolder, n)) && File.Exists(ExeIn(Path.Combine(VersionsFolder, n))))
                .OrderByDescending(Parse)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// What to start instead of this program: the newest downloaded version's PlaxisProMax.exe when it is newer than this
    /// one, and this start was not itself handed over. Null to carry on here.
    /// </summary>
    internal static string? HandOverTarget(Func<string, string?> env)
    {
        if (env(HandedOverVariable) is { Length: > 0 }) return null;
        if (Staged is not { } staged) return null;
        var exe = ExeIn(Path.Combine(VersionsFolder, staged));
        var running = Environment.ProcessPath ?? "";
        return string.Equals(Path.GetFullPath(exe), running.Length > 0 ? Path.GetFullPath(running) : "", StringComparison.OrdinalIgnoreCase) ? null : exe;
    }

    /// <summary>
    /// Starts <paramref name="exe"/> with <paramref name="args"/> and the environment as it is (Plaxis's password
    /// included, which the new start then clears for its own children). True when it started; on false the caller carries
    /// on with this version, so a damaged download never stops Plaxis ProMax from opening.
    /// </summary>
    internal static bool HandOver(string exe, IEnumerable<string> args, string? password = null)
    {
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in args) start.ArgumentList.Add(a);
            start.Environment[HandedOverVariable] = "1";
            if (password is not null) start.Environment["PLAXISPROMAX_PASSWORD"] = password;
            using var process = Process.Start(start);
            Tools.ActivityLog.Note("update", $"handed over from {CurrentText} to {exe}");
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Tools.ActivityLog.Note("update", $"could not hand over to {exe}: {ex.Message}");
            return false;
        }
    }

    // ---- Release lookup and download (Sketch ProMax's) --------------------------------------------------------------

    internal sealed record Release(string Version, string ZipUrl, string? Sha256, string? Notes = null);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PlaxisProMax/{CurrentText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The latest release: GitHub's API first, with a short time limit; on any failure of it the github.com release page.</summary>
    internal static async Task<Release?> LatestReleaseAsync(CancellationToken ct)
    {
        var (release, _) = await ReleaseSource.ApiThenWebAsync(LatestReleaseFromApiAsync, LatestReleaseFromWebAsync, ReleaseSource.ApiTimeout, ct).ConfigureAwait(false);
        return release;
    }

    private static async Task<ReleaseSource.Answer<Release>> LatestReleaseFromApiAsync(CancellationToken ct)
    {
        using var http = CreateClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return ReleaseSource.Found<Release>(null); // no release published yet
        if (!response.IsSuccessStatusCode) return ReleaseSource.Fallback<Release>($"GitHub's API answered {(int)response.StatusCode}");
        return ReleaseSource.Found(ParseRelease(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
    }

    /// <summary>The release GitHub's API describes, or null for a draft, a pre-release or a tag that is not a version.</summary>
    internal static Release? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        if (!Version.TryParse(version, out _)) return null;
        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("PlaxisProMax-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                // GitHub publishes a "sha256:..." digest for each release file; it is checked when present.
                var digest = asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest.Substring(7) : null;
                return new Release(version, asset.GetProperty("browser_download_url").GetString()!, sha, notes);
            }
        // A newer release whose package isn't listed (yet): its package has the usual name; when it is not there the
        // download fails, the error is shown and the next check tries again.
        return new Release(version, ReleaseSource.DownloadUrl(Repository, tag, version), null, notes);
    }

    private static async Task<Release?> LatestReleaseFromWebAsync(CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PlaxisProMax/{CurrentText}");
        using var response = await http.GetAsync($"https://github.com/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (ReleaseSource.TagFromRedirect(response.Headers.Location?.ToString()) is not { } tag)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            throw new HttpRequestException($"GitHub did not say which release is the latest ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }
        var version = tag.TrimStart('v', 'V');
        if (!Version.TryParse(version, out _)) throw new InvalidDataException($"The latest release tag '{tag}' is not a version number.");
        return new Release(version, ReleaseSource.DownloadUrl(Repository, tag, version), null);
    }

    /// <summary>
    /// Downloads and unpacks <paramref name="release"/> into versions\&lt;version&gt; and returns that folder. An earlier try
    /// that stopped part way is continued, not started again (<see cref="UpdateResume"/>).
    /// </summary>
    internal static async Task<string> DownloadAndUnpackAsync(Release release, UpdateProgress progress, CancellationToken ct)
    {
        var target = Path.Combine(VersionsFolder, release.Version);
        if (UpdateResume.IsComplete(target))
        {
            progress.Report(UpdateStage.Unpacking, $"Plaxis ProMax {release.Version} was already downloaded and unpacked by an earlier try.", release.Version);
            return target;
        }
        Directory.CreateDirectory(UpdatesFolder);
        var zip = Path.Combine(UpdatesFolder, $"PlaxisProMax-{release.Version}.zip");

        string? sha = null;
        if (File.Exists(zip))
        {
            progress.Report(UpdateStage.Verifying, "Checking the download an earlier try finished…", release.Version);
            try { sha = Verify(zip, release); }
            catch (InvalidDataException) { UpdateResume.Discard(zip); }
        }
        if (sha is null)
        {
            var have = UpdateResume.Downloaded(zip);
            progress.Report(UpdateStage.Downloading, have > 0
                ? $"Continuing the download of Plaxis ProMax {release.Version} from {UpdateSnapshot.Megabytes(have)}…"
                : $"Downloading Plaxis ProMax {release.Version}…", release.Version);
            using (var http = CreateClient())
                await UpdateResume.DownloadAsync(http, release.ZipUrl, zip, progress.ReportBytes, UpdateResume.Stall, ct).ConfigureAwait(false);
            progress.Report(UpdateStage.Verifying, "Checking the download is complete and undamaged…");
            try { sha = Verify(zip, release); }
            catch (InvalidDataException)
            {
                UpdateResume.Discard(zip);
                throw;
            }
        }
        var temp = target + ".partial";
        progress.Report(UpdateStage.Unpacking, $"Unpacking Plaxis ProMax {release.Version}…");
        int kept;
        try { kept = UpdateResume.Extract(zip, temp, sha); }
        catch (InvalidDataException)
        {
            UpdateResume.Discard(zip);
            throw;
        }
        if (kept > 0) progress.Report(UpdateStage.Unpacking, $"Unpacking Plaxis ProMax {release.Version} (continuing: {kept} files were already unpacked)…");
        var dist = Directory.GetDirectories(temp, "dist", SearchOption.AllDirectories).FirstOrDefault()
                   ?? throw new InvalidDataException("The update package has no dist folder.");
        if (Directory.Exists(target)) UpdateResume.WithRetry(() => Directory.Delete(target, true));
        UpdateResume.WithRetry(() => Directory.Move(dist, target));
        UpdateResume.MarkComplete(target, release.Version);
        TryDelete(() => Directory.Delete(temp, true));
        TryDelete(() => File.Delete(zip));
        return target;
    }

    /// <summary>
    /// Checks a downloaded zip against the published digest, or (without one) that it opens and holds the program.
    /// Returns its SHA-256; throws <see cref="InvalidDataException"/> when it is damaged or incomplete.
    /// </summary>
    internal static string Verify(string zip, Release release)
    {
        var actual = UpdateResume.Sha256Of(zip);
        if (release.Sha256 is { } expected)
        {
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update is damaged (checksum mismatch). The next try downloads it again.");
            return actual;
        }
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            if (!archive.Entries.Any(e => e.FullName.Replace('\\', '/').EndsWith("dist/app/PlaxisProMax.exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The downloaded update is incomplete (no PlaxisProMax.exe). The next try downloads it again.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException) { throw new InvalidDataException("The downloaded update can't be opened: " + ex.Message, ex); }
        return actual;
    }

    /// <summary>
    /// Removes downloads of other versions and version folders that are neither this one, the newest downloaded, nor
    /// running. A folder whose program is running can't be renamed, so it is never half deleted.
    /// </summary>
    private static void CleanUp()
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CurrentText };
        if (NewestComplete() is { } newest) keep.Add(newest);
        if (LoadState().Latest is { } latest)
        {
            keep.Add(latest);
            keep.Add(latest + ".partial");
        }
        if (Directory.Exists(UpdatesFolder))
            foreach (var file in Directory.GetFiles(UpdatesFolder, "PlaxisProMax-*"))
            {
                if (keep.Any(k => Path.GetFileName(file).StartsWith($"PlaxisProMax-{k}.zip", StringComparison.OrdinalIgnoreCase))) continue;
                TryDelete(() => File.Delete(file));
            }
        if (!Directory.Exists(VersionsFolder)) return;
        var running = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        foreach (var folder in Directory.GetDirectories(VersionsFolder))
        {
            var name = Path.GetFileName(folder);
            if (keep.Contains(name)) continue;
            if (running.Length > 0 && running.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var doomed = folder.EndsWith(".del", StringComparison.OrdinalIgnoreCase) ? folder : folder + ".del";
            try { if (doomed != folder) Directory.Move(folder, doomed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            TryDelete(() => Directory.Delete(doomed, true));
        }
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static Version Parse(string text) => Normalize(Version.TryParse(text, out var v) ? v : null);

    private static Version Normalize(Version? v) => v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    private sealed class State
    {
        public DateTime? LastCheck { get; set; }
        public string? LastError { get; set; }
        public string? Latest { get; set; }
        public string? Notes { get; set; }
    }

    private static State LoadState()
    {
        try
        {
            if (File.Exists(StateFile)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }

    private static void SaveState(State state)
    {
        try
        {
            Directory.CreateDirectory(LocalRoot);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch (IOException) { }
    }
}
