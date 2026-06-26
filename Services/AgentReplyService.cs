using System.IO;
using System.Text;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>세션 완료 시 에이전트의 마지막 assistant 응답 텍스트를 추출한다(Discord 전송용).
/// claude·gajae 는 transcript(.jsonl)에서 추출하고, 그 외 에이전트(codex/opencode 등)는
/// transcript 접근 경로가 없어 null 을 반환한다(호출부가 질문 폴백으로 처리).</summary>
public static class AgentReplyService
{
    /// <summary>roomId(=세션 ID)와 에이전트로 마지막 assistant 텍스트 응답을 구한다. 없으면 null.</summary>
    public static string? TryGetLastAssistantReply(string roomId, string agentId)
    {
        try
        {
            return agentId switch
            {
                "claude" => FromClaude(roomId),
                "gajae"  => FromGajae(roomId),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>%USERPROFILE%\.claude\projects\&lt;encoded-cwd&gt;\&lt;sessionId&gt;.jsonl 에서 추출
    /// (경로 규칙은 TerminalSessionManager.ClaudeTranscriptExists 와 동일).</summary>
    private static string? FromClaude(string roomId)
    {
        var workingDir = SettingsService.LoadClaudeCodeRoomDir(roomId);
        var sessionId = SettingsService.LoadClaudeCodeRoomSession(roomId);
        if (string.IsNullOrWhiteSpace(workingDir) || string.IsNullOrWhiteSpace(sessionId)) return null;

        var full = Path.GetFullPath(workingDir).TrimEnd('\\', '/');
        var encoded = System.Text.RegularExpressions.Regex.Replace(full, "[^a-zA-Z0-9]", "-");
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects", encoded, sessionId + ".jsonl");
        return File.Exists(path) ? LastAssistantTextFromJsonl(path) : null;
    }

    /// <summary>%AppData%\DevezCode\gajae\sessions\&lt;roomId&gt;\*.jsonl 중 최신 파일에서 추출.</summary>
    private static string? FromGajae(string roomId)
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
        return newest == null ? null : LastAssistantTextFromJsonl(newest.FullName);
    }

    /// <summary>jsonl 끝에서부터 거슬러 올라가 text 파트를 가진 첫 assistant 메시지의 텍스트를 반환.
    /// 마지막 줄이 tool_use(텍스트 없음)일 수 있어, 실제 답변 텍스트가 나올 때까지 위로 스캔한다.
    /// 라인 포맷: {"type":...,"message":{"role":"assistant","content":[{"type":"text","text":"…"}, …]}}</summary>
    private static string? LastAssistantTextFromJsonl(string path)
    {
        string[] lines;
        try
        {
            // 에이전트가 파일을 열고 있을 수 있어 공유 읽기.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            lines = sr.ReadToEnd().Split('\n');
        }
        catch { return null; }

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var m)) continue;
                if (!m.TryGetProperty("role", out var r) || r.GetString() != "assistant") continue;
                if (!m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

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
                            sb.Append(s.Trim());
                        }
                    }
                }
                var result = sb.ToString().Trim();
                if (result.Length > 0) return result; // text 없는 assistant(tool_use만)면 계속 위로
            }
            catch { /* 깨진 줄 무시 */ }
        }
        return null;
    }
}
