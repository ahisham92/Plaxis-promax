using System.IO;
using System.Windows;
using PlaxisProMax.Mcp;
using PlaxisProMax.Plaxis;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

namespace PlaxisProMax;

/// <summary>
/// Starts the window: finds Plaxis's Python, starts the worker link and the chat's MCP server, then shows the chat.
/// Started by "Plaxis ProMax.py" from Plaxis's Expert menu (with --port, --python and the password in the environment)
/// or from the Start menu.
/// </summary>
internal static class Program
{
    public const string Version = "0.4.0";

    /// <summary>The arguments this start had, so Restart to update starts the new version the same way.</summary>
    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--screenshots") return Screenshots.Run(args[1]);
        // The Viewer: a page with nothing behind it, so no Plaxis, no licence and no chat (the Viewer shortcut opens the page itself).
        if (args.Length >= 1 && args[0] == "--viewer") return OpenViewer(args.Length > 1 ? args[1] : null);
        // The Modeller: the chat with its Modeller window, and no Plaxis behind them, so no licence until Calculate in Plaxis.
        var modeller = args.Length >= 1 && args[0] == "--modeller";
        var modellerFile = modeller && args.Length > 1 ? args[1] : null;
        if (modeller) args = [];
        Args = modeller ? ["--modeller"] : args;
        Update.Updater.SetCurrent(Version);
        // A newer version downloaded by the updater: start it instead (same arguments, Plaxis's password still in the
        // environment) and leave. If it can't start, this version opens as usual.
        if (Update.Updater.HandOverTarget(Environment.GetEnvironmentVariable) is { } newer && Update.Updater.HandOver(newer, args)) return 0;
        var start = PlaxisInstall.Parse(args, Environment.GetEnvironmentVariable);
        // The password was for this process only: no child (Claude Code, its tools) inherits it.
        Environment.SetEnvironmentVariable("PLAXISPROMAX_PASSWORD", null);
        Bugs.CrashGuard.Start();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        // An error in a click or a redraw is written to Bugs\Crashes and the window carries on, as Sketch ProMax's does.
        app.DispatcherUnhandledException += (_, e) => e.Handled = Bugs.CrashGuard.OnUi(e.Exception);
        var settings = AgentSettings.Load();
        var python = PlaxisInstall.FindPython(start.Python, settings.PlaxisPython);
        var worker = Path.Combine(AppContext.BaseDirectory, "plaxis_worker.py");
        Modeller.Studio.Configure(() => python ?? PlaxisInstall.FindPython(null, AgentSettings.Load().PlaxisPython), worker);
        _ = Task.Run(Modeller.Shortcuts.Ensure);
        if (python is null && !modeller)
        {
            MessageBox.Show("Plaxis ProMax couldn't find Plaxis's own Python. Start it from Plaxis (Expert > Python > Run script, \"Plaxis ProMax.py\"), " +
                            "or set Plaxis's Python in settings.json.", "Plaxis ProMax", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        if (python is not null && start.Python is not null && !string.Equals(settings.PlaxisPython, python, StringComparison.OrdinalIgnoreCase))
        {
            settings.PlaxisPython = python;
            settings.Save();
        }
        // With no Python found (the Modeller on a PC that never started from Plaxis) the link only fails when it is used.
        using var link = new PlaxisLink(() => PlaxisLink.WorkerStart(python ?? "python", worker));
        var library = new CodeLibrary();
        ChatWindow? window = null;
        ChatMcp? mcp = null;
        var asker = new DeferredAsker(() => window);
        var registry = ToolRegistry.CreateDefault();
        var context = new ToolContext(link, library, AgentSettings.Load);
        RunSelfTestTool.Registry = registry;
        RunSelfTestTool.Version = Version;
        RunSelfTestTool.Context = context;
        mcp = new ChatMcp(registry, context, asker, Version);
        mcp.Start();
        try
        {
            window = new ChatWindow(link, start, () => mcp);
            app.MainWindow = window;
            var shown = window;
            Bugs.CrashGuard.Caught += text => shown.Dispatcher.BeginInvoke(() => shown.SayNote(text));
            window.Show();
            if (modeller) OpenModeller(window, modellerFile);
            if (Bugs.CrashGuard.TakeLastCrash() is { Length: > 0 } last) window.SayNote(last);
            return app.Run();
        }
        finally
        {
            mcp.Dispose();
            Modeller.Studio.Stop();
        }
    }

    private static void OpenModeller(ChatWindow window, string? file)
    {
        try
        {
            if (file is not null)
            {
                if (file.EndsWith(".ppr", StringComparison.OrdinalIgnoreCase)) { OpenViewer(file); return; }
                Modeller.Studio.Server.Session.Open(file);
            }
            Modeller.Studio.OpenModeller();
            window.SayNote("The Modeller is open in its own window. It needs no Plaxis and no licence: only Calculate in Plaxis uses one, for as long as the run takes.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            window.SayNote("The Modeller could not open: " + ex.Message);
        }
    }

    /// <summary>Opens the Viewer page, with a results file inside when one is named (double-clicking a .ppr).</summary>
    private static int OpenViewer(string? file)
    {
        try
        {
            var page = Modeller.StudioFiles.ViewerPage;
            if (file is not null && File.Exists(file))
            {
                var folder = Path.Combine(Path.GetTempPath(), "PlaxisProMax");
                Directory.CreateDirectory(folder);
                page = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + " (results).html");
                File.WriteAllText(page, Modeller.StudioFiles.ViewerWith(File.ReadAllBytes(file)), new System.Text.UTF8Encoding(false));
            }
            Modeller.StudioFiles.OpenWindow(Modeller.StudioFiles.FileUrl(page));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("The Viewer could not open: " + ex.Message, "Plaxis ProMax", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }

    /// <summary>The window answers approvals once it exists; a call before that is declined (none can come before it).</summary>
    private sealed class DeferredAsker(Func<IApprovalAsker?> window) : IApprovalAsker
    {
        public Task<ApprovalAnswer> AskAsync(ApprovalRequest request, CancellationToken ct) =>
            window() is { } w ? w.AskAsync(request, ct) : Task.FromResult(ApprovalAnswer.Deny);
    }
}
