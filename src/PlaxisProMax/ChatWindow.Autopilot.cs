using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using PlaxisProMax.Chat;
using PlaxisProMax.Plaxis;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// Autopilot in the window (Sketch ProMax's ChatPane.Autopilot): New chat > New autopilot chat, the banner under the
/// header with Stop and Resume, Approve on the plan, and the backup copy of the project before the run starts. The run
/// itself is Chat.Crew (Crew.Autopilot).
/// </summary>
internal sealed partial class ChatWindow
{
    private readonly Border _autoBanner = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S2) };
    private readonly TextBlock _autoText = ChatUi.Small("", Brand.K.Text);
    private Button? _autoStop, _autoResume;
    private Button? _approve;
    private DateTime _lastActivity;
    private bool _stopping;

    /// <summary>The banner under the header while the chat has an autopilot run: its state, Stop and Resume.</summary>
    private Border AutopilotBanner()
    {
        _autoStop = Brand.Button("Stop", Brand.Weight.Danger);
        _autoStop.ToolTip = "Ends the run now: the open jobs stop and the report is written. A command already sent to Plaxis finishes on its own.";
        _autoStop.Click += (_, _) => _crew?.AutopilotStop("you pressed Stop");
        _autoResume = Brand.Button("Resume", Brand.Weight.Primary);
        _autoResume.Click += (_, _) => _crew?.AutopilotResume("You pressed Resume.");
        var buttons = Brand.ButtonRow(_autoResume, _autoStop);
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        _autoText.TextWrapping = TextWrapping.Wrap;
        _autoText.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_autoText, System.Windows.Automation.AutomationLiveSetting.Polite);
        row.Children.Add(_autoText);
        _autoBanner.Child = row;
        _autoBanner.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        _autoBanner.BorderThickness = new Thickness(0, 0, 0, 1);
        _autoBanner.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        return _autoBanner;
    }

    /// <summary>The banner follows the run (called with every change of the crew).</summary>
    private void ShowAutopilotBanner()
    {
        if (_sample) return;
        var run = _crew?.Run;
        ShowAutopilotBanner(_crew?.Banner(), run?.State ?? "");
    }

    private void ShowAutopilotBanner(string? line, string state)
    {
        _autoBanner.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        _autoText.Text = line ?? "";
        if (_autoStop is not null) _autoStop.Visibility = state is AutopilotRun.Running or AutopilotRun.Paused or AutopilotRun.Approval ? Visibility.Visible : Visibility.Collapsed;
        if (_autoResume is not null) _autoResume.Visibility = state == AutopilotRun.Paused ? Visibility.Visible : Visibility.Collapsed;
        if (_approve is not null && state != AutopilotRun.Approval) _approve.IsEnabled = false;
    }

    /// <summary>New chat's menu: a plain new chat, or one that works on its own to a goal.</summary>
    private void NewChatMenu(Button from)
    {
        var plain = new MenuItem { Header = "New chat" };
        plain.Click += (_, _) => NewChat();
        var auto = new MenuItem { Header = "New autopilot chat", ToolTip = "Type a goal and approve the plan once; the coordinator and the workers then carry on without asking you until it is met." };
        auto.Click += (_, _) => NewAutopilotChat();
        var menu = new ContextMenu { PlacementTarget = from, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(plain);
        menu.Items.Add(auto);
        menu.IsOpen = true;
    }

    /// <summary>A new chat With coordinator that waits for the goal of an autopilot run.</summary>
    private void NewAutopilotChat()
    {
        if (_busy) Stop();
        _engine?.Dispose();
        _engine = null;
        _conversation = ChatStore.New(_conversation.Key, _conversation.Title, ChatTypes.Coordinator);
        _messages.Children.Clear();
        AttachCrew();
        _crew?.StartAutopilotChat();
        _input.Focus();
    }

    /// <summary>The note that carries Approve while the plan waits for it; once approved (or changed) the button is off.</summary>
    private UIElement ApproveCard(ChatItem item)
    {
        var approve = Brand.Button("Approve and start", Brand.Weight.Primary);
        approve.IsEnabled = !_sample && _crew?.Run is { State: AutopilotRun.Approval } && ReferenceEquals(_conversation.Items.LastOrDefault(i => i.Name == Crew.ApproveNote), item);
        approve.Click += async (_, _) =>
        {
            if (_crew is not { } crew) return;
            approve.IsEnabled = false;
            await crew.ApproveAutopilot();
            // Not started (the coordinator was still answering): the button is there for another press.
            if (crew.Run is { State: AutopilotRun.Approval }) approve.IsEnabled = true;
        };
        _approve = approve;
        var panel = new StackPanel();
        panel.Children.Add(Wrapped(ChatUi.Small(item.Text, Brand.K.Text)));
        var row = new WrapPanel { Margin = new Thickness(0, ChatUi.S2, 0, 0) };
        approve.Margin = new Thickness(0);
        row.Children.Add(approve);
        panel.Children.Add(row);
        var card = Card(panel);
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.Accent);
        return card;
    }

    /// <summary>
    /// The coordinator's answer is ended now (Stop on the banner, or an answer stuck mid-stream): its Claude Code is ended,
    /// also when it hangs; the next turn resumes the chat's session.
    /// </summary>
    private void AbortMainTurn(string note)
    {
        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            engine.Event -= OnEvent;
            engine.Exited -= OnExited;
            engine.KillNow();
        }
        foreach (var view in _streams.Values)
            if (view.Tag is string { Length: > 0 } text) _conversation.Items.Add(new ChatItem { Kind = "assistant", Text = text, Time = DateTime.UtcNow });
        _streams.Clear();
        _steps.Clear();
        _turnText.Clear();
        if (note.Length > 0) Note(note);
        if (_busy) SetBusy(false);
        Save();
    }

    /// <summary>
    /// The backup before a run: the project is saved, then its file and data folder are copied into a Backup folder beside
    /// it. "" when the project has never been saved to a file, or the copy failed.
    /// </summary>
    private async Task<string> BackupProjectAsync()
    {
        try
        {
            var saved = await _link.CallAsync("save_project", new JsonObject(), 120000, CancellationToken.None);
            var status = await _link.CallAsync("status", new JsonObject(), 30000, CancellationToken.None);
            var file = (status.Ok ? status.Result?["filename"]?.ToString() : null) ?? _link.LastStatus?["filename"]?.ToString() ?? "";
            if (!saved.Ok || file.Length == 0 || !File.Exists(file)) return "";
            return await Task.Run(() => ProjectBackup.Copy(file, DateTime.Now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            Tools.ActivityLog.Note("autopilot", "backup failed: " + ex.Message);
            return "";
        }
    }

    /// <summary>For the screenshots: a run at work, its plan approved, with the banner.</summary>
    internal void ShowSampleAutopilot()
    {
        ShowSampleCrew();
        _messages.Children.Clear();
        _conversation.Items.Clear();
        _jobCards.Clear();
        var started = DateTime.Now.AddMinutes(-42);
        ShowItem(new ChatItem { Kind = "user", Text = "Build the quay wall model from the GIR and the borehole logs, run all six construction phases, and put wall deflection, bending moment and anchor forces per phase in a report." });
        ShowItem(new ChatItem { Kind = "assistant", Text = "**Plan**\n\n1. Soil parameters from the GIR (Nova, reading only).\n2. Boreholes BH-1 to BH-4, the sheet pile wall and two anchor rows.\n3. Mesh, then six phases: initial, wall, dredge to -4, anchor 1, dredge to -9, anchor 2 and dredge to -14.\n4. Calculate; any phase that fails is read and fixed.\n5. Results per phase to `Quay wall results.xlsx` and the report.\n\n**Done when:** every phase ended OK, and the report holds the three results for each phase." });
        ShowItem(new ChatItem { Kind = "note", Name = Crew.ApproveNote, Text = "Approve this plan to start the autopilot. To change it, type what to change and send." });
        ShowItem(new ChatItem { Kind = "note", Text = @"Autopilot started. Backup copy of the project: C:\Jobs\Backup\Quay wall " + started.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture) + @"\Quay wall.p2dx" });
        foreach (var job in _sampleJobs!) AddItem(new ChatItem { Kind = "pjob", Name = job.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), Text = job.Title });
        ShowJobs();
        var run = new AutopilotRun { State = AutopilotRun.Running, Started = started.ToUniversalTime() };
        ShowAutopilotBanner(Autopilot.Banner(run, 1, DateTimeOffset.UtcNow), run.State);
    }
}
