using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services;
using DevezCode.Services.Dashboard;

namespace DevezCode.Views;

/// <summary>Google OAuth 로그인과 원격 PC 페어링 승인을 한 창에서 처리한다.</summary>
public sealed class RelayPairWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int DwmDarkMode = 20;
    private const int GwlExStyle = -20;
    private const int DlgModalFrame = 0x0001;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpFrameChanged = 0x0020;
    private const uint WmSetIcon = 0x0080;

    private readonly WebView2 _view = new();
    private readonly CancellationTokenSource _cts = new();
    private TextBlock _statusText = null!;
    private ProgressBar _progress = null!;
    private bool _done;

    public bool Paired { get; private set; }

    public RelayPairWindow(Window? owner)
    {
        Owner = owner;
        Title = "DevezCode 외부 접속";
        Width = 520;
        Height = 700;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        var dark = App.CurrentTheme == "dark";
        var windowBackground = dark ? Color.FromRgb(0x1e, 0x1e, 0x1e) : Colors.White;
        var barBackground = dark ? Color.FromRgb(0x27, 0x27, 0x27) : Color.FromRgb(0xfa, 0xfa, 0xfa);
        var foreground = dark ? Color.FromRgb(0xe8, 0xe8, 0xe8) : Color.FromRgb(0x2a, 0x26, 0x20);
        var line = dark ? Color.FromRgb(0x40, 0x40, 0x40) : Color.FromRgb(0xd8, 0xd2, 0xc6);
        Background = new SolidColorBrush(windowBackground);
        _view.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e) : System.Drawing.Color.White;

        var logo = new TextBlock
        {
            Text = "DevezCode",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(foreground),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        if (TryFindResource("BrunoAceSCFont") is FontFamily logoFont) logo.FontFamily = logoFont;
        var header = new Border
        {
            Background = new SolidColorBrush(barBackground),
            BorderBrush = new SolidColorBrush(line),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = 44,
            Child = logo,
        };

        _statusText = new TextBlock
        {
            Text = "Google 로그인 후 승인을 기다리는 중…",
            Foreground = new SolidColorBrush(foreground),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _progress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 4,
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var statusContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusContent.Children.Add(_statusText);
        statusContent.Children.Add(_progress);
        var statusBar = new Border
        {
            Background = new SolidColorBrush(barBackground),
            BorderBrush = new SolidColorBrush(line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 14, 10),
            Child = statusContent,
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
                if (dark) { var on = 1; DwmSetWindowAttribute(hwnd, DwmDarkMode, ref on, sizeof(int)); }
                RemoveTitleBarIcon(hwnd);
            }
            catch { }
        };
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { try { _cts.Cancel(); } catch { } try { _view.Dispose(); } catch { } };
    }

    private static void RemoveTitleBarIcon(IntPtr hwnd)
    {
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | DlgModalFrame);
        SendMessage(hwnd, WmSetIcon, new IntPtr(1), IntPtr.Zero);
        SendMessage(hwnd, WmSetIcon, IntPtr.Zero, IntPtr.Zero);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _view.EnsureCoreWebView2Async(environment);
            if (App.CurrentTheme == "dark")
                _view.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

            var baseUrl = RemoteDashboardConfig.DefaultRelayBaseUrl;
            var httpsBase = new RemoteDashboardConfig { RelayBaseUrl = baseUrl }.HttpsBaseUrl();
            var start = await RelayPairingClient.StartAsync(httpsBase, Environment.MachineName, _cts.Token);
            DiagLog.Write($"relay pair start: verify={start.VerificationUrl}");
            _view.CoreWebView2.Navigate(start.VerificationUrl);
            _ = PollAsync(baseUrl, httpsBase, start);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagLog.Write("relay pair init failed: " + ex);
            ShowEnded("페어링을 시작할 수 없습니다: " + ex.Message);
        }
    }

    private async Task PollAsync(string relayBaseUrl, string httpsBase, RelayPairingClient.StartResult start)
    {
        var deadline = DateTime.UtcNow.AddSeconds(start.ExpiresInSeconds);
        try
        {
            while (!_done && DateTime.UtcNow < deadline && !_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1.5, start.IntervalSeconds)), _cts.Token);
                RelayPairingClient.PollResult result;
                try { result = await RelayPairingClient.PollAsync(httpsBase, start.UserCode, _cts.Token); }
                catch (Exception ex) { DiagLog.Write("relay pair poll error: " + ex.Message); continue; }

                if (result.Status == "approved" && !string.IsNullOrWhiteSpace(result.DeviceId)
                    && !string.IsNullOrWhiteSpace(result.DeviceSecret))
                {
                    _done = true;
                    var config = RemoteDashboardConfig.Current;
                    config.RelayBaseUrl = relayBaseUrl;
                    config.DeviceId = result.DeviceId!;
                    config.DeviceSecret = result.DeviceSecret!;
                    config.DeviceName = Environment.MachineName;
                    config.Enabled = true;
                    config.Save();
                    Paired = true;
                    await Dispatcher.InvokeAsync(() => { try { DialogResult = true; } catch { } Close(); });
                    return;
                }
                if (result.Status is "denied" or "expired")
                {
                    ShowEnded(result.Status == "denied" ? "페어링이 거부되었습니다." : "페어링 요청이 만료되었습니다.");
                    return;
                }
            }
            if (!_done && !_cts.IsCancellationRequested) ShowEnded("시간이 초과되었습니다. 다시 시도해 주세요.");
        }
        catch (OperationCanceledException) { }
    }

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
