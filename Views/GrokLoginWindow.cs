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
