using System;
using System.IO;
using System.Text;
using System.Threading;

namespace DevezCode.Services;

/// <summary>codex 훅 (Resources/Hooks/codex-hook.ps1) 이 방별로 떨군 상태 파일
/// (lastmsg/busy/sessions) 을 감시해 세션의 마지막 user prompt · busy · codex session_id 를 알린다.
/// Claude 의 SessionLastMessageService / SessionBusyService 와 동일 패턴 (roomId 키).</summary>
public sealed class CodexHookService : IDisposable
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "codex");
    private static string LastmsgDir => Path.Combine(BaseDir, "lastmsg");
    private static string BusyDir => Path.Combine(BaseDir, "busy");
    private static string SessionDir => Path.Combine(BaseDir, "sessions");

    private FileSystemWatcher? _lastmsgWatcher;
    private FileSystemWatcher? _busyWatcher;
    private FileSystemWatcher? _sessionWatcher;

    /// <summary>(roomId, message) — 마지막 user prompt (1줄 요약, 200자).</summary>
    public event Action<string, string>? MessageChanged;
    /// <summary>(roomId, busy) — busy=true 면 요청 처리중 (스피너).</summary>
    public event Action<string, bool>? BusyChanged;
    /// <summary>(roomId, codexSessionId) — codex session_id 갱신. TerminalSessionManager 가 다음 --resume 에 사용.</summary>
    public event Action<string, string>? CodexSessionChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(LastmsgDir);
            Directory.CreateDirectory(BusyDir);
            Directory.CreateDirectory(SessionDir);

            // 앱 재시작 시 stale busy=running 이 남아 스피너가 영원히 도는 것 방지 (SessionBusyService 와 동일)
            foreach (var f in Directory.EnumerateFiles(BusyDir, "*.txt"))
                try { File.Delete(f); } catch { }
            // 세션 ID 는 보존 — 재오픈 시 이어가야 하므로 삭제 X

            _lastmsgWatcher = new FileSystemWatcher(LastmsgDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _lastmsgWatcher.Changed += (_, e) => EmitLastmsg(e.FullPath);
            _lastmsgWatcher.Created += (_, e) => EmitLastmsg(e.FullPath);
            foreach (var f in Directory.EnumerateFiles(LastmsgDir, "*.txt")) EmitLastmsg(f);

            _busyWatcher = new FileSystemWatcher(BusyDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _busyWatcher.Changed += (_, e) => EmitBusy(e.FullPath);
            _busyWatcher.Created += (_, e) => EmitBusy(e.FullPath);

            _sessionWatcher = new FileSystemWatcher(SessionDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _sessionWatcher.Changed += (_, e) => EmitSession(e.FullPath);
            _sessionWatcher.Created += (_, e) => EmitSession(e.FullPath);
            // 시작 시 저장된 세션 ID 1회 emit → TerminalSessionManager 가 첫 --resume 결정
            foreach (var f in Directory.EnumerateFiles(SessionDir, "*.txt")) EmitSession(f);
        }
        catch { /* 감시 실패해도 앱은 계속 */ }
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
        // Set-Content 의 truncate 찰나에 watcher 가 빈 파일을 읽어 idle 로 오인 → 가짜 "응답 완료" 알림 방지.
        // 다만 codex 훅은 Stop 에서만 idle 을 쓰므로, truncate 후 write 가 경합/중단으로 완료되지 못해
        // 파일이 0바이트로 "영구히" 남으면 이후 Changed 이벤트가 더 없어 idle 전이를 영영 놓친다(스피너
        // stuck-ON). claude 는 reconcile 타이머로 복구하지만 codex 엔 진실 소스가 없으므로, 빈 값은 짧게
        // 뒤 재확인해 정착값을 반영하고 그래도 비어 있으면 idle 로 확정한다(busy 파일 부재/빈 상태 = running 아님).
        if (string.IsNullOrWhiteSpace(status)) { _ = ReEmitBusyAfterSettleAsync(path, room); return; }
        BusyChanged?.Invoke(room, status.Equals("running", StringComparison.OrdinalIgnoreCase));
    }

    private async System.Threading.Tasks.Task ReEmitBusyAfterSettleAsync(string path, string room)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(120).ConfigureAwait(false);
            var status = TryRead(path);
            // 재확인 후에도 비어 있으면(truncate 중단·경합으로 영구 0바이트) idle 로 확정 → 스피너 해제.
            // 값이 정착됐으면 그대로 반영(running/idle).
            bool busy = !string.IsNullOrWhiteSpace(status)
                && status!.Equals("running", StringComparison.OrdinalIgnoreCase);
            BusyChanged?.Invoke(room, busy);
        }
        catch { /* fire-and-forget — 재읽기 실패해도 앱 영향 없음 */ }
    }

    private void EmitSession(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var sid = TryRead(path);
        if (sid != null) CodexSessionChanged?.Invoke(room, sid);
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
        _sessionWatcher?.Dispose();
    }
}
