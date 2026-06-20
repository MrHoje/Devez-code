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
public partial class BrowserHostView : UserControl
{
    private WebView2? _view;
    private bool _initStarted;
    private const string HomeUrl = "https://www.google.com";

    private readonly Action<string> _themeChangedHandler;

    public BrowserHostView()
    {
        InitializeComponent();
        _themeChangedHandler = _ => ApplyColorScheme();
        App.ThemeChanged += _themeChangedHandler;
    }

    private static CoreWebView2PreferredColorScheme PreferredScheme =>
        App.CurrentTheme == "dark"
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;

    private void ApplyColorScheme()
    {
        try { if (_view?.CoreWebView2 != null) _view.CoreWebView2.Profile.PreferredColorScheme = PreferredScheme; }
        catch { /* 해제 중 등 */ }
    }

    /// <summary>처음 표시될 때 WebView2 를 초기화하고 마지막/홈 URL 을 연다(1회만).</summary>
    public async void EnsureStarted()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            _view = new WebView2();
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
                SyncToolbar();
                var src = core.Source;
                if (!string.IsNullOrEmpty(src) && src != "about:blank")
                    SettingsService.SaveBrowserLastUrl(src);
            };
            core.HistoryChanged += (_, _) => SyncToolbar();
            // 새 창 요청은 같은 뷰에서 열기(팝업 차단 대신 인라인 이동)
            core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };

            var start = SettingsService.LoadBrowserLastUrl() ?? HomeUrl;
            core.Navigate(start);
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

    /// <summary>주소창·네비게이션 버튼을 현재 상태로 동기화.</summary>
    private void SyncToolbar()
    {
        var core = _view?.CoreWebView2;
        BackBtn.IsEnabled = core?.CanGoBack == true;
        ForwardBtn.IsEnabled = core?.CanGoForward == true;
        if (core != null && !AddressBox.IsKeyboardFocused)
            AddressBox.Text = core.Source;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    { if (_view?.CoreWebView2?.CanGoBack == true) _view.CoreWebView2.GoBack(); }

    private void ForwardBtn_Click(object sender, RoutedEventArgs e)
    { if (_view?.CoreWebView2?.CanGoForward == true) _view.CoreWebView2.GoForward(); }

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
        try { _view?.CoreWebView2?.Navigate(ToNavigationTarget(input)); }
        catch { /* 잘못된 주소 무시 */ }
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
        if (_view?.CoreWebView2 != null)
        {
            try
            {
                using var ms = new MemoryStream();
                await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
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

    /// <summary>앱 종료 시 — WebView2 + 이벤트 해제(Edge 렌더러 프로세스 잔류 방지).</summary>
    public void DisposeAll()
    {
        try { App.ThemeChanged -= _themeChangedHandler; } catch { }
        try { if (_view != null) BrowserContent.Children.Remove(_view); } catch { }
        try { _view?.Dispose(); } catch { }
        _view = null;
    }
}
