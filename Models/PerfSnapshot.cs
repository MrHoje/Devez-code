namespace DevezCode.Models;

/// <summary>성능 모니터 1회 측정값 (CPU/메모리). devez 이식.</summary>
public class PerfSnapshot
{
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public float CpuPercent    { get; init; }
    public float MemoryUsedMb  { get; init; }
    public float MemoryTotalMb { get; init; }
}
