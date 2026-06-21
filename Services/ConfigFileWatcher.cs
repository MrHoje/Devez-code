using System;
using System.IO;

namespace DevezCode.Services;

/// <summary>설정 파일 변경 감시 (FileSystemWatcher) — 파일 → DevezCode 방향 동기화.
/// 파일이 다른 프로세스(외부 에디터, 다른 claude 세션 등)에 의해 바뀐 경우
/// 짧은 디바운스 후 onChanged 콜백을 1회 호출한다. 자기 자신이 쓴 직후에는
/// 호출되면 안 되므로, 호출자가 마커를 넘겨 외부에서 비교해 무시할 수 있다.</summary>
public static class ConfigFileWatcher
{
    public static IDisposable? Watch(string? filePath, Action onChanged)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
        try
        {
            var dir = Path.GetDirectoryName(filePath)!;
            var name = Path.GetFileName(filePath);
            var w = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            // 1.0초 디바운스 — atomic write (.tmp → File.Replace) 가 두 이벤트(Deleted+Created)를
            // 한 번에 발생시킬 수 있어 1회로 합친다.
            object? pending = null;
            w.Changed += (_, e) => Schedule(e.FullPath, ref pending, onChanged);
            w.Created += (_, e) => Schedule(e.FullPath, ref pending, onChanged);
            w.Renamed += (_, e) => Schedule(e.FullPath, ref pending, onChanged);
            return new Disposer(() => { w.EnableRaisingEvents = false; w.Dispose(); });
        }
        catch { return null; }
    }

    private static void Schedule(string path, ref object? pending, Action onChanged)
    {
        // System.Timers.Timer 는 WPF 에서도 안전. 단순 lock-free 디바운스.
        if (pending is System.Timers.Timer t) { t.Stop(); t.Dispose(); }
        var n = new System.Timers.Timer(1000) { AutoReset = false };
        n.Elapsed += (_, _) => { n.Dispose(); System.Windows.Application.Current?.Dispatcher.Invoke(onChanged); };
        pending = n;
        n.Start();
    }

    private sealed class Disposer : IDisposable
    {
        private readonly Action _a;
        public Disposer(Action a) { _a = a; }
        public void Dispose() => _a();
    }
}
