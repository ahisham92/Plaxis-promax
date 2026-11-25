using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PlaxisProMax.Chat;
using PlaxisProMax.Settings;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// The chat large, as in Sketch ProMax (ChatPane.Big): the header's expand button maximises the window (and puts it back
/// at the side of the screen); once it is wide, the jobs are listed in a column on the right over the whole height, and
/// the conversation and the message box keep a width that reads well instead of stretching across the screen. The
/// window remembers where it was and how big, for the next start.
/// </summary>
internal sealed partial class ChatWindow
{
    /// <summary>From this width the jobs column shows; the text column is never wider than <see cref="ReadWidth"/>.</summary>
    private const double WideWidth = 900, SideWidth = 300, ReadWidth = 880;

    private Button? _bigButton;
    private Border? _side;
    private readonly StackPanel _sideList = new();
    private readonly TextBlock _sideEmpty = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
    private readonly Dictionary<int, ParallelJobCard> _sideCards = [];
    private bool _wide;

    private static string PlaceFile => Path.Combine(AgentSettings.Folder, "window.txt");

    /// <summary>The header's expand button: large, and back to the side.</summary>
    private Button BigButton()
    {
        _bigButton = ChatUi.IconButton(ChatUi.Paths.Expand, "", "");
        _bigButton.Margin = new Thickness(ChatUi.S1, 0, 0, 0);
        _bigButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        StateChanged += (_, _) => ShowBigButton();
        SizeChanged += (_, e) => { if (e.WidthChanged) ShowWide(); };
        ShowBigButton();
        return _bigButton;
    }

    private void ShowBigButton()
    {
        if (_bigButton is null) return;
        var big = WindowState == WindowState.Maximized;
        _bigButton.Content = ChatUi.Icon(big ? ChatUi.Paths.Collapse : ChatUi.Paths.Expand);
        _bigButton.ToolTip = big
            ? "Back to the side: the chat returns to its place beside Plaxis."
            : "Open the chat large: the whole screen, with the jobs listed on the right.";
        AutomationProperties.SetName(_bigButton, big ? "Back to the side" : "Open the chat large");
        AutomationProperties.SetHelpText(_bigButton, (string)_bigButton.ToolTip);
    }

    /// <summary>The column on the right of a wide window: each job with its state, Stop and Open.</summary>
    private Border Side()
    {
        var heading = Brand.Text("Jobs", Brand.K.Text, ChatUi.BodySize, bold: true);
        var help = Wrapped(Brand.Text("The work the coordinator handed out. Open one to see its own chat and write to it.", Brand.K.Muted, ChatUi.SmallSize));
        help.Margin = new Thickness(0, 2, 0, ChatUi.S3);
        _sideEmpty.TextWrapping = TextWrapping.Wrap;
        _sideEmpty.Margin = new Thickness(0, ChatUi.S1, 0, 0);
        _sideList.Children.Add(_sideEmpty);
        var scroll = new ScrollViewer { Content = _sideList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var stack = new DockPanel { Margin = new Thickness(ChatUi.S3) };
        var head = new StackPanel();
        head.Children.Add(heading);
        head.Children.Add(help);
        DockPanel.SetDock(head, Dock.Top);
        stack.Children.Add(head);
        stack.Children.Add(scroll);
        _side = new Border { Child = stack, Width = SideWidth, BorderThickness = new Thickness(1, 0, 0, 0), Visibility = Visibility.Collapsed };
        _side.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        _side.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        AutomationProperties.SetName(_side, "Jobs");
        return _side;
    }

    /// <summary>Wide or not: the jobs column shows from <see cref="WideWidth"/> on.</summary>
    private void ShowWide(bool? force = null)
    {
        if (_side is null) return;
        var wide = force ?? ActualWidth >= WideWidth;
        if (wide == _wide && force is null) return;
        _wide = wide;
        _side.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        ShowSide();
    }

    /// <summary>The jobs column follows the jobs: a card for each, newest first, or why there are none.</summary>
    private void ShowSide()
    {
        if (!_wide || _side is null) return;
        var jobs = JobsNow();
        var now = DateTime.UtcNow;
        var limit = ChatJobs.Limit;
        foreach (var gone in _sideCards.Keys.Where(n => jobs.All(j => j.Number != n)).ToList())
        {
            _sideList.Children.Remove(_sideCards[gone]);
            _sideCards.Remove(gone);
        }
        foreach (var job in jobs.OrderBy(j => j.Number))
        {
            if (!_sideCards.TryGetValue(job.Number, out var card))
            {
                card = new ParallelJobCard(job.Number) { Margin = new Thickness(0, 0, 0, ChatUi.S2) };
                card.StopPressed += StopJob;
                card.Opened += OpenJob;
                _sideCards[job.Number] = card;
                _sideList.Children.Insert(1, card); // under the empty line, newest first
            }
            card.Render(job, now, limit);
        }
        _sideEmpty.Text = IsCoordinator
            ? "No jobs yet. Ask for several separate things at once and the coordinator hands them to jobs that work side by side; they show here."
            : "This is a simple chat: Plaxis ProMax does the work here itself, without jobs. Switch it to With coordinator (under the message box) to use jobs.";
        _sideEmpty.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Where the window was -------------------------------------------------------------------------------------------

    /// <summary>Puts the window where it was last time (when that is still on a screen), large when it was large.</summary>
    private void RestorePlace()
    {
        if (_sample) return;
        try
        {
            if (!File.Exists(PlaceFile)) return;
            var parts = File.ReadAllText(PlaceFile).Split(';');
            if (parts.Length < 5) return;
            double N(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
            var (left, top, width, height) = (N(0), N(1), N(2), N(3));
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (width >= MinWidth && height >= MinHeight && screen.IntersectsWith(new Rect(left + 40, top + 20, Math.Max(1, width - 80), 40)))
            {
                Left = left;
                Top = top;
                Width = Math.Min(width, screen.Width);
                Height = Math.Min(height, screen.Height);
            }
            if (parts[4] == "max") WindowState = WindowState.Maximized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException) { /* the default place */ }
    }

    private void SavePlace()
    {
        if (_sample) return;
        try
        {
            var r = RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            string F(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);
            Directory.CreateDirectory(AgentSettings.Folder);
            File.WriteAllText(PlaceFile, string.Join(";", F(r.Left), F(r.Top), F(r.Width), F(r.Height), WindowState == WindowState.Maximized ? "max" : "side"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not remembered */ }
    }

    /// <summary>For the screenshots: the chat large, with the crew at work and the jobs in the column on the right.</summary>
    internal void ShowSampleWide()
    {
        Width = 1280;
        Height = 820;
        ShowSampleCrew();
        ShowWide(force: true);
    }
}
