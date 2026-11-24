namespace PlaxisProMax.Chat;

/// <summary>
/// The project and chat the window shows now (Sketch ProMax's ChatPane.CurrentModelKey and friends): the tools that
/// need no Plaxis (notes, earlier chats) and the prompt read them here. Set by the window on its own thread.
/// </summary>
internal static class ChatScope
{
    /// <summary>The key of an unsaved project's chats.</summary>
    public const string Unsaved = "plaxis:unsaved";

    /// <summary>The open project's key in the chat store ("plaxis:" and its file's path in lower case).</summary>
    public static volatile string ProjectKey = Unsaved;

    /// <summary>The open project's name (its file's, without the extension).</summary>
    public static volatile string ProjectTitle = "";

    /// <summary>The shown chat's id.</summary>
    public static volatile string ChatId = "";

    /// <summary>The key notes are kept under: null for a project that has no file yet (its notes would fit any such project).</summary>
    public static string? NotesKey => ProjectKey == Unsaved || ProjectKey.Length == 0 ? null : ProjectKey;

    public static void Set(string projectKey, string projectTitle, string chatId)
    {
        ProjectKey = projectKey;
        ProjectTitle = projectTitle;
        ChatId = chatId;
    }
}
