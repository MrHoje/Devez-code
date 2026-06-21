using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DevezCode.Services.Terminal;

namespace DevezCode.Views;

/// <summary>
/// 채팅방 메시지 영역을 덮는 임베디드 터미널 호스트.
/// WebView2 1개 + 로컬 번들 xterm.js. 방 전환 시 JS 쪽 xterm 인스턴스만 스위칭하고
/// ConPTY 세션(TerminalSessionManager)은 방마다 유지된다.
/// </summary>
public sealed class TerminalHostView : ContentControl, IDisposable
{
    private const string VirtualHost = "terminal.devezcode.local";

    /// <summary>해당 방의 셸이 첫 출력을 내보내(=터미널이 그려질 준비) 발생. roomId 전달.</summary>
    public event Action<string>? TerminalReady;
    /// <summary>방의 ConPTY 세션이 생성/배선되어 살아있음. roomId 전달.</summary>
    public event Action<string>? SessionStarted;
    /// <summary>방의 셸 프로세스가 종료됨(끊김/죽음). roomId 전달.</summary>
    public event Action<string>? SessionExited;
    /// <summary>터미널에서 세션(탭) 단축키 발생 — name: newSession/closeSession/nextSession/prevSession/gotoSession.
    /// gotoSession 일 때 index = 0-기준 세션 번호(-1 = 마지막), 그 외엔 의미 없음.</summary>
    public event Action<string, int>? SessionActionRequested;

    private WebView2? _webView;
    private bool _initStarted;
    private bool _pageReady;
    private string? _pendingShowRoomId;
    private readonly List<string> _pendingPreload = new(); // pageReady 전에 들어온 백그라운드 로드 요청
    private string? _activeRoomId;
    private double _fontSizePt = -1; // -1 = config에서 아직 읽지 않음
    private readonly Action<string> _themeChangedHandler;

    private const double PtToPx = 96.0 / 72.0;

    /// <summary>roomId → 현재 JS와 배선된 세션 (재시작 시 교체 감지용).</summary>
    private readonly Dictionary<string, TerminalSession> _wired = new();

    /// <summary>claude 등 풀스크린 TUI가 떠서(alt-screen 진입) 준비된 방. UI 스레드에서만 접근.</summary>
    private readonly HashSet<string> _ready = new();
    /// <summary>alt-screen 시퀀스 감지용 방별 누적 버퍼 (청크 경계 분할 대비). UI 스레드에서만 접근.</summary>
    private readonly Dictionary<string, string> _readyScan = new();
    /// <summary>alt-screen 진입 후 출력이 잠잠해지길 기다리는 방별 타이머(=실제 프롬프트 렌더 완료 근사). UI 스레드.</summary>
    private readonly Dictionary<string, System.Windows.Threading.DispatcherTimer> _settleTimers = new();
    /// <summary>TerminalReady 를 이미 통지한 방(중복 통지 방지). UI 스레드.</summary>
    private readonly HashSet<string> _readyNotified = new();
    /// <summary>alt-screen 진입 후 이만큼 추가 출력이 없으면 "준비 완료"로 본다.</summary>
    private static readonly TimeSpan SettleQuiet = TimeSpan.FromMilliseconds(600);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public TerminalHostView()
    {
        _themeChangedHandler = _ => { PushCurrentTheme(); RestartOpenCodeSessions(); };
        App.ThemeChanged += _themeChangedHandler;
    }

    /// <summary>해당 방의 claude 화면이 이미 떠서 안정화까지 끝났는지(로딩 불필요).</summary>
    public bool IsReady(string roomId) => _readyNotified.Contains(roomId);

    /// <summary>해당 방의 터미널을 표시 (필요 시 WebView2 초기화·세션 생성).</summary>
    public async void ShowTerminal(string roomId)
    {
        _activeRoomId = roomId;
        if (!_initStarted)
        {
            _initStarted = true;
            await InitWebViewAsync();
        }
        if (_pageReady) PostJson(new { type = "show", roomId });
        else _pendingShowRoomId = roomId; // pageReady 때 처리

        // 이미 안정화까지 끝난 방이면 즉시 준비 완료 통지 → 로딩 스킵
        if (_readyNotified.Contains(roomId))
            Dispatcher.BeginInvoke(() => TerminalReady?.Invoke(roomId));
    }

    /// <summary>화면에 표시하지 않고 방의 xterm 인스턴스+ConPTY 세션만 미리 생성(백그라운드 로드).
    /// 프로젝트 선택 시 비활성 세션들을 함께 띄워두는 용도. show 가 아니라 활성 방을 바꾸지 않는다.</summary>
    public async void PreloadTerminal(string roomId)
    {
        if (!_initStarted)
        {
            _initStarted = true;
            await InitWebViewAsync();
        }
        if (_pageReady) PostJson(new { type = "preload", roomId });
        else if (!_pendingPreload.Contains(roomId)) _pendingPreload.Add(roomId);
    }

    /// <summary>출력에서 alt-screen 진입 시퀀스를 찾고, 이후 출력이 잠잠해지면 준비 완료를 통지 (UI 스레드).</summary>
    private void ScanForReady(string roomId, byte[] bytes)
    {
        if (_ready.Contains(roomId))
        {
            // alt-screen 은 봤지만 아직 통지 전 — 출력이 계속되는 동안 안정화 타이머를 미룬다.
            BumpSettle(roomId);
            return;
        }
        // 찾는 시퀀스는 모두 ASCII 제어/문자라 ASCII 디코드로 충분
        var text = (_readyScan.TryGetValue(roomId, out var prev) ? prev : string.Empty)
                   + System.Text.Encoding.ASCII.GetString(bytes);
        if (text.Contains("[?1049h") || text.Contains("[?47h")) // 풀스크린 TUI(claude 등) 시작
        {
            _ready.Add(roomId);
            _readyScan.Remove(roomId);
            BumpSettle(roomId); // 즉시 통지하지 않고, 출력이 멎을 때까지 대기
            return;
        }
        _readyScan[roomId] = text.Length > 512 ? text[^512..] : text; // 버퍼 과다 방지
    }

    /// <summary>alt-screen 진입 후 출력이 올 때마다 안정화 타이머를 리셋. 만료되면(출력이 멎으면) TerminalReady 통지.</summary>
    private void BumpSettle(string roomId)
    {
        if (_readyNotified.Contains(roomId)) return; // 이미 통지함
        if (!_settleTimers.TryGetValue(roomId, out var t))
        {
            t = new System.Windows.Threading.DispatcherTimer { Interval = SettleQuiet };
            t.Tick += (_, _) =>
            {
                t.Stop();
                _settleTimers.Remove(roomId);
                if (_readyNotified.Add(roomId)) TerminalReady?.Invoke(roomId);
            };
            _settleTimers[roomId] = t;
        }
        t.Stop();
        t.Start();
    }

    /// <summary>pageReady 전에 들어온 포커스 요청 보류 플래그 (첫 init 중 호출 대비).</summary>
    private bool _pendingFocus;

    public void FocusTerminal()
    {
        if (_pageReady && _activeRoomId != null)
        {
            _webView?.Focus();
            PostJson(new { type = "focus", roomId = _activeRoomId });
        }
        else
        {
            // 첫 생성 시 WebView2 초기화가 끝나기 전 — OnPageReady 에서 적용
            _pendingFocus = true;
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            _webView = new WebView2();
            Content = _webView;

            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevezCode", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _webView.EnsureCoreWebView2Async(env);
            // 리사이즈(패널 접기/펴기) 중 WebView2 가 흰색으로 클리어했다 다시 그리며
            // 깜빡이는 것을 막는다 — 페인트 전 기본 배경을 터미널 배경(#0C0C0C)에 맞춤.
            _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C);
            // 초기화 완료 시 호스트가 숨겨진 상태라면 WPF 렌더 큐를 비워
            // 새로 생성된 HWND에 Collapsed 상태가 반영되기 전 한 프레임 튀는 현상을 방지한다.
            if (!IsVisible)
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false; // F5 새로고침 등 차단 (터미널 보호)
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            var webRoot = Path.Combine(AppContext.BaseDirectory, "Resources", "Terminal", "web");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, webRoot, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessageReceived;
            // terminal.html 이 바뀔 때마다 새로 로드되도록 캐시 무력화(WebView2 가상호스트 응답 캐시 회피)
            long ver = 0;
            try { ver = File.GetLastWriteTimeUtc(Path.Combine(webRoot, "terminal.html")).Ticks; } catch { }
            core.Navigate($"https://{VirtualHost}/terminal.html?v={ver}");
        }
        catch (Exception ex)
        {
            // WebView2 런타임 미설치 등 — 안내 문구로 대체
            Content = new TextBlock
            {
                Text = "터미널을 시작할 수 없습니다.\nWebView2 런타임이 필요합니다.\n\n" + ex.Message,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
            };
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "pageReady":
                    OnPageReady();
                    break;
                case "created":
                    WireSession(
                        root.GetProperty("roomId").GetString()!,
                        root.GetProperty("cols").GetInt32(),
                        root.GetProperty("rows").GetInt32());
                    break;
                case "input":
                {
                    var data = root.GetProperty("data").GetString() ?? "";
                    if (Environment.GetEnvironmentVariable("DEVEZCODE_TERM_LOG") == "1")
                    {
                        try
                        {
                            var p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devezcode-input.log");
                            System.IO.File.AppendAllText(p, data.Replace("\x1b", "<ESC>") + "\n");
                        }
                        catch (Exception) { }
                    }
                    // 포커스 리포팅(DEC 1004) 억제: IME 조합 시 helper-textarea blur 로
                    // xterm 이 focus-out(ESC[O)을 보내면 claude 가 "포커스 잃음"으로 판단해
                    // 커서를 입력 캐럿에서 치운다 → 조합 글자가 화면 끝으로 날아간다.
                    // focus-in/out 을 claude 로 전달하지 않아 항상 포커스 상태로 유지한다.
                    if (data is "\x1b[O" or "\x1b[I") break;
                    TerminalSessionManager.Instance
                        .Get(root.GetProperty("roomId").GetString()!)?.Write(data);
                    break;
                }
                case "resize":
                    TerminalSessionManager.Instance
                        .Get(root.GetProperty("roomId").GetString()!)
                        ?.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
                    break;
                case "restart":
                {
                    var roomId = root.GetProperty("roomId").GetString()!;
                    WireSession(roomId, 120, 30); // restarted 후 JS가 실제 크기로 resize 보냄
                    PostJson(new { type = "restarted", roomId });
                    break;
                }
                case "copy":
                {
                    var data = root.GetProperty("data").GetString() ?? "";
                    if (data.Length > 0) SetClipboardText(data);
                    break;
                }
                case "requestPaste":
                {
                    var roomId = root.GetProperty("roomId").GetString()!;
                    var imagePath = SaveClipboardImage();
                    if (imagePath != null)
                    {
                        PostJson(new { type = "paste", roomId, data = imagePath });
                    }
                    else
                    {
                        var text = GetClipboardText();
                        if (!string.IsNullOrEmpty(text))
                            PostJson(new { type = "paste", roomId, data = text });
                    }
                    break;
                }
                case "action":
                {
                    var name = root.GetProperty("name").GetString() ?? "";
                    switch (name)
                    {
                        case "fontInc":   AdjustFontSize(+1); break;
                        case "fontDec":   AdjustFontSize(-1); break;
                        case "fontReset": ResetFontSize();    break;
                        default:
                            int index = root.TryGetProperty("index", out var ie) ? ie.GetInt32() : 0;
                            SessionActionRequested?.Invoke(name, index);
                            break;
                    }
                    break;
                }
            }
        }
        catch (Exception) { /* 비정상 메시지 무시 */ }
    }

    private void OnPageReady()
    {
        _pageReady = true;
        var cfg = TerminalSessionManager.Instance.Config;
        var savedPt = DevezCode.Services.SettingsService.LoadTerminalFontSizePt();
        if (savedPt > 0) _fontSizePt = savedPt;
        double fontSizePx = _fontSizePt > 0
            ? Math.Round(_fontSizePt * PtToPx, 1)
            : cfg.FontSizePx;
        PostJson(new
        {
            type = "init",
            theme = cfg.Scheme,
            fontFamily = cfg.FontFamily,
            fontSize = fontSizePx,
            windowsBuild = Environment.OSVersion.Version.Build, // xterm windowsPty 휴리스틱 판정용
        });
        var pending = _pendingShowRoomId ?? _activeRoomId;
        _pendingShowRoomId = null;
        if (pending != null) PostJson(new { type = "show", roomId = pending });

        // 보류된 백그라운드 로드 처리 (show 로 이미 만들어진 방은 JS preload 가 스킵)
        foreach (var r in _pendingPreload)
            if (r != pending) PostJson(new { type = "preload", roomId = r });
        _pendingPreload.Clear();

        // 보류된 포커스 적용 — 그 사이 다른 방(채팅 등)으로 전환했으면 훔치지 않음
        if (_pendingFocus)
        {
            _pendingFocus = false;
            if (IsVisible) _webView?.Focus(); // JS 쪽은 show()가 term.focus() 처리
        }
    }

    /// <summary>세션을 가져오거나 만들고 출력·종료 이벤트를 JS로 배선.</summary>
    private void WireSession(string roomId, int cols, int rows)
    {
        // 삭제된 방이면 뒤늦게 도착한 생성 요청을 무시한다(삭제 후 claude 가 다시 떠 고아가 되는 것 방지)
        if (TerminalSessionManager.Instance.IsRoomDisposed(roomId)) return;

        TerminalSession session;
        try
        {
            session = TerminalSessionManager.Instance.GetOrCreate(roomId, cols, rows);
        }
        catch (Exception ex)
        {
            // 셸 실행 실패 등 — 빈 화면 대신 에러를 터미널에 표시
            var err = $"\r\n\x1b[91m터미널 세션을 시작할 수 없습니다:\r\n{ex.Message}\x1b[0m\r\n";
            PostJson(new
            {
                type = "output",
                roomId,
                data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(err)),
            });
            SessionExited?.Invoke(roomId); // 시작 실패 → 죽은 상태로 표시
            return;
        }
        SessionStarted?.Invoke(roomId); // ConPTY 프로세스 살아있음
        if (_wired.TryGetValue(roomId, out var prev) && ReferenceEquals(prev, session))
            return; // 이미 배선됨
        _wired[roomId] = session;

        // 셸 첫 출력(준비 완료 신호) 이후에 claude 커맨드 전송 — PSReadLine 초기화 완료 보장
        var initialCmd = TerminalSessionManager.Instance.GetInitialCommand(roomId);
        bool cmdSent = initialCmd == null; // 커맨드 없으면 전송 불필요

        session.OutputReceived += bytes =>
        {
            if (!cmdSent)
            {
                cmdSent = true;
                session.Write(initialCmd!);
            }
            var b64 = Convert.ToBase64String(bytes);
            Dispatcher.BeginInvoke(() =>
            {
                ScanForReady(roomId, bytes); // claude 화면이 뜨면 로딩 스피너 종료
                PostJson(new { type = "output", roomId, data = b64 });
            });
        };
        session.Exited += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                // 이 세션이 이미 새 세션으로 교체되었으면(테마 재시작 등) 종료 프롬프트를 띄우지 않는다.
                // Exited 는 WaitForExit 스레드에서 뒤늦게 와서 "restarted" 이후 도착할 수 있다.
                if (!_wired.TryGetValue(roomId, out var cur) || !ReferenceEquals(cur, session)) return;
                PostJson(new { type = "exited", roomId });
                SessionExited?.Invoke(roomId); // 끊김/죽음 → 회색 점
            });
        };
    }

    /// <summary>현재 활성 방의 xterm.js 폰트 크기만 즉시 변경. Devez 설정에 영구 저장.</summary>
    public void AdjustFontSize(int deltaPt)
    {
        if (_fontSizePt < 0)
        {
            var saved = DevezCode.Services.SettingsService.LoadTerminalFontSizePt();
            _fontSizePt = saved > 0
                ? saved
                : Math.Round(TerminalSessionManager.Instance.Config.FontSizePx / PtToPx);
        }
        _fontSizePt = Math.Max(6, Math.Min(72, _fontSizePt + deltaPt));
        double px = Math.Round(_fontSizePt * PtToPx, 1);

        PostJson(new { type = "adjustFontSize", size = px });
        DevezCode.Services.SettingsService.SaveTerminalFontSizePt((int)_fontSizePt);
    }

    /// <summary>폰트 크기를 WT 설정 기본값으로 초기화 (Ctrl+0). 영구 저장.</summary>
    public void ResetFontSize()
    {
        _fontSizePt = Math.Round(TerminalSessionManager.Instance.Config.FontSizePx / PtToPx);
        _fontSizePt = Math.Max(6, Math.Min(72, _fontSizePt));
        double px = Math.Round(_fontSizePt * PtToPx, 1);
        PostJson(new { type = "adjustFontSize", size = px });
        DevezCode.Services.SettingsService.SaveTerminalFontSizePt((int)_fontSizePt);
    }

    /// <summary>클립보드에 텍스트 기록 (잠금 충돌 대비 재시도).
    /// SetText 는 지연 렌더링(copy=false)이라 WebView2(별도 Edge 프로세스) 환경에서
    /// 실제 데이터가 클립보드에 안 올라오는 경우가 있어, 즉시 flush 하는
    /// SetDataObject(text, true) 로 기록한다(OleFlushClipboard).</summary>
    private static void SetClipboardText(string text)
    {
        for (int i = 0; i < 10; i++)
        {
            try { System.Windows.Clipboard.SetDataObject(text, true); return; }
            catch { System.Threading.Thread.Sleep(40); }
        }
    }

    private static string GetClipboardText()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; }
            catch { System.Threading.Thread.Sleep(20); }
        }
        return "";
    }

    /// <summary>클립보드에 이미지가 있으면 %TEMP%\DevezCode\clipboard\ 에 저장 후 파일 경로 반환.
    /// 이미지가 없거나 저장 실패 시 null.</summary>
    private static string? SaveClipboardImage()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsImage()) return null;
                using var ms = new System.IO.MemoryStream();
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
                    System.Windows.Clipboard.GetImage()));
                encoder.Save(ms);
                var dir = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "DevezCode", "clipboard");
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir,
                    $"clip_{DateTime.Now:yyyyMMddHHmmssfff}.png");
                System.IO.File.WriteAllBytes(path, ms.ToArray());
                return path;
            }
            catch { System.Threading.Thread.Sleep(20); }
        }
        return null;
    }

    /// <summary>현재 터미널 화면을 PNG 스냅샷으로 반환. airspace 우회용.</summary>
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
        try
        {
            _webView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, CamelCase));
        }
        catch (Exception) { /* WebView2 해제 중 등 */ }
    }

    /// <summary>현재 활성 스킴을 모든 xterm 인스턴스에 즉시 반영. 테마 변경 시 호출.</summary>
    private void PushCurrentTheme()
    {
        if (!_pageReady) return; // pageReady 시 OnPageReady 가 init 으로 보내줌
        var scheme = TerminalSessionManager.Instance.Config.Scheme;
        PostJson(new { type = "theme", theme = scheme });
    }

    /// <summary>OpenCode 는 tui.json 을 시작 시에만 읽으므로 테마 변경 시 살아있는 세션을 재시작한다.
    /// 매니저가 tui.json 을 새 테마로 기록한 뒤, 여기서 세션을 종료→재생성하고 JS 를 리셋해
    /// "Enter 로 재시작" 프롬프트 없이 매끄럽게 새 테마로 다시 띄운다.</summary>
    private void RestartOpenCodeSessions()
    {
        if (!_pageReady) return;
        foreach (var roomId in TerminalSessionManager.Instance.GetOpenCodeRoomIds())
        {
            if (!_wired.TryGetValue(roomId, out var s) || !s.IsAlive) continue;
            TerminalSessionManager.Instance.KillSession(roomId); // 기존 세션 종료·제거
            WireSession(roomId, 120, 30);                        // 새 세션(새 tui.json 테마) 생성·배선
            PostJson(new { type = "restarted", roomId });        // JS xterm 리셋(dead 해제) + resize 유도
        }
    }

    /// <summary>방 삭제 시 호출 — 방별 배선·준비 상태와 JS 쪽 xterm 인스턴스를 정리.
    /// ConPTY 셸 세션은 TerminalSessionManager.DisposeRoom 이 별도로 정리한다.</summary>
    public void CloseTerminal(string roomId)
    {
        _wired.Remove(roomId);
        _ready.Remove(roomId);
        _readyScan.Remove(roomId);
        _readyNotified.Remove(roomId);
        if (_settleTimers.Remove(roomId, out var st)) st.Stop();
        _pendingPreload.Remove(roomId);
        if (_activeRoomId == roomId) _activeRoomId = null;
        if (_pendingShowRoomId == roomId) _pendingShowRoomId = null;
        PostJson(new { type = "dispose", roomId }); // JS xterm 인스턴스·DOM 해제
    }

    private bool _disposed;

    /// <summary>앱 종료 시 호출 — WebView2 + 이벤트 구독 해제 (Edge 렌더러 프로세스 잔류 방지).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { App.ThemeChanged -= _themeChangedHandler; } catch { }
        try
        {
            if (_webView?.CoreWebView2 != null)
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
        catch (Exception) { }
        try { _webView?.Dispose(); } catch (Exception) { }
        _webView = null;
        _wired.Clear();
        _ready.Clear();
        _readyScan.Clear();
        _readyNotified.Clear();
        foreach (var t in _settleTimers.Values) t.Stop();
        _settleTimers.Clear();
    }
}
