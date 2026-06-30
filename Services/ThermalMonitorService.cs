using System.Timers;

namespace DevezCode.Services;

/// <summary>Windows PerformanceCounter(\Thermal Zone Information\Temperature)로
/// CPU 온도를 주기적으로 읽는다. Ring0 드라이버 불필요.
/// 연속 6회 실패(약 18초)면 타이머를 중지하고 TMP를 숨긴다.</summary>
public sealed class ThermalMonitorService : IDisposable
{
    private System.Threading.Timer? _poll;
    private int _failCount;
    private bool _stopped;
    private float _smoothedTemp = float.NaN;
    private const int MaxFailures = 6;
    private const float SmoothFactor = 0.25f;

    /// <summary>snapshot != null: 온도 값, snapshot == null: 중지(숨김) 신호</summary>
    public event Action<Models.ThermalSnapshot?>? SnapshotUpdated;
    public bool HasTemperature { get; private set; }
    public bool Stopped => _stopped;

    public void Start()
    {
        Stop();
        _failCount = 0;
        _smoothedTemp = float.NaN;
        _stopped = false;
        _poll = new System.Threading.Timer(_ => Capture(), null, 1000, 3000);
    }

    public void Stop()
    {
        _poll?.Dispose();
        _poll = null;
    }

    private void Capture()
    {
        if (_stopped) return;

        float? cpuTemp = null;

        try
        {
            cpuTemp = ReadThermalZone();
        }
        catch { }

        if (cpuTemp is > 0f and < 150f)
        {
            _failCount = 0;
            HasTemperature = true;

            // EMA smoothing — 급격한 온도 튐 완화
            if (float.IsNaN(_smoothedTemp))
                _smoothedTemp = cpuTemp.Value;
            else
                _smoothedTemp = _smoothedTemp * (1f - SmoothFactor) + cpuTemp.Value * SmoothFactor;

            float display = MathF.Round(_smoothedTemp);
            SnapshotUpdated?.Invoke(new Models.ThermalSnapshot { CpuTemperature = display });
        }
        else
        {
            _failCount++;
            if (_failCount >= MaxFailures)
            {
                _stopped = true;
                _poll?.Dispose();
                _poll = null;
                HasTemperature = false;
                SnapshotUpdated?.Invoke(null); // null = TMP 숨김 신호
            }
        }
    }

    /// <summary>\Thermal Zone Information\Temperature 퍼포먼스 카운터를 읽어 CPU 온도(°C) 반환.</summary>
    private static float? ReadThermalZone()
    {
        // 인스턴스 열거 시도
        try
        {
            var category = new System.Diagnostics.PerformanceCounterCategory("Thermal Zone Information");
            string[] instances = category.GetInstanceNames();
            foreach (var inst in instances)
            {
                var temp = TryReadCounter(inst);
                if (temp != null) return temp;
            }
        }
        catch { }

        // 고정 인스턴스명 폴백
        string[] fallbackInstances = { "_TZ.TZ00", "_TZ.TZ01", "_tz.tz00", "_tz.tz01", "TZ00", "TZ01", "tz00", "tz01" };
        foreach (var inst in fallbackInstances)
        {
            var temp = TryReadCounter(inst);
            if (temp != null) return temp;
        }

        return null;
    }

    private static float? TryReadCounter(string instanceName)
    {
        try
        {
            using var counter = new System.Diagnostics.PerformanceCounter(
                "Thermal Zone Information", "Temperature", instanceName, true);
            counter.ReadOnly = true;
            long raw = counter.RawValue;
            if (raw <= 0) return null;

            // deci-Kelvin(x10) 시도
            float c = (raw - 2732f) / 10f;
            if (c is > 0 and < 150) return c;
            // Kelvin 시도
            c = raw - 273.15f;
            if (c is > 0 and < 150) return c;
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        Stop();
    }
}
