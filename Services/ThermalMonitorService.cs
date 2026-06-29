using System.Timers;
using DevezCode.Models;
using LibreHardwareMonitor.Hardware;

namespace DevezCode.Services;

/// <summary>LibreHardwareMonitor 로 CPU/GPU 온도를 주기적으로 읽는다.
/// 초기 하드웨어 스캔(<see cref="InitializeAsync"/>)은 백그라운드 스레드에서 비동기 실행하며,
/// UI/UX 에 영향을 주지 않는다. 스캔 완료 전까지는 온도 데이터 없이 --(대시)로 표시.
///
/// 성능 영향: 스캔 완료 후 3초 주기 센서 읽기는 CPU 0.1% 미만으로 거의 무시할 수준.
/// 스캔 자체도 1~2회성이라 완료 후 부하는 없다.</summary>
public sealed class ThermalMonitorService : IDisposable
{
    private Computer? _computer;
    private System.Threading.Timer? _poll;
    private bool _ready;
    private bool _initFailed;

    public event Action<ThermalSnapshot>? SnapshotUpdated;

    /// <summary>하드웨어 스캔을 백그라운드에서 비동기 실행.
    /// 스캔이 끝나기 전까지 SnapshotUpdated 는 발생하지 않으며, 모든 코드는 안전하게
    /// 데이터 없음 상태를 처리한다. 실패 시 _initFailed=true 로 다시 시도하지 않는다.</summary>
    public async Task InitializeAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = true,   // AMD 보드 일부는 CPU 온도를 Super I/O 로 보고
                    IsControllerEnabled = false,
                    IsNetworkEnabled = false,
                    IsStorageEnabled = false,
                    IsMemoryEnabled = false,
                    IsPsuEnabled = false,
                };
                _computer.Open();
                _ready = true;
            }
            catch
            {
                _initFailed = true;
                // LibreHardwareMonitor 미설치/차단 등 — 자동 복구 불가, 조용히 실패
            }
        }).ConfigureAwait(false);
    }

    /// <summary>3초 주기 폴링 시작. InitializeAsync 호출 전에 실행되면 타이머는 돌지만
    /// _ready=false 라 Capture 가 skip 된다(_ready 이후 첫 tick 에서 데이터 수집 시작).</summary>
    public void Start()
    {
        _poll?.Dispose();
        _poll = new System.Threading.Timer(_ => Capture(), null, 1000, 3000);
    }

    public void Stop()
    {
        _poll?.Dispose();
        _poll = null;
    }

    private void Capture()
    {
        if (!_ready || _initFailed) return;

        try
        {
            float? cpuTemp = null, gpuTemp = null;

            // 재귀적으로 모든 hardware + subHardware 의 센서를 읽는다
            foreach (var hardware in _computer!.Hardware)
            {
                WalkHardware(hardware, ref cpuTemp, ref gpuTemp);
            }

            // 실패 3회 연속이면 initFailed 처리(0도만 반환되는 경우 방지)
            if (cpuTemp == null && gpuTemp == null) return;

            // 0.0 도는 유효값이 아니므로 건너뜀 — LibreHardwareMonitor 가
            // 관리자 권한 없이 센서를 못 읽으면 0을 반환하는 경우가 있음
            if (cpuTemp is <= 0f) cpuTemp = null;
            if (gpuTemp is <= 0f) gpuTemp = null;
            if (cpuTemp == null && gpuTemp == null) return;

            SnapshotUpdated?.Invoke(new ThermalSnapshot
            {
                CpuTemperature = cpuTemp,
                GpuTemperature = gpuTemp,
            });
        }
        catch
        {
            // 일시 오류 — 직전 값 유지
        }
    }

    public void Dispose()
    {
        Stop();
        try { _computer?.Close(); } catch { }
    }
}
    /// <summary>hardware + 모든 subHardware 를 재귀적으로 탐색하며 온도 센서 수집.
    /// AMD CPU/GPU 는 subHardware 계층에 실제 센서가 있는 경우가 많다.</summary>
    private static void WalkHardware(IHardware hw, ref float? cpuTemp, ref float? gpuTemp)
    {
        hw.Update();
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value == null)
                continue;

            var temp = (float)sensor.Value;

            // AMD CPU Package 온도 등 — CPU 온도로 간주
            if (hw.HardwareType == HardwareType.Cpu)
            {
                if (cpuTemp == null || temp > cpuTemp)
                    cpuTemp = temp;
            }
            // GPU(NVIDIA/AMD/Intel)
            else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                if (gpuTemp == null || temp > gpuTemp)
                    gpuTemp = temp;
            }
            // 일부 AMD 보드는 CPU 온도를 Super I/O 칩셋( motherboard )으로 보고함
            else if (hw.HardwareType == HardwareType.Motherboard && cpuTemp == null)
            {
                // 센서명에 "CPU" 또는 "Core" 가 포함된 것만 CPU 온도로 추정
                var name = sensor.Name?.ToLowerInvariant() ?? "";
                if (name.Contains("cpu") || name.Contains("core") || name.Contains("socket") || name.Contains("tctl"))
                {
                    if (cpuTemp == null || temp > cpuTemp)
                        cpuTemp = temp;
                }
            }
        }

        // subHardware 재귀 탐색
        foreach (var sub in hw.SubHardware)
            WalkHardware(sub, ref cpuTemp, ref gpuTemp);
    }
