using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PlaxisProMax.Chat;
using PlaxisProMax.Licensing;
using PlaxisProMax.Mcp;
using PlaxisProMax.Plaxis;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// "PlaxisProMax.exe --screenshots folder": the windows with sample content, drawn to PNG files, so the user sees how they
/// look without Plaxis or Claude (the Windows build on GitHub runs it and keeps the pictures). Nothing connects or starts.
/// </summary>
internal static class Screenshots
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LicenseState.Override = () => new LicenseState.Status(LicenseState.Kind.Locked, "SPM-7K2Q-9XW4-M3TD", null, LicenseKey.Check.Invalid, null);
        using var link = new PlaxisLink(() => throw new InvalidOperationException("Screenshots don't start Plaxis."));

        var chat = new ChatWindow(link, new PlaxisInstall.StartArgs(null, null, null, false), () => null!, sample: true);
        app.MainWindow = chat;
        Shoot(chat, Path.Combine(folder, "1-chat.png"), w => ((ChatWindow)w).ShowSample());
        var approval = new ChatWindow(link, new PlaxisInstall.StartArgs(null, null, null, false), () => null!, sample: true);
        Shoot(approval, Path.Combine(folder, "2-approval.png"), w => ((ChatWindow)w).ShowSampleApproval());
        var connect = new ChatWindow(link, new PlaxisInstall.StartArgs(null, null, null, false), () => null!, sample: true);
        Shoot(connect, Path.Combine(folder, "3-connect.png"), w => ((ChatWindow)w).ShowSampleConnect());
        Shoot(new SettingsWindow(null), Path.Combine(folder, "4-settings.png"));
        LicenseWindow.Open();
        Shoot(app.Windows.OfType<Window>().Last(w => w.GetType().Name == "LicenseWindow"), Path.Combine(folder, "5-license.png"), shown: true);
        Shoot(new PhoneConnectWindow(sample: true), Path.Combine(folder, "6-phone.png"));
        var bug = new ReportBugWindow();
        Shoot(bug, Path.Combine(folder, "7-report-bug.png"), w => ((ReportBugWindow)w).Fill("The boreholes came out 2 m too low after create_boreholes; the log says the top is at +3.5."));
        var update = new ChatWindow(link, new PlaxisInstall.StartArgs(null, null, null, false), () => null!, sample: true);
        Shoot(update, Path.Combine(folder, "8-update-ready.png"), w => { ((ChatWindow)w).ShowSample(); ((ChatWindow)w).ShowSampleUpdate("0.2.0"); });
        var crew = new ChatWindow(link, new PlaxisInstall.StartArgs(null, null, null, false), () => null!, sample: true);
        Shoot(crew, Path.Combine(folder, "9-crew.png"), w => ((ChatWindow)w).ShowSampleCrew());
        var job = new JobWindow(2);
        Shoot(job, Path.Combine(folder, "10-job.png"), w => ((JobWindow)w).ShowSample());
        app.Shutdown();
        return 0;
    }

    private static void Shoot(Window window, string file, Action<Window>? fill = null, bool shown = false)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        if (!shown) window.Show();
        fill?.Invoke(window);
        Pump(TimeSpan.FromSeconds(1.5));
        if (window.Content is FrameworkElement content)
        {
            var scale = 2.0;
            var w = Math.Max(1, (int)Math.Ceiling(content.ActualWidth * scale));
            var h = Math.Max(1, (int)Math.Ceiling(content.ActualHeight * scale));
            var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            // The window's background under the content (the content itself may be transparent).
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(file);
            encoder.Save(stream);
            Console.WriteLine($"Saved {file} ({w}x{h})");
        }
        window.Close();
    }

    private static void Pump(TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(30);
        }
    }
}

internal sealed partial class ChatWindow
{
    /// <summary>A sample chat: a borehole log turned into a model, with the steps and the answer.</summary>
    internal void ShowSample()
    {
        _plaxisState.Text = "PLAXIS 2D 2024.2 · Harbour wall";
        _conversation.Name = "Harbour wall";
        ShowItem(new ChatItem { Kind = "user", Text = "Build the soil from BH-03 in the attached log, then add the excavation phases to -6 m in 2 m steps." });
        ShowItem(new ChatItem { Kind = "tool", Name = "status", Text = "Checked the open Plaxis project", State = "done" });
        ShowItem(new ChatItem { Kind = "tool", Name = "find_kept_code", Text = "Looked for kept code: boreholes from a log", State = "done" });
        ShowItem(new ChatItem { Kind = "tool", Name = "create_soil_material", Text = "Made 3 soil materials", State = "done" });
        ShowItem(new ChatItem { Kind = "tool", Name = "create_boreholes", Text = "Made borehole BH-03 with 3 layers", State = "done" });
        ShowItem(new ChatItem { Kind = "tool", Name = "add_phase", Text = "Added 3 phases", State = "done" });
        ShowItem(new ChatItem { Kind = "assistant", Text =
            "Done. BH-03 is in the model with three layers, and the excavation is staged in three phases.\n\n" +
            "| Layer | From | To | Material |\n|---|---|---|---|\n| Made ground | 0.0 | -1.8 m | Fill (HS small) |\n| Soft clay | -1.8 | -7.5 m | Clay (SS) |\n| Dense sand | -7.5 | -20.0 m | Sand (HS) |\n\n" +
            "The phases are **Excavate to -2 m**, **-4 m** and **-6 m**, each after the one before. Nothing is calculated yet. " +
            "Say *calculate* when you want them run." });
        _input.Text = "Calculate the three phases and send me the wall's bending moments in Excel";
    }

    /// <summary>A sample approval card: new code, shown before it runs.</summary>
    internal void ShowSampleApproval()
    {
        _plaxisState.Text = "PLAXIS 3D 2024.2 · Quay deck";
        ShowItem(new ChatItem { Kind = "user", Text = "Export the anchor forces for every phase to Excel." });
        ShowItem(new ChatItem { Kind = "tool", Name = "find_kept_code", Text = "Looked for kept code: anchor forces", State = "done" });
        _ = AskAsync(new ApprovalRequest(Tools.RunPlaxisCodeTool.ToolName, "Run new Plaxis code", "No kept code reads anchor forces yet, so this is new code. Read it before allowing.",
            "for phase in g_o.Phases:\n    forces = g_o.getresults(g_o.NodeToNodeAnchor_1, phase, g_o.ResultTypes.NodeToNodeAnchor.AnchorForce2D, 'node')\n    rows.append((label(phase), max(forces, key=abs)))\nresult = rows",
            true), CancellationToken.None);
    }

    /// <summary>The card shown when the window was started outside Plaxis and has no password.</summary>
    internal void ShowSampleConnect()
    {
        _plaxisState.Text = "Not connected to Plaxis";
        ShowConnectCard(10000, "");
    }
}
