using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>
/// 메인 창 위에 겹쳐 띄우는 작은 브라우저 창. 웹 화면이 창 전체를 채우고, 상단 26px 은 창 이동·닫기용
/// 투명 오버레이 띠다 — 컨트롤 영역이 따로 보이지 않고 마우스를 올릴 때만 닫기 버튼이 나타난다.
/// (그 띠는 웹 화면 위에 겹치므로 최상단 26px 의 웹 클릭은 창 쪽으로 간다.)
/// Owner 를 메인 창으로 두어 메인 창 위에만 항상 표시된다.
///
/// 닫기는 창을 없애지 않고 숨기기다 — 앱이 살아 있는 동안 인스턴스를 유지해 다시 열 때 페이지를
/// 새로 로드하지 않는다. 앱을 껐다 켜면 설정의 시작 주소로 새로 연다.
/// 위치·크기는 설정에 저장해 다음 실행에도 복원한다.
///
/// 라운드 코너는 렌더 모드에 따라 두 경로로 갈린다.
/// - GPU 렌더: 투명 창 + WebView2CompositionControl(WPF 렌더 경로) → 큰 반경도 WPF 안티앨리어싱으로
///   매끄럽게 잘린다. 웹 화면까지 같은 클립을 탄다.
/// - 소프트웨어 렌더(GPU 끔·원격 접속): 합성 표면이 화면에 나오지 않아 웹 화면이 통째로 안 보인다.
///   그래서 불투명 창 + 일반 WebView2 로 두고 DWM 창 코너로 깎는다. 반경은 8 고정이지만 매끄럽다.
///   (창 리전으로 큰 반경을 깎는 방법은 이진 마스크라 모서리에 계단이 생겨 쓰지 않는다.)
/// </summary>
public partial class MiniBrowserWindow : Window
{
    /// <summary>GPU 렌더에서 쓰는 라운드 반경. 참고한 ChatGPT 미니 창 스크린샷의 코너를 픽셀로 재
    /// 반경 25(배율 100%)로 맞춘 값. 소프트웨어 렌더는 DWM 고정 반경(8)을 따른다.</summary>
    private const double GpuCornerRadius = 25;
    private const double DwmCornerRadius = 8;

    /// <summary>미니 창은 좁아서 웹 화면을 조금 축소해 연다.</summary>
    private const double InitialZoomFactor = 0.8;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    // 화면 가장자리로 끌었을 때 Windows 가 창을 반쪽/최대로 확장(스냅)하는 것을 막는다.
    // 스냅은 최대화 가능한 창에만 걸리므로 WS_MAXIMIZEBOX 비트를 떼면 비활성된다.
    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZEBOX = 0x00010000;

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

    private readonly BrowserSurface _surface;
    private readonly Action<string> _themeChangedHandler;
    private readonly double _cornerRadius;
    private bool _coreReady;
    private string? _pendingUrl;

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

        // 합성 컨트롤 생성 실패(WinRT 프로젝션 누락 등)로 앱 전체가 죽지 않도록 창 생성 자체를 감싼다.
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

        // 소프트웨어 렌더에서는 합성 표면이 표시되지 않으므로 투명 창·합성 컨트롤을 쓰지 않는다.
        bool gpu = !App.IsSoftwareRenderingActive();
        _cornerRadius = gpu ? GpuCornerRadius : DwmCornerRadius;
        Chrome.CornerRadius = new CornerRadius(_cornerRadius);
        if (gpu) AllowsTransparency = true;   // 핸들 생성 전이라 여기서 설정 가능
        Background = gpu ? Brushes.Transparent : (Brush)FindResource("BgBrush");

        _surface = gpu ? new CompositionSurface() : new HwndSurface();
        _surface.SetDefaultBackground(Application.Current.TryFindResource("BgBrush") is SolidColorBrush bg
            ? System.Drawing.Color.FromArgb(0xFF, bg.Color.R, bg.Color.G, bg.Color.B)
            : System.Drawing.Color.White);
        BrowserHost.Children.Add(_surface.Element);

        _themeChangedHandler = _ => ApplyColorScheme();
        App.ThemeChanged += _themeChangedHandler;

        Root.SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += OnLoadedFirst;
        // 소유 창을 최소화했다 복원하면 WPF 가 소유 창들을 함께 되살린다 — 사용자가 숨긴 창은 계속 숨긴다.
        IsVisibleChanged += (_, _) => { if (_hiddenByUser && IsVisible) Hide(); };
    }

    private void OnLoadedFirst(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedFirst;
        ApplyRoundedClip();
        _ = StartBrowserAsync();
    }

    /// <summary>라운드 코너 밖으로 자식 사각 모서리가 삐져나오지 않게 내용 Grid 를 둥글게 클립.
    /// 반경 = Chrome.CornerRadius - BorderThickness(1). 소프트웨어 렌더에서는 웹 화면이 별도 HWND 라
    /// 이 클립을 타지 않고 DWM 창 코너가 잘라준다.</summary>
    private void ApplyRoundedClip()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = Math.Max(0, _cornerRadius - 1);
        Root.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RestorePlacement();
        DisableEdgeSnap();

        if (!AllowsTransparency)
        {
            // 불투명 창 — DWM 이 창 자체를 라운드로 깎아 자식 HWND(웹 화면)까지 매끄럽게 잘린다.
            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                    DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch { }
        }
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
            await _surface.EnsureAsync(env);

            var core = _surface.Core!;
            _coreReady = true;
            ApplyColorScheme();
            _surface.SetZoom(InitialZoomFactor);
            // 웹 화면에 포커스가 있을 때의 F5·Ctrl+R 은 브라우저 기본 단축키가 처리한다.
            try { core.Settings.AreBrowserAcceleratorKeysEnabled = true; } catch { }

            // 새 창 요청은 같은 뷰에서 열기(팝업 창 대신 인라인 이동)
            core.NewWindowRequested += (_, args) => { args.Handled = true; NavigateCore(args.Uri); };

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

    /// <summary>외부 요청 주소로 이동. 코어 준비 전이면 준비 후 열도록 보류한다.</summary>
    public void Navigate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!_coreReady) { _pendingUrl = url; return; }
        NavigateCore(url);
    }

    private void NavigateCore(string url)
    {
        try { _surface.Core?.Navigate(url); } catch { }
    }

    private void ApplyColorScheme()
    {
        try
        {
            if (_surface.Core is { } core)
                core.Profile.PreferredColorScheme = App.IsDarkTheme(App.CurrentTheme)
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
        }
        catch { /* 해제 중 등 */ }
    }

    /// <summary>헤더 등 WPF 쪽에 포커스가 있을 때의 F5 — 웹 화면 포커스 시엔 브라우저 기본 단축키가 처리한다.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.F5) return;
        try { _surface.Core?.Reload(); } catch { }
        e.Handled = true;
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

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
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
        try { _surface.Dispose(); } catch { }
        OpenStateChanged?.Invoke();
    }

    // ── 브라우저 표면 — 두 컨트롤의 공통 기반(WebView2Base)이 internal 이라 얇게 감싼다 ─────────
    private abstract class BrowserSurface
    {
        public abstract UIElement Element { get; }
        public abstract CoreWebView2? Core { get; }
        public abstract Task EnsureAsync(CoreWebView2Environment env);
        public abstract void SetDefaultBackground(System.Drawing.Color color);
        public abstract void SetZoom(double factor);
        public abstract void Dispose();
    }

    /// <summary>WPF 렌더 경로. 투명 창·라운드 클립이 웹 화면까지 적용된다(GPU 렌더 전용).</summary>
    private sealed class CompositionSurface : BrowserSurface
    {
        private readonly WebView2CompositionControl _view = new();
        public override UIElement Element => _view;
        public override CoreWebView2? Core => _view.CoreWebView2;
        public override Task EnsureAsync(CoreWebView2Environment env) => _view.EnsureCoreWebView2Async(env);
        public override void SetDefaultBackground(System.Drawing.Color color) => _view.DefaultBackgroundColor = color;
        public override void SetZoom(double factor) => _view.ZoomFactor = factor;
        public override void Dispose() => _view.Dispose();
    }

    /// <summary>별도 HWND 경로. 소프트웨어 렌더·원격에서도 화면이 나오지만 WPF 클립을 타지 않는다.</summary>
    private sealed class HwndSurface : BrowserSurface
    {
        private readonly WebView2 _view = new();
        public override UIElement Element => _view;
        public override CoreWebView2? Core => _view.CoreWebView2;
        public override Task EnsureAsync(CoreWebView2Environment env) => _view.EnsureCoreWebView2Async(env);
        public override void SetDefaultBackground(System.Drawing.Color color) => _view.DefaultBackgroundColor = color;
        public override void SetZoom(double factor) => _view.ZoomFactor = factor;
        public override void Dispose() => _view.Dispose();
    }
}
