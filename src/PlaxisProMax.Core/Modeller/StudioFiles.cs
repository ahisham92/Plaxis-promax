using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace PlaxisProMax.Modeller;

/// <summary>
/// The Modeller and Viewer pages (one HTML file each, with the shared style and code put inside, so each works on its own
/// with nothing beside it), the stand-alone Plaxis script, and opening a page in a window of its own.
/// </summary>
public static class StudioFiles
{
    /// <summary>A page with common.css and common.js inlined.</summary>
    public static string Page(string name)
    {
        var html = ModelFile.Resource("PlaxisProMax.web." + name + ".html");
        var css = ModelFile.Resource("PlaxisProMax.web.common.css");
        var js = ModelFile.Resource("PlaxisProMax.web.common.js");
        return Inline(html, css, js);
    }

    internal static string Inline(string html, string css, string js) => html
        .Replace("<link rel=\"stylesheet\" href=\"common.css\">", "<style>\n" + css + "\n</style>", StringComparison.Ordinal)
        .Replace("<script src=\"common.js\"></script>", "<script>\n" + js.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase) + "\n</script>", StringComparison.Ordinal);

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlaxisProMax");

    /// <summary>The Viewer page on this PC, the one its shortcut opens; written again when this version's differs.</summary>
    public static string ViewerPage
    {
        get
        {
            var path = Path.Combine(Root, "Plaxis ProMax Viewer.html");
            var html = Page("viewer");
            try
            {
                Directory.CreateDirectory(Root);
                if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != html) File.WriteAllText(path, html, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return path;
        }
    }

    /// <summary>A Viewer page with one results file inside (to open a file from the command line, or to send to a reviewer).</summary>
    public static string ViewerWith(byte[] results)
    {
        var html = Page("viewer");
        var data = "<script id=\"ppr-data\" type=\"application/octet-stream\">" + Convert.ToBase64String(results) + "</script>\n";
        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html + data : html[..at] + data + html[at..];
    }

    /// <summary>Reads a results file (.ppr: gzip JSON), for checks and summaries on this side.</summary>
    public static JsonObject ReadResults(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new InvalidDataException("This is not a results file.");
    }

    /// <summary>
    /// The script that builds a model in Plaxis Input when run there (Expert > Python > Run script): the Modeller's builder
    /// and the model, in one file. Running it uses Plaxis, and so its licence; making it does not.
    /// </summary>
    public static string Script(JsonObject model)
    {
        var builder = ModelFile.Resource("PlaxisProMax.ppm_build.py");
        var json = Convert.ToBase64String(Encoding.UTF8.GetBytes(model.ToJsonString()));
        var sb = new StringBuilder();
        sb.Append("# Plaxis ProMax: builds '").Append((model["title"]?.ToString() ?? "model").Replace("\n", " ")).AppendLine("' in PLAXIS " + model["product"] + " Input.");
        sb.AppendLine("# In Plaxis Input: Expert > Python > Run script, then pick this file. It makes a new project, so save yours first.");
        sb.AppendLine("# Made by the Plaxis ProMax Modeller. The builder below is the one Calculate in Plaxis uses.");
        sb.AppendLine();
        sb.AppendLine(builder);
        sb.AppendLine();
        sb.AppendLine("import base64 as _b64");
        sb.Append("_MODEL = json.loads(_b64.b64decode(\"").Append(json).AppendLine("\").decode(\"utf-8\"))");
        sb.AppendLine("standalone_main(globals(), _MODEL)");
        return sb.ToString();
    }

    /// <summary>
    /// Opens a page in a window of its own: Edge (or Chrome) in app mode, which has no tabs or address bar, so it looks
    /// like a program; the default browser when neither is there.
    /// </summary>
    public static void OpenWindow(string url, int width = 1600, int height = 960)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { TryShell(url); return; }
        foreach (var exe in AppBrowsers())
        {
            if (!File.Exists(exe)) continue;
            try
            {
                var info = new ProcessStartInfo(exe) { UseShellExecute = false };
                info.ArgumentList.Add("--app=" + url);
                info.ArgumentList.Add($"--window-size={width},{height}");
                Process.Start(info)?.Dispose();
                return;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        TryShell(url);
    }

    private static IEnumerable<string> AppBrowsers()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe");
        yield return Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe");
        yield return Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe");
    }

    private static void TryShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>A file URL for a local path (file:///C:/...).</summary>
    public static string FileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}
