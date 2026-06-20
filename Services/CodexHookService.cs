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
        BusyChanged?.Invoke(room, status.Equals("running", StringComparison.OrdinalIgnoreCase));
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
