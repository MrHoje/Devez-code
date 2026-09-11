namespace DevezCode.Services;

/// <summary>
/// Windows PerformanceCounter(\Thermal Zone Information\Temperature)로 CPU 온도를 주기적으로 읽는다.
/// Ring0 드라이버 불필요(관리자 권한 불필요). ACPI 써멀존 값이라 근사치지만, 아래 두 가지로 안정화한다:
///   1) 최댓값 채택 — 매 폴링 모든 존을 읽어 최댓값을 쓴다(가장 뜨거운=CPU 근접 존, 결정론적이라 존 간 널뛰기 없음).
///   2) 스파이크 필터 — 한 스텝에서 과도한 급변은 1회 무시(연속되면 실제 변화로 인정).
/// 연속 6회 실패(약 18초)면 타이머를 멈추고 TMP 를 숨긴다.
/// </summary>
public sealed class ThermalMonitorService : IDisposable
{
    private System.Threading.Timer? _poll;
    // 써멀존 카운터 캐시 — PerformanceCounter/PerformanceCounterCategory 생성은 perflib 조회라 비싸다.
    // 3초마다 새로 만들면 유휴 CPU를 계속 먹으므로 한 번 만들어 재사용하고, 전부 실패할 때만 다시 만든다.
    private System.Diagnostics.PerformanceCounter[]? _zoneCounters;
    private int _failCount;
    private bool _stopped;
    private float _smoothedTemp = float.NaN;
    private int _spikeHits;            // 연속 스파이크 카운트

    private const int MaxFailures = 6;
    private const float SmoothFactor = 0.25f;
    private const float SpikeLimit = 25f; // 한 스텝(3초) 허용 최대 변화(°C). 초과 시 1회 무시.

    /// <summary>snapshot != null: 온도 값, snapshot == null: 중지(숨김) 신호</summary>
    public event Action<Models.ThermalSnapshot?>? SnapshotUpdated;
    public bool HasTemperature { get; private set; }
    public bool Stopped => _stopped;

    public void Start()
    {
        Stop();
        _failCount = 0;
        _spikeHits = 0;
        _smoothedTemp = float.NaN;
        _stopped = false;
        _poll = new System.Threading.Timer(_ => Capture(), null, 1000, 3000);
    }

    public void Stop()
    {
        _poll?.Dispose();
        _poll = null;
        ResetCounters();
    }

    private void Capture()
    {
        if (_stopped) return;

        float? cpuTemp = null;
        try { cpuTemp = ReadThermalZone(); }
        catch { }

        if (cpuTemp is > 0f and < 150f)
        {
            _failCount = 0;

            // 스파이크 필터 — 급변이 1회뿐이면 무시(전이 글리치), 연속되면 실제 변화로 인정.
            if (!float.IsNaN(_smoothedTemp) && MathF.Abs(cpuTemp.Value - _smoothedTemp) > SpikeLimit)
            {
                if (++_spikeHits < 2) return; // 이번 샘플 버림(스무딩 값 유지)
            }
            _spikeHits = 0;

            HasTemperature = true;

            // EMA smoothing — 잔떨림 완화
            if (float.IsNaN(_smoothedTemp))
                _smoothedTemp = cpuTemp.Value;
            else
                _smoothedTemp = _smoothedTemp * (1f - SmoothFactor) + cpuTemp.Value * SmoothFactor;

            SnapshotUpdated?.Invoke(new Models.ThermalSnapshot { CpuTemperature = MathF.Round(_smoothedTemp) });
        }
        else
        {
            _failCount++;
            if (_failCount >= MaxFailures)
            {
                _stopped = true;
                Stop();
                HasTemperature = false;
                SnapshotUpdated?.Invoke(null); // null = TMP 숨김 신호
            }
        }
    }

    /// <summary>\Thermal Zone Information\Temperature 의 모든 존을 읽어 최댓값(°C)을 반환.
    /// 가장 뜨거운 존이 CPU 에 가장 가깝고, 최댓값은 결정론적이라 존 간 널뛰기가 없다.
    /// 카운터 객체는 캐시해 재사용한다 — RawValue 읽기는 싸지만 카운터/카테고리 생성은 perflib 조회라 비싸다.
    /// 캐시된 존이 전부 실패하면 캐시를 버려 다음 폴링에서 다시 만든다(존 추가·제거 대응).</summary>
    private float? ReadThermalZone()
    {
        var counters = _zoneCounters ??= CreateCounters();
        if (counters.Length == 0) { ResetCounters(); return null; }

        float max = float.NaN;
        foreach (var counter in counters)
        {
            if (TryReadCounter(counter) is float v && (float.IsNaN(max) || v > max)) max = v;
        }

        if (float.IsNaN(max)) { ResetCounters(); return null; } // 다음 폴링에서 재생성
        return max;
    }

    /// <summary>써멀존 카운터를 한 번 만들어 배열로 돌려준다. 인스턴스를 열거해 만들고, 하나도 못 만들면
    /// 기존과 동일하게 고정 인스턴스명으로 한 번 더 시도한다.
    /// 생성 직후 RawValue 를 한 번 읽어 실재하지 않는 인스턴스는 캐시에 남기지 않는다.</summary>
    private static System.Diagnostics.PerformanceCounter[] CreateCounters()
    {
        string[] instances;
        try
        {
            instances = new System.Diagnostics.PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames();
        }
        catch { instances = System.Array.Empty<string>(); }

        var list = new System.Collections.Generic.List<System.Diagnostics.PerformanceCounter>(instances.Length);
        AddCounters(list, instances);
        if (list.Count == 0)
            AddCounters(list, new[] { "_TZ.TZ00", "_TZ.TZ01", "_tz.tz00", "_tz.tz01", "TZ00", "TZ01", "tz00", "tz01" });
        return list.ToArray();
    }

    private static void AddCounters(
        System.Collections.Generic.List<System.Diagnostics.PerformanceCounter> list, string[] instanceNames)
    {
        foreach (var inst in instanceNames)
        {
            try
            {
                var counter = new System.Diagnostics.PerformanceCounter(
                    "Thermal Zone Information", "Temperature", inst, true);
                _ = counter.RawValue;
                list.Add(counter);
            }
            catch { }
        }
    }

    /// <summary>캐시된 카운터를 모두 정리한다. 다음 ReadThermalZone 이 새로 만든다.</summary>
    private void ResetCounters()
    {
        var counters = _zoneCounters;
        _zoneCounters = null;
        if (counters == null) return;
        foreach (var counter in counters)
        {
            try { counter.Dispose(); } catch { }
        }
    }

    private static float? TryReadCounter(System.Diagnostics.PerformanceCounter counter)
    {
        try
        {
            long raw = counter.RawValue;
            if (raw <= 0) return null;

            float c = (raw - 2732f) / 10f;   // deci-Kelvin
            if (c is > 0 and < 150) return c;
            c = raw - 273.15f;               // Kelvin
            if (c is > 0 and < 150) return c;
        }
        catch { }
        return null;
    }

    public void Dispose() => Stop();
}
