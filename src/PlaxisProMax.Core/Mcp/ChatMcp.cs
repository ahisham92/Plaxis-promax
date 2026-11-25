using System.Text.Json;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

namespace PlaxisProMax.Mcp;

/// <summary>What the window answers on an approval card.</summary>
public enum ApprovalAnswer { Deny, Allow, AllowRestOfTask, DontAskAgain }

/// <summary>One approval card: the tool in plain words, the one-line reason, and the details folded away (the code, the commands).</summary>
public sealed record ApprovalRequest(string Tool, string Title, string Reason, string Details, bool CanSilence);

/// <summary>Shows an approval card and waits for the answer. The window implements it; it must never block its own UI thread.</summary>
public interface IApprovalAsker
{
    Task<ApprovalAnswer> AskAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>
/// The chat's MCP server, inside the window: <see cref="McpHttpServer"/> (from Sketch ProMax) serving the Plaxis tools,
/// with the approval policy in front of every call that changes the project. One per window.
/// </summary>
internal sealed class ChatMcp : IDisposable
{
    /// <summary>The window's running server, which ClaudeCli writes into the chat's MCP config.</summary>
    public static McpHttpServer? Current { get; private set; }

    private readonly ToolRegistry _registry;
    private readonly ToolContext _context;
    private readonly IApprovalAsker _asker;
    private readonly McpHttpServer _server;
    private volatile bool _taskApproved;

    public ChatMcp(ToolRegistry registry, ToolContext context, IApprovalAsker asker, string version)
    {
        _registry = registry;
        _context = context;
        _asker = asker;
        var endpoint = new McpEndpoint("plaxis-promax", version, Instructions, _ => Tools(), CallAsync);
        _server = new McpHttpServer(endpoint);
    }

    public McpHttpServer Server => _server;

    public void Start()
    {
        _server.Start();
        Current = _server;
    }

    /// <summary>A new user message: "Allow the rest of this task" no longer covers what follows.</summary>
    public void NewTask() => _taskApproved = false;

    /// <summary>Sent to Claude Code at initialize: what is kept, so every chat sees the kept code (Sketch ProMax bug 128).</summary>
    private string Instructions()
    {
        var catalog = _context.Library.Catalog();
        return catalog.Length == 0
            ? "Plaxis ProMax tools. No kept code yet: code that works is kept by run_plaxis_code."
            : "Plaxis ProMax tools. Kept code (run with run_plaxis_code kept_id; find more with find_kept_code):\n" + catalog;
    }

    private IReadOnlyList<McpTool> Tools() =>
        _registry.All.Select(t => new McpTool(t.Name, t.Description, ToolRegistry.Schema(t))).ToList();

    private async Task<McpResult> CallAsync(McpSession session, string name, IDictionary<string, JsonElement>? raw, CancellationToken ct)
    {
        var tool = _registry.Find(name);
        if (tool is null) return new McpResult($"No tool named {name}.", true);
        var args = new Dictionary<string, JsonElement>(raw ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal);
        var settings = AgentSettings.Load();
        var context = new ToolContext(_context.Link, _context.Library, _context.Settings) { Chat = session.Id };
        if (name == RunPlaxisCodeTool.ToolName && RunPlaxisCodeTool.Held(context, new ToolArgs(args)) is { } held)
            return new McpResult(held.Text, false);

        var facts = new ApprovalFacts
        {
            KeptCode = name == RunPlaxisCodeTool.ToolName && !args.ContainsKey("code") && args.TryGetValue("kept_id", out var kid)
                       && kid.ValueKind == JsonValueKind.String && _context.Library.Get(kid.GetString()!) is { Allowed: true },
        };
        var remembered = (ICollection<string>?)settings.NoAskTools ?? [];
        var decision = ApprovalPolicy.Decide(name, tool.ModifiesModel, false, args, true, facts, settings.Approvals, remembered,
            _taskApproved, Probe, [OutputFolder]);
        if (decision.Ask)
        {
            var answer = await _asker.AskAsync(new ApprovalRequest(name, ApprovalPolicy.Friendly(name), decision.Reason, Details(name, args), decision.CanSilence), ct).ConfigureAwait(false);
            switch (answer)
            {
                case ApprovalAnswer.Deny:
                    return new McpResult("The user declined this step. Ask what they want instead; don't try it another way.", true);
                case ApprovalAnswer.AllowRestOfTask:
                    _taskApproved = true;
                    break;
                case ApprovalAnswer.DontAskAgain when decision.CanSilence:
                    var s = AgentSettings.Load();
                    s.NoAskTools = (s.NoAskTools ?? []).Append(name).Distinct(StringComparer.Ordinal).ToList();
                    s.Save();
                    break;
            }
        }

        try
        {
            var result = await tool.ExecuteAsync(context, new ToolArgs(args), ct).ConfigureAwait(false);
            return new McpResult(result.Text, result.IsError, result.PngImage is { } png ? Convert.ToBase64String(png) : null);
        }
        catch (ArgumentException ex) { return new McpResult(ex.Message, true); }
        catch (InvalidOperationException ex) { return new McpResult(ex.Message, true); }
    }

    /// <summary>The code or commands the user should read before allowing, as plain text.</summary>
    internal static string Details(string tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        if (args.TryGetValue("code", out var code) && code.ValueKind == JsonValueKind.String) return code.GetString()!;
        if (args.TryGetValue("commands", out var list) && list.ValueKind == JsonValueKind.Array)
            return string.Join("\n", list.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText()));
        return JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Plaxis ProMax's own output folder (Documents\Plaxis ProMax): writing inside it never asks.</summary>
    public static string OutputFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Plaxis ProMax");

    private static PathState Probe(string path)
    {
        try
        {
            if (File.Exists(path)) return PathState.File;
            if (Directory.Exists(path)) return Directory.EnumerateFileSystemEntries(path).Any() ? PathState.Folder : PathState.EmptyFolder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return PathState.Missing;
    }

    public void Dispose()
    {
        if (Current == _server) Current = null;
        _server.Dispose();
    }
}
