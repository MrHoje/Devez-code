using System.Timers;
using System.Management;
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
    private bool _ready, _initFailed;

    public event Action<ThermalSnapshot>? SnapshotUpdated;
    /// <summary>한 번이라도 온도 센서 값을 읽었는지 (자동 설치 시도 여부 판단용).</summary>
    public bool HasTemperature { get; private set; }
    /// <summary>초기화 실패 여부 (Computer.Open() 예외 발생 등).</summary>
    public bool InitFailed => _initFailed;
    /// <summary>LibreHardwareMonitor 가 감지한 Hardware 수.</summary>
    public int HardwareCount { get; private set; }
    /// <summary>온도 센서를 가진 Hardware 수.</summary>
    public int TempSensorCount { get; private set; }

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
                    IsMotherboardEnabled = true,
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
            }
        }).ConfigureAwait(false);
    }

    public void Start()
    {
        _poll?.Dispose();
        _poll = new System.Threading.Timer(_ => Capture(), null, 1000, 3000);
    }

    public void Reopen()
    {
        _ready = false;
        var old = _computer;
        try
        {
            var c = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
            };
            c.Open();
            try { old?.Close(); } catch { }
            _computer = c;
            _initFailed = false;
            _ready = true;
        }
        catch
        {
            _computer = old;
            _ready = old != null;
        }
    }

    public void Stop()
    {
        _poll?.Dispose();
        _poll = null;
    }

    private void Capture()
    {
        try
        {
            float? cpuTemp = null, gpuTemp = null;

            // 1) LibreHardwareMonitor
            if (_ready && _computer != null)
            {
                int hwCount = 0, tempCount = 0;
                foreach (var hardware in _computer.Hardware)
                {
                    hwCount++;
                    WalkHardware(hardware, ref cpuTemp, ref gpuTemp, ref tempCount);
                }
                HardwareCount = hwCount;
                TempSensorCount = tempCount;
            }

            // 2) WMI 폴백 (LibreHW가 유효 온도 못 찾았을 때)
            if (cpuTemp == null || cpuTemp <= 0f)
            {
                var wmiTemp = CaptureWmi();
                if (wmiTemp > 0f) cpuTemp = wmiTemp;
            }

            if (gpuTemp <= 0f) gpuTemp = null;
            if (cpuTemp > 0f || (gpuTemp.HasValue && gpuTemp > 0f)) HasTemperature = true;

            SnapshotUpdated?.Invoke(new ThermalSnapshot
            {
                CpuTemperature = cpuTemp,
                GpuTemperature = gpuTemp,
            });
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _computer?.Close(); } catch { }
    }

    private static void WalkHardware(IHardware hw, ref float? cpuTemp, ref float? gpuTemp, ref int tempCount)
    {
        hw.Update();
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value == null)
                continue;

            tempCount++;
            var temp = (float)sensor.Value;

            if (hw.HardwareType == HardwareType.Cpu)
            {
                if (cpuTemp == null || temp > cpuTemp) cpuTemp = temp;
            }
            else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                if (gpuTemp == null || temp > gpuTemp) gpuTemp = temp;
            }
            else if (hw.HardwareType == HardwareType.Motherboard && cpuTemp == null)
            {
                var name = sensor.Name?.ToLowerInvariant() ?? "";
                if (name.Contains("cpu") || name.Contains("core") || name.Contains("socket") || name.Contains("tctl"))
                {
                    if (cpuTemp == null || temp > cpuTemp) cpuTemp = temp;
                }
            }
        }

        foreach (var sub in hw.SubHardware)
            WalkHardware(sub, ref cpuTemp, ref gpuTemp, ref tempCount);
    }

    /// <summary>WMI MSAcpi_ThermalZoneTemperature 폴백. 온도(°C) 반환, 실패 시 -1.</summary>
    private static float CaptureWmi()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
            foreach (var o in s.Get())
            {
                var raw = o["Temperature"] as uint?;
                if (raw != null)
                {
                    var celsius = (raw.Value - 2732f) / 10f;
                    if (celsius > 0 && celsius < 150) return celsius;
                }
            }
        }
        catch { }
        return -1f;
    }
}
