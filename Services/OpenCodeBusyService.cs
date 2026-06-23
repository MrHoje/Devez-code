using System.IO;
using System.Text;

namespace DevezCode.Services;

/// <summary>opencode 플러그인(opencode-room-tracker.js) 이 방별로 떨군 busy 상태 파일
/// (busy\&lt;roomId&gt;.txt = running|idle) 을 감시해 세션의 요청 처리중 여부를 알린다.
/// claude 의 SessionBusyService / codex 의 CodexHookService 와 동일 패턴 — roomId 키로 정확 매칭.</summary>
public sealed class OpenCodeBusyService : IDisposable
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "opencode", "busy");

    private FileSystemWatcher? _watcher;

    /// <summary>(roomId, busy) — busy=true 면 요청 처리중(스피너).</summary>
    public event Action<string, bool>? BusyChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // 앱 재시작 시 stale running 이 남아 스피너가 영원히 도는 것 방지 (SessionBusyService 와 동일).
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
        }
        catch { /* 감시 실패해도 앱은 계속 — 스피너만 안 뜸 */ }
    }

    private void Emit(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (status == null) return;
        BusyChanged?.Invoke(room, status.Equals("running", StringComparison.OrdinalIgnoreCase));
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
            catch (IOException) { System.Threading.Thread.Sleep(20); }
            catch { return null; }
        }
        return null;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
