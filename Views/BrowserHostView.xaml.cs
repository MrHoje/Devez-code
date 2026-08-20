using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>
/// 우측 패널 브라우저 — 단일 WebView2. devez BrowserHostView 를 방 개념 없이 단순화.
/// 툴바(뒤로/앞으로/새로고침 + 주소창), 마지막 URL 복원, 앱 테마 연동.
/// </summary>
public partial class BrowserHostView : UserControl
{
    /// <summary>브라우저 WebView가 실제 키보드 포커스를 받음. HwndHost라 부모 WPF 마우스 이벤트로는 관측할 수 없다.</summary>
    public event Action? NativeSurfaceFocused;

    private WebView2? _view;
    private bool _initStarted;
    private string? _pendingOpenUrl;

    private readonly Action<string> _themeChangedHandler;

    // ── 가상 히스토리 ────────────────────────────────────────────────
    // WebView2(Chromium) 네이티브 뒤로/앞으로 스택은 외부 주입 API 가 없어,
    // 앱이 방문 URL 스택(_history) + 현재 위치(_index)를 직접 관리한다.
    // 뒤로/앞으로 버튼은 네이티브 GoBack/GoForward 대신 이 스택을 navigate → 프로젝트 전환 후에도 동작.
    private readonly List<string> _history = new();
    private int _index = -1;

    /// <summary>다음 SourceChanged 가 무엇 때문에 발생했는지. User=자발적 탐색(스택에 기록),
    /// History=뒤/앞 버튼, Restore=프로젝트 전환/초기 복원(둘 다 스택 기록 안 함).</summary>
    private enum NavCause { User, History, Restore }
    private NavCause _pendingCause = NavCause.User;

    /// <summary>현재 브라우저가 속한 프로젝트 절대경로. null/empty 면 전역(미배정).
    /// 값이 바뀌면 해당 프로젝트의 저장된 URL 로 즉시 이동. 프로젝트별 독립 상태 유지의 핵심.</summary>
    public string? ProjectPath
    {
        get => _projectPath;
        set
        {
            if (_projectPath == value) return;
            _projectPath = value;
            OnProjectPathChanged();
        }
    }
    private string? _projectPath;

    /// <summary>방문 기록 저장 키. 기존 우측 브라우저는 프로젝트 경로, 중앙 브라우저는 탭 ID 기반 키를 사용한다.</summary>
    public string? StateKey
    {
        get => ProjectPath;
        set => ProjectPath = value;
    }

    public BrowserHostView()
    {
        InitializeComponent();
        _themeChangedHandler = _ => ApplyColorScheme();
        App.ThemeChanged += _themeChangedHandler;
    }

    /// <summary>프로젝트 전환 시 — 이미 초기화된 경우 해당 프로젝트의 저장된 히스토리를 불러와
    /// 현재 위치 URL 로 이동. 초기화 전이면 EnsureStarted 가 현재 ProjectPath 를 사용한다.</summary>
    private void OnProjectPathChanged()
    {
        if (!_initStarted || _view?.CoreWebView2 == null) return;
        LoadHistoryForCurrentProject();
        NavigateToCurrentOrHome();
        SyncToolbar();
    }

    /// <summary>저장된 히스토리가 있으면 현재 위치로 복원(기록 안 함),
    /// 비어 있으면 홈을 첫 방문(User)으로 열어 스택에 기록되게 한다.</summary>
    private void NavigateToCurrentOrHome()
    {
        if (CurrentHistoryUrl is { } cur)
            NavigateInternal(cur, NavCause.Restore);
        else
            NavigateInternal(SettingsService.LoadBrowserHomeUrl(), NavCause.User);
    }

    /// <summary>현재 _index 가 가리키는 URL. 스택이 비었으면 null.</summary>
    private string? CurrentHistoryUrl =>
        _index >= 0 && _index < _history.Count ? _history[_index] : null;

    /// <summary>현재 프로젝트의 저장된 히스토리를 _history/_index 로 적재. 없으면 빈 스택.</summary>
    private void LoadHistoryForCurrentProject()
    {
        _history.Clear();
        _index = -1;
        var loaded = SettingsService.LoadBrowserHistory(_projectPath);
        if (loaded is { } h && h.Urls.Count > 0)
        {
            _history.AddRange(h.Urls);
            _index = Math.Clamp(h.Index, 0, _history.Count - 1);
        }
    }

    /// <summary>지정 cause 로 표시 후 navigate. SourceChanged 가 이 cause 를 보고 기록 여부를 정한다.</summary>
    private void NavigateInternal(string url, NavCause cause)
    {
        var core = _view?.CoreWebView2;
        if (core == null) return;
        _pendingCause = cause;
        try { core.Navigate(url); }
        catch { _pendingCause = NavCause.User; }
    }

    /// <summary>터미널 링크 등 외부 요청 URL을 현재 브라우저 탭에서 연다. 초기화 전이면 첫 탐색으로 보류한다.</summary>
    public void NavigateToUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;

        var normalized = uri.AbsoluteUri;
        if (_view?.CoreWebView2 == null)
        {
            _pendingOpenUrl = normalized;
            return;
        }
        NavigateInternal(normalized, NavCause.User);
    }

    /// <summary>현재 페이지를 Windows 기본 브라우저로 연다.
    /// 아직 WebView2가 시작되지 않은 탭은 저장된 마지막 방문 URL을 사용한다.</summary>
    public bool TryOpenInDefaultBrowser(string? stateKey = null)
    {
        var url = ResolveCurrentWebUrl(stateKey);
        if (url == null) return false;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>현재 또는 저장된 마지막 방문 URL을 클립보드에 복사한다.</summary>
    public bool TryCopyCurrentUrl(string? stateKey = null)
    {
        var url = ResolveCurrentWebUrl(stateKey);
        if (url == null) return false;
        try
        {
            Clipboard.SetText(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>현재 또는 저장된 마지막 웹 주소. 자동화 탭 선택 목록에 표시할 때 사용한다.</summary>
    public string? GetCurrentUrl(string? stateKey = null)
        => ResolveCurrentWebUrl(stateKey);

    private string? ResolveCurrentWebUrl(string? stateKey)
    {
        string? url = _view?.CoreWebView2?.Source;
        if (!IsWebUrl(url)) url = _pendingOpenUrl;
        if (!IsWebUrl(url)) url = CurrentHistoryUrl;
        if (!IsWebUrl(url))
        {
            var saved = SettingsService.LoadBrowserHistory(stateKey ?? _projectPath);
            if (saved is { } history && history.Urls.Count > 0)
                url = history.Urls[Math.Clamp(history.Index, 0, history.Urls.Count - 1)];
        }
        return IsWebUrl(url) ? url : null;
    }

    private static bool IsWebUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>자발적 탐색 1건을 스택에 기록(브라우저 표준: 현재 위치 앞쪽은 버림).</summary>
    private void RecordVisit(string url)
    {
        // 동일 URL 연속(새로고침/같은주소 리다이렉트)은 중복 기록 안 함
        if (CurrentHistoryUrl == url) return;
        if (_index < _history.Count - 1)
            _history.RemoveRange(_index + 1, _history.Count - _index - 1);
        _history.Add(url);
        _index = _history.Count - 1;
    }

    /// <summary>현재 스택+위치를 프로젝트별로 영속.</summary>
    private void PersistHistory()
    {
        if (_history.Count > 0)
            SettingsService.SaveBrowserHistory(_projectPath, _history, _index);
    }

    private static CoreWebView2PreferredColorScheme PreferredScheme =>
        App.IsDarkTheme(App.CurrentTheme)
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;

    private void ApplyColorScheme()
    {
        try { if (_view?.CoreWebView2 != null) _view.CoreWebView2.Profile.PreferredColorScheme = PreferredScheme; }
        catch { /* 해제 중 등 */ }
    }

    /// <summary>처음 표시될 때 WebView2 를 초기화하고 마지막/홈 URL 을 연다(1회만).</summary>
    public void EnsureStarted() => _ = EnsureStartedAsync();

    /// <summary>EnsureStarted 의 await 가능 버전. 여러 번 호출해도 첫 초기화 Task 를 공유한다.
    /// 세션 자동화(MCP)가 탭을 화면에 띄우지 않고 조작할 때 초기화 완료를 기다리기 위해 필요.</summary>
    public Task EnsureStartedAsync() => _initTask ??= StartCoreAsync();
    private Task? _initTask;

    /// <summary>CoreWebView2 가 준비될 때까지 기다린 뒤 반환. 초기화 실패 시 null.</summary>
    public async Task<CoreWebView2?> EnsureCoreAsync()
    {
        await EnsureStartedAsync();
        return _view?.CoreWebView2;
    }

    private async Task StartCoreAsync()
    {
        _initStarted = true;
        try
        {
            _view = new WebView2();
            _view.GotKeyboardFocus += (_, _) => NativeSurfaceFocused?.Invoke();
            BrowserContent.Children.Add(_view);

            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevezCode", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _view.EnsureCoreWebView2Async(env);

            var core = _view.CoreWebView2;
            try { core.Profile.PreferredColorScheme = PreferredScheme; } catch { }

            core.SourceChanged += (_, _) =>
            {
                var src = core.Source;
                var cause = _pendingCause;
                _pendingCause = NavCause.User; // 다음 탐색 기본값으로 즉시 리셋

                if (string.IsNullOrEmpty(src) || src == "about:blank")
                { SyncToolbar(); return; }

                if (cause == NavCause.User)
                {
                    RecordVisit(src);
                    PersistHistory();
                }
                else if (CurrentHistoryUrl != null && CurrentHistoryUrl != src)
                {
                    // 뒤/앞·복원 도중 리다이렉트로 실제 URL 이 달라졌으면 현재 칸을 실제값으로 보정
                    _history[_index] = src;
                    PersistHistory();
                }
                SyncToolbar();
            };
            // 새 창 요청은 같은 뷰에서 열기(팝업 차단 대신 인라인 이동) — 자발적 탐색으로 기록
            core.NewWindowRequested += (_, e) => { e.Handled = true; NavigateInternal(e.Uri, NavCause.User); };

            LoadHistoryForCurrentProject();
            if (_pendingOpenUrl is { } pending)
            {
                _pendingOpenUrl = null;
                NavigateInternal(pending, NavCause.User);
            }
            else NavigateToCurrentOrHome();
            SyncToolbar();
        }
        catch (Exception ex)
        {
            BrowserContent.Children.Clear();
            BrowserContent.Children.Add(new TextBlock
            {
                Text = "브라우저를 시작할 수 없습니다.\nWebView2 런타임이 필요합니다.\n\n" + ex.Message,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
            });
        }
    }

    /// <summary>주소창·네비게이션 버튼을 가상 히스토리 기준으로 동기화.</summary>
    private void SyncToolbar()
    {
        var core = _view?.CoreWebView2;
        BackBtn.IsEnabled = _index > 0;
        ForwardBtn.IsEnabled = _index >= 0 && _index < _history.Count - 1;
        if (core != null && !AddressBox.IsKeyboardFocused)
            AddressBox.Text = core.Source;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_view?.CoreWebView2 == null || _index <= 0) return;
        _index--;
        NavigateInternal(_history[_index], NavCause.History);
        PersistHistory();
        SyncToolbar();
    }

    private void ForwardBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_view?.CoreWebView2 == null || _index >= _history.Count - 1) return;
        _index++;
        NavigateInternal(_history[_index], NavCause.History);
        PersistHistory();
        SyncToolbar();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        => _view?.CoreWebView2?.Reload();

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => AddressBox.SelectAll();

    private void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddressBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            AddressBox.Focus();
        }
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var input = AddressBox.Text.Trim();
        if (input.Length == 0) return;
        NavigateInternal(ToNavigationTarget(input), NavCause.User);
        _view?.Focus();
        e.Handled = true;
    }

    /// <summary>입력이 URL이면 그대로 이동, 아니면 구글 검색.</summary>
    private static string ToNavigationTarget(string input)
    {
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return input;

        bool looksLikeDomain = !input.Contains(' ')
            && input.Contains('.')
            && Uri.TryCreate("https://" + input, UriKind.Absolute, out var u)
            && u.Host.Contains('.');
        if (looksLikeDomain) return "https://" + input;

        return "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
    }

    // ── airspace 우회 (오버레이가 WebView2 뒤로 묻히는 것 방지) ───────
    /// <summary>WebView2 를 PNG 스냅샷으로 교체하고 숨긴다. 툴바(WPF)는 유지.</summary>
    public async Task SuspendContentAsync()
    {
        // 이미 숨긴 상태면 다시 캡처하지 않는다 — 숨겨진(Collapsed) WebView2 의 CapturePreviewAsync 는
        // 완료되지 않아 호출자가 매달린다(종료 준비 지연의 원인).
        if (BrowserContent.Visibility != Visibility.Visible) return;
        if (_view?.CoreWebView2 != null)
        {
            try
            {
                using var ms = new MemoryStream();
                var capture = _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                if (await Task.WhenAny(capture, Task.Delay(1500)) != capture)
                {
                    // 일부 WebView2 런타임은 CapturePreviewAsync 를 끝내지 않는다. 오버레이를 막지 않도록
                    // 스냅샷 없이 즉시 숨기고, 나중에 fault 되더라도 예외는 관찰한다.
                    _ = capture.ContinueWith(t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted);
                    DiagLog.Write("Browser snapshot timed out; hiding browser without snapshot.");
                }
                else
                {
                    await capture;
                    ms.Position = 0;
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = ms;
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    BrowserSnapshot.Source = bmp;
                    BrowserSnapshot.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }
        BrowserContent.Visibility = Visibility.Collapsed;
    }

    /// <summary>WebView2 컨텐츠 복원. 스냅샷 제거.</summary>
    public void ResumeContent()
    {
        BrowserContent.Visibility = Visibility.Visible;
        BrowserSnapshot.Visibility = Visibility.Collapsed;
        BrowserSnapshot.Source = null;
    }

    public bool IsStarted => _initStarted;

    // ── 세션 자동화(MCP 브라우저 도구) API ──────────────────────────
    // 화면에 띄우지 않은(파킹된) 탭에서도 동작해야 하므로 모두 EnsureCoreAsync 로 초기화를 기다린다.
    // 실패는 예외로 올려 브리지가 에이전트에게 사유를 그대로 전달한다.

    private async Task<CoreWebView2> RequireCoreAsync()
        => await EnsureCoreAsync() ?? throw new InvalidOperationException(
            "WebView2 를 시작할 수 없습니다(런타임 미설치 가능).");

    /// <summary>URL(또는 검색어)로 이동하고 탐색 완료까지 대기. 반환값=최종 URL.</summary>
    public async Task<string> AutomationNavigateAsync(string urlOrQuery, int timeoutMs = 30000)
    {
        var core = await RequireCoreAsync();
        var target = ToNavigationTarget(urlOrQuery.Trim());
        await NavigateAndWaitAsync(core, () =>
        {
            _pendingCause = NavCause.User;
            core.Navigate(target);
        }, timeoutMs);
        return core.Source;
    }

    /// <summary>탐색을 실행하고 <b>그 탐색</b>의 완료만 기다린다.
    /// <para>NavigationId 로 매칭하지 않으면 초기화 중 시작된 홈 URL 탐색의 완료 이벤트가 대기를 먼저
    /// 깨워, 로드가 끝나기 전에 about:blank 를 돌려주게 된다(실측 버그).</para></summary>
    private static async Task NavigateAndWaitAsync(CoreWebView2 core, Action navigate, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navId = null;
        void OnStarting(object? _, CoreWebView2NavigationStartingEventArgs e) => navId ??= e.NavigationId;
        void OnCompleted(object? _, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navId != null && e.NavigationId == navId) tcs.TrySetResult(e.IsSuccess);
        }

        core.NavigationStarting += OnStarting;
        core.NavigationCompleted += OnCompleted;
        try
        {
            navigate();
            await WaitOrTimeoutAsync(tcs.Task, timeoutMs, "페이지 로드");
        }
        finally
        {
            core.NavigationStarting -= OnStarting;
            core.NavigationCompleted -= OnCompleted;
        }
    }

    public async Task<string> AutomationCurrentUrlAsync()
        => (await RequireCoreAsync()).Source;

    public async Task<string> AutomationTitleAsync()
        => (await RequireCoreAsync()).DocumentTitle;

    /// <summary>본문 텍스트 추출(script/style/nav 노이즈 제외). maxChars 초과분은 잘라낸다.</summary>
    public async Task<string> AutomationReadTextAsync(int maxChars = 20000)
    {
        var text = await AutomationEvalAsync("""
            (() => {
              const t = (document.body ? document.body.innerText : '') || '';
              return t.replace(/\n{3,}/g, '\n\n').trim();
            })()
            """);
        return maxChars > 0 && text.Length > maxChars ? text[..maxChars] + "\n…(잘림)" : text;
    }

    /// <summary>페이지의 링크 목록을 "텍스트 | URL" 줄로 반환(중복/빈 텍스트 제외).</summary>
    public async Task<string> AutomationLinksAsync(int max = 50)
    {
        var js = $$"""
            (() => {
              const seen = new Set(); const out = [];
              for (const a of document.querySelectorAll('a[href]')) {
                const href = a.href; const text = (a.innerText || a.textContent || '').trim().replace(/\s+/g, ' ');
                if (!href || !text || href.startsWith('javascript:')) continue;
                if (seen.has(href)) continue;
                seen.add(href); out.push(text + ' | ' + href);
                if (out.length >= {{Math.Max(1, max)}}) break;
              }
              return out.join('\n');
            })()
            """;
        return await AutomationEvalAsync(js);
    }

    /// <summary>CSS 선택자 또는 화면에 보이는 텍스트로 클릭. 대상이 아직 없으면 timeoutMs 까지 폴링한다.
    /// <para>React 같은 프레임워크는 입력 반영 뒤 다음 렌더에서야 전송 버튼을 그리므로, 즉시 조회하면
    /// 대상을 못 찾는다(실측). 그래서 클릭은 '한 번 찾고 실패'가 아니라 짧게 기다린다.</para></summary>
    public async Task<string> AutomationClickAsync(string selectorOrText, int timeoutMs = 3000)
    {
        var arg = JsonSerializer.Serialize(selectorOrText);
        var js = $$"""
            (() => {
              const q = {{arg}};
              let el = null;
              try { el = document.querySelector(q); } catch (_) {}
              if (!el) {
                const cands = document.querySelectorAll('a,button,[role=button],input[type=submit],input[type=button],summary');
                const norm = s => (s || '').trim().replace(/\s+/g, ' ');
                el = [...cands].find(c => norm(c.innerText || c.value) === norm(q))
                  || [...cands].find(c => norm(c.innerText || c.value).includes(norm(q)));
              }
              if (!el) return 'NOTFOUND';
              el.scrollIntoView({ block: 'center' });
              el.click();
              return 'OK';
            })()
            """;
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            if (await AutomationEvalAsync(js) != "NOTFOUND") return "clicked";
            if (Environment.TickCount64 >= deadline)
                throw new InvalidOperationException(
                    $"클릭 대상을 찾지 못했습니다: {selectorOrText} " +
                    "(아직 렌더 전이면 browser_wait_selector 로 먼저 기다리세요)");
            await Task.Delay(200);
        }
    }

    /// <summary>CSS 선택자가 나타날 때까지 대기. 입력 후 버튼이 생기길 기다리는 용도.</summary>
    public async Task<string> AutomationWaitForSelectorAsync(string selector, int timeoutMs = 10000)
    {
        var js = $"document.querySelector({JsonSerializer.Serialize(selector)}) ? '1' : '0'";
        var deadline = Environment.TickCount64 + Math.Max(500, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            if (await AutomationEvalAsync(js) == "1") return "found";
            await Task.Delay(200);
        }
        throw new TimeoutException($"'{selector}' 가 {timeoutMs}ms 안에 나타나지 않았습니다.");
    }

    /// <summary>키 입력을 CDP(Input.dispatchKeyEvent)로 보낸다.
    /// <para>JS 로 만든 KeyboardEvent 는 untrusted 라 프레임워크/에디터가 무시하는 경우가 있다.
    /// CDP 는 브라우저 입력 파이프라인을 그대로 타므로 React 채팅창 Enter 전송 등에서 안정적이다.</para></summary>
    public async Task<string> AutomationPressKeyAsync(string key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        var core = await RequireCoreAsync();
        var (code, vk, text) = ResolveKey(key);
        int modifiers = (alt ? 1 : 0) | (ctrl ? 2 : 0) | (shift ? 8 : 0);

        var payload = new JsonObject
        {
            ["key"] = key,
            ["code"] = code,
            ["windowsVirtualKeyCode"] = vk,
            ["nativeVirtualKeyCode"] = vk,
            ["modifiers"] = modifiers,
        };
        // text 가 있는 키(문자·Enter)는 keyDown 에 실어야 실제 입력으로 처리된다.
        if (text != null && modifiers is 0 or 8) payload["text"] = text;

        async Task Dispatch(string type)
        {
            var p = (JsonObject)payload.DeepClone();
            p["type"] = type;
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", p.ToJsonString());
        }

        await Dispatch("keyDown");
        await Dispatch("keyUp");
        return "pressed " + key;
    }

    /// <summary>키 이름 → (code, Windows 가상키코드, 입력 텍스트). 모르는 키는 문자 1글자로 취급.</summary>
    private static (string Code, int Vk, string? Text) ResolveKey(string key) => key switch
    {
        "Enter" => ("Enter", 13, "\r"),
        "Tab" => ("Tab", 9, "\t"),
        "Escape" => ("Escape", 27, null),
        "Backspace" => ("Backspace", 8, null),
        "Delete" => ("Delete", 46, null),
        "ArrowUp" => ("ArrowUp", 38, null),
        "ArrowDown" => ("ArrowDown", 40, null),
        "ArrowLeft" => ("ArrowLeft", 37, null),
        "ArrowRight" => ("ArrowRight", 39, null),
        "Home" => ("Home", 36, null),
        "End" => ("End", 35, null),
        "PageUp" => ("PageUp", 33, null),
        "PageDown" => ("PageDown", 34, null),
        " " => ("Space", 32, " "),
        _ when key.Length == 1 => (
            char.IsLetter(key[0]) ? "Key" + char.ToUpperInvariant(key[0])
            : char.IsDigit(key[0]) ? "Digit" + key
            : "",
            char.ToUpperInvariant(key[0]),
            key),
        _ => throw new ArgumentException($"지원하지 않는 키: {key}"),
    };

    /// <summary>입력 요소에 값을 넣는다.
    /// <para>값 주입은 CDP <c>Input.insertText</c>(신뢰된 입력)로 한다 — React 처럼 value 를 제어하는
    /// 프레임워크는 JS 로 <c>el.value = ...</c> 만 하면 다음 렌더에서 되돌리므로 그대로는 안 먹힌다.
    /// insertText 가 통하지 않는 요소(구형 위젯 등)만 JS 대입으로 폴백한다.</para>
    /// <para>submit=true 는 폼 submit 대신 Enter 키(CDP)를 보낸다. 채팅 입력창처럼 폼이 없는 UI 가 많다.
    /// 다만 전송 <b>버튼</b>을 눌러야 하는 UI 라면 이 호출과 클릭을 반드시 나눠서 하라 — 입력 직후 같은
    /// 호출 안에서 버튼을 찾으면 아직 렌더 전이라 못 찾는다.</para></summary>
    public async Task<string> AutomationFillAsync(string selector, string value, bool submit)
    {
        var core = await RequireCoreAsync();
        var sel = JsonSerializer.Serialize(selector);

        // 포커스 + 기존 내용 전체 선택 → insertText 가 덮어쓰도록.
        var focused = await AutomationEvalAsync($$"""
            (() => {
              const el = document.querySelector({{sel}});
              if (!el) return 'NOTFOUND';
              el.scrollIntoView({ block: 'center' });
              el.focus();
              if (el.select) el.select();
              else if (el.isContentEditable) document.getSelection().selectAllChildren(el);
              return 'OK';
            })()
            """);
        if (focused == "NOTFOUND") throw new InvalidOperationException($"입력 대상을 찾지 못했습니다: {selector}");

        await core.CallDevToolsProtocolMethodAsync("Input.insertText",
            new JsonObject { ["text"] = value }.ToJsonString());

        // insertText 가 반영 안 된 경우(비표준 위젯)만 JS 대입 + 이벤트 발생으로 폴백.
        var current = await AutomationEvalAsync($$"""
            (() => {
              const el = document.querySelector({{sel}});
              if (!el) return '';
              return ('value' in el ? el.value : el.textContent) || '';
            })()
            """);
        if (current != value)
        {
            await AutomationEvalAsync($$"""
                (() => {
                  const el = document.querySelector({{sel}});
                  if (!el) return 'NOTFOUND';
                  const v = {{JsonSerializer.Serialize(value)}};
                  if ('value' in el) el.value = v; else el.textContent = v;
                  el.dispatchEvent(new Event('input', { bubbles: true }));
                  el.dispatchEvent(new Event('change', { bubbles: true }));
                  return 'OK';
                })()
                """);
        }

        if (submit) await AutomationPressKeyAsync("Enter");
        return "filled";
    }

    /// <summary>지정 텍스트가 본문에 나타날 때까지 폴링 대기(SPA/지연 로딩 대응).</summary>
    public async Task<string> AutomationWaitForTextAsync(string text, int timeoutMs = 15000)
    {
        var js = $"(document.body ? document.body.innerText : '').includes({JsonSerializer.Serialize(text)}) ? '1' : '0'";
        var deadline = Environment.TickCount64 + Math.Max(500, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            if (await AutomationEvalAsync(js) == "1") return "found";
            await Task.Delay(300);
        }
        throw new TimeoutException($"'{text}' 가 {timeoutMs}ms 안에 나타나지 않았습니다.");
    }

    /// <summary>임의 JS 실행. 결과는 문자열로 정규화(객체는 JSON 문자열).</summary>
    public async Task<string> AutomationEvalAsync(string script)
    {
        var core = await RequireCoreAsync();
        var raw = await core.ExecuteScriptAsync(script);
        if (string.IsNullOrEmpty(raw) || raw == "null") return "";
        try
        {
            var node = JsonNode.Parse(raw);
            return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";
        }
        catch { return raw; }
    }

    /// <summary>현재 화면 PNG 캡처(base64 로 브리지가 전달).</summary>
    public async Task<byte[]> AutomationCaptureAsync()
    {
        var core = await RequireCoreAsync();
        using var ms = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        return ms.ToArray();
    }

    public async Task<string> AutomationBackAsync()
    {
        var core = await RequireCoreAsync();
        if (_index <= 0) throw new InvalidOperationException("뒤로 갈 기록이 없습니다.");
        _index--;
        await NavigateAndWaitAsync(core, () => NavigateInternal(_history[_index], NavCause.History), 30000);
        PersistHistory();
        SyncToolbar();
        return core.Source;
    }

    public async Task<string> AutomationReloadAsync()
    {
        var core = await RequireCoreAsync();
        await NavigateAndWaitAsync(core, core.Reload, 30000);
        return core.Source;
    }

    private static async Task WaitOrTimeoutAsync(Task task, int timeoutMs, string what)
    {
        var done = await Task.WhenAny(task, Task.Delay(Math.Max(1000, timeoutMs)));
        if (done != task) throw new TimeoutException($"{what} 가 {timeoutMs}ms 안에 끝나지 않았습니다.");
        await task;
    }

    /// <summary>앱 종료 시 — WebView2 + 이벤트 해제(Edge 렌더러 프로세스 잔류 방지).</summary>
    public void DisposeAll()
    {
        try { PersistHistory(); } catch { }
        try { App.ThemeChanged -= _themeChangedHandler; } catch { }
        try { if (_view != null) BrowserContent.Children.Remove(_view); } catch { }
        try { _view?.Dispose(); } catch { }
        _view = null;
    }
}
