using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace DevezCode.Services;

/// <summary>안티그래비티(agy) 훅이 방별로 떨군 busy/sessions 파일 감시 + transcript 폴링.
/// GrokHookService 와 동일 패턴 (roomId 키).
/// 실측(agy 1.1.1, 2026-07-13):
/// · 훅에는 UserPromptSubmit 이 없고 Stop/SessionStart 훅 프로세스는 조기 취소될 수 있다
///   (대화형에서도 Stop 유실 확인) — busy-ON 은 PreInvocation/PostToolUse,
///   busy-OFF 는 transcript 폴러가 주력, stale failsafe(120s)가 최후 보루.
/// · agy 는 대화별 transcript 를 ~/.gemini/antigravity-cli/brain/&lt;conv&gt;/.system_generated/logs/
///   transcript_full.jsonl 에 실시간 기록한다(레코드: type=USER_INPUT|PLANNER_RESPONSE|도구…).
///   내용이 있는 마지막 PLANNER_RESPONSE가 정착되면 idle, USER_INPUT/도구면 running.
///   도구 실행 직전 PLANNER_RESPONSE는 내용이 비어 있으므로 장시간 도구 중 idle 오탐을 막는다.
/// · lastmsg 는 마지막 USER_INPUT 레코드의 &lt;USER_REQUEST&gt; 에서 추출.</summary>
public sealed class AntigravityHookService : IDisposable
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "antigravity");
    private static string BusyDir => Path.Combine(BaseDir, "busy");
    private static string WaitingDir => Path.Combine(BaseDir, "waiting");
    private static string CompletedDir => Path.Combine(BaseDir, "completed");
    private static string SessionDir => Path.Combine(BaseDir, "sessions");

    private static string SessionPath(string roomId) =>
        Path.Combine(SessionDir, Sanitize(roomId) + ".txt");
    private static string PreviousSessionPath(string roomId) =>
        Path.Combine(SessionDir, Sanitize(roomId) + ".prev.txt");
    private static string RootSessionPath(string roomId) =>
        Path.Combine(SessionDir, Sanitize(roomId) + ".root.txt");
    private static string SessionTransitionPath(string roomId) =>
        Path.Combine(SessionDir, Sanitize(roomId) + ".ended.txt");

    /// <summary>running 파일이 이 시간 넘게 갱신 없으면 idle 로 강제 전이(최후 보루 —
    /// 평시엔 transcript 폴러가 훨씬 먼저 idle 을 확정한다).</summary>
    private static readonly TimeSpan StaleBusyTtl = TimeSpan.FromSeconds(120);

    /// <summary>마지막 레코드가 PLANNER_RESPONSE 인 채 이 시간 지나면 응답 종료로 판정.
    /// 도구 사이 "생각" 구간이 이보다 길면 스피너가 잠깐 꺼졌다 다음 도구에서 다시 켜질 수
    /// 있는 절충값 — stuck-ON(120s) 보다 낫다.</summary>
    private static readonly TimeSpan ResponseSettle = TimeSpan.FromSeconds(10);

    /// <summary>transcript 가 이보다 오래되면 running 승격 근거로 안 씀(크래시 잔재 방지).</summary>
    private static readonly TimeSpan TranscriptFreshCap = TimeSpan.FromMinutes(3);

    private FileSystemWatcher? _busyWatcher;
    private FileSystemWatcher? _waitingWatcher;
    private FileSystemWatcher? _sessionWatcher;
    private System.Threading.Timer? _staleTimer;
    private System.Threading.Timer? _transcriptTimer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastEmittedMsg = new();
    private int _transcriptPolling; // 재진입 방지

    public event Action<string, bool>? BusyChanged;
    public event Action<string, bool>? WaitingChoiceChanged;
    public event Action<string, string>? SessionChanged;
    /// <summary>(roomId, 마지막 user prompt) — transcript 폴링에서 추출.</summary>
    public event Action<string, string>? MessageChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(BusyDir);
            Directory.CreateDirectory(WaitingDir);
            Directory.CreateDirectory(CompletedDir);
            Directory.CreateDirectory(SessionDir);

            // 이전 실행이 남긴 busy 상태는 신뢰 불가 — 시작 시 리셋(스피너 stuck-ON 방지).
            foreach (var f in Directory.EnumerateFiles(BusyDir, "*.txt"))
                try { File.Delete(f); } catch { }
            foreach (var f in Directory.EnumerateFiles(WaitingDir, "*.txt"))
                try { File.Delete(f); } catch { }
            foreach (var f in Directory.EnumerateFiles(CompletedDir, "*.flag"))
                try { File.Delete(f); } catch { }

            _busyWatcher = new FileSystemWatcher(BusyDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _busyWatcher.Changed += (_, e) => EmitBusy(e.FullPath);
            _busyWatcher.Created += (_, e) => EmitBusy(e.FullPath);
            _busyWatcher.Renamed += (_, e) => EmitBusy(e.FullPath);

            _waitingWatcher = new FileSystemWatcher(WaitingDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _waitingWatcher.Changed += (_, e) => EmitWaiting(e.FullPath);
            _waitingWatcher.Created += (_, e) => EmitWaiting(e.FullPath);
            _waitingWatcher.Renamed += (_, e) => EmitWaiting(e.FullPath);

            _sessionWatcher = new FileSystemWatcher(SessionDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _sessionWatcher.Changed += (_, e) => EmitSession(e.FullPath);
            _sessionWatcher.Created += (_, e) => EmitSession(e.FullPath);
            _sessionWatcher.Renamed += (_, e) => EmitSession(e.FullPath);
            foreach (var f in Directory.EnumerateFiles(SessionDir, "*.txt")) EmitSession(f);

            _staleTimer = new System.Threading.Timer(_ => SweepStaleBusy(), null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15));
            _transcriptTimer = new System.Threading.Timer(_ => PollTranscripts(), null,
                TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2));
        }
        catch { /* 감시 실패해도 앱은 계속 */ }
    }

    // ── transcript 폴링 — 빠른 idle 확정 + lastmsg ─────────────────

    private void PollTranscripts()
    {
        if (Interlocked.Exchange(ref _transcriptPolling, 1) == 1) return;
        try
        {
            if (!Directory.Exists(SessionDir)) return;
            foreach (var sessionFile in Directory.EnumerateFiles(SessionDir, "*.txt"))
            {
                try { PollRoomTranscript(sessionFile); }
                catch { /* 다음 방 */ }
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _transcriptPolling, 0); }
    }

    private void PollRoomTranscript(string sessionFile)
    {
        var room = Path.GetFileNameWithoutExtension(sessionFile);
        if (string.IsNullOrEmpty(room)) return;
        var conv = TryRead(sessionFile);
        if (conv == null || !Guid.TryParse(conv, out _)) return;

        var transcript = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "antigravity-cli", "brain", conv, ".system_generated", "logs", "transcript_full.jsonl");
        if (!File.Exists(transcript)) return;

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(transcript);
        var (lastType, lastUserText, lastPlannerHasContent) = ReadTranscriptTail(transcript);
        if (lastType == null) return;

        // lastmsg — 마지막 user prompt 를 헤더에 (변화 있을 때만 emit).
        if (lastUserText != null)
        {
            var compact = OneLine(lastUserText);
            if (compact.Length > 0
                && (!_lastEmittedMsg.TryGetValue(room, out var prev) || prev != compact))
            {
                _lastEmittedMsg[room] = compact;
                MessageChanged?.Invoke(room, compact);
            }
        }

        var busyPath = Path.Combine(BusyDir, room + ".txt");
        var busyNow = TryRead(busyPath) ?? "";
        bool busyRunning = busyNow.StartsWith("running", StringComparison.OrdinalIgnoreCase);

        if (lastType == "PLANNER_RESPONSE")
        {
            // 도구 직전의 빈 PLANNER_RESPONSE는 완료로 보지 않는다. 공식 PreToolUse는 권한 결정을
            // 강제하므로 관찰 훅으로 쓸 수 없고, 이 구분이 장시간 도구의 busy를 안전하게 유지한다.
            if (busyRunning && lastPlannerHasContent && age >= ResponseSettle)
                WriteBusyFile(busyPath, "idle");
        }
        else if (!busyRunning && age <= TranscriptFreshCap)
        {
            // USER_INPUT/도구/시스템 레코드가 방금 기록됨 = 턴 진행 중 — 훅이 못 켠 스피너 보강
            // (순수 텍스트 응답은 Pre/PostToolUse 훅이 안 와서 이 경로가 유일한 busy-ON).
            WriteBusyFile(busyPath, "running");
        }
    }

    /// <summary>transcript 끝부분에서 (마지막 레코드 type, 마지막 USER_INPUT 본문,
    /// 마지막 PLANNER_RESPONSE의 표시 내용 유무) 추출.
    /// 큰 파일 대비 끝 64KB 만 읽는다. 실패 시 (null, null).</summary>
    private static (string? lastType, string? lastUserText, bool lastPlannerHasContent) ReadTranscriptTail(string path)
    {
        try
        {
            const int tailBytes = 64 * 1024;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > tailBytes) fs.Seek(-tailBytes, SeekOrigin.End);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var text = sr.ReadToEnd();

            string? lastType = null, lastUser = null;
            bool lastPlannerHasContent = false;
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                // tail 절단으로 깨진 첫 줄 등은 파싱 실패로 자연히 스킵.
                if (trimmed.Length == 0 || trimmed[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    if (!doc.RootElement.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
                        continue;
                    lastType = t.GetString();
                    if (lastType == "PLANNER_RESPONSE")
                    {
                        lastPlannerHasContent = doc.RootElement.TryGetProperty("content", out var plannerContent)
                            && plannerContent.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(plannerContent.GetString());
                    }
                    if (lastType == "USER_INPUT"
                        && doc.RootElement.TryGetProperty("content", out var c)
                        && c.ValueKind == JsonValueKind.String)
                        lastUser = ExtractUserRequest(c.GetString() ?? "");
                }
                catch { /* 불완전 줄 무시 */ }
            }
            return (lastType, lastUser, lastPlannerHasContent);
        }
        catch { return (null, null, false); }
    }

    /// <summary>USER_INPUT content 의 &lt;USER_REQUEST&gt;…&lt;/USER_REQUEST&gt; 본문. 래퍼 없으면 원문.</summary>
    internal static string ExtractUserRequest(string content)
    {
        var m = Regex.Match(content, @"<USER_REQUEST>\s*(.*?)\s*</USER_REQUEST>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : content;
    }

    private static string OneLine(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 200 ? s.Substring(0, 200) : s;
    }

    /// <summary>busy 파일 원자적 쓰기(temp+move) — 워처 경유로 BusyChanged 가 자연히 발화된다.</summary>
    private static void WriteBusyFile(string path, string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, value);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    /// <summary>오래 갱신 없는 running 파일을 idle 로 강제 전이(최후 보루).
    /// mtime 은 매 스윕마다 새로 읽는다(캐시된 stale mtime 함정 방지).</summary>
    private void SweepStaleBusy()
    {
        try
        {
            if (!Directory.Exists(BusyDir)) return;
            foreach (var f in Directory.EnumerateFiles(BusyDir, "*.txt"))
            {
                try
                {
                    var status = TryRead(f);
                    if (status == null || !status.StartsWith("running", StringComparison.OrdinalIgnoreCase)) continue;
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < StaleBusyTtl) continue;
                    WriteBusyFile(f, "idle");
                }
                catch { /* 다음 스윕 */ }
            }
        }
        catch { }
    }

    private void EmitBusy(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (status == null || string.IsNullOrWhiteSpace(status))
        {
            // 빈 값 무시 금지 — 재확인 후 idle 확정 (.knowledge/훅-상태파일-원자적쓰기.md).
            ReEmitBusyAfterSettleAsync(path, room);
            return;
        }
        BusyChanged?.Invoke(room, status.StartsWith("running", StringComparison.OrdinalIgnoreCase));
    }

    private async void ReEmitBusyAfterSettleAsync(string path, string room)
    {
        await System.Threading.Tasks.Task.Delay(120);
        var status = TryRead(path);
        BusyChanged?.Invoke(room,
            !string.IsNullOrWhiteSpace(status)
            && status.StartsWith("running", StringComparison.OrdinalIgnoreCase));
    }

    private void EmitWaiting(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (string.IsNullOrWhiteSpace(status))
        {
            _ = ReEmitWaitingAfterSettleAsync(path, room);
            return;
        }
        WaitingChoiceChanged?.Invoke(room, status.Equals("waiting", StringComparison.OrdinalIgnoreCase));
    }

    private async System.Threading.Tasks.Task ReEmitWaitingAfterSettleAsync(string path, string room)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(120).ConfigureAwait(false);
            var status = TryRead(path);
            WaitingChoiceChanged?.Invoke(room,
                !string.IsNullOrWhiteSpace(status)
                && status!.Equals("waiting", StringComparison.OrdinalIgnoreCase));
        }
        catch { }
    }

    private void EmitSession(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        if (room.EndsWith(".prev", StringComparison.OrdinalIgnoreCase)
            || room.EndsWith(".root", StringComparison.OrdinalIgnoreCase)
            || room.EndsWith(".ended", StringComparison.OrdinalIgnoreCase)) return;
        var sid = TryRead(path);
        if (sid != null && Guid.TryParse(sid, out var parsed))
            SessionChanged?.Invoke(room, parsed.ToString());
    }

    private static string? TryRead(string path)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                return sr.ReadToEnd().Trim();
            }
            catch (IOException) { Thread.Sleep(20); }
            catch { return null; }
        }
        return null;
    }

    public static string? LoadTrackedSessionId(string roomId)
    {
        return LoadSessionId(SessionPath(roomId));
    }

    public static string? LoadPreviousTrackedSessionId(string roomId)
    {
        return LoadSessionId(PreviousSessionPath(roomId));
    }

    public static bool IsRootTrackedSession(string roomId, string? sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsed)) return false;
        return string.Equals(LoadSessionId(RootSessionPath(roomId)), parsed.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    public static void RestoreTrackedSessionId(string roomId, string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsed)) return;
        try
        {
            Directory.CreateDirectory(SessionDir);
            WriteAtomic(RootSessionPath(roomId), parsed.ToString());
            WriteAtomic(SessionPath(roomId), parsed.ToString());
            try { File.Delete(SessionTransitionPath(roomId)); } catch { }
        }
        catch { }
    }

    public static void ResetTrackedSessionIds(string roomId)
    {
        foreach (var path in new[]
        {
            SessionPath(roomId), PreviousSessionPath(roomId), RootSessionPath(roomId),
            SessionTransitionPath(roomId)
        })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string? LoadSessionId(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var sid = File.ReadAllText(path).Trim();
            return Guid.TryParse(sid, out var parsed) ? parsed.ToString() : null;
        }
        catch { return null; }
    }

    private static void WriteAtomic(string path, string value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, value, Encoding.ASCII);
            File.Move(temp, path, true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>훅 스크립트 roomSafe 규칙과 동일: 비-워드 문자 제거.</summary>
    private static string Sanitize(string roomId)
    {
        var sb = new StringBuilder(roomId.Length);
        foreach (var c in roomId)
            if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
        return sb.ToString();
    }

    public void Dispose()
    {
        _busyWatcher?.Dispose();
        _waitingWatcher?.Dispose();
        _sessionWatcher?.Dispose();
        _staleTimer?.Dispose();
        _transcriptTimer?.Dispose();
    }
}
