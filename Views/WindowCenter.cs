using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DevezCode.Views;

/// <summary>자식 창을 Owner 중앙에 배치한다. WPF DIP 좌표는 per-monitor DPI 에서
/// 창마다 스케일이 달라 섞으면 어긋난다(특히 가짜 전체화면 + 보조 모니터 배율≠100%).
/// 디바이스 픽셀로 직접 계산·배치해 DPI/배율과 무관하게 정확히 중앙에 둔다.
/// 호출 시점: 자식 창 Loaded(HWND·크기 확정 후).</summary>
internal static class WindowCenter
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    public static void CenterOverOwner(Window child)
    {
        if (child.Owner == null) return;
        var oh = new WindowInteropHelper(child.Owner).Handle;
        var ch = new WindowInteropHelper(child).Handle;
        if (oh == IntPtr.Zero || ch == IntPtr.Zero) return;
        if (!GetWindowRect(oh, out var o) || !GetWindowRect(ch, out var c)) return;

        int cw = c.Right - c.Left, chh = c.Bottom - c.Top;
        int x = o.Left + ((o.Right - o.Left) - cw) / 2;
        int y = o.Top + ((o.Bottom - o.Top) - chh) / 2;
        SetWindowPos(ch, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
