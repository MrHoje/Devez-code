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

    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _waitingWatcher;

    /// <summary>(roomId, busy) — busy=true 면 요청 처리중.</summary>
    public event Action<string, bool>? BusyChanged;

    /// <summary>(roomId, waiting) — waiting=true 면 선택지/권한 응답 대기 중(❗). Notification 훅으로 판정.</summary>
    public event Action<string, bool>? WaitingChoiceChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(WaitingDir);
            foreach (var f in Directory.EnumerateFiles(WaitingDir))
                try { File.Delete(f); } catch { /* 시작 시 stale 대기/perm 마커 제거 */ }
            // 앱 시작 시 기존 busy 파일 모두 삭제. 프로그램을 닫으면 ConPTY/claude 프로세스는 죽지만
            // busy 상태 파일은 디스크에 남아있어, 재시작 후 세션을 다시 열면 이전 세션의 stale 한
            // "running" 상태가 그대로 emit 되어 스피너가 영원히 도는 문제가 생긴다(앱 재시작 후 세션
            // 재오픈 시 스피너 안 멈춤). 새 세션은 hook 이 fresh 상태를 다시 쓸 때까지는 IsBusy=false 유지.
            foreach (var f in Directory.EnumerateFiles(Dir, "*.txt"))
                try { File.Delete(f); } catch { /* hook write 와 경합 가능, 무시 */ }
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(Dir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, e) => Emit(e.FullPath);
            _watcher.Created += (_, e) => Emit(e.FullPath);

            _waitingWatcher?.Dispose();
            _waitingWatcher = new FileSystemWatcher(WaitingDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _waitingWatcher.Changed += (_, e) => EmitWaiting(e.FullPath);
            _waitingWatcher.Created += (_, e) => EmitWaiting(e.FullPath);
        }
        catch { /* 감시 실패해도 앱은 계속 — 스피너만 안 뜸 */ }
    }

    private void EmitWaiting(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (string.IsNullOrWhiteSpace(status)) return; // truncate 찰나의 빈 읽기 무시
        WaitingChoiceChanged?.Invoke(room, status.Equals("waiting", StringComparison.OrdinalIgnoreCase));
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
        BusyChanged?.Invoke(room, status.Equals("running", StringComparison.OrdinalIgnoreCase));
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
        _watcher?.Dispose();
        _watcher = null;
        _waitingWatcher?.Dispose();
        _waitingWatcher = null;
    }
}
