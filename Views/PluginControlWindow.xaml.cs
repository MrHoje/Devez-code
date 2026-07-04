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
        ContentRendered += (_, _) => AnimateOpen();
    }

    private void ApplyRoundedClip()
    {
        double w = PluginView.ActualWidth, h = PluginView.ActualHeight;
        if (w <= 0 || h <= 0) return;
        PluginView.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 13, 13);
    }

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
