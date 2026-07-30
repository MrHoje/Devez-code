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
        Loaded += (_, _) =>
        {
            FitHeightToOwner();
            ApplyRoundedClip();
        };
        ContentRendered += (_, _) => AnimateOpen();
    }

    private void FitHeightToOwner()
    {
        if (Owner?.ActualHeight is not > 0) return;
        Height = Math.Min(Height, Math.Max(360, Owner.ActualHeight - 32));
    }

    private void ApplyRoundedClip()
    {
        double w = SettingsView.ActualWidth, h = SettingsView.ActualHeight;
        if (w <= 0 || h <= 0) return;
        SettingsView.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 13, 13);
    }

    private void AnimateOpen()
    {
        // 페이드 동안 콘텐츠 전체를 BitmapCache 로 캐시 → 매 프레임 DropShadow 블러 재계산을 없앤다
        // (플러그인 팝업과 동일 패턴). 완료 시 캐시 해제.
        var dpi = VisualTreeHelper.GetDpi(this);
        RootLayer.CacheMode = new BitmapCache { RenderAtScale = dpi.DpiScaleX };

        var dur  = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        anim.Completed += (_, _) => RootLayer.CacheMode = null;
        BeginAnimation(OpacityProperty, anim);
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
