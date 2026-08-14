using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services;

namespace DevezCode.Views;

public sealed class MarkdownWysiwygHost : ContentControl, IDisposable
{
    private const string VirtualHost = "md.devezcode.local";

    public event Action<string, bool>? MarkdownChanged;
    public event Action<string>? BaselineReady;
    public event Action? SaveRequested;
    public event Action? EditorReady;
    /// <summary>에디터 표면 클릭/포커스 — 분할 시 이 패널을 포커스 패널로 지정하는 데 사용.</summary>
    public event Action? Interacted;
    /// <summary>외부(탐색기) 파일이 에디터 위로 드래그돼 들어옴 — 패널이 드롭 선택 화면으로 전환한다.</summary>
    public event Action? ExternalFileDragEntered;
    /// <summary>오버레이가 뜨기 전 웹 표면에 그대로 드롭됨 — 패널이 오버레이를 정리한다.</summary>
    public event Action? ExternalFileDropReceived;
    /// <summary>WebView2 초기화 실패 또는 pageReady 무응답(타임아웃) — 호스트(MarkdownFileEditorView)가
    /// 이걸 받아 로딩 스피너를 내리고 에러를 보여줘야 한다. 안 그러면 스피너가 영원히 돈다(무한 스피너 버그).</summary>
    public event Action<string>? InitFailed;

    private WebView2? _webView;
    private bool _initStarted;
    private bool _pageReady;
    private (string md, bool markClean)? _pendingMarkdown;
    private string? _pendingTheme;
    private int? _pendingViewportWidth;
    private DispatcherTimer? _readyTimeoutTimer;
    private const int ReadyTimeoutMs = 15000;

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
            // Transparent 는 페이지 첫 페인트 전까지 '검정'으로 렌더된다(WebView2 는 컴포지션 시작 전
            // 투명을 지원 못 함) → md 에디터가 열릴 때 한 번 까매졌다 뜨는 원인. 테마 배경색으로 맞춰
            // 페인트 전 구간이 주변(커튼/패널 배경)과 동일하게 보이도록 한다. 테마 변경 시 ApplyTheme 이 갱신.
            _webView = new WebView2 { DefaultBackgroundColor = CurrentBgColor() };
            Content = _webView;

            var env = await SharedEnvironment.Value;
            await _webView.EnsureCoreWebView2Async(env);

            // 외부 파일 드래그는 웹(bridge.js)에서 dragenter 를 감지해 fileDragEnter 로 알린다(터미널과 동일).
            // WebView2 는 별도 HWND 라 WPF 부모의 DragOver 로 전달되지 않으므로 OS 드롭은 계속 웹이 받는다.
            _webView.AllowExternalDrop = true;

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
            // editor.html 또는 bridge.js 변경 시 새 URL로 로드해 WebView2 HTTP 캐시를 무력화한다.
            long ver = 0;
            try
            {
                ver = Math.Max(
                    File.GetLastWriteTimeUtc(Path.Combine(webRoot, "bridge.js")).Ticks,
                    File.GetLastWriteTimeUtc(Path.Combine(webRoot, "editor.html")).Ticks);
            }
            catch { }
            core.Navigate($"https://{VirtualHost}/editor.html?v={ver}");

            // pageReady(JS)가 안 오면(스크립트 예외 등) 스피너가 영원히 도는 것 방지 — 15초 폴백.
            _readyTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ReadyTimeoutMs) };
            _readyTimeoutTimer.Tick += (_, _) =>
            {
                _readyTimeoutTimer!.Stop();
                if (_pageReady) return;
                DiagLog.Write("MarkdownWysiwygHost: pageReady timeout — bridge.js 초기화 응답 없음");
                InitFailed?.Invoke("에디터 페이지 응답 시간 초과");
            };
            _readyTimeoutTimer.Start();
        }
        catch (Exception ex)
        {
            DiagLog.Write($"MarkdownWysiwygHost: init failed — {ex}");
            InitFailed?.Invoke(ex.Message);
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
                    _readyTimeoutTimer?.Stop();
                    _readyTimeoutTimer = null;
                    if (_pendingTheme != null) { ApplyTheme(_pendingTheme); _pendingTheme = null; }
                    if (_pendingViewportWidth is int width) { SetViewportWidth(width); _pendingViewportWidth = null; }
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
                case "interact":
                    Interacted?.Invoke();
                    break;
                case "fileDragEnter":
                    ExternalFileDragEntered?.Invoke();
                    break;
                case "fileDrop":
                    ExternalFileDropReceived?.Invoke();
                    break;
                case "runHtml":
                    if (root.TryGetProperty("html", out var html))
                        RunHtmlInBrowser(html.GetString() ?? "");
                    break;
                case "zoom":
                    if (_webView != null && root.TryGetProperty("factor", out var zf))
                        _webView.ZoomFactor = Math.Clamp(zf.GetDouble(), 0.5, 2.5);
                    break;
            }
        }
        catch { }
    }

    private static void RunHtmlInBrowser(string html)
    {
        var previewDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevezCode", "HtmlPreview");
        Directory.CreateDirectory(previewDirectory);

        foreach (var oldFile in Directory.EnumerateFiles(previewDirectory, "*.html"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(oldFile) < DateTime.UtcNow.AddDays(-1))
                    File.Delete(oldFile);
            }
            catch { }
        }

        var previewPath = Path.Combine(previewDirectory, $"preview-{Guid.NewGuid():N}.html");
        File.WriteAllText(previewPath, html, new System.Text.UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(previewPath) { UseShellExecute = true });
    }

    public void SetMarkdown(string md, bool markClean)
    {
        if (_pageReady) PostJson(new { type = "setMarkdown", markdown = md ?? "", markClean });
        else _pendingMarkdown = (md ?? "", markClean);
    }

    public void SetViewportWidth(int width)
    {
        width = System.Math.Max(0, width);
        if (_pageReady) PostJson(new { type = "setViewportWidth", width });
        else _pendingViewportWidth = width;
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

    /// <summary>현재 테마의 BgBrush → WebView2 기본 배경색(페이지 페인트 전 구간용).</summary>
    private static System.Drawing.Color CurrentBgColor()
    {
        if (Application.Current?.TryFindResource("BgBrush") is SolidColorBrush b)
            return System.Drawing.Color.FromArgb(0xFF, b.Color.R, b.Color.G, b.Color.B);
        return System.Drawing.Color.White;
    }

    private static System.Drawing.Color ColorFromHex(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return System.Drawing.Color.FromArgb(0xFF, c.R, c.G, c.B);
        }
        catch { return System.Drawing.Color.White; }
    }

    public void ApplyTheme(string appTheme)
    {
        // 문서 뷰어 설정이 "auto"면 앱 테마를 따르고, 고정이면 그 테마로. 고정 테마가 현재 로드된 앱 테마와
        // 같으면 라이브 리소스를 쓰고, 다르면 App.MarkdownPalette 의 색을 넘긴다.
        string eff = App.MarkdownEffectiveTheme(appTheme);
        bool live = eff == App.CurrentTheme;
        bool dark = App.IsDarkTheme(eff);

        string bg, panel, text, primary;
        string? codeBg, codeText;
        if (live)
        {
            bg = Hex("BgBrush", "#ffffff");
            panel = Hex("PanelBrush", "#ffffff");
            text = Hex("TextBrush", "#0f172a");
            primary = Hex("PrimaryBrush", "#2563eb");
            codeBg = Hex("CodeBgBrush", "#f1f5f9");
            codeText = Hex("CodeTextBrush", "#0f172a");
        }
        else
        {
            var p = App.MarkdownPalette(eff);
            bg = p.Bg; panel = p.Panel; text = p.Text; primary = p.Primary;
            codeBg = null; codeText = null; // bridge.js 반투명 fallback 사용
        }

        // 페이지 페인트 전 기본 배경도 실효 테마에 맞춰 갱신(리사이즈/재로드 시 노출될 수 있음).
        try { if (_webView != null) _webView.DefaultBackgroundColor = ColorFromHex(bg); } catch { }
        if (!_pageReady) { _pendingTheme = appTheme; return; }
        PostJson(new
        {
            type = "setTheme",
            dark,
            bg,
            panel,
            text,
            codeBg,
            codeText,
            primary,
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
        _readyTimeoutTimer?.Stop();
        _readyTimeoutTimer = null;
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
