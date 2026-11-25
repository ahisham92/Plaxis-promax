using PlaxisProMax.Chat;
using PlaxisProMax.Mcp;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

namespace PlaxisProMax.Bugs;

/// <summary>
/// The folders where Plaxis ProMax keeps what a person makes and what goes wrong on this PC (the ⋯ menu's Open folders),
/// so they can be opened, or copied to another PC, without knowing the paths. Each path comes from the code that writes
/// there, so the list can't drift from the folders in use.
/// </summary>
internal static class UserFolders
{
    internal sealed record Folder(string Title, string Path, string Tip);

    public static IReadOnlyList<Folder> List(CodeLibrary? library = null) =>
    [
        new("Your files", ChatMcp.OutputFolder, "The Excel, Word, PDF, PowerPoint and DXF files the chat makes for you."),
        new("Bugs", BugLog.Root, "Report bug zips (Reports), Export chats & bugs zips (Exports), crash notes (Crashes) and the bug log."),
        new("Self-test reports", SelfTest.Folder, "One report for each time the self-test ran."),
        new("Kept code", (library ?? new CodeLibrary()).Folder, "The code that worked and was kept to run again, one file each."),
        new("Recipes", Recipes.Folder, "Saved recipes: steps the chat can run again, one .json file each."),
        new("Work to do again", RedoLog.Folder, "Every change the chats made to each project, kept in full so it can be done again after a crash."),
        new("Snapshots", ModelSnapshots.Root, "The snapshots of each project, to compare with the project later."),
        new("Chats", ChatStore.FolderPath, "The saved chats of every project."),
        new("Notes", System.IO.Path.GetDirectoryName(MemoryNotes.File) ?? AgentSettings.Folder, "memory.md: what you asked Plaxis ProMax to remember."),
        new("Activity log", ActivityLog.Folder, "Every tool call, one line each, one file a day."),
        new("Attachments", ClaudeCli.AttachmentsFolder, "Copies of the files and pictures you attached to chats."),
        new("Settings", AgentSettings.Folder, "Settings, the license and everything above that is saved per user."),
    ];
}
