using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>우측/좌측 모서리 스택형 토스트 알림 (devez NotificationPopup 이식, 로컬 단독 버전).
/// 표시 위치(4모서리)는 설정값을 따르고, 여러 알림은 코너에서 바깥쪽으로 쌓인다.
/// PerMonitorV2 환경에서 모니터마다 DPI 가 달라 WPF DIP 좌표로는 화면 밖으로 튀어나가므로,
/// 위치는 선택 모니터의 물리 픽셀로 계산해 SetWindowPos 로 직접 배치한다.</summary>
public partial class NotificationPopup : Window
{
    private static readonly List<NotificationPopup> _active = new();
    // 모서리 여백(DIP) — 기존 12에서 절반으로. 알림 간 간격은 유지.
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
        // 물리 픽셀로 직접 배치하므로 SizeToContent 자동 리사이즈를 잠가 SetWindowPos 와 충돌 방지.
        SizeToContent = SizeToContent.Manual;
        UpdateLayout();
        LayoutAll();

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
        if (_active.Remove(this)) LayoutAll();
    }

    /// <summary>전체 활성 알림을 선택 모니터의 물리 픽셀 좌표로 코너에서 바깥쪽으로 재배치한다.
    /// 최신 알림이 코너에 가장 가깝다. 크기·여백은 DIP 를 모니터 배율로 곱해 픽셀로 환산.</summary>
    private static void LayoutAll()
    {
        var pos = SettingsService.LoadNotifyPosition();
        bool right  = pos is "br" or "tr";
        bool bottom = pos is "br" or "bl";
        var (waPx, sx, sy) = MonitorHelper.GetNotificationTarget();

        double marginX = Margin * sx, marginY = Margin * sy, gapPx = Gap * sy;
        double offset = 0;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var p = _active[i];
            p.UpdateLayout();
            double dipW = p.ActualWidth > 0 ? p.ActualWidth : p.Width;
            double dipH = p.ActualHeight;
            double wPx = dipW * sx, hPx = dipH * sy;
            double leftPx = right  ? waPx.Right  - marginX - wPx : waPx.Left + marginX;
            double topPx  = bottom ? waPx.Bottom - marginY - offset - hPx : waPx.Top + marginY + offset;
            p.SetPhysicalPlacement(leftPx, topPx, wPx, hPx);
            offset += hPx + gapPx;
        }
    }

    /// <summary>창을 물리 픽셀 좌표/크기로 직접 배치(SetWindowPos). DIP→PX 변환 없이 정확.</summary>
    private void SetPhysicalPlacement(double leftPx, double topPx, double wPx, double hPx)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST,
            (int)Math.Round(leftPx), (int)Math.Round(topPx),
            (int)Math.Ceiling(wPx), (int)Math.Ceiling(hPx),
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
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

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
