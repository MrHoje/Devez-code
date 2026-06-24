using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace DevezCode.Services;

/// <summary>모니터 열거 + 알림 표시 모니터의 작업영역 조회 (devez MonitorHelper 이식).
/// WPF 앱이라 WinForms(Screen) 의존을 피하고 Win32 EnumDisplayMonitors/GetMonitorInfo 로 구현.
/// 작업영역(WorkAreaPx)은 물리 픽셀 단위 — 위치 계산 시 창 DPI 로 나눠 DIP 로 환산한다.</summary>
public static class MonitorHelper
{
    public record MonitorInfo(string DeviceName, string DisplayName, Rect WorkAreaPx, bool IsPrimary);

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
                result.Add(new MonitorInfo(mi.szDevice, label, work, primary));
                if (!primary) index++;
            }
            return true;
        }, IntPtr.Zero);

        // 주 모니터를 맨 앞으로 정렬(콤보 기본 선택 일관성).
        result.Sort((a, b) => b.IsPrimary.CompareTo(a.IsPrimary));
        return result;
    }

    /// <summary>설정에 저장된 모니터(없으면 주 모니터)의 작업영역(물리 픽셀). 모니터가 없으면 1920×1080 폴백.</summary>
    public static Rect GetNotificationWorkArea()
    {
        var monitors = GetAllMonitors();
        var device = SettingsService.LoadNotifyMonitorDevice();
        if (!string.IsNullOrEmpty(device))
        {
            var m = monitors.Find(x => x.DeviceName == device);
            if (m != null) return m.WorkAreaPx;
        }
        var primary = monitors.Find(x => x.IsPrimary) ?? (monitors.Count > 0 ? monitors[0] : null);
        return primary?.WorkAreaPx ?? new Rect(0, 0, 1920, 1080);
    }

    private const int MONITORINFOF_PRIMARY = 0x1;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

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
