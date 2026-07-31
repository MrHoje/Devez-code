using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services;

namespace DevezCode.Views;

public sealed class MonacoHost : ContentControl, IDisposable
{
    private const string VirtualHost = "monaco.devezcode.local";

    /// <summary>Monaco DiffEditor 준비 완료(pageReady 수신 후). 호스트가 구독.</summary>
    public event Action? PageReady;
    /// <summary>WebView2 초기화 실패 또는 pageReady 무응답(타임아웃) — 호스트가
    /// 이걸 받아 로딩 스피너를 내리고 에러를 보여줘야 한다. 안 그러면 스피너가 영원히 돈다(무한 스피너 버그).</summary>
    public event Action<string>? InitFailed;

    private WebView2? _webView;
    private bool _initStarted;
    private bool _pageReady;
    private (string o, string m, string lang, bool sideBySide)? _pendingDiff;
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
            // 투명을 지원 못 함) → diff 에디터가 열릴 때 한 번 까매졌다 뜨는 원인. 테마 배경색으로 맞춰
            // 페인트 전 구간이 주변(커튼/패널 배경)과 동일하게 보이도록 한다. 테마 변경 시 ApplyTheme 이 갱신.
            _webView = new WebView2 { DefaultBackgroundColor = CurrentBgColor() };
            // 외부 파일 드래그는 자식 HWND 가 OLE Drop 을 거부해 부모 HwndSource 로 fall-through →
            // 패널의 WPF PreviewDragOver(FileEditorHostContainer)가 받아 세션과 동일한 드롭 선택 화면을 띄운다.
            _webView.AllowExternalDrop = false;
            _webView.AllowDrop = true;
            Content = _webView;

            var env = await SharedEnvironment.Value;
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            var webRoot = Path.Combine(AppContext.BaseDirectory, "Resources", "Monaco", "web");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, webRoot, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessageReceived;
            // bridge.js 가 바뀔 때마다 새로 로드되도록 캐시 무력화(?v=<ticks>). diff.html 이 이 쿼리를
            // 그대로 bridge.js 로 전파한다 → WebView2 HTTP 캐시가 옛 스크립트를 서빙하는 것 방지.
            long ver = 0;
            try { ver = File.GetLastWriteTimeUtc(Path.Combine(webRoot, "bridge.js")).Ticks; } catch { }
            core.Navigate($"https://{VirtualHost}/diff.html?v={ver}");

            // pageReady(JS)가 안 오면(스크립트 예외 등) 스피너가 영원히 도는 것 방지 — 15초 폴백.
            _readyTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ReadyTimeoutMs) };
            _readyTimeoutTimer.Tick += (_, _) =>
            {
                _readyTimeoutTimer!.Stop();
                if (_pageReady) return;
                DiagLog.Write("MonacoHost: pageReady timeout — bridge.js 초기화 응답 없음");
                InitFailed?.Invoke("에디터 페이지 응답 시간 초과");
            };
            _readyTimeoutTimer.Start();
        }
        catch (Exception ex)
        {
            DiagLog.Write($"MonacoHost: init failed — {ex}");
            InitFailed?.Invoke(ex.Message);
            Content = new TextBlock
            {
                Text = "diff 편집기를 시작할 수 없습니다.\nWebView2 런타임이 필요합니다.\n\n" + ex.Message,
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
                case "jsError":
                    DiagLog.Write($"MonacoHost[JS] {root.GetProperty("where").GetString()}: {root.GetProperty("message").GetString()}");
                    break;
                case "pageReady":
                    _pageReady = true;
                    _readyTimeoutTimer?.Stop();
                    _readyTimeoutTimer = null;
                    ApplyTheme();
                    if (_pendingDiff is { } d) { SetDiff(d.o, d.m, d.lang, d.sideBySide); _pendingDiff = null; }
                    PageReady?.Invoke();
                    break;
            }
        }
        catch { }
    }

    /// <summary>sideBySide=false 면 분할 없이 단일 뷰(신규/추가 파일 등 원본이 없는 경우).</summary>
    public void SetDiff(string original, string modified, string language, bool sideBySide = true)
    {
        if (!_pageReady) { _pendingDiff = (original, modified, language, sideBySide); return; }
        PostJson(new { type = "setDiff", originalText = original, modifiedText = modified, language, sideBySide });
    }

    public void ApplyTheme()
    {
        // 페이지 페인트 전 기본 배경도 테마에 맞춰 갱신(리사이즈/재로드 시 노출될 수 있음).
        try { if (_webView != null) _webView.DefaultBackgroundColor = CurrentBgColor(); } catch { }
        if (!_pageReady) return;
        var t = MonacoThemePayload.Current(); // Task 4 에서 구현하는 static 헬퍼
        PostJson(new { type = "setTheme", t.@base, t.rules, t.colors });

        // modal dialog(설정창)가 주 윈도우를 비활성화한 상태에서도 WebView2가 즉시 repaint
        // 하도록 강제한다. PostWebMessageAsJson 메시지는 WebView2 프로세스에 도달하지만
        // 부모 HWND가 disabled면 화면 갱신이 보류될 수 있음.
        Dispatcher.BeginInvoke(new Action(() => _webView?.InvalidateVisual()), System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>현재 테마의 BgBrush → WebView2 기본 배경색(페이지 페인트 전 구간용).</summary>
    private static System.Drawing.Color CurrentBgColor()
    {
        if (Application.Current?.TryFindResource("BgBrush") is SolidColorBrush b)
            return System.Drawing.Color.FromArgb(0xFF, b.Color.R, b.Color.G, b.Color.B);
        return System.Drawing.Color.White;
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
