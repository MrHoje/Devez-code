using System.IO;
using System.Text;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>세션의 대화 내용을 transcript(.jsonl)에서 읽어 Discord 전송용으로 추출한다.
/// claude·gajae 는 transcript 경로가 있어 추출 가능하고, 그 외 에이전트(codex/opencode 등)는
/// 접근 경로가 없어 null/빈 목록을 반환한다(호출부가 폴백 처리).</summary>
public static class AgentReplyService
{
    /// <summary>roomId(=세션 ID)와 에이전트로 마지막 assistant 텍스트 응답을 구한다. 없으면 null.</summary>
    public static string? TryGetLastAssistantReply(string roomId, string agentId)
    {
        try
        {
            // claude: busy-hook(Stop)이 stdin 의 last_assistant_message 를 그대로 기록한 파일을 우선 사용한다.
            // transcript 경로/세션ID 추적이 필요 없어 가장 정확하고 견고하다.
            if (agentId == "claude")
            {
                var hookReply = ReadHookReply(roomId);
                if (!string.IsNullOrWhiteSpace(hookReply)) return hookReply;
            }
            var path = TranscriptPath(roomId, agentId);
            return path == null ? null : LastAssistantTextFromJsonl(path);
        }
        catch { return null; }
    }

    /// <summary>busy-hook(Stop)이 기록한 방의 마지막 assistant 답변
    /// (%AppData%\DevezCode\claude\lastreply\&lt;room&gt;.txt).</summary>
    private static string? ReadHookReply(string roomId)
    {
        try
        {
            var safe = System.Text.RegularExpressions.Regex.Replace(roomId, @"[^\w\-]", "");
            var p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "claude", "lastreply", safe + ".txt");
            if (!File.Exists(p)) return null;
            var s = File.ReadAllText(p).Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    /// <summary>세션의 최근 대화를 시간순(오래된→최신)으로 최대 maxMessages 개 반환한다.
    /// 각 항목은 (role: "user"|"assistant", text). 실제 사용자/어시스턴트 텍스트만 — tool_use/tool_result 제외.</summary>
    public static List<(string role, string text)> GetRecentConversation(string roomId, string agentId, int maxMessages)
    {
        try
        {
            var path = TranscriptPath(roomId, agentId);
            return path == null ? new() : RecentFromJsonl(path, maxMessages);
        }
        catch { return new(); }
    }

    /// <summary>에이전트별 transcript .jsonl 경로. 없으면 null.</summary>
    private static string? TranscriptPath(string roomId, string agentId) => agentId switch
    {
        "claude" => ClaudeTranscriptPath(roomId),
        "gajae"  => GajaeTranscriptPath(roomId),
        _ => null,
    };

    /// <summary>%USERPROFILE%\.claude\projects\&lt;encoded-cwd&gt;\&lt;sessionId&gt;.jsonl
    /// sessionId 는 busy-hook 추적 파일(최신) → settings 순으로 구한다. 그 sessionId 의 transcript 만
    /// 사용한다 — "폴더 최근 jsonl" 폴백은 같은 폴더의 다른(옛) 세션 대화를 가져와 엉뚱한 답을 보내므로 쓰지 않는다.</summary>
    private static string? ClaudeTranscriptPath(string roomId)
    {
        var workingDir = SettingsService.LoadClaudeCodeRoomDir(roomId);
        if (string.IsNullOrWhiteSpace(workingDir)) return null;

        var full = Path.GetFullPath(workingDir);
        if (full.Length > 3) full = full.TrimEnd('\\', '/'); // 드라이브 루트(C:\)는 백슬래시 유지 — claude 인코딩(C--)과 일치
        var encoded = System.Text.RegularExpressions.Regex.Replace(full, "[^a-zA-Z0-9]", "-");
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects", encoded);
        if (!Directory.Exists(dir)) return null;

        // sessionId: busy-hook 이 기록한 추적 파일이 settings 보다 최신일 수 있어 우선.
        var sessionId = ReadTrackedClaudeSession(roomId)
                        ?? SettingsService.LoadClaudeCodeRoomSession(roomId);
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var path = Path.Combine(dir, sessionId + ".jsonl");
        return File.Exists(path) ? path : null;
    }

    /// <summary>busy-hook 이 기록한 방별 claude 세션 ID
    /// (%APPDATA%\DevezCode\claude\sessions\&lt;room&gt;.txt). settings 동기화 지연을 우회한다.</summary>
    private static string? ReadTrackedClaudeSession(string roomId)
    {
        try
        {
            var safe = System.Text.RegularExpressions.Regex.Replace(roomId, @"[^\w\-]", "");
            var p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "claude", "sessions", safe + ".txt");
            if (!File.Exists(p)) return null;
            var s = File.ReadAllText(p).Trim();
            return Guid.TryParse(s, out _) ? s : null;
        }
        catch { return null; }
    }

    /// <summary>%AppData%\DevezCode\gajae\sessions\&lt;roomId&gt;\*.jsonl 중 최신 파일.</summary>
    private static string? GajaeTranscriptPath(string roomId)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DevezCode", "gajae", "sessions",
            System.Text.RegularExpressions.Regex.Replace(roomId, @"[^\w\-]", ""));
        if (!Directory.Exists(dir)) return null;
        var newest = new DirectoryInfo(dir)
            .GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest?.FullName;
    }

    /// <summary>jsonl 끝에서부터 거슬러 올라가 text 파트를 가진 첫 assistant 메시지의 텍스트를 반환.
    /// 마지막 줄이 tool_use(텍스트 없음)일 수 있어, 실제 답변 텍스트가 나올 때까지 위로 스캔한다.</summary>
    private static string? LastAssistantTextFromJsonl(string path)
    {
        var lines = ReadLines(path);
        if (lines == null) return null;

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (TryParseMessage(lines[i], out var role, out var text) && role == "assistant" && text != null)
                return text;
        }
        return null;
    }

    /// <summary>최근 user/assistant 텍스트 메시지를 끝에서부터 maxMessages 개 모아 시간순으로 반환.</summary>
    private static List<(string role, string text)> RecentFromJsonl(string path, int maxMessages)
    {
        var result = new List<(string, string)>();
        var lines = ReadLines(path);
        if (lines == null) return result;

        for (int i = lines.Length - 1; i >= 0 && result.Count < maxMessages; i--)
        {
            if (TryParseMessage(lines[i], out var role, out var text)
                && (role == "user" || role == "assistant") && text != null)
                result.Add((role!, text));
        }
        result.Reverse(); // 오래된 → 최신
        return result;
    }

    /// <summary>jsonl 한 줄에서 (role, 합쳐진 text)를 뽑는다. text 파트가 없으면 text=null.
    /// content 가 문자열(사용자 입력)이거나 배열의 type=="text" 파트만 취하고 tool_use/tool_result 는 제외.</summary>
    private static bool TryParseMessage(string line, out string? role, out string? text)
    {
        role = null; text = null;
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var m)) return false;
            if (!m.TryGetProperty("role", out var r)) return false;
            role = r.GetString();
            if (!m.TryGetProperty("content", out var content)) return true;

            if (content.ValueKind == JsonValueKind.String)
            {
                var s = content.GetString();
                if (!string.IsNullOrWhiteSpace(s)) text = s!.Trim();
                return true;
            }
            if (content.ValueKind != JsonValueKind.Array) return true;

            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text"
                    && part.TryGetProperty("text", out var txt))
                {
                    var s = txt.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(s!.Trim());
                    }
                }
            }
            if (sb.Length > 0) text = sb.ToString();
            return true;
        }
        catch { return false; }
    }

    private static string[]? ReadLines(string path)
    {
        try
        {
            // 에이전트가 파일을 열고 있을 수 있어 공유 읽기.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Split('\n');
        }
        catch { return null; }
    }
}
