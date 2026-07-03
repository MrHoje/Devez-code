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
        (int x, int y)? Target()
        {
            if (!GetWindowRect(oh, out var o) || !GetWindowRect(ch, out var c)) return null;
            int cw = c.Right - c.Left, chh = c.Bottom - c.Top;
            return (o.Left + ((o.Right - o.Left) - cw) / 2,
                    o.Top  + ((o.Bottom - o.Top) - chh) / 2);
        }

        void Move(int x, int y) => SetWindowPos(ch, IntPtr.Zero, x, y, 0, 0, SWP_NOZORDER | SWP_NOSIZE | SWP_NOACTIVATE);

        int lastX = int.MinValue, lastY = int.MinValue;
        if (Target() is (int ix, int iy)) { lastX = ix; lastY = iy; Move(ix, iy); } // 즉시 1회

        // 전체화면(배율 A 모니터)+팝업이 주모니터(배율 B)에 먼저 생성되는 경우: 위 Move 로 owner 모니터로
        // 옮기는 순간 뒤늦게 WM_DPICHANGED 가 와서 WPF 가 리스케일/재배치 → 한 번의 중앙정렬만으론 어긋난 채
        // 고정된다. SizeChanged/DpiChanged 훅+제거는 그 '제거 타이밍'이 늦게 오는 DPICHANGED 와 경쟁해 놓친다.
        // 대신 짧게 폴링하며 목표 좌표가 '안정될 때까지' 재중앙 → 늦게 오는 리스케일도 무조건 잡고, 안정되면 조기 중단.
        int stable = 0, ticks = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Loaded, child.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (Target() is (int x, int y))
            {
                if (x == lastX && y == lastY) stable++;
                else { stable = 0; lastX = x; lastY = y; Move(x, y); }
            }
            // 2틱 연속 동일(리스케일/이동 정착) 또는 최대 ~1s 후 중단 — 이후 사용자 드래그 이동은 존중.
            if (stable >= 2 || ++ticks >= 10) timer.Stop();
        };
        timer.Start();
    }
}
