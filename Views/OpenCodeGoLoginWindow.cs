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

    // [임시 진단] 다크 적용 실패 원인 추적용 — webview-diag.txt 에 페이지 상태 기록.
    private const string DiagScript =
        "(function(){try{var r=document.querySelector('[data-component=\\\"root\\\"]');" +
        "return JSON.stringify({href:location.href," +
        "prefersDark:matchMedia('(prefers-color-scheme: dark)').matches," +
        "injected:!!document.getElementById('devez-dark')," +
        "varBg:getComputedStyle(document.documentElement).getPropertyValue('--color-background').trim()," +
        "rootBg:r?getComputedStyle(r).backgroundColor:'(no-root)'," +
        "bodyBg:document.body?getComputedStyle(document.body).backgroundColor:'(no-body)'});" +
        "}catch(e){return 'DIAGERR:'+e.message;}})();";

    private readonly WebView2 _view = new();
    private readonly DispatcherTimer _probe;
    private bool _done;

    /// <summary>로그인·캡처 성공 여부.</summary>
    public bool Captured { get; private set; }

    public OpenCodeGoLoginWindow(Window? owner)
    {
        Owner = owner;
        Title = "opencode.ai 로그인 — 로그인하면 자동으로 연결됩니다";
        Width = 520; Height = 680;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        // 다크 테마면 창/뷰 배경을 어둡게 — 로딩 중 흰 깜빡임 방지(WebView2 다크는 InitAsync 에서 적용).
        bool dark = App.CurrentTheme == "dark";
        Background = new System.Windows.Media.SolidColorBrush(
            dark ? System.Windows.Media.Color.FromRgb(0x1e, 0x1e, 0x1e) : System.Windows.Media.Colors.White);
        _view.DefaultBackgroundColor = dark
            ? System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e) : System.Drawing.Color.White;
        Content = _view;

        _probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _probe.Tick += async (_, _) => await TryCaptureAsync();

        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => { _probe.Stop(); try { _view.Dispose(); } catch { } };
    }

    private static void LogDiag(string msg)
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "webview-diag.txt");
            File.AppendAllText(p, msg + Environment.NewLine);
        }
        catch { }
    }

    private async Task InitAsync()
    {
        LogDiag($"[init] start theme={App.CurrentTheme}");
        try
        {
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _view.EnsureCoreWebView2Async(env);
            LogDiag("[init] core ready");
            // 앱 테마에 맞춰 웹 콘텐츠도 다크/라이트 적용(prefers-color-scheme).
            _view.CoreWebView2.Profile.PreferredColorScheme = App.CurrentTheme == "dark"
                ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            // opencode.ai(OpenAuth) 로그인 UI 가 prefers-color-scheme 를 무시할 때를 대비해,
            // 디자인 변수(--color-*)를 다크값으로 강제하는 CSS 를 주입(헤더·배경 다크).
            // 문서 생성 시점(깜빡임 방지) + 네비게이션 완료 후(확실한 적용) 양쪽에서 주입.
            if (App.CurrentTheme == "dark")
            {
                await _view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(InjectDarkScript);
                _view.CoreWebView2.NavigationCompleted += async (_, _) =>
                {
                    LogDiag("[navdone]");
                    try
                    {
                        await _view.CoreWebView2.ExecuteScriptAsync(InjectDarkScript);
                        var diag = await _view.CoreWebView2.ExecuteScriptAsync(DiagScript);
                        LogDiag("[diag] " + diag);
                    }
                    catch (Exception ex) { LogDiag("[navdone] ERR:" + ex.Message); }
                };
            }
            // 이전 로그인의 stale auth 쿠키가 WebView2 에 남아 있으면, 사용자가 새로 로그인하기도 전에
            // 그 만료 쿠키를 즉시 캡처·저장하고 창이 닫혀버린다(떴다 사라짐). 새 로그인을 강제하려 먼저 지운다.
            try { _view.CoreWebView2.CookieManager.DeleteCookies("auth", "https://opencode.ai"); } catch { }
            _view.CoreWebView2.SourceChanged += async (_, _) => await TryCaptureAsync();
            _view.CoreWebView2.Navigate(StartUrl);
            _probe.Start();
            LogDiag("[init] navigate called");
        }
        catch (Exception ex)
        {
            LogDiag("[init] EXCEPTION: " + ex);
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
