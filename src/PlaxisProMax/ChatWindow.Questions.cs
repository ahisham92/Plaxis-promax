using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using PlaxisProMax.Chat;
using PlaxisProMax.Phone;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// The Questions tab, the "Over to you" cards and the Away switch, as in Sketch ProMax (ChatPane.Questions, HandoffCard,
/// ChatPane.Pause). Questions: a pill under the message box, "Questions (3)", opens a view in the conversation's place
/// with nothing but the open questions, numbered. Each has its answers to tap and Other to type; "4 yes" typed in the
/// message box answers too. An answered question leaves the tab; the answers are gathered for a moment and go to the
/// chat as one message. While the phone is on, each open question is on the phone too, with its answers as buttons.
/// Over to you: a step only the user can do, with Continue, Skip this step and Stop. Away: nothing pops up until the user
/// is back, and they get the list of what waited.
/// </summary>
internal sealed partial class ChatWindow
{
    private readonly StackPanel _questionList = new() { Margin = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S4), MaxWidth = 880 };
    private readonly TextBlock _questionsLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _awayLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _awayDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<int, string> _drafts = [];
    /// <summary>What the phone was asked (the question's id on Iris) and what its answer does here.</summary>
    private readonly Dictionary<long, Action<string>> _phoneAsks = [];
    /// <summary>The Questions tab's questions that are on the phone: number, id on Iris.</summary>
    private readonly Dictionary<int, long> _phoneQuestions = [];
    private List<UserAsk>? _sampleQuestions;
    private ScrollViewer? _questionsScroll;
    private Button? _questionsPill, _awayPill;
    private bool _questionsShown;
    private DateTime _lastTabAnswer;
    private int _otherOpen;
    private string _questionsDrawn = "";
    private DispatcherTimer? _questionsTimer;

    /// <summary>How long answers are gathered before they go to the chat together.</summary>
    private static readonly TimeSpan AnswerGather = TimeSpan.FromSeconds(4);

    /// <summary>The hand-off card's answers on the phone, in the order of its buttons.</summary>
    private static readonly string[] HandoffChoices = ["Continue", "Skip this step", "Stop"];

    private ChatQuestions? Questions => _crew?.Board.Questions;

    private List<UserAsk> OpenQuestions() => _sampleQuestions ?? Questions?.Open() ?? [];

    // ---- The pill and the view ---------------------------------------------------------------------------------------

    private Button QuestionsPill()
    {
        var pill = new Button { Content = _questionsLabel, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(0, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(StyleProperty, "SpmChatGhost");
        pill.Click += (_, _) =>
        {
            if (_questionsShown) HideQuestions(focus: true);
            else ShowQuestions();
        };
        _questionsPill = pill;
        if (!_sample)
        {
            _questionsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _questionsTimer.Tick += (_, _) => DeliverAnswers(now: false);
            _questionsTimer.Start();
            Closed += (_, _) => _questionsTimer.Stop();
            PhoneLink.Changed += () => Dispatcher.BeginInvoke(() => SyncPhoneQuestions(again: true));
            Handoff.Presenter = (request, jobKey) =>
            {
                Dispatcher.BeginInvoke(() => ShowHandoff(request, jobKey));
                return true;
            };
            Closed += (_, _) => Handoff.Presenter = null;
        }
        ShowQuestionsPill();
        return pill;
    }

    /// <summary>The view in the conversation's place: the heading with Back, then the open questions.</summary>
    private ScrollViewer QuestionsView()
    {
        _questionsScroll = new ScrollViewer
        {
            Content = _questionList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed, Focusable = false,
        };
        AutomationProperties.SetName(_questionsScroll, "Questions for you");
        return _questionsScroll;
    }

    private void ShowQuestionsPill()
    {
        if (_questionsPill is null) return;
        var open = OpenQuestions().Count;
        _questionsLabel.Text = _questionsShown ? "Back to the chat" : open > 0 ? $"Questions ({open})" : "Questions";
        Brand.Fg(_questionsLabel, open > 0 && !_questionsShown ? Brand.K.Link : Brand.K.Muted);
        _questionsLabel.FontWeight = open > 0 && !_questionsShown ? FontWeights.SemiBold : FontWeights.Normal;
        // Always there in a chat With coordinator, so the tab can be found before the first question.
        _questionsPill.Visibility = open > 0 || _questionsShown || IsCoordinator ? Visibility.Visible : Visibility.Collapsed;
        var tip = _questionsShown ? "Back to the conversation." : open > 0
            ? $"{open} question{(open == 1 ? "" : "s")} for you, numbered. Tap an answer, or type it in the box as \"4 yes\"."
            : "No questions are waiting. Questions from the coordinator and the jobs come here.";
        _questionsPill.ToolTip = tip;
        AutomationProperties.SetName(_questionsPill, _questionsLabel.Text);
        AutomationProperties.SetHelpText(_questionsPill, tip);
    }

    private void ShowQuestions()
    {
        if (_questionsScroll is null) return;
        CloseDrawer();
        _questionsShown = true;
        _otherOpen = 0;
        _scroll.Visibility = Visibility.Collapsed;
        _jump.Visibility = Visibility.Collapsed;
        _questionsScroll.Visibility = Visibility.Visible;
        RenderQuestions();
        ShowQuestionsPill();
    }

    private void HideQuestions(bool focus)
    {
        if (!_questionsShown || _questionsScroll is null) return;
        _questionsShown = false;
        _questionsScroll.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;
        ShowQuestionsPill();
        DeliverAnswers(now: true); // leaving the tab sends what was answered
        if (focus) _input.Focus();
    }

    /// <summary>The questions changed (asked, rewritten, answered), or another chat is shown: the pill and the tab follow.</summary>
    private void QuestionsChanged()
    {
        try { SyncPhoneQuestions(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* the phone is a convenience: the tab still shows the questions */ }
        ShowQuestionsPill();
        // Drawn again only when the questions changed: the jobs' progress mustn't take the box away while the user types.
        if (_questionsShown && Signature() != _questionsDrawn) RenderQuestions();
    }

    private string Signature() =>
        string.Join("\n", OpenQuestions().Select(q => $"{q.Number}|{q.Text}|{string.Join("/", q.Options)}|{q.Recommended}|{q.Unclear}|{q.Answer}"));

    private void RenderQuestions()
    {
        _questionList.Children.Clear();
        _questionsDrawn = Signature();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, ChatUi.S3) };
        var back = ChatUi.Ghost(ChatUi.GlyphUp, "Back to the chat", "Back to the chat", "Back to the conversation (Esc).");
        back.Click += (_, _) => HideQuestions(focus: true);
        DockPanel.SetDock(back, Dock.Right);
        head.Children.Add(back);
        head.Children.Add(Brand.Text("Questions for you", Brand.K.Text, ChatUi.TitleSize, bold: true));
        _questionList.Children.Add(head);
        var open = OpenQuestions();
        if (open.Count == 0)
        {
            _questionList.Children.Add(Wrapped(Brand.Text("No questions are waiting. When the coordinator or a job needs you to decide something, it comes here, numbered.", Brand.K.Muted, ChatUi.BodySize)));
            return;
        }
        var how = Wrapped(Brand.Text("Tap an answer, or Other to type one. You can also type \"4 yes\" in the box below. Answered questions leave this list.", Brand.K.Muted, ChatUi.SmallSize));
        how.Margin = new Thickness(0, 0, 0, ChatUi.S3);
        _questionList.Children.Add(how);
        foreach (var q in open) _questionList.Children.Add(QuestionCard(q));
    }

    private UIElement QuestionCard(UserAsk q)
    {
        var panel = new StackPanel { Margin = new Thickness(ChatUi.S3) };
        var top = new WrapPanel();
        top.Children.Add(Brand.Text($"Q{q.Number}", Brand.K.Accent, ChatUi.BodySize, bold: true));
        top.Children.Add(ChatUi.Small("  " + q.From + (q.Rewritten != default ? " · written again" : "")));
        panel.Children.Add(top);
        var text = Wrapped(Brand.Text(q.Text, Brand.K.Text, ChatUi.BodySize));
        text.Margin = new Thickness(0, ChatUi.S1, 0, ChatUi.S2);
        panel.Children.Add(text);
        if (q.Unclear)
        {
            var wait = Wrapped(Brand.Text($"You said: \"{q.Answer}\". It is being written again; you can still answer it.", Brand.K.Muted, ChatUi.SmallSize));
            wait.Margin = new Thickness(0, 0, 0, ChatUi.S2);
            panel.Children.Add(wait);
        }
        var options = new WrapPanel();
        for (var k = 0; k < q.Options.Count; k++)
        {
            var answer = q.Options[k];
            var label = $"{(char)('A' + k)}. {answer}" + (k == q.Recommended ? " (suggested)" : "");
            // A WrapPanel measures its children unbounded: the width cap lets a long option wrap.
            var option = new Button { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, Margin = new Thickness(0, 0, ChatUi.S1, ChatUi.S1) };
            option.SetResourceReference(StyleProperty, "SpmChatPill");
            if (k == q.Recommended) option.SetResourceReference(Control.BorderBrushProperty, Brand.K.Accent);
            AutomationProperties.SetName(option, $"Q{q.Number}: {answer}");
            option.ToolTip = "Answer with this.";
            option.Click += (_, _) => AnswerQuestion(q.Number, answer);
            options.Children.Add(option);
        }
        var other = new Button { Content = new TextBlock { Text = q.Options.Count > 0 ? "Other…" : "Answer…" }, Margin = new Thickness(0, 0, ChatUi.S1, ChatUi.S1) };
        other.SetResourceReference(StyleProperty, "SpmChatPill");
        AutomationProperties.SetName(other, $"Q{q.Number}: type an answer");
        other.ToolTip = "Type your own answer (or \"I don't understand\" to have it written again).";
        options.Children.Add(other);
        panel.Children.Add(options);
        var box = new TextBox
        {
            AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 160, FontSize = ChatUi.BodySize, Padding = new Thickness(ChatUi.S2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, SpellCheck = { IsEnabled = true },
        };
        box.SetResourceReference(StyleProperty, "SpmChatField");
        if (_drafts.TryGetValue(q.Number, out var draft)) box.Text = draft;
        box.TextChanged += (_, _) => _drafts[q.Number] = box.Text;
        AutomationProperties.SetName(box, $"Your answer to Q{q.Number}");
        AutomationProperties.SetHelpText(box, "Enter sends it. Escape closes the box.");
        var send = new Button { Content = new TextBlock { Text = "Send answer" }, Margin = new Thickness(0, ChatUi.S1, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        send.SetResourceReference(StyleProperty, "SpmChatPrimary");
        var typing = new StackPanel { Visibility = _otherOpen == q.Number ? Visibility.Visible : Visibility.Collapsed };
        typing.Children.Add(box);
        typing.Children.Add(send);
        panel.Children.Add(typing);
        void SendTyped()
        {
            if (box.Text.Trim().Length == 0) { box.Focus(); return; }
            _otherOpen = 0;
            _drafts.Remove(q.Number);
            AnswerQuestion(q.Number, ChatQuestions.PickOption(q, box.Text.Trim()));
        }
        other.Click += (_, _) =>
        {
            _otherOpen = q.Number;
            typing.Visibility = Visibility.Visible;
            box.Focus();
        };
        send.Click += (_, _) => SendTyped();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; SendTyped(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _otherOpen = 0; typing.Visibility = Visibility.Collapsed; other.Focus(); }
        };
        var card = ChatUi.Elevated(panel, out _);
        card.Margin = new Thickness(0, 0, 0, ChatUi.S2);
        return card;
    }

    // ---- Answers -----------------------------------------------------------------------------------------------------

    /// <summary>The user's answer to question <paramref name="number"/>: it leaves the tab and goes to the chat shortly.</summary>
    private void AnswerQuestion(int number, string answer, bool onPhone = false)
    {
        if (Questions?.Answer(number, answer, DateTime.UtcNow) is not { } q) return;
        _lastTabAnswer = DateTime.UtcNow;
        ActivityLog.Note("question_answered", $"Q{q.Number}{(onPhone ? " on the phone" : "")}{(q.Unclear ? " (not understood)" : "")}: {JobBoard.OneLine(q.Answer, 200)}");
        Save();
        QuestionsChanged();
    }

    /// <summary>
    /// Answers typed in the message box by number ("4 yes", one per line): taken as answers, sent at once. False when the
    /// text isn't that (an ordinary message).
    /// </summary>
    private bool TypedAnswers(string typed)
    {
        if (_sample || Questions is not { OpenCount: > 0 } questions) return false;
        var answers = questions.Typed(typed);
        if (answers.Count == 0) return false;
        var now = DateTime.UtcNow;
        foreach (var (ask, answer) in answers) questions.Answer(ask.Number, answer, now);
        Save();
        QuestionsChanged();
        DeliverAnswers(now: true);
        return true;
    }

    /// <summary>
    /// Sends the answers gathered to the chat as one message, as its next turn; while it works they wait for the turn to
    /// end. <paramref name="now"/>: without waiting for more answers.
    /// </summary>
    private void DeliverAnswers(bool now)
    {
        if (_sample || Questions is not { AnswersWaiting: true } questions) return;
        if (!now && DateTime.UtcNow - _lastTabAnswer < AnswerGather) return;
        if (_busy || _claude is null || !Licensing.LicenseState.Allowed) return;
        var answers = questions.TakeAnswers();
        if (answers.Count == 0) return;
        var shown = "Answered " + string.Join("; ", answers.Select(q => $"Q{q.Number}: {JobBoard.OneLine(q.Answer, 120)}"));
        ActivityLog.Note("questions_sent", string.Join(", ", answers.Select(q => "Q" + q.Number)));
        AddItem(new ChatItem { Kind = "user", Text = shown, Time = DateTime.UtcNow });
        Save();
        QuestionsChanged();
        _mcp().NewTask();
        SetBusy(true);
        _turnStart = DateTime.UtcNow;
        _lastActivity = DateTime.UtcNow;
        _ = SendAsync(ChatQuestions.Message(answers));
    }

    /// <summary>
    /// A turn ended with questions written in its text instead of the tab (chat_questions): they go in the tab too, so
    /// every question can be answered there.
    /// </summary>
    private void CatchQuestions()
    {
        if (_sample || Questions is not { } questions || !IsCoordinator) return;
        var last = _conversation.Items.FindLastIndex(i => i.Kind == "user");
        var items = _conversation.Items.Skip(last + 1).ToList();
        if (items.Any(i => i.Kind == "tool" && i.Name.EndsWith(ChatQuestionsTool.ToolName, StringComparison.Ordinal))) return;
        if (items.LastOrDefault(i => i.Kind == "assistant" && i.Text.Trim().Length > 0) is not { } reply) return;
        var now = DateTime.UtcNow;
        var added = new List<int>();
        foreach (var (text, options) in ChatQuestions.FromReply(reply.Text))
        {
            var before = questions.OpenCount;
            var q = questions.Ask(0, "Coordinator", text, options, -1, now);
            if (questions.OpenCount > before) added.Add(q.Number);
        }
        if (added.Count == 0) return;
        ActivityLog.Note("questions_caught", string.Join(", ", added.Select(n => "Q" + n)));
        Note(string.Join(", ", added.Select(n => "Q" + n)) + (added.Count == 1 ? " is" : " are") + " in Questions: answer there or type \"" + added[0] + " a\".");
        QuestionsChanged();
        Save();
    }

    // ---- The phone -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// While the phone is on, the Questions tab is on the phone too: each open question it hasn't had goes there with its
    /// answers as buttons, and one answered here, withdrawn or being written again is closed there. <paramref name="again"/>
    /// (the phone was just switched on): every open question the phone isn't waiting on goes again.
    /// </summary>
    private void SyncPhoneQuestions(bool again = false)
    {
        if (_sample || Questions is not { } questions) return;
        if (!PhoneLink.Active)
        {
            _phoneQuestions.Clear();
            return;
        }
        foreach (var (n, id) in _phoneQuestions.ToList())
        {
            var q = questions.Find(n);
            if (q is { Open: true, Unclear: false }) continue;
            _phoneQuestions.Remove(n);
            _phoneAsks.Remove(id);
            var said = q is null || q.Closed ? $"Q{n} is no longer needed."
                : q.Unclear ? $"Q{n} is being written more plainly; the new one follows."
                : $"Q{n} was answered on the PC: {JobBoard.OneLine(q.Answer, 120)}";
            _ = Task.Run(() => PhoneLink.SettleAsync(id, said));
        }
        if (again)
            foreach (var q in questions.Open().Where(q => !_phoneQuestions.ContainsKey(q.Number))) questions.MarkOnPhone(q, default);
        var now = DateTime.UtcNow;
        var topic = PhoneTopic();
        foreach (var q in questions.NotOnPhone())
        {
            questions.MarkOnPhone(q, now);
            var number = q.Number;
            var version = q.Version;
            var text = $"Q{q.Number} ({q.From}): {q.Text}" + (q.Recommended >= 0 && q.Recommended < q.Options.Count ? $"\n\nSuggested: {q.Options[q.Recommended]}" : "");
            var options = q.Options.Count > 0 ? q.Options.ToList() : null;
            _ = Task.Run(async () =>
            {
                var (id, error) = await PhoneLink.AskAsync(text, "", null, topic, options);
                await Dispatcher.InvokeAsync(() => PhoneQuestionSent(number, version, id, error));
            });
        }
    }

    /// <summary>A question went to the phone, or couldn't: one that couldn't goes again with the next change, and the chat says so once.</summary>
    private void PhoneQuestionSent(int number, DateTime version, long id, string? error)
    {
        if (Questions?.Find(number) is not { } ask) return;
        if (id != 0)
        {
            ask.PhoneFailSaid = false;
            if (!ask.Open || ask.Version != version)
            {
                _ = Task.Run(() => PhoneLink.SettleAsync(id, $"Q{number} changed on the PC."));
                return;
            }
            _phoneQuestions[number] = id;
            _phoneAsks[id] = answer => PhoneQuestionAnswered(number, answer);
            return;
        }
        if (ask.Open && ask.Version == version) Questions.MarkOnPhone(ask, default);
        if (ask.PhoneFailSaid) return;
        ask.PhoneFailSaid = true;
        Note($"Q{number} couldn't go to your phone yet ({error}). It is in Questions here, and it is sent again when the phone answers.");
    }

    /// <summary>A Questions tab question was answered on the phone: taken as if tapped here.</summary>
    private void PhoneQuestionAnswered(int number, string answer)
    {
        _phoneQuestions.Remove(number);
        if (Questions?.Find(number) is not { Open: true } ask)
        {
            _ = Task.Run(() => PhoneLink.UpdateAsync($"Q{number} was already answered on the PC, so this answer wasn't used.", PhoneTopic()));
            return;
        }
        AnswerQuestion(number, ChatQuestions.PickOption(ask, answer), onPhone: true);
    }

    // ---- Over to you ---------------------------------------------------------------------------------------------------

    /// <summary>A step only the user can do: the card goes in the chat (and to the phone while it is on).</summary>
    private void ShowHandoff(HandoffRequest request, string? jobKey)
    {
        var from = jobKey is null ? "" : ChatJobs.AskerName(jobKey);
        var item = new ChatItem { Kind = "handoff", Name = request.Id, Text = request.Step.What, Input = request.Step.Where, Result = request.Step.Why, Time = DateTime.UtcNow };
        _conversation.Items.Add(item);
        if (_questionsShown) HideQuestions(focus: false);
        _messages.Children.Add(HandoffCard(item, request, from));
        ScrollToEnd();
        ChatUi.FlashIfAway(this);
        Save();
        if (!PhoneLink.Active) return;
        var topic = PhoneTopic();
        var text = "Over to you" + (from.Length > 0 ? $" ({from})" : "") + ": " + request.Step.What + "." +
                   (request.Step.Where.Length > 0 ? "\nWhere: " + request.Step.Where : "") + (request.Step.Why.Length > 0 ? "\nWhy: " + request.Step.Why : "");
        _ = Task.Run(async () =>
        {
            var (id, _) = await PhoneLink.AskAsync(text, "", null, topic, HandoffChoices);
            if (id == 0) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (request.State == HandoffState.Closed) { _ = Task.Run(() => PhoneLink.SettleAsync(id, request.Outcome ?? "Closed.")); return; }
                _phoneAsks[id] = said => request.Answer(Array.FindIndex(HandoffChoices, c => c.Equals(said, StringComparison.OrdinalIgnoreCase)) switch
                {
                    0 => HandoffChoice.Continue, 2 => HandoffChoice.Stop, _ => HandoffChoice.Skip,
                });
                request.Changed += r =>
                {
                    if (r.State != HandoffState.Closed) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_phoneAsks.Remove(id)) _ = Task.Run(() => PhoneLink.SettleAsync(id, r.Outcome ?? "Closed."));
                    });
                };
            });
        });
    }

    /// <summary>
    /// The "Over to you" card: what, where and why, with Continue, Skip this step and Stop while it waits
    /// (<paramref name="live"/>); a saved one, or one whose request has gone, shows how it ended.
    /// </summary>
    private UIElement HandoffCard(ChatItem item, HandoffRequest? live, string from = "")
    {
        var body = new StackPanel();
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, ChatUi.S2) };
        var icon = ChatUi.Glyph(ChatUi.GlyphInfo, Brand.K.Accent, 14);
        icon.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        DockPanel.SetDock(icon, Dock.Left);
        title.Children.Add(icon);
        title.Children.Add(Brand.Text("Over to you" + (from.Length > 0 ? "  ·  " + from : ""), Brand.K.Text, ChatUi.BodySize, bold: true));
        body.Children.Add(title);
        body.Children.Add(Wrapped(Brand.Text(item.Text, Brand.K.Text, ChatUi.BodySize, bold: true)));
        if (item.Input.Length > 0) body.Children.Add(Wrapped(Brand.Text("Where: " + item.Input, Brand.K.Text, ChatUi.SmallSize)));
        if (item.Result.Length > 0) body.Children.Add(Wrapped(Brand.Text("Why: " + item.Result, Brand.K.Muted, ChatUi.SmallSize)));
        var ended = Wrapped(Brand.Text("", Brand.K.Muted, ChatUi.SmallSize));
        ended.Margin = new Thickness(0, ChatUi.S2, 0, 0);
        var buttons = new WrapPanel { Margin = new Thickness(0, ChatUi.S3, 0, 0) };
        void Ended()
        {
            buttons.Visibility = Visibility.Collapsed;
            ended.Text = item.State switch
            {
                "Done" => "Done; the task carried on.",
                "Skipped" => "Skipped.",
                "Stopped" => "Stopped here.",
                { Length: > 0 } other => other,
                _ => "No longer waiting (the task ended or the window was closed).",
            };
            Brand.Fg(ended, item.State == "Done" ? Brand.K.Text : Brand.K.Muted);
        }
        if (live is null) Ended();
        else
        {
            var go = Brand.Button("Continue", Brand.Weight.Primary);
            var skip = Brand.Button("Skip this step");
            var stop = Brand.Button("Stop");
            go.ToolTip = "You did the step: the task carries on.";
            skip.ToolTip = "Leave this step: the task carries on without it.";
            stop.ToolTip = "Stop the task here.";
            foreach (var b in new[] { go, skip, stop })
            {
                b.Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S2);
                buttons.Children.Add(b);
            }
            go.Click += (_, _) => live.Answer(HandoffChoice.Continue);
            skip.Click += (_, _) => live.Answer(HandoffChoice.Skip);
            stop.Click += (_, _) => live.Answer(HandoffChoice.Stop);
            void Draw()
            {
                if (live.State == HandoffState.Closed)
                {
                    item.State = live.Outcome ?? "";
                    Ended();
                    Save();
                    return;
                }
                buttons.IsEnabled = live.State == HandoffState.Waiting;
                ended.Text = live.State == HandoffState.Checking ? "Checking…" : live.Note ?? "";
            }
            live.Changed += _ => Dispatcher.BeginInvoke(Draw);
            Draw();
        }
        body.Children.Add(buttons);
        body.Children.Add(ended);
        var card = new Border
        {
            Child = body, Padding = new Thickness(ChatUi.S3), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ChatUi.Radius), Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1),
        };
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.AccentSoft);
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.Accent);
        AutomationProperties.SetName(card, "Over to you: " + item.Text);
        AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Assertive);
        return card;
    }

    // ---- Away ----------------------------------------------------------------------------------------------------------

    private Button AwayPill()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_awayDot);
        row.Children.Add(_awayLabel);
        var pill = new Button { Content = row, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(0, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(StyleProperty, "SpmChatGhost");
        pill.Click += (_, _) =>
        {
            var on = !Away.On;
            if (!on && Away.Take() is { Length: > 0 } meanwhile) Note(meanwhile);
            Away.Set(on, DateTime.UtcNow);
            Note(on
                ? $"Away mode until {Away.Until.ToLocalTime():HH:mm}: nothing pops up. Changes that need your OK and steps only you can do wait for you " +
                  "(approvals still reach your phone while it is on), and the rest of the work carries on. Click Away again when you're back."
                : "Away mode is off: approvals and questions show again.");
            Save();
        };
        if (!_sample)
        {
            Away.StoreFile = System.IO.Path.Combine(AgentSettings.Folder, "away.txt");
            Away.Restore();
            Away.Changed += () => Dispatcher.BeginInvoke(ShowAway);
        }
        _awayPill = pill;
        ShowAway();
        return pill;
    }

    private void ShowAway()
    {
        if (_awayPill is null) return;
        var on = !_sample && Away.On;
        _awayLabel.Text = on ? $"Away until {Away.Until.ToLocalTime():HH:mm}" : "Away: off";
        Brand.Fg(_awayLabel, on ? Brand.K.Text : Brand.K.Muted);
        _awayDot.SetResourceReference(Shape.FillProperty, on ? Brand.K.WarningText : Brand.K.Muted);
        var tip = on
            ? "On: nothing pops up and the work carries on by itself. Click when you're back."
            : "Off. Click before you leave the PC: nothing pops up, and what needs you waits in a list, for up to 12 hours.";
        _awayPill.ToolTip = tip;
        AutomationProperties.SetName(_awayPill, _awayLabel.Text);
        AutomationProperties.SetHelpText(_awayPill, tip);
    }

    /// <summary>A message the user typed: what waited while they were away is listed, and "I'm away for 2 hours" or "I'm back" switches Away.</summary>
    private void HearAway(string typed)
    {
        if (_sample) return;
        if (Away.Take() is { Length: > 0 } meanwhile) Note(meanwhile);
        if (Away.Heard(typed, DateTime.UtcNow) is { } away) Note(away);
    }

    // ---- For the screenshots -------------------------------------------------------------------------------------------

    /// <summary>For the screenshots: the Questions tab with three questions, and the pills under the message box.</summary>
    internal void ShowSampleQuestions()
    {
        var now = DateTime.UtcNow;
        _conversation.Name = "Quay wall: soil layers and phases";
        _conversation.Type = ChatTypes.Coordinator;
        _sampleQuestions =
        [
            new UserAsk { Number = 4, From = "Coordinator", Asked = now, Text = "BH-3 has no groundwater reading. Which head should its borehole get?",
                Options = ["The same as BH-2 (+0.80 m)", "Interpolate between BH-2 and BH-4", "Leave it dry"], Recommended = 1 },
            new UserAsk { Number = 5, Job = 2, From = "Job 2: Soil parameters", Asked = now, Text = "The report gives no stiffness for the dense sand. Use a correlation from its SPT (N = 38)?",
                Options = ["Yes, E50 = 45 MPa from the SPT, marked as estimated", "No, wait for the lab results"], Recommended = 0 },
            new UserAsk { Number = 6, Job = 3, From = "Job 3: Phases", Asked = now, Rewritten = now, Text = "Should the anchors be prestressed in the same phase they are installed in, or in a phase of their own?",
                Options = ["Same phase", "A phase of their own"], Recommended = 1 },
        ];
        ShowTitle();
        ShowQuestions();
    }

    /// <summary>For the screenshots: a chat with an "Over to you" card that waits, and one that ended.</summary>
    internal void ShowSampleHandoff()
    {
        var now = DateTime.UtcNow;
        _conversation.Name = "Results of the excavation phases";
        _conversation.Items.Add(new ChatItem { Kind = "user", Text = "Get the wall's bending moments for every phase into Excel.", Time = now });
        ShowItem(_conversation.Items[^1]);
        AddAnswer("The phases are calculated. To read their results I need Plaxis Output's remote scripting, which only you can switch on.", save: false);
        var done = new ChatItem { Kind = "handoff", Text = "Close the Materials window in Plaxis Input", Input = "The Material sets window, OK or Cancel", Result = "Plaxis takes no commands while it is open", State = "Done" };
        _messages.Children.Add(HandoffCard(done, null));
        var step = new HandoffStep { What = "Start the remote scripting server in Plaxis Output", Where = "Output: Expert > Configure remote scripting server > Start server", Why = "The results of the phases are read through it" };
        var request = new HandoffRequest(step, 1, 1);
        _ = request.Wait(null);
        _messages.Children.Add(HandoffCard(new ChatItem { Kind = "handoff", Text = step.What, Input = step.Where, Result = step.Why }, request));
        ShowTitle();
    }
}
