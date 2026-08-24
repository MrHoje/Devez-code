using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>
/// 메인 창 위에 겹쳐 띄우는 작은 브라우저 창. 컨트롤박스(창 이동 띠)는 평소 높이 0 으로 접혀 보이지
/// 않고, 커서가 창 위쪽에 들어오면 펼쳐진다. 그 띠를 끌면 창이 이동한다. 여닫기는 타이틀바 버튼 토글만.
/// Owner 를 메인 창으로 두어 메인 창 위에만 항상 표시된다.
///
/// 리사이즈: 웹 화면(별도 HWND)이 창 테두리를 가려 표준 리사이즈 판정이 오지 않으므로, 웹 화면을
/// 4px 안으로 들여 창 테두리 여백을 노출한다. 그 여백에서 WindowChrome 표준 리사이즈가 동작하고,
/// 여백은 현재 페이지 배경색으로 칠해 이음새가 보이지 않게 한다(이동/로드마다 갱신).
///
/// 닫기는 창을 없애지 않고 숨기기다 — 앱이 살아 있는 동안 인스턴스를 유지해 다시 열 때 페이지를
/// 새로 로드하지 않는다. 앱을 껐다 켜면 설정의 시작 주소로 새로 연다.
/// 위치·크기는 설정에 저장해 다음 실행에도 복원한다.
/// </summary>
public partial class MiniBrowserWindow : Window
{
    /// <summary>DWM 창 코너 반경(고정 8).</summary>
    private const double CornerRadiusDip = 8;

    /// <summary>미니 창은 좁아서 웹 화면을 조금 축소해 연다.</summary>
    private const double InitialZoomFactor = 0.8;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private static MiniBrowserWindow? _instance;

    /// <summary>표시/숨김이 바뀔 때 — 타이틀바 버튼 색을 갱신하는 쪽에서 구독한다.</summary>
    public static event Action? OpenStateChanged;

    /// <summary>미니 창이 화면에 떠 있는지.</summary>
    public static bool IsOpen => _instance is { IsVisible: true };

    /// <summary>사용자가 닫기(숨기기)를 눌렀는지. 소유 창 최소화→복원 때 다시 나타나는 것을 막는 데 쓴다.</summary>
    private bool _hiddenByUser;

    /// <summary>설정·MCP 오버레이 때문에 임시로 숨긴 상태인지(오버레이가 닫히면 되돌린다).</summary>
    private bool _hiddenForOverlay;

    private readonly WebView2 _view = new();
    private readonly Action<string> _themeChangedHandler;
    private readonly Action _browserThemeChangedHandler;
    private readonly SolidColorBrush _chromeBrush = new(Colors.White);
    private bool _coreReady;
    private string? _pendingUrl;

    /// <summary>설정/MCP 오버레이가 열릴 때 — 같은 화면을 가리지 않도록 잠시 숨긴다.</summary>
    public static void HideForOverlay()
    {
        if (_instance is not { IsVisible: true } win) return;
        win.SavePlacement();
        win._hiddenForOverlay = true;
        win.Hide();
        OpenStateChanged?.Invoke();
    }

    /// <summary>오버레이가 닫힐 때 — 그때 숨긴 창만 다시 보여준다(사용자가 닫아 둔 창은 그대로).</summary>
    public static void RestoreAfterOverlay()
    {
        if (_instance is not { IsLoaded: true } win || !win._hiddenForOverlay) return;
        win._hiddenForOverlay = false;
        if (!win._hiddenByUser) win.Show();
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
            var created = new MiniBrowserWindow { Owner = owner };
            _instance = created;
            created.Show();
            created.Navigate(url);
            OpenStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _instance = null;
            ConfirmDialog.Alert("미니 브라우저", "미니 브라우저를 열 수 없습니다.\n\n" + ex.Message);
        }
    }

    /// <summary>설정에서 시작 주소를 바꿨을 때 — 열려 있는 미니 창을 새 주소로 즉시 이동시킨다.</summary>
    public static void ApplyHomeUrlToOpenWindow()
    {
        if (_instance is { IsLoaded: true } win)
            win.Navigate(SettingsService.LoadMiniBrowserHomeUrl());
    }

    public MiniBrowserWindow()
    {
        InitializeComponent();

        Chrome.CornerRadius = new CornerRadius(CornerRadiusDip);
        // 여백(4px) 배경 = 페이지 배경색. 초기값은 앱 배경색.
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

        Root.SizeChanged += (_, _) => { ApplyRoundedClip(); SyncHeaderBar(); };
        LocationChanged += (_, _) => SyncHeaderBar();
        Deactivated += (_, _) => SetHeaderShown(false);   // 팝업은 별도 창이라 창이 뒤로 가면 직접 내린다
        Loaded += OnLoadedFirst;
        // 소유 창을 최소화했다 복원하면 WPF 가 소유 창들을 함께 되살린다 — 사용자가 숨긴 창은 계속 숨긴다.
        IsVisibleChanged += (_, _) =>
        {
            if (_hiddenByUser && IsVisible) { Hide(); return; }
            if (IsVisible) StartHoverWatch(); else StopHoverWatch();
        };
    }

    private void OnLoadedFirst(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedFirst;
        ApplyRoundedClip();
        _ = StartBrowserAsync();
    }

    /// <summary>컨트롤박스 등 WPF 자식의 사각 모서리가 라운드 밖으로 삐져나오지 않게 클립.
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
            core.DOMContentLoaded += (_, _) => _ = UpdateBackgroundFromPageAsync();
            core.NavigationCompleted += (_, _) => _ = UpdateBackgroundFromPageAsync();

            NavigateCore(_pendingUrl ?? SettingsService.LoadMiniBrowserHomeUrl());
            _pendingUrl = null;
        }
        catch (Exception ex)
        {
            BrowserHost.Children.Clear();
            BrowserHost.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "브라우저를 시작할 수 없습니다.\nWebView2 런타임이 필요합니다.\n\n" + ex.Message,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
            });
        }
    }

    /// <summary>현재 페이지 body/html 배경색을 읽어 4px 여백(Chrome 배경)에 칠한다.</summary>
    private async Task UpdateBackgroundFromPageAsync()
    {
        var core = _view.CoreWebView2;
        if (core == null) return;
        try
        {
            // body 가 투명이면 html 을 본다. rgb/rgba 문자열을 그대로 받는다.
            var json = await core.ExecuteScriptAsync(
                "(function(){var b=getComputedStyle(document.body).backgroundColor;" +
                "if(!b||b==='rgba(0, 0, 0, 0)'||b==='transparent')" +
                "b=getComputedStyle(document.documentElement).backgroundColor;return b;})()");
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
                _ = UpdateBackgroundFromPageAsync();   // 테마가 바뀌면 배경색도 다시 읽는다
            }
        }
        catch { /* 해제 중 등 */ }
    }

    /// <summary>WPF 쪽(컨트롤박스 등)에 포커스가 있을 때의 F5 는 창 닫기(숨기기).
    /// 웹 화면에 포커스가 있으면 주입 스크립트가 먼저 잡아 페이지 새로고침으로 처리한다.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.F5) return;
        HideForLater();
        e.Handled = true;
    }

    // ── 컨트롤박스 펼침/접힘 ──────────────────────────────────────────
    /// <summary>펼쳤을 때 컨트롤박스 높이.</summary>
    private const double HeaderHeight = 22;

    /// <summary>커서가 창 상단 이 범위에 들어오면 펼친다(컨트롤박스 높이와 같게).</summary>
    private const double HoverEnterZone = HeaderHeight;

    /// <summary>펼친 뒤에는 이 범위를 벗어나야 접는다(경계에서 깜빡이는 것 방지).</summary>
    private const double HoverExitZone = HeaderHeight + 13;

    private DispatcherTimer? _hoverTimer;
    private bool _headerShown;

    private void StartHoverWatch()
    {
        if (_hoverTimer != null) { _hoverTimer.Start(); return; }
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
        _hoverTimer.Tick += (_, _) => UpdateHeaderByCursor();
        _hoverTimer.Start();
    }

    private void StopHoverWatch()
    {
        _hoverTimer?.Stop();
        SetHeaderShown(false);
    }

    /// <summary>커서가 창 위쪽에 있으면 컨트롤박스를 펼치고, 벗어나면 접는다.</summary>
    private void UpdateHeaderByCursor()
    {
        if (!IsVisible) { SetHeaderShown(false); return; }
        if (!GetCursorPos(out var cur)) return;

        Point p;
        try { p = PointFromScreen(new Point(cur.X, cur.Y)); }
        catch { return; }   // 핸들 정리 중

        bool insideX = p.X >= 0 && p.X <= ActualWidth;
        double limit = _headerShown ? HoverExitZone : HoverEnterZone;
        SetHeaderShown(insideX && p.Y >= 0 && p.Y <= limit);
    }

    private void SetHeaderShown(bool show)
    {
        if (_headerShown == show) return;
        _headerShown = show;

        if (show)
        {
            SyncHeaderBar();
            HeaderPopup.IsOpen = true;
        }

        var slide = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? HeaderHeight : 0,
            Duration = TimeSpan.FromMilliseconds(show ? 150 : 120),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = show ? System.Windows.Media.Animation.EasingMode.EaseOut
                                  : System.Windows.Media.Animation.EasingMode.EaseIn,
            },
        };
        if (!show) slide.Completed += (_, _) => { if (!_headerShown) HeaderPopup.IsOpen = false; };
        HeaderBar.BeginAnimation(HeightProperty, slide);
    }

    /// <summary>창이 움직이거나 크기가 바뀌면 팝업 위치를 다시 잡는다
    /// (팝업은 별도 창이라 부모가 이동해도 스스로 따라오지 않는다. 폭은 XAML 에서 창 폭에 묶여 있다).</summary>
    private void SyncHeaderBar()
    {
        if (!HeaderPopup.IsOpen) return;
        HeaderPopup.HorizontalOffset += 1;
        HeaderPopup.HorizontalOffset -= 1;
    }

    // ── 컨트롤박스 드래그로 창 이동 ────────────────────────────────────
    // 팝업은 별도 HWND 라 Window.DragMove() 가 통하지 않는다. 커서 이동량을 창 위치에 직접 반영한다.
    private NativePoint? _dragOrigin;
    private Point _windowOrigin;

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!GetCursorPos(out var cur)) return;
        _dragOrigin = cur;
        _windowOrigin = new Point(Left, Top);
        HeaderBar.CaptureMouse();
    }

    private void HeaderBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOrigin is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
        if (!GetCursorPos(out var cur)) return;

        var delta = new Vector(cur.X - origin.X, cur.Y - origin.Y);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            delta = target.TransformFromDevice.Transform(delta);   // 화면 픽셀 → DIP

        Left = _windowOrigin.X + delta.X;
        Top = _windowOrigin.Y + delta.Y;
    }

    private void HeaderBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragOrigin == null) return;
        _dragOrigin = null;
        HeaderBar.ReleaseMouseCapture();
        SavePlacement();
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
        if (WindowState != WindowState.Normal || Width <= 0 || Height <= 0) return;
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
        if (ReferenceEquals(_instance, this)) _instance = null;
        App.ThemeChanged -= _themeChangedHandler;
        try { SettingsService.BrowserThemeChanged -= _browserThemeChangedHandler; } catch { }
        _hoverTimer?.Stop();
        HeaderPopup.IsOpen = false;
        try { _view.Dispose(); } catch { }
        OpenStateChanged?.Invoke();
    }
}
