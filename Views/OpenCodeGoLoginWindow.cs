using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>opencode.ai 로그인 창. 사용자가 로그인하면 auth 쿠키와 workspaceId 를 자동 캡처해
/// <see cref="OpenCodeGoCredentialStore"/> 에 저장한다. 캡처되면 <see cref="Captured"/>=true 로 닫힌다.</summary>
public sealed class OpenCodeGoLoginWindow : UsageLoginWindowBase
{
    private const string StartUrl = "https://opencode.ai/auth";

    // 다크 강제 CSS — OpenAuth/opencode 의 테마 변수를 다크값으로 덮고 배경을 어둡게. 문서 생성마다 주입.
    private const string DarkCss =
        ":root{color-scheme:dark!important;" +
        "--color-background:var(--color-background-dark,#0e0e11)!important;" +
        "--color-primary:var(--color-primary-dark,#ffffff)!important;}" +
        "html,body{background:var(--color-background-dark,#0e0e11)!important;}";
    // head 가 아직 없을 수 있어(문서 생성 직후) DOMContentLoaded 까지 대비. 중복 주입은 id 로 방지.
    private static readonly string InjectDarkScript =
        "(function(){var css=" + System.Text.Json.JsonSerializer.Serialize(DarkCss) + ";" +
        "function add(){try{if(document.getElementById('devez-dark'))return;" +
        "var s=document.createElement('style');s.id='devez-dark';s.textContent=css;" +
        "(document.head||document.documentElement).appendChild(s);}catch(e){}}" +
        "add();if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',add);})();";

    private readonly DispatcherTimer _probe;
    private bool _done;

    public OpenCodeGoLoginWindow(Window? owner)
        : base(owner, "opencode.ai 로그인 — 로그인하면 자동으로 연결됩니다")
    {
        _probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _probe.Tick += async (_, _) => await TryCaptureAsync();

        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => _probe.Stop();
    }

    private async Task InitAsync()
    {
        try
        {
            await InitializeBrowserAsync();
            // opencode.ai(OpenAuth) 로그인 UI 가 prefers-color-scheme 를 무시할 때를 대비해,
            // 디자인 변수(--color-*)를 다크값으로 강제하는 CSS 를 주입(헤더·배경 다크).
            // 문서 생성 시점(깜빡임 방지) + 네비게이션 완료 후(확실한 적용) 양쪽에서 주입.
            if (App.IsDarkTheme(App.CurrentTheme))
            {
                await _view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(InjectDarkScript);
                _view.CoreWebView2.NavigationCompleted += async (_, _) =>
                {
                    try { await _view.CoreWebView2.ExecuteScriptAsync(InjectDarkScript); } catch { }
                };
            }
            // 이전 로그인의 stale auth 쿠키가 WebView2 에 남아 있으면, 사용자가 새로 로그인하기도 전에
            // 그 만료 쿠키를 즉시 캡처·저장하고 창이 닫혀버린다(떴다 사라짐). 새 로그인을 강제하려 먼저 지운다.
            try { _view.CoreWebView2.CookieManager.DeleteCookies("auth", "https://opencode.ai"); } catch { }
            _view.CoreWebView2.SourceChanged += async (_, _) => await TryCaptureAsync();
            _view.CoreWebView2.Navigate(StartUrl);
            _probe.Start();
        }
        catch (Exception ex)
        {
            Title = "WebView2 를 시작할 수 없습니다: " + ex.Message;
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
            if (string.IsNullOrEmpty(ws)) return;

            _done = true;
            OpenCodeGoCredentialStore.Save(ws, auth);
            OpenCodeGoCredentialStore.Enable();
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
