using System.IO.Compression;
using System.Net;
using PlaxisProMax.Update;

/// <summary>
/// Self-update: the release GitHub describes, the download checked and unpacked into versions\&lt;v&gt; from a release server
/// on this machine, and the next start handing over to the newer version (and only once).
/// </summary>
internal static class UpdaterTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- The release GitHub's API describes
        var api = """
            {"tag_name":"v0.2.0","draft":false,"prerelease":false,"body":"Notes",
             "assets":[{"name":"other.txt","browser_download_url":"https://x/other.txt"},
                       {"name":"PlaxisProMax-0.2.0.zip","browser_download_url":"https://x/PlaxisProMax-0.2.0.zip","digest":"sha256:ABC"}]}
            """;
        var release = Updater.ParseRelease(api);
        check("updater: the release's package and digest are read", release is { Version: "0.2.0", ZipUrl: "https://x/PlaxisProMax-0.2.0.zip", Sha256: "ABC", Notes: "Notes" }, release?.ToString() ?? "null");
        check("updater: drafts and pre-releases are not offered",
            Updater.ParseRelease("""{"tag_name":"v0.3.0","draft":true}""") is null && Updater.ParseRelease("""{"tag_name":"v0.3.0","prerelease":true}""") is null, "");
        check("updater: a tag that is not a version is not offered", Updater.ParseRelease("""{"tag_name":"latest-build"}""") is null, "");
        var bare = Updater.ParseRelease("""{"tag_name":"v0.4.1","assets":[]}""");
        check("updater: a release without its package listed gets the usual address", bare?.ZipUrl == "https://github.com/ahisham92/plaxis-promax/releases/download/v0.4.1/PlaxisProMax-0.4.1.zip", bare?.ZipUrl ?? "");

        var dir = Path.Combine(Path.GetTempPath(), "ppm-updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var before = Updater.CurrentText;
        try
        {
            // ---- A package as the release workflow builds it: PlaxisProMax\dist\app\PlaxisProMax.exe
            var good = Path.Combine(dir, "good.zip");
            Package(good, ("PlaxisProMax/dist/app/PlaxisProMax.exe", "new program"), ("PlaxisProMax/dist/app/plaxis_worker.py", "worker"),
                ("PlaxisProMax/dist/Plaxis ProMax.py", "launcher"), ("PlaxisProMax/install.ps1", "installer"));
            var noExe = Path.Combine(dir, "noexe.zip");
            Package(noExe, ("PlaxisProMax/dist/app/readme.txt", "x"));
            var sha = UpdateResume.Sha256Of(good);
            check("updater: a package with the program passes without a digest", Updater.Verify(good, new Updater.Release("0.2.0", "", null)) == sha, "");
            check("updater: a package without the program is refused", Throws<InvalidDataException>(() => Updater.Verify(noExe, new Updater.Release("0.2.0", "", null))), "");
            check("updater: a package that doesn't match its digest is refused", Throws<InvalidDataException>(() => Updater.Verify(good, new Updater.Release("0.2.0", "", "00"))), "");

            // ---- Downloaded from a release server and unpacked into versions\0.2.0
            Updater.SetCurrent("0.1.0");
            using (var server = new ZipServer(File.ReadAllBytes(good)))
            {
                var progress = new UpdateProgress();
                var folder = Updater.DownloadAndUnpackAsync(new Updater.Release("0.2.0", server.Url, sha), progress, CancellationToken.None).GetAwaiter().GetResult();
                check("updater: the download is unpacked into versions\\0.2.0 with its program and worker",
                    UpdateResume.IsComplete(folder) && File.ReadAllText(Updater.ExeIn(folder)) == "new program"
                    && File.Exists(Path.Combine(folder, "app", "plaxis_worker.py")) && File.Exists(Path.Combine(folder, "Plaxis ProMax.py")), folder);
                check("updater: the downloaded version is the one ready for the next start", Updater.Staged == "0.2.0" && Updater.Describe().Contains("0.2.0 is downloaded"), Updater.Describe());
            }

            // ---- A half-unpacked newer version is never started
            var half = Path.Combine(Updater.VersionsFolder, "0.3.0");
            Directory.CreateDirectory(Path.Combine(half, "app"));
            File.WriteAllText(Updater.ExeIn(half), "half");
            check("updater: a version not completely unpacked is not used", Updater.NewestComplete() == "0.2.0", Updater.NewestComplete() ?? "null");

            // ---- The next start hands over once
            var target = Updater.HandOverTarget(_ => null);
            check("updater: an older program hands over to the downloaded version",
                target is not null && Path.GetFullPath(target) == Path.GetFullPath(Updater.ExeIn(Path.Combine(Updater.VersionsFolder, "0.2.0"))), target ?? "null");
            check("updater: a start that was handed over never hands over again", Updater.HandOverTarget(v => v == "PLAXISPROMAX_HANDED_OVER" ? "1" : null) is null, "");
            Updater.SetCurrent("0.2.0");
            check("updater: the same version doesn't hand over", Updater.HandOverTarget(_ => null) is null && Updater.Staged is null, "");
            Updater.SetCurrent("0.5.0");
            check("updater: a newer installed program (a fresh Install.cmd) ignores an older download", Updater.HandOverTarget(_ => null) is null, "");
        }
        finally
        {
            Updater.SetCurrent(before);
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    private static void Package(string file, params (string Name, string Text)[] entries)
    {
        using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    /// <summary>Serves one zip on 127.0.0.1, as GitHub serves a release file.</summary>
    private sealed class ZipServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _content;
        public string Url { get; } = "";

        public ZipServer(byte[] content)
        {
            _content = content;
            for (var port = 18631; ; port++)
            {
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    Url = $"http://127.0.0.1:{port}/PlaxisProMax-0.2.0.zip";
                    break;
                }
                catch (HttpListenerException) when (port < 18730) { }
            }
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (Exception) { return; }
                    try
                    {
                        context.Response.Headers["ETag"] = "\"v1\"";
                        context.Response.ContentLength64 = _content.Length;
                        await context.Response.OutputStream.WriteAsync(_content);
                        context.Response.Close();
                    }
                    catch (Exception) { }
                }
            });
        }

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch (Exception) { } }
    }
}
