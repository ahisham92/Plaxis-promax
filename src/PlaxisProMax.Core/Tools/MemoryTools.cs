using PlaxisProMax.Chat;

namespace PlaxisProMax.Tools;

/// <summary>Notes the user asks Claude to remember, kept in memory.md in the settings folder (see MemoryNotes).</summary>
internal sealed class MemoryNoteTool : IPlaxisTool
{
    public string Name => "memory_note";
    public string Description =>
        "Remember or forget a note for the user. When the user says \"remember that ...\" use action=remember with the note " +
        "in their words, short and self-contained; when they say \"forget ...\" use action=forget with the note or a few " +
        "words of it. scope=project (default) keeps it for the open project only; scope=global for every project (their habits, " +
        "standards, preferences). Notes are given to every chat at its start (global plus that project's) and can be " +
        "seen and edited under Menu > Notes. action=list shows them. When the new note changes an earlier " +
        "one (the user corrected it), pass replaces with words of the earlier note: it is taken out, in either scope, so the two never " +
        "stand side by side. Not a change to the project.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["remember", "forget", "list"], "description": "Default list."},
      "note": {"type": "string", "description": "remember: the note. forget: the note, or words that appear in it."},
      "scope": {"type": "string", "enum": ["project", "global"], "description": "Default project (the open project); global for every project."},
      "replaces": {"type": "string", "description": "remember: words of an earlier note this one changes; that note is removed (project or global)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct) => Task.FromResult(Answer(args));

    /// <summary>Needs nothing from Plaxis (Sketch ProMax bug 116): the project the chat is on stands for the open one.</summary>
    internal static ToolResult Answer(ToolArgs args)
    {
        var key = ChatScope.NotesKey;
        return Run(key, key is null ? null : ChatScope.ProjectTitle, args);
    }

    private static ToolResult Run(string? modelKey, string? title, ToolArgs args)
    {
        var action = (args.OptString("action") ?? "list").Trim().ToLowerInvariant();
        var global = string.Equals(args.OptString("scope"), "global", StringComparison.OrdinalIgnoreCase);
        if (!global && modelKey is null && action != "list") return ToolResult.Error("The open project has no file yet: use scope=global, or save the project first.");
        var key = global ? null : modelKey;
        var where = global ? "for every project" : $"for this project ({title})";

        switch (action)
        {
            case "list":
            {
                var (g, m) = MemoryNotes.For(MemoryNotes.Load(), modelKey);
                return ToolResult.Ok(new { global = g, this_project = m, project = title, file = MemoryNotes.File });
            }
            case "remember":
            {
                var note = MemoryNotes.Clean(args.OptString("note") ?? "");
                if (note.Length == 0) return ToolResult.Error("Give the note to remember in 'note'.");
                var old = args.OptString("replaces") ?? "";
                var gone = new List<string>();
                var added = MemoryNotes.Change(s =>
                {
                    if (MemoryNotes.Clean(old).Length > 0)
                        foreach (var k in new[] { modelKey, null }.Distinct())
                            foreach (var match in MemoryNotes.Matches(s, k, old).Where(n => !string.Equals(n, note, StringComparison.OrdinalIgnoreCase)))
                                if (MemoryNotes.Remove(s, k, match)) gone.Add(match);
                    return MemoryNotes.Add(s, key, note);
                });
                return ToolResult.Ok(new
                {
                    remembered = note, scope = global ? "global" : "project", added, replaced = gone,
                    next = added ? $"Tell the user it is remembered {where}; it is under Menu > Notes." : "It was already remembered.",
                });
            }
            case "forget":
            {
                var text = args.OptString("note") ?? "";
                if (MemoryNotes.Clean(text).Length == 0) return ToolResult.Error("Say which note to forget in 'note' (the note or words from it).");
                var result = MemoryNotes.Change(s =>
                {
                    var matches = MemoryNotes.Matches(s, key, text);
                    if (matches.Count == 1) MemoryNotes.Remove(s, key, matches[0]);
                    return matches;
                });
                if (result.Count == 1) return ToolResult.Ok(new { forgotten = result[0], scope = global ? "global" : "project" });
                if (result.Count == 0)
                {
                    var (g, m) = MemoryNotes.For(MemoryNotes.Load(), modelKey);
                    return ToolResult.Error($"No note {where} matches '{text}'. Notes for every project: {Join(g)}. Notes for this project: {Join(m)}. " +
                                            "Try the other scope, or the note's exact words.");
                }
                return ToolResult.Error($"{result.Count} notes {where} match '{text}': {Join(result)}. Ask the user which one, then forget it by its exact words.");
            }
            default:
                return ToolResult.Error("action is remember, forget or list.");
        }
    }

    private static string Join(List<string> notes) => notes.Count == 0 ? "(none)" : string.Join(" | ", notes.Select(n => "\"" + n + "\""));
}

/// <summary>
/// Searches the user's earlier chats, for every project (ChatRecall): "do it the same way as in the
/// Tower project" finds that chat, and action=open gives its summary, requests and steps to follow again.
/// </summary>
internal sealed class RecallChatsTool : IPlaxisTool
{
    public string Name => "recall_chats";
    public string Description =>
        "Search the user's earlier chats, across every project. Use it when the user refers to " +
        "something done before (\"the same way as in the quay wall project\", \"like last time\", \"what did we decide about ...\"). " +
        "action=search (default) with query = the words that name the project or task: lists the best matching chats " +
        "with their project, date, summary and matching lines. action=open with chat = a chat's id from search: its summary, " +
        "the user's requests and the steps taken with their settings, to repeat on this project. action=list: this project's " +
        "chats with what each remembered. Read-only.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["search", "open", "list"], "description": "Default search."},
      "query": {"type": "string", "description": "search: words naming the project or task, e.g. 'quay wall anchors'."},
      "chat": {"type": "string", "description": "open: the chat id from search or list."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public Task<ToolResult> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken ct) => Task.FromResult(Answer(args));

    internal static ToolResult Answer(ToolArgs args)
    {
        var action = (args.OptString("action") ?? "search").Trim().ToLowerInvariant();
        var all = ChatStore.All();
        switch (action)
        {
            case "search":
            {
                var query = args.OptString("query") ?? "";
                var hits = ChatRecall.Search(all, query);
                return ToolResult.Ok(new
                {
                    query, searched = all.Count,
                    chats = hits.Select(h => Row(h.Chat, h.Lines)).ToList(),
                    next = hits.Count == 0
                        ? "No earlier chat matches. Ask the user which project or chat they mean, or search with other words."
                        : "Open the best match with action=open and chat=<id> to see its steps, then follow them on this project.",
                });
            }
            case "open":
            {
                var id = (args.OptString("chat") ?? "").Trim();
                if (id.Length == 0) return ToolResult.Error("Give the chat's id in 'chat' (from action=search or list).");
                var chat = all.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
                if (chat is null) return ToolResult.Error($"No chat has the id '{id}'. Search again with action=search.");
                var (summary, requests, steps) = ChatRecall.Details(chat);
                return ToolResult.Ok(new
                {
                    chat = chat.Id, name = chat.Name, project = chat.Title, date = chat.Updated.ToLocalTime().ToString("yyyy-MM-dd"),
                    summary, requests, steps,
                    next = "Repeat what applies on this project with the same settings (check its soil layers, materials and units first). " +
                           "Tell the user in one line which chat you followed.",
                });
            }
            case "list":
            {
                var key = ChatScope.ProjectKey;
                var mine = all.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase) && c.Items.Count > 0)
                    .OrderByDescending(c => c.Updated).Take(20).ToList();
                return ToolResult.Ok(new { project = ChatScope.ProjectTitle, chats = mine.Select(c => Row(c, [])).ToList() });
            }
            default:
                return ToolResult.Error("action is search, open or list.");
        }
    }

    private static object Row(Conversation c, List<string> lines) => new
    {
        chat = c.Id, name = c.Name, project = c.Title, date = c.Updated.ToLocalTime().ToString("yyyy-MM-dd"),
        summary = c.Recall.Length > 0 ? c.Recall : "(not summarised yet)", matches = lines,
    };
}
