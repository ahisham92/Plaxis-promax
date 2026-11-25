using System.Text;

namespace PlaxisProMax.Bugs;

/// <summary>
/// Keeps one mistake from closing the window (Sketch ProMax's CrashGuard): an exception that reaches the window's
/// dispatcher is caught, written to the Bugs folder and said once in the chat, and the window carries on; a task's
/// forgotten exception is noted; one on a background thread, which can't be stopped, leaves a file that the next start
/// reads, so the user hears what happened and Report bug carries it. No WPF here: the program hooks its dispatcher to
/// <see cref="OnUi"/>.
/// </summary>
internal static class CrashGuard
{
    private static int _lastUiLog = Environment.TickCount - 60000;
    private static bool _started;

    /// <summary>Where the crash notes are kept (Documents\Plaxis ProMax\Bugs\Crashes).</summary>
    public static string Folder => Path.Combine(BugLog.Root, "Crashes");

    private static string LastFile => Path.Combine(Folder, "last-crash.txt");

    /// <summary>Raised (any thread) when an error was caught and the window carried on: one plain line for the chat.</summary>
    public static event Action<string>? Caught;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Write(LastFile, "Plaxis ProMax closed on an error on a background thread.", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                Tools.ActivityLog.Note("task_error_caught", Describe(e.Exception));
                e.SetObserved();
            }
            catch (Exception) { /* never fail here */ }
        };
    }

    /// <summary>
    /// An exception reached the window's dispatcher. True: it is handled and the window carries on (everything but running
    /// out of memory). Logged at most once every few seconds, so a handler failing on every redraw doesn't fill the log.
    /// </summary>
    public static bool OnUi(Exception ex)
    {
        if (ex is OutOfMemoryException) return false;
        try
        {
            if (unchecked(Environment.TickCount - _lastUiLog) < 5000) return true;
            _lastUiLog = Environment.TickCount;
            var file = Path.Combine(Folder, $"error-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");
            Write(file, "An error was caught in the window; Plaxis ProMax carried on.", ex);
            Tools.ActivityLog.Note("ui_error_caught", Describe(ex));
            Caught?.Invoke($"Something went wrong in the window ({Short(ex)}). Plaxis ProMax carried on; if it happens again, Settings > Report bug sends the details.");
        }
        catch (Exception) { /* never fail here */ }
        return true;
    }

    /// <summary>What the last start left when it closed on an error ("" when it closed normally), read once.</summary>
    public static string TakeLastCrash()
    {
        try
        {
            if (!File.Exists(LastFile)) return "";
            var text = File.ReadAllText(LastFile);
            var kept = Path.Combine(Folder, $"crash-{File.GetLastWriteTime(LastFile):yyyy-MM-dd-HHmmss}.txt");
            File.Move(LastFile, kept, overwrite: true);
            var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Error:", StringComparison.Ordinal)) ?? "";
            return "Plaxis ProMax closed unexpectedly last time" + (first.Length > 0 ? " (" + first.Substring(6).Trim() + ")" : "") +
                   ". Your chats are saved. Settings > Report bug sends the details to the developer.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static void Write(string file, string what, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var sb = new StringBuilder();
            sb.AppendLine(what);
            sb.AppendLine("When: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Versions: " + BugReport.Versions());
            sb.AppendLine("Error: " + Short(ex));
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            File.WriteAllText(file, sb.ToString());
        }
        catch (Exception) { /* nothing more can be done */ }
    }

    internal static string Short(Exception ex)
    {
        var inner = ex is System.Reflection.TargetInvocationException or AggregateException && ex.InnerException is { } i ? i : ex;
        var message = (inner.Message ?? "").Split('\n')[0].Trim();
        return inner.GetType().Name + (message.Length > 0 ? ": " + (message.Length > 160 ? message.Substring(0, 160) + "…" : message) : "");
    }

    internal static string Describe(Exception ex)
    {
        var where = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("PlaxisProMax", StringComparison.Ordinal)) ?? "";
        return Short(ex) + (where.Length > 0 ? " " + (where.Length > 200 ? where.Substring(0, 200) : where) : "");
    }
}
