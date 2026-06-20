using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DevezCode.Services;

namespace DevezCode.Services;

/// <summary>opencode 의 마지막 프롬프트 추적. claude 는 SessionLastMessageService+hook,
/// codex 는 CodexHookService+hook. 이 서비스는 opencode 만 담당 (SQLite 라 subprocess 폴링).
/// `opencode session list` → cwd 매칭 ID → `opencode export <id>` 로 JSON 받아
/// 가장 최근 user message 추출. (workingDir, agentId) 단위로 추적.</summary>
public sealed class AgentLastMessageService : IDisposable
{
    // ── 추적 대상: (workingDir, agentId) → 마지막으로 본 마지막 user prompt 와 비교용 mtime/seq ──
    private sealed record TrackKey(string WorkingDir, string AgentId);
    private readonly ConcurrentDictionary<TrackKey, string> _lastPrompt = new();
    private readonly ConcurrentDictionary<TrackKey, DateTime> _lastSeen = new();

    // opencode 만 담당 — codex 는 CodexHookService, claude 는 SessionLastMessageService 가 처리.
    private readonly DispatcherTimer _opencodePoll; // opencode 는 SQLite 라 폴링

    public AgentLastMessageService()
    {
        _opencodePoll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _opencodePoll.Tick += (_, _) => PollOpenCode();
    }

    /// <summary>(workingDir, lastPrompt) — 해당 디렉터리의 활성 세션이 마지막으로 보낸 메시지.
    /// 같은 디렉터리에 여러 DevezCode 세션이 있으면 모두 같은 last prompt 를 공유한다 (acceptable MVP).</summary>
    public event Action<string, string>? LastPromptChanged;

    /// <summary>경로 정규화 — 트레일링 슬래시 제거 + 풀패스화 (MainWindow 의 project.Path 와 매칭 가능하게).</summary>
    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p.TrimEnd('\\', '/'); }
    }

    public void Start()
    {
        // codex 는 CodexHookService, claude 는 SessionLastMessageService 가 처리. 이 서비스는 opencode 만.
        _opencodePoll.Start();
    }

    /// <summary>MainWindow 가 세션 활성화 시 호출 — 이 (workingDir, agentId) 추적 시작 + 1회 즉시 갱신.</summary>
    public void TrackSession(string workingDir, string agentId)
    {
        if (string.IsNullOrEmpty(workingDir) || string.IsNullOrEmpty(agentId)) return;
        var norm = NormalizePath(workingDir);
        var key = new TrackKey(norm, agentId);
        _lastSeen[key] = DateTime.UtcNow;
        ScanForKey(key);
    }

    // ── opencode — SQLite 라 subprocess 로 export 호출 ──────────
    // `opencode session list -j` (또는 기본 출력) 에서 workingDir 매칭 세션 ID 를 찾고
    // `opencode export <id>` 로 JSON 받아 마지막 user message 추출.
    private void PollOpenCode()
    {
        if (!AgentRegistry.IsInstalled(AgentRegistry.Find("opencode")!)) return;
        // 모든 추적 키에 대해 검사
        foreach (var key in _lastSeen.Keys)
        {
            if (key.AgentId != "opencode") continue;
            try { ScanOpenCodeForKey(key); }
            catch { /* 다음 폴링 */ }
        }
    }

    private void ScanOpenCodeForKey(TrackKey key)
    {
        try
        {
            // 1) 세션 목록에서 cwd 매칭 세션 ID 찾기
            var listOutput = RunCommand("opencode", "session list");
            if (string.IsNullOrEmpty(listOutput)) return;
            string? matchedSessionId = null;
            string? matchedTitle = null;
            foreach (var line in listOutput.Split('\n'))
            {
                // 기본 포맷: "ses_xxx  Title text  updated"
                // session ID 는 ses_ 접두사 (24자 hex-ish)
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("ses_")) continue;
                var idEnd = trimmed.IndexOfAny(new[] { ' ', '\t' });
                if (idEnd < 0) continue;
                var sid = trimmed.Substring(0, idEnd);
                // 해당 세션 export 해서 cwd 확인
                var exported = RunCommand("opencode", $"export {sid}");
                if (string.IsNullOrEmpty(exported)) continue;
                if (exported.Contains($"\"directory\": \"{key.WorkingDir.Replace("\\", "\\\\")}\"", StringComparison.OrdinalIgnoreCase) ||
                    exported.Contains($"\"directory\":\"{key.WorkingDir.Replace("\\", "\\\\")}\"", StringComparison.OrdinalIgnoreCase))
                {
                    matchedSessionId = sid;
                    // title 추출 (export JSON 의 info.title)
                    try
                    {
                        using var doc = JsonDocument.Parse(exported);
                        if (doc.RootElement.TryGetProperty("info", out var info) &&
                            info.TryGetProperty("title", out var t))
                            matchedTitle = t.GetString();
                    }
                    catch { }
                    break;
                }
            }
            if (matchedSessionId == null) return;

            // 2) 가장 최근 user message 를 export 의 messages 배열에서 추출
            // export 포맷은 messages: [{ info: { role: "user" }, parts: [{ type: "text", text: "..." }] }, ...]
            var exportJson = RunCommand("opencode", $"export {matchedSessionId}");
            if (string.IsNullOrEmpty(exportJson)) return;
            try
            {
                using var doc = JsonDocument.Parse(exportJson);
                if (!doc.RootElement.TryGetProperty("messages", out var msgs)) return;
                string? lastUser = null;
                foreach (var msg in msgs.EnumerateArray().Reverse())
                {
                    if (msg.TryGetProperty("info", out var info) &&
                        info.TryGetProperty("role", out var r) && r.GetString() == "user")
                    {
                        if (msg.TryGetProperty("parts", out var parts))
                        {
                            foreach (var part in parts.EnumerateArray())
                            {
                                if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text" &&
                                    part.TryGetProperty("text", out var text))
                                {
                                    lastUser = text.GetString();
                                    break;
                                }
                            }
                        }
                        break;
                    }
                }
                if (lastUser == null && matchedTitle != null) lastUser = matchedTitle;
                if (lastUser != null) EmitIfChanged(key.WorkingDir, "opencode", lastUser);
                _lastSeen[key] = DateTime.UtcNow;
            }
            catch { }
        }
        catch { }
    }

    // ── 유틸 ─────────────────────────────────────────────────────
    private static string? ExtractTextContent(JsonElement parent)
    {
        if (!parent.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind != JsonValueKind.Array) return null;
        var sb = new StringBuilder();
        foreach (var c in content.EnumerateArray())
        {
            var type = c.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is "text" or "input_text")
            {
                if (c.TryGetProperty("text", out var txt)) sb.Append(txt.GetString());
            }
        }
        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private void EmitIfChanged(string workingDir, string agentId, string prompt)
    {
        var norm = NormalizePath(workingDir);
        var key = new TrackKey(norm, agentId);
        var compact = OneLine(prompt);
        if (_lastPrompt.TryGetValue(key, out var prev) && prev == compact) return;
        _lastPrompt[key] = compact;
        LastPromptChanged?.Invoke(norm, compact);
    }

    private void ScanForKey(TrackKey key)
    {
        // codex 는 CodexHookService 가 처리 — 이 서비스는 opencode 만 담당.
        if (key.AgentId == "opencode") ScanOpenCodeForKey(key);
    }

    private static string OneLine(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        // 200자 제한 (헤더 부제용 — 너무 길면 잘라냄)
        if (s.Length > 200) s = s.Substring(0, 200);
        // 연속 공백 정리
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s;
    }

    private static string? RunCommand(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(); } catch { }
            }
            return p.StandardOutput.ReadToEnd();
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _opencodePoll.Stop(); } catch { }
    }
}
