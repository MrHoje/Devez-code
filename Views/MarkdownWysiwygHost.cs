using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DevezCode.Views;

public sealed class MarkdownWysiwygHost : ContentControl, IDisposable
{
    private const string VirtualHost = "md.devezcode.local";

    public event Action<string, bool>? MarkdownChanged;
    public event Action<string>? BaselineReady;
    public event Action? SaveRequested;
    public event Action? EditorReady;

    private WebView2? _webView;
    private bool _initStarted;
    private bool _pageReady;
    private (string md, bool markClean)? _pendingMarkdown;
    private string? _pendingTheme;

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(CreateEnvironmentAsync);

    public static Task PrewarmAsync() => SharedEnvironment.Value;

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevezCode", "WebView2");
        return CoreWebView2Environment.CreateAsync(null, userDataDir);
    }

    public async Task EnsureReadyAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            _webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            Content = _webView;

            var env = await SharedEnvironment.Value;
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            var webRoot = Path.Combine(AppContext.BaseDirectory, "Resources", "Markdown", "web");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, webRoot, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessageReceived;
            core.Navigate($"https://{VirtualHost}/editor.html");
        }
        catch (Exception ex)
        {
            Content = new TextBlock
            {
                Text = "마크다운 편집기를 시작할 수 없습니다.\nWebView2 런타임이 필요합니다.\n\n" + ex.Message,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
                Foreground = Application.Current.TryFindResource("TextBrush") as Brush,
            };
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "pageReady":
                    _pageReady = true;
                    if (_pendingTheme != null) { ApplyTheme(_pendingTheme); _pendingTheme = null; }
                    if (_pendingMarkdown is { } pm) { SetMarkdown(pm.md, pm.markClean); _pendingMarkdown = null; }
                    EditorReady?.Invoke();
                    break;
                case "markdownChanged":
                    MarkdownChanged?.Invoke(
                        root.GetProperty("markdown").GetString() ?? "",
                        root.TryGetProperty("dirty", out var d) && d.GetBoolean());
                    break;
                case "baseline":
                    BaselineReady?.Invoke(root.GetProperty("markdown").GetString() ?? "");
                    break;
                case "saveRequested":
                    SaveRequested?.Invoke();
                    break;
                case "zoom":
                    if (_webView != null && root.TryGetProperty("factor", out var zf))
                        _webView.ZoomFactor = Math.Clamp(zf.GetDouble(), 0.5, 2.5);
                    break;
            }
        }
        catch { }
    }

    public void SetMarkdown(string md, bool markClean)
    {
        if (_pageReady) PostJson(new { type = "setMarkdown", markdown = md ?? "", markClean });
        else _pendingMarkdown = (md ?? "", markClean);
    }

    public void MarkClean()
    {
        if (_pageReady) PostJson(new { type = "markClean" });
    }

    /// <summary>콘텐츠는 그대로 두고 정규화 기준선만 갱신 — 외부 변경 감지에서
    /// "현재 내용 유지" 선택 시 Toast UI 측 baseline만 디스크로 리베이스한다.</summary>
    public void SetBaseline(string md)
    {
        if (_pageReady) PostJson(new { type = "setBaseline", markdown = md ?? "" });
    }

    public void FocusEditor()
    {
        if (_pageReady) { _webView?.Focus(); PostJson(new { type = "focus" }); }
    }

    public void ApplyTheme(string theme)
    {
        if (!_pageReady) { _pendingTheme = theme; return; }
        PostJson(new
        {
            type = "setTheme",
            dark = theme == "dark",
            bg = Hex("BgBrush", "#ffffff"),
            text = Hex("TextBrush", "#0f172a"),
            codeBg = Hex("CodeBgBrush", "#f1f5f9"),
            codeText = Hex("CodeTextBrush", "#0f172a"),
            primary = Hex("PrimaryBrush", "#2563eb"),
        });

        // modal dialog(설정창)가 주 윈도우를 비활성화한 상태에서도 WebView2가 즉시 repaint
        // 하도록 강제한다. PostWebMessageAsJson 메시지는 WebView2 프로세스에 도달하지만
        // 부모 HWND가 disabled면 화면 갱신이 보류될 수 있음.
        Dispatcher.BeginInvoke(new Action(() => _webView?.InvalidateVisual()), System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>WebView 내부 토스트(저장/자동 갱신 알림 등) — WPF 토스트는 WebView airspace로 가려지므로 웹 레이어로 띄운다.</summary>
    public void ShowToast(string text)
    {
        if (_pageReady) PostJson(new { type = "toast", text = text ?? "" });
    }

    private static string Hex(string brushKey, string fallback)
    {
        if (Application.Current?.TryFindResource(brushKey) is SolidColorBrush b)
        {
            var c = b.Color;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        return fallback;
    }

    /// <summary>현재 에디터 화면을 PNG 스냅샷으로 반환. airspace 우회용(앱 종료/오버레이 배경).</summary>
    public async Task<System.Windows.Media.Imaging.BitmapSource?> CaptureSnapshotAsync()
    {
        if (_webView?.CoreWebView2 == null) return null;
        try
        {
            using var ms = new MemoryStream();
            await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = ms;
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private void PostJson(object message)
    {
        try { _webView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, CamelCase)); }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            if (_webView?.CoreWebView2 != null)
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
        catch { }
        try { _webView?.Dispose(); } catch { }
        _webView = null;
    }
}
