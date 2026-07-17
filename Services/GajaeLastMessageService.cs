using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DevezCode.Services;

/// <summary>가재코드(gjc) 방 상태 하이브리드 추적 — 두 신호원을 병합해 세 가지를 알린다:
/// 1) 마지막 보낸 user 프롬프트(헤더 타이틀) — MessageChanged
/// 2) 요청 처리중 여부(좌측 스피너) — BusyChanged
/// 3) 'ask' 선택지 응답 대기(❗) — WaitingChoiceChanged
///
/// <para><b>1차 신호원 = gjc 런타임 사이드카</b>:
/// <c>&lt;방 workingDir&gt;\.gjc\_session-&lt;sessionId&gt;\runtime\runtime-state.json</c>.
/// gjc 0.11 이 agent_start/turn_start/agent_end 이벤트 시점마다 잠금+원자적으로 직접 쓰는 공식 상태 파일로
/// state(running/completed/errored/needs_user_input…)·updated_at·session_file 을 담는다.
/// gjc 0.11 은 세션 transcript(.jsonl)를 지연 flush 하므로(수 분~수 시간 실측) transcript 폴링만으로는
/// busy/idle 이 그만큼 늦어 완료기록·스피너가 죽는다 — 사이드카는 이벤트 즉시 기록이라 이 문제가 없다.
/// (0.10 의 --hook 은 미파싱, 0.11 의 파일시스템 확장 로딩은 격리(quarantine)되어 확장 주입 경로는 불가 —
/// 사이드카가 유일하게 신뢰 가능한 네이티브 실시간 신호였다. 2026-07-17 실기 검증.)</para>
///
/// <para><b>폴백 = 세션 .jsonl 증분 폴링</b>: 사이드카가 없거나(구버전 gjc) stale 인 방의 기존 동작 유지.
/// 디렉터리: %AppData%\DevezCode\gajae\sessions\&lt;roomId&gt;\&lt;timestamp&gt;_&lt;id&gt;.jsonl (roomId=GUID "N").
/// busy 판정: 마지막 message 엔트리 role 이 user/toolResult(또는 assistant+toolCall)면 처리중,
/// assistant text-only 면 완료 후보 → 같은 EOF 가 정착 지연 후에도 유지되면 idle 확정.
/// 사이드카가 유효 상태(running/completed 등)를 가지는 방에서는 jsonl 이 busy 를 재점화하지 못한다 —
/// 지연 flush 가 "이미 idle 확정된 턴"의 중간 줄을 뒤늦게 내려보내며 생기는 가짜 스피너/중복 카드 차단.</para>
///
/// <para><b>goal 모드 백필</b>: transcript 의 custom/goal-completed 엔트리를 파싱해 골 단위 완료를
/// GoalCompleted 로 알린다(사이드카 미가동 방 한정 — 가동 방은 agent_end 완료 카드가 이미 골마다 찍힌다).
/// 파일 최초 스캔(과거 내역)은 재발행하지 않는다.</para></summary>
public sealed class GajaeLastMessageService : IDisposable
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "gajae", "sessions");

    private readonly DispatcherTimer _poll;
    // roomId → 마지막으로 처리한 상태 시그니처(최신 .jsonl 경로+길이+mtime + 사이드카 서명). 같으면 스킵.
    private readonly Dictionary<string, string> _seen = new();
    private readonly Dictionary<string, string> _lastMsg = new();
    private readonly Dictionary<string, bool> _busy = new();
    private readonly Dictionary<string, bool> _waiting = new();
    // GJC는 최종 assistant 레코드를 디스크에 먼저 기록한 뒤 TUI 출력 플러시를 마친다.
    // 같은 transcript 시그니처가 다음 poll까지 안정적으로 유지될 때만 idle을 확정해,
    // 최종 응답이 화면에 아직 출력 중인데 스피너가 먼저 사라지는 것을 막는다(JSONL 폴백 판정 한정).
    private readonly Dictionary<string, (string Signature, DateTime FirstSeenUtc)> _idleCandidates = new();
    // 이번 앱 실행 중 사이드카 가동이 증명된 방 — 사이드카가 일시적으로 안 읽혀도(잠금 등)
    // goal 백필이 오발행되지 않게 하는 가드(가동 방은 agent_end 완료 카드가 골마다 이미 찍힌다).
    private readonly HashSet<string> _sidecarProven = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TranscriptCursor> _transcripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(roomId, message) — gjc 세션이 마지막으로 보낸 프롬프트(1줄 요약). 빈 문자열이면 세션명으로 표시.</summary>
    public event Action<string, string>? MessageChanged;

    /// <summary>(roomId, busy) — busy=true 면 요청 처리중(스피너).</summary>
    public event Action<string, bool>? BusyChanged;

    /// <summary>(roomId, waiting) — waiting=true 면 선택지('ask') 응답 대기 중(❗).</summary>
    public event Action<string, bool>? WaitingChoiceChanged;

    /// <summary>(roomId, objective, completedAt) — goal 모드에서 골 하나 완료(transcript 백필, 사이드카 미가동 방 한정).</summary>
    public event Action<string, string, DateTime>? GoalCompleted;

    private bool _started;

    /// <summary>방의 현재 병합 busy 상태(마지막 발행값). 완료 디바운스의 isStillActive 재확인용.</summary>
    public bool IsRoomBusy(string roomId) => _busy.TryGetValue(roomId, out var b) && b;

    // 적응형 폴링 주기: 처리중인 방이 있으면 스피너 종료를 빨리 감지하도록 BusyInterval(1s),
    // 전부 idle 이면 IdleInterval(2.5s)로 늦춰 다세션 시 디렉터리 열거·파일 open 부하를 낮춘다.
    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan IdleSettleDelay = TimeSpan.FromMilliseconds(900);
    // 사이드카 running 신뢰 창 — gjc 는 매 턴 이벤트마다 updated_at 을 갱신하지만 단일 도구가 오래 돌면
    // 이벤트가 없어 stale 해질 수 있다. 이 창을 넘긴 running 은 하드킬 잔재일 수 있으므로 jsonl 판정으로
    // 폴백한다(정상 종료·시그널은 gjc postmortem 이 completed/errored 로 갱신하므로 잔재는 하드킬뿐).
    private static readonly TimeSpan SidecarRunningStale = TimeSpan.FromMinutes(15);

    private sealed class TranscriptCursor
    {
        public long Offset;
        public byte[] Pending = Array.Empty<byte>();
        public string? LastUserMessage;
        public bool Busy;
        public bool Waiting;
        // 최초 전체 스캔(과거 내역) 여부 — goal-completed 는 Primed 이후 append 분만 발행한다.
        public bool Primed;
        public List<(string Objective, DateTime CompletedAt)> NewGoals = new();
    }

    /// <summary>gjc 런타임 사이드카(runtime-state.json) 스냅샷.</summary>
    private sealed record SidecarState(string State, long UpdatedAtMs, string? SessionFile, string? Event)
    {
        public string Signature => $"{State}:{UpdatedAtMs}:{Event}";
        public TimeSpan Age => TimeSpan.FromMilliseconds(
            Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - UpdatedAtMs));
    }

    public GajaeLastMessageService()
    {
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = BusyInterval };
        _poll.Tick += (_, _) => Scan();
    }

    public void Start()
    {
        Scan(); // 시작 직후 한 번 확정해 이전 실행의 stale busy를 기다림 없이 idle로 정리.
        _poll.Start();
    }

    private void Scan()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var roomDir in Directory.EnumerateDirectories(Root))
                {
                    try { ScanRoom(roomDir); }
                    catch { /* 다음 폴링 */ }
                }
            }
            _started = true;
        }
        catch { /* 다음 폴링 */ }

        // 처리중/대기 방이 하나라도 있으면 빠른 주기, 전부 idle 이면 느린 주기로 전환.
        var want = (_busy.Values.Any(b => b) || _waiting.Values.Any(w => w)) ? BusyInterval : IdleInterval;
        if (_poll.Interval != want) _poll.Interval = want;
    }

    private void ScanRoom(string roomDir)
    {
        var roomId = Path.GetFileName(roomDir);
        if (string.IsNullOrEmpty(roomId)) return;

        var di = new DirectoryInfo(roomDir);
        var files = di.Exists ? di.GetFiles("*.jsonl", SearchOption.TopDirectoryOnly) : Array.Empty<FileInfo>();
        var newest = files
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        // /new 감지: gjc 는 새 세션의 디렉터리(<ts>_<id>/)를 즉시 만들지만 .jsonl 은 첫 메시지 전까지 안 쓴다.
        // 그래서 최신 .jsonl 보다 새로운 "orphan 디렉터리"(대응 .jsonl 없음)가 있으면 = 빈 새 세션 → 헤더 리셋.
        var jsonlIds = files
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newestOrphan = (di.Exists ? di.GetDirectories() : Array.Empty<DirectoryInfo>())
            .Where(sd => !jsonlIds.Contains(sd.Name))
            .OrderByDescending(sd => sd.LastWriteTimeUtc)
            .FirstOrDefault();
        bool freshNew = newestOrphan != null
            && (newest == null || newestOrphan.LastWriteTimeUtc > newest.LastWriteTimeUtc);

        var sidecar = ReadFreshestSidecar(roomId, roomDir, newest, newestOrphan);

        // 상태 시그니처 — 빈 새 세션이면 orphan 디렉터리 기준, 아니면 최신 .jsonl(경로+실제길이+mtime) 기준.
        // mtime 만으로는 부족: gjc 가 핸들을 연 채 append 하면 디렉터리 엔트리의 mtime 이 stale 일 수 있어
        // FileStream.Length(커널 실제 EOF)를 포함해 append 누락을 우회한다. 사이드카 서명도 포함해
        // 사이드카 전이(프롬프트 시작/완료)가 반영을 트리거하게 한다.
        long contentLen = newest != null ? RealContentLength(newest.FullName) : 0;
        string? sig = freshNew ? "NEW:" + newestOrphan!.Name
                : newest != null ? newest.FullName + "|" + contentLen + "|" + newest.LastWriteTimeUtc.Ticks
                : null;
        if (sidecar != null) sig = (sig ?? "NOJSONL") + "|SC:" + sidecar.Signature;
        if (sig == null) return;
        // busy 로 마킹된 방은 sig 가 같아도 강제 재파싱한다(핸들 열린 채 append 시 mtime stale 로
        // 종료 줄이 sig 에 안 잡혀 idle 전이를 놓치는 것 방지 — 내용 읽기는 항상 최신 바이트를 준다).
        bool roomBusy = _busy.TryGetValue(roomId, out var wasBusyNow) && wasBusyNow;
        bool roomWaiting = _waiting.TryGetValue(roomId, out var wasWaitingNow) && wasWaitingNow;
        if (!roomBusy && !roomWaiting && _seen.TryGetValue(roomId, out var prev) && prev == sig) return;
        _seen[roomId] = sig;

        // ── 1) JSONL 폴백 판정 ─────────────────────────────────────
        string? msg;
        bool jsonlBusy;
        bool jsonlWaiting;
        List<(string Objective, DateTime CompletedAt)>? goals = null;
        if (freshNew) { msg = ""; jsonlBusy = false; jsonlWaiting = false; }   // 빈 새 세션 → 헤더 세션명 복귀, idle
        else if (newest != null) (msg, jsonlBusy, jsonlWaiting, goals) = ParseState(newest.FullName);
        else { msg = null; jsonlBusy = false; jsonlWaiting = false; }

        // ── 2) 사이드카 상태와 병합 ─────────────────────────────────
        bool busy;
        bool waiting;
        bool sidecarOwns = sidecar != null && sidecar.State is "running" or "needs_user_input" or
            "ready_for_input" or "completed" or "errored";
        if (!sidecarOwns)
        {
            // 사이드카 없음/판정 불가(booting·stale·unknown) — 기존 jsonl 단독 판정 + idle 정착.
            busy = ApplyJsonlIdleGates(roomId, freshNew, sig, jsonlBusy);
            waiting = _started && jsonlWaiting && busy;
        }
        else if (sidecar!.State == "needs_user_input")
        {
            busy = true;
            waiting = true;
            _idleCandidates.Remove(roomId);
            _sidecarProven.Add(roomId);
        }
        else if (sidecar.State == "running" && sidecar.Age < SidecarRunningStale)
        {
            // 앱 재시작 직후에도 그대로 신뢰(작업 중 재시작 케이스). ❗는 jsonl 의 ask 파싱으로 보강.
            busy = true;
            waiting = _started && jsonlWaiting;
            _idleCandidates.Remove(roomId);
            _sidecarProven.Add(roomId);
        }
        else if (sidecar.State == "running")
        {
            // running 이 신뢰 창을 넘겨 stale — 하드킬 잔재 가능성. jsonl 판정으로 폴백해 고착을 봉인.
            busy = ApplyJsonlIdleGates(roomId, freshNew, sig, jsonlBusy);
            waiting = _started && jsonlWaiting && busy;
        }
        else
        {
            // 사이드카가 idle(완료/에러/입력대기 준비) 확정 — jsonl 은 busy 를 재점화하지 못한다.
            // gjc 의 지연 flush 는 "이미 idle 확정된 턴"의 중간 줄(toolResult 등)을 몇 분~몇 시간 뒤
            // 부분적으로 내려보낼 수 있어, jsonl 을 인정하면 가짜 스피너/중복 완료 카드가 생긴다.
            // 진짜 새 턴이면 gjc 가 agent_start 즉시 사이드카에 running 을 쓰므로 jsonl 재점화가 필요 없다.
            busy = false;
            waiting = false;
            _idleCandidates.Remove(roomId);
        }

        msg ??= ""; // 안전망 (헤더 lastmsg 는 transcript 원본 — flush 지연 시 늦게 갱신될 수 있음)

        Publish(roomId, msg, busy, waiting);

        // ── 3) goal 백필 — 사이드카 미가동 방만(가동 방은 agent_end 완료 카드가 골마다 이미 찍힘) ──
        if (!_sidecarProven.Contains(roomId) && sidecar == null && goals is { Count: > 0 })
            foreach (var g in goals)
                GoalCompleted?.Invoke(roomId, g.Objective, g.CompletedAt);
    }

    /// <summary>방의 후보 세션 ID(최신 .jsonl 파일명·orphan 새 세션 디렉터리명·저장값)들로
    /// <c>&lt;workingDir&gt;\.gjc\_session-&lt;id&gt;\runtime\runtime-state.json</c> 을 읽어,
    /// 이 방의 세션임이 확인되는 것 중 가장 최근 갱신본을 고른다. 없으면 null(폴백 모드).</summary>
    private static SidecarState? ReadFreshestSidecar(
        string roomId, string roomDir, FileInfo? newestJsonl, DirectoryInfo? newestOrphan)
    {
        string? workingDir;
        try { workingDir = SettingsService.LoadClaudeCodeRoomDir(roomId); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(workingDir)) return null;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddIdFromName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var us = name.LastIndexOf('_');
            if (us < 0 || us + 1 >= name.Length) return;
            var id = name.Substring(us + 1);
            if (Guid.TryParse(id, out _)) candidates.Add(id);
        }
        AddIdFromName(newestJsonl != null ? Path.GetFileNameWithoutExtension(newestJsonl.Name) : null);
        AddIdFromName(newestOrphan?.Name);
        try { if (SettingsService.LoadGajaeRoomSession(roomId) is { Length: > 0 } saved && Guid.TryParse(saved, out _)) candidates.Add(saved); }
        catch { }
        if (candidates.Count == 0) return null;

        SidecarState? best = null;
        foreach (var id in candidates)
        {
            var state = TryReadSidecar(
                Path.Combine(workingDir, ".gjc", "_session-" + id, "runtime", "runtime-state.json"),
                id, roomDir);
            if (state != null && (best == null || state.UpdatedAtMs > best.UpdatedAtMs))
                best = state;
        }
        return best;
    }

    /// <summary>runtime-state.json 한 개를 읽고 이 방의 세션인지 검증한다.
    /// session_file 이 기록돼 있으면 그 부모 디렉터리가 방의 session-dir 와 일치해야 한다 —
    /// 같은 workingDir 를 공유하는 다른 방/서브에이전트(sessions\&lt;room&gt;\&lt;parent&gt;\… 한 단계 깊음)의
    /// 사이드카가 이 방의 스피너·완료기록을 오염시키지 않게 하는 격리 가드.</summary>
    private static SidecarState? TryReadSidecar(string path, string expectedSessionId, string roomDir)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var text = sr.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("session_id", out var sid)
                || !string.Equals(sid.GetString(), expectedSessionId, StringComparison.OrdinalIgnoreCase))
                return null;
            var state = root.TryGetProperty("state", out var st) ? st.GetString() : null;
            if (string.IsNullOrEmpty(state)) return null;

            string? sessionFile = root.TryGetProperty("session_file", out var sf) && sf.ValueKind == JsonValueKind.String
                ? sf.GetString() : null;
            if (sessionFile != null)
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(sessionFile));
                if (parent == null || !string.Equals(
                        parent.TrimEnd('\\', '/'),
                        Path.GetFullPath(roomDir).TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            long updatedMs = 0;
            if (root.TryGetProperty("updated_at", out var ua) && ua.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ua.GetString(), out var dto))
                updatedMs = dto.ToUnixTimeMilliseconds();
            var evt = root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String
                ? ev.GetString() : null;
            return new SidecarState(state!, updatedMs, sessionFile, evt);
        }
        catch { return null; }
    }

    /// <summary>JSONL 판정 전용 게이트: 첫 스캔은 이전 실행 잔재로 보고 idle 강제,
    /// busy→idle 전이는 같은 EOF 시그니처가 정착 지연 후에도 유지될 때만 확정.</summary>
    private bool ApplyJsonlIdleGates(string roomId, bool freshNew, string sig, bool busy)
    {
        if (!_started)
        {
            _idleCandidates.Remove(roomId);
            return false;
        }
        if (busy)
        {
            _idleCandidates.Remove(roomId);
            return true;
        }
        if (!freshNew && _busy.TryGetValue(roomId, out var wasBusy) && wasBusy)
        {
            // 최종 assistant JSONL append 와 실제 TUI 출력 완료 사이에 짧은 시차가 있다.
            // 한 번 더 같은 EOF 를 관찰한 뒤 idle 로 내리면 중간 toolResult/user append 도
            // 자연스럽게 후보를 취소하므로 고정 지연 타이머보다 상태 전이가 정확하다.
            var now = DateTime.UtcNow;
            if (!_idleCandidates.TryGetValue(roomId, out var candidate)
                || !string.Equals(candidate.Signature, sig, StringComparison.Ordinal))
            {
                _idleCandidates[roomId] = (sig, now);
                return true;
            }
            if (now - candidate.FirstSeenUtc < IdleSettleDelay) return true;
            _idleCandidates.Remove(roomId);
            return false;
        }
        _idleCandidates.Remove(roomId);
        return false;
    }

    private void Publish(string roomId, string msg, bool busy, bool waiting)
    {
        if (!_lastMsg.TryGetValue(roomId, out var wasMsg) || wasMsg != msg)
        {
            _lastMsg[roomId] = msg;
            MessageChanged?.Invoke(roomId, msg);
        }
        if (!_busy.TryGetValue(roomId, out var wasBusy) || wasBusy != busy)
        {
            _busy[roomId] = busy;
            BusyChanged?.Invoke(roomId, busy);
        }
        if (!_waiting.TryGetValue(roomId, out var wasWaiting) || wasWaiting != waiting)
        {
            _waiting[roomId] = waiting;
            WaitingChoiceChanged?.Invoke(roomId, waiting);
        }
    }

    /// <summary>jsonl의 최초 스캔은 전체를 읽고 이후에는 append된 바이트만 순방향 파싱한다.
    /// 세션이 길어져도 매 1초마다 전체 대화를 다시 읽지 않으면서 마지막 user/상태 결과는 종전과 같다.
    /// busy 판정(시간 무관, 내용 기반): 마지막 message 엔트리 기준 —
    ///  • role=user/toolResult → 처리중(응답·다음 단계 대기)
    ///  • role=assistant + content 에 toolCall 있음 → 처리중(툴 실행/연속)
    ///  • role=assistant + text 만(toolCall 없음) → 완료(idle)
    /// gjc 한 턴: user → assistant(toolCall) → toolResult → … → assistant(text) 로 끝남.
    /// 부수로 custom/goal-completed 엔트리를 수집한다(최초 전체 스캔 분은 제외).</summary>
    private (string? lastUserMsg, bool busy, bool waitingChoice, List<(string Objective, DateTime CompletedAt)>? goals)
        ParseState(string path)
    {
        if (!_transcripts.TryGetValue(path, out var cursor))
        {
            cursor = new TranscriptCursor();
            _transcripts[path] = cursor;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < cursor.Offset) // truncate/교체 — 재프라임(과거 goal 재발행 방지)
            {
                cursor = new TranscriptCursor();
                _transcripts[path] = cursor;
            }
            fs.Position = cursor.Offset;

            using var line = new MemoryStream();
            if (cursor.Pending.Length > 0) line.Write(cursor.Pending);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                cursor.Offset += read;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        ProcessTranscriptLine(line.ToArray(), completeLine: true, cursor);
                        line.SetLength(0);
                    }
                    else line.WriteByte(buffer[i]);
                }
            }

            cursor.Pending = line.ToArray();
            // GJC는 보통 LF까지 한 번에 append한다. LF가 아직 관찰되지 않아도 완성 JSON이면 즉시 반영하고,
            // 쓰기 중인 부분 JSON이면 바이트를 보존해 다음 append와 합친다(UTF-8 경계도 손실 없음).
            if (cursor.Pending.Length > 0 && ProcessTranscriptLine(cursor.Pending, completeLine: false, cursor))
                cursor.Pending = Array.Empty<byte>();
        }
        catch
        {
            // 일시 잠금/IO 실패 때 직전 정상 상태를 보존한다. 다음 poll에서 append를 다시 읽는다.
        }

        cursor.Primed = true;
        List<(string Objective, DateTime CompletedAt)>? goals = null;
        if (cursor.NewGoals.Count > 0)
        {
            goals = new List<(string Objective, DateTime CompletedAt)>(cursor.NewGoals);
            cursor.NewGoals.Clear();
        }
        return (cursor.LastUserMessage, cursor.Busy, cursor.Waiting, goals);
    }

    /// <returns>부분 tail이 완성 JSON으로 소비됐거나, LF로 끝난 완전한 줄이면 true.</returns>
    private static bool ProcessTranscriptLine(byte[] bytes, bool completeLine, TranscriptCursor cursor)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).Trim();
            if (text.Length == 0) return true;
            if (text[0] != '{') return completeLine;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t)) return true;
            var entryType = t.GetString();

            // goal 모드: 골 하나 완료 마커. 최초 전체 스캔(Primed 전)의 과거 내역은 수집하지 않는다.
            if (entryType == "custom")
            {
                if (cursor.Primed
                    && root.TryGetProperty("customType", out var ct)
                    && ct.GetString() == "goal-completed"
                    && root.TryGetProperty("data", out var data)
                    && data.TryGetProperty("objective", out var objEl)
                    && objEl.GetString() is { Length: > 0 } objective)
                {
                    var at = DateTime.Now;
                    if (root.TryGetProperty("timestamp", out var ts))
                    {
                        if (ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var ms))
                            at = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                        else if (ts.ValueKind == JsonValueKind.String
                                 && DateTimeOffset.TryParse(ts.GetString(), out var dto))
                            at = dto.LocalDateTime;
                    }
                    var summary = string.Join(" ", objective.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    if (summary.Length > 200) summary = summary[..200];
                    cursor.NewGoals.Add((summary, at));
                }
                return true;
            }

            if (entryType != "message") return true;
            if (!root.TryGetProperty("message", out var m) || !m.TryGetProperty("role", out var r)) return true;

            var role = r.GetString();
            string? userText = null;
            bool hasToolCall = false;
            bool hasAskCall = false;
            if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (!part.TryGetProperty("type", out var pt)) continue;
                    var partType = pt.GetString();
                    if (partType == "toolCall")
                    {
                        hasToolCall = true;
                        if (part.TryGetProperty("name", out var name)
                            && string.Equals(name.GetString(), "ask", StringComparison.OrdinalIgnoreCase))
                            hasAskCall = true;
                    }
                    if (role == "user" && partType == "text" && userText == null
                        && part.TryGetProperty("text", out var txt))
                    {
                        var value = txt.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            userText = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    }
                }
            }

            cursor.Busy = role is "user" or "toolResult" || (role == "assistant" && hasToolCall);
            cursor.Waiting = role == "assistant" && hasAskCall;
            if (role == "user" && userText != null)
                cursor.LastUserMessage = userText.Length > 200 ? userText[..200] : userText;
            return true;
        }
        catch
        {
            // LF가 있으면 손상된 한 줄만 버리고 다음 줄을 계속 읽는다. tail이면 쓰기 중일 수 있어 보존.
            return completeLine;
        }
    }

    /// <summary>파일의 실제 콘텐츠 길이(바이트). gjc 가 핸들을 연 채 append 하는 동안 디렉터리 엔트리 기반
    /// FileInfo.Length/LastWriteTime 은 stale 일 수 있으나, FileStream 으로 열면 커널이 실제 EOF 를 반환해
    /// 항상 최신 길이를 준다. 변화 감지 시그니처에 사용해 busy 진입(append) 누락을 막는다.</summary>
    private static long RealContentLength(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.Length;
        }
        catch { return 0; }
    }

    public void Dispose() => _poll.Stop();
}
