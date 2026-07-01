using DevezCode.Services.Thermal;

namespace DevezCode.Services;

/// <summary>
/// CPU 온도 모니터. 능력 캐시(SettingsService.ThermalCapability)에 따라 소스를 고른다.
///   helper : LibreHardwareMonitorLib 관리자 헬퍼(정확) — 파일 IPC 로 값 수신.
///   acpi   : \Thermal Zone Information\Temperature 퍼포먼스 카운터(근사, 관리자 불필요).
///   none   : 판독 불가 → TMP 숨김.
/// unknown(최초): ACPI 사전확인 → 헬퍼 프로브(UAC 1회) → 결과를 캐시.
/// </summary>
public sealed class ThermalMonitorService : IDisposable
{
    private System.Threading.Timer? _poll;
    private bool _stopped;
    private float _smoothedTemp = float.NaN;
    private int _failCount;             // acpi 모드 연속 실패
    private int _staleCount;            // helper 모드 연속 미갱신
    private bool _lastWasNull;

    private const int AcpiMaxFailures = 6;   // 약 18초
    private const int HelperStaleHide = 15;  // 약 30초 무갱신 → 숨김
    private const float HelperSmooth = 0.4f; // 헬퍼는 정확 → 가벼운 스무딩
    private const float AcpiSmooth   = 0.25f;

    /// <summary>snapshot != null: 온도 값, snapshot == null: 중지(숨김) 신호</summary>
    public event Action<Models.ThermalSnapshot?>? SnapshotUpdated;
    public bool HasTemperature { get; private set; }
    public bool Stopped => _stopped;

    public void Start()
    {
        Stop();
        _stopped = false;
        _failCount = 0;
        _staleCount = 0;
        _smoothedTemp = float.NaN;
        _lastWasNull = false;
        // 능력 판별은 ACPI 읽기 + (최초 1회) UAC 프롬프트를 수반하므로 UI 스레드를 막지 않도록 백그라운드에서.
        System.Threading.Tasks.Task.Run(Resolve);
    }

    public void Stop()
    {
        _poll?.Dispose();
        _poll = null;
    }

    // ── 능력 판별(백그라운드) ────────────────────────────────────
    private void Resolve()
    {
        switch (SettingsService.LoadThermalCapability())
        {
            case "none":   Emit(null);      return;
            case "helper": BeginHelper();   return;
            case "acpi":   BeginAcpi();     return;
        }

        // unknown — 최초 판별.
        float? acpi = ReadThermalZone(); // 관리자 불필요(공짜 사전확인)

        // 정확한 헬퍼를 우선 시도(최초 1회 UAC). 실제 신선한 판독까지 확인해야 성공으로 본다.
        bool helperOk = false;
        try
        {
            ThermalIpc.ClearReading(); // 이전 세션의 낡은 값 제거
            ThermalIpc.TouchConsumer(); // 헬퍼가 곧바로 자멸하지 않도록 먼저 살아있음 표시
            helperOk = ThermalHelperLauncher.EnsureRunning() && WaitFirstHelperReading(7000);
        }
        catch { helperOk = false; }

        if (helperOk)
        {
            SettingsService.SaveThermalCapability("helper");
            BeginHelper();
            return;
        }

        if (acpi is > 0f and < 150f)
        {
            SettingsService.SaveThermalCapability("acpi");
            BeginAcpi();
            return;
        }

        SettingsService.SaveThermalCapability("none");
        Emit(null);
    }

    /// <summary>헬퍼가 신선한 첫 온도를 기록할 때까지 대기.</summary>
    private static bool WaitFirstHelperReading(int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            ThermalIpc.TouchConsumer(); // 대기 동안 하트비트 유지 — 헬퍼가 유예 후 자멸하지 않도록
            System.Threading.Thread.Sleep(500);
            waited += 500;
            if (ThermalIpc.TryReadReading(out var c, out var age) && age <= 8 && c is > 0f and < 150f)
                return true;
        }
        return false;
    }

    // ── helper 모드 ──────────────────────────────────────────────
    private void BeginHelper()
    {
        // 헬퍼 프로세스 기동 보장. 이미 등록된 상태라 /run 만 트리거(UAC 없음).
        // (unknown 경로에선 이미 EnsureRunning 했지만 IgnoreNew 정책이라 중복 무해.)
        ThermalIpc.TouchConsumer();
        System.Threading.Tasks.Task.Run(() => { try { ThermalHelperLauncher.EnsureRunning(); } catch { } });
        _poll = new System.Threading.Timer(_ => HelperTick(), null, 0, 2000);
    }

    private void HelperTick()
    {
        if (_stopped) return;
        ThermalIpc.TouchConsumer(); // 하트비트 — 끊기면 헬퍼가 스스로 종료

        if (ThermalIpc.TryReadReading(out var c, out var age) && age <= 10 && c is > 0f and < 150f)
        {
            _staleCount = 0;
            PushTemp(c, HelperSmooth);
            return;
        }

        // 미갱신 — 헬퍼가 아직 안 떴거나 죽음. 일정 시간 지나면 숨김(캐시는 helper 유지 → 다음 실행 때 재시도).
        if (++_staleCount >= HelperStaleHide) Emit(null);
    }

    // ── acpi 모드 (기존 동작 유지) ───────────────────────────────
    private void BeginAcpi()
    {
        _poll = new System.Threading.Timer(_ => AcpiTick(), null, 1000, 3000);
    }

    private void AcpiTick()
    {
        if (_stopped) return;

        float? cpuTemp = null;
        try { cpuTemp = ReadThermalZone(); } catch { }

        if (cpuTemp is > 0f and < 150f)
        {
            _failCount = 0;
            PushTemp(cpuTemp.Value, AcpiSmooth);
        }
        else if (++_failCount >= AcpiMaxFailures)
        {
            _stopped = true;
            Stop();
            Emit(null);
        }
    }

    // ── 공통: EMA 스무딩 후 방출 ─────────────────────────────────
    private void PushTemp(float raw, float factor)
    {
        if (float.IsNaN(_smoothedTemp)) _smoothedTemp = raw;
        else _smoothedTemp = _smoothedTemp * (1f - factor) + raw * factor;

        HasTemperature = true;
        _lastWasNull = false;
        SnapshotUpdated?.Invoke(new Models.ThermalSnapshot { CpuTemperature = MathF.Round(_smoothedTemp) });
    }

    private void Emit(Models.ThermalSnapshot? snap)
    {
        if (snap == null)
        {
            if (_lastWasNull) return; // 숨김 신호 중복 방지
            _lastWasNull = true;
            HasTemperature = false;
        }
        SnapshotUpdated?.Invoke(snap);
    }

    // ── ACPI 써멀존 판독 (관리자 불필요) ─────────────────────────
    /// <summary>\Thermal Zone Information\Temperature 퍼포먼스 카운터를 읽어 CPU 온도(°C) 반환.</summary>
    private static float? ReadThermalZone()
    {
        try
        {
            var category = new System.Diagnostics.PerformanceCounterCategory("Thermal Zone Information");
            foreach (var inst in category.GetInstanceNames())
            {
                var temp = TryReadCounter(inst);
                if (temp != null) return temp;
            }
        }
        catch { }

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
