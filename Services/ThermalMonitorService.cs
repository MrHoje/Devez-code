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
                    IsMotherboardEnabled = false,
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

            foreach (var hardware in _computer!.Hardware)
            {
                // 최신 센서값으로 갱신
                hardware.Update();

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || sensor.Value == null)
                        continue;

                    var temp = (float)sensor.Value;
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        // CPU는 여러 코어 센서 중 최고 온도를 사용
                        if (cpuTemp == null || temp > cpuTemp)
                            cpuTemp = temp;
                    }
                    else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        if (gpuTemp == null || temp > gpuTemp)
                            gpuTemp = temp;
                    }
                }
            }

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
