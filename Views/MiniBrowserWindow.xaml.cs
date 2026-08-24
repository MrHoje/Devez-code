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
    /// <summary>미니 브라우저 전용 방문 기록 키. 프로젝트별 브라우저 탭 기록과 섞이지 않게 고정 키를 쓴다.</summary>
    private const string StateKey = "__mini_browser__";

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

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

    public MiniBrowserWindow()
    {
        InitializeComponent();
        Browser.StateKey = StateKey;
        Browser.DocumentTitleChanged += OnDocumentTitleChanged;
        Loaded += OnLoadedFirst;
        Root.SizeChanged += (_, _) => ApplyRoundedClip();
    }

    private void OnLoadedFirst(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedFirst;
        ApplyRoundedClip();
        Browser.EnsureStarted();
        SyncTitle();
    }

    /// <summary>라운드 코너 밖으로 자식 사각 모서리가 삐져나오지 않게 내용 Grid 를 둥글게 클립.
    /// 반경 = Chrome.CornerRadius(8) - BorderThickness(1).</summary>
    private void ApplyRoundedClip()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;
        Root.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 7, 7);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { }
        RestorePlacement();
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

    private void OpenExternalBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!Browser.TryOpenInDefaultBrowser(StateKey))
            ConfirmDialog.Alert("기본 브라우저로 열기", "열 수 있는 웹 주소가 없습니다.");
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
