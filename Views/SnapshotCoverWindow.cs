using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>터미널(WebView2 HWND) 위를 덮는 borderless 최상위 창. airspace 우회로
/// 패널 리사이즈 reveal 순간의 xterm reflow 를 가린다(ProjectTargetPickerWindow 와 동일 패턴).
/// WS_EX_NOACTIVATE 로 포커스를 안 가져가고, WS_EX_TRANSPARENT 로 클릭을 통과시킨다.
/// 내용은 중앙 영역을 캡처한 정적 비트맵 한 장 — 잠깐(settle) 떠 있다 닫힌다.</summary>
public sealed class SnapshotCoverWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public SnapshotCoverWindow(ImageSource image)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;     // 보일 때 포커스를 가져가지 않는다.
        Background = Brushes.Black;
        Content = new Image { Source = image, Stretch = Stretch.Fill };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
    }
}
