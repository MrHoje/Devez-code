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
    private static readonly TimeSpan ActivityFresh = TimeSpan.FromMinutes(3);

    /// <summary>열린 턴/도구 없이 활동만 멈춘 채 이 시간 지나면 idle(하드킬/유실 훅 잔재 방지).</summary>
    private static readonly TimeSpan StaleRunningCap = TimeSpan.FromMinutes(30);

    private FileSystemWatcher? _lastmsgWatcher;
    private FileSystemWatcher? _busyWatcher;
    private FileSystemWatcher? _waitingWatcher;
    private FileSystemWatcher? _sessionWatcher;
    private Timer? _eventsTimer;
    private int _eventsPolling;
    private readonly ConcurrentDictionary<string, string> _eventsPathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _lastPolledBusy = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>방별 events.jsonl 증분 커서. 긴 턴은 phase_changed 만으로 수백 KB 가 쌓여
    /// 고정 꼬리 윈도우에서 turn_started 가 잘리면 조기 idle 이 난다 → 파일 전체 상태를 유지한다.</summary>
    private readonly ConcurrentDictionary<string, EventsCursor> _eventsCursors = new(StringComparer.OrdinalIgnoreCase);

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
            // 1초 주기: 조기 Stop 뒤 스피너 소등 창을 최소화 (이전 2초).
            _eventsTimer = new Timer(_ => PollEvents(), null,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
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

        var truth = AnalyzeEvents(room, sid, eventsPath);
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

    /// <summary>events.jsonl 증분 파서 상태. phase_changed 폭주로 파일이 커져도
    /// turn_started 를 잃지 않도록 방별로 누적한다.</summary>
    private sealed class EventsCursor
    {
        public string Path = "";
        public string SessionId = "";
        /// <summary>완전히 소비한 바이트 오프셋(줄 경계). 이후 바이트만 이어서 파싱.</summary>
        public long ParsedThrough;
        public bool InOpenTurn;
        public int ToolDepth;
        public bool SawTurn;
        public DateTimeOffset? LastTurnStarted;
        public DateTimeOffset? LastTurnEnded;
        public DateTimeOffset? LastActivity;
        public string? LastType;
        public string? LastPhase;
    }

    /// <summary>events.jsonl 증분 판정.
    /// 한 user 턴은 turn_started~turn_ended 이고 그 안에 loop/tool 이 수십~수백 회 반복된다.
    /// 예전 96KB 꼬리 스캔은 긴 턴에서 turn_started 를 놓쳐(실측 턴 스팬 90KB~250KB+)
    /// 서브에이전트/장시간 도구 대기 중 idle 로 오판했다.</summary>
    private EventsTruth? AnalyzeEvents(string room, string sessionId, string path)
    {
        try
        {
            var cursor = _eventsCursors.GetOrAdd(room, _ => new EventsCursor());
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = fs.Length;
            if (len == 0)
            {
                ResetCursor(cursor, path, sessionId);
                return new EventsTruth { IsActive = false, Reason = "empty" };
            }

            // 세션 전환·파일 축소(로테이션)·경로 변경 시 처음부터 재구축.
            if (!string.Equals(cursor.Path, path, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cursor.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                || cursor.ParsedThrough > len)
            {
                ResetCursor(cursor, path, sessionId);
            }

            if (cursor.ParsedThrough < len)
            {
                // 바이트 단위로 읽어 완전한 줄(\n 로 끝남)만 소비한다.
                // StreamReader.ReadLine 은 EOF 의 개행 없는 미완 줄도 반환 → 다음 폴에
                // 이어서 붙는 바이트와 합쳐지지 않아 이벤트를 영구 유실할 수 있다.
                var toRead = (int)Math.Min(len - cursor.ParsedThrough, 4 * 1024 * 1024);
                var buf = new byte[toRead];
                fs.Seek(cursor.ParsedThrough, SeekOrigin.Begin);
                var got = fs.Read(buf, 0, toRead);
                if (got > 0)
                {
                    // 바이트 기준 마지막 \n — UTF-8 멀티바이트 경계/ GetByteCount 불일치 방지.
                    var lastNl = -1;
                    for (var i = got - 1; i >= 0; i--)
                    {
                        if (buf[i] == (byte)'\n') { lastNl = i; break; }
                    }
                    if (lastNl < 0)
                    {
                        // 아직 한 줄도 완성되지 않음 — 커서 유지, 다음 폴에 재시도.
                    }
                    else
                    {
                        var consumedBytes = lastNl + 1;
                        var complete = Encoding.UTF8.GetString(buf, 0, consumedBytes);
                        foreach (var raw in complete.Split('\n'))
                        {
                            if (raw.Length == 0) continue;
                            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
                            ApplyEventLine(cursor, line);
                        }
                        cursor.ParsedThrough += consumedBytes;
                    }
                }
            }

            return TruthFromCursor(cursor);
        }
        catch
        {
            return null;
        }
    }

    private static void ResetCursor(EventsCursor cursor, string path, string sessionId)
    {
        cursor.Path = path;
        cursor.SessionId = sessionId;
        cursor.ParsedThrough = 0;
        cursor.InOpenTurn = false;
        cursor.ToolDepth = 0;
        cursor.SawTurn = false;
        cursor.LastTurnStarted = null;
        cursor.LastTurnEnded = null;
        cursor.LastActivity = null;
        cursor.LastType = null;
        cursor.LastPhase = null;
    }

    private static void ApplyEventLine(EventsCursor cursor, string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] != '{') return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var tEl) || tEl.ValueKind != JsonValueKind.String)
                return;
            var type = tEl.GetString() ?? "";
            cursor.LastType = type;

            DateTimeOffset? ts = null;
            if (root.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(tsEl.GetString(), out var parsedTs))
                ts = parsedTs;
            if (ts != null) cursor.LastActivity = ts;

            switch (type)
            {
                case "turn_started":
                    cursor.SawTurn = true;
                    cursor.LastTurnStarted = ts ?? cursor.LastTurnStarted ?? DateTimeOffset.UtcNow;
                    cursor.InOpenTurn = true;
                    cursor.ToolDepth = 0;
                    break;
                case "turn_ended":
                    cursor.SawTurn = true;
                    cursor.LastTurnEnded = ts ?? DateTimeOffset.UtcNow;
                    cursor.InOpenTurn = false;
                    cursor.ToolDepth = 0;
                    break;
                case "tool_started":
                    // 열린 턴 안, 또는 turn 마커를 아직 못 본 구형 로그.
                    if (cursor.InOpenTurn || !cursor.SawTurn) cursor.ToolDepth++;
                    break;
                case "tool_completed":
                    if ((cursor.InOpenTurn || !cursor.SawTurn) && cursor.ToolDepth > 0)
                        cursor.ToolDepth--;
                    break;
                case "loop_started":
                case "first_token":
                    // 열린 턴 안에서만 루프 재개로 본다. 이미 turn_ended 된 뒤의 잔여 이벤트는
                    // post_end/fresh 경로가 짧게 흡수하고, 여기서 InOpenTurn 을 다시 켜면 stuck-ON.
                    if (cursor.InOpenTurn)
                    {
                        // no-op: LastActivity 만 갱신됨
                    }
                    else if (cursor.SawTurn && cursor.LastTurnStarted != null
                        && (cursor.LastTurnEnded == null || cursor.LastTurnStarted > cursor.LastTurnEnded))
                    {
                        cursor.InOpenTurn = true;
                    }
                    break;
                case "phase_changed":
                    if (root.TryGetProperty("phase", out var pEl) && pEl.ValueKind == JsonValueKind.String)
                        cursor.LastPhase = pEl.GetString();
                    // 열린 턴 중 tool_execution/streaming 은 활동 유지(LastActivity 이미 갱신).
                    break;
            }
        }
        catch { /* 잘린 줄 */ }
    }

    private static EventsTruth TruthFromCursor(EventsCursor cursor)
    {
        var now = DateTimeOffset.UtcNow;

        if (cursor.ToolDepth > 0)
            return new EventsTruth { IsActive = true, Reason = $"openTools={cursor.ToolDepth}" };

        if (cursor.InOpenTurn)
        {
            var anchor = cursor.LastActivity ?? cursor.LastTurnStarted ?? now;
            var age = now - anchor;
            if (age > StaleRunningCap)
                return new EventsTruth { IsActive = false, Reason = $"staleTurn age={age.TotalMinutes:F0}m" };
            return new EventsTruth { IsActive = true, Reason = "turn_open" };
        }

        if (cursor.SawTurn && cursor.LastTurnStarted != null)
        {
            bool turnOpen = cursor.LastTurnEnded == null
                || cursor.LastTurnStarted > cursor.LastTurnEnded;
            if (turnOpen)
            {
                var anchor = cursor.LastActivity ?? cursor.LastTurnStarted.Value;
                var age = now - anchor;
                if (age > StaleRunningCap)
                    return new EventsTruth { IsActive = false, Reason = $"staleTurn age={age.TotalMinutes:F0}m" };
                return new EventsTruth { IsActive = true, Reason = "turn_open" };
            }

            var endAge = cursor.LastTurnEnded != null ? now - cursor.LastTurnEnded.Value : TimeSpan.MaxValue;
            if (endAge < TurnEndSettle)
                return new EventsTruth { IsActive = true, Reason = "turn_end_settle" };

            // turn_ended 직후에도 곧이어 loop/streaming 이 오면(멀티턴 경계 흔들림) 유지.
            if (cursor.LastActivity != null && cursor.LastActivity > cursor.LastTurnEnded
                && now - cursor.LastActivity.Value <= ActivityFresh)
            {
                bool activePhase = IsActivePhase(cursor.LastPhase, cursor.LastType);
                if (activePhase)
                    return new EventsTruth { IsActive = true, Reason = $"post_end:{cursor.LastType}/{cursor.LastPhase}" };
            }

            return new EventsTruth { IsActive = false, Reason = "turn_ended" };
        }

        // 구형: turn_* 없음 → 최근 활동/페이즈로 추정.
        if (cursor.LastActivity != null)
        {
            var age = now - cursor.LastActivity.Value;
            if (age <= ActivityFresh)
            {
                if (IsActivePhase(cursor.LastPhase, cursor.LastType) || cursor.LastType != "turn_ended")
                    return new EventsTruth { IsActive = true, Reason = $"fresh:{cursor.LastType}/{cursor.LastPhase}" };
            }
        }

        return new EventsTruth { IsActive = false, Reason = $"idle last={cursor.LastType}" };
    }

    private static bool IsActivePhase(string? phase, string? lastType) =>
        phase is "waiting_for_model" or "streaming_reasoning" or "streaming_text"
            or "tool_execution" or "permission_prompt"
        || lastType is "loop_started" or "first_token" or "tool_started"
            or "tool_completed" or "permission_requested" or "permission_resolved";
    // phase_changed 단독 lastType 은 phase 문자열로만 판정(유휴 phase 오인 방지).

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
            _eventsCursors.TryRemove(room, out _);  // 커서 세션 상태 초기화
            GrokSessionChanged?.Invoke(room, parsed.ToString());
        }
    }

    /// <summary>완료 정착 창에서 busy 파일 재확인 (조기 Stop 뒤 events 폴러가 running 으로 되돌린 경우).</summary>
    public bool IsRoomBusy(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return false;
        try
        {
            var status = TryRead(BusyPath(roomId));
            return !string.IsNullOrWhiteSpace(status)
                && status.Equals("running", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
