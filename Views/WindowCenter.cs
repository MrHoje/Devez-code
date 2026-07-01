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
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    public static void CenterOverOwner(Window child)
    {
        if (child.Owner == null) return;
        var oh = new WindowInteropHelper(child.Owner).Handle;
        var ch = new WindowInteropHelper(child).Handle;
        if (oh == IntPtr.Zero || ch == IntPtr.Zero) return;
        if (!GetWindowRect(oh, out var o)) return;

        // 자식이 owner 와 다른 배율 모니터에서 생성됐다가 owner(전체화면·확대 모니터)로 이동되면
        // Loaded 시점 GetWindowRect(child) 크기가 아직 옛 배율(예: 100%→150% 이동 전 900px)이라
        // 그 값으로 센터링하면 실제 리스케일된 크기(1350px)와 어긋나 팝업이 중앙을 벗어난다.
        // owner 모니터 배율로 자식 물리 크기를 직접 산출하고 위치+크기를 함께 지정(SWP_NOSIZE 제거)해
        // 생성/이동 타이밍과 무관하게 정확히 중앙에 둔다.
        uint dpi = GetDpiForWindow(oh);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        double dipW = child.ActualWidth  > 0 ? child.ActualWidth  : child.Width;
        double dipH = child.ActualHeight > 0 ? child.ActualHeight : child.Height;
        if (double.IsNaN(dipW) || double.IsNaN(dipH) || dipW <= 0 || dipH <= 0)
        {
            // 크기를 못 구하면 위치만이라도(기존 동작) — 자식 실제 rect 로 폴백
            if (!GetWindowRect(ch, out var cr)) return;
            dipW = (cr.Right - cr.Left) / scale;
            dipH = (cr.Bottom - cr.Top) / scale;
        }
        int cw  = (int)Math.Round(dipW * scale);
        int chh = (int)Math.Round(dipH * scale);
        int x = o.Left + ((o.Right - o.Left) - cw) / 2;
        int y = o.Top  + ((o.Bottom - o.Top) - chh) / 2;
        SetWindowPos(ch, IntPtr.Zero, x, y, cw, chh, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
