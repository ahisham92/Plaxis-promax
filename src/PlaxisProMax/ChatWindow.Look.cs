using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PlaxisProMax.Chat;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// How the chat looks: Sketch ProMax's pane as it is now, not as it started. A quiet header of icon buttons with the
/// chat's name and the Plaxis project; a greeting with ideas in an empty chat; your messages as bubbles and the answers
/// on the page under the mark; each step a one-line card that opens to its input and result; a breathing mark while it
/// works; Jump to latest; and the message box as one raised card with a hint, attachments as chips, and a round Send
/// that turns into Stop.
/// </summary>
internal sealed partial class ChatWindow
{
    /// <summary>One step's row: its mark (ring, check or cross), its line, and the details it opens to.</summary>
    private sealed class StepRow
    {
        public string Tool = "", Input = "", Result = "", State = "";
        public readonly ContentControl Mark = new() { Width = 18, Height = 18, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        public TextBlock Line = null!, Chevron = null!, Detail = null!;
        public ToggleButton Header = null!;
        public StackPanel Details = null!;
    }

    private readonly TextBlock _chatName = ChatUi.Small("", Brand.K.Text, bold: true);
    private readonly Border _field = new();
    private readonly TextBlock _hint = Brand.Text("Ask anything about your Plaxis model, or tell me what to do…", Brand.K.Muted, ChatUi.BodySize);
    private readonly Border _workingBox = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, ChatUi.S4, 0, 0) };
    private readonly StackPanel _home = new() { Margin = new Thickness(0, ChatUi.S8, 0, 0) };
    private readonly Button _jump = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _typeLabel = ChatUi.Small("");
    private readonly TextBlock _usageLabel = ChatUi.Small("");
    private Button? _usagePill;
    private bool _follow = true, _homeShown = true;
    private UIElement? _lastDrawn;

    // ---- Header ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The history menu, this chat's name with the Plaxis project as a muted caption, and on the right More (notes,
    /// routines, saved prompts, plan usage), New chat and Settings.
    /// </summary>
    private FrameworkElement Header()
    {
        TextElement.SetFontFamily(this, ChatUi.Text);
        TextElement.SetFontSize(this, ChatUi.BodySize);
        SetResourceReference(TextElement.ForegroundProperty, Brand.K.Text);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var top = new DockPanel { Margin = new Thickness(ChatUi.S2, ChatUi.S2, ChatUi.S2, ChatUi.S2) };
        var history = ChatUi.IconButton(ChatUi.Paths.Menu, "Chat history", "Your chats: search, open, rename, pin, export or delete them.");
        history.Margin = new Thickness(0, 0, ChatUi.S1, 0);
        history.Click += (_, _) => { if (_drawerOpen) CloseDrawer(); else OpenDrawer(); };
        DockPanel.SetDock(history, Dock.Left);
        top.Children.Add(history);

        var settings = ChatUi.Ghost("", null, "Settings", "Settings: when to ask, your rules, Plaxis, the crew, Claude, the license, updates and bug reports.");
        settings.Click += (_, _) => ShowSettings();
        DockPanel.SetDock(settings, Dock.Right);
        top.Children.Add(settings);
        var newChat = ChatUi.IconButton(ChatUi.Paths.Plus, "New chat", "A new chat, or a new autopilot chat that works on its own to a goal. This one stays in the history.");
        newChat.Margin = new Thickness(ChatUi.S1, 0, 0, 0);
        newChat.Click += (_, _) => NewChatMenu(newChat);
        DockPanel.SetDock(newChat, Dock.Right);
        top.Children.Add(newChat);
        var more = ChatUi.Ghost(ChatUi.GlyphMore, null, "More", "Notes, routines, saved prompts and your plan's usage.");
        more.Click += (_, _) => ShowMenu(more);
        DockPanel.SetDock(more, Dock.Right);
        top.Children.Add(more);

        var mark = ChatUi.Mark(24);
        AutomationProperties.SetName(mark, "Plaxis ProMax");
        mark.Margin = new Thickness(ChatUi.S1, 0, ChatUi.S2, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(mark, Dock.Left);
        top.Children.Add(mark);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _chatName.FontSize = ChatUi.BodySize;
        _chatName.TextTrimming = TextTrimming.CharacterEllipsis;
        _chatName.Text = "Plaxis ProMax";
        names.Children.Add(_chatName);
        Brand.Fg(_plaxisState, Brand.K.Muted);
        _plaxisState.FontSize = ChatUi.SmallSize;
        _plaxisState.TextWrapping = TextWrapping.NoWrap;
        _plaxisState.TextTrimming = TextTrimming.CharacterEllipsis;
        _plaxisState.ToolTip = "The Plaxis and the project this chat works on";
        names.Children.Add(_plaxisState);
        top.Children.Add(names);

        var header = new Border { Child = top, BorderThickness = new Thickness(0, 0, 0, 1) };
        header.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        return header;
    }

    /// <summary>The header's first line is the chat's name; the pills under the message box say its type and the plan's usage.</summary>
    private void ShowTitle()
    {
        var name = _conversation.Name.Trim();
        _chatName.Text = name.Length > 0 && (_sample || _conversation.Items.Count > 0) ? name : "Plaxis ProMax";
        _chatName.ToolTip = _chatName.Text;
        _typeLabel.Text = ChatTypes.Label(ChatTypes.Of(_conversation));
        ShowUsage();
    }

    private void ShowUsage()
    {
        if (_usagePill is null) return;
        if (_sample)
        {
            _usageLabel.Text = "5-hour 42% · week 17%";
            return;
        }
        var usage = PlanUsage.Load(Crew.RoutinesFolder);
        var now = DateTimeOffset.UtcNow;
        var parts = usage?.Windows.Where(w => PlanUsage.Current(w, now) is not null).Select(w => $"{PlanUsage.Short(w.Key)} {PlanUsage.Percent(w.Used ?? 0)}").ToList() ?? [];
        _usageLabel.Text = string.Join(" · ", parts);
        _usagePill.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _usagePill.ToolTip = string.Join("\n", UsageLines());
    }

    // ---- Conversation ------------------------------------------------------------------------------------------------

    /// <summary>The greeting (an empty chat), the messages, and what Plaxis ProMax is doing now.</summary>
    private ScrollViewer Conversation()
    {
        BuildHome();
        var dots = ChatUi.Thinking();
        var working = new DockPanel();
        DockPanel.SetDock(dots, Dock.Left);
        working.Children.Add(dots);
        _working.VerticalAlignment = VerticalAlignment.Center;
        _working.TextTrimming = TextTrimming.CharacterEllipsis;
        working.Children.Add(_working);
        _workingBox.Child = working;
        AutomationProperties.SetLiveSetting(_workingBox, AutomationLiveSetting.Polite);

        _messages.Margin = new Thickness(0);
        var conversation = new StackPanel { Margin = new Thickness(ChatUi.S4, 0, ChatUi.S4, ChatUi.S8) };
        conversation.Children.Add(_home);
        conversation.Children.Add(_messages);
        conversation.Children.Add(_workingBox);
        // The greeting is there while the chat is empty, whoever empties or fills it.
        _messages.LayoutUpdated += (_, _) =>
        {
            var empty = _messages.Children.Count == 0;
            if (empty == _homeShown) return;
            _homeShown = empty;
            _home.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        };
        var scroll = new ScrollViewer
        {
            Content = conversation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true, IsTabStop = true,
        };
        scroll.SetResourceReference(FocusVisualStyleProperty, "SpmChatFocus");
        AutomationProperties.SetName(scroll, "Conversation");
        // Reading back while the answer is written: the list stops following until Jump to latest (or the end is reached).
        scroll.ScrollChanged += (_, e) =>
        {
            var atEnd = scroll.ScrollableHeight - scroll.VerticalOffset < 48;
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) _follow = atEnd;
            else if (_follow) scroll.ScrollToEnd();
            _jump.Visibility = !_follow && !atEnd ? Visibility.Visible : Visibility.Collapsed;
        };
        scroll.PreviewMouseWheel += (_, e) => { if (e.Delta > 0 && scroll.VerticalOffset > 0) _follow = false; };

        var jumpRow = new StackPanel { Orientation = Orientation.Horizontal };
        var arrow = ChatUi.Icon(ChatUi.Paths.ArrowDown);
        arrow.Margin = new Thickness(0, 0, ChatUi.S1, 0);
        jumpRow.Children.Add(arrow);
        jumpRow.Children.Add(ChatUi.Small("Jump to latest", Brand.K.Text));
        _jump.Content = jumpRow;
        _jump.SetResourceReference(StyleProperty, "SpmChatFloat");
        _jump.HorizontalAlignment = HorizontalAlignment.Center;
        _jump.VerticalAlignment = VerticalAlignment.Bottom;
        _jump.Margin = new Thickness(0, 0, 0, ChatUi.S3);
        _jump.ToolTip = "Scrolls to the newest message and follows the answer again.";
        _jump.Click += (_, _) => { _follow = true; scroll.ScrollToEnd(); };
        return scroll;
    }

    /// <summary>An empty chat: the mark, a greeting, and a few things to ask, each one click into the message box.</summary>
    private void BuildHome()
    {
        var mark = ChatUi.Mark(44);
        AutomationProperties.SetName(mark, "Plaxis ProMax");
        mark.HorizontalAlignment = HorizontalAlignment.Left;
        _home.Children.Add(mark);
        var hello = Brand.Text(_sample ? "Hey Ahmed!" : Greeting.Line(Greeting.Pick(Environment.UserName)), Brand.K.Text, 24, bold: true);
        hello.FontFamily = ChatUi.Display;
        hello.Margin = new Thickness(0, ChatUi.S4, 0, 0);
        TextOptions.SetTextFormattingMode(hello, TextFormattingMode.Ideal);
        _home.Children.Add(hello);
        var sub = Brand.Text("What shall we do in Plaxis? Ask in your own words, attach a borehole log or a report, or start from one of these.", Brand.K.Muted, ChatUi.BodySize);
        sub.Margin = new Thickness(0, ChatUi.S1, 0, ChatUi.S4);
        _home.Children.Add(sub);
        (string Title, string Line, string Prompt)[] ideas =
        [
            ("Model from a borehole log", "Soil layers, materials and boreholes from the log you attach.", "Build the soil model from the attached borehole log: one borehole per log, the layers with their levels, and a material for each layer. List what you read first."),
            ("Set up the construction phases", "Phases in order, with what is active in each.", "Set up the construction phases for this model in order, and tell me what is active in each before you calculate."),
            ("Calculate and say how it went", "Every phase, one at a time, saved as it goes.", "Calculate every phase one at a time, saving after each, and tell me how each one ended."),
            ("Results into Excel", "Deflection, moments and anchor forces per phase.", "Put the wall deflection, bending moments and anchor forces of every calculated phase into one Excel file beside the project."),
        ];
        foreach (var idea in ideas)
        {
            var text = new StackPanel();
            text.Children.Add(ChatUi.Small(idea.Title, Brand.K.Text, bold: true));
            var line = ChatUi.Small(idea.Line);
            line.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(line);
            var card = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, ChatUi.S2), Padding = new Thickness(ChatUi.S4, ChatUi.S3, ChatUi.S4, ChatUi.S3), ToolTip = idea.Prompt };
            card.SetResourceReference(StyleProperty, "SpmChatCard");
            AutomationProperties.SetName(card, idea.Title + ". " + idea.Line);
            var prompt = idea.Prompt;
            card.Click += (_, _) =>
            {
                _input.Text = prompt;
                _input.CaretIndex = prompt.Length;
                _input.Focus();
            };
            _home.Children.Add(card);
        }
    }

    private void ShowWorking(bool busy)
    {
        _workingBox.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) ScrollToEnd();
        else ShowUsage();
    }

    private void ScrollToEnd()
    {
        if (_follow) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _scroll.ScrollToEnd());
    }

    // ---- The message box ---------------------------------------------------------------------------------------------

    private TextBox InputBox()
    {
        var input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = ChatStyles.ComposerMinHeight, MaxHeight = ChatStyles.ComposerMaxHeight,
            Padding = new Thickness(ChatStyles.ComposerPadX, ChatStyles.ComposerPadY, ChatStyles.ComposerPadX, ChatStyles.ComposerPadY),
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, SpellCheck = { IsEnabled = true }, FontSize = ChatUi.BodySize,
        };
        input.SetResourceReference(StyleProperty, "SpmChatBare");
        AutomationProperties.SetName(input, "Message to Plaxis ProMax");
        AutomationProperties.SetHelpText(input, "Type your question or request here. Enter sends, Shift+Enter starts a new line, Escape stops the answer.");
        input.ToolTip = "Type here. Enter sends. Shift+Enter: new line.";
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; if (!_busy) SendOrStop(); }
            else if (e.Key == Key.Escape && _busy) { e.Handled = true; SendOrStop(); }
        };
        return input;
    }

    /// <summary>
    /// One raised card: the attachments as chips, the message box with its hint, then Attach and Saved prompts on the
    /// left and the round Send (Stop while it works) on the right. Under it, the chat's type, the phone and the plan's usage.
    /// </summary>
    private FrameworkElement Composer()
    {
        _hint.IsHitTestVisible = false;
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.VerticalAlignment = VerticalAlignment.Top;
        _hint.Margin = new Thickness(ChatStyles.ComposerPadX + 2, ChatStyles.ComposerPadY, ChatStyles.ComposerPadX + 2, 0);
        _input.TextChanged += (_, _) => _hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var field = new Grid();
        field.Children.Add(_input);
        field.Children.Add(_hint);
        _field.Child = field;
        _field.CornerRadius = new CornerRadius(ChatUi.RadiusSmall);
        _field.SnapsToDevicePixels = true;
        _field.Margin = new Thickness(ChatUi.S2, ChatUi.S2, ChatUi.S2, ChatUi.S1);
        _field.Cursor = Cursors.IBeam;
        _field.SetResourceReference(Border.BackgroundProperty, Brand.K.ComposerField);
        ShowFieldState();
        _field.MouseEnter += (_, _) => ShowFieldState();
        _field.MouseLeave += (_, _) => ShowFieldState();
        _input.IsKeyboardFocusedChanged += (_, _) => ShowFieldState();

        var buttons = new DockPanel { Margin = new Thickness(ChatUi.S1, 0, ChatUi.S2, ChatUi.S2) };
        var attach = ChatUi.IconButton(ChatUi.Paths.Plus, "Attach files", "Attach borehole logs, reports, drawings or pictures.");
        attach.Click += (_, _) => Attach();
        var prompts = ChatUi.IconButton(ChatUi.Paths.Bookmark, "Saved prompts", "Your saved prompts: pick one to put it in the message box, or save what is in the box.");
        prompts.Click += (_, _) => TogglePrompts();
        attach.Margin = prompts.Margin = new Thickness(0, 0, 2, 0);
        _send.SetResourceReference(StyleProperty, "SpmChatSend");
        AutomationProperties.SetName(_send, "Send message");
        _send.Click += (_, _) => SendOrStop();
        _stop.SetResourceReference(StyleProperty, "SpmChatStop");
        AutomationProperties.SetName(_stop, "Stop the answer");
        _stop.Click += (_, _) => SendOrStop();
        DockPanel.SetDock(_send, Dock.Right);
        DockPanel.SetDock(_stop, Dock.Right);
        DockPanel.SetDock(attach, Dock.Left);
        DockPanel.SetDock(prompts, Dock.Left);
        buttons.Children.Add(_send);
        buttons.Children.Add(_stop);
        buttons.Children.Add(attach);
        buttons.Children.Add(prompts);
        buttons.Children.Add(new Border()); // fills the middle

        var inside = new StackPanel();
        _attachments.Margin = new Thickness(ChatUi.S3, ChatUi.S3, ChatUi.S3, 0);
        _attachments.Visibility = Visibility.Collapsed;
        inside.Children.Add(_attachments);
        inside.Children.Add(_field);
        inside.Children.Add(buttons);
        var card = ChatUi.Elevated(inside, out var frame);
        // A click on the card's empty parts puts the caret in the box, as in a text field.
        frame.AddHandler(MouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.Handled || e.ChangedButton != MouseButton.Left || _input.IsKeyboardFocused) return;
            _input.Focus();
            _input.CaretIndex = _input.Text.Length;
            e.Handled = true;
        }));

        // Under the card: the chat's type and the phone on the left, the plan's usage on the right.
        var under = new DockPanel { Margin = new Thickness(ChatUi.S2, ChatUi.S1, 0, 0) };
        _usagePill = new Button { Content = _usageLabel, Padding = new Thickness(ChatUi.S2, ChatUi.S1, ChatUi.S2, ChatUi.S1), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _usagePill.SetResourceReference(StyleProperty, "SpmChatGhost");
        AutomationProperties.SetName(_usagePill, "Your plan's usage");
        _usagePill.Click += (_, _) => ShowMenu(_usagePill);
        DockPanel.SetDock(_usagePill, Dock.Right);
        under.Children.Add(_usagePill);
        var type = new Button { Content = _typeLabel, Padding = new Thickness(ChatUi.S2, ChatUi.S1, ChatUi.S2, ChatUi.S1), Margin = new Thickness(0, 0, ChatUi.S1, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "This chat's type. With coordinator: bigger requests are split into jobs for the workers. Simple chat: one chat does all the work. Click for a new chat of either type." };
        type.SetResourceReference(StyleProperty, "SpmChatGhost");
        AutomationProperties.SetName(type, "Chat type");
        type.Click += (_, _) => NewChatMenu(type);
        var pickers = new WrapPanel();
        pickers.Children.Add(type);
        pickers.Children.Add(PhonePill());
        under.Children.Add(pickers);

        var composer = new StackPanel { Margin = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S3) };
        composer.Children.Add(_promptPanel);
        composer.Children.Add(card);
        composer.Children.Add(under);
        ShowTitle();
        return composer;
    }

    /// <summary>The box's border: a field at rest, darker on hover, a 2 px ring while the caret is in it (the text doesn't move).</summary>
    private void ShowFieldState()
    {
        var (brush, thickness) = ChatStyles.ComposerBorder(_input.IsKeyboardFocused, _field.IsMouseOver);
        _field.SetResourceReference(Border.BorderBrushProperty, brush);
        _field.BorderThickness = new Thickness(thickness);
        _field.Padding = new Thickness(2 - thickness);
    }

    /// <summary>The attached files as chips in the card, each with a cross that takes it off.</summary>
    private void ShowChips()
    {
        _attachments.Children.Clear();
        foreach (var file in _attached.ToList())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = ChatUi.Glyph(ChatUi.GlyphFile, Brand.K.Muted);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(glyph);
            var name = ChatUi.Small(" " + System.IO.Path.GetFileName(file), Brand.K.Text);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.MaxWidth = 220;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(name);
            var remove = ChatUi.Ghost(ChatUi.GlyphCross, null, "Remove " + System.IO.Path.GetFileName(file), "Take this file off the message");
            remove.Click += (_, _) =>
            {
                _attached.Remove(file);
                ShowChips();
            };
            row.Children.Add(remove);
            var chip = new Border { Child = row, CornerRadius = new CornerRadius(ChatUi.RadiusSmall), Padding = new Thickness(ChatUi.S2, 0, 0, 0), Margin = new Thickness(0, 0, ChatUi.S1, ChatUi.S1), BorderThickness = new Thickness(1), ToolTip = file };
            chip.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
            chip.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
            _attachments.Children.Add(chip);
        }
        _attachments.Visibility = _attached.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Messages ----------------------------------------------------------------------------------------------------

    private static string When(DateTime time)
    {
        var stamp = StreamRender.Stamp(time == default ? default : time.ToLocalTime(), DateTime.Now);
        return stamp.Length > 0 ? " · " + stamp : "";
    }

    /// <summary>True when the next answer or step starts a new block of Plaxis ProMax's (so it gets the mark and the name).</summary>
    private bool NeedsHeader => _messages.Children.Count == 0 || !ReferenceEquals(_messages.Children[_messages.Children.Count - 1], _lastDrawn);

    private void Drawn(UIElement element, bool ours)
    {
        _messages.Children.Add(element);
        _lastDrawn = ours ? element : null;
    }

    private UIElement ClaudeHeader(DateTime time)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, ChatUi.S2) };
        var mark = ChatUi.Mark(20);
        mark.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(mark, "");
        row.Children.Add(mark);
        row.Children.Add(ChatUi.Small(IsCoordinator ? ChatJobs.Names.Coordinator + " · Plaxis ProMax" : "Plaxis ProMax", Brand.K.Text, bold: true));
        row.Children.Add(ChatUi.Small(When(time)));
        return row;
    }

    /// <summary>Your message: "You" and the time over a rounded bubble in the accent tint, tail corner bottom right, with Copy.</summary>
    private void AddBubble(ChatItem item)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(ChatUi.S8 + ChatUi.S2, ChatUi.S6, 0, ChatUi.S1) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, ChatUi.S1) };
        var copy = ChatUi.CopyButton(() => item.Text, "your message", null);
        head.Children.Add(copy);
        var save = ChatUi.Ghost(ChatUi.GlyphPrompts, null, "Save this message as a prompt", "Keeps this text in your saved prompts, to use again from the bookmark button by the message box.");
        save.Click += (_, _) =>
        {
            try { SavedPrompts.Update(SavedPrompts.DefaultFile, l => SavedPrompts.Add(l, item.Text, DateTime.UtcNow)); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return; }
            if (_promptPanel.Visibility == Visibility.Visible) ShowPrompts(SavedPrompts.Load(SavedPrompts.DefaultFile));
            save.Content = ChatUi.Glyph(ChatUi.GlyphCheck, Brand.K.Muted);
        };
        head.Children.Add(save);
        copy.Opacity = save.Opacity = 0;
        var who = ChatUi.Small("You" + When(item.Time));
        who.Margin = new Thickness(ChatUi.S1, 0, 0, 0);
        who.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(who);
        panel.Children.Add(head);
        var bubble = ChatUi.Opaque(new Border
        {
            Child = ChatUi.Selectable(item.Text, "Your message"), Padding = new Thickness(ChatUi.S4, ChatUi.S2 + 2, ChatUi.S4, ChatUi.S2 + 2),
            CornerRadius = new CornerRadius(ChatUi.Radius, ChatUi.Radius, ChatUi.S1, ChatUi.Radius), HorizontalAlignment = HorizontalAlignment.Right, SnapsToDevicePixels = true,
        });
        bubble.SetResourceReference(Border.BackgroundProperty, Brand.K.AccentSoft);
        panel.Children.Add(bubble);
        // Copy and Save prompt show on hover or with the keyboard, so they take no attention at rest.
        void Tools() => copy.Opacity = save.Opacity = panel.IsMouseOver || head.IsKeyboardFocusWithin ? 1 : 0;
        panel.MouseEnter += (_, _) => Tools();
        panel.MouseLeave += (_, _) => Tools();
        head.IsKeyboardFocusWithinChanged += (_, _) => Tools();
        AutomationProperties.SetName(panel, "You" + When(item.Time) + ": " + item.Text);
        Drawn(panel, ours: false);
        ChatUi.Arrive(panel);
    }

    /// <summary>An answer: on the page under the mark and the name (once per block), with Copy over its top right corner on hover.</summary>
    private MarkdownView AddAnswer(string text, bool save = true, ChatItem? item = null)
    {
        var header = NeedsHeader;
        var panel = new StackPanel { Margin = new Thickness(0, header ? ChatUi.S6 : ChatUi.S2, 0, 0) };
        if (header) panel.Children.Add(ClaudeHeader(item?.Time ?? DateTime.UtcNow));
        var view = new MarkdownView { Tag = text };
        view.SetMarkdown(text);
        var grid = new Grid();
        grid.Children.Add(view);
        var copy = ChatUi.CopyButton(() => view.Tag as string ?? "", "the answer", null);
        copy.HorizontalAlignment = HorizontalAlignment.Right;
        copy.VerticalAlignment = VerticalAlignment.Top;
        copy.Margin = new Thickness(0, -ChatUi.S1, 0, 0);
        copy.SetResourceReference(BackgroundProperty, Brand.K.Bg);
        copy.Opacity = 0;
        void Shown() => copy.Opacity = grid.IsMouseOver || copy.IsKeyboardFocused ? 1 : 0;
        grid.MouseEnter += (_, _) => Shown();
        grid.MouseLeave += (_, _) => Shown();
        copy.IsKeyboardFocusedChanged += (_, _) => Shown();
        grid.Children.Add(copy);
        panel.Children.Add(grid);
        Drawn(panel, ours: true);
        _lastAnswer = view;
        if (save && text.Length > 0) _conversation.Items.Add(new ChatItem { Kind = "assistant", Text = text, Time = DateTime.UtcNow });
        ScrollToEnd();
        return view;
    }

    /// <summary>A step: a one-line card (ring, check or cross; what it does) that opens to its input and result.</summary>
    private StepRow AddStepRow(string tool, string text, string state, string result)
    {
        var header = NeedsHeader;
        var row = new StepRow { Tool = tool };
        var outer = new StackPanel { Margin = new Thickness(0, header ? ChatUi.S6 : ChatUi.S1, 0, 0) };
        if (header) outer.Children.Add(ClaudeHeader(DateTime.UtcNow));
        var line = new DockPanel();
        DockPanel.SetDock(row.Mark, Dock.Left);
        line.Children.Add(row.Mark);
        row.Chevron = ChatUi.Glyph(ChatUi.GlyphRight, Brand.K.Muted, 9);
        row.Chevron.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        DockPanel.SetDock(row.Chevron, Dock.Right);
        line.Children.Add(row.Chevron);
        row.Line = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ChatUi.S2, 0, 0, 0) };
        Brand.Fg(row.Line, Brand.K.Muted);
        line.Children.Add(row.Line);
        row.Header = new ToggleButton { Content = line };
        row.Header.SetResourceReference(StyleProperty, "SpmChatDisclosure");
        row.Header.Checked += (_, _) => Expand(row, true);
        row.Header.Unchecked += (_, _) => Expand(row, false);
        row.Detail = Brand.Text("", Brand.K.DangerText, ChatUi.SmallSize);
        row.Detail.Margin = new Thickness(26, 0, ChatUi.S2, ChatUi.S2);
        row.Detail.Visibility = Visibility.Collapsed;
        row.Details = new StackPanel { Margin = new Thickness(26, 0, ChatUi.S2, ChatUi.S2), Visibility = Visibility.Collapsed };
        var stack = new StackPanel();
        stack.Children.Add(row.Header);
        stack.Children.Add(row.Detail);
        stack.Children.Add(row.Details);
        var card = ChatUi.Opaque(new Border { Child = stack, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(ChatUi.Radius), Padding = new Thickness(ChatUi.S1 / 2), SnapsToDevicePixels = true });
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        outer.Children.Add(card);
        SetStep(row, state, result, pop: false);
        Drawn(outer, ours: true);
        ScrollToEnd();
        return row;
    }

    private void SetStep(StepRow row, string state, string result, bool pop)
    {
        row.State = state;
        if (result.Length > 0) row.Result = result;
        FrameworkElement mark = state switch
        {
            "running" => ChatUi.StepRing(),
            "done" => ChatUi.StepMark(done: true),
            "stopped" => ChatUi.Glyph(ChatUi.GlyphStop, Brand.K.Muted, 11),
            _ => ChatUi.StepMark(done: false),
        };
        row.Mark.Content = mark;
        // A step that just finished ticks off: its check or cross pops in where the ring was.
        if (pop && state is "done" or "failed") ChatUi.Pop(mark);
        var title = StepNames.Title(row.Tool);
        var said = state switch { "running" => "running", "done" => "succeeded", "stopped" => "stopped", _ => "failed" };
        row.Header.ToolTip = title + " (" + said + ")\nClick for the input and the result.";
        AutomationProperties.SetName(row.Header, $"Step {title}, {said}. {row.Line.Text}");
        if (state == "failed" && row.Result.Length > 0)
        {
            var first = row.Result.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            row.Detail.Text = first.Length > 240 ? first.Substring(0, 240) + "…" : first;
            row.Detail.TextWrapping = TextWrapping.Wrap;
            row.Detail.Visibility = Visibility.Visible;
        }
        if (row.Header.IsChecked == true) FillDetails(row);
    }

    private void Expand(StepRow row, bool open)
    {
        row.Chevron.Text = open ? ChatUi.GlyphDown : ChatUi.GlyphRight;
        AutomationProperties.SetItemStatus(row.Header, open ? "Expanded" : "Collapsed");
        if (open) FillDetails(row);
        ChatUi.Reveal(row.Details, open);
    }

    /// <summary>The step's details: what was sent to Plaxis and what came back, to read and copy.</summary>
    private static void FillDetails(StepRow row)
    {
        row.Details.Children.Clear();
        void Part(string label, string text)
        {
            if (text.Trim().Length == 0 || text.Trim() == "{}") return;
            var head = ChatUi.Small(label, Brand.K.Muted, bold: true);
            head.Margin = new Thickness(0, ChatUi.S1, 0, 2);
            row.Details.Children.Add(head);
            var box = ChatUi.Selectable(text.Length > 4000 ? text.Substring(0, 4000) + "…" : text, label);
            box.FontFamily = ChatUi.Mono;
            box.FontSize = ChatUi.CodeSize;
            box.TextWrapping = TextWrapping.Wrap;
            box.MaxHeight = 220;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            row.Details.Children.Add(box);
        }
        Part("Sent", Pretty(row.Input));
        Part(row.State == "failed" ? "Problem" : "Result", Pretty(row.Result));
        if (row.Details.Children.Count == 0) row.Details.Children.Add(ChatUi.Small(row.State == "running" ? "Still running…" : "Nothing more to show for this step."));
    }

    private static string Pretty(string json)
    {
        var text = json.Trim();
        if (text.Length == 0 || text[0] is not ('{' or '[')) return json;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (System.Text.Json.JsonException) { return json; }
    }

    /// <summary>A note (muted, italic, with an info mark) or a problem (a notice box with an error mark).</summary>
    private void AddNote(string text, string key)
    {
        if (key == Brand.K.DangerText)
        {
            var row = new DockPanel();
            var icon = ChatUi.Glyph(ChatUi.GlyphError, Brand.K.DangerText, 14);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 1, ChatUi.S2, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(Brand.Text(text, Brand.K.Text));
            var box = new Border
            {
                Child = row, Padding = new Thickness(ChatUi.S4, ChatUi.S3, ChatUi.S4, ChatUi.S3), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(ChatUi.Radius), Margin = new Thickness(0, ChatUi.S4, 0, ChatUi.S1),
            };
            box.SetResourceReference(Border.BackgroundProperty, Brand.K.NoticeBg);
            box.SetResourceReference(Border.BorderBrushProperty, Brand.K.NoticeBorder);
            AutomationProperties.SetName(box, "Problem: " + text);
            AutomationProperties.SetLiveSetting(box, AutomationLiveSetting.Assertive);
            Drawn(box, ours: false);
            return;
        }
        var line = new DockPanel { Margin = new Thickness(0, ChatUi.S4, 0, ChatUi.S1) };
        var info = ChatUi.Glyph(ChatUi.GlyphInfo, Brand.K.Muted, 12);
        info.VerticalAlignment = VerticalAlignment.Top;
        info.Margin = new Thickness(0, 1, ChatUi.S2, 0);
        DockPanel.SetDock(info, Dock.Left);
        line.Children.Add(info);
        var note = Brand.Text(text, Brand.K.Muted, ChatUi.SmallSize);
        note.FontStyle = FontStyles.Italic;
        line.Children.Add(note);
        AutomationProperties.SetName(line, text);
        Drawn(line, ours: false);
    }
}
