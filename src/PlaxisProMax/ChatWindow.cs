using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PlaxisProMax.Chat;
using PlaxisProMax.Licensing;
using PlaxisProMax.Mcp;
using PlaxisProMax.Plaxis;
using PlaxisProMax.Settings;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// The chat window beside Plaxis: Sketch ProMax's chat (its engine, stream parser, Markdown view, styles and step
/// names) in a window of its own, so a long Plaxis call never freezes it (Sketch ProMax bug 110). Approval cards show
/// inline and never block: the call waits, the window doesn't (bugs 79, 80, 112).
/// </summary>
internal sealed partial class ChatWindow : Window, IApprovalAsker
{
    private readonly PlaxisLink _link;
    private readonly PlaxisInstall.StartArgs _start;
    private readonly Func<ChatMcp> _mcp;
    private readonly StackPanel _messages = new() { Margin = new Thickness(ChatUi.S4, ChatUi.S3, ChatUi.S4, ChatUi.S3) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly Button _stop;
    private readonly TextBlock _plaxisState = Brand.Text("Connecting to Plaxis…", Brand.K.HeaderSub, 12);
    private readonly TextBlock _working = ChatUi.Small("");
    private readonly List<string> _attached = [];
    private readonly WrapPanel _attachments = new();
    private readonly Dictionary<string, MarkdownView> _streams = [];
    private readonly Dictionary<string, StepRow> _steps = [];
    private ClaudeCli.Install? _claude;
    private ChatEngine? _engine;
    private Conversation _conversation;
    private bool _busy;
    private readonly bool _sample;
    private MarkdownView? _lastAnswer;
    private readonly System.Text.StringBuilder _turnText = new();

    public ChatWindow(PlaxisLink link, PlaxisInstall.StartArgs start, Func<ChatMcp> mcp, bool sample = false)
    {
        _sample = sample;
        _link = link;
        _start = start;
        _mcp = mcp;
        _conversation = ChatStore.New("plaxis:unsaved", "Plaxis");
        Title = "Plaxis ProMax";
        Width = 480;
        Height = 820;
        MinWidth = 360;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 8;
        Top = area.Top + 8;
        Height = Math.Min(Height, area.Height - 16);
        Brand.UseTheme(this);
        if (Brand.Icon is { } icon) Icon = icon;
        SetResourceReference(BackgroundProperty, Brand.K.Bg);
        Resources.MergedDictionaries.Add(ChatUi.Styles());

        var header = Header();
        _scroll = Conversation();
        _input = InputBox();
        _send = new Button { Content = ChatUi.Icon(ChatUi.Paths.Send, stroke: 2), ToolTip = "Send (Enter)" };
        _stop = new Button { Content = ChatUi.Icon(ChatUi.Paths.Square, fill: true), Visibility = Visibility.Collapsed, ToolTip = "Stops the answer (Escape). A command already sent to Plaxis finishes on its own." };
        var foot = Composer();

        var root = new DockPanel();
        // Docked first, so the jobs column runs the whole height on the right of a wide window.
        var side = Side();
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(header);
        var updateBar = UpdateBar();
        DockPanel.SetDock(updateBar, Dock.Top);
        root.Children.Add(updateBar);
        var strip = Strip();
        DockPanel.SetDock(strip, Dock.Top);
        root.Children.Add(strip);
        var autoBanner = AutopilotBanner();
        DockPanel.SetDock(autoBanner, Dock.Top);
        root.Children.Add(autoBanner);
        root.Children.Add(foot);
        var crewBar = CrewBar();
        DockPanel.SetDock(crewBar, Dock.Bottom);
        root.Children.Add(crewBar);
        root.Children.Add(Center());
        Content = root;
        // Escape stops the answer from anywhere in the window.
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            if (_questionsShown) { e.Handled = true; HideQuestions(focus: true); return; }
            if (!_busy) return;
            e.Handled = true;
            StopNow();
        };

        RestorePlace();
        Closing += (_, _) => SavePlace();
        _link.ConnectionChanged += connected => Dispatcher.BeginInvoke(() => ShowPlaxis(connected));
        if (!sample) Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            _crew?.Dispose();
            _engine?.KillNow();
        };
    }

    // ---- Start: Plaxis, then Claude Code ---------------------------------------------------------------------------

    private async Task StartAsync()
    {
        _input.Focus();
        // The owner's later changes (cancelled keys, earlier end dates), then the License window if this PC is locked.
        _ = Task.Run(() => LicenseState.RefreshChangesAsync(CancellationToken.None));
        LicenseWindow.BlockIfLocked();
        ApplyChatDefaults();
        _conversation = ChatStore.New("plaxis:unsaved", "Plaxis");
        AttachCrew();
        StartPhone();
        StartHelp();
        StartRoutineClock();
        await ConnectPlaxisAsync();
        await FindClaudeAsync();
    }

    private async Task ConnectPlaxisAsync()
    {
        var settings = AgentSettings.Load();
        var port = _start.Port ?? settings.InputPort;
        var password = _start.Password ?? settings.ServerPassword;
        if (_start.Port is null && string.IsNullOrEmpty(password))
        {
            ShowConnectCard(port, "");
            return;
        }
        var answer = await _link.ConnectAsync(port, password, CancellationToken.None);
        if (answer.Ok) OpenProjectChat();
        else ShowConnectCard(port, answer.Error);
    }

    private void ShowPlaxis(bool connected)
    {
        var s = _link.LastStatus;
        if (!connected || s is null)
        {
            _plaxisState.Text = "Not connected to Plaxis";
            return;
        }
        var file = Path.GetFileNameWithoutExtension(s["filename"]?.ToString() ?? "");
        var project = file.Length > 0 ? file : s["title"]?.ToString() ?? "new project";
        _plaxisState.Text = $"{s["product"]} {s["version"]} · {project}";
    }

    /// <summary>The project's own chat: its latest conversation, resumed (as Sketch ProMax keeps chats per model).</summary>
    private void OpenProjectChat()
    {
        ShowPlaxis(true);
        var s = _link.LastStatus;
        var file = s?["filename"]?.ToString();
        var key = string.IsNullOrEmpty(file) ? "plaxis:unsaved" : "plaxis:" + file.ToLowerInvariant();
        var title = string.IsNullOrEmpty(file) ? "Unsaved project" : Path.GetFileNameWithoutExtension(file);
        if (_conversation.Key == key && _messages.Children.Count > 0) return;
        _conversation = ChatStore.Load(key, title);
        _engine?.Dispose();
        _engine = null;
        _messages.Children.Clear();
        AttachCrew();
        foreach (var item in _conversation.Items.TakeLast(80)) ShowItem(item);
        if (_conversation.Items.Count == 0)
            Note($"Connected to {s?["product"]} {s?["version"]}. Ask for what you need: build a model from a borehole log, set up phases, calculate, or read results.");
        ScrollToEnd();
    }

    private void ShowConnectCard(int port, string error)
    {
        var portBox = new TextBox { Text = port.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 90 };
        var password = new PasswordBox { Width = 200, MinHeight = 30, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
        var remember = new CheckBox { Content = "Remember the password on this PC", IsChecked = true, Margin = new Thickness(0, ChatUi.S1, 0, 0) };
        var connect = Brand.Button("Connect", Brand.Weight.Primary);
        var panel = new StackPanel();
        panel.Children.Add(Brand.Text("Connect to Plaxis", Brand.K.Text, 14, bold: true));
        panel.Children.Add(Wrapped(ChatUi.Small(error.Length > 0 ? error : "In Plaxis Input, open Expert > Configure remote scripting server, start the server, and enter its port and password here. Started from Plaxis's Expert menu, this happens by itself.")));
        var fields = new WrapPanel { Margin = new Thickness(0, ChatUi.S2, 0, 0) };
        fields.Children.Add(Labelled("Port", portBox));
        fields.Children.Add(Labelled("Password", password));
        panel.Children.Add(fields);
        panel.Children.Add(remember);
        panel.Children.Add(Brand.ButtonRow(connect));
        var card = Card(panel);
        connect.Click += async (_, _) =>
        {
            if (!int.TryParse(portBox.Text, out var p)) return;
            Brand.SetBusy(connect, true, "Connecting…");
            var answer = await _link.ConnectAsync(p, password.Password, CancellationToken.None);
            Brand.SetBusy(connect, false);
            if (!answer.Ok)
            {
                ((TextBlock)panel.Children[1]).Text = answer.Error;
                return;
            }
            var s = AgentSettings.Load();
            s.InputPort = p;
            if (remember.IsChecked == true) s.ServerPassword = password.Password;
            s.Save();
            _messages.Children.Remove(card);
            OpenProjectChat();
        };
        _messages.Children.Add(card);
    }

    private async Task FindClaudeAsync()
    {
        var found = await Task.Run(() =>
        {
            var install = ClaudeCli.Find();
            return (install, signIn: install is null ? ClaudeCli.SignIn.Unknown : ClaudeCli.CheckSignIn(install).State);
        });
        _claude = found.install;
        if (_claude is not null && found.signIn != ClaudeCli.SignIn.SignedOut) RememberLeftOver();
        if (_claude is null)
        {
            var install = Brand.Button("Install Claude Code", Brand.Weight.Primary);
            var again = Brand.Button("Check again");
            var panel = Setup("Claude Code isn't on this PC yet",
                "Plaxis ProMax chats through Claude Code on your Claude plan (no API key). Install it once; it takes a minute.", install, again);
            install.Click += (_, _) => ClaudeCli.StartInstall();
            again.Click += async (_, _) => { _messages.Children.Remove(panel); await FindClaudeAsync(); };
        }
        else if (found.signIn == ClaudeCli.SignIn.SignedOut)
        {
            ShowSignInCard(_claude);
        }
    }

    private Border Setup(string title, string text, params Button[] buttons)
    {
        var panel = new StackPanel();
        panel.Children.Add(Brand.Text(title, Brand.K.Text, 14, bold: true));
        panel.Children.Add(Wrapped(ChatUi.Small(text)));
        panel.Children.Add(Brand.ButtonRow(buttons));
        var card = Card(panel);
        _messages.Children.Add(card);
        return card;
    }

    // ---- Sending ---------------------------------------------------------------------------------------------------

    private void SendOrStop()
    {
        // "4 yes" answers question 4 of the Questions tab, also while the chat is busy.
        if (_input.Text.Trim().Length > 0 && _attached.Count == 0 && TypedAnswers(_input.Text.Trim()))
        {
            _input.Clear();
            return;
        }
        if (_busy)
        {
            // Typed while it works: a question is answered at once, anything else waits for the answer to end.
            if (!TypedWhileBusy()) StopNow();
            return;
        }
        Send(_input.Text.Trim(), typed: true);
    }

    /// <summary>Stop on the composer, or Escape: while a run works it ends the run (not only this answer, which it would start again).</summary>
    private void StopNow()
    {
        if (!_busy) return;
        if (_crew is { Run.State: AutopilotRun.Running } crew) crew.AutopilotStop("you pressed Stop");
        else Stop();
    }

    /// <summary>Sends <paramref name="text"/> with the attached files (typed here, or written on the phone).</summary>
    private void Send(string text, bool typed)
    {
        if (text.Length == 0 && (!typed || _attached.Count == 0)) return;
        // A PC without a valid key: the License window, and nothing is sent (as in Sketch ProMax).
        if (LicenseWindow.BlockIfLocked())
        {
            Note("Plaxis ProMax needs a license for this PC. Send the PC code in the License window to get a key.");
            return;
        }
        if (_claude is null)
        {
            Note("Claude Code isn't ready yet: install it or sign in with the card above.");
            return;
        }
        // Written on the phone: "4 yes" answers a question there too.
        if (!typed && TypedAnswers(text)) return;
        if (_questionsShown) HideQuestions(focus: false);
        HearAway(text);
        var raw = text;
        var files = typed ? _attached.ToList() : [];
        if (typed)
        {
            if (_attached.Count > 0)
            {
                // Word, Excel and PowerPoint files are read here (Claude Code's Read can't open them); the rest by path.
                try { text += Attachments.Note(_attached.ToList(), ClaudeCli.AttachmentsFolder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text += "\n\nAttached files:\n" + string.Join("\n", _attached.Select(f => "- " + f)); }
            }
            _input.Clear();
            _attached.Clear();
            ShowChips();
        }
        AddItem(new ChatItem { Kind = "user", Text = text, Time = DateTime.UtcNow });
        if (_conversation.Items.Count(i => i.Kind == "user") == 1) _conversation.Name = ChatHistory.AutoTitle(text);
        ShowTitle();
        // An autopilot chat: the goal goes to the coordinator as a request for a plan; "stop" ends a run; anything typed
        // to a paused or ended run carries it on.
        var handled = false;
        var plan = _crew?.AutopilotSend(text, out handled);
        if (handled) return;
        SupersedeBriefs();
        // A correction, a repeat or a plain question carries how to take it; a correction also leaves a lesson.
        var hint = plan is null ? FocusHint(raw, files.Count > 0) : "";
        if (plan is null && typed) LearnFrom(raw);
        // A task that changes the project is first written as a brief to check (the setting in the ⋯ menu).
        if (plan is null && typed && StartBrief(raw, text, files)) return;
        _mcp().NewTask();
        _strip.Heard();
        SetBusy(true);
        _turnStart = DateTime.UtcNow;
        _lastActivity = DateTime.UtcNow;
        if (plan is not null) { _ = SendAsync(plan); return; }
        // A coordinator hears with each message which jobs are at work and what they changed so far (JobTruth).
        _ = SendAsync(text + hint + TakeAsides() + (IsCoordinator && _crew is { } crew ? JobTruth.Note(crew.Board.Snapshot()) : ""));
    }

    private async Task SendAsync(string text)
    {
        try
        {
            // A new chat starts knowing the chat just left: its summary is waited for (a little).
            if (_conversation.SessionId.Length == 0) await WaitForMemory();
            await SendLineAsync(StreamInput.UserMessage(text, []), text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Error("The chat couldn't start: " + ex.Message);
            SetBusy(false);
        }
    }

    /// <summary>Sends one input line to the chat's Claude Code, starting it (resumed) when it isn't running.</summary>
    private async Task SendLineAsync(string line, string text)
    {
        {
            // The chat's model, effort or kind changed: its process starts again for it (the same conversation).
            if (_restartEngine && _engine is not null)
            {
                _engine.Dispose();
                _engine = null;
            }
            _restartEngine = false;
            if (_engine is null || !_engine.IsAlive)
            {
                _engine?.Dispose();
                var resume = _conversation.SessionId.Length > 0 ? _conversation.SessionId : null;
                // The coordinator runs light, on one model for the whole chat; a Simple chat on what its first request needs
                // (ChatAuto). A model or effort set by hand in Settings wins.
                var settings = AgentSettings.Load();
                var coordinator = IsCoordinator;
                var pick = ChatJobs.Pick(text, _conversation.Model, _conversation.Effort, coordinator ? ChatAuto.Coordinating : null);
                // The chat's own choice (its pills) wins; then a model or effort set by hand in Settings; then Automatic's pick.
                var model = !pick.ModelAuto || settings.ChatModel.Trim().Length == 0 ? pick.Model : settings.ChatModel.Trim();
                var effort = ClaudeCli.EffortRefused ? "" : !pick.EffortAuto || settings.ChatEffort.Trim().Length == 0 ? pick.Effort : settings.ChatEffort.Trim();
                var shown = pick with { Model = model, Effort = effort };
                var changed = _lastPick is null || _lastPick.Model != model || _lastPick.Effort != effort;
                _lastPick = shown;
                if (changed && (pick.ModelAuto || pick.EffortAuto) && model == pick.Model && effort == pick.Effort) Note(ChatAuto.Said(pick));
                ShowPickers();
                _engine = new ChatEngine(a => Dispatcher.BeginInvoke(a), _claude!, resume, model, coordinator: coordinator, effort: effort, chatKey: JobBoard.ChatKey(_conversation.Id));
                _engine.Event += OnEvent;
                _engine.Exited += OnExited;
                await _engine.StartAsync(line);
            }
            else await _engine.SendAsync(line);
        }
    }

    private void Stop()
    {
        if (StopBrief()) return;
        var engine = _engine;
        if (engine is null) { SetBusy(false); return; }
        _working.Text = "Stopping…";
        _stopping = true;
        _ = engine.InterruptAsync();
        // An interrupt that isn't answered in a few seconds ends the process; the next message resumes the chat.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_busy || _engine != engine) return;
            engine.Kill();
            _engine = null;
            Note("Stopped.");
            _stopping = false;
            SetBusy(false);
            _crew?.AutopilotTurnEnded(stopped: true, ok: false);
        };
        timer.Start();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _send.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _stop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _working.Text = busy ? "Working…" : "";
        ShowWorking(busy);
        ShowShrink();
    }

    private void OnEvent(ChatEvent e)
    {
        _lastActivity = DateTime.UtcNow;
        switch (e)
        {
            case InitEvent init:
                if (init.SessionId.Length > 0) _conversation.SessionId = init.SessionId;
                if (init.Servers.FirstOrDefault(s => s.Name == ClaudeCli.ServerName) is { Status: not "connected" } server)
                    Error($"The Plaxis tools didn't connect ({server.Status}). Close and open Plaxis ProMax again.");
                break;
            case TextStartEvent start:
                _streams[start.Key] = AddAnswer("");
                break;
            case TextDeltaEvent delta:
                if (!_streams.TryGetValue(delta.Key, out var view)) _streams[delta.Key] = view = AddAnswer("");
                view.Tag = (view.Tag as string ?? "") + delta.Text;
                view.SetMarkdown((string)view.Tag, fade: ChatUi.Lively);
                ScrollToEnd();
                break;
            case AssistantTextEvent text:
                AddAnswer(text.Text);
                _turnText.Append(text.Text).Append("\n\n");
                break;
            case ToolUseEvent use:
                Step(use.Id, use.Name, use.InputJson);
                break;
            case ToolResultEvent result:
                StepDone(result.ToolUseId, result.IsError, result.Text);
                break;
            case RetryEvent retry:
                _working.Text = SignInHealth.LooksBlocked(retry.Error) ? "The network refused Claude; trying again…" : $"Trying again ({retry.Attempt} of {retry.MaxRetries})…";
                break;
            case NoticeEvent notice:
                Note(notice.Text);
                break;
            case ContextEvent context:
                ContextNow(context.Tokens);
                break;
            case CompactedEvent compacted:
                Compacted(compacted);
                break;
            case UsageEvent usage:
                _crew?.OnUsage(usage.Usage);
                break;
            case ResultEvent result:
                EndTurn(result);
                break;
        }
    }

    private void EndTurn(ResultEvent result)
    {
        if (ShrinkEnded(result)) return;
        // Keep the streamed answer as one item in the saved chat.
        foreach (var view in _streams.Values)
            if (view.Tag is string { Length: > 0 } text)
            {
                _conversation.Items.Add(new ChatItem { Kind = "assistant", Text = text, Time = DateTime.UtcNow });
                _turnText.Append(text).Append("\n\n");
            }
        _streams.Clear();
        if (result.SessionId.Length > 0) _conversation.SessionId = result.SessionId;
        if (result.IsError && result.Subtype != "success")
        {
            var said = string.Join(" ", result.Errors.Prepend(result.Text)).Trim();
            // A blocked network is not an expired sign-in (Sketch ProMax bugs 71, 137).
            if (SignInHealth.LooksBlocked(said)) Error(SignInHealth.ChatNetBlocked(said));
            else if (SignInHealth.LooksExpired(said)) Error("Your Claude sign-in has expired. Sign in again in Settings > Claude, then send your message again.");
            else if (result.Subtype != "error_during_execution" || said.Length > 0) Error(said.Length > 0 ? said : "The answer stopped: " + result.Subtype);
        }
        var failed = result.IsError && result.Subtype != "success";
        var lost = failed ? string.Join(" ", result.Errors.Prepend(result.Text)).Trim() : "";
        var stopped = _stopping;
        _stopping = false;
        SetBusy(false);
        ChatUi.FlashIfAway(this);
        Save();
        ReplyToPhone(_turnText.ToString());
        _turnText.Clear();
        CatchQuestions();
        SendQueuedFromPhone();
        SendTypedMeanwhile();
        AfterTurn(result);
        DeliverAnswers(now: false);
        // A run in this chat: the plan waits for Approve, the run carries on, or its goal is reviewed.
        if (lost.Length > 0 && Autopilot.LostAccess(lost)) _crew?.AutopilotLostAccess(lost);
        else _crew?.AutopilotTurnEnded(stopped, !failed);
        ShrinkIfLong(stopped, failed);
    }

    private void OnExited(string errors)
    {
        _engine = null;
        if (!_busy) return;
        Error("The chat stopped" + (errors.Length > 0 ? ": " + errors.Split('\n').Last().Trim() : ".") + " Send your message again to carry on.");
        _stopping = false;
        SetBusy(false);
        Save();
        if (Autopilot.LostAccess(errors)) _crew?.AutopilotLostAccess(errors);
        else _crew?.AutopilotTurnEnded(stopped: false, ok: false);
    }

    private void Save()
    {
        try { ChatStore.Save(_conversation); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void NewChat()
    {
        if (_busy) Stop();
        _engine?.Dispose();
        _engine = null;
        Remember(_conversation);
        _contextTokens = 0;
        _conversation = ChatStore.New(_conversation.Key, _conversation.Title);
        _messages.Children.Clear();
        AttachCrew();
        Note(IsCoordinator ? $"New chat with a coordinator: bigger requests are split into jobs that {ChatJobs.Names.Coordinator} hands to the workers. Settings > Crew changes that."
            : "New simple chat: one chat does all the work itself. Settings > Crew changes that.");
    }

    private void Attach()
    {
        var pick = new OpenFileDialog
        {
            Multiselect = true, Title = "Attach files (borehole logs, reports, drawings, spreadsheets, pictures)",
            Filter = "All files (*.*)|*.*|Documents (PDF, Word, Excel, PowerPoint, text)|*.pdf;*.docx;*.xlsx;*.xlsm;*.pptx;*.csv;*.txt;*.ags|CAD drawings (DXF)|*.dxf|Pictures|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp",
        };
        if (pick.ShowDialog(this) != true) return;
        foreach (var file in pick.FileNames)
            if (!_attached.Contains(file)) _attached.Add(file);
        ShowChips();
    }

    // ---- What the chat shows ---------------------------------------------------------------------------------------

    private void ShowItem(ChatItem item)
    {
        switch (item.Kind)
        {
            case "user": AddBubble(item); break;
            case "assistant": AddAnswer(item.Text, save: false, item); break;
            case "tool": AddStepRow(item.Name, item.Text, item.State, item.Result); break;
            case "pjob": _messages.Children.Add(ParallelJobCardFor(item)); break;
            case "file": _messages.Children.Add(FileCard(item)); break;
            case "brief": _messages.Children.Add(BriefCard(item)); break;
            case "handoff": _messages.Children.Add(HandoffCard(item, Tools.Handoff.Find(item.Name))); break;
            case "error": AddNote(item.Text, Brand.K.DangerText); break;
            case "note" when item.Name == Crew.ApproveNote: _messages.Children.Add(ApproveCard(item)); break;
            default: AddNote(item.Text, Brand.K.Muted); break;
        }
    }

    private void AddItem(ChatItem item)
    {
        _conversation.Items.Add(item);
        ShowItem(item);
        ScrollToEnd();
    }

    private void Note(string text) => AddItem(new ChatItem { Kind = "note", Text = text, Time = DateTime.UtcNow });

    private void Error(string text) => AddItem(new ChatItem { Kind = "error", Text = text, Time = DateTime.UtcNow });

    private void Step(string id, string tool, string inputJson)
    {
        var name = StreamParser.ShortName(tool);
        var text = StepNames.Line(name, StepNames.Describe(name, inputJson));
        if (_steps.TryGetValue(id, out var known))
        {
            known.Line.Text = text;
            return;
        }
        var row = AddStepRow(name, text, "running", "");
        row.Input = inputJson;
        _steps[id] = row;
        _working.Text = StepNames.Running(name, text, !StepNames.ReadOnly(name));
        _strip.CoordinatorDoes(name);
    }

    private void StepDone(string id, bool failed, string result)
    {
        if (!_steps.TryGetValue(id, out var step)) return;
        _steps.Remove(id);
        SetStep(step, failed ? "failed" : "done", result, pop: true);
        _conversation.Items.Add(new ChatItem { Kind = "tool", Name = step.Tool, Text = step.Line.Text, State = failed ? "failed" : "done", Result = result.Length > 2000 ? result.Substring(0, 2000) : result, Time = DateTime.UtcNow });
        _working.Text = _busy ? "Working…" : "";
        // A file the step made (a report, a workbook, a drawing): its card, saved with the chat.
        if (!failed && FilesItem(step.Tool, result) is { } files)
        {
            files.Time = DateTime.UtcNow;
            _conversation.Items.Add(files);
            _messages.Children.Add(FileCard(files));
            ScrollToEnd();
        }
    }

    private static Border Card(UIElement content)
    {
        var card = ChatUi.Opaque(new Border { Child = content, CornerRadius = new CornerRadius(ChatUi.Radius), Padding = new Thickness(ChatUi.S4, ChatUi.S3, ChatUi.S4, ChatUi.S3), Margin = new Thickness(0, ChatUi.S4, 0, ChatUi.S1), BorderThickness = new Thickness(1), SnapsToDevicePixels = true });
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        return card;
    }

    private static FrameworkElement Labelled(string label, FrameworkElement field)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, ChatUi.S3, ChatUi.S1) };
        panel.Children.Add(ChatUi.Small(label));
        panel.Children.Add(field);
        return panel;
    }

    /// <summary>A small text that wraps instead of being cut with "…" (cards, notes, errors).</summary>
    internal static TextBlock Wrapped(TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        return text;
    }

    // ---- Approval cards --------------------------------------------------------------------------------------------

    /// <summary>
    /// An inline card in the chat. Called from the MCP server's thread; the card is drawn on the window's thread and the
    /// call waits for a button, or for Stop, without blocking anything.
    /// </summary>
    public Task<ApprovalAnswer> AskAsync(ApprovalRequest request, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(Brand.Text(request.Title, Brand.K.Text, 14, bold: true));
            var reason = Wrapped(ChatUi.Small(request.Reason, Brand.K.Text));
            reason.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(reason);
            if (request.Details.Length > 0)
            {
                var details = new Expander { Header = "Details", Margin = new Thickness(0, ChatUi.S2, 0, 0) };
                var code = new TextBox { Text = request.Details, IsReadOnly = true, FontFamily = ChatUi.Mono, FontSize = ChatUi.CodeSize, TextWrapping = TextWrapping.NoWrap,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 260 };
                details.Content = code;
                // Code is read before it is allowed: open by default for code.
                details.IsExpanded = request.Tool == Tools.RunPlaxisCodeTool.ToolName;
                panel.Children.Add(details);
            }
            var allow = Brand.Button("Allow", Brand.Weight.Primary);
            var task = Brand.Button("Allow the rest of this task");
            var never = Brand.Button("Don't ask again");
            var deny = Brand.Button("Don't allow");
            // A wrap panel: four buttons don't fit a narrow window in one row, and a row would cut off Allow.
            var buttons = new WrapPanel { Margin = new Thickness(0, ChatUi.S3, 0, 0) };
            foreach (var b in request.CanSilence ? new[] { allow, task, never, deny } : new[] { allow, task, deny })
            {
                b.Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S2);
                buttons.Children.Add(b);
            }
            panel.Children.Add(buttons);
            var card = Card(panel);
            card.SetResourceReference(Border.BorderBrushProperty, Brand.K.Accent);
            _messages.Children.Add(card);
            ScrollToEnd();
            ChatUi.FlashIfAway(this);
            long phoneId = 0;
            void Answer(ApprovalAnswer a, string said)
            {
                if (!answer.TrySetResult(a)) return;
                buttons.IsEnabled = false;
                panel.Children.Add(ChatUi.Small(said));
                SettleOnPhone(ref phoneId, said);
            }
            AskOnPhone(request, id => phoneId = id, (a, said) => Answer(a, said + " (on your phone)"));
            allow.Click += (_, _) => Answer(ApprovalAnswer.Allow, "Allowed.");
            task.Click += (_, _) => Answer(ApprovalAnswer.AllowRestOfTask, "Allowed for the rest of this task.");
            never.Click += (_, _) => Answer(ApprovalAnswer.DontAskAgain, "Allowed; it won't ask again for this (Settings can undo that).");
            deny.Click += (_, _) => Answer(ApprovalAnswer.Deny, "Not allowed.");
            ct.Register(() => Dispatcher.BeginInvoke(() => Answer(ApprovalAnswer.Deny, "Stopped before an answer.")));
        });
        return answer.Task;
    }

    // ---- Settings --------------------------------------------------------------------------------------------------

    private void ShowSettings()
    {
        var window = new SettingsWindow(_claude, this) { Owner = this };
        window.ShowDialog();
        // The crew's names, the jobs at once and the kind of the next new chat may have changed.
        ApplyChatDefaults();
        ShowJobs();
    }
}
