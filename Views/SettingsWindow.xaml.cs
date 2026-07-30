using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>설정창 — 메인창(Owner)을 꽉 채우는 화면(Slack 스타일).
/// Owner 의 시각적 콘텐츠 영역에 정확히 겹치고, Owner 가 이동·리사이즈·최대화되면 함께 따라간다.
/// 콘텐츠는 UserControl(SettingsDialog)에 위임하고, CloseRequested 가 오면 Window 를 닫는다.</summary>
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
        // 최대화 시 Owner 창 rect 는 프레임만큼 화면 밖으로 나가고 이 요소의 마진으로 보정된다.
        // 창 rect 를 그대로 쓰면 설정창이 화면 밖으로 삐져나가 모서리가 잘려 보인다.
        _ownerChrome = (owner as MainWindow)?.ChromeRootElement;
        FitToOwner();
        SyncOwnerVisuals();
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
        SyncOwnerVisuals();
    }

    private void SyncOwnerVisuals()
    {
        if (Owner is not { } owner) return;
        SettingsView.SyncOwnerMaximizeIcon(owner.WindowState == WindowState.Maximized);
        ApplyCornerPreference();
    }

    // ── 설정창이 재현한 타이틀바의 창 컨트롤 → 덮고 있는 메인창을 실제로 제어 ──

    /// <summary>메인창 최소화. owned window 인 설정창은 함께 숨고, 복원 시 다시 나타난다.
    /// 이 창이 모달이라 Owner 는 비활성 상태 — WindowState 대입은 활성 모달에 되돌려질 수 있으므로
    /// Win32 ShowWindow 로 직접 최소화한다.</summary>
    public void MinimizeOwner()
    {
        if (Owner is not { } owner) return;
        var h = new WindowInteropHelper(owner).Handle;
        if (h != IntPtr.Zero) ShowWindow(h, SW_MINIMIZE);
        else owner.WindowState = WindowState.Minimized;
    }

    /// <summary>Owner 가 최대화/전체화면 상태인지 — 이 상태에서는 헤더 드래그로 이동하지 않는다.</summary>
    public bool IsOwnerMaximized
        => (Owner as MainWindow)?.IsSquareCornerState
        ?? Owner?.WindowState == WindowState.Maximized;

    /// <summary>헤더 드래그 — 비활성(모달 Owner)에는 DragMove 가 먹지 않으므로 좌표를 직접 옮긴다.
    /// 이동량은 device px 로 받아 DIP 로 환산한다. 설정창은 FitToOwner 로 따라온다.</summary>
    public void MoveOwnerBy(double dxDevice, double dyDevice)
    {
        if (Owner is not { WindowState: WindowState.Normal } owner) return;
        if (IsOwnerMaximized) return;   // 전체화면은 WindowState=Normal 을 유지하므로 별도 차단
        var dpi = VisualTreeHelper.GetDpi(owner);
        owner.Left += dxDevice / dpi.DpiScaleX;
        owner.Top  += dyDevice / dpi.DpiScaleY;
        FitToOwner();
    }

    /// <summary>메인창 최대화/복원. 전체화면 설정까지 반영되도록 MainWindow 의 공통 토글을 쓴다.</summary>
    public void ToggleMaximizeOwner()
    {
        if (Owner is MainWindow main) main.ToggleMaximizeFromChild();
        else if (Owner is { } owner)
            owner.WindowState = owner.WindowState == WindowState.Maximized
                              ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>앱 닫기. 모달인 설정창을 먼저 정리(대기 중 저장 확정)하고, 모달 루프가 끝난 뒤
    /// 메인창을 닫는다(모달이 살아 있는 동안 Owner.Close() 는 처리되지 않는다).</summary>
    public void CloseOwner()
    {
        var owner = Owner;
        Closed += (_, _) => owner?.Close();
        SettingsView.CloseForAppExit();              // CloseRequested → 이 창 Close()
    }

    /// <summary>Owner 의 시각적 콘텐츠 영역(RootChrome)에 좌표·크기를 맞춘다.
    /// Owner 가 최대화·스냅된 경우 Left/Top 은 복원 좌표라 신뢰할 수 없으므로 실제 화면 좌표로 계산하고,
    /// 최대화 프레임 보정 마진까지 반영되도록 창 rect 대신 그 요소를 기준으로 삼는다.</summary>
    private void FitToOwner()
    {
        if (Owner is not { } owner) return;
        if (owner.WindowState == WindowState.Minimized) return;

        // 기준 요소: MainWindow.RootChrome(없으면 창 자체).
        FrameworkElement anchor = _ownerChrome is { ActualWidth: > 0, ActualHeight: > 0 } c ? c : owner;
        if (anchor.ActualWidth <= 0 || anchor.ActualHeight <= 0) return;
        if (!anchor.IsVisible) return;

        var dpi = VisualTreeHelper.GetDpi(owner);
        var origin = anchor.PointToScreen(new Point(0, 0));   // device px
        Left   = origin.X / dpi.DpiScaleX;
        Top    = origin.Y / dpi.DpiScaleY;
        Width  = anchor.ActualWidth;
        Height = anchor.ActualHeight;
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
    // 이 창은 borderless(WindowStyle=None)라 DWM 이 기본값으로는 모서리를 깎지 않는다.
    // 메인창을 그대로 덮으므로 각진 채로 두면 메인창의 둥근 모서리가 사라져 보인다.
    // 메인창과 같은 규칙으로 맞춘다: 일반 상태는 ROUND, 최대화/전체화면은 DONOTROUND.

    private IntPtr _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // 창이 화면에 나오기 전 시점 — 여기서 Owner 에 맞춰야 첫 프레임부터 제 크기로 뜬다.
        AttachToOwner();
        ApplyCornerPreference();
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
    private const int SW_MINIMIZE = 6;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
