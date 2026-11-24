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
    public const string Version = "0.1.0";

    [STAThread]
    public static int Main(string[] args)
    {
        var start = PlaxisInstall.Parse(args, Environment.GetEnvironmentVariable);
        // The password was for this process only: no child (Claude Code, its tools) inherits it.
        Environment.SetEnvironmentVariable("PLAXISPROMAX_PASSWORD", null);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var settings = AgentSettings.Load();
        var python = PlaxisInstall.FindPython(start.Python, settings.PlaxisPython);
        if (python is null)
        {
            MessageBox.Show("Plaxis ProMax couldn't find Plaxis's own Python. Start it from Plaxis (Expert > Python > Run script, \"Plaxis ProMax.py\"), " +
                            "or set Plaxis's Python in settings.json.", "Plaxis ProMax", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        if (start.Python is not null && !string.Equals(settings.PlaxisPython, python, StringComparison.OrdinalIgnoreCase))
        {
            settings.PlaxisPython = python;
            settings.Save();
        }
        var worker = Path.Combine(AppContext.BaseDirectory, "plaxis_worker.py");
        using var link = new PlaxisLink(() => PlaxisLink.WorkerStart(python, worker));
        var library = new CodeLibrary();
        ChatWindow? window = null;
        ChatMcp? mcp = null;
        var asker = new DeferredAsker(() => window);
        mcp = new ChatMcp(ToolRegistry.CreateDefault(), new ToolContext(link, library, AgentSettings.Load), asker, Version);
        mcp.Start();
        try
        {
            window = new ChatWindow(link, start, () => mcp);
            app.MainWindow = window;
            window.Show();
            return app.Run();
        }
        finally { mcp.Dispose(); }
    }

    /// <summary>The window answers approvals once it exists; a call before that is declined (none can come before it).</summary>
    private sealed class DeferredAsker(Func<IApprovalAsker?> window) : IApprovalAsker
    {
        public Task<ApprovalAnswer> AskAsync(ApprovalRequest request, CancellationToken ct) =>
            window() is { } w ? w.AskAsync(request, ct) : Task.FromResult(ApprovalAnswer.Deny);
    }
}
