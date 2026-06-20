using System.Runtime.InteropServices;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>시스템 CPU/메모리 사용량을 주기적으로 측정해 <see cref="SnapshotUpdated"/> 로 알린다.
/// Win32 GetSystemTimes(시스템 전체 CPU) + GetPerformanceInfo(물리 메모리) 사용. devez 이식.</summary>
public class PerfMonitorService : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation pi, uint cb);

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint  cb;
        public nint  CommitTotal, CommitLimit, CommitPeak;
        public nint  PhysicalTotal, PhysicalAvailable;
        public nint  SystemCache;
        public nint  KernelTotal, KernelPaged, KernelNonPaged;
        public nint  PageSize;
        public uint  HandleCount, ProcessCount, ThreadCount;
    }

    private Timer? _timer;
    private readonly float _memTotalMb;

    private long _prevIdle, _prevKernel, _prevUser;

    public event Action<PerfSnapshot>? SnapshotUpdated;

    public PerfMonitorService()
    {
        _memTotalMb = (float)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024d * 1024));
        // 첫 번째 읽기는 델타 계산용 기준값으로만 사용
        GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);
    }

    public void Start()
    {
        _timer?.Dispose();
        _timer = new Timer(_ => Capture(), null, 1000, 3000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Capture()
    {
        try
        {
            GetSystemTimes(out long idle, out long kernel, out long user);

            long dKernel = kernel - _prevKernel;
            long dUser   = user   - _prevUser;
            long dIdle   = idle   - _prevIdle;
            long dTotal  = dKernel + dUser;

            _prevIdle   = idle;
            _prevKernel = kernel;
            _prevUser   = user;

            float cpu = dTotal > 0 ? (float)(dTotal - dIdle) / dTotal * 100f : 0f;

            float availMb = 0;
            if (GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<PerformanceInformation>()))
                availMb = (float)(pi.PhysicalAvailable.ToInt64() * pi.PageSize.ToInt64() / (1024d * 1024));

            SnapshotUpdated?.Invoke(new PerfSnapshot
            {
                CpuPercent    = Math.Clamp(cpu, 0f, 100f),
                MemoryUsedMb  = _memTotalMb - availMb,
                MemoryTotalMb = _memTotalMb,
            });
        }
        catch { }
    }

    public void Dispose() => Stop();
}
