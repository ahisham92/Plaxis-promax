using System.Diagnostics;
using System.Text;
using PlaxisProMax.Chat;

/// <summary>
/// Sketch ProMax's chat engine driving a stand-in Claude Code (a small script speaking stream-json): events arrive in
/// order through the window's post, a second message reuses the same process, and the exit is reported.
/// </summary>
internal static class ChatEngineTests
{
    private const string FakeClaude = """
        import json, sys
        print(json.dumps({"type": "system", "subtype": "init", "session_id": "s1", "model": "test", "mcp_servers": [{"name": "plaxis-promax", "status": "connected"}]}), flush=True)
        for line in sys.stdin:
            msg = json.loads(line)
            if msg.get("type") != "user":
                continue
            content = msg["message"]["content"]
            text = content if isinstance(content, str) else " ".join(c.get("text", "") for c in content if c.get("type") == "text")
            print(json.dumps({"type": "assistant", "message": {"id": "m1", "content": [{"type": "text", "text": "You said: " + text}]}}), flush=True)
            print(json.dumps({"type": "result", "subtype": "success", "is_error": False, "result": "You said: " + text, "session_id": "s1", "usage": {"input_tokens": 1, "output_tokens": 2}}), flush=True)
        """;

    public static void Run(Action<string, bool, string> check)
    {
        var script = Path.Combine(Path.GetTempPath(), "ppm-fake-claude-" + Guid.NewGuid().ToString("N") + ".py");
        File.WriteAllText(script, FakeClaude);
        try
        {
            var events = new List<ChatEvent>();
            var exited = new ManualResetEventSlim();
            var start = () => new ProcessStartInfo(TestPlaxis.Python, "-u " + ClaudeCli.Quote(script))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            var engine = new ChatEngine(a => a(), new ClaudeCli.Install("claude", "2.0.0"), null, "", start);
            engine.Event += e => { lock (events) events.Add(e); };
            engine.Exited += _ => exited.Set();
            engine.StartAsync(StreamInput.UserMessage("first", [])).GetAwaiter().GetResult();
            bool Wait(Func<bool> done) { var until = DateTime.UtcNow.AddSeconds(15); while (DateTime.UtcNow < until) { lock (events) if (done()) return true; Thread.Sleep(50); } return false; }
            check("chat engine: init then the answer then the result", Wait(() => events.OfType<ResultEvent>().Any())
                && events.OfType<InitEvent>().FirstOrDefault()?.SessionId == "s1"
                && events.OfType<AssistantTextEvent>().FirstOrDefault()?.Text == "You said: first", string.Join(" | ", events.Select(e => e.GetType().Name)));
            engine.SendAsync(StreamInput.UserMessage("second", [])).GetAwaiter().GetResult();
            check("chat engine: a second message goes to the same process", Wait(() => events.OfType<ResultEvent>().Count() == 2)
                && events.OfType<AssistantTextEvent>().Last().Text == "You said: second" && engine.IsAlive, "");
            engine.Kill();
            check("chat engine: killed, it is no longer alive", Wait(() => !engine.IsAlive), "");
        }
        finally { File.Delete(script); }
    }
}
