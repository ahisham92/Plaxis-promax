using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PlaxisProMax.Chat;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// Shrink chat and chats that recall each other (ChatRecall, from Sketch ProMax): the Shrink button and the automatic
/// shrink once a chat sends a lot with every message (Claude Code's /compact), and the short summary each chat keeps of
/// itself, written in the background when the chat is shrunk or left, which later chats about the same project start with.
/// </summary>
internal sealed partial class ChatWindow
{
    /// <summary>How much the last request of this chat sent (its context), from the stream; 0 when unknown.</summary>
    private long _contextTokens;
    private bool _shrinking, _autoShrink, _compacted;
    private Button? _shrink;
    private static Task? _remembering;
    private static readonly HashSet<string> RememberingIds = [];

    /// <summary>The "Shrink chat" icon button of the header.</summary>
    private Button ShrinkButton()
    {
        _shrink = ChatUi.IconButton(ChatUi.Paths.Shrink, "Shrink chat",
            "Shrink this chat: Plaxis ProMax replaces the earlier messages with a short summary and carries on, so each new message uses less of your plan. " +
            "It also happens by itself once a chat gets long.");
        _shrink.Margin = new Thickness(ChatUi.S1, 0, 0, 0);
        _shrink.Click += (_, _) => _ = Shrink(auto: false);
        return _shrink;
    }

    /// <summary>Shrink is offered when nothing runs and the chat has a Claude Code conversation to shrink.</summary>
    private void ShowShrink()
    {
        if (_shrink is null) return;
        var can = !_busy && _claude is not null && _conversation.SessionId.Length > 0;
        _shrink.IsEnabled = can || _sample;
        AutomationProperties.SetHelpText(_shrink, can ? (string)_shrink.ToolTip : "Shrink chat works once the chat has a conversation and Plaxis ProMax isn't answering.");
    }

    /// <summary>
    /// Runs /compact as its own turn: the conversation Claude Code keeps becomes a summary of the earlier messages (what
    /// <see cref="ChatRecall.CompactInstructions"/> names), the window keeps showing every message, and this chat's own
    /// summary for later chats is brought up to date.
    /// </summary>
    private async Task Shrink(bool auto)
    {
        if (_sample) return;
        if (_busy)
        {
            if (!auto) Note("Plaxis ProMax is still answering. Shrink the chat when it is done.");
            return;
        }
        if (_claude is null || _conversation.SessionId.Length == 0)
        {
            if (!auto) Note(_conversation.SessionId.Length == 0 ? "There is nothing to shrink yet: this chat has no conversation yet." : "Finish the step above first.");
            return;
        }
        _shrinking = true;
        _autoShrink = auto;
        _compacted = false;
        SetBusy(true);
        _working.Text = auto ? "This chat got long, so it is being shrunk to save usage…" : "Shrinking this chat…";
        _lastActivity = DateTime.UtcNow;
        try
        {
            await SendLineAsync(StreamInput.Compact(ChatRecall.CompactInstructions), "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            _shrinking = false;
            SetBusy(false);
            Error($"The chat could not be shrunk ({ex.Message}). It carries on as it was.");
        }
    }

    /// <summary>The end of a shrink turn: say what happened. True when <paramref name="end"/> was a shrink's.</summary>
    private bool ShrinkEnded(ResultEvent end)
    {
        if (!_shrinking) return false;
        _shrinking = false;
        var message = end.Text.Trim().Length > 0 ? end.Text.Trim() : string.Join(" ", end.Errors);
        var stopped = _stopping;
        _stopping = false;
        if (end.SessionId.Length > 0) _conversation.SessionId = end.SessionId;
        if (stopped) Note("Shrinking was stopped. The chat carries on as it was.");
        else if (end.IsError && message.IndexOf("No conversation found", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _conversation.SessionId = "";
            _engine?.Kill();
            _engine = null;
            Note("This chat couldn't be shrunk because its earlier conversation couldn't be reopened. Your next message starts fresh.");
        }
        else if (end.IsError) Note("The chat couldn't be shrunk" + (message.Length > 0 ? $" ({(message.Length > 200 ? message.Substring(message.Length - 200) : message)})" : "") + ". It carries on as it was.");
        else if (!_compacted) Note(message.Length > 0 ? message : "There wasn't enough in this chat to shrink yet.");
        _streams.Clear();
        SetBusy(false);
        Save();
        if (_compacted) Remember(_conversation);
        SendQueuedFromPhone();
        return true;
    }

    /// <summary>The compaction itself (from the stream): the conversation sent with each message is now a summary.</summary>
    private void Compacted(CompactedEvent e)
    {
        _contextTokens = 0;
        _compacted = true;
        var shrinking = _shrinking;
        if (shrinking)
        {
            // The shrink's result normally follows at once; should it never come, the window doesn't stay busy.
            var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            wait.Tick += (_, _) =>
            {
                wait.Stop();
                if (!_shrinking) return;
                _shrinking = false;
                SetBusy(false);
                Save();
                Remember(_conversation);
            };
            wait.Start();
        }
        var before = e.PreTokens > 0 ? $" (it was about {Math.Round(e.PreTokens / 1000.0):0}k tokens per message)" : "";
        if (shrinking && _autoShrink)
            Note("This chat got long, so it was shrunk to save usage" + before + ". Plaxis ProMax keeps a summary of the earlier messages; they stay on screen.");
        else if (shrinking)
            Note("Chat shrunk" + before + ". Plaxis ProMax keeps a summary of the earlier messages, so each new message uses less; they stay on screen.");
        else
            Note("The conversation was long, so Plaxis ProMax summarized the earlier part to keep going.");
    }

    /// <summary>What the last request sent: the chat's size now, and the smallest it has been (what a shrink can't go under).</summary>
    private void ContextNow(long tokens)
    {
        if (tokens <= 0) return;
        _contextTokens = tokens;
        _conversation.ContextFloor = ChatRecall.Floor(_conversation.ContextFloor, tokens);
    }

    /// <summary>After a finished turn: shrink by itself when the chat now sends ChatRecall.AutoShrinkTokens more than its floor.</summary>
    private void ShrinkIfLong(bool stopped, bool failed)
    {
        ShowShrink();
        if (stopped || failed) return;
        if (!ChatRecall.ShouldAutoShrink(_contextTokens, _conversation.ContextFloor, IsCoordinator)) return;
        // After what the turn's end still does (a message from the phone, a run's next step: a turn they start comes first).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!_busy) _ = Shrink(auto: true); }));
    }

    // ---- What each chat remembers -----------------------------------------------------------------------------------

    /// <summary>
    /// Brings a chat's summary up to date in the background when it has new messages: a short Claude run (the quickest
    /// model) over its text. The summary is saved with the chat, without moving it up the history.
    /// </summary>
    private void Remember(Conversation conv)
    {
        if (_sample || _claude is not { } install || conv.Id.Length == 0 || !ChatRecall.NeedsSummary(conv)) return;
        lock (RememberingIds)
            if (!RememberingIds.Add(conv.Id)) return;
        var id = conv.Id;
        var file = conv.File;
        var count = conv.Items.Count;
        var transcript = ChatRecall.Transcript(conv);
        var previous = _remembering;
        var task = Task.Run(async () =>
        {
            try
            {
                // One at a time: two summaries of the same chat must not race.
                if (previous is not null) await previous.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
                var (text, error) = await BriefRunner.Run(install, ChatRecall.SummaryInstructions, transcript, readTools: false,
                    ChatRecall.SummaryModel, BriefRunner.DistilTimeoutMs, CancellationToken.None).ConfigureAwait(false);
                // A plan without the quick model: the plan's default.
                if (error.Length > 0 && ChatModels.IsRejection(error))
                    (text, error) = await BriefRunner.Run(install, ChatRecall.SummaryInstructions, transcript, readTools: false,
                        null, BriefRunner.DistilTimeoutMs, CancellationToken.None).ConfigureAwait(false);
                if (error.Length > 0) return; // tried again when the chat next changes
                var summary = ChatRecall.CleanSummary(text);
                await Dispatcher.BeginInvoke(new Action(() => SetRecall(id, file, summary, count)));
            }
            catch (Exception) { /* the chat works without its summary */ }
            finally
            {
                lock (RememberingIds) RememberingIds.Remove(id);
            }
        });
        _remembering = task;
    }

    /// <summary>Saves a chat's new summary: the open chat in place, any other in its file. An empty one only marks it done.</summary>
    private void SetRecall(string id, string file, string summary, int count)
    {
        void Apply(Conversation c)
        {
            if (summary.Length > 0) c.Recall = summary;
            c.RecallCount = Math.Min(count, c.Items.Count);
        }
        try
        {
            if (_conversation.Id == id)
            {
                Apply(_conversation);
                ChatStore.Save(_conversation, touch: false);
            }
            else if (file.Length > 0 && ChatStore.Read(file) is { } saved && saved.Id == id)
            {
                Apply(saved);
                ChatStore.Save(saved, touch: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (summary.Length > 0) RecallChanged?.Invoke();
    }

    /// <summary>
    /// Once Claude Code is ready: this project's chats left with new messages since their summary (the window closed
    /// while one was open, say) are summarised now, so the next new chat starts knowing them.
    /// </summary>
    private void RememberLeftOver()
    {
        if (_sample) return;
        var key = _conversation.Key;
        var current = _conversation.Id;
        Task.Run(() => ChatStore.ForModel(key).Where(c => c.Id != current && ChatRecall.NeedsSummary(c)).Take(3).ToList())
            .ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                Dispatcher.BeginInvoke(new Action(() => { foreach (var c in t.Result) Remember(c); }));
            }, TaskScheduler.Default);
    }

    /// <summary>A new chat's first message waits (a little) for the summary of the chat just left, so the new chat starts with it.</summary>
    private async Task WaitForMemory()
    {
        if (_remembering is not { IsCompleted: false } pending) return;
        _working.Text = "Remembering your last chat for this one…";
        await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(30)));
        if (_busy) _working.Text = "Working…";
    }

    /// <summary>For the screenshots: a chat that was just shrunk.</summary>
    internal void ShowSampleShrunk()
    {
        ShowSample();
        _input.Clear();
        Note("Chat shrunk (it was about 118k tokens per message). Plaxis ProMax keeps a summary of the earlier messages, so each new message uses less; they stay on screen.");
    }
}
