using System.IO;
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
public partial class BrowserHostView : UserControl, IAutomationBrowser
{
    /// <summary>브라우저 WebView가 실제 키보드 포커스를 받음. HwndHost라 부모 WPF 마우스 이벤트로는 관측할 수 없다.</summary>
    public event Action? NativeSurfaceFocused;

    private WebView2? _view;
    private bool _initStarted;
    private string? _pendingOpenUrl;

    private readonly Action<string> _themeChangedHandler;
    private readonly Action _browserThemeChangedHandler;

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
        _browserThemeChangedHandler = ApplyColorScheme;
        SettingsService.BrowserThemeChanged += _browserThemeChangedHandler;
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

    /// <summary>설정의 웹브라우저 테마("system"=앱 테마 따라감 / light / dark)를 WebView2 색 구성으로 변환.</summary>
    private static CoreWebView2PreferredColorScheme PreferredScheme => SettingsService.LoadBrowserTheme() switch
    {
        "light" => CoreWebView2PreferredColorScheme.Light,
        "dark"  => CoreWebView2PreferredColorScheme.Dark,
        _       => App.IsDarkTheme(App.CurrentTheme)
                       ? CoreWebView2PreferredColorScheme.Dark
                       : CoreWebView2PreferredColorScheme.Light,
    };

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
        NavigateInternal(BrowserAutomationEngine.ToNavigationTarget(input), NavCause.User);
        _view?.Focus();
        e.Handled = true;
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
    // 실제 동작은 공용 엔진(BrowserAutomationEngine)이 맡는다. 여기서는 이 탭의 가상 히스토리·탐색 원인
    // 표시처럼 호스트 고유 처리가 필요한 것만 감싼다.

    private BrowserAutomationEngine? _automation;

    private BrowserAutomationEngine Automation => _automation ??= new BrowserAutomationEngine(EnsureCoreAsync);

    /// <summary>URL(또는 검색어)로 이동하고 탐색 완료까지 대기. 반환값=최종 URL.</summary>
    public Task<string> AutomationNavigateAsync(string urlOrQuery, int timeoutMs = 30000)
        => Automation.NavigateAsync(urlOrQuery, timeoutMs, (core, target) =>
        {
            _pendingCause = NavCause.User;
            core.Navigate(target);
        });

    public Task<string> AutomationCurrentUrlAsync() => Automation.CurrentUrlAsync();

    public Task<string> AutomationTitleAsync() => Automation.TitleAsync();

    public Task<string> AutomationReadTextAsync(int maxChars = 20000) => Automation.ReadTextAsync(maxChars);

    public Task<string> AutomationLinksAsync(int max = 50) => Automation.LinksAsync(max);

    public Task<string> AutomationClickAsync(string selectorOrText, int timeoutMs = 3000)
        => Automation.ClickAsync(selectorOrText, timeoutMs);

    public Task<string> AutomationWaitForSelectorAsync(string selector, int timeoutMs = 10000)
        => Automation.WaitForSelectorAsync(selector, timeoutMs);

    public Task<string> AutomationPressKeyAsync(string key, bool ctrl = false, bool shift = false, bool alt = false)
        => Automation.PressKeyAsync(key, ctrl, shift, alt);

    public Task<string> AutomationFillAsync(string selector, string value, bool submit)
        => Automation.FillAsync(selector, value, submit);

    public Task<string> AutomationWaitForTextAsync(string text, int timeoutMs = 15000)
        => Automation.WaitForTextAsync(text, timeoutMs);

    public Task<string> AutomationEvalAsync(string script) => Automation.EvalAsync(script);

    public Task<byte[]> AutomationCaptureAsync() => Automation.CaptureAsync();

    public Task<string> AutomationReloadAsync() => Automation.ReloadAsync();

    /// <summary>뒤로 가기는 WebView2 자체 스택이 아니라 이 탭의 가상 히스토리를 따른다.</summary>
    public async Task<string> AutomationBackAsync()
    {
        var core = await Automation.RequireCoreAsync();
        if (_index <= 0) throw new InvalidOperationException("뒤로 갈 기록이 없습니다.");
        _index--;
        await BrowserAutomationEngine.NavigateAndWaitAsync(
            core, () => NavigateInternal(_history[_index], NavCause.History), 30000);
        PersistHistory();
        SyncToolbar();
        return core.Source;
    }

    /// <summary>앱 종료 시 — WebView2 + 이벤트 해제(Edge 렌더러 프로세스 잔류 방지).</summary>
    public void DisposeAll()
    {
        try { PersistHistory(); } catch { }
        try { App.ThemeChanged -= _themeChangedHandler; } catch { }
        try { SettingsService.BrowserThemeChanged -= _browserThemeChangedHandler; } catch { }
        try { if (_view != null) BrowserContent.Children.Remove(_view); } catch { }
        try { _view?.Dispose(); } catch { }
        _view = null;
    }
}
