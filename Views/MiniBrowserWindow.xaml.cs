using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>
/// 메인 창 위에 겹쳐 띄우는 작은 브라우저 창. 본문은 우측 패널과 같은 BrowserHostView 를 재사용한다.
/// Owner 를 메인 창으로 두어 "메인 창 위에만" 항상 표시되고(다른 앱 위로는 올라가지 않음),
/// 위치·크기·마지막 주소는 저장해 다시 열 때 복원한다.
/// </summary>
public partial class MiniBrowserWindow : Window
{
    /// <summary>미니 브라우저 전용 방문 기록 키(프로젝트별 브라우저 탭 기록과 분리).</summary>
    private const string StateKey = SettingsService.MiniBrowserStateKey;

    /// <summary>창 모서리 라운드 반경(DIP). DWM 기본 라운드(약 8)보다 크게 보이도록 창 리전으로 직접 깎는다.</summary>
    private const double CornerRadiusDip = 14;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private static MiniBrowserWindow? _instance;

    /// <summary>이미 열려 있으면 활성화만, 아니면 새로 띄운다. url 이 있으면 그 주소로 이동.</summary>
    public static void ShowOrActivate(Window? owner, string? url = null)
    {
        if (_instance is { IsLoaded: true } win)
        {
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
            if (!string.IsNullOrWhiteSpace(url)) win.Browser.NavigateToUrl(url!);
            return;
        }

        var created = new MiniBrowserWindow { Owner = owner };
        _instance = created;
        created.Show();
        if (!string.IsNullOrWhiteSpace(url)) created.Browser.NavigateToUrl(url!);
    }

    /// <summary>설정에서 시작 주소를 바꿨을 때 — 열려 있는 미니 창을 새 주소로 즉시 이동시킨다.</summary>
    public static void ApplyHomeUrlToOpenWindow()
    {
        if (_instance is not { IsLoaded: true } win) return;
        var url = SettingsService.LoadMiniBrowserHomeUrl();
        win.Browser.HomeUrlOverride = url;
        win.Browser.NavigateToUrl(url);
    }

    public MiniBrowserWindow()
    {
        InitializeComponent();
        Browser.ShowToolbar = false;   // 미니 창은 웹 화면만 — 주소창·네비게이션 버튼 없음
        Browser.HomeUrlOverride = SettingsService.LoadMiniBrowserHomeUrl();
        Browser.StateKey = StateKey;
        Browser.DocumentTitleChanged += OnDocumentTitleChanged;
        Loaded += OnLoadedFirst;
        Root.SizeChanged += (_, _) => ApplyRoundedClip();
        SizeChanged += (_, _) => ApplyWindowRegion();
        DpiChanged += (_, _) => ApplyWindowRegion();
    }

    private void OnLoadedFirst(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedFirst;
        ApplyRoundedClip();
        Browser.EnsureStarted();
        SyncTitle();
    }

    /// <summary>라운드 코너 밖으로 자식 사각 모서리가 삐져나오지 않게 내용 Grid 를 둥글게 클립.
    /// 반경 = Chrome.CornerRadius - BorderThickness(1).</summary>
    private void ApplyRoundedClip()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = CornerRadiusDip - 1;
        Root.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    /// <summary>창 자체를 둥근 리전으로 클립. WebView2 는 별도 HWND 라 WPF 클립으로는 안 깎이므로
    /// 창 리전으로 잘라야 라운드 코너 밖으로 웹 화면이 삐져나오지 않는다.
    /// (DWM 코너 지정은 반경이 8px 로 고정이라 더 둥근 모서리를 만들 수 없다.)</summary>
    private void ApplyWindowRegion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(hwnd, out var wr)) return;

        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        if (w <= 0 || h <= 0) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int d = (int)Math.Round(CornerRadiusDip * 2 * (scale <= 0 ? 1 : scale));

        // CreateRoundRectRgn 은 우/하단 경계를 배타적으로 다뤄 +1 이 필요하다.
        var rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
        if (rgn == IntPtr.Zero) return;
        if (SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn); // 실패 시에만 해제(성공 시 소유권 이전)
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RestorePlacement();
        ApplyWindowRegion();
    }

    /// <summary>저장된 위치·크기 복원. 화면 작업영역과 겹치지 않으면(모니터 제거 등) 소유 창 기준으로 배치.</summary>
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

    /// <summary>헤더 제목 — 페이지 제목이 없으면 주소의 호스트명, 그것도 없으면 기본 문구.</summary>
    private void SyncTitle(string? documentTitle = null)
    {
        if (!string.IsNullOrWhiteSpace(documentTitle))
        {
            TitleText.Text = documentTitle!.Trim();
            return;
        }
        var url = Browser.GetCurrentUrl(StateKey);
        TitleText.Text = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "미니 브라우저";
    }

    private void OnDocumentTitleChanged(string title) => SyncTitle(title);

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
        Browser.DocumentTitleChanged -= OnDocumentTitleChanged;
        try { Browser.DisposeAll(); } catch { }
    }
}
