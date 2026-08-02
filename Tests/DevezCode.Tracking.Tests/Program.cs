using DevezCode.Services;

var failures = new List<string>();

Check(AgentEventOwnership.IsMatch("claude", "claude"), "same agent");
Check(AgentEventOwnership.IsMatch("CoDeX", "codex"), "case-insensitive");
Check(!AgentEventOwnership.IsMatch("claude", "codex"), "cross-agent");
Check(!AgentEventOwnership.IsMatch("", "codex"), "missing owner fails closed");
Check(!AgentEventOwnership.IsMatch("codex", ""), "missing source fails closed");
Check(TrackingEnvironment.IsExpected("codex", "codex"), "matching marker");
Check(!TrackingEnvironment.IsExpected("claude", "codex"), "mismatched marker");
Check(!TrackingEnvironment.IsExpected(null, "codex"), "missing marker");
Check(
    TrackingEnvironment.CmdSetLine("kimi") == "set \"DEVEZCODE_TRACKING_AGENT=kimi\"\r\n",
    "cmd marker");

var codexStart = PathCommandProcess.Create(
    "codex", new[] { "mcp", "list" }, redirectOutput: true);
Check(!codexStart.UseShellExecute && codexStart.CreateNoWindow, "CLI hidden process");
Check(codexStart.RedirectStandardOutput && codexStart.RedirectStandardError, "CLI output capture");
if (OperatingSystem.IsWindows())
{
    Check(
        string.Equals(
            codexStart.FileName,
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            StringComparison.OrdinalIgnoreCase),
        "Windows command shell");
    Check(
        codexStart.ArgumentList.SequenceEqual(new[] { "/d", "/c", "codex", "mcp", "list" }),
        "Windows PATH command arguments");
}

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine($"FAIL: {failure}");
    return 1;
}

Console.WriteLine("PASS: agent event ownership");
return 0;

void Check(bool condition, string name)
{
    if (!condition) failures.Add(name);
}
