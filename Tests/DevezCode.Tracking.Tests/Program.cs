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

var restoredClaude = ClaudeTranscriptSnapshotParser.ParseLines(new[]
{
    """{"type":"user","isSidechain":false,"message":{"content":[{"type":"text","text":"question"}]}}""",
    """{"type":"assistant","isSidechain":true,"effort":"low","message":{"model":"claude-haiku-5","content":[{"type":"text","text":"child"}],"usage":{"input_tokens":900}}}""",
    """{"type":"user","isSidechain":false,"isCompactSummary":true,"message":{"content":[{"type":"text","text":"internal compact summary"}]}}""",
    """{"type":"user","isSidechain":false,"isMeta":true,"message":{"content":[{"type":"text","text":"internal metadata"}]}}""",
    """{"type":"assistant","isSidechain":false,"effort":"xhigh","context_window":{"total_input_tokens":55,"context_window_size":1000000},"message":{"model":"claude-opus-5","content":[{"type":"text","text":"answer"},{"type":"tool_use","name":"Read"}],"usage":{"input_tokens":10,"cache_creation_input_tokens":20,"cache_read_input_tokens":30,"output_tokens":999}}}""",
    """{"type":"assistant","isSidechain":false,"message":{"model":"claude-opus-5","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""",
    """{"type":"assistant","isSidechain":false,"message":{"model":"claude-opus-5","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}}""",
    """{"type":"user","message":{"content":"<command-name>/effort</command-name>\n<command-message>effort</command-message>\n<command-args>xhigh</command-args>"}}""",
    """{"type":"user","message":{"content":"<local-command-stdout>Set effort level to xhigh</local-command-stdout>"}}""",
    """{"type":"user","message":{"content":"<local-command-stderr>Command failed</local-command-stderr>"}}""",
    """{"type":"user","isSidechain":false,"message":{"content":[{"type":"tool_result","content":"hidden"}]}}""",
    """{"type":"permission-mode","permissionMode":"auto"}""",
});
Check(restoredClaude.Turns.Count == 2, "Claude main conversation only");
Check(restoredClaude.Turns[0] == new ClaudeTranscriptTurn("user", "question"), "Claude user restore");
Check(restoredClaude.Turns[1] == new ClaudeTranscriptTurn("assistant", "answer"), "Claude assistant restore");
Check(restoredClaude.Model == "claude-opus-5", "Claude model restore");
Check(restoredClaude.Effort == "xhigh", "Claude effort restore");
Check(restoredClaude.PermissionMode == "auto", "Claude permission restore");
Check(restoredClaude.ContextTokens == 60, "Claude context input tokens restore");
Check(restoredClaude.ContextWindow == 1_000_000, "Claude context window restore");
var interruptedClaude = ClaudeTranscriptSnapshotParser.ParseLines(new[]
{
    """{"type":"assistant","message":{"content":[{"type":"text","text":"partial answer\n[Request interrupted by user for tool use]"}]}}""",
});
Check(interruptedClaude.Turns.Count == 1
      && interruptedClaude.Turns[0].Text == "partial answer",
    "Claude interruption marker hidden while partial answer remains");
var legacyRuntime = ModelEffortService.ParsePersistedContent("claude-sonnet-5\nhigh");
Check(legacyRuntime.Model == "claude-sonnet-5" && legacyRuntime.ContextTokens == null,
    "Claude legacy runtime state compatibility");
var currentRuntime = ModelEffortService.ParsePersistedContent(
    "claude-opus-5\nxhigh\n123456\n1000000\ndbfe95b9-7f54-428a-870a-0c8c86e5c848");
Check(currentRuntime.ContextTokens == 123456 && currentRuntime.ContextWindow == 1_000_000,
    "Claude runtime context restore");
Check(currentRuntime.SessionId == "dbfe95b9-7f54-428a-870a-0c8c86e5c848",
    "Claude runtime session fence");
var largeTranscriptPath = Path.Combine(Path.GetTempPath(), $"devezcode-claude-restore-{Guid.NewGuid():N}.jsonl");
try
{
    File.WriteAllLines(largeTranscriptPath, new[]
    {
        "{\"type\":\"last-prompt\",\"lastPrompt\":\"" + new string('x', 2 * 1024 * 1024) + "\"}",
        """{"type":"user","isSidechain":false,"message":{"content":[{"type":"text","text":"after metadata"}]}}""",
    });
    var largeTranscript = ClaudeTranscriptSnapshotParser.ParseFile(largeTranscriptPath);
    Check(largeTranscript.Turns.Count == 1 && largeTranscript.Turns[0].Text == "after metadata",
        "Claude large metadata line skip");
}
finally
{
    try { File.Delete(largeTranscriptPath); } catch { }
}
if (args.Length == 1 && File.Exists(args[0]))
{
    var realTranscript = ClaudeTranscriptSnapshotParser.ParseFile(args[0]);
    Check(realTranscript.Turns.Count > 0, "real Claude transcript conversation");
    Check(!string.IsNullOrWhiteSpace(realTranscript.Model), "real Claude transcript model");
    Check(realTranscript.Effort is "low" or "medium" or "high" or "xhigh" or "max",
        "real Claude transcript effort");
    Check(realTranscript.ContextTokens is > 0, "real Claude transcript context");
}

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

Console.WriteLine("PASS: agent event ownership and Claude transcript restore");
return 0;

void Check(bool condition, string name)
{
    if (!condition) failures.Add(name);
}
