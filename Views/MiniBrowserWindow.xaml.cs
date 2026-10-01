using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>
/// 메인 창 위에 겹쳐 띄우는 작은 브라우저 창. 상단에 내장 브라우저와 같은 모양의 툴바(뒤로/앞으로/새로고침/
/// 주소창/닫기)를 상시 표시하고, 툴바 빈 영역을 끌면 창이 이동한다. 여닫기는 타이틀바 버튼 토글과 F5.
/// Owner 를 메인 창으로 두어 메인 창 위에만 항상 표시된다.
///
/// 리사이즈: 툴바(WPF)는 표준 리사이즈 판정이 그대로 오고, 웹 화면(별도 HWND)이 가리는 좌·우·하단은
/// 웹 화면을 6px 안으로 들여 창 테두리 여백을 노출한다. 그 여백은 현재 페이지 배경색으로 칠해 이음새가
/// 보이지 않게 한다(이동/로드마다 갱신).
///
/// 닫기는 창을 없애지 않고 숨기기다 — 앱이 살아 있는 동안 인스턴스를 유지해 다시 열 때 페이지를
/// 새로 로드하지 않는다. 앱을 껐다 켜면 설정의 시작 주소로 새로 연다.
/// 위치·크기는 설정에 저장해 다음 실행에도 복원한다.
/// </summary>
public partial class MiniBrowserWindow : Window, IAutomationBrowser
{
    /// <summary>DWM 창 코너 반경(고정 8).</summary>
    private const double CornerRadiusDip = 8;

    /// <summary>미니 창은 좁아서 웹 화면을 조금 축소해 연다.</summary>
    private const double InitialZoomFactor = 0.9;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    // 화면 가장자리로 끌었을 때 Windows 가 창을 반쪽/최대로 확장(스냅)하는 것을 막는다.
    // 스냅은 최대화 가능한 창에만 걸리므로 WS_MAXIMIZEBOX 비트를 떼면 비활성된다.
    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZEBOX = 0x00010000;

    /// <summary>웹 화면에서 F5 를 눌렀을 때 호스트로 보내는 신호.</summary>
    private const string ReloadMessage = "devez:reload";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static MiniBrowserWindow? _instance;

    /// <summary>표시/숨김이 바뀔 때 — 타이틀바 버튼 색을 갱신하는 쪽에서 구독한다.</summary>
    public static event Action? OpenStateChanged;

    /// <summary>미니 창이 화면에 떠 있는지.</summary>
    public static bool IsOpen => _instance is { IsVisible: true };

    /// <summary>사용자가 닫기(숨기기)를 눌렀는지. 소유 창 최소화→복원 때 다시 나타나는 것을 막는 데 쓴다.</summary>
    private bool _hiddenByUser;
    private bool _capturingOffscreen;   // 숨긴 창을 화면 밖에 잠깐 띄워 캡처하는 중

    /// <summary>설정·MCP 오버레이 때문에 임시로 숨긴 상태인지(오버레이가 닫히면 되돌린다).</summary>
    private bool _hiddenForOverlay;

    private readonly WebView2 _view = new();
    private readonly Action<string> _themeChangedHandler;
    private readonly Action _browserThemeChangedHandler;
    private readonly SolidColorBrush _chromeBrush = new(Colors.White);   // 웹 화면 여백 = 페이지 배경색
    private bool _coreReady;
    private string? _pendingUrl;

    /// <summary>코어 준비 완료(또는 시작 실패) 신호. 세션 자동화가 초기화를 기다리는 데 쓴다.</summary>
    private readonly TaskCompletionSource<CoreWebView2?> _coreTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BrowserAutomationEngine? _automation;

    private BrowserAutomationEngine Automation => _automation ??= new BrowserAutomationEngine(() => _coreTcs.Task);

    /// <summary>설정/MCP 오버레이가 열릴 때 — 같은 화면을 가리지 않도록 잠시 숨긴다.</summary>
    public static void HideForOverlay()
    {
        if (_instance is not { IsVisible: true } win) return;
        win.SavePlacement();
        win._hiddenForOverlay = true;
        win.Hide();
        OpenStateChanged?.Invoke();
    }

    /// <summary>오버레이가 닫힐 때 — 그때 숨긴 창만 다시 보여준다(사용자가 닫아 둔 창은 그대로).
    /// 숨겨진 동안 바뀐 설정(브라우저 테마 등)을 못 받았을 수 있으니 다시 적용한다.</summary>
    public static void RestoreAfterOverlay()
    {
        if (_instance is not { IsLoaded: true } win || !win._hiddenForOverlay) return;
        win._hiddenForOverlay = false;
        if (!win._hiddenByUser) win.Show();
        win.ApplyColorScheme();
        OpenStateChanged?.Invoke();
    }

    /// <summary>앱 종료 시작 시 — 미니 창을 실제로 닫는다(종료 오버레이 위를 덮지 않도록 가장 먼저).</summary>
    public static void CloseForShutdown()
    {
        var win = _instance;
        _instance = null;
        try { win?.Close(); } catch { }
    }

    /// <summary>타이틀바 버튼용 — 떠 있으면 숨기고, 아니면 보여준다.</summary>
    public static void Toggle(Window? owner)
    {
        if (_instance is { IsVisible: true } open) { open.HideForLater(); return; }
        ShowOrActivate(owner);
    }

    /// <summary>숨겨져 있던 창은 그대로 다시 보여주고(페이지 재로드 없음), 없으면 새로 띄운다.</summary>
    public static void ShowOrActivate(Window? owner, string? url = null)
    {
        if (_instance is { IsLoaded: true } win)
        {
            win._hiddenByUser = false;
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            if (!win.IsVisible) win.Show();
            win.Activate();
            win.Navigate(url);
            OpenStateChanged?.Invoke();
            return;
        }

        try
        {
            CreateAndShow(owner).Navigate(url);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Alert("미니 브라우저", "미니 브라우저를 열 수 없습니다.\n\n" + ex.Message);
        }
    }

    /// <summary>새 인스턴스를 만들어 띄운다. 실패는 호출자에게 그대로 올린다
    /// (자동화 경로에서는 모달 알림을 띄우면 UI 스레드가 막혀 세션 명령이 매달린다).</summary>
    private static MiniBrowserWindow CreateAndShow(Window? owner)
    {
        try
        {
            var created = new MiniBrowserWindow { Owner = owner };
            _instance = created;
            created.Show();
            OpenStateChanged?.Invoke();
            return created;
        }
        catch
        {
            _instance = null;
            throw;
        }
    }

    public MiniBrowserWindow()
    {
        InitializeComponent();

        Chrome.CornerRadius = new CornerRadius(CornerRadiusDip);
        // 여백 배경 = 페이지 배경색. 초기값은 앱 배경색.
        if (Application.Current.TryFindResource("BgBrush") is SolidColorBrush bg)
            _chromeBrush.Color = bg.Color;
        Chrome.Background = _chromeBrush;
        _view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            0xFF, _chromeBrush.Color.R, _chromeBrush.Color.G, _chromeBrush.Color.B);
        BrowserHost.Children.Add(_view);

        _themeChangedHandler = _ => ApplyColorScheme();
        App.ThemeChanged += _themeChangedHandler;
        _browserThemeChangedHandler = ApplyColorScheme;
        SettingsService.BrowserThemeChanged += _browserThemeChangedHandler;

        Root.SizeChanged += (_, _) => ApplyRoundedClip();
        Activated += (_, _) =>
        {
            if (_coreReady) RefreshPageBackground();   // 창 전환으로 다시 앞에 오면 색 재확인
            UpdateFitState();
        };
        // 메인 창에서 패널을 여닫아도 미니 창은 알림을 못 받으므로, 툴바에 마우스가 들어올 때 다시 판정한다.
        Toolbar.MouseEnter += (_, _) => UpdateFitState();
        Loaded += OnLoadedFirst;
        // 소유 창을 최소화했다 복원하면 WPF 가 소유 창들을 함께 되살린다 — 사용자가 숨긴 창은 계속 숨긴다.
        IsVisibleChanged += (_, _) => { if (_hiddenByUser && IsVisible && !_capturingOffscreen) Hide(); };
    }

    private void OnLoadedFirst(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedFirst;
        ApplyRoundedClip();
        UpdateFitState();
        _ = StartBrowserAsync();
    }

    /// <summary>WPF 자식(툴바 등)의 사각 모서리가 라운드 밖으로 삐져나오지 않게 클립.
    /// 웹 화면은 별도 HWND 라 이 클립을 타지 않고 DWM 창 코너가 잘라준다.</summary>
    private void ApplyRoundedClip()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = Math.Max(0, CornerRadiusDip - 1);
        Root.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RestorePlacement();
        DisableEdgeSnap();

        // DWM 이 창 자체를 라운드로 깎아 자식 HWND(웹 화면)까지 매끄럽게 잘린다.
        try
        {
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { }
    }

    /// <summary>가장자리 스냅 차단. 리사이즈(WS_THICKFRAME)는 그대로 두고 최대화 비트만 뗀다.</summary>
    private void DisableEdgeSnap()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int style = GetWindowLong(hwnd, GWL_STYLE);
            if ((style & WS_MAXIMIZEBOX) != 0)
                SetWindowLong(hwnd, GWL_STYLE, style & ~WS_MAXIMIZEBOX);
        }
        catch { }
    }

    private async Task StartBrowserAsync()
    {
        try
        {
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevezCode", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _view.EnsureCoreWebView2Async(env);

            var core = _view.CoreWebView2;
            _coreReady = true;
            ApplyColorScheme();
            _view.ZoomFactor = InitialZoomFactor;

            // F5·Ctrl+R 은 아래 주입 스크립트로 단독 처리한다. 브라우저 기본 가속키를 켜 두면 같은 키를
            // 브라우저도 처리해 두 번 새로고침되므로 끈다.
            try { core.Settings.AreBrowserAcceleratorKeysEnabled = false; } catch { }
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('keydown',function(e){" +
                "if(e.key==='F5'||(e.ctrlKey&&(e.key==='r'||e.key==='R'))){" +
                "e.preventDefault();" +
                "try{window.chrome.webview.postMessage('" + ReloadMessage + "');}catch(_){}" +
                "}},true);");
            core.WebMessageReceived += (_, args) =>
            {
                string? msg = null;
                try { msg = args.TryGetWebMessageAsString(); } catch { }
                if (msg == ReloadMessage) ReloadPage();
            };

            // 새 창 요청은 같은 뷰에서 열기(팝업 창 대신 인라인 이동)
            core.NewWindowRequested += (_, args) => { args.Handled = true; NavigateCore(args.Uri); };
            // 페이지가 그려진 뒤 배경색을 읽어 여백을 같은 색으로 칠한다.
            core.DOMContentLoaded += (_, _) => RefreshPageBackground();
            core.NavigationCompleted += (_, _) => RefreshPageBackground();

            // 주소창·뒤로/앞으로 상태를 실제 이동에 맞춰 갱신한다.
            core.SourceChanged += (_, _) => UpdateNavState();
            core.HistoryChanged += (_, _) => UpdateNavState();
            UpdateNavState();

            NavigateCore(_pendingUrl ?? SettingsService.LoadBrowserHomeUrl());
            _pendingUrl = null;
            // 초기 탐색을 건 뒤에 알린다 — 자동화 명령이 홈 로드와 겹쳐 엉뚱한 URL 을 잡지 않게.
            _coreTcs.TrySetResult(core);
        }
        catch (Exception ex)
        {
            _coreTcs.TrySetResult(null);
            BrowserHost.Children.Clear();
            BrowserHost.Children.Add(new TextBlock
            {
                Text = "브라우저를 시작할 수 없습니다.\nWebView2 런타임이 필요합니다.\n\n" + ex.Message,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
            });
        }
    }

    private int _bgSampleToken;

    /// <summary>페이지 배경색을 로드 직후부터 잠시 동안 여러 번 다시 읽는다.
    /// claude.ai 같은 SPA 는 NavigationCompleted 뒤에도 한동안 실제 배경을 그리므로, 한 번만 읽으면
    /// 이른(placeholder) 색을 잡는다. 마지막 값이 이기게 하고, 더 최근 요청이 오면 이전 루프는 멈춘다.</summary>
    private async void RefreshPageBackground()
    {
        int token = ++_bgSampleToken;
        int[] delays = { 0, 200, 500, 1000, 1800 };
        foreach (var d in delays)
        {
            if (d > 0) await Task.Delay(d);
            if (token != _bgSampleToken) return;
            await UpdateBackgroundFromPageAsync();
        }
    }

    /// <summary>현재 페이지 배경색을 읽어 웹 화면 여백(Chrome 배경)에 칠한다.</summary>
    private async Task UpdateBackgroundFromPageAsync()
    {
        var core = _view.CoreWebView2;
        if (core == null) return;
        try
        {
            // 화면 가장자리에 '실제로 보이는' 색을 쓴다. body/html 배경색은 안쪽 컨테이너가 색을
            // 칠하는 사이트(claude.ai 등)에서 엉뚱한 값이 나온다. 모서리 지점의 요소에서 위로 올라가며
            // 처음 만나는 불투명 배경색을 뽑는다.
            var json = await core.ExecuteScriptAsync(
                "(function(){" +
                "function op(x,y){var el=document.elementFromPoint(x,y);" +
                "while(el){var c=getComputedStyle(el).backgroundColor;" +
                "var m=c&&c.match(/[\\d.]+/g);" +
                "if(m&&(m.length<4||parseFloat(m[3])>=0.5))return c;" +
                "el=el.parentElement;}return null;}" +
                "var w=innerWidth,h=innerHeight;" +
                // 오른쪽은 스크롤바라 피하고, 좌·중앙·하단 가장자리에서 실제 배경을 뽑는다.
                "return op(2,Math.floor(h/2))||op(Math.floor(w/2),2)||op(Math.floor(w/2),h-2)||op(2,2)||" +
                "getComputedStyle(document.documentElement).backgroundColor||" +
                "getComputedStyle(document.body).backgroundColor;})()");
            if (TryParseCssColor(json, out var color)) ApplyChromeColor(color);
        }
        catch { /* 페이지 접근 제한 등 — 기존 색 유지 */ }
    }

    /// <summary>ExecuteScriptAsync 가 돌려준 JSON 문자열(예: "\"rgb(10, 61, 145)\"")을 Color 로 파싱.</summary>
    private static bool TryParseCssColor(string? json, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrEmpty(json)) return false;
        var s = json.Trim('"').Trim();
        int open = s.IndexOf('('), close = s.IndexOf(')');
        if (open < 0 || close <= open) return false;

        var parts = s[(open + 1)..close].Split(',');
        if (parts.Length < 3) return false;
        if (!byte.TryParse(parts[0].Trim(), out var r)) return false;
        if (!byte.TryParse(parts[1].Trim(), out var g)) return false;
        if (!byte.TryParse(parts[2].Trim(), out var b)) return false;
        color = Color.FromRgb(r, g, b);
        return true;
    }

    private void ApplyChromeColor(Color color)
    {
        _chromeBrush.Color = color;
        try { _view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, color.R, color.G, color.B); }
        catch { }
    }

    /// <summary>외부 요청 주소로 이동. 코어 준비 전이면 준비 후 열도록 보류한다.</summary>
    public void Navigate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!_coreReady) { _pendingUrl = url; return; }
        NavigateCore(url);
    }

    private void NavigateCore(string url)
    {
        try { _view.CoreWebView2?.Navigate(url); } catch { }
    }

    private void ReloadPage()
    {
        try { _view.CoreWebView2?.Reload(); } catch { }
    }

    /// <summary>설정의 웹브라우저 테마("system"=앱 테마 따라감 / light / dark)를 WebView2 색 구성으로 변환.
    /// 브라우저 탭과 같은 규칙을 쓴다.</summary>
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
        try
        {
            if (_view.CoreWebView2 is { } core)
            {
                core.Profile.PreferredColorScheme = PreferredScheme;
                RefreshPageBackground();   // 테마가 바뀌면 배경색도 다시 읽는다
            }
        }
        catch { /* 해제 중 등 */ }
    }

    /// <summary>WPF 쪽(툴바 등)에 포커스가 있을 때의 F5 는 창 닫기(숨기기).
    /// 웹 화면에 포커스가 있으면 주입 스크립트가 먼저 잡아 페이지 새로고침으로 처리한다.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.F5) return;
        HideForLater();
        e.Handled = true;
    }

    // ── 툴바: 창 이동 + 버튼/주소창 ─────────────────────────────────────
    /// <summary>툴바 빈 영역을 끌면 창을 옮긴다. 버튼·주소창 위에서는 각자 동작에 맡긴다.</summary>
    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        // 툴바 자신 또는 DockPanel 여백에서만 이동(버튼·주소창은 별도 요소라 제외).
        if (ReferenceEquals(e.OriginalSource, Toolbar) || e.OriginalSource is DockPanel)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        try { if (_view.CoreWebView2 is { CanGoBack: true } c) c.GoBack(); } catch { }
    }

    private void ForwardBtn_Click(object sender, RoutedEventArgs e)
    {
        try { if (_view.CoreWebView2 is { CanGoForward: true } c) c.GoForward(); } catch { }
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => ReloadPage();

    // ── 화면 맞추기: 메인 창의 오른쪽 서브 패널 영역을 그대로 덮는다 ──────────
    private MainWindow? MainHost =>
        (Owner as MainWindow) ?? (Application.Current?.MainWindow as MainWindow);

    /// <summary>오른쪽 서브 패널이 하나라도 펼쳐져 있을 때만 맞추기 버튼을 켠다.</summary>
    private void UpdateFitState()
    {
        var host = MainHost;
        FitBtn.IsEnabled = host != null && host.TryGetRightPanelArea(out _);
    }

    private void FitBtn_Click(object sender, RoutedEventArgs e)
    {
        var host = MainHost;
        if (host == null || !host.TryGetRightPanelArea(out var area)) { UpdateFitState(); return; }

        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        Left = area.Left;
        Top = area.Top;
        Width = Math.Max(MinWidth, area.Width);
        Height = Math.Max(MinHeight, area.Height);
        SavePlacement();
    }

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
        NavigateCore(BrowserAutomationEngine.ToNavigationTarget(input));
        _view.Focus();
        e.Handled = true;
    }

    // ── 세션 자동화(MCP 브라우저 도구) API ──────────────────────────
    // 실제 동작은 탭 브라우저와 같은 공용 엔진이 맡는다. 미니 창은 가상 히스토리가 없어 뒤로 가기도
    // WebView2 자체 스택을 그대로 쓴다.

    /// <summary>자동화 명령을 받을 미니 브라우저를 준비한다. 사용자가 숨겨 둔 창은 숨긴 채로 쓴다.
    /// <para>창을 앞으로 끌어오지는 않는다(Activate 생략) — 세션 명령 때문에 사용자가 보던 창의
    /// 포커스를 뺏지 않기 위해서다.</para></summary>
    public static async Task<MiniBrowserWindow> EnsureForAutomationAsync(Window? owner)
    {
        var win = _instance;
        if (win is { IsLoaded: true })
        {
            // 설정·MCP 오버레이가 열려 잠시 숨긴 창은 자동화가 다시 띄우지 않는다 — 오버레이를 가린다.
            if (win._hiddenForOverlay)
                throw new InvalidOperationException(
                    "설정 창이 열려 있는 동안에는 미니 브라우저를 쓸 수 없습니다. 설정을 닫고 다시 시도하세요.");
        }
        else win = CreateAndShow(owner);

        if (await win._coreTcs.Task == null)
            throw new InvalidOperationException("미니 브라우저의 WebView2 를 시작할 수 없습니다(런타임 미설치 가능).");
        return win;
    }

    public Task<string> AutomationNavigateAsync(string urlOrQuery, int timeoutMs = 30000)
        => Automation.NavigateAsync(urlOrQuery, timeoutMs);

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

    /// <summary>숨기거나 최소화한 창은 캡처가 끝나지 않는다. 숨긴 창은 화면 밖 좌표에 포커스 없이
    /// 잠깐 띄워 찍고 다시 숨긴다 — 사용자 눈에는 창이 나타나지 않는다.</summary>
    public async Task<byte[]> AutomationCaptureAsync()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (IsVisible) return await Automation.CaptureAsync();

        var (left, top) = (Left, Top);
        _capturingOffscreen = true;
        ShowActivated = false;
        try
        {
            Left = SystemParameters.VirtualScreenLeft - ActualWidth - 1000;
            Top = SystemParameters.VirtualScreenTop - ActualHeight - 1000;
            Show();
            return await Automation.CaptureAsync();
        }
        finally
        {
            // 캡처 도중 사용자가 창을 다시 열었으면 숨기지 않고 원래 위치로만 되돌린다.
            if (_hiddenByUser || _hiddenForOverlay) Hide();
            Left = left;
            Top = top;
            ShowActivated = true;
            _capturingOffscreen = false;
        }
    }

    public Task<string> AutomationBackAsync() => Automation.GoBackAsync();

    public Task<string> AutomationReloadAsync() => Automation.ReloadAsync();

    /// <summary>주소 텍스트와 뒤로/앞으로 버튼 활성 상태를 현재 이동 위치에 맞춘다.
    /// 사용자가 주소창을 편집 중이면 입력을 덮지 않는다.</summary>
    private void UpdateNavState()
    {
        var core = _view.CoreWebView2;
        if (core == null) return;
        BackBtn.IsEnabled = core.CanGoBack;
        ForwardBtn.IsEnabled = core.CanGoForward;
        if (!AddressBox.IsKeyboardFocusWithin)
            AddressBox.Text = core.Source ?? "";
    }

    /// <summary>저장된 위치·크기 복원. 화면 밖이면(모니터 제거 등) 소유 창 기준으로 배치.</summary>
    private void RestorePlacement()
    {
        var (left, top, width, height) = SettingsService.LoadMiniBrowserPlacement();
        if (width is > 0) Width = Math.Max(MinWidth, width.Value);
        if (height is > 0) Height = Math.Max(MinHeight, height.Value);

        if (left is { } l && top is { } t && IsOnScreen(l, t, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
            return;
        }

        // 저장값이 없거나 화면 밖 — 소유 창 우측 안쪽에 배치.
        if (Owner is { } owner && owner.WindowState == WindowState.Normal &&
            IsOnScreen(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left + Math.Max(0, owner.ActualWidth - Width - 40);
            Top = owner.Top + 80;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    /// <summary>창 사각형이 가상 화면과 충분히 겹치지 않으면 false(모니터 제거·해상도 축소 → 복원 취소).</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        if (double.IsNaN(left) || double.IsInfinity(left) ||
            double.IsNaN(top) || double.IsInfinity(top)) return false;

        var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var hit = Rect.Intersect(virt, new Rect(left, top, width, height));
        return !hit.IsEmpty && hit.Width >= 100 && hit.Height >= 60;
    }

    private void SavePlacement()
    {
        if (_capturingOffscreen || WindowState != WindowState.Normal || Width <= 0 || Height <= 0) return;
        try { SettingsService.SaveMiniBrowserPlacement(Left, Top, Width, Height); } catch { }
    }

    /// <summary>닫기 = 숨기기. 인스턴스와 로드된 페이지를 그대로 두어 다시 열 때 재로드하지 않는다.</summary>
    private void HideForLater()
    {
        SavePlacement();
        _hiddenByUser = true;
        Hide();
        OpenStateChanged?.Invoke();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => HideForLater();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        SavePlacement();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _coreTcs.TrySetResult(null);   // 코어를 기다리던 자동화 호출이 매달리지 않게
        if (ReferenceEquals(_instance, this)) _instance = null;
        App.ThemeChanged -= _themeChangedHandler;
        try { SettingsService.BrowserThemeChanged -= _browserThemeChangedHandler; } catch { }
        try { _view.Dispose(); } catch { }
        OpenStateChanged?.Invoke();
    }
}
