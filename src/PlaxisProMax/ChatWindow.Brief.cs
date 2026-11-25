using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using PlaxisProMax.Chat;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// The brief (Sketch ProMax's prompt enhancer, ChatPane.Brief): before a task that changes the project (or every
/// message, per the setting), a planning run of Claude Code reads the project with read-only tools and turns the message
/// into a precise brief, shown as a card with Run this, Edit and Run as I typed. Nothing reaches the chat (and so the
/// project) until one is picked. An edited brief is distilled into a "Brief preference" note, so later briefs need fewer
/// edits. Also here: what goes with a correction or a plain question (ChatFocus.Hint), and the lesson kept from a
/// correction.
/// </summary>
internal sealed partial class ChatWindow
{
    private BriefMode _briefMode = PromptBriefs.LoadMode(ClaudeCli.Folder);
    private readonly Dictionary<ChatItem, Action> _briefCards = [];
    private CancellationTokenSource? _planning;

    // ---- Planning -------------------------------------------------------------------------------------------------

    /// <summary>Starts the planning run when the setting says so; false when the message should run as typed.</summary>
    private bool StartBrief(string typed, string sent, List<string> files)
    {
        if (_sample || _claude is not { } install || !PromptBriefs.ShouldPlan(_briefMode, typed)) return false;
        // A correction of the last answer, or a plain question: it runs at once (ChatFocus).
        if (FocusHint(typed, files.Count > 0).Length > 0) return false;
        var cancel = _planning = new CancellationTokenSource();
        SetBusy(true);
        _lastActivity = DateTime.UtcNow;
        _working.Text = "Reading the project to write a brief. Nothing changes yet…";
        // The end of the conversation (before this message), so a follow-up is briefed in its context.
        var recent = _conversation.Items.Take(_conversation.Items.Count - 1).Where(i => i.Kind is "user" or "assistant" && i.Text.Length > 0)
            .Reverse().Take(6).Reverse().Select(i => (i.Kind, i.Text)).ToList();
        var message = PromptBriefs.PlanMessage(sent, files.Select(Path.GetFileName).OfType<string>(), recent, _conversation.Recall);
        var pick = ChatJobs.Pick(typed, _conversation.Model, _conversation.Effort);
        _ = PlanAndShow(cancel, install, sent, message, pick.Model, _briefMode, files);
        return true;
    }

    private async Task PlanAndShow(CancellationTokenSource cancel, ClaudeCli.Install install, string text, string message, string model, BriefMode mode, List<string> files)
    {
        var conv = _conversation;
        (PromptBrief? Brief, string Error) result;
        try { result = await Task.Run(() => BriefRunner.Plan(install, text, message, model, cancel.Token)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { result = (null, ex.Message); }
        if (!ReferenceEquals(_planning, cancel)) return; // stopped meanwhile
        _planning = null;
        cancel.Dispose();
        SetBusy(false);
        if (result.Brief is { } plain && !PromptBriefs.ShowCard(mode, plain))
        {
            if (ReferenceEquals(_conversation, conv)) RunText(text); // a question: it runs as typed
            return;
        }
        var item = new ChatItem { Kind = "brief", Files = files, State = result.Brief is null ? "failed" : "pending", Time = DateTime.UtcNow };
        var brief = result.Brief ?? new PromptBrief { Request = text, Prompt = text };
        item.Brief = PromptBriefs.ToJson(brief);
        item.Text = result.Brief is null ? result.Error : PromptBriefs.Compose(brief);
        if (!ReferenceEquals(_conversation, conv))
        {
            // The chat was switched meanwhile: the card stays in its own chat.
            conv.Items.Add(item);
            try { ChatStore.Save(conv); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return;
        }
        AddItem(item);
        Save();
        ChatUi.FlashIfAway(this);
    }

    /// <summary>Stop while the brief is written: nothing was sent, so nothing changed.</summary>
    private bool StopBrief()
    {
        if (_planning is not { } cancel) return false;
        _planning = null;
        cancel.Cancel();
        SetBusy(false);
        Note("Stopped writing the brief. Nothing in the project changed. Send again, or turn briefs off in the ⋯ menu.");
        return true;
    }

    /// <summary>A newer message replaces any brief still waiting for a choice.</summary>
    private void SupersedeBriefs()
    {
        foreach (var item in _conversation.Items.Where(i => i.Kind == "brief" && i.State is "pending" or "failed"))
        {
            item.State = "stale";
            if (_briefCards.TryGetValue(item, out var render)) render();
        }
    }

    /// <summary>Sends <paramref name="text"/> to the chat as its next turn (the user's message is already shown).</summary>
    private void RunText(string text, bool approved = false)
    {
        if (_claude is null) return;
        _mcp().NewTask();
        if (approved) _mcp().ApproveTask();
        _strip.Heard();
        SetBusy(true);
        _turnStart = DateTime.UtcNow;
        _lastActivity = DateTime.UtcNow;
        _ = SendAsync(text + TakeAsides() + (IsCoordinator && _crew is { } crew ? JobTruth.Note(crew.Board.Snapshot()) : ""));
    }

    // ---- The card -------------------------------------------------------------------------------------------------

    /// <summary>The brief card: what was understood, one-tap questions, then Run this (primary), Edit and Run as I typed.</summary>
    private UIElement BriefCard(ChatItem item)
    {
        var holder = new ContentControl { Focusable = false, Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1) };
        var brief = PromptBriefs.FromStored(item.Brief) ?? new PromptBrief { Request = item.Text, Prompt = item.Text };
        var editing = false;
        TextBox? editor = null;

        Button Pill(string text, bool primary, string help, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S1), ToolTip = help };
            b.SetResourceReference(StyleProperty, primary ? "SpmChatPrimary" : "SpmChatPill");
            AutomationProperties.SetName(b, text);
            AutomationProperties.SetHelpText(b, help);
            b.Click += (_, _) => click();
            return b;
        }

        void Render()
        {
            var panel = new StackPanel();
            var pending = item.State == "pending";
            var failed = item.State == "failed";

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, ChatUi.S1) };
            var icon = ChatUi.Icon(ChatUi.Paths.Sparkle, 14);
            icon.Margin = new Thickness(0, 0, ChatUi.S2, 0);
            DockPanel.SetDock(icon, Dock.Left);
            head.Children.Add(icon);
            var kind = brief.Kind switch { "change" => "Changes the project", "multi_step" => "Several steps", "read_only" => "Reads only", _ => "May change the project" };
            var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            Brand.Fg(title, Brand.K.Text);
            title.Inlines.Add(new Run("Brief") { FontWeight = FontWeights.SemiBold });
            if (!failed) title.Inlines.Add(new Run(" · " + kind) { FontSize = ChatUi.SmallSize });
            head.Children.Add(title);
            panel.Children.Add(head);

            var line = item.State switch
            {
                "pending" => "Nothing changes in the project until you choose.",
                "run" => "You ran this brief.",
                "edited" => "You ran your edited brief.",
                "typed" => "You ran your message as typed; this brief wasn't used.",
                "failed" => "No brief this time: " + item.Text.TrimEnd('.') + ". Nothing in the project changed.",
                _ => "Not run: a newer message replaced it.",
            };
            var sub = Wrapped(Brand.Text(line, Brand.K.Muted, ChatUi.SmallSize));
            sub.Margin = new Thickness(0, 0, 0, ChatUi.S2);
            panel.Children.Add(sub);

            if (!failed && !editing)
            {
                var view = new MarkdownView();
                view.SetMarkdown(item.Text);
                view.SetMuted(item.State is "typed" or "stale");
                panel.Children.Add(view);
            }

            // One-tap questions: the suggested option is picked until another is tapped; the brief above updates.
            if (pending && !editing)
            {
                foreach (var q in brief.Questions)
                {
                    var ask = Wrapped(Brand.Text(q.Text, Brand.K.Text, ChatUi.SmallSize, bold: true));
                    ask.Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1);
                    panel.Children.Add(ask);
                    var options = new WrapPanel();
                    for (var k = 0; k < q.Options.Count; k++)
                    {
                        var picked = k == q.Effective;
                        var at = k;
                        var label = q.Options[k] + (k == q.Recommended && q.Chosen < 0 ? " (suggested)" : "");
                        var option = new Button { Content = new TextBlock { Text = (picked ? "✓ " : "") + label, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, Margin = new Thickness(0, 0, ChatUi.S1, ChatUi.S1) };
                        option.SetResourceReference(StyleProperty, "SpmChatPill");
                        if (picked)
                        {
                            option.SetResourceReference(Control.BackgroundProperty, Brand.K.AccentSoft);
                            option.SetResourceReference(Control.BorderBrushProperty, Brand.K.Accent);
                        }
                        AutomationProperties.SetName(option, q.Text + ": " + q.Options[k]);
                        AutomationProperties.SetItemStatus(option, picked ? "Selected" : "");
                        option.ToolTip = picked ? "Picked. The brief above uses it." : "Use this answer in the brief.";
                        option.Click += (_, _) =>
                        {
                            if (item.State != "pending" || !PromptBriefs.Answer(brief, q.Id, at)) return;
                            item.Brief = PromptBriefs.ToJson(brief);
                            item.Text = PromptBriefs.Compose(brief);
                            Save();
                            Render();
                        };
                        options.Children.Add(option);
                    }
                    panel.Children.Add(options);
                }
            }

            var row = new WrapPanel { Margin = new Thickness(0, ChatUi.S3, 0, 0) };
            if (editing)
            {
                editor = new TextBox
                {
                    Text = failed ? brief.Request : item.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 280,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    SpellCheck = { IsEnabled = true }, FontSize = ChatUi.BodySize, Padding = new Thickness(ChatUi.S2),
                };
                editor.SetResourceReference(StyleProperty, "SpmChatField");
                AutomationProperties.SetName(editor, "Edit the brief");
                AutomationProperties.SetHelpText(editor, "Change the brief, then Run edited brief (Ctrl+Enter). Escape cancels.");
                editor.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; RunEdited(); }
                    else if (e.Key == Key.Escape) { e.Handled = true; editing = false; Render(); }
                };
                panel.Children.Add(editor);
                row.Children.Add(Pill("Run edited brief", true, "Runs the brief as you edited it (Ctrl+Enter). Your change is remembered for later briefs.", RunEdited));
                row.Children.Add(Pill("Cancel", false, "Back to the brief without running anything.", () => { editing = false; Render(); }));
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    editor?.Focus();
                    if (editor is not null) editor.CaretIndex = editor.Text.Length;
                });
            }
            else if (pending)
            {
                row.Children.Add(Pill("Run this", true, "Runs the brief above, with your answers. Running it approves the task: its steps don't ask again, except risky ones.", () => Choose(item, brief, BriefChoice.RunBrief, null, Render)));
                row.Children.Add(Pill("Edit", false, "Opens the brief as text to change before running it.", () => { editing = true; Render(); }));
                row.Children.Add(Pill("Run as I typed", false, "Runs your message exactly as you typed it, without the brief.", () => Choose(item, brief, BriefChoice.RunAsTyped, null, Render)));
            }
            else if (failed)
            {
                row.Children.Add(Pill("Run as I typed", true, "Runs your message exactly as you typed it.", () => Choose(item, brief, BriefChoice.RunAsTyped, null, Render)));
                row.Children.Add(Pill("Edit", false, "Opens your message as text to change before running it.", () => { editing = true; Render(); }));
            }
            else if (item.State is "run" or "edited")
            {
                var save = ChatUi.Ghost(ChatUi.GlyphPrompts, "Save to my prompts", "Save this brief to my prompts",
                    "Keeps this brief in your saved prompts, to use again from the bookmark button by the message box or the home screen.");
                save.Click += (_, _) =>
                {
                    try { SavedPrompts.Update(SavedPrompts.DefaultFile, l => SavedPrompts.Add(l, item.Text, DateTime.UtcNow)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
                    _home.SavedChanged();
                    save.IsEnabled = false;
                };
                row.Children.Add(save);
                row.Children.Add(ChatUi.CopyButton(() => item.Text, "the brief"));
            }
            if (row.Children.Count > 0) panel.Children.Add(row);

            var card = ChatUi.Elevated(panel, out var frame);
            frame.Padding = new Thickness(ChatUi.S3 + 2, ChatUi.S3, ChatUi.S3 + 2, ChatUi.S2);
            AutomationProperties.SetName(card, "Brief: " + line);
            if (pending || failed) AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Polite);
            holder.Content = card;
        }

        void RunEdited()
        {
            var text = editor?.Text ?? "";
            if (text.Trim().Length == 0)
            {
                editor?.Focus();
                return;
            }
            var wasFailed = item.State == "failed";
            // Edit on a card without a brief: the user's own message, changed. Nothing to learn from.
            Choose(item, brief, wasFailed ? BriefChoice.RunAsTyped : BriefChoice.RunEdited, text, () => { editing = false; Render(); }, learn: !wasFailed);
        }

        _briefCards[item] = Render;
        Render();
        return holder;
    }

    /// <summary>Runs what the user picked on a brief card. Only now does anything reach Claude Code (and the project).</summary>
    private void Choose(ChatItem item, PromptBrief brief, BriefChoice choice, string? edited, Action render, bool learn = true)
    {
        if (_sample || item.State is not ("pending" or "failed")) return;
        if (_busy)
        {
            Note("Plaxis ProMax is still answering. Wait for it, or press Stop, then choose again.");
            return;
        }
        if (_claude is null)
        {
            Note("Claude Code isn't ready yet: install it or sign in with the card above, then choose again.");
            return;
        }
        var shown = item.Text;
        var text = edited is not null && choice == BriefChoice.RunAsTyped ? edited.Trim() : PromptBriefs.TextFor(choice, brief, edited);
        item.State = choice switch { BriefChoice.RunBrief => "run", BriefChoice.RunEdited => "edited", _ => "typed" };
        if (choice == BriefChoice.RunEdited) item.Text = text;
        item.Brief = PromptBriefs.ToJson(brief);
        render();
        Save();
        if (choice == BriefChoice.RunEdited && learn && PromptBriefs.IsEdit(shown, text)) RememberEdit(brief.Request, shown, text);
        // Running a brief approves the task: its steps don't ask again, except what always asks.
        RunText(text, approved: choice != BriefChoice.RunAsTyped);
    }

    /// <summary>Distils the edit into a note in the background, and says what was kept.</summary>
    private void RememberEdit(string request, string brief, string edited)
    {
        var install = _claude;
        var model = _lastPick?.Model;
        Task.Run(() => BriefRunner.RememberEdit(install, request, brief, edited, model)).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result.Length == 0) return;
            var note = t.Result.StartsWith(PromptBriefs.NotePrefix, StringComparison.Ordinal) ? t.Result.Substring(PromptBriefs.NotePrefix.Length) : t.Result;
            Dispatcher.BeginInvoke(() => Note("Remembered for later briefs: " + note + " (edit or delete it in Notes)."));
        }, TaskScheduler.Default);
    }

    // ---- Corrections, questions and lessons ---------------------------------------------------------------------------

    /// <summary>What goes with a typed message for a correction, a repeat or a plain question (ChatFocus.Hint); "" in an autopilot.</summary>
    private string FocusHint(string text, bool attachments)
    {
        if (_crew is { Run.State: AutopilotRun.Running or AutopilotRun.Planning or AutopilotRun.Approval }) return "";
        var answered = _conversation.Items.Any(i => i.Kind == "assistant" && i.Text.Length > 0);
        return ChatFocus.Hint(text, -1, attachments, answered);
    }

    /// <summary>
    /// A correction of the last answer is distilled into one lesson in the background and kept in the notes, so the same
    /// thing isn't said twice; the chat says what it kept.
    /// </summary>
    private void LearnFrom(string correction)
    {
        if (_sample || _claude is not { } install || !ChatFocus.IsCorrection(correction)) return;
        if (_crew is { Run.State: AutopilotRun.Running }) return;
        // The answer being corrected, and the request it answered (the message before this one is the last in the list).
        var before = _conversation.Items.Take(_conversation.Items.Count - 1).ToList();
        var answer = before.LastOrDefault(i => i.Kind == "assistant" && i.Text.Trim().Length > 0);
        if (answer is null) return;
        var request = before.Take(before.IndexOf(answer)).LastOrDefault(i => i.Kind == "user" && i.Text.Trim().Length > 0)?.Text ?? "";
        var (model, key, conv) = (_lastPick?.Model, _conversation.Key, _conversation);
        Task.Run(() => BriefRunner.RememberLesson(install, request, answer.Text, correction, key, model)).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result.Length == 0) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_conversation, conv)) Note(ChatFocus.Saved(t.Result));
            });
        }, TaskScheduler.Default);
    }

    // ---- The setting (in the ⋯ menu) -----------------------------------------------------------------------------------

    /// <summary>"Brief before running: Always / Project changes only / Off", in the ⋯ menu.</summary>
    private MenuItem BriefMenu()
    {
        var menu = new MenuItem { Header = "Brief before running: " + PromptBriefs.Label(_briefMode), ToolTip = "Before a task, Plaxis ProMax reads the project and writes a brief for you to check, edit or skip." };
        foreach (var mode in new[] { BriefMode.Always, BriefMode.ModelChanges, BriefMode.Off })
        {
            var chosen = mode;
            var item = new MenuItem { Header = PromptBriefs.Label(mode), ToolTip = PromptBriefs.Hint(mode), IsCheckable = true, IsChecked = mode == _briefMode };
            item.Click += (_, _) =>
            {
                _briefMode = chosen;
                PromptBriefs.SaveMode(ClaudeCli.Folder, chosen);
                Note("Brief before running: " + PromptBriefs.Label(chosen) + ". " + PromptBriefs.Hint(chosen) + ".");
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>For the screenshots: a request with its brief waiting for a choice.</summary>
    internal void ShowSampleBrief()
    {
        _plaxisState.Text = "PLAXIS 2D 2024.2 · Harbour wall";
        _conversation.Name = "Excavation stages";
        ShowTitle();
        ShowItem(new ChatItem { Kind = "user", Text = "add the excavation stages down to -6 with the anchors", Time = DateTime.UtcNow });
        var brief = new PromptBrief
        {
            Request = "add the excavation stages down to -6 with the anchors", Kind = "change", Units = "m, kN",
            Prompt = "Add the staged excavation to **Harbour wall** after the phase *Install wall*:\n\n1. Excavate to -2.0 m, then install anchor row 1 at -1.5 m with a prestress of {q1}.\n2. Excavate to -4.0 m, then install anchor row 2 at -3.5 m with the same prestress.\n3. Excavate to -6.0 m (final level).\n\nLower the water inside the excavation to each excavation level. Name each phase by what it does. Don't calculate yet.",
            Scope = [new BriefScope { Label = "Phases", Value = "Initial phase, Install wall (2 phases now, 5 to add)" }, new BriefScope { Label = "Anchors", Value = "Row 1 at -1.5 m, row 2 at -3.5 m (both exist, not yet active)" }],
            Assumptions = ["The anchors are prestressed in the phase they are installed in.", "Water inside follows the excavation level."],
            Questions = [new BriefQuestion { Id = "q1", Text = "Which prestress for the anchors?", Options = ["150 kN/m", "200 kN/m", "None"], Recommended = 0 }],
        };
        var item = new ChatItem { Kind = "brief", State = "pending", Brief = PromptBriefs.ToJson(brief), Text = PromptBriefs.Compose(brief) };
        _messages.Children.Add(BriefCard(item));
    }
}
