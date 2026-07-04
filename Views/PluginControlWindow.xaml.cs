using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>플러그인 관리 창. 메인창과 동일하게 WindowChrome 기반 —
/// 네이티브 모서리 리사이즈 + 최대화 애니메이션. 크기는 저장하지 않아 재실행 시 기본 크기로 열림. ESC 로 닫기.</summary>
public partial class PluginControlWindow : Window
{
    private IntPtr _hwnd;

    public PluginControlWindow()
    {
        InitializeComponent();
        PluginView.CloseRequested += (_, _) => Close();
        StateChanged += (_, _) =>
        {
            ApplyMaximizeMargin();
            ApplyCornerPreference();
            PluginView.SetMaximizedVisual(WindowState == WindowState.Maximized);
        };
        ContentRendered += (_, _) => PluginView.BeginInitialLoad();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        EnableDwmTransitions(_hwnd);  // 최대화/복원 시 DWM 부드러운 전환
        ApplyCornerPreference();
        ApplyMaximizeMargin();
    }

    private void MaxRestore() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // WindowStyle=None + WS_THICKFRAME(WindowChrome) 창은 최대화 시 프레임만큼 화면 밖으로 위치해
    // 가장자리가 잘린다 → 최대화일 때만 프레임 두께(DPI 보정)만큼 마진 보정.
    private void ApplyMaximizeMargin()
    {
        if (RootChrome == null) return;
        if (WindowState == WindowState.Maximized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int pad = GetSystemMetrics(SM_CXPADDEDBORDER);
            double x = (GetSystemMetrics(SM_CXFRAME) + pad) / dpi.DpiScaleX;
            double y = (GetSystemMetrics(SM_CYFRAME) + pad) / dpi.DpiScaleY;
            RootChrome.Margin = new Thickness(x, y, x, y);
        }
        else RootChrome.Margin = default;
    }

    // 최대화 시 각진 모서리(둥근 모서리가 화면 모서리를 깎는 문제 방지), 복원 시 둥근 모서리.
    private void ApplyCornerPreference()
    {
        if (_hwnd == IntPtr.Zero) return;
        int pref = WindowState == WindowState.Maximized ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private void EnableDwmTransitions(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style | WS_CAPTION);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { PluginView.TryClose(); e.Handled = true; }
    }

    // 대화상자(PluginControlDialog)의 최대화 버튼이 호출.
    public void ToggleMaximize() => MaxRestore();

    // ── P/Invoke ──────────────────────────────────────────────────
    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int SM_CXFRAME = 32, SM_CYFRAME = 33, SM_CXPADDEDBORDER = 92;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
}
