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

Section("approvals", ApprovalTests.Run);
Section("kept code", CodeLibraryTests.Run);
Section("mcp http", McpHttpTests.Run);
Section("plaxis link", PlaxisLinkTests.Run);
Section("chat mcp", ChatMcpTests.Run);
Section("chat engine", ChatEngineTests.Run);
Section("install", InstallTests.Run);

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures;
