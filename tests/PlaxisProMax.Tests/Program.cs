var failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail == "" ? "" : ": " + detail)}");
    if (!ok) failures++;
}

void Section(string name, Action<Action<string, bool, string>> run)
{
    try { run(Check); }
    catch (Exception ex) { Check(name + ": ran to the end", false, ex.ToString()); }
}

// The tests run as the owner's PC, so the tools aren't locked; the license checks set their own state.
PlaxisProMax.Licensing.LicenseState.Override = () => new PlaxisProMax.Licensing.LicenseState.Status(
    PlaxisProMax.Licensing.LicenseState.Kind.Owner, "", null, PlaxisProMax.Licensing.LicenseKey.Check.Valid, null);
var root = FindRoot();
// Nothing the checks write lands in the user's own folders.
var scratch = Path.Combine(Path.GetTempPath(), "ppm-checks-" + Guid.NewGuid().ToString("N"));
PlaxisProMax.Tools.ActivityLog.RootOverride = Path.Combine(scratch, "logs");
PlaxisProMax.Bugs.BugLog.RootOverride = Path.Combine(scratch, "Bugs");
PlaxisProMax.Chat.ChatStore.RootOverride = Path.Combine(scratch, "conversations");
PlaxisProMax.Update.Updater.RootOverride = Path.Combine(scratch, "local");

Section("approvals", ApprovalTests.Run);
Section("kept code", CodeLibraryTests.Run);
Section("mcp http", McpHttpTests.Run);
Section("plaxis link", PlaxisLinkTests.Run);
Section("chat mcp", ChatMcpTests.Run);
Section("chat engine", ChatEngineTests.Run);
Section("install", InstallTests.Run);
Section("license", c => LicenseTests.Run(root, c));
Section("iris", IrisTests.Run);
Section("update source", ReleaseSourceTests.Run);
Section("update resume", UpdateResumeTests.Run);
Section("updater", UpdaterTests.Run);
Section("bug log", BugLogTests.Run);
Section("bug reports and export", BugReportTests.Run);
Section("job pause", JobPauseTests.Run);
Section("job cards", JobCardTests.Run);
Section("jobs", ParallelJobTests.Run);
Section("crew", CrewTests.Run);
Section("job checks", JobChecksTests.Run);
Section("job truth", JobTruthTests.Run);
Section("automatic model", ChatAutoTests.Run);
Section("questions", ChatQuestionsTests.Run);
Section("chat work", ChatWorkTests.Run);

try { Directory.Delete(scratch, true); } catch (Exception) { }
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures;

static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "PlaxisProMax.Core", "Mcp", "ChatMcp.cs"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("The repository folder wasn't found above " + AppContext.BaseDirectory);
}
