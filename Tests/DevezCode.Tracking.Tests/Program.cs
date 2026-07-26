using DevezCode.Services;

var failures = new List<string>();

Check(AgentEventOwnership.IsMatch("claude", "claude"), "same agent");
Check(AgentEventOwnership.IsMatch("CoDeX", "codex"), "case-insensitive");
Check(!AgentEventOwnership.IsMatch("claude", "codex"), "cross-agent");
Check(!AgentEventOwnership.IsMatch("", "codex"), "missing owner fails closed");
Check(!AgentEventOwnership.IsMatch("codex", ""), "missing source fails closed");

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
