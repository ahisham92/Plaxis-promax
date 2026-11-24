using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using PlaxisProMax.Chat;
using PlaxisProMax.Mcp;
using PlaxisProMax.Phone;
using PlaxisProMax.Settings;
using PlaxisProMax.UI;

namespace PlaxisProMax;

/// <summary>
/// The phone, through Iris (Sketch ProMax's phone link, Phone/PhoneLink): the Phone switch under the message box, as in
/// Sketch ProMax. On when the user leaves the PC: approval cards go to the phone with one-tap answers, every reply goes
/// there, and what they write there (with pictures) reaches this project's chat; Stop on the phone stops the chat. Off
/// while they work here: nothing reaches the phone. Each Plaxis project has its own chat on the phone. Network calls
/// never run on the window's thread.
/// </summary>
internal sealed partial class ChatWindow
{
    private readonly TextBlock _phoneLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _phoneDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<long, Action<ApprovalAnswer, string>> _phoneCards = [];
    private readonly Queue<string> _fromPhone = new();
    private readonly DispatcherTimer _phoneTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private Button? _phonePill;
    private bool _reading;
    private DateTime _claimedAt;

    /// <summary>The approval card's answers on the phone, in the order of its buttons.</summary>
    internal static readonly string[] PhoneChoices = ["Allow", "Allow the rest of this task", "Don't allow"];

    private Button PhonePill()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_phoneDot);
        row.Children.Add(_phoneLabel);
        var pill = new Button { Content = row, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(0, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(StyleProperty, "SpmChatGhost");
        pill.Click += (_, _) => PhoneClicked();
        PhoneLink.Changed += () => Dispatcher.BeginInvoke(ShowPhone);
        _phonePill = pill;
        ShowPhone();
        return pill;
    }

    private void StartPhone()
    {
        _phoneTimer.Tick += async (_, _) => await ReadPhoneAsync();
        _phoneTimer.Start();
        Closed += (_, _) => _phoneTimer.Stop();
    }

    private void PhoneClicked()
    {
        if (!PhoneLink.Connected)
        {
            var window = new PhoneConnectWindow() { Owner = this };
            window.Show();
            return;
        }
        var on = !PhoneLink.On;
        PhoneLink.SetOn(on);
        Note(on ? "Phone on: approvals and replies go to your phone, and you can write to this chat from it."
                : "Phone off: nothing goes to your phone while you work here.");
    }

    private void ShowPhone()
    {
        if (_phonePill is null) return;
        var connected = PhoneLink.Connected;
        var on = connected && PhoneLink.On;
        _phoneLabel.Text = !connected ? "Phone: connect" : on ? "Phone: on" : "Phone: off";
        Brand.Fg(_phoneLabel, on ? Brand.K.Text : Brand.K.Muted);
        _phoneDot.SetResourceReference(Shape.FillProperty, on ? Brand.K.SuccessText : Brand.K.Muted);
        var tip = !connected ? "Connect your phone to get approvals and replies there, and write to this chat from it."
            : on ? "On: approvals and replies go to your phone. Click when you're back at the PC to stop them."
            : "Off: nothing goes to your phone. Click before you leave the PC.";
        _phonePill.ToolTip = tip;
        AutomationProperties.SetName(_phonePill, _phoneLabel.Text);
        AutomationProperties.SetHelpText(_phonePill, tip);
    }

    /// <summary>This project's chat on the phone: its file's name, as Sketch ProMax names a model's (Phone/PhoneProjects).</summary>
    private string PhoneTopic()
    {
        var file = _link.LastStatus?["filename"]?.ToString();
        return PhoneProjects.Of(file, _conversation.Name, Environment.UserName, AgentSettings.Load().PhoneProjectNames);
    }

    /// <summary>An approval card goes to the phone too while it is on; a tap there answers it here.</summary>
    private void AskOnPhone(ApprovalRequest request, Action<long> sent, Action<ApprovalAnswer, string> answer)
    {
        if (!PhoneLink.Active) return;
        var topic = PhoneTopic();
        var details = request.Details.Length > 1500 ? request.Details.Substring(0, 1500) + "\n…" : request.Details;
        var text = $"{request.Title}: {request.Reason}" + (details.Length > 0 ? "\n\n" + details : "");
        _ = Task.Run(async () =>
        {
            var (id, error) = await PhoneLink.AskAsync(text, "", null, topic, PhoneChoices);
            await Dispatcher.InvokeAsync(() =>
            {
                if (id == 0) { Note($"The approval didn't reach your phone ({error})."); return; }
                _phoneCards[id] = answer;
                sent(id);
            });
        });
    }

    /// <summary>Answered on the PC (or stopped): the question on the phone is closed with what happened.</summary>
    private void SettleOnPhone(ref long phoneId, string said)
    {
        if (phoneId == 0) return;
        _phoneCards.Remove(phoneId);
        var id = phoneId;
        phoneId = 0;
        _ = Task.Run(() => PhoneLink.SettleAsync(id, said));
    }

    /// <summary>The reply of the turn that just ended goes to this project's chat on the phone.</summary>
    private void ReplyToPhone(string text)
    {
        text = text.Trim();
        if (!PhoneLink.Active || text.Length == 0) return;
        if (text.Length > 4000) text = text.Substring(0, 4000) + "\n… (the rest is in Plaxis ProMax)";
        var topic = PhoneTopic();
        _ = Task.Run(async () =>
        {
            var error = await PhoneLink.UpdateAsync(text, topic);
            if (error is not null) await Dispatcher.InvokeAsync(() => Note($"The reply didn't reach your phone ({error})."));
        });
    }

    /// <summary>A message written on the phone while the chat worked: sent when the turn ends.</summary>
    private void SendQueuedFromPhone()
    {
        if (_busy || _fromPhone.Count == 0) return;
        var text = string.Join("\n\n", _fromPhone);
        _fromPhone.Clear();
        Send(text, typed: false);
    }

    /// <summary>Reads the phone: answers to approval cards, messages for this project's chat, and Stop.</summary>
    private async Task ReadPhoneAsync()
    {
        if (_reading || !PhoneLink.Active) return;
        _reading = true;
        try
        {
            var now = DateTime.UtcNow;
            var topic = PhoneTopic();
            if (now - _claimedAt > TimeSpan.FromMinutes(1))
            {
                _claimedAt = now;
                await Task.Run(() => PhoneLink.Claim([topic], now));
            }
            var takes = await Task.Run(() => PhoneLink.TakesMessages(now));
            var got = await Task.Run(PhoneLink.InboxAsync);
            foreach (var m in got ?? [])
            {
                var text = m.Text.Trim();
                if (m.Error.Length > 0) continue;
                if (m.Kind == "answer" && m.ReplyTo is { } to && _phoneCards.TryGetValue(to, out var answer))
                {
                    _phoneCards.Remove(to);
                    var pick = Array.FindIndex(PhoneChoices, c => c.Equals(text, StringComparison.OrdinalIgnoreCase));
                    answer(pick switch { 0 => ApprovalAnswer.Allow, 1 => ApprovalAnswer.AllowRestOfTask, _ => ApprovalAnswer.Deny },
                           pick switch { 0 => "Allowed.", 1 => "Allowed for the rest of this task.", _ => "Not allowed." });
                    continue;
                }
                if (m.Kind is "command" or "message" && !PhoneLink.TakesTopic(m.Topic, takes, now)) continue;
                if (m.Kind == "command")
                {
                    if (text == "Stop" && _busy) { Note("Stopped from your phone."); Stop(); }
                    continue;
                }
                if (m.Kind != "message") continue;
                if (m.Image.Length > 0 && PhonePictures.Save(m.Image, System.IO.Path.Combine(ChatMcp.OutputFolder, "Phone"), now) is { } picture)
                    text += (text.Length > 0 ? "\n\n" : "") + "Attached files:\n- " + picture;
                if (text.Length == 0) continue;
                _fromPhone.Enqueue(text);
            }
            SendQueuedFromPhone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* the next tick tries again */ }
        finally { _reading = false; }
    }
}
