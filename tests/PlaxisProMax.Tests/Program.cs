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

Section("approvals", ApprovalTests.Run);
Section("kept code", CodeLibraryTests.Run);
Section("mcp http", McpHttpTests.Run);
Section("plaxis link", PlaxisLinkTests.Run);
Section("chat mcp", ChatMcpTests.Run);
Section("chat engine", ChatEngineTests.Run);
Section("install", InstallTests.Run);
Section("license", c => LicenseTests.Run(root, c));

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures;

static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "PlaxisProMax.Core", "Mcp", "ChatMcp.cs"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("The repository folder wasn't found above " + AppContext.BaseDirectory);
}
