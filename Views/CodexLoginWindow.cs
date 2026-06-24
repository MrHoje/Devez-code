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

/// <summary>ChatGPT(Codex) OAuth 로그인 창. OpenAI 공식 codex CLI 와 동일한 PKCE 플로우로
/// 브라우저 로그인 → authorization code → 토큰 교환 후 <see cref="CodexCredentialStore"/> 에 저장한다.
/// redirect(localhost:1455)는 실제 서버 없이 WebView2 NavigationStarting 에서 가로채 처리한다.</summary>
public sealed class CodexLoginWindow : Window
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann"; // OpenAI Codex 공개 클라이언트(opencode 도 동일)
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string Redirect = "http://localhost:1455/auth/callback";
    private const string Scope = "openid profile email offline_access";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly WebView2 _view = new();
    private readonly string _verifier;
    private readonly string _state;
    private bool _done;

    /// <summary>로그인·토큰 저장 성공 여부.</summary>
    public bool Captured { get; private set; }

    public CodexLoginWindow(Window? owner)
    {
        Owner = owner;
        Title = "ChatGPT(Codex) 로그인";
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
            var url = $"{AuthorizeUrl}?client_id={ClientId}&response_type=code" +
                      $"&redirect_uri={Uri.EscapeDataString(Redirect)}" +
                      $"&scope={Uri.EscapeDataString(Scope)}" +
                      $"&code_challenge={challenge}&code_challenge_method=S256" +
                      $"&state={_state}&id_token_add_organizations=true" +
                      $"&codex_cli_simplified_flow=true&originator=opencode";
            _view.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Title = "WebView2 를 시작할 수 없습니다: " + ex.Message;
        }
    }

    /// <summary>redirect(localhost:1455)로의 이동을 가로채 code 를 받아 토큰으로 교환한다(실제 서버 불필요).</summary>
    private async void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_done || !e.Uri.StartsWith(Redirect, StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true; // localhost 로의 실제 요청은 막고 직접 처리
        _done = true;
        try
        {
            var (code, state) = ParseCallback(e.Uri);
            if (code == null || state != _state) { Close(); return; }
            await ExchangeAsync(code);
            Captured = true;
            DialogResult = true;
        }
        catch { /* 실패 — 그냥 닫는다 */ }
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
        CodexCredentialStore.Save(access, refresh, expiresMs);
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
