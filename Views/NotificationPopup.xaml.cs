using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>우측/좌측 모서리 스택형 토스트 알림 (devez NotificationPopup 이식, 로컬 단독 버전).
/// 표시 위치(4모서리)는 설정값을 따르고, 여러 알림은 코너에서 바깥쪽으로 쌓인다.
/// 자기 자신을 정적 목록으로 관리해 추가/닫힘 시 전체를 재배치한다.</summary>
public partial class NotificationPopup : Window
{
    private static readonly List<NotificationPopup> _active = new();
    // 모서리 여백 — 기존 12에서 절반으로(요청). 알림 간 간격은 유지.
    private const double Margin = 6, Gap = 8;

    private readonly Action? _onClick;
    private DispatcherTimer? _autoClose;
    private bool _closing;

    public NotificationPopup(string title, string? content, Action? onClick = null)
    {
        InitializeComponent();
        _onClick = onClick;
        TitleText.Text = title;
        if (!string.IsNullOrEmpty(content))
        {
            ContentText.Text = content;
            ContentText.Visibility = Visibility.Visible;
        }

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _active.Add(this);
        UpdateLayout();
        LayoutAll(animateNew: false);

        // 페이드 인
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200))));

        // 자동 닫힘: 설정값(초). 0=영구 → 타이머 없음(닫기 버튼/클릭으로만 닫힘).
        var sec = SettingsService.LoadNotifyAutoCloseSeconds();
        if (sec > 0)
        {
            _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
            _autoClose.Tick += (_, _) => CloseWithAnimation();
            _autoClose.Start();
        }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        if (_active.Remove(this)) LayoutAll(animateNew: true);
    }

    /// <summary>전체 활성 알림을 현재 설정 위치 기준으로 코너에서 바깥쪽으로 재배치한다.
    /// 최신 알림이 코너에 가장 가깝게 위치한다.</summary>
    private static void LayoutAll(bool animateNew)
    {
        var pos = SettingsService.LoadNotifyPosition();
        bool right  = pos is "br" or "tr";
        bool bottom = pos is "br" or "bl";
        // 선택 모니터 작업영역(물리 픽셀). 창 DPI 로 나눠 DIP 로 환산해 위치 계산.
        var waPx = MonitorHelper.GetNotificationWorkArea();

        double offset = 0;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var p = _active[i];
            p.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(p);
            double waLeft   = waPx.Left   / dpi.DpiScaleX;
            double waRight  = waPx.Right  / dpi.DpiScaleX;
            double waTop    = waPx.Top    / dpi.DpiScaleY;
            double waBottom = waPx.Bottom / dpi.DpiScaleY;
            double w = p.ActualWidth > 0 ? p.ActualWidth : p.Width;
            double h = p.ActualHeight;
            double left = right ? waRight - Margin - w : waLeft + Margin;
            double top  = bottom ? waBottom - Margin - offset - h : waTop + Margin + offset;

            p.Left = left;
            if (animateNew) p.AnimateTop(top);
            else            p.Top = top;
            offset += h + Gap;
        }
    }

    private void AnimateTop(double targetTop)
    {
        var anim = new DoubleAnimation(Top, targetTop, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) => { BeginAnimation(TopProperty, null); Top = targetTop; };
        BeginAnimation(TopProperty, anim);
    }

    private void CloseWithAnimation()
    {
        if (_closing) return;
        _closing = true;
        _autoClose?.Stop();
        var fade = new DoubleAnimation(Opacity, 0, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWithAnimation();

    private void Body_Click(object sender, RoutedEventArgs e)
    {
        try { _onClick?.Invoke(); } catch { /* best effort */ }
        CloseWithAnimation();
    }
}
