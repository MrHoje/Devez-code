using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DevezCode.Services;

/// <summary>Devez CLI(dvz) 가 방별로 떨군 상태 파일(sessions/busy/waiting/lastmsg)을 감시해
/// 마지막 user prompt · busy · ❗대기 · thread ID 를 알린다. 다른 에이전트와 달리 훅 스크립트가 없다 —
/// dvz 자체(src/devezcode.rs)가 <c>DEVEZCODE_ROOM_ID</c> 를 보고 같은 경로에 직접 기록한다.
/// 파일 규약은 KimiHookService/CodexHookService 와 동일(roomId 키, 원자적 rename 쓰기).</summary>
public sealed class DevezCliStateService : IDisposable
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "devezcli");
    private static string LastmsgDir => Path.Combine(BaseDir, "lastmsg");
    private static string BusyDir => Path.Combine(BaseDir, "busy");
    private static string WaitingDir => Path.Combine(BaseDir, "waiting");
    private static string SessionDir => Path.Combine(BaseDir, "sessions");

    private FileSystemWatcher? _lastmsgWatcher;
    private FileSystemWatcher? _busyWatcher;
    private FileSystemWatcher? _waitingWatcher;
    private FileSystemWatcher? _sessionWatcher;

    /// <summary>(roomId, message) — 마지막 user prompt (1줄 요약, 200자).</summary>
    public event Action<string, string>? MessageChanged;
    /// <summary>(roomId, busy) — busy=true 면 턴 진행 중 (스피너).</summary>
    public event Action<string, bool>? BusyChanged;
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

    /// <summary>dvz(=codex thread) 세션 ID 형식 검증.</summary>
    public static bool LooksLikeSessionId(string? sid)
        => !string.IsNullOrWhiteSpace(sid) && Guid.TryParse(sid, out _);

    /// <summary>방이 턴 진행 중인지(종료 계획의 Esc 선행 판단용). 인스턴스 없이 파일만 본다.</summary>
    public static bool IsBusyRunning(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return false;
        var status = TryRead(Path.Combine(BusyDir, Sanitize(roomId) + ".txt"));
        return !string.IsNullOrWhiteSpace(status)
            && status.Equals("running", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>방 종료·삭제 시 남은 상태 파일 정리.</summary>
    public static void DeleteRoomFiles(string room)
    {
        var name = Sanitize(room) + ".txt";
        foreach (var dir in new[] { LastmsgDir, BusyDir, WaitingDir, SessionDir })
            try { File.Delete(Path.Combine(dir, name)); } catch { }
    }

    public void Start()
    {
        try
        {
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
        BusyChanged?.Invoke(room, status.Equals("running", StringComparison.OrdinalIgnoreCase));
    }

    private async System.Threading.Tasks.Task ReEmitBusyAfterSettleAsync(string path, string room)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(120).ConfigureAwait(false);
            var status = TryRead(path);
            bool busy = !string.IsNullOrWhiteSpace(status)
                && status!.Equals("running", StringComparison.OrdinalIgnoreCase);
            BusyChanged?.Invoke(room, busy);
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
