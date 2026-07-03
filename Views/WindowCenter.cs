using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

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
    private const uint SWP_NOZORDER = 0x0004, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    public static void CenterOverOwner(Window child)
    {
        if (child.Owner == null) return;
        var oh = new WindowInteropHelper(child.Owner).Handle;
        var ch = new WindowInteropHelper(child).Handle;
        if (oh == IntPtr.Zero || ch == IntPtr.Zero) return;

        // 크기는 건드리지 않고(SWP_NOSIZE) 위치만 잡는다. 크기를 물리픽셀로 강제하면 PerMonitorV2 에서
        // 배율 다른 모니터로 넘어갈 때 이어지는 WM_DPICHANGED 리스케일과 충돌해 창이 어긋난다.
        // child 의 '현재 실제 물리 크기'(GetWindowRect)를 owner 물리 rect 중앙에 맞추기만 한다.
        void Center()
        {
            if (!GetWindowRect(oh, out var o) || !GetWindowRect(ch, out var c)) return;
            int cw = c.Right - c.Left, chh = c.Bottom - c.Top;
            int x = o.Left + ((o.Right - o.Left) - cw) / 2;
            int y = o.Top  + ((o.Bottom - o.Top) - chh) / 2;
            SetWindowPos(ch, IntPtr.Zero, x, y, 0, 0, SWP_NOZORDER | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        Center();

        // Manual 배치 창은 OS 기본 위치(대개 주 모니터)에 생성됐다가 위 Center() 로 owner(확대 모니터)로
        // 옮겨지면서 WM_DPICHANGED → WPF 가 창을 리스케일한다. 그 새 크기 기준으로 한 번 더 중앙 정렬.
        DpiChangedEventHandler? onDpi = null;
        onDpi = (_, _) => child.Dispatcher.BeginInvoke(new Action(Center), DispatcherPriority.Loaded);
        child.DpiChanged += onDpi;
        // 이후 사용자가 창을 다른 모니터로 끌 때 재중앙되지 않도록, 초기 배치가 끝나면 핸들러를 뗀다.
        child.Dispatcher.BeginInvoke(new Action(() => child.DpiChanged -= onDpi), DispatcherPriority.ApplicationIdle);
    }
}
