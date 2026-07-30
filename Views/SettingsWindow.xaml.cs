using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>설정창 — 메인창(Owner)을 꽉 채우는 화면(Slack 스타일).
/// Owner 의 클라이언트 영역에 정확히 겹치고, Owner 가 이동·리사이즈·최대화되면 함께 따라간다.
/// 콘텐츠는 UserControl(SettingsDialog)에 위임하고, CloseRequested 가 오면 Window 를 닫는다.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SettingsView.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => AttachToOwner();
    }

    private void AttachToOwner()
    {
        if (Owner is not { } owner) return;
        FitToOwner();
        owner.LocationChanged += OwnerBoundsChanged;
        owner.SizeChanged     += OwnerBoundsChanged;
        owner.StateChanged    += OwnerBoundsChanged;
        Closed += (_, _) =>
        {
            owner.LocationChanged -= OwnerBoundsChanged;
            owner.SizeChanged     -= OwnerBoundsChanged;
            owner.StateChanged    -= OwnerBoundsChanged;
        };
    }

    private void OwnerBoundsChanged(object? sender, EventArgs e) => FitToOwner();

    /// <summary>Owner 의 클라이언트 영역(=WindowStyle None 이므로 창 전체)에 좌표·크기를 맞춘다.
    /// Owner 가 최대화·스냅된 경우 Left/Top 은 복원 좌표라 신뢰할 수 없으므로 실제 화면 좌표로 계산한다.</summary>
    private void FitToOwner()
    {
        if (Owner is not { } owner) return;
        if (owner.WindowState == WindowState.Minimized) return;
        if (owner.ActualWidth <= 0 || owner.ActualHeight <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(owner);
        var origin = owner.PointToScreen(new Point(0, 0));   // device px
        Left   = origin.X / dpi.DpiScaleX;
        Top    = origin.Y / dpi.DpiScaleY;
        Width  = owner.ActualWidth;
        Height = owner.ActualHeight;
    }

    /// <summary>헤더 드래그 → 붙어 있는 메인창을 함께 움직인다(설정창은 FitToOwner 로 따라옴).</summary>
    public void DragOwnerWindow()
    {
        if (Owner is { WindowState: WindowState.Normal } owner) owner.DragMove();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SettingsView.TryCloseWithConfirm();
        }
    }
}
