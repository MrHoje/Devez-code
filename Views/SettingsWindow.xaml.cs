using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DevezCode.Views;

/// <summary>설정창 (devez SettingsWindow 이식).
/// 테두리/그림자는 Window 가 담당하고, 실제 콘텐츠는 UserControl(SettingsDialog)에 위임한다.
/// UserControl 의 CloseRequested 가 오면 Window 를 닫는다.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SettingsView.CloseRequested += (_, _) => Close();
        Opacity = 0;
        SettingsView.SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += (_, _) => ApplyRoundedClip();
        ContentRendered += async (_, _) => await AnimateOpenAsync();
    }

    private void ApplyRoundedClip()
    {
        double w = SettingsView.ActualWidth, h = SettingsView.ActualHeight;
        if (w <= 0 || h <= 0) return;
        SettingsView.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 13, 13);
    }

    private async Task AnimateOpenAsync()
    {
        // scale 은 콘텐츠 레이어에만 적용 — 그림자(DropShadowEffect) 레이어까지 스케일하면
        // 매 프레임 블러 재계산으로 끊긴다. 그림자는 창 Opacity 페이드만 따라간다.
        // (devez SettingsWindow 와 동일 패턴)
        var dur  = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        await Task.Delay(220);
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
