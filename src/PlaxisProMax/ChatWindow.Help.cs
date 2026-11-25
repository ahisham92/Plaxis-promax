using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PlaxisProMax.Bugs;
using PlaxisProMax.Chat;
using PlaxisProMax.Settings;
using PlaxisProMax.Update;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// Self-update and bug reports in the window (Sketch ProMax's Update button and Report bug / Open bug folder / Export
/// chats &amp; bugs): the update check when the window opens and hourly, the "Restart to update" bar under the header, and
/// the picture of the window and the versions that a bug report carries.
/// </summary>
internal sealed partial class ChatWindow
{
    private readonly Border _updateBar = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S2) };
    private readonly TextBlock _updateText = Brand.Text("", Brand.K.Text, 12);
    private DispatcherTimer? _updateTimer;
    private readonly Button _whatsNew = Brand.Button("What's new");

    /// <summary>The bar under the header that says a newer version is ready, with Restart to update.</summary>
    private Border UpdateBar()
    {
        var restart = Brand.Button("Restart to update", Brand.Weight.Primary);
        restart.Click += (_, _) => RestartToUpdate();
        var row = new DockPanel();
        _whatsNew.Click += (_, _) => { if (Updater.Staged is { } v) ShowWhatsNew(v); };
        var buttons = Brand.ButtonRow(_whatsNew, restart);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        _updateText.TextWrapping = TextWrapping.Wrap;
        _updateText.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_updateText);
        _updateBar.Child = row;
        _updateBar.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        _updateBar.BorderThickness = new Thickness(0, 0, 0, 1);
        _updateBar.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        return _updateBar;
    }

    /// <summary>Bug reports picture this window and say what Plaxis has open; the update check starts.</summary>
    private void StartHelp()
    {
        BugReport.Screenshot = () => Dispatcher.Invoke(() => Picture(this));
        BugReport.Situation = () => Dispatcher.Invoke(SituationLines);
        ShowStaged();
        SayUpdated();
        // A little after opening (the window and Plaxis first), then every 10 minutes: due once an hour by the wall clock,
        // so a PC that slept still checks when it wakes (Sketch ProMax's update loop).
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromMinutes(10);
            if (!Updater.IsDue(TimeSpan.FromHours(1))) return;
            await CheckForUpdatesAsync(force: false, null);
        };
        _updateTimer.Start();
        Closed += (_, _) => _updateTimer?.Stop();
    }

    /// <summary>Checks now (Settings' Check for updates, or the hourly check); the bug log is refreshed with it.</summary>
    internal async Task<string> CheckForUpdatesAsync(bool force, UpdateProgress? progress)
    {
        progress ??= new UpdateProgress { Interactive = force };
        var auto = AgentSettings.Load().AutoUpdate;
        _ = Task.Run(() => BugLog.SyncAsync($"PlaxisProMax/{Updater.CurrentText}", CancellationToken.None));
        await Task.Run(() => Updater.CheckAsync(force, progress, CancellationToken.None, install: auto || force));
        ShowStaged();
        return progress.Snapshot.Detail ?? Updater.Describe();
    }

    private void ShowStaged()
    {
        if (Updater.Staged is { } staged)
        {
            _updateText.Text = $"Plaxis ProMax {staged} is ready. It is used from the next start; your chat is kept.";
            _whatsNew.Visibility = Updater.NotesFor(staged) is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            _updateBar.Visibility = Visibility.Visible;
        }
        else _updateBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>The release notes of a version, as the last update check read them (Sketch ProMax's What's new).</summary>
    internal void ShowWhatsNew(string version)
    {
        var notes = Updater.NotesFor(version);
        var text = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(notes) ? "The notes of this version weren't read yet. Settings > Check for updates reads them." : UpdateNotes.Plain(notes),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 12, 18, 12),
        };
        text.SetResourceReference(BackgroundProperty, Brand.K.Bg);
        text.SetResourceReference(ForegroundProperty, Brand.K.Text);
        var window = new Window { Title = "Plaxis ProMax: What's new", Width = 520, Height = 460, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Brand.Prepare(window);
        var root = new DockPanel();
        var header = Brand.Header("What's new", "Plaxis ProMax " + version);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(text);
        window.Content = root;
        window.Show();
    }

    /// <summary>The first start of a newer version says so once, with the top of its notes.</summary>
    private void SayUpdated()
    {
        if (UpdateNotes.TakeUpdated(Updater.CurrentText) is not { } line) return;
        Note(line);
    }

    /// <summary>Starts the downloaded version with the same Plaxis connection and closes this one (the chat is saved; it picks it up).</summary>
    internal void RestartToUpdate()
    {
        if (Updater.Staged is not { } staged) { ShowStaged(); return; }
        if (_busy)
        {
            MessageBox.Show(this, "The chat is working. Restart to update when it has finished (or press Stop first).", "Plaxis ProMax");
            return;
        }
        var exe = Updater.ExeIn(Path.Combine(Updater.VersionsFolder, staged));
        if (!Updater.HandOver(exe, Program.Args, _start.Password ?? NullIfEmpty(AgentSettings.Load().ServerPassword)))
        {
            MessageBox.Show(this, $"Plaxis ProMax {staged} didn't start, so this version stays open. Settings > Report bug saves what happened.", "Plaxis ProMax");
            return;
        }
        Close();
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private IReadOnlyList<string> SituationLines()
    {
        var lines = new List<string>();
        if (_link.LastStatus is { } s)
        {
            lines.Add($"- Plaxis: {s["product"]} {s["version"]}");
            var file = s["filename"]?.ToString() ?? "";
            lines.Add($"- Project: {(file.Length > 0 ? Path.GetFileName(file) : "(not saved yet)")}");
        }
        else lines.Add("- Plaxis: not connected");
        lines.Add($"- Claude Code: {(_claude is { } c ? c.Version : "not found")}");
        lines.Add($"- Chat: {_conversation.Title}, {_conversation.Items.Count} items");
        return lines;
    }

    /// <summary>A JPEG of a window's content, at most 1600 pixels wide (Sketch ProMax's ScreenGrab of Revit's window).</summary>
    internal static (byte[]? Jpeg, string? Error) Picture(Window window)
    {
        try
        {
            if (window.Content is not FrameworkElement content || content.ActualWidth < 1) return (null, "the window isn't shown");
            var scale = Math.Min(1.5, 1600 / content.ActualWidth);
            var w = (int)Math.Ceiling(content.ActualWidth * scale);
            var h = (int)Math.Ceiling(content.ActualHeight * scale);
            var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }
            bitmap.Render(visual);
            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return (stream.ToArray(), null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>Settings > Report bug: the picture first (before any dialog covers the window), then the one line, then the zip.</summary>
    internal void ReportBug(Window owner)
    {
        var shot = Picture(this);
        var ask = new ReportBugWindow { Owner = owner };
        if (ask.ShowDialog() != true) return;
        try { ShowInFolder(BugReport.Create(ask.Description, shot, "Settings > Report bug")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            MessageBox.Show(owner, $"The bug report could not be saved: {ex.Message}", "Plaxis ProMax");
        }
    }

    /// <summary>Settings > Export chats &amp; bugs: the last week's chats, every bug report and the activity log in one zip.</summary>
    internal static async Task<string> ExportChatsAndBugsAsync()
    {
        var result = await Task.Run(() => ChatsAndBugsExport.Export("week"));
        var file = (string)result.GetType().GetProperty("file")!.GetValue(result)!;
        ShowInFolder(file);
        return file;
    }

    internal static void OpenBugFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{BugLog.EnsureFolder()}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { MessageBox.Show($"Couldn't open the bug folder: {ex.Message}", "Plaxis ProMax"); }
    }

    private static void ShowInFolder(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { MessageBox.Show($"Saved: {file}", "Plaxis ProMax"); }
    }

    /// <summary>For the screenshots: the bar as it shows when a newer version is ready.</summary>
    internal void ShowSampleUpdate(string version)
    {
        _updateText.Text = $"Plaxis ProMax {version} is ready. It is used from the next start; your chat is kept.";
        _updateBar.Visibility = Visibility.Visible;
    }
}

/// <summary>Report bug: one line of what went wrong (Sketch ProMax's Report bug dialog).</summary>
internal sealed class ReportBugWindow : Window
{
    private readonly TextBox _text = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 200,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6),
    };

    public string Description => _text.Text.Trim();

    public ReportBugWindow()
    {
        Title = "Plaxis ProMax: Report bug";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);

        var page = new StackPanel { Margin = new Thickness(18, 10, 18, 14) };
        var help = ChatWindow.Wrapped(ChatUi.Small(
            "Say in one line what went wrong. The report also holds a picture of the Plaxis ProMax window, the versions, the project's " +
            "name, and the last tool calls with their errors. Nothing goes over the internet: the zip is saved in Documents\\Plaxis ProMax\\Bugs\\Reports."));
        page.Children.Add(help);
        page.Children.Add(new TextBlock { Text = "What went wrong", Margin = new Thickness(0, 10, 0, 2) });
        _text.SetResourceReference(BackgroundProperty, Brand.K.ComposerField);
        _text.SetResourceReference(ForegroundProperty, Brand.K.Text);
        System.Windows.Automation.AutomationProperties.SetName(_text, "What went wrong");
        page.Children.Add(_text);
        var hint = ChatUi.Small("For example: the boreholes came out 2 m too low after create_boreholes.");
        hint.Margin = new Thickness(0, 4, 0, 0);
        page.Children.Add(hint);
        var save = Brand.Button("Save report", Brand.Weight.Primary);
        save.IsDefault = true;
        save.IsEnabled = false;
        _text.TextChanged += (_, _) => save.IsEnabled = Description.Length > 0;
        save.Click += (_, _) => DialogResult = true;
        var cancel = Brand.Button("Cancel");
        cancel.IsCancel = true;
        var buttons = Brand.ButtonRow(cancel, save);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        page.Children.Add(buttons);

        var root = new DockPanel();
        var header = Brand.Header("Report bug", "One small zip for the Plaxis ProMax developer");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(page);
        Content = root;
        Loaded += (_, _) => _text.Focus();
    }

    /// <summary>For the screenshots.</summary>
    internal void Fill(string text) => _text.Text = text;
}
