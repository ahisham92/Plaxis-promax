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
internal sealed class ChatWindow : Window, IApprovalAsker
{
    private readonly PlaxisLink _link;
    private readonly PlaxisInstall.StartArgs _start;
    private readonly Func<ChatMcp> _mcp;
    private readonly StackPanel _messages = new() { Margin = new Thickness(ChatUi.S4, ChatUi.S3, ChatUi.S4, ChatUi.S3) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly TextBlock _plaxisState = Brand.Text("Connecting to Plaxis…", Brand.K.HeaderSub, 12);
    private readonly TextBlock _working = ChatUi.Small("");
    private readonly List<string> _attached = [];
    private readonly WrapPanel _attachments = new();
    private readonly Dictionary<string, MarkdownView> _streams = [];
    private readonly Dictionary<string, (TextBlock Line, Border Row)> _steps = [];
    private ClaudeCli.Install? _claude;
    private ChatEngine? _engine;
    private Conversation _conversation;
    private bool _busy;
    private MarkdownView? _lastAnswer;

    public ChatWindow(PlaxisLink link, PlaxisInstall.StartArgs start, Func<ChatMcp> mcp)
    {
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

        // Header: the mark, the name, what Plaxis is open, and New chat / Settings.
        var newChat = Brand.Button("New chat");
        newChat.Click += (_, _) => NewChat();
        var settings = Brand.Button("Settings");
        settings.Click += (_, _) => ShowSettings();
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ChatUi.S2, 0, 0, 0) };
        title.Children.Add(Brand.Text("Plaxis ProMax", Brand.K.HeaderText, 15, bold: true));
        title.Children.Add(_plaxisState);
        var head = new DockPanel { Margin = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S2), LastChildFill = true };
        var buttons = Brand.ButtonRow(newChat, settings);
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        var logo = new Image { Source = Brand.Logo, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        head.Children.Add(title);
        var header = new Border { Child = head, Background = Brand.Brush(Brand.Navy) };

        _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // Composer: attachments, the message box, Attach and Send/Stop.
        _input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(ChatUi.S2), FontSize = ChatUi.BodySize,
        };
        _input.SetResourceReference(BackgroundProperty, Brand.K.ComposerField);
        _input.SetResourceReference(ForegroundProperty, Brand.K.Text);
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; SendOrStop(); }
        };
        System.Windows.Automation.AutomationProperties.SetName(_input, "Message");
        _send = Brand.Button("Send", Brand.Weight.Primary);
        _send.Click += (_, _) => SendOrStop();
        var attach = Brand.Button("Attach");
        attach.Click += (_, _) => Attach();
        var row = new DockPanel { Margin = new Thickness(0, ChatUi.S2, 0, 0) };
        var sendRow = Brand.ButtonRow(attach, _send);
        DockPanel.SetDock(sendRow, Dock.Right);
        row.Children.Add(sendRow);
        row.Children.Add(_working);
        _working.VerticalAlignment = VerticalAlignment.Center;
        var composer = new StackPanel { Margin = new Thickness(ChatUi.S3) };
        composer.Children.Add(_attachments);
        composer.Children.Add(_input);
        composer.Children.Add(row);
        var foot = new Border { Child = composer, BorderThickness = new Thickness(0, 1, 0, 0) };
        foot.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(foot);
        root.Children.Add(_scroll);
        Content = root;

        _link.ConnectionChanged += connected => Dispatcher.BeginInvoke(() => ShowPlaxis(connected));
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _engine?.KillNow();
    }

    // ---- Start: Plaxis, then Claude Code ---------------------------------------------------------------------------

    private async Task StartAsync()
    {
        _input.Focus();
        // The owner's later changes (cancelled keys, earlier end dates), then the License window if this PC is locked.
        _ = Task.Run(() => LicenseState.RefreshChangesAsync(CancellationToken.None));
        LicenseWindow.BlockIfLocked();
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
        foreach (var item in _conversation.Items.TakeLast(80)) ShowItem(item);
        if (_conversation.Items.Count == 0)
            Note($"Connected to {s?["product"]} {s?["version"]}. Ask for what you need: build a model from a borehole log, set up phases, calculate, or read results.");
        ScrollToEnd();
    }

    private void ShowConnectCard(int port, string error)
    {
        var portBox = new TextBox { Text = port.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 90 };
        var password = new PasswordBox { Width = 200 };
        var remember = new CheckBox { Content = "Remember the password on this PC", IsChecked = true, Margin = new Thickness(0, ChatUi.S1, 0, 0) };
        var connect = Brand.Button("Connect", Brand.Weight.Primary);
        var panel = new StackPanel();
        panel.Children.Add(Brand.Text("Connect to Plaxis", Brand.K.Text, 14, bold: true));
        panel.Children.Add(ChatUi.Small(error.Length > 0 ? error : "In Plaxis Input, open Expert > Configure remote scripting server, start the server, and enter its port and password here. Started from Plaxis's Expert menu, this happens by itself."));
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
            var signIn = Brand.Button("Sign in", Brand.Weight.Primary);
            var again = Brand.Button("Check again");
            var panel = Setup("Sign in to Claude", "Sign in once with your Claude account; the browser opens for it.", signIn, again);
            signIn.Click += (_, _) => ClaudeCli.StartSignIn(_claude);
            again.Click += async (_, _) => { _messages.Children.Remove(panel); await FindClaudeAsync(); };
        }
    }

    private Border Setup(string title, string text, params Button[] buttons)
    {
        var panel = new StackPanel();
        panel.Children.Add(Brand.Text(title, Brand.K.Text, 14, bold: true));
        panel.Children.Add(ChatUi.Small(text));
        panel.Children.Add(Brand.ButtonRow(buttons));
        var card = Card(panel);
        _messages.Children.Add(card);
        return card;
    }

    // ---- Sending ---------------------------------------------------------------------------------------------------

    private void SendOrStop()
    {
        if (_busy) { Stop(); return; }
        var text = _input.Text.Trim();
        if (text.Length == 0 && _attached.Count == 0) return;
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
        if (_attached.Count > 0)
            text += "\n\nAttached files:\n" + string.Join("\n", _attached.Select(f => "- " + f));
        _input.Clear();
        _attached.Clear();
        _attachments.Children.Clear();
        AddItem(new ChatItem { Kind = "user", Text = text, Time = DateTime.UtcNow });
        if (_conversation.Items.Count(i => i.Kind == "user") == 1) _conversation.Name = ChatHistory.AutoTitle(text);
        _mcp().NewTask();
        SetBusy(true);
        _ = SendAsync(text);
    }

    private async Task SendAsync(string text)
    {
        try
        {
            var line = StreamInput.UserMessage(text, []);
            if (_engine is null || !_engine.IsAlive)
            {
                _engine?.Dispose();
                var resume = _conversation.SessionId.Length > 0 ? _conversation.SessionId : null;
                var settings = AgentSettings.Load();
                _engine = new ChatEngine(a => Dispatcher.BeginInvoke(a), _claude!, resume, settings.ChatModel, effort: settings.ChatEffort);
                _engine.Event += OnEvent;
                _engine.Exited += OnExited;
                await _engine.StartAsync(line);
            }
            else await _engine.SendAsync(line);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Error("The chat couldn't start: " + ex.Message);
            SetBusy(false);
        }
    }

    private void Stop()
    {
        var engine = _engine;
        if (engine is null) { SetBusy(false); return; }
        _working.Text = "Stopping…";
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
            SetBusy(false);
        };
        timer.Start();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _send.Content = busy ? "Stop" : "Send";
        _working.Text = busy ? "Working…" : "";
    }

    private void OnEvent(ChatEvent e)
    {
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
            case ResultEvent result:
                EndTurn(result);
                break;
        }
    }

    private void EndTurn(ResultEvent result)
    {
        // Keep the streamed answer as one item in the saved chat.
        foreach (var view in _streams.Values)
            if (view.Tag is string { Length: > 0 } text)
                _conversation.Items.Add(new ChatItem { Kind = "assistant", Text = text, Time = DateTime.UtcNow });
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
        SetBusy(false);
        ChatUi.FlashIfAway(this);
        Save();
    }

    private void OnExited(string errors)
    {
        _engine = null;
        if (!_busy) return;
        Error("The chat stopped" + (errors.Length > 0 ? ": " + errors.Split('\n').Last().Trim() : ".") + " Send your message again to carry on.");
        SetBusy(false);
        Save();
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
        _conversation = ChatStore.New(_conversation.Key, _conversation.Title);
        _messages.Children.Clear();
        Note("New chat.");
    }

    private void Attach()
    {
        var pick = new OpenFileDialog { Multiselect = true, Title = "Attach files (borehole logs, reports, pictures)" };
        if (pick.ShowDialog(this) != true) return;
        foreach (var file in pick.FileNames)
        {
            if (_attached.Contains(file)) continue;
            _attached.Add(file);
            var chip = ChatUi.Small(Path.GetFileName(file), Brand.K.Text);
            chip.Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S1);
            _attachments.Children.Add(chip);
        }
    }

    // ---- What the chat shows ---------------------------------------------------------------------------------------

    private void ShowItem(ChatItem item)
    {
        switch (item.Kind)
        {
            case "user": AddBubble(item.Text); break;
            case "assistant": AddAnswer(item.Text, save: false); break;
            case "tool": AddStepRow(item.Name, item.Text, item.State); break;
            case "error": AddNote(item.Text, Brand.K.DangerText); break;
            default: AddNote(item.Text, Brand.K.Muted); break;
        }
    }

    private void AddItem(ChatItem item)
    {
        _conversation.Items.Add(item);
        ShowItem(item);
        ScrollToEnd();
    }

    private void AddBubble(string text)
    {
        var box = ChatUi.Selectable(text, "Your message");
        var bubble = new Border { Child = box, CornerRadius = new CornerRadius(ChatUi.Radius), Padding = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S2),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(48, ChatUi.S3, 0, ChatUi.S1), MaxWidth = 560 };
        bubble.SetResourceReference(Border.BackgroundProperty, Brand.K.AccentSoft);
        _messages.Children.Add(bubble);
        ChatUi.Arrive(bubble);
    }

    private MarkdownView AddAnswer(string text, bool save = true)
    {
        var view = new MarkdownView { Margin = new Thickness(0, ChatUi.S2, 0, ChatUi.S1), Tag = text };
        view.SetMarkdown(text);
        _messages.Children.Add(view);
        _lastAnswer = view;
        if (save && text.Length > 0) _conversation.Items.Add(new ChatItem { Kind = "assistant", Text = text, Time = DateTime.UtcNow });
        ScrollToEnd();
        return view;
    }

    private void Step(string id, string tool, string inputJson)
    {
        var name = StreamParser.ShortName(tool);
        var text = StepNames.Line(name, StepNames.Describe(name, inputJson));
        if (_steps.TryGetValue(id, out var known))
        {
            known.Line.Text = text;
            return;
        }
        var (line, row) = AddStepRow(name, text, "running");
        _steps[id] = (line, row);
        _working.Text = StepNames.Running(name, text, !StepNames.ReadOnly(name));
    }

    private void StepDone(string id, bool failed, string result)
    {
        if (!_steps.TryGetValue(id, out var step)) return;
        _steps.Remove(id);
        if (step.Row.Child is DockPanel panel && panel.Children[0] is FrameworkElement mark)
        {
            panel.Children.RemoveAt(0);
            var done = failed ? ChatUi.Glyph("", Brand.K.DangerText) : ChatUi.StepMark(true);
            DockPanel.SetDock(done, Dock.Left);
            panel.Children.Insert(0, done);
        }
        step.Row.ToolTip = result.Length > 600 ? result.Substring(0, 600) + "…" : result;
        _conversation.Items.Add(new ChatItem { Kind = "tool", Name = step.Line.Tag as string ?? "", Text = step.Line.Text, State = failed ? "failed" : "done", Result = result.Length > 2000 ? result.Substring(0, 2000) : result, Time = DateTime.UtcNow });
        _working.Text = _busy ? "Working…" : "";
    }

    private (TextBlock Line, Border Row) AddStepRow(string tool, string text, string state)
    {
        var line = ChatUi.Small(text);
        line.Tag = tool;
        line.TextTrimming = TextTrimming.CharacterEllipsis;
        FrameworkElement mark = state switch
        {
            "running" => ChatUi.StepRing(),
            "failed" => ChatUi.Glyph("", Brand.K.DangerText),
            _ => ChatUi.StepMark(true),
        };
        mark.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        var panel = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left);
        panel.Children.Add(mark);
        panel.Children.Add(line);
        var row = new Border { Child = panel, Margin = new Thickness(0, 2, 0, 2) };
        _messages.Children.Add(row);
        ScrollToEnd();
        return (line, row);
    }

    private void Note(string text) => AddItem(new ChatItem { Kind = "note", Text = text, Time = DateTime.UtcNow });

    private void Error(string text) => AddItem(new ChatItem { Kind = "error", Text = text, Time = DateTime.UtcNow });

    private void AddNote(string text, string key)
    {
        var note = ChatUi.Small(text, key);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, ChatUi.S2, 0, ChatUi.S2);
        _messages.Children.Add(note);
    }

    private static Border Card(UIElement content)
    {
        var card = new Border { Child = content, CornerRadius = new CornerRadius(ChatUi.Radius), Padding = new Thickness(ChatUi.S3), Margin = new Thickness(0, ChatUi.S2, 0, ChatUi.S2), BorderThickness = new Thickness(1) };
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

    private void ScrollToEnd() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _scroll.ScrollToEnd());

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
            var reason = ChatUi.Small(request.Reason, Brand.K.Text);
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
            var buttons = request.CanSilence ? Brand.ButtonRow(allow, task, never, deny) : Brand.ButtonRow(allow, task, deny);
            panel.Children.Add(buttons);
            var card = Card(panel);
            card.SetResourceReference(Border.BorderBrushProperty, Brand.K.Accent);
            _messages.Children.Add(card);
            ScrollToEnd();
            ChatUi.FlashIfAway(this);
            void Answer(ApprovalAnswer a, string said)
            {
                if (!answer.TrySetResult(a)) return;
                buttons.IsEnabled = false;
                panel.Children.Add(ChatUi.Small(said));
            }
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
        var window = new SettingsWindow(_claude) { Owner = this };
        window.ShowDialog();
    }
}
