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
/// (1) 먼저 대상 모니터로 옮겨 WPF 가 해당 모니터 DPI 로 리사이즈하게 한 뒤
/// (2) 실제 창 픽셀 크기(GetWindowRect)로 코너에 맞춰 SetWindowPos 한다.</summary>
public partial class NotificationPopup : Window
{
    private static readonly List<NotificationPopup> _active = new();
    // 모서리 여백(DIP) — 기존 12에서 절반으로. 알림 간 간격은 유지.
    private const double EdgeMargin = 6, Gap = 8;

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
        Opacity = 0; // 배치 전 깜빡임 방지

        // 1차: 대상 모니터로 이동 → WPF 가 그 모니터 DPI 로 리사이즈하게 한다(정확한 픽셀 크기 확보).
        var (waPx, _, _) = MonitorHelper.GetNotificationTarget();
        MovePx((int)waPx.Left, (int)waPx.Top);

        // 2차: 레이아웃/DPI 안정 후 실제 픽셀 크기로 코너 정렬 + 페이드 인.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            LayoutAll();
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200))));
        }));

        // 자동 닫힘: 설정값(초). 0=영구 → 타이머 없음(닫기 버튼/클릭으로만 닫힘).
        var sec = SettingsService.LoadNotifyAutoCloseSeconds();
        if (sec > 0)
        {
            _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
            _autoClose.Tick += (_, _) => CloseWithAnimation();
            _autoClose.Start();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_active.Remove(this)) LayoutAll();
    }

    /// <summary>전체 활성 알림을 선택 모니터의 물리 픽셀 좌표로 코너에서 바깥쪽으로 재배치한다.
    /// 최신 알림이 코너에 가장 가깝다. 창 크기는 GetWindowRect 로 실제 픽셀을 읽어 정확히 정렬.</summary>
    private static void LayoutAll()
    {
        var pos = SettingsService.LoadNotifyPosition();
        bool right  = pos is "br" or "tr";
        bool bottom = pos is "br" or "bl";
        var (waPx, sx, sy) = MonitorHelper.GetNotificationTarget();

        double marginX = EdgeMargin * sx, marginY = EdgeMargin * sy, gapPx = Gap * sy;
        double offset = 0;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var p = _active[i];
            var hwnd = new WindowInteropHelper(p).Handle;
            if (hwnd == IntPtr.Zero) continue;
            if (!GetWindowRect(hwnd, out var r)) continue;
            double wPx = r.Right - r.Left, hPx = r.Bottom - r.Top;
            double leftPx = right  ? waPx.Right  - marginX - wPx : waPx.Left + marginX;
            double topPx  = bottom ? waPx.Bottom - marginY - offset - hPx : waPx.Top + marginY + offset;
            SetWindowPos(hwnd, HWND_TOPMOST, (int)Math.Round(leftPx), (int)Math.Round(topPx), 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            offset += hPx + gapPx;
        }
    }

    private void MovePx(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
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
    private const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
