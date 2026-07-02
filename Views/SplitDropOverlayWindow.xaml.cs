using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DevezCode.Views;

/// <summary>탭 드래그 중 분할 대상 반쪽을 미리 보여주는 오버레이. 별도 최상위 창(WS_EX_NOACTIVATE +
/// WS_EX_TRANSPARENT)이라 포커스를 뺏지 않고 마우스도 통과시킨다(캡처는 원본 TabsHost가 계속 들고 있음).
/// ProjectTargetPickerWindow 와 동일한 airspace 우회 패턴.</summary>
public partial class SplitDropOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public SplitDropOverlayWindow()
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
}
