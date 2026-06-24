using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>opencode.ai 로그인 창. 사용자가 로그인하면 auth 쿠키와 workspaceId 를 자동 캡처해
/// <see cref="OpenCodeGoCredentialStore"/> 에 저장한다. 캡처되면 <see cref="Captured"/>=true 로 닫힌다.</summary>
public sealed class OpenCodeGoLoginWindow : Window
{
    private const string StartUrl = "https://opencode.ai/auth";
    private readonly WebView2 _view = new();
    private readonly DispatcherTimer _probe;
    private readonly TextBlock _status;
    private bool _done;

    /// <summary>로그인·캡처 성공 여부.</summary>
    public bool Captured { get; private set; }

    public OpenCodeGoLoginWindow(Window? owner)
    {
        Owner = owner;
        Title = "opencode.ai 로그인 — 로그인하면 자동으로 연결됩니다";
        Width = 520; Height = 680;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _status = new TextBlock
        {
            Text = "opencode.ai 에 로그인하세요. 로그인되면 자동으로 연결되고 창이 닫힙니다.",
            Margin = new Thickness(12, 10, 12, 10), TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(_status, 0);
        Grid.SetRow(_view, 1);
        grid.Children.Add(_status);
        grid.Children.Add(_view);
        Content = grid;

        _probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _probe.Tick += async (_, _) => await TryCaptureAsync();

        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => { _probe.Stop(); try { _view.Dispose(); } catch { } };
    }

    private async Task InitAsync()
    {
        try
        {
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _view.EnsureCoreWebView2Async(env);
            // 이전 로그인의 stale auth 쿠키가 WebView2 에 남아 있으면, 사용자가 새로 로그인하기도 전에
            // 그 만료 쿠키를 즉시 캡처·저장하고 창이 닫혀버린다(떴다 사라짐). 새 로그인을 강제하려 먼저 지운다.
            try { _view.CoreWebView2.CookieManager.DeleteCookies("auth", "https://opencode.ai"); } catch { }
            _view.CoreWebView2.SourceChanged += async (_, _) => await TryCaptureAsync();
            _view.CoreWebView2.Navigate(StartUrl);
            _probe.Start();
        }
        catch (Exception ex)
        {
            _status.Text = "WebView2 를 시작할 수 없습니다: " + ex.Message;
        }
    }

    /// <summary>현재 쿠키/URL 에서 auth 쿠키 + workspaceId 를 캡처 시도.</summary>
    private async Task TryCaptureAsync()
    {
        if (_done || _view.CoreWebView2 == null) return;
        try
        {
            var cookies = await _view.CoreWebView2.CookieManager.GetCookiesAsync("https://opencode.ai");
            string? auth = null;
            foreach (var c in cookies)
                if (c.Name == "auth" && !string.IsNullOrEmpty(c.Value)) { auth = c.Value; break; }
            if (auth == null) return;

            // workspaceId 는 반드시 현재 URL(/workspace/{id})에서만 얻는다.
            // 기존 저장값으로 fallback 하면 /auth(또는 about:blank) 단계에서 남은 stale 쿠키와 함께
            // 즉시 캡처돼 창이 뜨자마자 닫혀버린다. URL 이 workspace 에 도달 = 로그인 성공으로 본다.
            var ws = WorkspaceFromUrl(_view.CoreWebView2.Source);
            if (string.IsNullOrEmpty(ws))
            {
                _status.Text = "로그인하세요 — 워크스페이스 페이지(opencode.ai/workspace/…)에 들어가면 자동으로 연결됩니다.";
                return;
            }

            _done = true;
            OpenCodeGoCredentialStore.Save(ws, auth);
            Captured = true;
            DialogResult = true;
            Close();
        }
        catch { /* 초기화 중/해제 중 — 다음 tick 재시도 */ }
    }

    private static string? WorkspaceFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = Regex.Match(url, @"/workspace/([^/?#]+)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }
}
