using System.IO;

namespace DevezCode.Services;

/// <summary>claude statusLine 훅이 방별로 떨군 실제 model/effort(modeleffort\&lt;room&gt;.txt = "modelId\neffortLevel")를
/// 감시해 메타바 콤보에 라이브 연동한다. statusLine 은 매 메시지/턴마다 호출되므로 TUI 안에서 /model 로 바꿔도 반영된다.</summary>
public sealed class ModelEffortService : IDisposable
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude", "modeleffort");

    private FileSystemWatcher? _watcher;

    /// <summary>(roomId, modelId, effortLevel) — 파일에 기록된 원본 값(매핑 전).</summary>
    public event Action<string, string?, string?>? Changed;

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
        }
        catch { /* 감시 실패해도 앱은 계속 — 콤보가 저장값/기본값으로만 동작 */ }
    }

    /// <summary>현재 디스크에 기록된 방의 (modelId, effortLevel). 없으면 (null, null). 초기 표시용 동기 조회.</summary>
    public (string? model, string? effort) Read(string roomId)
    {
        try
        {
            var path = Path.Combine(Dir, SafeRoom(roomId) + ".txt");
            return ParseFile(path);
        }
        catch { return (null, null); }
    }

    private void Emit(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var (model, effort) = ParseFile(path);
        if (model == null && effort == null) return;
        Changed?.Invoke(room, model, effort);
    }

    /// <summary>"modelId\neffortLevel" 2줄 파일을 읽는다. 쓰기 경합 시 짧게 재시도.</summary>
    private static (string? model, string? effort) ParseFile(string path)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!File.Exists(path)) return (null, null);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var lines = sr.ReadToEnd().Replace("\r", "").Split('\n');
                var model  = lines.Length > 0 && lines[0].Trim().Length > 0 ? lines[0].Trim() : null;
                var effort = lines.Length > 1 && lines[1].Trim().Length > 0 ? lines[1].Trim() : null;
                return (model, effort);
            }
            catch (IOException) { System.Threading.Thread.Sleep(20); }
            catch { return (null, null); }
        }
        return (null, null);
    }

    /// <summary>statusLine 훅이 영속한 방의 마지막 (modelId, effort). 인스턴스 없이 런치 시점에 조회용.</summary>
    public static (string? model, string? effort) ReadPersisted(string roomId)
    {
        try { return ParseFile(Path.Combine(Dir, SafeRoom(roomId) + ".txt")); }
        catch { return (null, null); }
    }

    /// <summary>claude model.id("claude-opus-4-8…") → 런치 플래그/콤보 값(opus/sonnet/haiku/fable). 미상이면 null.</summary>
    public static string? ToModelValue(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var s = id.ToLowerInvariant();
        if (s.Contains("opus")) return "opus";
        if (s.Contains("sonnet")) return "sonnet";
        if (s.Contains("haiku")) return "haiku";
        if (s.Contains("fable") || s.Contains("mythos")) return "fable";
        return null;
    }

    private static string SafeRoom(string roomId)
        => System.Text.RegularExpressions.Regex.Replace(roomId, @"[^\w\-]", "");

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
