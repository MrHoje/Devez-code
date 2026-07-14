using System.IO;
using System.Text;

namespace DevezCode.Services;

/// <summary>opencode 플러그인(opencode-room-tracker.js) 이 방별로 떨군 마지막 프롬프트
/// 파일(lastmsg\&lt;roomId&gt;.txt) 을 감시해 세션의 "마지막 보낸 메시지"를 알린다.
/// claude 의 SessionLastMessageService 와 동일 패턴 — FileSystemWatcher 로 즉시 반영되어
/// 헤더 타이틀이 폴링 지연 없이 갱신된다.</summary>
public sealed class OpenCodeLastMessageService : IDisposable
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "opencode", "lastmsg");

    private FileSystemWatcher? _watcher;

    /// <summary>(roomId, message) — 마지막으로 보낸 프롬프트(1줄 요약).</summary>
    public event Action<string, string>? MessageChanged;

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
            _watcher.Renamed += (_, e) => Emit(e.FullPath);
            // 시작 시 기존 값 1회 반영(앱 재시작 후 헤더 복원)
            foreach (var f in Directory.EnumerateFiles(Dir, "*.txt")) Emit(f);
        }
        catch { /* 감시 실패해도 앱은 계속 — 헤더 부제만 안 뜸 */ }
    }

    private void Emit(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var msg = TryRead(path);
        if (msg == null) return;
        MessageChanged?.Invoke(room, msg);
    }

    /// <summary>프롬프트 파일 내용을 읽는다. 쓰기 경합 시 짧게 재시도.</summary>
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
