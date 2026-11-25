using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PlaxisProMax.Chat;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// The Menu button of the header (Sketch ProMax's history drawer, Notes tab, Routines window, saved prompts and plan
/// meter): chat history with search, rename, pin, export and delete; the notes Claude was asked to remember; routines
/// that run by themselves at their time; prompts saved to use again; and how much of the plan is used.
/// </summary>
internal sealed partial class ChatWindow : IHistoryHost
{
    private readonly Grid _center = new();
    private readonly Border _scrim = new() { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0, 0, 0)), Visibility = Visibility.Collapsed };
    private HistoryDrawer? _drawer;
    private bool _drawerOpen;
    private Window? _notes;
    private RoutinesWindow? _routines;
    private DispatcherTimer? _routineTimer;
    private readonly Border _promptPanel = new() { Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(ChatUi.Radius), Margin = new Thickness(0, 0, 0, ChatUi.S2), MaxHeight = 260 };
    private static ChatWindow? _shownWindow;

    /// <summary>A chat's summary changed (the Notes window lists them).</summary>
    internal static event Action? RecallChanged;

    /// <summary>The middle of the window: the messages, and the history drawer over them.</summary>
    private Grid Center()
    {
        _center.Children.Add(_scroll);
        _scrim.MouseLeftButtonDown += (_, _) => CloseDrawer();
        _center.Children.Add(_scrim);
        _center.SizeChanged += (_, _) => SizeDrawer();
        _promptPanel.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        _promptPanel.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        _shownWindow = this;
        Closed += (_, _) =>
        {
            _routineTimer?.Stop();
            if (_notes is not null) _notes.Close();
            _routines?.Close();
            if (ReferenceEquals(_shownWindow, this)) _shownWindow = null;
        };
        return _center;
    }

    /// <summary>Menu on the header.</summary>
    private void ShowMenu(Button from)
    {
        var menu = new ContextMenu { PlacementTarget = from, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string header, string tip, Action click)
        {
            var item = new MenuItem { Header = header, ToolTip = tip };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        Item("Chat history", "Every saved chat of this project (or all projects): search, open, rename, pin, export, delete.", OpenDrawer);
        Item("Notes", "What you asked Plaxis ProMax to remember, for all projects and for this one.", OpenNotes);
        Item("Routines", "Work the crew does by itself at a set time, such as a nightly calculation.", OpenRoutines);
        Item("Saved prompts", "Prompts you saved to use again.", TogglePrompts);
        menu.Items.Add(new Separator());
        foreach (var line in UsageLines()) menu.Items.Add(new MenuItem { Header = line, IsEnabled = false });
        menu.IsOpen = true;
    }

    /// <summary>The plan's usage as last reported by Claude Code: one line per limit.</summary>
    internal static List<string> UsageLines()
    {
        var usage = PlanUsage.Load(Crew.RoutinesFolder);
        var now = DateTimeOffset.UtcNow;
        var lines = usage?.Windows.Where(w => PlanUsage.Current(w, now) is not null)
            .Select(w => $"{PlanUsage.Label(w.Key)}: {PlanUsage.Percent(w.Used ?? 0)} used · {PlanUsage.ResetText(w, now)}").ToList() ?? [];
        return lines.Count > 0 ? lines : ["Plan usage: shown after the next answer"];
    }

    // ---- History drawer (IHistoryHost) ----------------------------------------------------------------------------

    private void OpenDrawer()
    {
        if (_drawer is null)
        {
            _drawer = new HistoryDrawer(this) { HorizontalAlignment = HorizontalAlignment.Left };
            _center.Children.Add(_drawer);
            _drawer.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; CloseDrawer(); } };
        }
        SizeDrawer();
        _drawerOpen = true;
        _scrim.Visibility = Visibility.Visible;
        _drawer.Visibility = Visibility.Visible;
        _drawer.Refresh();
        _drawer.FocusSearch();
    }

    private void SizeDrawer()
    {
        if (_drawer is not null) _drawer.Width = Math.Max(200, Math.Min(340, _center.ActualWidth - 40));
    }

    public void CloseDrawer()
    {
        if (_drawer is not { } drawer || !_drawerOpen) return;
        _drawerOpen = false;
        drawer.Visibility = Visibility.Collapsed;
        _scrim.Visibility = Visibility.Collapsed;
        _input.Focus();
    }

    string IHistoryHost.ModelKey => _conversation.Key;

    string IHistoryHost.CurrentFile => _conversation.File;

    string? IHistoryHost.StateOf(string file) =>
        !string.Equals(file, _conversation.File, StringComparison.OrdinalIgnoreCase) ? null
        : _busy ? "Working" : _crew is { JobsOpen: true } ? "Jobs at work" : null;

    void IHistoryHost.NewChat()
    {
        NewChat();
        CloseDrawer();
    }

    /// <summary>The chat shown can be left: nothing is answering and no job is open (they would stop with it).</summary>
    private bool CanLeaveChat(out string why)
    {
        why = _busy ? "This chat is still answering. Press Stop first, or wait for it."
            : _crew is { JobsOpen: true } ? "This chat's jobs are still at work. Wait for them, or stop them first."
            : _crew is { Run.Active: true } ? "This chat's autopilot run is still on. Stop it first." : "";
        return why.Length == 0;
    }

    void IHistoryHost.OpenChat(ChatSummary chat)
    {
        if (string.Equals(chat.File, _conversation.File, StringComparison.OrdinalIgnoreCase))
        {
            CloseDrawer();
            return;
        }
        if (!CanLeaveChat(out var why))
        {
            CloseDrawer();
            AddNote(why, Brand.K.Muted);
            return;
        }
        if (ChatStore.Read(chat.File) is not { } conv)
        {
            CloseDrawer();
            AddNote("That chat couldn't be read. Open the history again.", Brand.K.Muted);
            return;
        }
        ShowChat(conv);
        CloseDrawer();
    }

    /// <summary>Another saved chat takes the window: its messages, its session (resumed with the next message) and its crew.</summary>
    private void ShowChat(Conversation conv)
    {
        _engine?.Dispose();
        _engine = null;
        _conversation = conv;
        _messages.Children.Clear();
        AttachCrew();
        foreach (var item in _conversation.Items.TakeLast(80)) ShowItem(item);
        ShowJobs();
        ScrollToEnd();
        _input.Focus();
    }

    void IHistoryHost.ChangeChat(string file, Action<Conversation> change)
    {
        if (string.Equals(file, _conversation.File, StringComparison.OrdinalIgnoreCase))
        {
            change(_conversation);
            ChatStore.Save(_conversation, touch: false);
            return;
        }
        if (ChatStore.Read(file) is not { } conv) return;
        change(conv);
        ChatStore.Save(conv, touch: false);
    }

    bool IHistoryHost.DeleteChat(string file)
    {
        var current = string.Equals(file, _conversation.File, StringComparison.OrdinalIgnoreCase);
        if (current && !CanLeaveChat(out _)) return false;
        if (!ChatStore.Delete(file)) return false;
        if (current) NewChat();
        return true;
    }

    /// <summary>Changes a saved chat (the Notes window edits or deletes a summary): the open chat in place, others in their file.</summary>
    internal static bool ChangeSaved(string file, Action<Conversation> change)
    {
        if (_shownWindow is { } window && string.Equals(window._conversation.File, file, StringComparison.OrdinalIgnoreCase))
        {
            change(window._conversation);
            window.Save();
            RecallChanged?.Invoke();
            return true;
        }
        if (ChatStore.Read(file) is not { } conv) return false;
        change(conv);
        ChatStore.Save(conv, touch: false);
        RecallChanged?.Invoke();
        return true;
    }

    // ---- Notes -------------------------------------------------------------------------------------------------------

    private void OpenNotes()
    {
        if (_notes is not null)
        {
            _notes.Activate();
            return;
        }
        _notes = NotesWindow(this);
        _notes.Closed += (_, _) => _notes = null;
        _notes.Show();
    }

    /// <summary>The notes in a window of their own (Sketch ProMax's Notes tab).</summary>
    internal static Window NotesWindow(Window? owner)
    {
        var window = new Window { Title = "Plaxis ProMax: notes", Width = 460, Height = 640, MinWidth = 340, MinHeight = 360, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Brand.UseTheme(window);
        if (Brand.Icon is { } icon) window.Icon = icon;
        window.SetResourceReference(BackgroundProperty, Brand.K.Bg);
        window.Resources.MergedDictionaries.Add(ChatUi.Styles());
        window.Content = NotesView.Get();
        // The view is one for the app: let go of it, so the next window can show it.
        window.Closed += (_, _) => window.Content = null;
        return window;
    }

    // ---- Routines ----------------------------------------------------------------------------------------------------

    /// <summary>Once a minute: a routine whose time has come runs as a job (Crew.Routines).</summary>
    private void StartRoutineClock()
    {
        if (_sample || _routineTimer is not null) return;
        _routineTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(45) };
        _routineTimer.Tick += (_, _) =>
        {
            _routineTimer.Interval = TimeSpan.FromMinutes(1);
            try { RoutineTick(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* tried again next minute */ }
        };
        _routineTimer.Start();
    }

    private void RoutineTick()
    {
        if (_claude is null || !_link.Connected) return;
        var folder = Crew.RoutinesFolder;
        var list = Routines.Load(folder);
        var now = DateTime.Now;
        var title = ChatScope.ProjectTitle;
        if (list.FirstOrDefault(r => Routines.Due(r, now) is not null && Crew.RoutineFits(r, title)) is not { } routine) return;
        if (!RoutineChat(routine)) return;
        if (_crew!.RoutineTick(list, now, title)) Routines.Save(folder, list);
    }

    /// <summary>A chat With coordinator for the routine: the one shown when it is one, else a new one when the window is free.</summary>
    private bool RoutineChat(Routine routine)
    {
        if (_crew is null || _crew.Run is { Active: true }) return false;
        if (IsCoordinator) return true;
        if (!CanLeaveChat(out _)) return false;
        ShowChat(ChatStore.New(_conversation.Key, _conversation.Title, ChatTypes.Coordinator));
        _conversation.Name = "Routine · " + routine.Name;
        return IsCoordinator && _crew is not null;
    }

    private void OpenRoutines()
    {
        if (_routines is not null)
        {
            _routines.Activate();
            return;
        }
        var window = _routines = new RoutinesWindow(Crew.RoutinesFolder, ChatScope.ProjectTitle) { Owner = this };
        window.Closed += (_, _) => _routines = null;
        window.RunNow += id =>
        {
            var list = Routines.Load(Crew.RoutinesFolder);
            if (list.FirstOrDefault(r => r.Id == id) is not { } routine) return "That routine is gone.";
            if (_claude is null) return "The chat isn't ready yet (Claude Code installed and signed in).";
            if (!_link.Connected) return "Connect to Plaxis first.";
            if (!Crew.RoutineFits(routine, ChatScope.ProjectTitle)) return $"It runs on {routine.Model}; open that project first.";
            // Run now: it counts as the run for now, so the next one is at its next time.
            var due = DateTime.Now;
            if (!RoutineChat(routine) || !_crew!.RunRoutine(routine, due, due, ChatScope.ProjectTitle)) return "The chat is busy (an answer, open jobs or an autopilot). Try again when it is free.";
            Routines.Save(Crew.RoutinesFolder, list);
            return null;
        };
        window.OpenRun += (chatId, job) =>
        {
            if (chatId != _conversation.Id)
            {
                if (ChatStore.All().FirstOrDefault(c => c.Id == chatId) is not { } conv || !CanLeaveChat(out _)) return;
                ShowChat(conv);
            }
            OpenJob(job);
        };
        window.Show();
    }

    // ---- Saved prompts -----------------------------------------------------------------------------------------------

    private void TogglePrompts()
    {
        if (_promptPanel.Visibility == Visibility.Visible)
        {
            _promptPanel.Visibility = Visibility.Collapsed;
            return;
        }
        ShowPrompts(SavedPrompts.Load(SavedPrompts.DefaultFile));
        _promptPanel.Visibility = Visibility.Visible;
    }

    /// <summary>The saved prompts above the message box: pick one to put it in the box, save what is in the box, or delete one.</summary>
    private void ShowPrompts(List<SavedPrompt> saved)
    {
        var head = new DockPanel { Margin = new Thickness(ChatUi.S2, ChatUi.S1, ChatUi.S1, ChatUi.S1) };
        var close = ChatUi.Ghost(ChatUi.GlyphCross, null, "Close saved prompts", "Close");
        close.Click += (_, _) => TogglePrompts();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        var save = ChatUi.Ghost(ChatUi.GlyphSave, "Save what's in the box", "Save the message box as a prompt", "Keeps the text in the message box to use again.");
        save.Click += (_, _) =>
        {
            var text = _input.Text.Trim();
            if (text.Length == 0) return;
            Change(l => SavedPrompts.Add(l, text, DateTime.UtcNow), "The prompt couldn't be saved (the prompts file is in use or read-only).");
        };
        DockPanel.SetDock(save, Dock.Right);
        head.Children.Add(save);
        var title = ChatUi.Small("Saved prompts", Brand.K.Text);
        title.FontWeight = FontWeights.SemiBold;
        title.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(title);
        var list = new StackPanel { Margin = new Thickness(ChatUi.S1, 0, ChatUi.S1, ChatUi.S1) };
        if (saved.Count == 0)
        {
            var none = Wrapped(ChatUi.Small("No saved prompts yet. Type one in the message box and press Save what's in the box."));
            none.Margin = new Thickness(ChatUi.S1, 0, 0, ChatUi.S1);
            list.Children.Add(none);
        }
        foreach (var prompt in saved)
        {
            var row = new DockPanel();
            var delete = ChatUi.Ghost(ChatUi.GlyphDelete, null, "Delete saved prompt " + prompt.Title, "Delete this saved prompt");
            delete.Click += (_, _) => Change(l => SavedPrompts.Remove(l, prompt.Text), "The prompt couldn't be deleted (the prompts file is in use or read-only).");
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            var use = ChatUi.Ghost("", prompt.Title, "Use saved prompt " + prompt.Title, prompt.Text.Length > 600 ? prompt.Text.Substring(0, 600) + "…" : prompt.Text);
            use.HorizontalContentAlignment = HorizontalAlignment.Left;
            use.Click += (_, _) =>
            {
                _input.Text = prompt.Text;
                _input.CaretIndex = _input.Text.Length;
                _promptPanel.Visibility = Visibility.Collapsed;
                _input.Focus();
            };
            row.Children.Add(use);
            list.Children.Add(row);
        }
        var panel = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _promptPanel.Child = panel;

        void Change(Func<List<SavedPrompt>, List<SavedPrompt>> change, string failed)
        {
            try
            {
                var (now, ok) = SavedPrompts.Update(SavedPrompts.DefaultFile, change);
                ShowPrompts(now);
                if (!ok) AddNote(failed, Brand.K.DangerText);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddNote(failed + " " + ex.Message, Brand.K.DangerText); }
        }
    }

    // ---- Screenshots -------------------------------------------------------------------------------------------------

    /// <summary>For the screenshots: the history drawer over a chat.</summary>
    internal void ShowSampleHistory()
    {
        ShowSample();
        OpenDrawer();
    }

    /// <summary>For the screenshots: the saved prompts over the message box.</summary>
    internal void ShowSamplePrompts()
    {
        ShowSample();
        ShowPrompts(
        [
            new SavedPrompt { Title = "Model from the borehole logs", Text = "Build the soil model from the attached borehole logs: one borehole per log, layers and materials from the GIR." },
            new SavedPrompt { Title = "Six phases for the quay wall", Text = "Set up the six construction phases for the quay wall and calculate them." },
            new SavedPrompt { Title = "Results per phase to Excel", Text = "Wall deflection, bending moment and anchor forces per phase into an Excel file beside the project." },
        ]);
        _promptPanel.Visibility = Visibility.Visible;
    }
}
