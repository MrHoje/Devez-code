using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DevezCode.Views;

/// <summary>플러그인 컨트롤러의 borderless 호스트. 설정창과 동일한 Opacity 페이드로 열린다.
/// ESC 로 닫기(마켓플레이스 오버레이가 열려 있으면 그것부터).</summary>
public partial class PluginControlWindow : Window
{
    public PluginControlWindow()
    {
        InitializeComponent();
        PluginView.CloseRequested += (_, _) => Close();
        Opacity = 0;
        ContentRendered += async (_, _) => await AnimateOpenAsync();
    }

    private async Task AnimateOpenAsync()
    {
        // 설정창(SettingsWindow)과 동일 패턴 — 그림자 블러 재계산을 피하려고 창 Opacity 페이드만 준다.
        var dur = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        await Task.Delay(220);
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
