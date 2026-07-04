using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
        // 콘텐츠 Border 의 ClipToBounds 는 사각 경계로만 클립 → 자식 사각 모서리가 라운드 코너 위로 삐져나온다.
        // 설정창(SettingsWindow)처럼 콘텐츠 UserControl 을 둥근 RectangleGeometry 로 직접 클립해야 한다.
        PluginView.SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += (_, _) => ApplyRoundedClip();
        ContentRendered += async (_, _) => await AnimateOpenAsync();
    }

    private void ApplyRoundedClip()
    {
        double w = PluginView.ActualWidth, h = PluginView.ActualHeight;
        if (w <= 0 || h <= 0) return;
        PluginView.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 13, 13);
    }

    private async Task AnimateOpenAsync()
    {
        // 설정창(SettingsWindow)과 동일 패턴 — 그림자 블러 재계산을 피하려고 창 Opacity 페이드만 준다.
        var dur = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        await Task.Delay(220);
        // 페이드가 끝난 뒤에 첫 목록 로드(서브프로세스 실행) — 애니메이션 프레임과 겹치지 않게.
        PluginView.BeginInitialLoad();
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
