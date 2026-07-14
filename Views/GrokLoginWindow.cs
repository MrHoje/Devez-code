using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>Grok Build(xAI) OAuth 로그인 창. Grok CLI / open-grok-build 와 동일한 PKCE 플로우.
/// redirect(localhost)는 실제 서버 없이 WebView2 NavigationStarting 에서 가로채 처리한다.</summary>
public sealed class GrokLoginWindow : Window
{
    private const string ClientId = GrokCredentialStore.OAuthClientId;
    private const string DiscoveryUrl = "https://auth.x.ai/.well-known/openid-configuration";
    private const string Redirect = "http://127.0.0.1:56122/callback";
    private const string Scope = "openid profile email offline_access grok-cli:access api:access";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly WebView2 _view = new();
    private readonly string _verifier;
    private readonly string _state;
    private readonly string _nonce;
    private string? _tokenEndpoint;
    private bool _done;

    public bool Captured { get; private set; }

    public GrokLoginWindow(Window? owner)
    {
        Owner = owner;
        Title = "Grok Build 로그인 — 로그인하면 자동으로 연결됩니다";
        Width = 520; Height = 680;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        bool dark = App.CurrentTheme == "dark";
        Background = new System.Windows.Media.SolidColorBrush(
            dark ? System.Windows.Media.Color.FromRgb(0x1e, 0x1e, 0x1e) : System.Windows.Media.Colors.White);
        _view.DefaultBackgroundColor = dark
            ? System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e) : System.Drawing.Color.White;
        Content = _view;

        _verifier = RandomUrlSafe(32);
        _state = RandomUrlSafe(16);
        _nonce = RandomUrlSafe(16);

        if (dark)
            SourceInitialized += (_, _) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    int on = 1;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
                }
                catch { }
            };

        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => { try { _view.Dispose(); } catch { } };
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

            _view.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _view.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            await _view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
                (() => {
                    let lastCode = '';
                    let lastPostAt = 0;

                    const reportGrokAuthCode = () => {
                        const text = document.body?.innerText || '';
                        if (!text.includes('Grok Build') || !/finish signing in/i.test(text)) return;

                        const candidates = [];
                        for (const element of document.querySelectorAll('input, textarea, code, [data-testid*="code" i]')) {
                            candidates.push(element.value, element.getAttribute?.('value'), element.textContent);
                        }
                        candidates.push(...text.split(/\r?\n/));

                        const code = candidates
                            .map(value => (value || '').trim())
                            .find(value => /^[A-Za-z0-9._~-]{20,4096}$/.test(value));
                        if (!code) return;

                        // 교환 실패 시 자동 재시도하되, 진행 중 중복 메시지는 제한한다.
                        const now = Date.now();
                        if (code === lastCode && now - lastPostAt < 3000) return;
                        lastCode = code;
                        lastPostAt = now;
                        chrome.webview.postMessage({ type: 'grok-auth-code', code });
                    };

                    const start = () => {
                        reportGrokAuthCode();
                        const observer = new MutationObserver(reportGrokAuthCode);
                        observer.observe(document.documentElement || document,
                            { subtree: true, childList: true, attributes: true, characterData: true });
                        const poll = window.setInterval(reportGrokAuthCode, 500);
                        window.setTimeout(() => {
                            observer.disconnect();
                            window.clearInterval(poll);
                        }, 120000);
                    };

                    if (document.readyState === 'loading')
                        document.addEventListener('DOMContentLoaded', start, { once: true });
                    else
                        start();
                })();
                """);

            var (authEndpoint, tokenEndpoint) = await DiscoverAsync();
            _tokenEndpoint = tokenEndpoint;

            var challenge = Challenge(_verifier);
            var url = $"{authEndpoint}?response_type=code" +
                      $"&client_id={Uri.EscapeDataString(ClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(Redirect)}" +
                      $"&scope={Uri.EscapeDataString(Scope)}" +
                      $"&code_challenge={challenge}&code_challenge_method=S256" +
                      $"&state={_state}&nonce={_nonce}" +
                      $"&plan=generic&referrer=devezcode";
            _view.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Title = "로그인을 시작할 수 없습니다: " + ex.Message;
        }
    }

    private static async Task<(string auth, string token)> DiscoverAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var res = await http.GetAsync(DiscoveryUrl);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var auth = root.GetProperty("authorization_endpoint").GetString()
                   ?? throw new InvalidOperationException("authorization_endpoint 없음");
        var token = root.GetProperty("token_endpoint").GetString()
                    ?? throw new InvalidOperationException("token_endpoint 없음");
        return (auth, token);
    }

    private async void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_done) return;
        if (!e.Uri.StartsWith("http://127.0.0.1:56122/", StringComparison.OrdinalIgnoreCase)
            && !e.Uri.StartsWith("http://localhost:56122/", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;
        _done = true;
        try
        {
            var (code, state, error) = ParseCallback(e.Uri);
            if (!string.IsNullOrEmpty(error) || code == null || state != _state || _tokenEndpoint == null)
            {
                Close();
                return;
            }
            await ExchangeAsync(code, _tokenEndpoint);
            Captured = true;
            DialogResult = true;
        }
        catch { /* 실패 시 닫기 */ }
        Close();
    }

    /// <summary>xAI OIDC 는 loopback redirect 대신 페이지에 auth code를 표시해
    /// CLI에 붙여넣게 하는 client-paste 폴백을 사용할 수 있다. WebView가 그 code를
    /// 전달하면 일반 authorization_code + PKCE 교환으로 로그인을 완료한다.</summary>
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_done || _tokenEndpoint == null) return;

        try
        {
            var source = new Uri(e.Source);
            if (!source.Host.Equals("x.ai", StringComparison.OrdinalIgnoreCase)
                && !source.Host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase))
                return;

            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || type.GetString() != "grok-auth-code"
                || !root.TryGetProperty("code", out var codeElement))
                return;

            var code = codeElement.GetString()?.Trim();
            if (string.IsNullOrEmpty(code) || code.Length > 4096 || code.Any(char.IsWhiteSpace))
                return;

            DiagLog.Write($"GrokLogin client-paste code detected host={source.Host}");
            _done = true;
            await ExchangeAsync(code, _tokenEndpoint);
            Captured = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"GrokLogin client-paste exchange failed: {ex.GetType().Name}: {ex.Message}");
            Title = "Grok Build 로그인 실패: " + ex.Message;
            _done = false;
        }
    }

    private static (string? code, string? state, string? error) ParseCallback(string uri)
    {
        var q = new Uri(uri).Query.TrimStart('?');
        string? code = null, state = null, error = null;
        foreach (var kv in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            if (i < 0) continue;
            var k = kv[..i];
            var v = Uri.UnescapeDataString(kv[(i + 1)..]);
            if (k == "code") code = v;
            else if (k == "state") state = v;
            else if (k == "error") error = v;
        }
        return (code, state, error);
    }

    private async Task ExchangeAsync(string code, string tokenEndpoint)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = Redirect,
            ["code"] = code,
            ["code_verifier"] = _verifier,
        });
            using var res = await http.PostAsync(tokenEndpoint, body);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        var access = r.GetProperty("access_token").GetString()
                     ?? throw new InvalidOperationException("access_token 없음");
        var refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresSec = r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
            ? ei.GetInt64() : 3600;
        GrokCredentialStore.Save(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresSec));
        GrokCredentialStore.Enable();
    }

    private static string RandomUrlSafe(int bytes)
    {
        var b = new byte[bytes];
        RandomNumberGenerator.Fill(b);
        return B64Url(b);
    }

    private static string Challenge(string verifier)
    {
        using var sha = SHA256.Create();
        return B64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
    }

    private static string B64Url(byte[] b)
        => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
