using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DevezCode.Services;
using DevezCode.Services.Dashboard;

namespace DevezCode.Views;

/// <summary>외부 접속(릴레이) 페어링 창. 앱 내 WebView2 로 OAuth 로그인·승인 페이지를 열고,
/// 동시에 백그라운드로 페어링 폴링을 돌려 승인이 감지되면 deviceId/deviceSecret 을 로컬 저장하고
/// 외부 접속을 자동으로 켠 뒤 창을 닫는다.</summary>
public sealed class RelayPairWindow : BrowserPopupWindowBase
{
    private readonly CancellationTokenSource _cts = new();
    private TextBlock _statusText = null!;
    private ProgressBar _progress = null!;
    private bool _done;

    /// <summary>페어링 성공 여부(성공 시 config 저장 + Enabled=true).</summary>
    public bool Paired { get; private set; }

    public RelayPairWindow(Window? owner) : base(owner, "DevezCode", 700)
    {
        _statusText = new TextBlock
        {
            Text = "승인을 기다리는 중… 승인하면 자동으로 완료됩니다.",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _progress = new ProgressBar { IsIndeterminate = true, Height = 4, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        var barContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        barContent.Children.Add(_statusText);
        barContent.Children.Add(_progress);
        var statusBar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 14, 10),
            Child = barContent,
        };
        statusBar.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        statusBar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        SetFooterContent(statusBar);

        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => { try { _cts.Cancel(); } catch { } };
    }

    private async Task InitAsync()
    {
        try
        {
            await InitializeBrowserAsync();

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
