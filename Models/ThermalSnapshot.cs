namespace DevezCode.Models;

/// <summary>CPU 온도 스냅샷. null = 중지(숨김) 신호.</summary>
public sealed class ThermalSnapshot
{
    public float? CpuTemperature { get; init; }
    public bool HasData => CpuTemperature.HasValue;
}
