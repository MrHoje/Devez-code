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

/// <summary>Google Antigravity(agy) OAuth 로그인 창. codex/grok 과 동일한 WebView2 + PKCE 플로우.
/// redirect(loopback)는 실제 서버 없이 WebView2 NavigationStarting 에서 가로채 처리한다.
/// 성공 시 토큰을 <see cref="AntigravityCredentialStore"/> 자체 스토어에 저장.
/// <para>주의: Google 은 임베디드 WebView 로그인을 정책으로 막을 수 있다("안전하지 않은 브라우저").
/// 그 경우 disallowed_useragent 페이지가 뜨며, 시스템 브라우저+loopback 폴백이 필요하다
/// (미구현 — 그때 확장). 로그인 실패해도 agy 키링 자동 인식으로 기존 동작은 유지.</para></summary>
public sealed class AntigravityLoginWindow : Window
{
    private const string ClientId = AntigravityCredentialStore.OAuthClientId;
    private const string ClientSecret = AntigravityCredentialStore.OAuthClientSecret;
    private const string AuthorizeUrl = AntigravityCredentialStore.OAuthAuthorizeUrl;
    private const string TokenUrl = AntigravityCredentialStore.OAuthTokenUrl;
    private const string Scope = AntigravityCredentialStore.OAuthScope;
    private const string Redirect = "http://127.0.0.1:8123/oauth2callback";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly WebView2 _view = new();
    private readonly string _verifier;
    private readonly string _state;
    private bool _done;

    /// <summary>로그인·토큰 저장 성공 여부.</summary>
    public bool Captured { get; private set; }

    public AntigravityLoginWindow(Window? owner)
    {
        Owner = owner;
        Title = "Antigravity(Google) 로그인 — 로그인하면 자동으로 연결됩니다";
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

            var challenge = Challenge(_verifier);
            var url = $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(ClientId)}&response_type=code" +
                      $"&redirect_uri={Uri.EscapeDataString(Redirect)}" +
                      $"&scope={Uri.EscapeDataString(Scope)}" +
                      $"&code_challenge={challenge}&code_challenge_method=S256" +
                      $"&state={_state}&access_type=offline&prompt=consent";
            _view.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Title = "WebView2 를 시작할 수 없습니다: " + ex.Message;
        }
    }

    /// <summary>redirect(loopback)로의 이동을 가로채 code 를 받아 토큰으로 교환한다(실제 서버 불필요).</summary>
    private async void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_done || !e.Uri.StartsWith(Redirect, StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true; // loopback 로의 실제 요청은 막고 직접 처리
        _done = true;
        try
        {
            var (code, state) = ParseCallback(e.Uri);
            if (code == null || state != _state) { Close(); return; }
            await ExchangeAsync(code);
            Captured = true;
            DialogResult = true;
        }
        catch { /* 실패 — 그냥 닫는다 (키링 폴백 유지) */ }
        Close();
    }

    private static (string? code, string? state) ParseCallback(string uri)
    {
        var q = new Uri(uri).Query.TrimStart('?');
        string? code = null, state = null;
        foreach (var kv in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            if (i < 0) continue;
            var k = kv[..i];
            var v = Uri.UnescapeDataString(kv[(i + 1)..]);
            if (k == "code") code = v;
            else if (k == "state") state = v;
        }
        return (code, state);
    }

    private async Task ExchangeAsync(string code)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = Redirect,
            ["code"] = code,
            ["code_verifier"] = _verifier,
        });
        using var res = await http.PostAsync(TokenUrl, body);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        var access = r.GetProperty("access_token").GetString()
                     ?? throw new InvalidOperationException("access_token 없음");
        var refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresSec = r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
            ? ei.GetInt64() : 3600;
        var expiresMs = DateTimeOffset.Now.ToUnixTimeMilliseconds() + expiresSec * 1000;
        AntigravityCredentialStore.Save(access, refresh, expiresMs);
        AntigravityCredentialStore.Enable();
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
