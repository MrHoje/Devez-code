using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>설정 화면 — 메인창(Owner)의 <b>상단바 아래</b>를 꽉 채우는 창.
/// 상단바(앱 이름·패널 토글·최소화/최대화/닫기)는 메인창의 것을 그대로 노출해 계속 동작하고,
/// 이 창은 그 아래 본문 영역만 덮는다. 모달이 아니어서(Show) 상단바 조작이 막히지 않는다.
/// Owner 가 이동·리사이즈·최대화되면 함께 따라가며, 콘텐츠는 UserControl(SettingsDialog)에 위임한다.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SettingsView.CloseRequested += (_, _) => Close();
        // 크기·위치 확정은 OnSourceInitialized(창이 화면에 나오기 전)에서 한다.
        // Loaded 에서 하면 XAML 의 초기 크기로 한 프레임 그려진 뒤 줄어드는 게 눈에 보인다.
        Loaded += (_, _) => FitToOwner();   // 레이아웃 확정 후 한 번 더 보정
    }

    private FrameworkElement? _ownerChrome;   // Owner 의 시각적 루트(최대화 프레임 보정이 반영된 요소)

    private void AttachToOwner()
    {
        if (Owner is not { } owner) return;
        _ownerChrome = (owner as MainWindow)?.ChromeRootElement;
        FitToOwner();
        ApplyCornerPreference();
        owner.LocationChanged += OwnerBoundsChanged;
        owner.SizeChanged     += OwnerBoundsChanged;
        owner.StateChanged    += OwnerBoundsChanged;
        // 최대화 보정 마진은 Owner.SizeChanged 이후에 적용되므로 이 요소의 크기 변화도 따라간다.
        if (_ownerChrome != null) _ownerChrome.SizeChanged += OwnerBoundsChanged;
        Closed += (_, _) =>
        {
            owner.LocationChanged -= OwnerBoundsChanged;
            owner.SizeChanged     -= OwnerBoundsChanged;
            owner.StateChanged    -= OwnerBoundsChanged;
            if (_ownerChrome != null) _ownerChrome.SizeChanged -= OwnerBoundsChanged;
        };
    }

    private void OwnerBoundsChanged(object? sender, EventArgs e)
    {
        FitToOwner();
        ApplyCornerPreference();
    }

    /// <summary>메인창의 상단바 아래 영역에 좌표·크기를 맞춘다(상단바는 그대로 노출).
    /// 최대화·스냅 시 창 rect 는 프레임만큼 화면 밖이라 신뢰할 수 없으므로 MainWindow 가 계산한
    /// 화면 좌표(SettingsHostRectDip)를 그대로 쓴다.</summary>
    private void FitToOwner()
    {
        if (Owner is not { } owner) return;
        if (owner.WindowState == WindowState.Minimized) return;

        if (owner is MainWindow main)
        {
            var r = main.SettingsHostRectDip();
            if (r.Width <= 0 || r.Height <= 0) return;
            Left = r.X; Top = r.Y; Width = r.Width; Height = r.Height;
            return;
        }

        // MainWindow 가 아닌 경우(테스트 등)의 보수적 폴백 — 창 클라이언트 전체.
        if (owner.ActualWidth <= 0 || owner.ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(owner);
        var origin = owner.PointToScreen(new Point(0, 0));
        Left   = origin.X / dpi.DpiScaleX;
        Top    = origin.Y / dpi.DpiScaleY;
        Width  = owner.ActualWidth;
        Height = owner.ActualHeight;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SettingsView.TryCloseWithConfirm();
        }
    }

    // ── Win11 둥근 모서리 ──────────────────────────────────────────────
    // borderless(WindowStyle=None) 창은 DWM 이 기본값으로는 모서리를 깎지 않는다. 메인창 본문 영역을
    // 덮으므로 각진 채로 두면 메인창의 둥근 아래 모서리가 사라져 보인다.
    // 메인창과 같은 규칙: 일반 상태는 ROUND, 최대화/전체화면은 DONOTROUND.

    private IntPtr _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // 창이 화면에 나오기 전 시점 — 여기서 Owner 에 맞춰야 첫 프레임부터 제 크기로 뜬다.
        AttachToOwner();
    }

    private void ApplyCornerPreference()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool square = (Owner as MainWindow)?.IsSquareCornerState
                   ?? Owner?.WindowState == WindowState.Maximized;
        int pref = square ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
}
