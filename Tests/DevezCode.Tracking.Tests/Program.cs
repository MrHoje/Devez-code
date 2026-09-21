using DevezCode.Services;
using DevezCode.Services.Terminal;

var failures = new List<string>();

var openCodeThemeRoot = Path.Combine(Path.GetTempPath(), $"devezcode-opencode-theme-test-{Guid.NewGuid():N}");
try
{
    var tuiPath = Path.Combine(openCodeThemeRoot, "tui.json");
    var kvPath = Path.Combine(openCodeThemeRoot, "state", "opencode", "kv.json");
    Directory.CreateDirectory(Path.GetDirectoryName(kvPath)!);
    File.WriteAllText(kvPath, """{"sidebar":"hide","theme":"devezcode-dark","theme_mode_lock":"light"}""");

    OpenCodeCustomThemes.ApplyToPaths("dark", tuiPath, kvPath);
    using var darkTui = System.Text.Json.JsonDocument.Parse(File.ReadAllText(tuiPath));
    using var darkKv = System.Text.Json.JsonDocument.Parse(File.ReadAllText(kvPath));
    Check(darkTui.RootElement.GetProperty("theme").GetString() == "devez-dark", "OpenCode dark TUI theme");
    Check(darkKv.RootElement.GetProperty("theme").GetString() == "devez-dark", "OpenCode stale theme replaced");
    Check(darkKv.RootElement.GetProperty("theme_mode").GetString() == "dark"
          && darkKv.RootElement.GetProperty("theme_mode_lock").GetString() == "dark",
        "OpenCode dark mode locked");
    Check(darkKv.RootElement.GetProperty("sidebar").GetString() == "hide", "OpenCode KV settings preserved");

    OpenCodeCustomThemes.ApplyToPaths("softpink", tuiPath, kvPath);
    using var lightKv = System.Text.Json.JsonDocument.Parse(File.ReadAllText(kvPath));
    Check(lightKv.RootElement.GetProperty("theme").GetString() == "devez-softpink", "OpenCode light TUI theme");
    Check(lightKv.RootElement.GetProperty("theme_mode").GetString() == "light"
          && lightKv.RootElement.GetProperty("theme_mode_lock").GetString() == "light",
        "OpenCode light mode locked");
}
finally
{
    try { Directory.Delete(openCodeThemeRoot, recursive: true); } catch { }
}

var installerTestRoot = Path.Combine(Path.GetTempPath(), $"devezcode-installer-test-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(installerTestRoot);
    var validZip = Path.Combine(installerTestRoot, "valid.zip");
    using (var archive = System.IO.Compression.ZipFile.Open(validZip, System.IO.Compression.ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("DevezCode_Setup_9.9.9.exe");
        using var stream = new StreamWriter(entry.Open());
        stream.Write("setup");
    }
    var extracted = InstallerPackage.ExtractSetup(validZip, Path.Combine(installerTestRoot, "valid"));
    Check(File.ReadAllText(extracted) == "setup", "installer archive extraction");

    var unsafeZip = Path.Combine(installerTestRoot, "unsafe.zip");
    using (var archive = System.IO.Compression.ZipFile.Open(unsafeZip, System.IO.Compression.ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("..\\DevezCode_Setup_9.9.9.exe");
        using var stream = new StreamWriter(entry.Open());
        stream.Write("setup");
    }
    try
    {
        InstallerPackage.ExtractSetup(unsafeZip, Path.Combine(installerTestRoot, "unsafe"));
        Check(false, "installer archive traversal rejected");
    }
    catch (InvalidDataException)
    {
        Check(true, "installer archive traversal rejected");
    }

    var multipleZip = Path.Combine(installerTestRoot, "multiple.zip");
    using (var archive = System.IO.Compression.ZipFile.Open(multipleZip, System.IO.Compression.ZipArchiveMode.Create))
    {
        archive.CreateEntry("DevezCode_Setup_9.9.9.exe");
        archive.CreateEntry("extra.exe");
    }
    try
    {
        InstallerPackage.ExtractSetup(multipleZip, Path.Combine(installerTestRoot, "multiple"));
        Check(false, "installer archive extra file rejected");
    }
    catch (InvalidDataException)
    {
        Check(true, "installer archive extra file rejected");
    }
}
finally
{
    try { Directory.Delete(installerTestRoot, recursive: true); } catch { }
}

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
var forkedClaude = ClaudeTranscriptSnapshotParser.CloneForFork(string.Join('\n', new[]
{
    "{\"type\":\"user\",\"sessionId\":\"old-id\",\"message\":{\"content\":\"keep\"}}",
    "{\"type\":\"frame-link\",\"sessionId\":\"old-id\",\"artifactCount\":1}",
    "{\"type\":\"assistant\",\"sessionId\":\"old-id\",\"message\":{\"content\":\"answer\"}}",
}), "old-id", "new-id");
Check(!forkedClaude.Contains("frame-link", StringComparison.Ordinal),
    "Claude fork drops artifact panel state");
Check(!forkedClaude.Contains("old-id", StringComparison.Ordinal)
      && forkedClaude.Split("new-id").Length - 1 == 2,
    "Claude fork rewrites retained session ids");
var codexMetadataPath = Path.Combine(Path.GetTempPath(), $"devezcode-codex-meta-{Guid.NewGuid():N}.jsonl");
try
{
    File.WriteAllLines(codexMetadataPath, new[]
    {
        "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-old\",\"effort\":\"low\"}}",
        "{\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\",\"thread_settings\":{\"model\":\"gpt-parent\",\"reasoning_effort\":\"max\"}}}",
        "{\"type\":\"turn_context\"",
    });
    var codexMetadata = CodexTranscriptMetadata.ReadLatest(codexMetadataPath);
    Check(codexMetadata == ("gpt-parent", "max"), "Codex latest model and effort restore");
}
finally
{
    try { File.Delete(codexMetadataPath); } catch { }
}
var interruptedClaude = ClaudeTranscriptSnapshotParser.ParseLines(new[]
{
    """{"type":"assistant","message":{"content":[{"type":"text","text":"partial answer\n[Request interrupted by user for tool use]"}]}}""",
});
Check(interruptedClaude.Turns.Count == 1
      && interruptedClaude.Turns[0].Text == "partial answer",
    "Claude interruption marker hidden while partial answer remains");
var narrationClaude = ClaudeTranscriptSnapshotParser.ParseLines(new[]
{
    """{"type":"user","message":{"content":[{"type":"text","text":"first ask"}]}}""",
    """{"type":"assistant","message":{"content":[{"type":"text","text":"코드 확인 시작."}]}}""",
    """{"type":"user","message":{"content":[{"type":"tool_result","content":"file body"}]}}""",
    """{"type":"assistant","message":{"content":[{"type":"text","text":"이제 편집."}]}}""",
    """{"type":"assistant","message":{"content":[{"type":"text","text":"최종 답변"}]}}""",
    """{"type":"user","message":{"content":[{"type":"text","text":"second ask"}]}}""",
    """{"type":"assistant","message":{"content":[{"type":"text","text":"두 번째 답변"}]}}""",
});
Check(narrationClaude.Turns.Count == 4
      && narrationClaude.Turns[1] == new ClaudeTranscriptTurn("assistant", "최종 답변")
      && narrationClaude.Turns[3] == new ClaudeTranscriptTurn("assistant", "두 번째 답변"),
    "Claude restore keeps only the last assistant text per turn");
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
if (args.Length == 2 && args[0] == "--codex" && File.Exists(args[1]))
{
    var realCodex = CodexTranscriptMetadata.ReadLatest(args[1]);
    Check(!string.IsNullOrWhiteSpace(realCodex.Model), "real Codex transcript model");
    Check(!string.IsNullOrWhiteSpace(realCodex.Effort), "real Codex transcript effort");
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
