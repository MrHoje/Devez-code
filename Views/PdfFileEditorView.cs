using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DevezCode.Views;

/// <summary>WebView2(Edge) 내장 PDF 뷰어를 사용하는 읽기 전용 파일 탭.</summary>
public sealed class PdfFileEditorView : UserControl, IFileTabEditor, INativeInputSurface
{
    public event EventHandler? CloseRequested;
#pragma warning disable CS0067 // PDF는 읽기 전용
    public event EventHandler? DirtyChanged;
#pragma warning restore CS0067
    public event EventHandler? Interacted;
    public event EventHandler? NativeSurfaceFocused;

    private WebView2? _webView;
    private string? _path;
    private bool _initializing;
    private readonly Action<string> _themeChangedHandler;
    private bool _themeSubscribed;

    public PdfFileEditorView()
    {
        PreviewMouseDown += (_, _) => Interacted?.Invoke(this, EventArgs.Empty);
        _themeChangedHandler = _ => ApplyTheme();
        Loaded += (_, _) => SubscribeTheme();
        Unloaded += (_, _) => UnsubscribeTheme();
    }

    public static bool IsPdf(string path)
        => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public string? FilePath => _path;
    public bool IsDirty => false;

    public bool LoadFile(string path)
    {
        if (!File.Exists(path)) return false;
        _path = path;
        _ = InitializeAsync();
        return true;
    }

    private async Task InitializeAsync()
    {
        if (_initializing || _path == null) return;
        _initializing = true;
        try
        {
            _webView = new WebView2 { DefaultBackgroundColor = CurrentBackgroundColor() };
            // WebView2는 자식 HWND라 부모 UserControl의 PreviewMouseDown으로 실제 본문 클릭이 올라오지 않는다.
            // 네이티브 입력면이 포커스를 얻는 순간 패널 라우팅에 알려 늦은 터미널 복귀를 취소한다.
            _webView.GotKeyboardFocus += (_, _) => NativeSurfaceFocused?.Invoke(this, EventArgs.Empty);
            Content = _webView;
            await _webView.EnsureCoreWebView2Async();

            // 외부 파일 드래그를 패널의 WPF PreviewDragOver(FileEditorHostContainer)로 넘긴다 —
            // AllowExternalDrop=false 로 자식 HWND 가 OLE Drop 을 거부해 부모 HwndSource 로 fall-through 하고,
            // AllowDrop=false 로 WebView2 가 WPF DragOver 를 흡수하지 않게 한다(둘 다 필요).
            _webView.AllowExternalDrop = false;
            _webView.AllowDrop = false;

            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // WebView2 내장 PDF 뷰어의 전체 화면은 일부 런타임에서 Escape로 해제되지 않는다.
            // 빠져나올 수 없는 상태를 막기 위해 해당 툴바 버튼을 노출하지 않는다.
            core.Settings.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.FullScreen;
            core.Profile.PreferredColorScheme = PreferredColorScheme;
            core.Navigate(new Uri(_path).AbsoluteUri);
        }
        catch (Exception ex)
        {
            Content = new TextBlock
            {
                Text = $"PDF를 표시할 수 없습니다.\n\n{ex.Message}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Application.Current.TryFindResource("TextBrush") as Brush,
            };
        }
    }

    private static CoreWebView2PreferredColorScheme PreferredColorScheme
        => App.IsDarkTheme(App.CurrentTheme)
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;

    private static System.Drawing.Color CurrentBackgroundColor()
    {
        if (Application.Current?.TryFindResource("BgBrush") is SolidColorBrush brush)
            return System.Drawing.Color.FromArgb(0xFF, brush.Color.R, brush.Color.G, brush.Color.B);
        return System.Drawing.Color.White;
    }

    private void ApplyTheme()
    {
        if (_webView == null) return;
        try
        {
            _webView.DefaultBackgroundColor = CurrentBackgroundColor();
            if (_webView.CoreWebView2 != null)
                _webView.CoreWebView2.Profile.PreferredColorScheme = PreferredColorScheme;
            _webView.InvalidateVisual();
        }
        catch { }
    }

    private void SubscribeTheme()
    {
        if (_themeSubscribed) return;
        App.ThemeChanged += _themeChangedHandler;
        _themeSubscribed = true;
        ApplyTheme();
    }

    private void UnsubscribeTheme()
    {
        if (!_themeSubscribed) return;
        App.ThemeChanged -= _themeChangedHandler;
        _themeSubscribed = false;
    }

    public bool Save() => true;
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
    public new bool Focus() => _webView?.Focus() ?? base.Focus();

    public async Task<BitmapSource?> CaptureSnapshotAsync()
    {
        if (_webView?.CoreWebView2 == null) return null;
        try
        {
            using var stream = new MemoryStream();
            await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
