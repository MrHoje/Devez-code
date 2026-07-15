using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services;
using DevezCode.Services.Dashboard;

namespace DevezCode.Views;

/// <summary>외부 접속(릴레이) 페어링 창. 앱 내 WebView2 로 OAuth 로그인·승인 페이지를 열고,
/// 동시에 백그라운드로 페어링 폴링을 돌려 승인이 감지되면 deviceId/deviceSecret 을 로컬 저장하고
/// 외부 접속을 자동으로 켠 뒤 창을 닫는다.</summary>
public sealed class RelayPairWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // 타이틀바 아이콘 제거용 P/Invoke.
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_DLGMODALFRAME = 0x0001;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;
    private const uint WM_SETICON = 0x0080;

    private readonly WebView2 _view = new();
    private readonly CancellationTokenSource _cts = new();
    private TextBlock _statusText = null!;
    private ProgressBar _progress = null!;
    private bool _done;

    /// <summary>페어링 성공 여부(성공 시 config 저장 + Enabled=true).</summary>
    public bool Paired { get; private set; }

    public RelayPairWindow(Window? owner)
    {
        Owner = owner;
        Title = "DevezCode";
        Width = 520; Height = 700;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        bool dark = App.CurrentTheme == "dark";
        var winBg = dark ? Color.FromRgb(0x1e, 0x1e, 0x1e) : Colors.White;
        var barBg = dark ? Color.FromRgb(0x27, 0x27, 0x27) : Color.FromRgb(0xFA, 0xFA, 0xFA);
        var barFg = dark ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x2A, 0x26, 0x20);
        var line = dark ? Color.FromRgb(0x40, 0x40, 0x40) : Color.FromRgb(0xD8, 0xD2, 0xC6);
        Background = new SolidColorBrush(winBg);
        _view.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e) : System.Drawing.Color.White;

        // 상단 헤더: DevezCode 텍스트 로고(좌상단 로고와 동일 폰트), 아이콘 이미지 없음.
        var logo = new TextBlock
        {
            Text = "DevezCode",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(barFg),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        if (TryFindResource("BrunoAceSCFont") is FontFamily logoFont) logo.FontFamily = logoFont;
        var header = new Border
        {
            Background = new SolidColorBrush(barBg),
            BorderBrush = new SolidColorBrush(line),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = 44,
            Child = logo,
        };

        _statusText = new TextBlock
        {
            Text = "승인을 기다리는 중… 승인하면 자동으로 완료됩니다.",
            Foreground = new SolidColorBrush(barFg),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _progress = new ProgressBar { IsIndeterminate = true, Height = 4, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        var barContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        barContent.Children.Add(_statusText);
        barContent.Children.Add(_progress);
        var statusBar = new Border
        {
            Background = new SolidColorBrush(barBg),
            BorderBrush = new SolidColorBrush(line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 14, 10),
            Child = barContent,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(_view, 1);
        Grid.SetRow(statusBar, 2);
        root.Children.Add(header);
        root.Children.Add(_view);
        root.Children.Add(statusBar);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (dark) { int on = 1; DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)); }
                RemoveTitleBarIcon(hwnd);
            }
            catch { }
        };

        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => { try { _cts.Cancel(); } catch { } try { _view.Dispose(); } catch { } };
    }

    private static void RemoveTitleBarIcon(IntPtr hwnd)
    {
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_DLGMODALFRAME);
        SendMessage(hwnd, WM_SETICON, new IntPtr(1), IntPtr.Zero); // ICON_BIG
        SendMessage(hwnd, WM_SETICON, new IntPtr(0), IntPtr.Zero); // ICON_SMALL
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private async Task InitAsync()
    {
        try
        {
            var userDataDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _view.EnsureCoreWebView2Async(env);
            if (App.CurrentTheme == "dark")
                _view.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

            var relayBaseUrl = RemoteDashboardConfig.DefaultRelayBaseUrl;
            var httpsBase = new RemoteDashboardConfig { RelayBaseUrl = relayBaseUrl }.HttpsBaseUrl();
            var start = await RelayPairingClient.StartAsync(httpsBase, Environment.MachineName, _cts.Token);
            DiagLog.Write($"relay pair start: code set, verify={start.VerificationUrl}");

            _view.CoreWebView2.Navigate(start.VerificationUrl);
            _ = PollLoopAsync(relayBaseUrl, httpsBase, start);
        }
        catch (OperationCanceledException) { /* 창 닫힘 */ }
        catch (Exception ex)
        {
            DiagLog.Write("relay pair init failed: " + ex.Message);
            ShowEnded("페어링을 시작할 수 없습니다: " + ex.Message);
        }
    }

    private async Task PollLoopAsync(string relayBaseUrl, string httpsBase, RelayPairingClient.StartResult start)
    {
        var deadline = DateTime.UtcNow.AddSeconds(start.ExpiresInSeconds);
        var interval = TimeSpan.FromSeconds(1.5);
        try
        {
            while (!_done && DateTime.UtcNow < deadline && !_cts.IsCancellationRequested)
            {
                await Task.Delay(interval, _cts.Token);
                RelayPairingClient.PollResult poll;
                try { poll = await RelayPairingClient.PollAsync(httpsBase, start.UserCode, _cts.Token); }
                catch (Exception ex) { DiagLog.Write("relay pair poll error: " + ex.Message); continue; }

                if (poll.Status == "approved" && !string.IsNullOrWhiteSpace(poll.DeviceId) && !string.IsNullOrWhiteSpace(poll.DeviceSecret))
                {
                    _done = true;
                    var cfg = RemoteDashboardConfig.Current;
                    cfg.RelayBaseUrl = relayBaseUrl;
                    cfg.DeviceId = poll.DeviceId!;
                    cfg.DeviceSecret = poll.DeviceSecret!;
                    cfg.DeviceName = Environment.MachineName;
                    cfg.Enabled = true;
                    cfg.Save();
                    Paired = true;
                    DiagLog.Write("relay pair approved, closing window");
                    await Dispatcher.InvokeAsync(() => { try { DialogResult = true; } catch { } Close(); });
                    return;
                }
                if (poll.Status is "denied" or "expired")
                {
                    ShowEnded(poll.Status == "denied" ? "페어링이 거부되었습니다." : "페어링 요청이 만료되었습니다. 창을 닫고 다시 시도해 주세요.");
                    return;
                }
            }
            if (!_done && !_cts.IsCancellationRequested)
                ShowEnded("시간이 초과되었습니다. 창을 닫고 다시 시도해 주세요.");
        }
        catch (OperationCanceledException) { /* 창 닫힘 */ }
    }

    /// <summary>승인 실패/타임아웃 시 스피너를 멈추고 안내 문구를 표시(자동으로 닫지 않음).</summary>
    private void ShowEnded(string message)
    {
        _done = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            _progress.IsIndeterminate = false;
            _progress.Visibility = Visibility.Collapsed;
            _statusText.Text = message;
        });
    }
}
