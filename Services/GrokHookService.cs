using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace DevezCode.Services;

/// <summary>Grok 훅이 방별로 떨군 lastmsg/busy/sessions 파일을 감시 + events.jsonl 폴링.
/// 훅(UserPromptSubmit/Stop)이 빠른 경로, events 폴러가 장시간 턴 중 조기 idle/UI 해제 복구.
/// CodexHookService 와 동일 roomId 키 패턴.
/// <para>
/// 실측(Grok events.jsonl): 한 user 턴 안에 loop_started 가 수십 회 반복되고
/// turn_started~turn_ended 가 진짜 턴 경계다. Stop/Notification 이 중간에 idle 을 쓰거나
/// ESC 가 UI 만 꺼도, 폴러가 열린 턴/미완료 도구를 보면 busy 를 다시 켠다.
/// </para></summary>
public sealed class GrokHookService : IDisposable
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "grok");
    private static string LastmsgDir => Path.Combine(BaseDir, "lastmsg");
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
    private static string BusyPath(string roomId) =>
        Path.Combine(BusyDir, Sanitize(roomId) + ".txt");
    private static string CompletedPath(string roomId) =>
        Path.Combine(CompletedDir, Sanitize(roomId) + ".flag");

    /// <summary>turn_ended 직후 훅 Stop 과 경합할 때 스피너가 깜빡이지 않게 짧은 정착.</summary>
    private static readonly TimeSpan TurnEndSettle = TimeSpan.FromSeconds(2);

    /// <summary>turn_* 이벤트가 없는 구형 로그용 — 최근 활동이 이 시간 안이면 running 유지.</summary>
    private static readonly TimeSpan ActivityFresh = TimeSpan.FromSeconds(45);

    /// <summary>열린 도구 없이 활동만 멈춘 채 이 시간 지나면 idle(하드킬/유실 훅 잔재 방지).</summary>
    private static readonly TimeSpan StaleRunningCap = TimeSpan.FromMinutes(30);

    private FileSystemWatcher? _lastmsgWatcher;
    private FileSystemWatcher? _busyWatcher;
    private FileSystemWatcher? _waitingWatcher;
    private FileSystemWatcher? _sessionWatcher;
    private Timer? _eventsTimer;
    private int _eventsPolling;
    private readonly ConcurrentDictionary<string, string> _eventsPathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _lastPolledBusy = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string, string>? MessageChanged;
    public event Action<string, bool>? BusyChanged;
    public event Action<string, bool>? WaitingChoiceChanged;
    public event Action<string, string>? GrokSessionChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(LastmsgDir);
            Directory.CreateDirectory(BusyDir);
            Directory.CreateDirectory(WaitingDir);
            Directory.CreateDirectory(CompletedDir);
            Directory.CreateDirectory(SessionDir);

            foreach (var f in Directory.EnumerateFiles(BusyDir, "*.txt"))
                try { File.Delete(f); } catch { }
            foreach (var f in Directory.EnumerateFiles(WaitingDir, "*.txt"))
                try { File.Delete(f); } catch { }
            foreach (var f in Directory.EnumerateFiles(CompletedDir, "*.flag"))
                try { File.Delete(f); } catch { }

            _lastmsgWatcher = new FileSystemWatcher(LastmsgDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _lastmsgWatcher.Changed += (_, e) => EmitLastmsg(e.FullPath);
            _lastmsgWatcher.Created += (_, e) => EmitLastmsg(e.FullPath);
            _lastmsgWatcher.Renamed += (_, e) => EmitLastmsg(e.FullPath);
            foreach (var f in Directory.EnumerateFiles(LastmsgDir, "*.txt")) EmitLastmsg(f);

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

            // 장시간 턴 조기 idle 복구 — 훅만으로는 Stop/Notification/ESC UI 해제를 못 되돌림.
            _eventsTimer = new Timer(_ => PollEvents(), null,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
        }
        catch { /* 감시 실패해도 앱은 계속 */ }
    }

    // ── events.jsonl 폴링 — 열린 턴/미완료 도구면 busy 재무장 ─────────

    private void PollEvents()
    {
        if (Interlocked.Exchange(ref _eventsPolling, 1) == 1) return;
        try
        {
            if (!Directory.Exists(SessionDir)) return;
            foreach (var sessionFile in Directory.EnumerateFiles(SessionDir, "*.txt"))
            {
                try { PollRoomEvents(sessionFile); }
                catch { /* 다음 방 */ }
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _eventsPolling, 0); }
    }

    private void PollRoomEvents(string sessionFile)
    {
        var room = Path.GetFileNameWithoutExtension(sessionFile);
        if (string.IsNullOrEmpty(room)
            || room.EndsWith(".prev", StringComparison.OrdinalIgnoreCase)
            || room.EndsWith(".root", StringComparison.OrdinalIgnoreCase)
            || room.EndsWith(".ended", StringComparison.OrdinalIgnoreCase))
            return;

        var sid = TryRead(sessionFile);
        if (sid == null || !Guid.TryParse(sid, out var parsed)) return;
        sid = parsed.ToString();
        if (!IsRootTrackedSession(room, sid)) return;

        var eventsPath = ResolveEventsPath(room, sid);
        if (eventsPath == null || !File.Exists(eventsPath)) return;

        var truth = AnalyzeEvents(eventsPath);
        if (truth == null) return;

        var busyPath = BusyPath(room);
        var busyNow = TryRead(busyPath) ?? "";
        bool fileRunning = busyNow.Equals("running", StringComparison.OrdinalIgnoreCase);
        bool wantRunning = truth.IsActive;

        if (wantRunning == fileRunning)
        {
            _lastPolledBusy[room] = wantRunning;
            return;
        }

        // idle→running: completed 플래그도 지워 이후 툴 훅이 waiting 을 다시 쓸 수 있게 한다.
        if (wantRunning)
        {
            try { File.Delete(CompletedPath(room)); } catch { }
            WriteBusyFile(busyPath, "running");
            DiagLog.Write($"busy[{room}] grok events 정정: idle→running ({truth.Reason})");
        }
        else
        {
            // running→idle 은 turn_ended 정착 후에만. 훅이 이미 idle 이면 파일 쓰기 생략.
            WriteBusyFile(busyPath, "idle");
            DiagLog.Write($"busy[{room}] grok events 정정: running→idle ({truth.Reason})");
        }
        _lastPolledBusy[room] = wantRunning;
    }

    private string? ResolveEventsPath(string room, string sessionId)
    {
        if (_eventsPathCache.TryGetValue(room, out var cached) && File.Exists(cached))
        {
            // 캐시 경로의 세션 폴더명이 현재 sid 와 다르면 무효(세션 전환).
            var dirName = Path.GetFileName(Path.GetDirectoryName(cached));
            if (string.Equals(dirName, sessionId, StringComparison.OrdinalIgnoreCase))
                return cached;
            _eventsPathCache.TryRemove(room, out _);
        }

        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "sessions");
            if (!Directory.Exists(root)) return null;

            foreach (var cwdDir in Directory.EnumerateDirectories(root))
            {
                var p = Path.Combine(cwdDir, sessionId, "events.jsonl");
                if (!File.Exists(p)) continue;
                _eventsPathCache[room] = p;
                return p;
            }

            var hit = new DirectoryInfo(root).GetFiles("events.jsonl", SearchOption.AllDirectories)
                .Where(f => string.Equals(f.Directory?.Name, sessionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (hit != null)
            {
                _eventsPathCache[room] = hit.FullName;
                return hit.FullName;
            }
        }
        catch { }
        return null;
    }

    private sealed class EventsTruth
    {
        public bool IsActive;
        public string Reason = "";
    }

    /// <summary>events.jsonl 꼬리에서 턴/도구 진행 여부 판정.
    /// turn_started 가 turn_ended 보다 최신이면 진행 중(장시간 도구 중 이벤트 공백 포함).
    /// tool_started 가 짝 tool_completed 보다 많으면 미완료 도구 있음.</summary>
    private static EventsTruth? AnalyzeEvents(string path)
    {
        try
        {
            const int tailBytes = 96 * 1024;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return new EventsTruth { IsActive = false, Reason = "empty" };
            if (fs.Length > tailBytes) fs.Seek(-tailBytes, SeekOrigin.End);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var text = sr.ReadToEnd();

            DateTimeOffset? lastTurnStarted = null, lastTurnEnded = null, lastActivity = null;
            // 현재 열린 턴 안의 도구 깊이만 센다(꼬리 절단으로 과거 start 가 잘려도
            // 턴 시작 이후 구간만 보면 과대 openTools 가 안 생긴다).
            int turnToolDepth = 0;
            bool sawTurn = false;
            bool inOpenTurn = false;
            string? lastType = null;
            string? lastPhase = null;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var tEl) || tEl.ValueKind != JsonValueKind.String)
                        continue;
                    var type = tEl.GetString() ?? "";
                    lastType = type;

                    DateTimeOffset? ts = null;
                    if (root.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(tsEl.GetString(), out var parsedTs))
                        ts = parsedTs;
                    if (ts != null) lastActivity = ts;

                    switch (type)
                    {
                        case "turn_started":
                            sawTurn = true;
                            lastTurnStarted = ts ?? lastTurnStarted;
                            inOpenTurn = true;
                            turnToolDepth = 0;
                            break;
                        case "turn_ended":
                            sawTurn = true;
                            lastTurnEnded = ts ?? lastTurnEnded;
                            inOpenTurn = false;
                            turnToolDepth = 0;
                            break;
                        case "tool_started":
                            if (inOpenTurn || !sawTurn) turnToolDepth++;
                            break;
                        case "tool_completed":
                            if ((inOpenTurn || !sawTurn) && turnToolDepth > 0) turnToolDepth--;
                            break;
                        case "phase_changed":
                            if (root.TryGetProperty("phase", out var pEl) && pEl.ValueKind == JsonValueKind.String)
                                lastPhase = pEl.GetString();
                            break;
                    }
                }
                catch { /* 잘린 줄 */ }
            }

            var now = DateTimeOffset.UtcNow;
            if (turnToolDepth > 0)
                return new EventsTruth { IsActive = true, Reason = $"openTools={turnToolDepth}" };

            if (sawTurn && lastTurnStarted != null)
            {
                bool turnOpen = lastTurnEnded == null || lastTurnStarted > lastTurnEnded;
                if (turnOpen)
                {
                    // 비정상 잔재(프로세스 킬 후 turn_ended 없음) — 활동이 너무 오래면 idle.
                    var age = lastActivity != null ? now - lastActivity.Value : now - lastTurnStarted.Value;
                    if (age > StaleRunningCap)
                        return new EventsTruth { IsActive = false, Reason = $"staleTurn age={age.TotalMinutes:F0}m" };
                    return new EventsTruth { IsActive = true, Reason = "turn_open" };
                }

                // turn_ended 가 최신 — 정착 시간 지나면 idle.
                var endAge = lastTurnEnded != null ? now - lastTurnEnded.Value : TimeSpan.MaxValue;
                if (endAge < TurnEndSettle)
                    return new EventsTruth { IsActive = true, Reason = "turn_end_settle" };
                return new EventsTruth { IsActive = false, Reason = "turn_ended" };
            }

            // 구형: turn_* 없음 → 최근 활동/페이즈로 추정.
            if (lastActivity != null)
            {
                var age = now - lastActivity.Value;
                if (age <= ActivityFresh)
                {
                    bool activePhase = lastPhase is "waiting_for_model" or "streaming_reasoning"
                        or "streaming_text" or "tool_execution" or "permission_prompt"
                        || lastType is "loop_started" or "first_token" or "tool_started"
                            or "permission_requested" or "permission_resolved";
                    if (activePhase || lastType != "turn_ended")
                        return new EventsTruth { IsActive = true, Reason = $"fresh:{lastType}/{lastPhase}" };
                }
            }

            return new EventsTruth { IsActive = false, Reason = $"idle last={lastType}" };
        }
        catch
        {
            return null;
        }
    }

    private static void WriteBusyFile(string path, string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteAtomic(path, value);
        }
        catch { }
    }

    private void EmitLastmsg(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var msg = TryRead(path);
        if (msg != null) MessageChanged?.Invoke(room, NormalizeLastMessage(msg));
    }

    /// <summary>Grok 훅 payload가 추가하는 system-reminder와 user_query 래퍼를 제거.
    /// 기존 lastmsg 파일과 구버전 훅 출력도 헤더/완료 기록에 노출되지 않게 앱에서 한 번 더 방어한다.</summary>
    internal static string NormalizeLastMessage(string message)
    {
        // 구버전 훅이 200자로 자른 값은 닫는 태그가 없을 수 있으므로, 미완성 블록도 끝까지 제거한다.
        message = Regex.Replace(message,
            @"<system-remi(?:n)?der\b[^>]*>.*?</system-remi(?:n)?der\s*>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        message = Regex.Replace(message,
            @"<system-remi(?:n)?der\b[^>]*>.*$", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        message = Regex.Replace(message,
            @"</?system-remi(?:n)?der\b[^>]*>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var match = Regex.Match(message,
            @"^\s*<user_query>\s*(.*?)\s*</user_query>\s*(?:…|\.\.\.)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success) message = match.Groups[1].Value;
        return Regex.Replace(message, @"\s+", " ").Trim();
    }

    private void EmitBusy(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (status == null || string.IsNullOrWhiteSpace(status))
        {
            ReEmitBusyAfterSettleAsync(path, room);
            return;
        }
        bool running = status.Equals("running", StringComparison.OrdinalIgnoreCase);
        BusyChanged?.Invoke(room, running);
    }

    private async void ReEmitBusyAfterSettleAsync(string path, string room)
    {
        await System.Threading.Tasks.Task.Delay(120);
        var status = TryRead(path);
        BusyChanged?.Invoke(room,
            !string.IsNullOrWhiteSpace(status)
            && status.Equals("running", StringComparison.OrdinalIgnoreCase));
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
        WaitingChoiceChanged?.Invoke(room,
            status.Equals("waiting", StringComparison.OrdinalIgnoreCase));
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
        // 복구용 sidecar(.prev.txt/.ended.txt)는 현재 방의 세션 변경 이벤트가 아니다.
        if (room.EndsWith(".prev", StringComparison.OrdinalIgnoreCase)
            || room.EndsWith(".root", StringComparison.OrdinalIgnoreCase)
            || room.EndsWith(".ended", StringComparison.OrdinalIgnoreCase)) return;
        var sid = TryRead(path);
        if (sid != null && Guid.TryParse(sid, out var parsed))
        {
            _eventsPathCache.TryRemove(room, out _); // 세션 전환 시 경로 재탐색
            GrokSessionChanged?.Invoke(room, parsed.ToString());
        }
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

    /// <summary>루트 세션 전환 직전 ID. 상속된 DEVEZCODE_ROOM_ID 로 내부 Grok 프로세스가
    /// 잘못 발화해도 정상 대화를 되살릴 수 있는 복구 후보.</summary>
    public static string? LoadPreviousTrackedSessionId(string roomId)
    {
        return LoadSessionId(PreviousSessionPath(roomId));
    }

    /// <summary>현재 ID가 새 root-session fence를 통과해 기록된 값인지.</summary>
    public static bool IsRootTrackedSession(string roomId, string? sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsed)) return false;
        return string.Equals(LoadSessionId(RootSessionPath(roomId)), parsed.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>검증된 ID 로 현재 추적값을 복구. 다음 정상 SessionStart 가 stale 전환 마커의
    /// 영향을 받지 않도록 마커도 제거한다.</summary>
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

    /// <summary>현재/이전/전환 추적값 제거. 실제 대화 파일은 건드리지 않는다.</summary>
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

    /// <summary>훅 스크립트 roomSafe 규칙과 동일: 비-워드 문자 제거 (underscore 치환 아님).</summary>
    private static string Sanitize(string roomId)
    {
        var sb = new StringBuilder(roomId.Length);
        foreach (var c in roomId)
            if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
        return sb.ToString();
    }

    public void Dispose()
    {
        _eventsTimer?.Dispose();
        _eventsTimer = null;
        _lastmsgWatcher?.Dispose();
        _busyWatcher?.Dispose();
        _waitingWatcher?.Dispose();
        _sessionWatcher?.Dispose();
    }
}
