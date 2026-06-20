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

/// <summary>codex/opencode/gjc 등 비-Claude 에이전트 중 codex 를 제외한 것들의 마지막 프롬프트 추적.
/// codex 는 CodexHookService (Claude 와 동일하게 ~/.codex/hooks.json 훅) 가 처리.
/// 이 서비스는 opencode(SQLite) / gjc(JSONL) 가 자기 저장소에 직접 쓴 세션 파일/DB 를 읽어
/// 가장 최근 user message 를 추출한다. (workingDir, agentId) 단위로 추적하며
/// 활성화된 세션에 대해서만 폴링한다 (성능).</summary>
public sealed class AgentLastMessageService : IDisposable
{
    // ── 추적 대상: (workingDir, agentId) → 마지막으로 본 마지막 user prompt 와 비교용 mtime/seq ──
    private sealed record TrackKey(string WorkingDir, string AgentId);
    private readonly ConcurrentDictionary<TrackKey, string> _lastPrompt = new();
    private readonly ConcurrentDictionary<TrackKey, DateTime> _lastSeen = new();

    // codex/gjc — FileSystemWatcher
    private FileSystemWatcher? _codexWatcher;
    private FileSystemWatcher? _gjcWatcher;
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
        // codex 는 CodexHookService (Claude 정합 훅 패턴) 가 처리 — JSONL 폴링은 더 이상 사용 X.
        try
        {
            var gjcDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gjc", "agent", "sessions");
            if (Directory.Exists(gjcDir))
            {
                _gjcWatcher = new FileSystemWatcher(gjcDir, "*.jsonl")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };
                _gjcWatcher.Changed += (_, e) => ScanGjcFile(e.FullPath);
                _gjcWatcher.Created += (_, e) => ScanGjcFile(e.FullPath);
            }
        }
        catch { }

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

    // ── codex JSONL ──────────────────────────────────────────────
    // 파일: ~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<session_id>.jsonl
    // 첫 줄: session_meta (cwd 포함)
    // user 메시지: { "type":"response_item", "payload":{ "type":"message", "role":"user", "content":[{"type":"input_text","text":"..."}] } }
    private void ScanCodexFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            string? cwd = null;
            string? lastUserPrompt = null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "session_meta")
                    {
                        if (root.TryGetProperty("payload", out var p) &&
                            p.TryGetProperty("cwd", out var c))
                            cwd = c.GetString();
                    }
                    else if (type == "response_item" && root.TryGetProperty("payload", out var p))
                    {
                        var ptype = p.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                        var role = p.TryGetProperty("role", out var r) ? r.GetString() : null;
                        if (ptype == "message" && role == "user")
                        {
                            lastUserPrompt = ExtractTextContent(p);
                        }
                    }
                }
                catch { /* 한 줄 파싱 실패는 무시 */ }
            }
            if (cwd != null && lastUserPrompt != null)
            {
                EmitIfChanged(cwd, "codex", lastUserPrompt);
                _lastSeen[new TrackKey(cwd, "codex")] = DateTime.UtcNow; // 갱신 시각 기록
            }
        }
        catch { /* 파일 잠김 등 — 다음 이벤트에 재시도 */ }
    }

    private void ScanCodexForKey(TrackKey key)
    {
        try
        {
            var codexDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            if (!Directory.Exists(codexDir)) return;
            // cwd 매칭 + 가장 최근 mtime
            string? bestFile = null;
            DateTime bestMtime = DateTime.MinValue;
            foreach (var f in Directory.EnumerateFiles(codexDir, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    var mtime = File.GetLastWriteTimeUtc(f);
                    if (mtime <= bestMtime) continue;
                    // 첫 줄만 읽어 cwd 추출 (성능)
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    var firstLine = sr.ReadLine();
                    if (firstLine == null) continue;
                    using var doc = JsonDocument.Parse(firstLine);
                    if (doc.RootElement.TryGetProperty("payload", out var p) &&
                        p.TryGetProperty("cwd", out var c) &&
                        string.Equals(c.GetString(), key.WorkingDir, StringComparison.OrdinalIgnoreCase))
                    {
                        bestFile = f;
                        bestMtime = mtime;
                    }
                }
                catch { }
            }
            if (bestFile != null) ScanCodexFile(bestFile);
        }
        catch { }
    }

    // ── gjc JSONL ────────────────────────────────────────────────
    // 파일: ~/.gjc/agent/sessions/<encoded-path>/<ts>_<uuid>.jsonl
    // 첫 줄: { "type":"session", "cwd":"...", "title":"..." }
    // user 메시지: { "type":"message", "message":{ "role":"user", "content":[{"type":"text","text":"..."}] } }
    private void ScanGjcFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            // 디렉터리 경로에서 workingDir 역추출 (gjc 는 <encoded-path> 사용)
            // 단순화: 파일 첫 줄의 cwd 를 신뢰
            string? cwd = null;
            string? lastUserPrompt = null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            bool firstLine = true;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (firstLine && type == "session")
                    {
                        if (root.TryGetProperty("cwd", out var c)) cwd = c.GetString();
                        firstLine = false;
                        continue;
                    }
                    if (type == "message" && root.TryGetProperty("message", out var msg))
                    {
                        var role = msg.TryGetProperty("role", out var r) ? r.GetString() : null;
                        if (role == "user")
                        {
                            lastUserPrompt = ExtractTextContent(msg);
                        }
                    }
                    firstLine = false;
                }
                catch { }
            }
            if (cwd != null && lastUserPrompt != null)
            {
                EmitIfChanged(cwd, "gajaecode", lastUserPrompt);
                _lastSeen[new TrackKey(cwd, "gajaecode")] = DateTime.UtcNow;
            }
        }
        catch { }
    }

    private void ScanGjcForKey(TrackKey key)
    {
        try
        {
            var sessionsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gjc", "agent", "sessions");
            if (!Directory.Exists(sessionsRoot)) return;
            // gjc 는 sessions/<encoded-path>/ 에 저장. <encoded-path> 가 cwd 의 인코딩.
            // 인코딩 규칙을 정확히 모르면 모든 세션 파일을 훑어 cwd 매칭. (mtime desc 상위 1개)
            string? bestFile = null;
            DateTime bestMtime = DateTime.MinValue;
            foreach (var f in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    var mtime = File.GetLastWriteTimeUtc(f);
                    if (mtime <= bestMtime) continue;
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    var first = sr.ReadLine();
                    if (first == null) continue;
                    using var doc = JsonDocument.Parse(first);
                    if (doc.RootElement.TryGetProperty("cwd", out var c) &&
                        string.Equals(c.GetString(), key.WorkingDir, StringComparison.OrdinalIgnoreCase))
                    {
                        bestFile = f;
                        bestMtime = mtime;
                    }
                }
                catch { }
            }
            if (bestFile != null) ScanGjcFile(bestFile);
        }
        catch { }
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
        switch (key.AgentId)
        {
            // codex 는 CodexHookService 가 처리. 이 경로는 호출되지 않지만 호환성 위해 보존.
            case "codex": break;
            case "opencode": ScanOpenCodeForKey(key); break;
            case "gajaecode": ScanGjcForKey(key); break;
        }
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
        try { _codexWatcher?.Dispose(); } catch { }
        try { _gjcWatcher?.Dispose(); } catch { }
        try { _opencodePoll.Stop(); } catch { }
    }
}
