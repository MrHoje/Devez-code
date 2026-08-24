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
/// 메인 창 위에 겹쳐 띄우는 작은 브라우저 창. 주소창·네비게이션 없이 웹 화면만 보여준다.
/// Owner 를 메인 창으로 두어 메인 창 위에만 항상 표시되고(다른 앱 위로는 올라가지 않음),
/// 위치·크기·마지막 주소를 저장해 다시 열 때 복원한다.
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
    /// <summary>미니 브라우저 전용 방문 기록 키(프로젝트별 브라우저 탭 기록과 분리).</summary>
    private const string StateKey = SettingsService.MiniBrowserStateKey;

    /// <summary>GPU 렌더에서 쓰는 라운드 반경. 소프트웨어 렌더는 DWM 고정 반경(8)을 따른다.</summary>
    private const double GpuCornerRadius = 16;
    private const double DwmCornerRadius = 8;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static MiniBrowserWindow? _instance;

    private readonly BrowserSurface _surface;
    private readonly Action<string> _themeChangedHandler;
    private readonly double _cornerRadius;
    private bool _coreReady;
    private string? _pendingUrl;

    /// <summary>이미 열려 있으면 활성화만, 아니면 새로 띄운다. url 이 있으면 그 주소로 이동.</summary>
    public static void ShowOrActivate(Window? owner, string? url = null)
    {
        if (_instance is { IsLoaded: true } win)
        {
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
            win.Navigate(url);
            return;
        }

        // 합성 컨트롤 생성 실패(WinRT 프로젝션 누락 등)로 앱 전체가 죽지 않도록 창 생성 자체를 감싼다.
        try
        {
            var created = new MiniBrowserWindow { Owner = owner };
            _instance = created;
            created.Show();
            created.Navigate(url);
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

            core.DocumentTitleChanged += (_, _) => SyncTitle(core.DocumentTitle);
            core.SourceChanged += (_, _) => PersistCurrentUrl(core.Source);
            // 새 창 요청은 같은 뷰에서 열기(팝업 창 대신 인라인 이동)
            core.NewWindowRequested += (_, args) => { args.Handled = true; NavigateCore(args.Uri); };

            NavigateCore(_pendingUrl ?? LoadStartUrl());
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

    /// <summary>마지막으로 보던 주소, 없으면 설정의 미니 브라우저 시작 주소.</summary>
    private static string LoadStartUrl()
    {
        var saved = SettingsService.LoadBrowserHistory(StateKey);
        if (saved is { } h && h.Urls.Count > 0)
        {
            var idx = Math.Clamp(h.Index, 0, h.Urls.Count - 1);
            if (!string.IsNullOrWhiteSpace(h.Urls[idx])) return h.Urls[idx];
        }
        return SettingsService.LoadMiniBrowserHomeUrl();
    }

    /// <summary>다시 열 때 이어보도록 현재 주소만 저장한다(미니 창은 히스토리 UI 가 없다).</summary>
    private static void PersistCurrentUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "about:blank") return;
        try { SettingsService.SaveBrowserHistory(StateKey, new[] { url }, 0); } catch { }
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

    /// <summary>헤더 제목 — 페이지 제목이 없으면 기본 문구.</summary>
    private void SyncTitle(string? documentTitle)
        => TitleText.Text = string.IsNullOrWhiteSpace(documentTitle) ? "미니 브라우저" : documentTitle!.Trim();

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

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (WindowState == WindowState.Normal && Width > 0 && Height > 0)
        {
            try { SettingsService.SaveMiniBrowserPlacement(Left, Top, Width, Height); } catch { }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (ReferenceEquals(_instance, this)) _instance = null;
        App.ThemeChanged -= _themeChangedHandler;
        try { _surface.Dispose(); } catch { }
    }

    // ── 브라우저 표면 — 두 컨트롤의 공통 기반(WebView2Base)이 internal 이라 얇게 감싼다 ─────────
    private abstract class BrowserSurface
    {
        public abstract UIElement Element { get; }
        public abstract CoreWebView2? Core { get; }
        public abstract Task EnsureAsync(CoreWebView2Environment env);
        public abstract void SetDefaultBackground(System.Drawing.Color color);
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
        public override void Dispose() => _view.Dispose();
    }
}
