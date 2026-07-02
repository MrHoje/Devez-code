using System.IO;

namespace DevezCode.Services;

/// <summary>세션 열기 지연 진단용 경량 로거 — %AppData%\DevezCode\diag.log.
/// "일부 자리에서 세션 열기에 1분" 증상의 구간 계측(어디서 새는지)이 목적.
/// 원인 확정 후 로그 포인트는 제거해도 되고, 상시 켜둬도 비용은 파일 append 뿐.</summary>
public static class DiagLog
{
    private static readonly object Lock = new();
    private const long MaxBytes = 2 * 1024 * 1024; // 초과 시 .1 로 로테이션

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "diag.log");

    public static void Write(string msg)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = LogPath + ".1";
                    File.Delete(old);
                    File.Move(LogPath, old);
                }
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId}] {msg}\r\n");
            }
        }
        catch { /* 진단 로그 실패는 무시 */ }
    }

    /// <summary>구간 시간 측정 — using 으로 감싸면 종료 시 소요 ms 기록. 10ms 미만은 생략.</summary>
    public static IDisposable Time(string label) => new Scope(label);

    private sealed class Scope : IDisposable
    {
        private readonly string _label;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public Scope(string label) => _label = label;
        public void Dispose()
        {
            if (_sw.ElapsedMilliseconds >= 10)
                Write($"{_label} took {_sw.ElapsedMilliseconds}ms");
        }
    }

    private static System.Windows.Threading.DispatcherTimer? _heartbeat;
    private static int _lastTick;

    /// <summary>UI 스레드 정지 감지 — 500ms 심장박동 사이 간격이 2초 넘으면 그 길이를 기록.
    /// "1분 대기"가 UI 스레드 블록인지(간격 하나가 60초) 다른 대기인지 즉시 판별된다.</summary>
    public static void StartUiStallDetector()
    {
        if (_heartbeat != null) return;
        _lastTick = Environment.TickCount;
        _heartbeat = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _heartbeat.Tick += (_, _) =>
        {
            int now = Environment.TickCount;
            int gap = unchecked(now - _lastTick);
            if (gap > 2000) Write($"UI-STALL: dispatcher blocked ~{gap}ms");
            _lastTick = now;
        };
        _heartbeat.Start();
        Write("=== app started, UI stall detector on ===");
    }
}
