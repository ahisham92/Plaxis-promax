using System.Text.Json.Nodes;

namespace PlaxisProMax.Modeller;

/// <summary>
/// What the Modeller's and Viewer's ProMax panel shows of the chat, and how it writes to it. The chat window adds each
/// message and step here as it shows it; the page reads them (api/promax) and sends what is typed in its panel or its
/// command line back to the chat. An approval the chat is waiting for can be answered from the page too (Allow or Don't
/// allow). Nothing here talks to Plaxis.
/// </summary>
public static class ChatBridge
{
    private const int Keep = 120;
    private static readonly object Gate = new();
    private static readonly List<JsonObject> Items = [];
    private static long _seq;
    private static Func<string, bool>? _send;
    private static Action? _focus;
    private static bool _busy;
    private static string _chat = "";
    private static (long Id, string Title, string Reason, Action<bool> Answer)? _ask;

    /// <summary>The chat window: how text from the page is sent, and how the window is brought to the front.</summary>
    public static void Attach(Func<string, bool> send, Action focus)
    {
        lock (Gate) { _send = send; _focus = focus; }
    }

    public static bool Attached { get { lock (Gate) return _send is not null; } }

    /// <summary>Adds a line: kind is user, assistant, step or note. A step's state is running, done or failed.</summary>
    public static long Add(string kind, string text, string state = "", string tool = "", string? id = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        lock (Gate)
        {
            // A running step that ends is updated in place, so the panel shows one line per step.
            if (id is not null && Items.FindLast(i => (string?)i["key"] == id) is { } known)
            {
                known["text"] = Cut(text);
                known["state"] = state;
                known["seq"] = ++_seq;
                return _seq;
            }
            Items.Add(new JsonObject { ["seq"] = ++_seq, ["kind"] = kind, ["text"] = Cut(text), ["state"] = state, ["tool"] = tool, ["key"] = id, ["at"] = DateTime.UtcNow.ToString("o") });
            if (Items.Count > Keep) Items.RemoveRange(0, Items.Count - Keep);
            return _seq;
        }
    }

    public static void Busy(bool busy)
    {
        lock (Gate) { _busy = busy; _seq++; }
    }

    public static void ChatName(string name)
    {
        lock (Gate) { _chat = name; _seq++; }
    }

    /// <summary>The chat asks for an OK: the page shows it with Allow and Don't allow until it is answered either place.</summary>
    public static long Asking(string title, string reason, Action<bool> answer)
    {
        lock (Gate)
        {
            _ask = (++_seq, title, reason, answer);
            return _seq;
        }
    }

    public static void Answered(long id)
    {
        lock (Gate)
        {
            if (_ask is { } a && a.Id == id) { _ask = null; _seq++; }
        }
    }

    /// <summary>The panel's view: lines after <paramref name="since"/>, whether the chat works, and an OK it waits for.</summary>
    public static JsonObject Read(long since)
    {
        lock (Gate)
        {
            var o = new JsonObject
            {
                ["seq"] = _seq, ["attached"] = _send is not null, ["busy"] = _busy, ["chat"] = _chat,
                ["items"] = new JsonArray(Items.Where(i => (long)i["seq"]! > since).Select(i => (JsonNode)i.DeepClone()).ToArray()),
            };
            if (_ask is { } a) o["ask"] = new JsonObject { ["id"] = a.Id, ["title"] = a.Title, ["reason"] = a.Reason };
            return o;
        }
    }

    public static bool Send(string text)
    {
        Func<string, bool>? send;
        lock (Gate) send = _send;
        if (send is null) throw new InvalidOperationException("The chat window isn't open. Open Plaxis ProMax to ask it.");
        return send(text.Trim());
    }

    public static void Focus()
    {
        Action? focus;
        lock (Gate) focus = _focus;
        focus?.Invoke();
    }

    public static void Answer(long id, bool allow)
    {
        Action<bool>? answer = null;
        lock (Gate)
        {
            if (_ask is { } a && a.Id == id) { answer = a.Answer; _ask = null; _seq++; }
        }
        if (answer is null) throw new InvalidOperationException("That question was already answered in the chat.");
        answer(allow);
    }

    /// <summary>For the tests.</summary>
    internal static void Reset()
    {
        lock (Gate) { Items.Clear(); _seq = 0; _send = null; _focus = null; _busy = false; _chat = ""; _ask = null; }
    }

    private static string Cut(string text) => text.Length > 6000 ? text[..6000] + "…" : text;
}
