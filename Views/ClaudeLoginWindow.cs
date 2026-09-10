using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>Claude(claude.ai Pro/Max) OAuth 로그인 창. claude CLI 와 동일한 PKCE 플로우로
/// 브라우저 로그인 → authorization code → 토큰 교환 후 <see cref="ClaudeCredentialStore"/> 에 저장한다.
/// redirect(console.anthropic.com/oauth/code/callback)는 WebView2 NavigationStarting 에서 가로채 처리한다.</summary>
public sealed class ClaudeLoginWindow : UsageLoginWindowBase
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"; // Claude Code 공개 클라이언트
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    private const string Redirect = "https://console.anthropic.com/oauth/code/callback";
    private const string Scope = "org:create_api_key user:profile user:inference";

    private readonly string _verifier;
    private readonly string _state;
    private bool _done;
    private readonly bool _captureOnly;

    public ClaudeLoginWindow(Window? owner, bool captureOnly = false) : base(owner, "Claude 로그인")
    {
        _captureOnly = captureOnly;
        _verifier = RandomUrlSafe(32);
        _state = RandomUrlSafe(32);

        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await InitializeBrowserAsync(_captureOnly);
            if (!IsLoaded) return;

            _view.CoreWebView2.NavigationStarting += OnNavigationStarting;

            var challenge = Challenge(_verifier);
            var url = $"{AuthorizeUrl}?code=true&client_id={ClientId}&response_type=code" +
                      $"&redirect_uri={Uri.EscapeDataString(Redirect)}" +
                      $"&scope={Uri.EscapeDataString(Scope)}" +
                      $"&code_challenge={challenge}&code_challenge_method=S256&state={_state}";
            _view.CoreWebView2.Navigate(url);
        }
        catch
        {
            if (IsLoaded)
            {
                ConfirmDialog.Alert("Claude 로그인", "로그인 창을 열지 못했습니다. 다시 시도하거나 현재 CLI 계정 가져오기를 사용하세요.");
                Close();
            }
        }
    }

    /// <summary>redirect(console.anthropic.com/oauth/code/callback)로의 이동을 가로채 code 를 받아 토큰 교환.</summary>
    private async void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_done || !IsOAuthCallback(e.Uri, Redirect)) return;
        e.Cancel = true;
        _done = true;
        try
        {
            var (code, state) = ParseCallback(e.Uri);
            if (code == null || state != _state) { Close(); return; }
            await ExchangeAsync(code);
            if (!IsLoaded) return;
            Captured = true;
            DialogResult = true;
        }
        catch { if (IsLoaded) ConfirmDialog.Alert("Claude 로그인", "로그인을 완료하지 못했습니다. 다시 시도하세요."); }
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
        // code 가 "code#fragment" 형태로 올 수 있다 — '#' 앞만 실제 인가 코드.
        if (code != null)
        {
            var h = code.IndexOf('#');
            if (h >= 0) code = code[..h];
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
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["code_verifier"] = _verifier,
            ["state"] = _state,
        });
        using var res = await http.PostAsync(TokenUrl, body);
        res.EnsureSuccessStatusCode();
        var response = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(response);
        var r = doc.RootElement;
        var access = r.GetProperty("access_token").GetString()
                     ?? throw new InvalidOperationException("access_token 없음");
        var refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresSec = r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
            ? ei.GetInt64() : 3600;
        var expiresMs = DateTimeOffset.Now.ToUnixTimeMilliseconds() + expiresSec * 1000;
        if (!IsLoaded) return;
        if (_captureOnly) TokenResponse = response;
        else ClaudeCredentialStore.Save(access, refresh, expiresMs);
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
