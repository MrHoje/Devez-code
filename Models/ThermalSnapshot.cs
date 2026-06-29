namespace DevezCode.Models;

/// <summary>시스템 온도 스냅샷(CPU/GPU). 읽기 전용.</summary>
public sealed class ThermalSnapshot
{
    /// <summary>CPU 온도(°C). 센서가 없으면 null.</summary>
    public float? CpuTemperature { get; init; }
    /// <summary>GPU 온도(°C). 센서가 없으면 null.</summary>
    public float? GpuTemperature { get; init; }

    public bool HasData => CpuTemperature.HasValue || GpuTemperature.HasValue;
}
