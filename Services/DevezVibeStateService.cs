using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DevezCode.Services;

/// <summary>Devez Vibe(dvz) 가 방별로 떨군 상태 파일(sessions/busy/waiting/lastmsg)을 감시해
/// 마지막 user prompt · busy · ❗대기 · thread ID 를 알린다. 다른 에이전트와 달리 훅 스크립트가 없다 —
/// dvz 자체(src/devezcode.rs)가 <c>DEVEZCODE_ROOM_ID</c> 를 보고 같은 경로에 직접 기록한다.
/// 파일 규약은 KimiHookService/CodexHookService 와 동일(roomId 키, 원자적 rename 쓰기).</summary>
public sealed class DevezVibeStateService : IDisposable
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "devezvibe");
    private static string LastmsgDir => Path.Combine(BaseDir, "lastmsg");
    private static string BusyDir => Path.Combine(BaseDir, "busy");
    private static string WaitingDir => Path.Combine(BaseDir, "waiting");
    private static string SessionDir => Path.Combine(BaseDir, "sessions");
    /// <summary>개명 전(devezcli) 상태 폴더. 새 폴더가 없을 때만 통째로 옮겨 세션 복원을 잇는다.</summary>
    private static string LegacyBaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "devezcli");

    private FileSystemWatcher? _lastmsgWatcher;
    private FileSystemWatcher? _busyWatcher;
    private FileSystemWatcher? _waitingWatcher;
    private FileSystemWatcher? _sessionWatcher;

    /// <summary>(roomId, message) — 마지막 user prompt (1줄 요약, 200자).</summary>
    public event Action<string, string>? MessageChanged;
    /// <summary>(roomId, busy, loading) — busy=true 면 스피너 표시, loading=true 면 resume 복원 중.</summary>
    public event Action<string, bool, bool>? BusyChanged;
    /// <summary>(roomId, waiting) — 승인/질문/MCP 응답 대기 중(❗).</summary>
    public event Action<string, bool>? WaitingChoiceChanged;
    /// <summary>(roomId, threadId) — dvz thread ID 갱신. TerminalSessionManager 가 다음 -r 에 사용.</summary>
    public event Action<string, string>? SessionChanged;

    /// <summary>방 파일명 정규화 — dvz 쪽 sanitize(영숫자·_·- 만 유지)와 동일.</summary>
    private static string Sanitize(string room) => Regex.Replace(room ?? "", @"[^\w\-]", "");

    private static string SessionPath(string room) => Path.Combine(SessionDir, Sanitize(room) + ".txt");

    /// <summary>방의 추적 thread ID (스냅샷/복원용). UUID 가 아니면 null.</summary>
    public static string? LoadTrackedSessionId(string room)
    {
        var sid = TryRead(SessionPath(room));
        return LooksLikeSessionId(sid) ? sid : null;
    }

    /// <summary>dvz 세션 ID 형식 검증. dvz 는 한 방에서 Codex thread(UUID) 외에 Claude 세션
    /// (<c>claude:UUID</c>)과 OpenCode 세션(<c>ses_…</c>)도 기록한다 — UUID 만 통과시키면
    /// 그 방은 복원 대상이 없다고 판정돼 매번 새 대화로 열린다.</summary>
    public static bool LooksLikeSessionId(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return false;
        if (sid!.StartsWith("ses_", StringComparison.Ordinal)) return sid.Length > 4;
        return Guid.TryParse(StripBackendPrefix(sid), out _);
    }

    /// <summary>dvz 가 화면에 쓰는 ID 에서 백엔드 접두사를 뗀 실제 세션 ID.</summary>
    public static string StripBackendPrefix(string sid)
        => sid.StartsWith("claude:", StringComparison.Ordinal)
            ? sid.Substring("claude:".Length)
            : sid;

    /// <summary>방이 턴 진행 중인지(종료 계획의 Esc 선행 판단용). 인스턴스 없이 파일만 본다.</summary>
    public static bool IsBusyRunning(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return false;
        var status = TryRead(Path.Combine(BusyDir, Sanitize(roomId) + ".txt"));
        return !string.IsNullOrWhiteSpace(status)
            && status.Equals("running", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>복원 대화가 아직 화면에 재구성되는 중인지 확인한다.
    /// 터미널의 초기 프레임만으로 로딩 커버가 먼저 걷히는 것을 막는 준비 게이트에서 사용한다.</summary>
    public static bool IsSessionLoading(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return false;
        var status = TryRead(Path.Combine(BusyDir, Sanitize(roomId) + ".txt"));
        return !string.IsNullOrWhiteSpace(status)
            && status.Equals("loading", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>방 종료·삭제 시 남은 상태 파일 정리.</summary>
    public static void DeleteRoomFiles(string room)
    {
        var name = Sanitize(room) + ".txt";
        foreach (var dir in new[] { LastmsgDir, BusyDir, WaitingDir, SessionDir })
            try { File.Delete(Path.Combine(dir, name)); } catch { }
        ClearRoomOwner(room);
    }

    /// <summary>새 dvz 프로세스가 방 상태 파일의 소유권을 다시 획득할 수 있게 이전 실행의 소유권만 비운다.</summary>
    public static void ClearRoomOwner(string room)
    {
        try { File.Delete(Path.Combine(BaseDir, "owners", Sanitize(room) + ".txt")); } catch { }
    }

    /// <summary>Devez CLI → Devez Vibe 개명 1회 이관. 새 폴더가 이미 있으면(새 이름 dvz 가 한 번 돈 뒤)
    /// 그쪽이 최신이라 건드리지 않는다 — 스레드 ID 는 settings 에도 있어 복원은 유지된다.</summary>
    private static void MigrateLegacyDirOnce()
    {
        try
        {
            if (Directory.Exists(BaseDir) || !Directory.Exists(LegacyBaseDir)) return;
            Directory.Move(LegacyBaseDir, BaseDir);
        }
        catch { /* 실패해도 새 폴더로 새로 시작 */ }
    }

    public void Start()
    {
        try
        {
            MigrateLegacyDirOnce();
            Directory.CreateDirectory(LastmsgDir);
            Directory.CreateDirectory(BusyDir);
            Directory.CreateDirectory(WaitingDir);
            Directory.CreateDirectory(SessionDir);

            // 앱 재시작 시 stale busy/waiting 이 남아 스피너·❗ 가 영원히 도는 것 방지.
            foreach (var f in Directory.EnumerateFiles(BusyDir, "*.txt"))
                try { File.Delete(f); } catch { }
            foreach (var f in Directory.EnumerateFiles(WaitingDir, "*.txt"))
                try { File.Delete(f); } catch { }
            // 세션 ID 는 보존 — 재오픈 시 이어가야 하므로 삭제 X

            _lastmsgWatcher = MakeWatcher(LastmsgDir, EmitLastmsg);
            foreach (var f in Directory.EnumerateFiles(LastmsgDir, "*.txt")) EmitLastmsg(f);

            _busyWatcher = MakeWatcher(BusyDir, EmitBusy);
            _waitingWatcher = MakeWatcher(WaitingDir, EmitWaiting);

            _sessionWatcher = MakeWatcher(SessionDir, EmitSession);
            // 시작 시 저장된 thread ID 1회 emit → TerminalSessionManager 가 첫 -r 결정
            foreach (var f in Directory.EnumerateFiles(SessionDir, "*.txt")) EmitSession(f);
        }
        catch { /* 감시 실패해도 앱은 계속 */ }
    }

    private static FileSystemWatcher MakeWatcher(string dir, Action<string> emit)
    {
        var w = new FileSystemWatcher(dir, "*.txt")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        w.Changed += (_, e) => emit(e.FullPath);
        w.Created += (_, e) => emit(e.FullPath);
        w.Renamed += (_, e) => emit(e.FullPath);
        return w;
    }

    private void EmitLastmsg(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var msg = TryRead(path);
        if (msg != null) MessageChanged?.Invoke(room, msg);
    }

    private void EmitBusy(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (status == null) return;
        // rename 직전의 빈 파일을 idle 로 오인 방지 — 짧게 뒤 재확인, 그래도 비면 idle 확정(stuck-ON 방지).
        if (string.IsNullOrWhiteSpace(status)) { _ = ReEmitBusyAfterSettleAsync(path, room); return; }
        bool loading = status.Equals("loading", StringComparison.OrdinalIgnoreCase);
        bool busy = status.Equals("running", StringComparison.OrdinalIgnoreCase);
        BusyChanged?.Invoke(room, busy, loading);
    }

    private async System.Threading.Tasks.Task ReEmitBusyAfterSettleAsync(string path, string room)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(120).ConfigureAwait(false);
            var status = TryRead(path);
            bool loading = !string.IsNullOrWhiteSpace(status)
                && status!.Equals("loading", StringComparison.OrdinalIgnoreCase);
            bool busy = !string.IsNullOrWhiteSpace(status)
                && status!.Equals("running", StringComparison.OrdinalIgnoreCase);
            BusyChanged?.Invoke(room, busy, loading);
        }
        catch { /* best effort */ }
    }

    /// <summary>완료 정착 재확인 — busy 파일이 running 이면 아직 진행 중(완료 토스트 보류).</summary>
    public bool IsRoomBusy(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return false;
        try
        {
            var status = TryRead(Path.Combine(BusyDir, Sanitize(roomId) + ".txt"));
            return !string.IsNullOrWhiteSpace(status)
                && status.Equals("running", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void EmitWaiting(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (string.IsNullOrWhiteSpace(status)) { _ = ReEmitWaitingAfterSettleAsync(path, room); return; }
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
        catch { /* best effort */ }
    }

    private void EmitSession(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var sid = TryRead(path);
        if (LooksLikeSessionId(sid)) SessionChanged?.Invoke(room, sid!);
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

    public void Dispose()
    {
        _lastmsgWatcher?.Dispose();
        _busyWatcher?.Dispose();
        _waitingWatcher?.Dispose();
        _sessionWatcher?.Dispose();
    }
}
