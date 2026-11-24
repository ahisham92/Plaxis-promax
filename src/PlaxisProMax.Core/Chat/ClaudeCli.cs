using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlaxisProMax.Settings;

namespace PlaxisProMax.Chat;

/// <summary>
/// Finds Claude Code on this PC, checks the sign-in, opens the official installer and the sign-in in a console window
/// the user can see, and builds the command line of the chat's engine. Everything here may block for a few seconds
/// (it starts processes): call it from a background thread, never on the window's UI thread.
/// </summary>
internal static class ClaudeCli
{
    /// <summary>The official native installer for Windows (code.claude.com/docs/en/setup).</summary>
    public const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

    /// <summary>The permission rule that keeps the chat from reading Claude Code's transcripts of earlier conversations.</summary>
    public const string TranscriptRule = "Read(~/.claude/projects/**/*.jsonl)";

    /// <summary>The name the chat registers our MCP server under: its tools are mcp__plaxis-promax__&lt;tool&gt;.</summary>
    public const string ServerName = "plaxis-promax";

    private static readonly Regex VersionText = new(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant);

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlaxisProMax", "chat");

    /// <summary>The chat engine's working folder: per user, holds nothing but what Claude writes there.</summary>
    public static string WorkFolder => Path.Combine(Folder, "work");

    public static string AttachmentsFolder => Path.Combine(Folder, "attachments");

    /// <summary>Where Claude Code was found, and its version text.</summary>
    internal sealed record Install(string Path, string Version);

    internal enum SignIn { SignedIn, SignedOut, Unknown }

    /// <summary>Every place Claude Code is installed to on Windows, most likely first.</summary>
    public static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(home, ".local", "bin", "claude.exe");                // native installer
        yield return Path.Combine(local, "Programs", "claude", "claude.exe");
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe"); // WinGet
        // PATH as the user has it now (this program's own copy is from when it started, before an install).
        var paths = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value = null;
            try { value = Environment.GetEnvironmentVariable("PATH", target); }
            catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException) { }
            if (value is not null) paths.AddRange(value.Split(';'));
        }
        foreach (var dir in paths.Select(p => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string exe, cmd;
            try
            {
                exe = Path.Combine(dir, "claude.exe");
                cmd = Path.Combine(dir, "claude.cmd");
            }
            catch (ArgumentException) { continue; }
            yield return exe;
            yield return cmd;
        }
        yield return Path.Combine(roaming, "npm", "claude.cmd");                        // npm install -g
    }

    /// <summary>Finds a working Claude Code (it answers --version). Null when there is none.</summary>
    public static Install? Find()
    {
        foreach (var path in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // The Claude desktop app is also "claude.exe"; never start it by mistake.
            if (path.IndexOf("AnthropicClaude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (!File.Exists(path)) continue;
            var (code, output) = Run(path, "--version", 20000);
            var match = VersionText.Match(output);
            if (code == 0 && match.Success) return new Install(path, match.Value);
        }
        return null;
    }

    /// <summary>
    /// Whether Claude Code is signed in: `claude auth status` exits 0 when signed in and 1 when not. An older Claude Code
    /// without that command gives Unknown; the first message then says whether it works. Detail: the sign-in method.
    /// </summary>
    public static (SignIn State, string Method) CheckSignIn(Install install)
    {
        var (code, output) = Run(install.Path, "auth status", 20000);
        var method = "";
        var json = output.IndexOf('{') is var at and >= 0 ? output.Substring(at) : "";
        try
        {
            if (json.Length > 0 && JsonNode.Parse(json) is JsonObject o)
            {
                method = o["authMethod"]?.ToString() ?? "";
                if (o["loggedIn"] is JsonValue v && v.TryGetValue<bool>(out var loggedIn))
                    return (loggedIn ? SignIn.SignedIn : SignIn.SignedOut, method);
                if (code == 0) return (SignIn.SignedIn, method);
                if (code == 1) return (SignIn.SignedOut, method);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        if (code == 1 && output.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0) return (SignIn.SignedOut, "");
        return (SignIn.Unknown, "");
    }

    /// <summary>Opens a PowerShell window running Anthropic's official installer. The user watches it run.</summary>
    public static void StartInstall()
    {
        var script = InstallCommand + "; Write-Host ''; Write-Host 'Finished. Close this window and go back to Plaxis ProMax: the chat finds it by itself.'";
        Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NoExit -Command \"" + script + "\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    /// <summary>Opens a console window running `claude auth login`, which opens the browser to sign in.</summary>
    public static void StartSignIn(Install install)
    {
        var line = Quote(install.Path) + " auth login & echo. & echo When it says you are signed in, close this window and go back to Plaxis ProMax.";
        Process.Start(new ProcessStartInfo("cmd.exe", "/d /s /k \"" + line + "\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    /// <summary>
    /// The engine's start info: Claude Code in print mode with stream-json in and out, the window's own MCP server
    /// (<see cref="Mcp.ChatMcp"/>) with its tools allowed without prompts, Read, Glob and Grep for attached files, and
    /// ToolSearch, and nothing else that could act on the PC. Anything that would need a permission prompt is denied
    /// (dontAsk), because nobody can answer a prompt in a hidden process: the window's own approval card asks instead.
    /// </summary>
    public static ProcessStartInfo EngineStart(Install install, string? resumeSessionId, string? model = null, string? effort = null)
    {
        Directory.CreateDirectory(WorkFolder);
        var engine = Path.Combine(Folder, "engine");
        Directory.CreateDirectory(engine);
        var pid = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var mcpFile = WriteMcpConfig(engine, pid, model);
        var promptFile = Path.Combine(engine, "prompt-" + pid + ".md");
        File.WriteAllText(promptFile, Agent.SystemPrompt.ForChat(AgentSettings.Load().ProjectRules), new UTF8Encoding(false));

        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--mcp-config", mcpFile, "--strict-mcp-config",
            "--append-system-prompt-file", promptFile,
            "--tools", BuiltInTools,
            "--allowedTools", $"mcp__{ServerName},mcp__{ServerName}__*,Read,Glob,Grep",
            "--permission-mode", "dontAsk",
            // Claude Code's own transcripts of earlier conversations: never read back into a chat (Sketch ProMax bug 35).
            "--disallowedTools", TranscriptRule,
        };
        if (!string.IsNullOrEmpty(resumeSessionId)) { args.Add("--resume"); args.Add(resumeSessionId!); }
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model!); }
        AddEffort(args, effort);
        var info = CliStart(install, args);
        info.EnvironmentVariables["PLAXISPROMAX_PID"] = pid;
        return info;
    }

    /// <summary>The efforts Claude Code takes with --effort.</summary>
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Set once Claude Code refused --effort (a version older than the option): from then on it starts without.</summary>
    public static volatile bool EffortRefused;

    private static void AddEffort(List<string> args, string? effort)
    {
        if (EffortRefused || string.IsNullOrEmpty(effort) || !Efforts.Contains(effort)) return;
        args.Add("--effort");
        args.Add(effort!);
    }

    /// <summary>
    /// Claude Code run with <paramref name="args"/> (through cmd.exe for an npm .cmd shim), its output read as UTF-8, in
    /// the chat's work folder, on the user's Claude plan.
    /// </summary>
    private static ProcessStartInfo CliStart(Install install, List<string> args)
    {
        var command = string.Join(" ", args.Select(Quote));
        var info = install.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(install.Path) + " " + command + "\"")
            : new ProcessStartInfo(install.Path, command);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.WorkingDirectory = WorkFolder;
        // The chat runs on the user's Claude plan: an API key in the environment would bill the API instead.
        info.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
        return info;
    }

    /// <summary>
    /// The built-in tools the chat gets: Read, Glob and Grep for files, and ToolSearch, so Claude Code lists the Plaxis
    /// tools by name and loads a tool's description only when it is used (Sketch ProMax: without it every message carried
    /// all the tool descriptions, about 130k tokens, and the chat was shrunk after every answer).
    /// </summary>
    internal const string BuiltInTools = "Read,Glob,Grep,ToolSearch";

    /// <summary>
    /// Writes the MCP config: the window's own MCP server, its address, its token and the model. Written at each start,
    /// so a new address is used. Claude Code gives up on an HTTP call that is silent for 5 minutes: the server's own
    /// limit raises that to 6 hours, since a call may wait on an approval or a long calculation.
    /// </summary>
    private static string WriteMcpConfig(string engine, string pid, string? model)
    {
        var http = Mcp.ChatMcp.Current ?? throw new InvalidOperationException("The Plaxis tools are not running.");
        var headers = new JsonObject { ["Authorization"] = "Bearer " + http.Token };
        if (!string.IsNullOrEmpty(model)) headers[Mcp.McpHttpServer.ModelHeader] = model;
        var server = new JsonObject { ["type"] = "http", ["url"] = http.Url, ["headers"] = headers, ["timeout"] = 21_600_000 };
        var mcp = new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = server } };
        var mcpFile = Path.Combine(engine, "mcp-" + pid + ".json");
        File.WriteAllText(mcpFile, mcp.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return mcpFile;
    }

    /// <summary>The address the chat's Claude Code reaches the Plaxis tools at now.</summary>
    internal static string McpAddress => Mcp.ChatMcp.Current?.Url ?? "";

    /// <summary>Runs a short command hidden and returns its exit code and output (stdout then stderr); -1 on timeout or failure.</summary>
    public static (int Code, string Output) Run(string exe, string arguments, int timeoutMs)
    {
        try
        {
            var cmdShim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var info = cmdShim
                ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(exe) + " " + arguments + "\"")
                : new ProcessStartInfo(exe, arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.RedirectStandardInput = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.WorkingDirectory = Directory.Exists(WorkFolder) ? WorkFolder : Path.GetTempPath();
            info.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
            using var process = ChildProcess.Start(info); // inherits none of the window's open handles (Sketch ProMax bug 72)
            process.StandardInput!.Close();
            var stdout = process.StandardOutput!.ReadToEndAsync();
            var stderr = process.StandardError!.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                KillTree(process.Process);
                return (-1, "");
            }
            Task.WaitAll(new Task[] { stdout, stderr }, 2000);
            return (process.Process.ExitCode, (stdout.IsCompleted ? stdout.Result : "") + "\n" + (stderr.IsCompleted ? stderr.Result : ""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Ends a process and everything it started (Claude Code starts the MCP server).</summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited) return;
#if NETFRAMEWORK
            using var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F")
            {
                UseShellExecute = false, CreateNoWindow = true,
            });
            kill?.WaitForExit(5000);
            if (!process.HasExited) process.Kill();
#else
            process.Kill(entireProcessTree: true);
#endif
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    /// <summary>Quotes one argument for the Windows command line (CommandLineToArgvW rules).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }
}
