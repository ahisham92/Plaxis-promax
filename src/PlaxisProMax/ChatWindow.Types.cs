using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PlaxisProMax.Chat;
using PlaxisProMax.Settings;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// The chat's own choices, as in Sketch ProMax (ChatPane.Types, SignInFlow, ChatPane.Quick): its kind (With coordinator
/// or Simple chat, switched from the pill under the message box), its model and effort (pills by Send; Automatic picks
/// per request by the kind of work), signing in to Claude from inside the window with no console, and what is typed
/// while the chat works: a question is answered at once by a quick read-only run, anything else waits for the answer
/// to end and is sent then.
/// </summary>
internal sealed partial class ChatWindow
{
    private readonly Popup _choiceMenu = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Top };
    private readonly StackPanel _choiceList = new() { Margin = new Thickness(ChatUi.S1) };
    private readonly TextBlock _modelPickLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _effortPickLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private Button? _modelPick, _effortPick, _typePill;
    private DateTime _choiceClosed;
    /// <summary>What Automatic picked for this chat's process (null before one started, or after a chat switch).</summary>
    private AutoPick? _lastPick;
    /// <summary>The chat's process was started for another model, effort or kind: the next message starts it again (the same conversation).</summary>
    private bool _restartEngine;
    /// <summary>Typed while the chat worked: sent when the answer ends.</summary>
    private readonly List<string> _typedMeanwhile = [];
    /// <summary>Questions answered while the chat was busy (Answer "" when none came), told to it with the next message.</summary>
    private readonly List<(string Question, string Answer)> _asides = [];

    /// <summary>The longest a quick answer may take before the question goes to the chat itself instead.</summary>
    private const int QuickTimeoutMs = 120_000;

    private sealed record Choice(string Heading, List<(string Label, string Hint, bool Chosen, Action Pick)> Rows);

    // ---- The pills ------------------------------------------------------------------------------------------------------

    /// <summary>The model and effort pills (by Send) and the menu they share with the kind pill.</summary>
    private (Button Model, Button Effort) BuildPickers()
    {
        _modelPick = PickerPill(_modelPickLabel, () => ToggleChoice(_modelPick!, new Choice("Model for this chat",
            new[] { (Id: ChatAuto.Automatic, Label: "Automatic", Hint: "Haiku, Sonnet or Opus per request and per job, by the kind of work (Settings > Crew)") }
                .Concat(ChatModels.All.Select(c => (c.Id, c.Label, c.Hint)))
                .Select(c => (c.Label, c.Hint, c.Id == ChatAuto.ParseModel(_conversation.Model), (Action)(() => SetChatModel(c.Id)))).ToList())));
        _effortPick = PickerPill(_effortPickLabel, () => ToggleChoice(_effortPick!, new Choice("Effort for this chat",
            new[] { ChatAuto.Automatic }.Concat(ChatAuto.Efforts)
                .Select(e => (ChatAuto.IsAuto(e) ? "Automatic" : ChatAuto.EffortLabel(e), ChatAuto.IsAuto(e) ? "Low for simple work, higher for hard reasoning" : ChatAuto.EffortHint(e),
                    e == ChatAuto.ParseEffort(_conversation.Effort), (Action)(() => SetChatEffort(e)))).ToList())));
        var card = ChatUi.Elevated(_choiceList, out _);
        card.Margin = new Thickness(ChatUi.S3);
        card.MinWidth = 240;
        card.MaxWidth = 320;
        _choiceMenu.Child = card;
        _choiceMenu.Closed += (_, _) => _choiceClosed = DateTime.UtcNow;
        ShowPickers();
        return (_modelPick, _effortPick);
    }

    private Button PickerPill(TextBlock label, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        var chevron = ChatUi.Icon(ChatUi.Paths.ChevronDown, 12);
        chevron.Margin = new Thickness(2, 0, 0, 0);
        row.Children.Add(chevron);
        var pill = new Button { Content = row, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(0, 0, ChatUi.S1, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(StyleProperty, "SpmChatGhost");
        pill.Click += (_, _) => click();
        return pill;
    }

    /// <summary>The kind pill under the message box: this chat's kind, and the switch to the other.</summary>
    private Button TypePill()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_typeLabel);
        var chevron = ChatUi.Icon(ChatUi.Paths.ChevronDown, 12);
        chevron.Margin = new Thickness(2, 0, 0, 0);
        row.Children.Add(chevron);
        var pill = new Button { Content = row, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(0, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(StyleProperty, "SpmChatGhost");
        pill.Click += (_, _) => ToggleChoice(pill, new Choice("This chat",
            ChatTypes.All.Select(t => (ChatTypes.Label(t), ChatTypes.Hint(t), t == ChatTypes.Of(_conversation), (Action)(() => SetChatType(t)))).ToList()));
        _typePill = pill;
        return pill;
    }

    /// <summary>The pills say the chat's choice; on Automatic, what it picked last.</summary>
    private void ShowPickers()
    {
        if (_modelPick is null || _effortPick is null) return;
        var model = ChatAuto.ParseModel(_conversation.Model);
        var effort = ChatAuto.ParseEffort(_conversation.Effort);
        var (modelAuto, effortAuto) = (ChatAuto.IsAuto(model), ChatAuto.IsAuto(effort));
        _modelPickLabel.Text = modelAuto ? "Auto" + (_lastPick is { } p ? " (" + ChatAuto.ModelLabel(p.Model) + ")" : "") : ChatAuto.ModelLabel(model);
        _effortPickLabel.Text = effortAuto ? "Effort: auto" + (_lastPick is { Effort.Length: > 0 } q ? " (" + ChatAuto.EffortLabel(q.Effort).ToLowerInvariant() + ")" : "") : "Effort: " + ChatAuto.EffortLabel(effort).ToLowerInvariant();
        Brand.Fg(_modelPickLabel, Brand.K.Muted);
        Brand.Fg(_effortPickLabel, Brand.K.Muted);
        _modelPick.ToolTip = modelAuto
            ? "Automatic: a light model for simple work, a stronger one for hard reasoning (Settings > Crew). Click to pick one for this chat."
            : $"{ChatAuto.ModelLabel(model)} for this chat and its new jobs, until you set it back to Automatic. Click to change.";
        _effortPick.ToolTip = effortAuto
            ? "Automatic: low effort for simple work, higher for hard reasoning (Settings > Crew). Click to pick one for this chat."
            : $"{ChatAuto.EffortLabel(effort)} effort for this chat and its new jobs, until you set it back to Automatic. Click to change.";
        AutomationProperties.SetName(_modelPick, "Model for this chat: " + (modelAuto ? "Automatic" : ChatAuto.ModelLabel(model)));
        AutomationProperties.SetHelpText(_modelPick, (string)_modelPick.ToolTip);
        AutomationProperties.SetName(_effortPick, "Effort for this chat: " + (effortAuto ? "Automatic" : ChatAuto.EffortLabel(effort)));
        AutomationProperties.SetHelpText(_effortPick, (string)_effortPick.ToolTip);
        if (_typePill is not null)
        {
            var type = ChatTypes.Of(_conversation);
            _typePill.ToolTip = $"This chat: {ChatTypes.Label(type)}. {ChatTypes.Hint(type)}. Click to switch (while no job is open).";
            AutomationProperties.SetName(_typePill, "Chat type: " + ChatTypes.Label(type));
            AutomationProperties.SetHelpText(_typePill, (string)_typePill.ToolTip);
        }
        if (_sample) return;
        ChatJobs.ChatModel = model;
        ChatJobs.ChatEffort = effort;
    }

    /// <summary>Opens (or closes) the small menu over <paramref name="anchor"/> with <paramref name="choice"/>'s rows.</summary>
    private void ToggleChoice(Button anchor, Choice choice)
    {
        if (_choiceMenu.IsOpen)
        {
            _choiceMenu.IsOpen = false;
            anchor.Focus();
            return;
        }
        // A click on the button while the menu is open first closes it (it closes on any click outside): don't reopen it.
        if (DateTime.UtcNow - _choiceClosed < TimeSpan.FromMilliseconds(250)) return;
        _choiceList.Children.Clear();
        var heading = Brand.Text(choice.Heading, Brand.K.Muted, ChatUi.SmallSize, bold: true);
        heading.Margin = new Thickness(10, 6, 10, 4);
        _choiceList.Children.Add(heading);
        Button? first = null;
        foreach (var (label, hint, chosen, pick) in choice.Rows)
        {
            var text = new StackPanel();
            text.Children.Add(Brand.Text(label, Brand.K.Text, ChatUi.BodySize, bold: chosen));
            var line = Wrapped(Brand.Text(hint, Brand.K.Muted, ChatUi.SmallSize));
            line.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(line);
            var row = new DockPanel();
            var check = ChatUi.Glyph(ChatUi.GlyphCheck, Brand.K.Accent, 12);
            check.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
            check.Visibility = chosen ? Visibility.Visible : Visibility.Hidden;
            DockPanel.SetDock(check, Dock.Right);
            row.Children.Add(check);
            row.Children.Add(text);
            var item = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            item.SetResourceReference(StyleProperty, "SpmChatMenuItem");
            AutomationProperties.SetName(item, label + (chosen ? ", chosen" : ""));
            AutomationProperties.SetHelpText(item, hint);
            item.Click += (_, _) =>
            {
                _choiceMenu.IsOpen = false;
                anchor.Focus();
                pick();
            };
            _choiceList.Children.Add(item);
            if (chosen) first = item;
            else first ??= item;
        }
        _choiceMenu.PlacementTarget = anchor;
        _choiceMenu.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => first?.Focus());
    }

    /// <summary>The chat's model: a model sticks for this chat and its new jobs; Automatic picks per request again.</summary>
    private void SetChatModel(string id)
    {
        id = ChatAuto.ParseModel(id);
        if (id == ChatAuto.ParseModel(_conversation.Model)) return;
        _conversation.Model = id;
        _lastPick = null;
        _restartEngine = true;
        Save();
        ShowPickers();
        Note(ChatAuto.IsAuto(id) ? "Model: Automatic. Each request and new job gets one by the kind of work."
            : $"Model: {ChatModels.Find(id).Label} for this chat and its new jobs, from your next message, until you set it back to Automatic.");
    }

    /// <summary>The chat's effort: a level sticks for this chat and its new jobs; Automatic picks per request again.</summary>
    private void SetChatEffort(string level)
    {
        level = ChatAuto.ParseEffort(level);
        if (level == ChatAuto.ParseEffort(_conversation.Effort)) return;
        _conversation.Effort = level;
        _lastPick = null;
        _restartEngine = true;
        Save();
        ShowPickers();
        Note((ChatAuto.IsAuto(level) ? "Effort: Automatic. Low for simple work, higher for hard reasoning."
            : $"Effort: {ChatAuto.EffortLabel(level)} for this chat and its new jobs, from your next message, until you set it back to Automatic.")
            + (ClaudeCli.EffortRefused ? " This Claude Code doesn't take an effort setting; update it (claude update) to use it." : ""));
    }

    /// <summary>
    /// Switches the shown chat's kind, unless one of its jobs is open or an answer is running (the chat says why). The
    /// next message starts the chat's Claude Code again with the new kind's tools and instructions; the conversation
    /// itself carries on.
    /// </summary>
    private void SetChatType(string type)
    {
        type = ChatTypes.Parse(type);
        if (type == ChatTypes.Of(_conversation)) return;
        var jobsOpen = _crew?.Board.Snapshot().Any(j => j.Open) == true;
        if (ChatTypes.WhyNotSwitch(jobsOpen, _busy) is { } why)
        {
            Note(why);
            return;
        }
        _conversation.Type = type;
        _engine?.Dispose();
        _engine = null;
        _lastPick = null;
        AttachCrew();
        Note(type == ChatTypes.Simple
            ? "This chat is now a simple chat: Plaxis ProMax does all the work here itself, without jobs."
            : "This chat now works with a coordinator: it can hand separate parts of a request to jobs that work side by side.");
        Save();
        ShowTitle();
    }

    // ---- Typed while the chat works ------------------------------------------------------------------------------------

    /// <summary>
    /// Typed while the chat works: a plain question is answered at once by a quick read-only run (the work carries on);
    /// anything else waits and is sent when the answer ends. False when there is nothing to take (Stop then stops).
    /// </summary>
    private bool TypedWhileBusy()
    {
        var typed = _input.Text.Trim();
        if (typed.Length == 0 || _sample || !_busy) return false;
        if (_attached.Count == 0 && _crew is not { Run.State: AutopilotRun.Running } && QuickAnswer(typed)) return true;
        // With files, or not a question: kept (with its files' note) for the end of this answer.
        if (_attached.Count > 0)
        {
            try { typed += Attachments.Note(_attached.ToList(), ClaudeCli.AttachmentsFolder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { typed += "\n\nAttached files:\n" + string.Join("\n", _attached.Select(f => "- " + f)); }
            _attached.Clear();
            ShowChips();
        }
        _typedMeanwhile.Add(typed);
        _input.Clear();
        AddNote("Kept for when this answer ends: " + JobBoard.OneLine(typed, 160), Brand.K.Muted);
        ScrollToEnd();
        return true;
    }

    /// <summary>What was typed while the chat worked goes in now, as one message.</summary>
    private void SendTypedMeanwhile()
    {
        if (_busy || _typedMeanwhile.Count == 0) return;
        var text = string.Join("\n\n", _typedMeanwhile);
        _typedMeanwhile.Clear();
        Send(text, typed: false);
    }

    /// <summary>True when the typed message was a question taken by the quick run (the box is cleared, the answer comes).</summary>
    private bool QuickAnswer(string question)
    {
        if (_claude is not { } install || !ChatFocus.IsQuestion(question)) return false;
        _input.Clear();
        AddItem(new ChatItem { Kind = "user", Text = question, Time = DateTime.UtcNow });
        AddNote("Answering your question now; the work carries on.", Brand.K.Muted);
        Save();
        var conv = _conversation;
        var recent = conv.Items.Where(i => i.Kind is "user" or "assistant" && i.Text.Trim().Length > 0)
            .Reverse().Skip(1).Take(10).Reverse().Select(i => (i.Kind, i.Text)).ToList();
        var message = ChatFocus.QuickMessage(question, conv.Recall, recent, _crew?.Board.Snapshot() ?? []);
        _ = Task.Run(() => BriefRunner.Run(install, ChatFocus.QuickInstructions, message, readTools: true, "sonnet", QuickTimeoutMs, CancellationToken.None))
            .ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                var (text, error) = t.Status == TaskStatus.RanToCompletion ? t.Result : ("", "failed");
                if (!ReferenceEquals(conv, _conversation)) return;
                if (error.Length == 0 && text.Trim().Length > 0)
                {
                    AddItem(new ChatItem { Kind = "assistant", Text = text.Trim(), Time = DateTime.UtcNow });
                    _asides.Add((question, text.Trim()));
                    Save();
                    return;
                }
                // No quick answer: the question goes to the chat itself when this answer ends.
                _typedMeanwhile.Add(question + "\n\n[Plaxis ProMax, not typed by the user: " + ChatFocus.QuestionRule + "]");
                SendTypedMeanwhile();
            }), TaskScheduler.Default);
        return true;
    }

    /// <summary>The questions answered while the chat was busy, for its next message (and forgotten once said).</summary>
    private string TakeAsides()
    {
        var said = ChatFocus.Answered(_asides);
        _asides.Clear();
        return said;
    }

    // ---- Sign in ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The sign-in card: Sign in runs Claude Code's sign-in hidden and opens the browser; the card waits and carries on by
    /// itself when it is done. If the browser didn't open, the page can be opened again, and the code it shows pasted here.
    /// </summary>
    private void ShowSignInCard(ClaudeCli.Install install)
    {
        var signIn = Brand.Button("Sign in", Brand.Weight.Primary);
        var page = Brand.Button("Open the sign-in page");
        var again = Brand.Button("Check again");
        var state = Wrapped(ChatUi.Small(""));
        var codeBox = new TextBox { MinWidth = 180, Margin = new Thickness(0, 0, ChatUi.S2, 0), VerticalContentAlignment = VerticalAlignment.Center };
        codeBox.SetResourceReference(StyleProperty, "SpmChatField");
        AutomationProperties.SetName(codeBox, "The code from the sign-in page");
        var useCode = Brand.Button("Use this code");
        var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, ChatUi.S2, 0, 0), Visibility = Visibility.Collapsed };
        codeRow.Children.Add(codeBox);
        codeRow.Children.Add(useCode);
        var panel = new StackPanel();
        panel.Children.Add(Brand.Text("Sign in to Claude", Brand.K.Text, 14, bold: true));
        panel.Children.Add(Wrapped(ChatUi.Small("Sign in once with your Claude account (your Claude plan, no API key). The browser opens for it; come back here when it says you are signed in.")));
        panel.Children.Add(Brand.ButtonRow(signIn, page, again));
        panel.Children.Add(codeRow);
        panel.Children.Add(state);
        var card = Card(panel);
        _messages.Children.Add(card);
        page.Visibility = Visibility.Collapsed;
        var closed = false;

        void Flow()
        {
            if (closed) return;
            switch (SignInFlow.Now)
            {
                case SignInFlow.State.Waiting:
                    state.Text = "Finish signing in in your browser. If it didn't open, press Open the sign-in page, sign in, and paste the code it shows here.";
                    signIn.IsEnabled = false;
                    page.Visibility = codeRow.Visibility = Visibility.Visible;
                    break;
                case SignInFlow.State.Done:
                    Close();
                    _ = FindClaudeAsync();
                    break;
                case SignInFlow.State.Failed:
                    state.Text = "The sign-in didn't finish" + (SignInFlow.Problem.Length > 0 ? ": " + SignInFlow.Problem : ".") + " Press Sign in to try again.";
                    signIn.IsEnabled = true;
                    break;
                default:
                    signIn.IsEnabled = true;
                    break;
            }
        }
        void Changed() => Dispatcher.BeginInvoke(Flow);
        void Close()
        {
            if (closed) return;
            closed = true;
            SignInFlow.Changed -= Changed;
            _messages.Children.Remove(card);
        }
        SignInFlow.Changed += Changed;
        Closed += (_, _) => SignInFlow.Changed -= Changed;
        signIn.Click += (_, _) =>
        {
            try { SignInFlow.Start(install); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                state.Text = "Claude Code couldn't be started for the sign-in: " + ex.Message;
            }
        };
        page.Click += (_, _) =>
        {
            if (!SignInFlow.OpenPage()) state.Text = "The sign-in page isn't ready yet; try again in a few seconds, or press Sign in again.";
        };
        useCode.Click += (_, _) =>
        {
            if (SignInFlow.SendCode(codeBox.Text)) { state.Text = "Checking the code…"; codeBox.Clear(); }
            else state.Text = "Press Sign in first, then paste the code.";
        };
        again.Click += async (_, _) =>
        {
            SignInFlow.Cancel();
            Close();
            await FindClaudeAsync();
        };
        Flow();
    }
}
