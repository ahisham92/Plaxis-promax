using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PlaxisProMax.Chat;
using PlaxisProMax.Settings;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// The crew in the window (Sketch ProMax's ChatPane.Jobs): a chat With coordinator hands work to jobs with chat_jobs, each
/// job shows as a card in the chat (Open job, Stop), the line over the composer says who does what and carries Pause all
/// / Continue, and the jobs' reports come back as the coordinator's next turn. The work itself is Chat.Crew.
/// </summary>
internal sealed partial class ChatWindow
{
    private Crew? _crew;
    private readonly Dictionary<int, ParallelJobCard> _jobCards = new();
    private readonly Dictionary<int, JobWindow> _jobWindows = new();
    private readonly CrewPlan _crewPlan = new();
    private readonly Border _crewBar = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S2) };
    private readonly TextBlock _crewText = ChatUi.Small("", Brand.K.Text);
    private Button? _pauseAll;
    private readonly CrewStrip _strip = new() { Visibility = Visibility.Collapsed, Compact = true };
    private DispatcherTimer? _jobClock;
    private List<ChatJob>? _sampleJobs;
    private DateTime _turnStart;

    /// <summary>The open chat is With coordinator (ChatTypes).</summary>
    private bool IsCoordinator => ChatTypes.Of(_conversation) == ChatTypes.Coordinator;

    /// <summary>The line over the composer: who does what now, and Pause all / Continue.</summary>
    private Border CrewBar()
    {
        _pauseAll = ParallelJobCard.Pill("Pause all", "SpmChatPill", "Every job stops as soon as its current step in Plaxis is done, and nothing new starts. Plaxis is yours meanwhile; Continue carries on.");
        _pauseAll.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        _pauseAll.Click += (_, _) =>
        {
            if (JobPause.On) _crew?.ContinueAll();
            else _crew?.PauseAll();
            ShowJobs();
        };
        var row = new DockPanel();
        DockPanel.SetDock(_pauseAll, Dock.Right);
        row.Children.Add(_pauseAll);
        _crewText.TextWrapping = TextWrapping.Wrap;
        _crewText.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_crewText, System.Windows.Automation.AutomationLiveSetting.Polite);
        row.Children.Add(_crewText);
        _crewBar.Child = row;
        _crewBar.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        _crewBar.BorderThickness = new Thickness(0, 1, 0, 0);
        _crewBar.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        return _crewBar;
    }

    /// <summary>
    /// The crew strip under the header (Sketch ProMax's CrewStrip): you, the coordinator, the workers at their desks and
    /// who applies the changes in Plaxis, one at a time. Only for a chat With coordinator.
    /// </summary>
    private CrewStrip Strip()
    {
        _strip.WorkerPicked += (slot, _) =>
        {
            // The worker's job now, else the last one they did.
            if (JobsNow().Where(j => j.Worker == slot).OrderByDescending(j => j.Active).ThenByDescending(j => j.Number).FirstOrDefault() is { } job) OpenJob(job.Number);
        };
        _strip.CoordinatorPicked += _ => { Activate(); _input.Focus(); };
        _strip.ApplierPicked += _ =>
        {
            if (JobsNow().FirstOrDefault(j => JobBoard.ActivityOf(j) == JobBoard.Applying) is { } job) OpenJob(job.Number);
        };
        _strip.WorkersChanged += SetWorkers;
        _strip.TeamPicked += () => new CrewPerformanceWindow { Owner = this }.Show();
        CrewStats.Changed += () => Dispatcher.BeginInvoke(ShowKpis);
        CrewStrip.NamesChanged += () => Dispatcher.BeginInvoke(ShowJobs);
        return _strip;
    }

    /// <summary>Today's jobs done per worker, and who leads, on the strip.</summary>
    private void ShowKpis()
    {
        if (_sample) return;
        var today = CrewStats.Since(CrewStats.All(), DateTime.Today.ToUniversalTime());
        _strip.ShowKpis(CrewStats.Workers(today).ToDictionary(k => k.Slot, k => k.Done), CrewStats.Leader(today));
    }

    /// <summary>+ or − on the strip: that many workers from now on (Settings: jobs at once); a new one takes a waiting job at once.</summary>
    private void SetWorkers(int workers)
    {
        try
        {
            var settings = AgentSettings.Load();
            settings.ChatJobLimit = JobBoard.ClampLimit(workers);
            settings.Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Note("Couldn't change the number of workers: " + ex.Message);
            return;
        }
        _crew?.PumpJobs();
        ShowJobs();
    }

    /// <summary>The shown chat's crew: the one before it ends (its jobs stop), and the MCP server reaches this one.</summary>
    private void AttachCrew()
    {
        foreach (var window in _jobWindows.Values.ToList()) window.Close();
        _jobWindows.Clear();
        _jobCards.Clear();
        _crewPlan.Reset();
        _strip.Clear();
        _crew?.Dispose();
        _crew = null;
        _drafts.Clear();
        _phoneQuestions.Clear();
        _lastPick = null;
        _restartEngine = false;
        _typedMeanwhile.Clear();
        _asides.Clear();
        if (_questionsShown) HideQuestions(focus: false);
        _follow = true;
        ShowTitle();
        if (_sample) return;
        ChatScope.Set(_conversation.Key, _conversation.Title, _conversation.Id);
        ChatJobs.ChatModel = ChatAuto.ParseModel(_conversation.Model);
        ChatJobs.ChatEffort = ChatAuto.ParseEffort(_conversation.Effort);
        _crew = new Crew(a => Dispatcher.BeginInvoke(a), _conversation, () => _claude, new CrewHost
        {
            Changed = ShowJobs,
            Note = Note,
            MainBusy = () => _busy,
            MainTurn = MainTurn,
            Save = Save,
            AddItem = AddItem,
            AbortMainTurn = AbortMainTurn,
            MainLastActivity = () => _lastActivity,
            MainStepRunning = () => _steps.Count > 0,
            Backup = BackupProjectAsync,
            SignInLost = () => Error("Your Claude sign-in has expired. Sign in again in Settings > Claude; the jobs carry on when you send them a message."),
        });
        ShowJobs();
    }

    /// <summary>The settings' defaults a new chat starts with (again after Settings is saved).</summary>
    internal static void ApplyChatDefaults()
    {
        var s = AgentSettings.Load();
        ChatStore.NewType = ChatTypes.Parse(s.ChatTypeDefault);
        ChatStore.NewModel = ChatAuto.ParseModel(s.ChatModelDefault);
        ChatStore.NewEffort = ChatAuto.ParseEffort(s.ChatEffortDefault);
        CrewStrip.ReloadNames();
    }

    /// <summary>The jobs' reports (or a nudge to share work) go to the coordinator as its next turn; the user never typed it.</summary>
    private void MainTurn(string text, bool quiet)
    {
        if (_busy || _claude is null) return;
        SetBusy(true);
        _turnStart = DateTime.UtcNow;
        _lastActivity = DateTime.UtcNow;
        _working.Text = quiet ? "Sharing the work…" : "Reading the jobs' reports…";
        _ = SendAsync(text);
    }

    /// <summary>The jobs as they are now (the samples' own for the screenshots).</summary>
    private List<ChatJob> JobsNow() => _sampleJobs ?? _crew?.Board.Snapshot() ?? [];

    private UIElement ParallelJobCardFor(ChatItem item)
    {
        int.TryParse(item.Name, out var number);
        var card = new ParallelJobCard(number);
        card.StopPressed += StopJob;
        card.Opened += OpenJob;
        _jobCards[number] = card;
        if (JobsNow().FirstOrDefault(j => j.Number == number) is { } job) card.Render(job, DateTime.UtcNow, ChatJobs.Limit);
        return card;
    }

    /// <summary>
    /// A new job gets its card in the chat (where it was started), every card shows its job as it is now, and the crew's
    /// line says who does what.
    /// </summary>
    private void ShowJobs()
    {
        ShowAutopilotBanner();
        var jobs = JobsNow();
        foreach (var job in jobs.Where(j => j.Active || j.State == JobBoard.Queued))
        {
            var name = job.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_conversation.Items.Any(i => i.Kind == "pjob" && i.Name == name)) AddItem(new ChatItem { Kind = "pjob", Name = name, Text = job.Title, Time = DateTime.UtcNow });
        }
        // Each job keeps its worker with the chat, so its card names who did it.
        var limit = ChatJobs.Limit;
        var events = _crewPlan.Update(jobs, DateTime.UtcNow, JobBoard.ActivityOf, limit);
        _strip.Visibility = IsCoordinator ? Visibility.Visible : Visibility.Collapsed;
        if (IsCoordinator) _strip.Show(_crewPlan.Seats, events, limit, _crewPlan.Unassigned);
        foreach (var seat in _crewPlan.Seats)
            if (jobs.FirstOrDefault(j => j.Number == seat.Number) is { } job && job.Worker != seat.Slot)
            {
                _crew?.Board.SetWorker(seat.Number, seat.Slot);
                job.Worker = seat.Slot;
            }
        var now = DateTime.UtcNow;
        foreach (var job in jobs)
        {
            if (_jobCards.TryGetValue(job.Number, out var card)) card.Render(job, now, limit);
            if (_jobWindows.TryGetValue(job.Number, out var window)) window.Render(job, _crew?.Board.ItemsOf(job.Number) ?? job.Items);
        }
        var open = jobs.Count(j => j.Open);
        var paused = JobPause.On;
        _crewBar.Visibility = open > 0 || paused ? Visibility.Visible : Visibility.Collapsed;
        _crewText.Text = paused
            ? (_crew is { Running: > 0 } ? "Pausing: each job stops when its current step in Plaxis is done." : "All jobs are paused. Plaxis is yours; press Continue to carry on.")
            : open == 1 ? "1 job is open." : $"{open} jobs are open.";
        if (_pauseAll is not null) _pauseAll.Content = paused ? "Continue" : "Pause all";
        if ((open > 0 || _crewPlan.Lingering) && !_sample)
        {
            if (_jobClock is null)
            {
                _jobClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _jobClock.Tick += (_, _) => TickJobs();
            }
            _jobClock.Start();
        }
        else _jobClock?.Stop();
        QuestionsChanged();
    }

    /// <summary>While jobs run, their cards' clocks move.</summary>
    private void TickJobs()
    {
        var jobs = JobsNow();
        var now = DateTime.UtcNow;
        var limit = ChatJobs.Limit;
        foreach (var job in jobs.Where(j => j.Active))
            if (_jobCards.TryGetValue(job.Number, out var card)) card.Render(job, now, limit);
        // A finished job's worker leaves the strip after a few seconds even when nothing else changes meanwhile.
        if (_crewPlan.Lingering) ShowJobs();
        else if (!jobs.Any(j => j.Open)) _jobClock?.Stop();
    }

    private void StopJob(int number)
    {
        if (_crew is null || !_crew.StopJob(number)) return;
        Note($"Job {number} stopped. A command it had already sent to Plaxis finishes on its own.");
    }

    /// <summary>Open job on a card: the job's own chat in a window beside this one, where the user talks to it.</summary>
    private void OpenJob(int number)
    {
        if (_jobWindows.TryGetValue(number, out var open))
        {
            open.Activate();
            return;
        }
        if (JobsNow().FirstOrDefault(j => j.Number == number) is not { } job) return;
        var window = new JobWindow(number) { Owner = this };
        window.Sent += text =>
        {
            if (_crew is null) return;
            var delivery = _crew.SendToJob(number, text);
            if (delivery == JobBoard.Delivery.NotFound) Note($"Job {number} is no longer in this chat.");
        };
        window.StopPressed += () => StopJob(number);
        window.Closed += (_, _) => _jobWindows.Remove(number);
        _jobWindows[number] = window;
        window.Render(job, _crew?.Board.ItemsOf(number) ?? job.Items);
        window.Show();
    }

    /// <summary>The coordinator's turn ended: its numbers are kept, and reports that waited for it go in now.</summary>
    private void AfterTurn(ResultEvent result)
    {
        if (_sample) return;
        if (IsCoordinator)
            CrewStats.Add(new CrewRecord
            {
                Start = _turnStart == default ? DateTime.UtcNow : _turnStart, End = DateTime.UtcNow, Role = CrewRecord.Coordinator, Chat = _conversation.Id,
                In = result.InputTokens, Out = result.OutputTokens, Usd = result.CostUsd, Ok = !result.IsError,
            });
        _strip.CoordinatorDoes(null);
        _crew?.FlushReports();
    }

    /// <summary>For the screenshots: a coordinator's chat with three jobs at work.</summary>
    internal void ShowSampleCrew()
    {
        _plaxisState.Text = "PLAXIS 2D 2024.2 · Quay wall";
        var now = DateTime.UtcNow;
        _sampleJobs =
        [
            new ChatJob { Number = 1, Title = "Soil parameters from the GIR", State = JobBoard.Done, Kind = ChatAuto.Hard, Model = "opus", Effort = "high", Worker = 0, Created = now.AddMinutes(-9), Started = now.AddMinutes(-9), Ended = now.AddMinutes(-3),
                Summary = "Read the interpretive report: five strata with HS small parameters for each, in parameters.xlsx. A teammate checked the work in the project: it passed." },
            new ChatJob { Number = 2, Title = "Boreholes BH-1 to BH-4 and the wall", State = JobBoard.Running, Activity = JobBoard.Applying, Kind = ChatAuto.Everyday, Model = "sonnet", Effort = "medium", Worker = 1, Created = now.AddMinutes(-3), Started = now.AddMinutes(-3),
                Now = "Create boreholes: BH-3 with five layers" },
            new ChatJob { Number = 3, Title = "Design basis against the model", State = JobBoard.Running, Kind = ChatAuto.Hard, Model = "opus", Effort = "high", Worker = 2, Created = now.AddMinutes(-3), Started = now.AddMinutes(-2),
                Now = "Read the design basis: section 4, loads and water levels" },
        ];
        ShowItem(new ChatItem { Kind = "user", Text = "Build the quay wall model from the attached GIR and borehole logs, and check it against the design basis." });
        ShowItem(new ChatItem { Kind = "tool", Name = "status", Text = "Checked the open Plaxis project", State = "done" });
        ShowItem(new ChatItem { Kind = "tool", Name = "chat_jobs", Text = "Start a job: Soil parameters from the GIR", State = "done" });
        ShowItem(new ChatItem { Kind = "assistant", Text = "Nova works out the soil parameters from the report first; the model is built from them, and Pixel checks it against the design basis meanwhile." });
        // Each job's card once, in the order they were started (ShowJobs adds a card only for a job that has none).
        foreach (var job in _sampleJobs) AddItem(new ChatItem { Kind = "pjob", Name = job.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), Text = job.Title });
        ShowJobs();
    }
}

/// <summary>
/// A job's own chat (Sketch ProMax's job view): everything it was asked and did, a box to write to it, and Stop. Drawn
/// again from the job when it changes; only new items are added, so reading isn't disturbed.
/// </summary>
internal sealed class JobWindow : Window
{
    private readonly StackPanel _items = new() { Margin = new Thickness(ChatUi.S4, ChatUi.S3, ChatUi.S4, ChatUi.S3) };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _title = Brand.Text("", Brand.K.Text, ChatUi.BodySize, bold: true);
    private readonly TextBlock _state = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
    private readonly TextBox _input;
    private readonly Button _stop;
    private readonly List<(ChatItem Item, string State, Action<ChatItem>? Update)> _drawn = [];

    /// <summary>The user wrote to the job.</summary>
    public event Action<string>? Sent;

    /// <summary>Stop job was pressed.</summary>
    public event Action? StopPressed;

    public JobWindow(int number)
    {
        Title = $"Plaxis ProMax: job {number}";
        Width = 460;
        Height = 640;
        MinWidth = 340;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.UseTheme(this);
        if (Brand.Icon is { } icon) Icon = icon;
        SetResourceReference(BackgroundProperty, Brand.K.Bg);
        Resources.MergedDictionaries.Add(ChatUi.Styles());

        _stop = ParallelJobCard.Pill("Stop job", "SpmChatDanger", "Stops this job. A command it already sent to Plaxis finishes on its own; send it a message later to carry on.");
        _stop.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        _stop.VerticalAlignment = VerticalAlignment.Center;
        _stop.Click += (_, _) => StopPressed?.Invoke();
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _state.TextTrimming = TextTrimming.CharacterEllipsis;
        names.Children.Add(_title);
        names.Children.Add(_state);
        var head = new DockPanel { Margin = new Thickness(ChatUi.S4, ChatUi.S2, ChatUi.S3, ChatUi.S2) };
        var mark = ChatUi.Mark(24);
        mark.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(_stop, Dock.Right);
        head.Children.Add(mark);
        head.Children.Add(_stop);
        head.Children.Add(names);
        var header = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        header.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);

        _scroll = new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // The message box as in the main chat: one raised card, the box with its hint, and the round Send.
        _input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = ChatStyles.ComposerMinHeight, MaxHeight = ChatStyles.ComposerMaxHeight,
            Padding = new Thickness(ChatStyles.ComposerPadX, ChatStyles.ComposerPadY, ChatStyles.ComposerPadX, ChatStyles.ComposerPadY),
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = ChatUi.BodySize,
        };
        _input.SetResourceReference(StyleProperty, "SpmChatBare");
        System.Windows.Automation.AutomationProperties.SetName(_input, $"Message to job {number}");
        var send = new Button { Content = ChatUi.Icon(ChatUi.Paths.Send, stroke: 2), ToolTip = "Send to the job (Enter)" };
        send.SetResourceReference(StyleProperty, "SpmChatSend");
        System.Windows.Automation.AutomationProperties.SetName(send, "Send to job");
        void Send()
        {
            var text = _input.Text.Trim();
            if (text.Length == 0) return;
            _input.Clear();
            Sent?.Invoke(text);
        }
        send.Click += (_, _) => Send();
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None) { e.Handled = true; Send(); }
        };
        var placeholder = ChatUi.Small("Write to this job…");
        placeholder.FontSize = ChatUi.BodySize;
        placeholder.IsHitTestVisible = false;
        placeholder.VerticalAlignment = VerticalAlignment.Top;
        placeholder.Margin = new Thickness(ChatStyles.ComposerPadX + 2, ChatStyles.ComposerPadY, ChatStyles.ComposerPadX + 2, 0);
        _input.TextChanged += (_, _) => placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var box = new Grid();
        box.Children.Add(_input);
        box.Children.Add(placeholder);
        var field = new Border { Child = box, CornerRadius = new CornerRadius(ChatUi.RadiusSmall), SnapsToDevicePixels = true, Margin = new Thickness(ChatUi.S2, ChatUi.S2, ChatUi.S2, ChatUi.S1), BorderThickness = new Thickness(1) };
        field.SetResourceReference(Border.BackgroundProperty, Brand.K.ComposerField);
        void FieldState() => field.SetResourceReference(Border.BorderBrushProperty, ChatStyles.ComposerBorder(_input.IsKeyboardFocused, field.IsMouseOver));
        FieldState();
        field.MouseEnter += (_, _) => FieldState();
        field.MouseLeave += (_, _) => FieldState();
        _input.IsKeyboardFocusedChanged += (_, _) => FieldState();
        var hint = ChatUi.Small("A working job gets your message with its next step; a finished one carries on with it.");
        hint.TextWrapping = TextWrapping.Wrap;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(ChatUi.S2, 0, ChatUi.S2, 0);
        var row = new DockPanel { Margin = new Thickness(ChatUi.S1, 0, ChatUi.S2, ChatUi.S2) };
        DockPanel.SetDock(send, Dock.Right);
        row.Children.Add(send);
        row.Children.Add(hint);
        var inside = new StackPanel();
        inside.Children.Add(field);
        inside.Children.Add(row);
        var foot = ChatUi.Elevated(inside, out _);
        foot.Margin = new Thickness(ChatUi.S3, 0, ChatUi.S3, ChatUi.S3);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(foot);
        root.Children.Add(_scroll);
        Content = root;
    }

    /// <summary>Draws the job as it is now: its name and state, and the items not drawn yet (a step's state where it changed).</summary>
    public void Render(ChatJob job, IReadOnlyList<ChatItem> items)
    {
        _title.Text = $"Job {job.Number} · {job.Title}";
        var who = job.Worker >= 0 ? ChatJobs.Names.Worker(job.Worker) + " · " : "";
        _state.Text = who + JobBoard.StateLabel(job) + (job.Model.Length > 0 ? " · " + ParallelJobCard.RunsOn(job) : "");
        _stop.Visibility = job.Active || job.State == JobBoard.Queued ? Visibility.Visible : Visibility.Collapsed;
        // The list was started again (its session was reopened from the task): draw it from the top.
        if (items.Count < _drawn.Count || _drawn.Where((d, i) => !ReferenceEquals(d.Item, items[i])).Any())
        {
            _drawn.Clear();
            _items.Children.Clear();
        }
        for (var i = 0; i < _drawn.Count; i++)
        {
            var (item, state, update) = _drawn[i];
            if (item.State == state || update is null) continue;
            update(item);
            _drawn[i] = (item, item.State, update);
        }
        var added = false;
        foreach (var item in items.Skip(_drawn.Count).ToList())
        {
            _drawn.Add((item, item.State, Draw(item)));
            added = true;
        }
        if (added) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _scroll.ScrollToEnd());
    }

    /// <summary>For the screenshots: a job building the soil while its teammates read.</summary>
    internal void ShowSample()
    {
        var now = DateTime.UtcNow;
        var job = new ChatJob { Number = 2, Title = "Boreholes BH-1 to BH-4 and the wall", State = JobBoard.Running, Activity = JobBoard.Applying, Model = "sonnet", Effort = "medium", Worker = 1, Started = now.AddMinutes(-3) };
        Render(job,
        [
            new ChatItem { Kind = "user", State = JobBoard.FromCoordinator, Text = "Create boreholes BH-1 to BH-4 from the logs with the five strata Nova worked out (parameters.xlsx), then the sheet pile wall at x = 0 from +2.5 to -14 m.\n\nDone when: list_model shows four boreholes with five layers each, every layer has a material, and the plate exists." },
            new ChatItem { Kind = "tool", Name = "status", Text = "Checked the open Plaxis project", State = "done" },
            new ChatItem { Kind = "tool", Name = "create_soil_material", Text = "Made 5 soil materials", State = "done" },
            new ChatItem { Kind = "assistant", Text = "The five materials are in. Now the boreholes, one at a time: Plaxis takes one command at a time, so Pixel's reading waits its turn between them." },
            new ChatItem { Kind = "tool", Name = "create_boreholes", Text = "Made boreholes BH-1 and BH-2 with five layers each", State = "done" },
            new ChatItem { Kind = "tool", Name = "create_boreholes", Text = "Create boreholes: BH-3 with five layers", State = "running" },
        ]);
        _input.Text = "Use the wall's top level from drawing S-201 instead: +3.0";
    }

    private Action<ChatItem>? Draw(ChatItem item)
    {
        switch (item.Kind)
        {
            case "user":
            {
                // From the coordinator or from you: who, over a bubble with the tail at the bottom right.
                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(ChatUi.S8, ChatUi.S4, 0, ChatUi.S1) };
                var who = ChatUi.Small(item.State == JobBoard.FromCoordinator ? "From the coordinator" : "You");
                who.HorizontalAlignment = HorizontalAlignment.Right;
                who.Margin = new Thickness(0, 0, 0, ChatUi.S1);
                panel.Children.Add(who);
                var bubble = new Border
                {
                    Child = ChatUi.Selectable(item.Text, item.State == JobBoard.FromCoordinator ? "From the coordinator" : "Message to the job"),
                    Padding = new Thickness(ChatUi.S4, ChatUi.S2 + 2, ChatUi.S4, ChatUi.S2 + 2), MaxHeight = 320,
                    CornerRadius = new CornerRadius(ChatUi.Radius, ChatUi.Radius, ChatUi.S1, ChatUi.Radius), HorizontalAlignment = HorizontalAlignment.Right, SnapsToDevicePixels = true,
                };
                bubble.SetResourceReference(Border.BackgroundProperty, Brand.K.AccentSoft);
                panel.Children.Add(bubble);
                _items.Children.Add(panel);
                return null;
            }
            case "assistant":
            {
                var view = new MarkdownView { Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1) };
                view.SetMarkdown(item.Text);
                _items.Children.Add(view);
                return null;
            }
            case "tool":
            {
                // A step as in the main chat: a one-line card with its ring, check or cross.
                var mark = new ContentControl { Width = 18, Height = 18, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
                var line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ChatUi.S2, 0, 0, 0) };
                var row = new DockPanel();
                DockPanel.SetDock(mark, Dock.Left);
                row.Children.Add(mark);
                row.Children.Add(line);
                var card = new Border { Child = row, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(ChatUi.Radius), Padding = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S2), Margin = new Thickness(0, ChatUi.S1, 0, 0), SnapsToDevicePixels = true };
                card.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
                card.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
                void Show(ChatItem step)
                {
                    mark.Content = step.State switch
                    {
                        "running" => ChatUi.StepRing(),
                        "failed" => ChatUi.StepMark(done: false),
                        "stopped" => ChatUi.Glyph(ChatUi.GlyphStop, Brand.K.Muted, 11),
                        _ => ChatUi.StepMark(done: true),
                    };
                    line.Text = StepNames.Line(step.Name, step.Text);
                    Brand.Fg(line, step.State == "failed" ? Brand.K.DangerText : Brand.K.Muted);
                    if (step.Result.Length > 0) card.ToolTip = step.Result.Length > 600 ? step.Result.Substring(0, 600) + "…" : step.Result;
                }
                Show(item);
                _items.Children.Add(card);
                return Show;
            }
            default:
            {
                var note = ChatWindow.Wrapped(ChatUi.Small(item.Text, item.Kind == "error" ? Brand.K.DangerText : Brand.K.Muted));
                note.Margin = new Thickness(0, ChatUi.S2, 0, ChatUi.S2);
                _items.Children.Add(note);
                return null;
            }
        }
    }
}
