using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace DevezCode.Services;

/// <summary>모니터 열거 + 알림 표시 모니터의 작업영역/배율 조회 (devez MonitorHelper 이식).
/// WPF 앱이라 WinForms(Screen) 의존을 피하고 Win32 EnumDisplayMonitors/GetMonitorInfo 로 구현.
/// PerMonitorV2 환경에서 모니터마다 DPI 가 다르므로 ScaleX/ScaleY 도 함께 반환한다.
/// 작업영역(WorkAreaPx)은 물리 픽셀 단위 — 알림 위치는 물리 픽셀로 계산해 SetWindowPos 로 배치한다.</summary>
public static class MonitorHelper
{
    public record MonitorInfo(string DeviceName, string DisplayName, Rect WorkAreaPx, bool IsPrimary, double ScaleX, double ScaleY);

    public static List<MonitorInfo> GetAllMonitors()
    {
        var result = new List<MonitorInfo>();
        int index = 1;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                bool primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                var label = primary ? $"주 모니터 ({w}×{h})" : $"모니터 {index} ({w}×{h})";
                var work = new Rect(mi.rcWork.Left, mi.rcWork.Top,
                                    mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
                GetScale(hMon, out double sx, out double sy);
                result.Add(new MonitorInfo(mi.szDevice, label, work, primary, sx, sy));
                if (!primary) index++;
            }
            return true;
        }, IntPtr.Zero);

        result.Sort((a, b) => b.IsPrimary.CompareTo(a.IsPrimary));
        return result;
    }

    /// <summary>설정에 저장된 모니터(없으면 주 모니터)의 작업영역(물리 픽셀)과 DPI 배율. 없으면 1920×1080 / 1배.</summary>
    public static (Rect WorkAreaPx, double ScaleX, double ScaleY) GetNotificationTarget()
    {
        var monitors = GetAllMonitors();
        var device = SettingsService.LoadNotifyMonitorDevice();
        MonitorInfo? m = null;
        if (!string.IsNullOrEmpty(device)) m = monitors.Find(x => x.DeviceName == device);
        m ??= monitors.Find(x => x.IsPrimary) ?? (monitors.Count > 0 ? monitors[0] : null);
        if (m == null) return (new Rect(0, 0, 1920, 1080), 1, 1);
        return (m.WorkAreaPx, m.ScaleX, m.ScaleY);
    }

    private static void GetScale(IntPtr hMon, out double sx, out double sy)
    {
        try
        {
            if (GetDpiForMonitor(hMon, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out uint dy) == 0 && dx > 0)
            {
                sx = dx / 96.0; sy = dy / 96.0; return;
            }
        }
        catch { /* Shcore 미지원 — 1배 폴백 */ }
        sx = sy = 1.0;
    }

    private const int MONITORINFOF_PRIMARY = 0x1;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
}
