using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DevezCode.Views;

/// <summary>플러그인 컨트롤러의 borderless 호스트. 설정창과 동일한 Opacity 페이드로 열린다.
/// 리사이즈·최대화 가능(크기는 저장하지 않아 재실행 시 기본 크기로 열림). ESC 로 닫기.</summary>
public partial class PluginControlWindow : Window
{
    public PluginControlWindow()
    {
        InitializeComponent();
        PluginView.CloseRequested += (_, _) => Close();
        Opacity = 0;
        // 콘텐츠 Border 의 ClipToBounds 는 사각 경계로만 클립 → 자식 사각 모서리가 라운드 코너 위로 삐져나온다.
        // 설정창(SettingsWindow)처럼 콘텐츠 UserControl 을 둥근 RectangleGeometry 로 직접 클립해야 한다.
        PluginView.SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += (_, _) => ApplyRoundedClip();
        ContentRendered += (_, _) => AnimateOpen();
        StateChanged += (_, _) => ApplyMaximizedChrome();
    }

    private bool _maxed;

    private void ApplyRoundedClip()
    {
        double w = PluginView.ActualWidth, h = PluginView.ActualHeight;
        if (w <= 0 || h <= 0) return;
        PluginView.Clip = new RectangleGeometry(new Rect(0, 0, w, h), _maxed ? 0 : 13, _maxed ? 0 : 13);
    }

    // 최대화/복원 시 그림자 여백·라운드 코너·버튼 글리프를 전환.
    private void ApplyMaximizedChrome()
    {
        _maxed = WindowState == WindowState.Maximized;
        RootLayer.Margin = _maxed ? default : (Thickness)FindResource("WindowShadowOuterMargin");
        ContentChrome.CornerRadius = new CornerRadius(_maxed ? 0 : 14);
        ApplyRoundedClip();
        PluginView.SetMaximizedVisual(_maxed);
    }

    // ── 최대화 크기 보정 (WM_GETMINMAXINFO) ──────────────────────
    // WindowStyle=None + AllowsTransparency 창은 기본 최대화 시 작업영역을 넘쳐 가장자리가 잘리고
    // 작업표시줄을 덮는다. 최대화 크기/위치를 모니터 작업영역에 맞춰 오버플로를 없앤다.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = (HwndSource?)PresentationSource.FromVisual(this);
        src?.AddHook(WndProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x2;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO) { WmGetMinMaxInfo(hwnd, lParam); handled = true; }
        return IntPtr.Zero;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref info))
            {
                var work = info.rcWork; var monitor = info.rcMonitor;
                mmi.ptMaxPosition.X = work.Left - monitor.Left;
                mmi.ptMaxPosition.Y = work.Top - monitor.Top;
                mmi.ptMaxSize.X = work.Right - work.Left;
                mmi.ptMaxSize.Y = work.Bottom - work.Top;
                var dpi = VisualTreeHelper.GetDpi(this);
                mmi.ptMinTrackSize.X = (int)(MinWidth * dpi.DpiScaleX);
                mmi.ptMinTrackSize.Y = (int)(MinHeight * dpi.DpiScaleY);
            }
        }
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void AnimateOpen()
    {
        // AllowsTransparency 레이어드 창은 크기가 클수록(플러그인 창 1240x840) 페이드 매 프레임마다
        // 전체 픽셀 blit + DropShadow(BlurRadius=40) 재계산을 해서 버벅인다.
        // 페이드 동안 콘텐츠 전체를 BitmapCache 로 캐시 → 그림자/텍스트를 한 번만 렌더하고, 매 프레임엔
        // 캐시된 비트맵만 알파 블렌딩한다. 애니메이션이 끝나면 캐시를 풀고(라이브 리사이즈/스크롤을 위해)
        // 그때 첫 목록 로드를 시작한다.
        var dpi = VisualTreeHelper.GetDpi(this);
        RootLayer.CacheMode = new BitmapCache { RenderAtScale = dpi.DpiScaleX };

        var dur = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        anim.Completed += (_, _) =>
        {
            RootLayer.CacheMode = null;
            PluginView.BeginInitialLoad();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            PluginView.TryClose();
            e.Handled = true;
        }
    }
}
