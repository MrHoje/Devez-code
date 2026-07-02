using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>탭 드래그 고스트가 WebView2 콘텐츠 영역 위로 넘어갈 때 대신 보여줄 최상위 창.
/// SplitDropOverlayWindow 와 동일한 airspace 우회 패턴(WS_EX_NOACTIVATE + WS_EX_TRANSPARENT).</summary>
public partial class DragGhostWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public DragGhostWindow()
    {
        InitializeComponent();
        ShowActivated = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
    }

    public void SetSnapshot(Brush brush, Size size)
    {
        Snapshot.Background = brush;
        Width = size.Width;
        Height = size.Height;
    }
}
