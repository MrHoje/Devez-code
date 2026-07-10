using System.Collections.Generic;
using System.IO;

namespace DevezCode.Services;

/// <summary>claude busy 훅(busy-hook.ps1)이 방별로 떨군 상태 파일(busy\&lt;roomId&gt;.txt = running|idle)을
/// 감시해 세션의 요청 처리중 여부를 알린다. clude-blinker 의 훅+파일 감시 방식 이식.
/// roomId 는 파일명에서 그대로 얻으므로 세션 매칭이 정확하다(터미널 출력 파싱 불필요).</summary>
public sealed class SessionBusyService : IDisposable
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude", "busy");
    private static string WaitingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude", "waiting");
    private static string SubrunsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude", "subruns");
    private static string StateDir => Path.Combine(Dir, "_state");

    // 서브에이전트 run 파일이 이보다 오래되면 SubagentStop 을 못 받은 유령으로 보고 prune(스피너 stuck-ON 방지).
    private static readonly TimeSpan SubMaxAge = TimeSpan.FromHours(1);
    // reconcile 재예약 간격: 활성(진행중 턴/서브 有)이면 촘촘, 완전 유휴면 느슨(유휴 CPU wake 최소화).
    private const int ReconcileActiveMs = 4000;
    private const int ReconcileIdleMs = 30000;
    private const int ReconcileWakeMs = 500;

    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _waitingWatcher;
    private System.Threading.Timer? _reconcileTimer;
    // 방별 마지막으로 로그에 남긴 busy 값 — watcher 중복 이벤트(쓰기당 여러 Changed)로 같은 값이
    // 반복 기록되는 것을 걸러 diag.log 를 전이 시점만 남긴다. 여러 watcher 스레드에서 접근.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastEmitted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(roomId, busy) — busy=true 면 요청 처리중.</summary>
    public event Action<string, bool>? BusyChanged;

    /// <summary>(roomId, waiting) — waiting=true 면 선택지/권한 응답 대기 중(❗). 선택지·권한을 구분하지 않고 통합.</summary>
    public event Action<string, bool>? WaitingChoiceChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(WaitingDir);
            foreach (var f in Directory.EnumerateFiles(WaitingDir))
                try { File.Delete(f); } catch { /* 시작 시 stale 대기 상태 파일 제거 */ }
            // 앱 시작 시 기존 busy 파일 모두 삭제. 프로그램을 닫으면 ConPTY/claude 프로세스는 죽지만
            // busy 상태 파일은 디스크에 남아있어, 재시작 후 세션을 다시 열면 이전 세션의 stale 한
            // "running" 상태가 그대로 emit 되어 스피너가 영원히 도는 문제가 생긴다(앱 재시작 후 세션
            // 재오픈 시 스피너 안 멈춤). 새 세션은 hook 이 fresh 상태를 다시 쓸 때까지는 IsBusy=false 유지.
            foreach (var f in Directory.EnumerateFiles(Dir, "*.txt"))
                try { File.Delete(f); } catch { /* hook write 와 경합 가능, 무시 */ }
            // 서브에이전트 생존 추적 상태도 stale 제거: 종료 시 SubagentStop 을 못 받은 run 파일이나
            // 메인 턴 플래그가 남아있으면, 재시작 후 첫 Stop 에서 유령 서브로 오판돼 스피너가 안 꺼진다.
            var subruns = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude", "subruns");
            if (Directory.Exists(subruns)) try { Directory.Delete(subruns, true); } catch { /* 경합/사용중 무시 */ }
            var stateDir = Path.Combine(Dir, "_state");
            if (Directory.Exists(stateDir))
                foreach (var f in Directory.EnumerateFiles(stateDir, "main_*.flag"))
                    try { File.Delete(f); } catch { /* hook write 와 경합 가능, 무시 */ }
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(Dir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, e) => Emit(e.FullPath);
            _watcher.Created += (_, e) => Emit(e.FullPath);
            _watcher.Renamed += (_, e) => Emit(e.FullPath);

            _waitingWatcher?.Dispose();
            _waitingWatcher = new FileSystemWatcher(WaitingDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _waitingWatcher.Changed += (_, e) => EmitWaiting(e.FullPath);
            _waitingWatcher.Created += (_, e) => EmitWaiting(e.FullPath);
            _waitingWatcher.Renamed += (_, e) => EmitWaiting(e.FullPath);

            // 주기 reconcile: 훅의 단발 busy 쓰기가 레이스/누락으로 진실과 어긋나도 지속 수렴시킨다.
            // (재현 안 되는 간헐 조기소멸의 실질 방어 — 원인 무관하게 run 파일이 살아있으면 스피너 재무장,
            //  유령 run 파일은 prune 해 stuck-ON 도 자동 해제.) FileSystemWatcher 는 일회성이라 놓친 뒤 못 고침.
            // 부하 최소화: 자기 재예약 방식으로, 활성(진행중 턴/서브 존재) 시에만 촘촘히, 완전 유휴면 느슨히 돈다.
            // busy 파일 변경(Emit) 시엔 즉시 wake 해 방금 쓰인 idle 이 잘못됐는지 곧바로 교차확인한다.
            _reconcileTimer?.Dispose();
            _reconcileTimer = new System.Threading.Timer(_ => Reconcile(), null, ReconcileActiveMs, System.Threading.Timeout.Infinite);
        }
        catch { /* 감시 실패해도 앱은 계속 — 스피너만 안 뜸 */ }
    }

    /// <summary>방별 busy 파일을 "진실"(메인 턴 진행중 OR 살아있는 서브에이전트&gt;0)로 재평가해
    /// 어긋난 경우에만 정정 기록 + BusyChanged emit. 훅이 놓치거나 레이스로 틀리게 쓴 상태를 복구한다.</summary>
    private void Reconcile()
    {
        bool anyActive = false;
        try
        {
            var rooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var f in Directory.EnumerateFiles(Dir, "*.txt")) rooms.Add(Path.GetFileNameWithoutExtension(f)); } catch { }
            try
            {
                if (Directory.Exists(StateDir))
                    foreach (var f in Directory.EnumerateFiles(StateDir, "main_*.flag"))
                    {
                        var n = Path.GetFileNameWithoutExtension(f); // main_<room>
                        if (n.StartsWith("main_")) rooms.Add(n.Substring(5));
                    }
            }
            catch { }
            try { if (Directory.Exists(SubrunsDir)) foreach (var d in Directory.EnumerateDirectories(SubrunsDir)) rooms.Add(Path.GetFileName(d)); } catch { }

            foreach (var room in rooms)
            {
                if (string.IsNullOrEmpty(room)) continue;
                bool truth = ComputeBusyTruth(room);
                if (truth) anyActive = true; // 활성 방 있으면 다음 tick 을 촘촘히.
                var busyFile = Path.Combine(Dir, room + ".txt");
                var cur = TryRead(busyFile);
                // 파일 없고 idle 이 진실이면 그대로 둔다(앱 시작 시 stale 방지 정책과 일관 — 새 파일 안 만듦).
                if (cur == null && !truth) continue;
                bool curBusy = string.Equals(cur, "running", StringComparison.OrdinalIgnoreCase);
                if (curBusy == truth) continue; // 이미 일치 → 훅과 안 싸운다.
                // 계측: reconcile 이 훅 기록과 다른 판정을 내린 순간 — 스피너 오표시("작업 중인데 꺼짐"류) 조사용.
                DiagLog.Write($"busy[{room}] reconcile 정정: {(curBusy ? "running" : "idle")}→{(truth ? "running" : "idle")}");
                try { File.WriteAllText(busyFile, truth ? "running" : "idle"); } catch { /* 훅 쓰기와 경합 가능, 무시 */ }
                BusyChanged?.Invoke(room, truth);
            }
        }
        catch { /* reconcile 실패해도 앱은 계속 */ }
        // 활성이면 촘촘히, 완전 유휴면 느슨히 다음 tick 예약(유휴 시 CPU wake 최소화).
        try { _reconcileTimer?.Change(anyActive ? ReconcileActiveMs : ReconcileIdleMs, System.Threading.Timeout.Infinite); } catch { }
    }

    /// <summary>방이 실제로 활성인가(메인 턴 진행중 OR 살아있는 서브에이전트&gt;0) — 파일시스템 진실 직접 조회.
    /// 완료 판정 레이스(메인 Stop 이 SubagentStart 보다 먼저 idle emit) 확정용. roomId 는 훅과 동일한 sanitized 키.</summary>
    public bool IsRoomActive(string roomId)
        => !string.IsNullOrEmpty(roomId) && ComputeBusyTruth(roomId);

    /// <summary>방의 실제 busy 여부 = 메인 턴 진행중(main 플래그) OR 살아있는 서브에이전트 run 파일&gt;0.
    /// 카운트 전 SubMaxAge 초과 stale run 파일(SubagentStop 누락분)을 prune 한다.</summary>
    private static bool ComputeBusyTruth(string room)
    {
        if (File.Exists(Path.Combine(StateDir, "main_" + room + ".flag"))) return true;
        var rd = Path.Combine(SubrunsDir, room);
        if (!Directory.Exists(rd)) return false;
        bool anyLive = false;
        try
        {
            var cut = DateTime.Now - SubMaxAge;
            foreach (var f in Directory.EnumerateFiles(rd, "*.run"))
            {
                DateTime lw;
                try { lw = File.GetLastWriteTime(f); } catch { continue; }
                if (lw < cut) { try { File.Delete(f); } catch { /* 경합 무시 */ } }
                else anyLive = true;
            }
        }
        catch { }
        return anyLive;
    }

    private void EmitWaiting(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (string.IsNullOrWhiteSpace(status))
        {
            // Set-Content 가 파일을 비웠다 쓰는 truncate 찰나에 watcher 가 단발 Changed 를 받아 빈 값을
            // 읽으면, 그 뒤 'waiting'/'idle' 정착값을 영영 못 잡는다. 특히 1번 답변 '직후' 2번 선택지가
            // 곧바로 떠 같은 'waiting' 을 재기록하는 타이트한 타이밍에서 ❗ 재무장이 누락된다(몇 초 뒤면
            // 안정된 값을 읽어 우연히 잡힘). 짧게 뒤 한 번 더 읽어 정착값을 반드시 반영한다.
            _ = ReEmitWaitingAfterSettleAsync(path, room);
            return;
        }
        EmitWaitingKind(room, status);
    }

    private async System.Threading.Tasks.Task ReEmitWaitingAfterSettleAsync(string path, string room)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(60).ConfigureAwait(false);
            var status = TryRead(path);
            if (string.IsNullOrWhiteSpace(status)) return; // 그래도 비면(파일 삭제 등) 포기
            EmitWaitingKind(room, status);
        }
        catch { /* fire-and-forget — 재읽기 실패해도 앱 영향 없음 */ }
    }

    private void EmitWaitingKind(string room, string status)
    {
        // 선택지·권한을 구분하지 않고 통틀어 '입력 대기(❗)'로 emit(permission/input/waiting 모두 대기 취급).
        bool waiting = status.Equals("waiting", StringComparison.OrdinalIgnoreCase)
            || status.Equals("permission", StringComparison.OrdinalIgnoreCase)
            || status.Equals("input", StringComparison.OrdinalIgnoreCase);
        // 계측: ❗(입력 대기) 무장/해제 시각 — "멈춘 줄 알았는데 실은 권한/질문 대기였다" 판별용.
        if (!string.Equals(status, _lastEmitted.GetValueOrDefault("wait:" + room), StringComparison.OrdinalIgnoreCase))
        {
            _lastEmitted["wait:" + room] = status;
            DiagLog.Write($"wait[{room}]={status}");
        }
        WaitingChoiceChanged?.Invoke(room, waiting);
    }

    private void Emit(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (status == null) return;
        // 훅의 Set-Content 는 파일을 0바이트로 truncate 후 기록한다. 그 찰나에 watcher 가 Changed 를 받아
        // 빈 파일을 읽으면 status="" 가 되어 "running 아님"=idle 로 오인되고, busy→idle 전이로 잡혀 가짜
        // "응답 완료" 알림이 뜬다(서브에이전트 작업 중 매 상태 기록마다 깜빡임). 빈 읽기는 쓰기 중 과도상태이므로 무시.
        if (string.IsNullOrWhiteSpace(status)) return;
        // 계측: 훅이 쓴 busy 전이 기록. "작업 미완인데 스피너 꺼짐" 발생 시각의 마지막 전이가
        // (hook)idle 이면 Stop 훅이 실제 발화한 것(=턴이 정말 끝남), 앱 ESC 는 GracefulExit 줄로 구분.
        if (!string.Equals(status, _lastEmitted.GetValueOrDefault(room), StringComparison.OrdinalIgnoreCase))
        {
            _lastEmitted[room] = status;
            DiagLog.Write($"busy[{room}]={status} (hook)");
        }
        BusyChanged?.Invoke(room, status.Equals("running", StringComparison.OrdinalIgnoreCase));
        // busy 파일이 방금 바뀜 → 곧바로 reconcile 해 (특히 잘못 쓰인 idle 인지) 교차확인. 유휴 모드여도 즉시 깨움.
        try { _reconcileTimer?.Change(ReconcileWakeMs, System.Threading.Timeout.Infinite); } catch { }
    }

    /// <summary>상태 파일 내용("running"/"idle")을 읽는다. 쓰기 경합 시 짧게 재시도.</summary>
    private static string? TryRead(string path)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd().Trim();
            }
            catch (IOException) { System.Threading.Thread.Sleep(20); }
            catch { return null; }
        }
        return null;
    }

    public void Dispose()
    {
        _reconcileTimer?.Dispose();
        _reconcileTimer = null;
        _watcher?.Dispose();
        _watcher = null;
        _waitingWatcher?.Dispose();
        _waitingWatcher = null;
    }
}
