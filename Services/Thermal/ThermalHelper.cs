using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace DevezCode.Services.Thermal;

/// <summary>
/// 관리자 권한 헬프리스 모드(--thermal-helper). LibreHardwareMonitorLib(WinRing0 드라이버)로
/// CPU 코어 온도를 직접 읽어 <see cref="ThermalIpc"/> 파일에 기록한다.
/// 스케줄러(최고 권한 작업)가 이 프로세스를 UAC 없이 띄운다.
/// GUI 하트비트가 끊기면(=앱 종료) 스스로 종료해 좀비 프로세스를 남기지 않는다.
/// </summary>
public static class ThermalHelper
{
    private const string HelperMutex = @"Global\DevezCodeThermalHelper";

    /// <summary>App.OnStartup 에서 --thermal-helper 감지 시 호출. 반환값 = 프로세스 종료 코드.</summary>
    public static int Run()
    {
        // 중복 실행 방지 — 스케줄러가 두 번 트리거돼도 하나만 산다.
        using var mutex = new Mutex(initiallyOwned: true, HelperMutex, out bool first);
        if (!first) return 0;

        Computer? computer = null;
        try
        {
            computer = new Computer { IsCpuEnabled = true };
            computer.Open();

            // 시작 직후 소비자가 아직 첫 하트비트를 못 썼을 수 있으므로 잠깐의 유예를 둔다.
            int graceTicks = 3; // 약 6초

            while (true)
            {
                if (!ThermalIpc.IsConsumerAlive())
                {
                    if (graceTicks-- <= 0) break; // GUI 없음 → 종료
                }
                else graceTicks = 0;

                float? t = ReadCpuTemp(computer);
                if (t is > 0f and < 150f) ThermalIpc.WriteReading(t.Value);

                Thread.Sleep(2000);
            }
        }
        catch { /* 드라이버 로드 실패 등 — 조용히 종료(캡슐레이션: GUI 가 none 으로 캐시) */ }
        finally
        {
            try { computer?.Close(); } catch { }
        }
        return 0;
    }

    /// <summary>CPU 대표 온도. Package/Tctl/Tdie 우선, 없으면 코어 최댓값.</summary>
    private static float? ReadCpuTemp(Computer computer)
    {
        float? pkg = null, coreMax = null;
        foreach (var hw in computer.Hardware)
        {
            if (hw.HardwareType != HardwareType.Cpu) continue;
            hw.Update();
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Temperature || s.Value is not float v) continue;
                var n = s.Name ?? "";
                if (n.Contains("Package") || n.Contains("Tctl") || n.Contains("Tdie"))
                    pkg = v;
                else if (n.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
                    coreMax = coreMax is null ? v : MathF.Max(coreMax.Value, v);
            }
        }
        return pkg ?? coreMax;
    }
}
