using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Models;
using DevezCode.Services;
using DevezCode.Services.ClaudeSdk;

namespace DevezCode.Views;

/// <summary>Claude Agent SDK는 호스트에서 실행하고, 채팅 표면만 로컬 WebView2로 렌더링한다.</summary>
public partial class ClaudeChatHostView : UserControl, IDisposable
{
    private const string VirtualHostSuffix = "devezcode.local";
    private const int MaxAttachments = 20;
    private const int MaxImageEncodedBytes = 10 * 1024 * 1024;
    private const int MaxTotalImageEncodedBytes = 28 * 1024 * 1024;
    private static readonly HashSet<string> ImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp",
    };
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(CreateEnvironmentAsync);

    private WebView2? _webView;
    private SessionItem? _session;
    private string _cwd = "";
    private bool _initStarted;
    private bool _pageReady;
    private bool _disposed;
    private System.Windows.Threading.DispatcherTimer? _readyTimeout;
    private bool _focusPending;
    private readonly Dictionary<string, string> _pendingInsertions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _streamDeltaLock = new();
    private readonly Dictionary<(string Type, string StreamId), StringBuilder> _pendingStreamDeltas = new();
    private bool _streamDeltaFlushScheduled;

    public event Action? UserInteracted;
    public event Action<string>? ResponseCompleted;

    public ClaudeChatHostView()
    {
        InitializeComponent();
        ClaudeSdkSessionManager.Instance.EventReceived += OnSdkEvent;
        App.ThemeChanged += OnThemeChanged;
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevezCode", "WebView2");
        return CoreWebView2Environment.CreateAsync(null, userDataDir);
    }

    public void ActivateSession(SessionItem session, string cwd)
    {
        bool changed = !ReferenceEquals(_session, session);
        if (changed) ClearPendingStreamDeltas();
        _session = session;
        _cwd = cwd;
        _ = EnsureReadyAsync();
        if (changed)
        {
            SyncActiveSession();
            FlushPendingInsertion();
        }
        _ = ClaudeSdkSessionManager.Instance.EnsureStartedAsync(session, cwd);
        FocusInput();
    }

    public void Deactivate()
    {
        _session = null;
        _focusPending = false;
    }

    public void FocusInput()
    {
        if (_session == null) return;
        _focusPending = true;
        _ = EnsureReadyAsync();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_pageReady || _session == null) return;
            _webView?.Focus();
            PostJson(new { type = "focus" });
            _focusPending = false;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void InsertFilePaths(IEnumerable<string> paths)
    {
        var text = string.Join(" ", paths.Select(path => path.Contains(' ') ? $"\"{path}\"" : path));
        if (text.Length == 0 || _session == null) return;
        if (_pageReady) PostJson(new { type = "insertText", text });
        else
        {
            _pendingInsertions.TryGetValue(_session.Id, out var previous);
            _pendingInsertions[_session.Id] = string.Join(" ", new[] { previous ?? "", text }.Where(value => value.Length > 0));
        }
        FocusInput();
    }

    private async Task EnsureReadyAsync()
    {
        if (_initStarted || _disposed) return;
        _initStarted = true;
        try
        {
            _webView = new WebView2 { DefaultBackgroundColor = CurrentBackgroundColor() };
            WebViewHost.Children.Add(_webView);
            var environment = await SharedEnvironment.Value;
            if (_disposed) return;
            await _webView.EnsureCoreWebView2Async(environment);

            _webView.AllowExternalDrop = false;
            _webView.ZoomFactor = SettingsService.LoadClaudeGuiZoomFactor();
            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            var webRoot = Path.Combine(AppContext.BaseDirectory, "Resources", "ClaudeChat", "web");
            long version = 0;
            try
            {
                version = Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
                    .Select(File.GetLastWriteTimeUtc).Max().Ticks;
            }
            catch { }
            var virtualHost = $"claude-chat-{version:x}.{VirtualHostSuffix}";
            core.SetVirtualHostNameToFolderMapping(virtualHost, webRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;
            _readyTimeout = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15),
            };
            _readyTimeout.Tick += OnReadyTimeout;
            _readyTimeout.Start();
            core.Navigate($"https://{virtualHost}/chat.html");
        }
        catch (Exception ex)
        {
            DiagLog.Write($"ClaudeChat WebView init failed: {ex}");
            ShowInitError(ex.Message);
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            switch (type)
            {
                case "pageReady":
                    _pageReady = true;
                    StopReadyTimeout();
                    ApplyTheme();
                    SyncActiveSession();
                    FlushPendingInsertion();
                    if (_focusPending && _session != null)
                    {
                        _webView?.Focus();
                        PostJson(new { type = "focus" });
                        _focusPending = false;
                    }
                    break;
                case "send":
                    var promptText = root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
                    if (!TryReadAttachments(root, out var attachments, out var attachmentError))
                    {
                        PostJson(new { type = "sendFailed", text = promptText, message = attachmentError });
                        break;
                    }
                    await SendPromptAsync(promptText, attachments);
                    break;
                case "pickAttachments":
                    await PickAttachmentsAsync();
                    break;
                case "stop":
                    if (_session != null) await ClaudeSdkSessionManager.Instance.InterruptAsync(_session.Id);
                    break;
                case "permission":
                    if (_session != null)
                    {
                        var requestId = root.TryGetProperty("requestId", out var request) ? request.GetString() ?? "" : "";
                        var allow = root.TryGetProperty("allow", out var allowed) && allowed.ValueKind == JsonValueKind.True;
                        var answer = root.TryGetProperty("answer", out var answerValue) ? answerValue.GetString() : null;
                        Dictionary<string, string>? answers = null;
                        if (root.TryGetProperty("answers", out var answersValue)
                            && answersValue.ValueKind == JsonValueKind.Object)
                        {
                            answers = new Dictionary<string, string>();
                            foreach (var property in answersValue.EnumerateObject())
                                if (property.Value.ValueKind == JsonValueKind.String)
                                    answers[property.Name] = property.Value.GetString() ?? "";
                        }
                        await ClaudeSdkSessionManager.Instance.RespondPermissionAsync(
                            _session.Id, requestId, allow, answer, answers);
                    }
                    break;
                case "setModel":
                    if (_session != null)
                    {
                        var model = root.TryGetProperty("model", out var modelValue)
                            ? modelValue.GetString() ?? ""
                            : "";
                        UserInteracted?.Invoke();
                        await ClaudeSdkSessionManager.Instance.SetModelAsync(_session.Id, model);
                    }
                    break;
                case "setEffort":
                    if (_session != null)
                    {
                        var effort = root.TryGetProperty("effort", out var effortValue)
                            ? effortValue.GetString() ?? ""
                            : "";
                        UserInteracted?.Invoke();
                        await ClaudeSdkSessionManager.Instance.SetEffortAsync(_session.Id, effort);
                    }
                    break;
                case "setPermissionMode":
                    if (_session != null)
                    {
                        var permissionMode = root.TryGetProperty("permissionMode", out var permissionValue)
                            ? permissionValue.GetString() ?? "default"
                            : "default";
                        UserInteracted?.Invoke();
                        await ClaudeSdkSessionManager.Instance.SetPermissionModeAsync(_session.Id, permissionMode);
                    }
                    break;
                case "setVibeMode":
                    var vibeEnabled = root.TryGetProperty("enabled", out var vibeValue)
                                      && vibeValue.ValueKind == JsonValueKind.True;
                    SettingsService.SaveClaudeVibeMode(vibeEnabled);
                    UserInteracted?.Invoke();
                    break;
                case "setShowSkills":
                    var showSkills = root.TryGetProperty("enabled", out var showSkillsValue)
                                     && showSkillsValue.ValueKind == JsonValueKind.True;
                    SettingsService.SaveClaudeShowSkills(showSkills);
                    UserInteracted?.Invoke();
                    break;
                case "adjustZoom":
                    if (_webView != null
                        && root.TryGetProperty("direction", out var zoomDirectionValue)
                        && zoomDirectionValue.TryGetInt32(out var zoomDirection)
                        && zoomDirection != 0)
                    {
                        var zoomFactor = Math.Clamp(
                            _webView.ZoomFactor + (zoomDirection > 0 ? 0.1 : -0.1),
                            0.75,
                            1.75);
                        _webView.ZoomFactor = Math.Round(zoomFactor, 2);
                        SettingsService.SaveClaudeGuiZoomFactor(_webView.ZoomFactor);
                        UserInteracted?.Invoke();
                    }
                    break;
                case "refreshCapabilities":
                    if (_session != null)
                        await ClaudeSdkSessionManager.Instance.RefreshCapabilitiesAsync(_session.Id);
                    break;
                case "copy":
                    var copyText = root.TryGetProperty("text", out var clipboardText) ? clipboardText.GetString() ?? "" : "";
                    if (copyText.Length > 0) Clipboard.SetText(copyText);
                    break;
                case "interact":
                    UserInteracted?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write($"ClaudeChat web message failed: {ex}");
        }
    }

    private void FlushPendingInsertion()
    {
        if (!_pageReady || _session == null || !_pendingInsertions.Remove(_session.Id, out var text)) return;
        PostJson(new { type = "insertText", text });
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) return;
        DiagLog.Write($"ClaudeChat navigation failed: {e.WebErrorStatus}");
        ShowInitError($"채팅 리소스를 불러오지 못했습니다. ({e.WebErrorStatus})");
    }

    private void OnReadyTimeout(object? sender, EventArgs e)
    {
        if (_pageReady) return;
        DiagLog.Write("ClaudeChat pageReady timeout");
        ShowInitError("채팅 페이지가 제한 시간 안에 응답하지 않았습니다.");
    }

    private void StopReadyTimeout()
    {
        if (_readyTimeout == null) return;
        _readyTimeout.Stop();
        _readyTimeout.Tick -= OnReadyTimeout;
        _readyTimeout = null;
    }

    private async Task SendPromptAsync(string text, IReadOnlyList<ClaudeSdkAttachment> attachments)
    {
        var session = _session;
        text = text.Trim();
        if (session == null || (text.Length == 0 && attachments.Count == 0)) return;
        UserInteracted?.Invoke();
        if (!await ClaudeSdkSessionManager.Instance.SendPromptAsync(session, _cwd, text, attachments))
            PostJson(new { type = "sendFailed", text, attachments, message = "Claude SDK에 요청을 전달하지 못했습니다." });
    }

    private async Task PickAttachmentsAsync()
    {
        if (_session == null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "첨부할 사진 또는 파일 선택",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "모든 파일|*.*|이미지|*.png;*.jpg;*.jpeg;*.jfif;*.gif;*.webp",
        };
        var owner = Window.GetWindow(this);
        var selected = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (selected != true) return;

        var paths = dialog.FileNames.Take(MaxAttachments).ToArray();
        var items = new List<object>(paths.Length);
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                var mediaType = ImageMediaType(path);
                if (mediaType == null)
                {
                    items.Add(new { kind = "file", name = info.Name, path = info.FullName, size = info.Length });
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(info.FullName);
                var data = Convert.ToBase64String(bytes);
                if (data.Length > MaxImageEncodedBytes)
                {
                    errors.Add($"{info.Name}: Base64 기준 10MB를 초과합니다.");
                    continue;
                }
                items.Add(new { kind = "image", name = info.Name, mediaType, data, size = info.Length });
            }
            catch (Exception ex)
            {
                errors.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}");
            }
        }
        if (items.Count > 0) PostJson(new { type = "addAttachments", items });
        if (dialog.FileNames.Length > MaxAttachments)
            errors.Add($"한 번에 최대 {MaxAttachments}개까지 첨부할 수 있습니다.");
        if (errors.Count > 0) PostJson(new { type = "attachmentError", message = string.Join("\n", errors) });
    }

    private static bool TryReadAttachments(
        JsonElement root,
        out IReadOnlyList<ClaudeSdkAttachment> attachments,
        out string error)
    {
        var result = new List<ClaudeSdkAttachment>();
        attachments = result;
        error = "";
        if (!root.TryGetProperty("attachments", out var values) || values.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        if (values.ValueKind != JsonValueKind.Array)
        {
            error = "첨부 파일 정보가 올바르지 않습니다.";
            return false;
        }
        if (values.GetArrayLength() > MaxAttachments)
        {
            error = $"첨부 파일은 최대 {MaxAttachments}개까지 전송할 수 있습니다.";
            return false;
        }

        var totalEncoded = 0;
        foreach (var value in values.EnumerateArray())
        {
            var kind = String(value, "kind");
            var name = SafeName(String(value, "name"));
            if (kind == "image")
            {
                var mediaType = String(value, "mediaType");
                var data = String(value, "data");
                if (!ImageMediaTypes.Contains(mediaType) || !IsBase64(data))
                {
                    error = $"{name}: 지원하지 않거나 손상된 이미지입니다.";
                    return false;
                }
                totalEncoded += data.Length;
                if (data.Length > MaxImageEncodedBytes || totalEncoded > MaxTotalImageEncodedBytes)
                {
                    error = $"{name}: 첨부 이미지 크기 제한을 초과했습니다.";
                    return false;
                }
                var preview = String(value, "preview");
                if (preview.Length > 512 * 1024 || (preview.Length > 0 && !preview.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
                    preview = "";
                result.Add(new ClaudeSdkAttachment(
                    "image", name, mediaType, data, preview, Size: Long(value, "size"),
                    Width: Integer(value, "width"), Height: Integer(value, "height")));
                continue;
            }
            if (kind == "file")
            {
                var path = String(value, "path");
                try { path = System.IO.Path.GetFullPath(path); }
                catch { path = ""; }
                if (path.Length == 0 || !File.Exists(path))
                {
                    error = $"{name}: 파일 경로를 확인할 수 없습니다.";
                    return false;
                }
                result.Add(new ClaudeSdkAttachment("file", name, Path: path, Size: new FileInfo(path).Length));
                continue;
            }
            error = $"{name}: 지원하지 않는 첨부 형식입니다.";
            return false;
        }
        return true;
    }

    private static string String(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "" : "";

    private static int Integer(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : 0;

    private static long Long(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.TryGetInt64(out var number) ? number : 0;

    private static string SafeName(string value)
    {
        value = System.IO.Path.GetFileName(value.Trim());
        if (value.Length == 0) return "첨부 파일";
        return value.Length <= 180 ? value : value[..180];
    }

    private static bool IsBase64(string value)
    {
        if (value.Length == 0 || value.Length % 4 != 0) return false;
        var padding = value.EndsWith("==", StringComparison.Ordinal) ? 2 : value.EndsWith('=') ? 1 : 0;
        for (var index = 0; index < value.Length - padding; index++)
        {
            var c = value[index];
            if (!char.IsAsciiLetterOrDigit(c) && c is not '+' and not '/') return false;
        }
        for (var index = value.Length - padding; index < value.Length; index++)
            if (value[index] != '=') return false;
        return true;
    }

    private static string? ImageMediaType(string path)
        => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jfif" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };

    private void SyncActiveSession()
    {
        if (!_pageReady || _session == null) return;
        var events = ClaudeSdkSessionManager.Instance.GetEvents(_session.Id).ToList();
        if (!events.Any(item => item.Type is "user" or "assistant"))
        {
            var transcript = SessionExporter.LoadClaudeConversation(_session.Id, _cwd)
                .Select(item => new ClaudeSdkEvent(item.Role == "user" ? "user" : "assistant", item.Text));
            events.InsertRange(0, transcript);
        }
        PostJson(new
        {
            type = "session",
            roomId = _session.Id,
            events,
            busy = _session.IsBusy,
            waiting = _session.IsWaitingChoice,
            alive = _session.IsAlive,
            model = SettingsService.LoadClaudeCodeRoomModel(_session.Id) ?? "",
            effort = SettingsService.LoadClaudeCodeRoomEffort(_session.Id) ?? "high",
            permissionMode = SettingsService.LoadClaudeCodeRoomPermissionMode(_session.Id),
            vibeMode = SettingsService.LoadClaudeVibeMode(),
            showSkills = SettingsService.LoadClaudeShowSkills(),
        });
    }

    private void OnSdkEvent(string roomId, ClaudeSdkEvent sdkEvent)
    {
        if (_disposed || _session?.Id != roomId) return;
        if (sdkEvent.Type is "assistant_delta" or "thinking_delta")
        {
            QueueStreamDelta(roomId, sdkEvent);
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                () => OnSdkEvent(roomId, sdkEvent),
                System.Windows.Threading.DispatcherPriority.Render);
            return;
        }

        FlushPendingStreamDeltas(roomId);
        if (sdkEvent.Type == "result") ResponseCompleted?.Invoke(roomId);
        PostJson(new { type = "event", roomId, @event = sdkEvent });
    }

    private void QueueStreamDelta(string roomId, ClaudeSdkEvent sdkEvent)
    {
        lock (_streamDeltaLock)
        {
            if (_disposed || _session?.Id != roomId) return;
            var key = (sdkEvent.Type, sdkEvent.StreamId);
            if (!_pendingStreamDeltas.TryGetValue(key, out var buffer))
            {
                buffer = new StringBuilder();
                _pendingStreamDeltas[key] = buffer;
            }
            buffer.Append(sdkEvent.Text);
            if (_streamDeltaFlushScheduled) return;
            _streamDeltaFlushScheduled = true;
        }
        Dispatcher.BeginInvoke(
            () => FlushPendingStreamDeltas(roomId),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void FlushPendingStreamDeltas(string roomId)
    {
        ClaudeSdkEvent[] pending;
        lock (_streamDeltaLock)
        {
            _streamDeltaFlushScheduled = false;
            if (_pendingStreamDeltas.Count == 0 || _disposed || _session?.Id != roomId)
            {
                _pendingStreamDeltas.Clear();
                return;
            }
            pending = _pendingStreamDeltas
                .Select(item => new ClaudeSdkEvent(
                    item.Key.Type, Text: item.Value.ToString(), StreamId: item.Key.StreamId))
                .ToArray();
            _pendingStreamDeltas.Clear();
        }

        foreach (var sdkEvent in pending)
            PostJson(new { type = "event", roomId, @event = sdkEvent });
    }

    private void ClearPendingStreamDeltas()
    {
        lock (_streamDeltaLock)
        {
            _streamDeltaFlushScheduled = false;
            _pendingStreamDeltas.Clear();
        }
    }

    private void OnThemeChanged(string _) => Dispatcher.BeginInvoke(new Action(ApplyTheme));

    private void ApplyTheme()
    {
        try { if (_webView != null) _webView.DefaultBackgroundColor = CurrentBackgroundColor(); } catch { }
        if (!_pageReady) return;
        PostJson(new
        {
            type = "setTheme",
            theme = App.CurrentTheme,
            dark = App.IsDarkTheme(App.CurrentTheme),
            terminalBg = Hex("TerminalBgBrush", "#1f1f1e"),
            bg = Hex("BgBrush", "#1f1f1e"),
            panel = Hex("PanelBrush", "#272727"),
            panelSoft = Hex("PanelSoftBrush", "#2f2f2f"),
            line = Hex("LineBrush", "#404040"),
            text = Hex("TextBrush", "#e8e8e8"),
            muted = Hex("TextMutedBrush", "#aaaaaa"),
            primary = Hex("PrimaryBrush", "#c2622a"),
            primarySoft = Hex("PrimarySoftBrush", "#434343"),
            danger = Hex("DangerBrush", "#ef4444"),
            success = Hex("SuccessBrush", "#22c55e"),
            codeBg = Hex("CodeBgBrush", "#1f1f1e"),
            codePanel = Hex("CodePanelBrush", "#272727"),
            codeBorder = Hex("CodeBorderBrush", "#404040"),
            codeText = Hex("CodeTextBrush", "#e8e8e8"),
            codeMuted = Hex("CodeMutedBrush", "#aaaaaa"),
            codeLabelBg = Hex("CodeLabelBgBrush", "#363636"),
            codeLabelBorder = Hex("CodeLabelBorderBrush", "#424242"),
            codeLabelText = Hex("CodeLabelTextBrush", "#e8e8e8"),
            syntaxKeyword = Hex("CodeSyntaxKeywordBrush", "#a78bfa"),
            syntaxString = Hex("CodeSyntaxStringBrush", "#4ade80"),
            syntaxNumber = Hex("CodeSyntaxNumberBrush", "#fb923c"),
            syntaxFunction = Hex("CodeSyntaxFunctionBrush", "#60a5fa"),
            syntaxType = Hex("CodeSyntaxTypeBrush", "#2dd4bf"),
        });
    }

    private static System.Drawing.Color CurrentBackgroundColor()
    {
        if (Application.Current?.TryFindResource("TerminalBgBrush") is SolidColorBrush brush)
            return System.Drawing.Color.FromArgb(0xff, brush.Color.R, brush.Color.G, brush.Color.B);
        return System.Drawing.Color.FromArgb(0xff, 0x1f, 0x1f, 0x1e);
    }

    private static string Hex(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is not SolidColorBrush brush) return fallback;
        var color = brush.Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void PostJson(object message)
    {
        if (!_pageReady || _webView?.CoreWebView2 == null) return;
        try { _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, CamelCase)); }
        catch (Exception ex) { DiagLog.Write($"ClaudeChat post failed: {ex.Message}"); }
    }

    public async Task<System.Windows.Media.Imaging.BitmapSource?> CaptureSnapshotAsync()
    {
        if (_webView?.CoreWebView2 == null || !_pageReady) return null;
        try
        {
            using var stream = new MemoryStream();
            await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Position = 0;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private void ShowInitError(string message)
    {
        StopReadyTimeout();
        WebViewHost.Children.Clear();
        WebViewHost.Children.Add(new TextBlock
        {
            Text = "AI 채팅 화면을 시작할 수 없습니다.\nWebView2 런타임을 확인하세요.\n\n" + message,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24),
            Foreground = Application.Current.TryFindResource("TextMutedBrush") as Brush,
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClaudeSdkSessionManager.Instance.EventReceived -= OnSdkEvent;
        App.ThemeChanged -= OnThemeChanged;
        StopReadyTimeout();
        ClearPendingStreamDeltas();
        try
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
        }
        catch { }
        try { _webView?.Dispose(); } catch { }
        _webView = null;
    }
}
