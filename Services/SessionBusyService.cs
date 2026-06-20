using System.IO;

namespace DevezCode.Services;

/// <summary>claude busy 훅(busy-hook.ps1)이 방별로 떨군 상태 파일(busy\&lt;roomId&gt;.txt = running|idle)을
/// 감시해 세션의 요청 처리중 여부를 알린다. clude-blinker 의 훅+파일 감시 방식 이식.
/// roomId 는 파일명에서 그대로 얻으므로 세션 매칭이 정확하다(터미널 출력 파싱 불필요).</summary>
public sealed class SessionBusyService : IDisposable
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude", "busy");

    private FileSystemWatcher? _watcher;

    /// <summary>(roomId, busy) — busy=true 면 요청 처리중.</summary>
    public event Action<string, bool>? BusyChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(Dir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, e) => Emit(e.FullPath);
            _watcher.Created += (_, e) => Emit(e.FullPath);
            // 시작 시 기존 상태 1회 반영(앱 재시작 중 진행되던 세션 등)
            foreach (var f in Directory.EnumerateFiles(Dir, "*.txt")) Emit(f);
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
    }
}
