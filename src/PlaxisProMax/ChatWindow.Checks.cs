using System.Diagnostics;
using System.IO;
using PlaxisProMax.Tools;

namespace PlaxisProMax;

/// <summary>The ⋯ menu's Run self-test, and the notes the crash guard leaves in the chat (Sketch ProMax's self-test and crash guard).</summary>
internal sealed partial class ChatWindow
{
    private bool _selfTesting;

    /// <summary>A grey note in the chat from outside the window's own code (the crash guard).</summary>
    internal void SayNote(string text) => Note(text);

    /// <summary>
    /// Runs the self-test beside the chat: it only reads the project, so a job may be running. The result is a note with
    /// the counts and each failure, and the report opens.
    /// </summary>
    private async void RunSelfTest()
    {
        if (_selfTesting) return;
        if (RunSelfTestTool.Context is not { } context)
        {
            Note("The self-test can't run in this window.");
            return;
        }
        _selfTesting = true;
        Note("Self-test started. It only reads your project; nothing in it changes. This takes a minute or two.");
        try
        {
            var report = await Task.Run(() => SelfTest.RunAsync(context, RunSelfTestTool.Registry ?? ToolRegistry.CreateDefault(), Program.Version, CancellationToken.None));
            var lines = new List<string> { $"Self-test: {report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped." };
            foreach (var r in report.Results.Where(r => r.Status == "fail")) lines.Add("Failed: " + r.Step + ". " + SelfTest.Short(r.Message, 240));
            if (report.File is { } file)
            {
                lines.Add("The report, with the steps to try by hand on real Plaxis: " + file);
                try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
            Note(string.Join("\n", lines));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Error("The self-test stopped: " + ex.Message); }
        finally { _selfTesting = false; }
    }
}
