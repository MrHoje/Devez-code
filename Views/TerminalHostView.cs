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
    /// <summary>사용자가 단독 ESC 로 응답 취소를 요청. roomId 전달 — 구독자가 busy 스피너를 끈다.</summary>
    public event Action<string>? InterruptRequested;
    /// <summary>선택지 메뉴에 제출 입력(Enter/숫자키)이 들어옴 — 입력 대기 ❗ 해제용.</summary>
    public event Action<string>? MenuInputSubmitted;
    /// <summary>방의 셸 프로세스가 종료됨(끊김/죽음). roomId 전달.</summary>
    public event Action<string>? SessionExited;
    /// <summary>터미널에서 세션(탭) 단축키 발생 — name: newSession/closeSession/nextSession/prevSession/gotoSession.
    /// gotoSession 일 때 index = 0-기준 세션 번호(-1 = 마지막), 그 외엔 의미 없음.</summary>
    public event Action<string, int>? SessionActionRequested;
    /// <summary>터미널 폰트 크기(px)가 바뀜(Ctrl+휠/리셋/초기화). 세션 헤더 타이틀 동기화용.</summary>
    public event Action<double>? FontSizePxChanged;
    /// <summary>사용자가 WebView2 터미널 표면을 클릭/조작함. WPF PreviewMouseDown 이 HWND 경계를 넘지 못해 별도 통지한다.</summary>
    public event Action? UserInteracted;

    private WebView2? _webView;
    private bool _initStarted;
    private bool _pageReady;
    private bool _pendingLoading; // pageReady 전 SetLoading(true) 요청 보류 (콜드스타트 첫 세션 스피너)
    private string? _pendingShowRoomId;
    private readonly List<string> _pendingPreload = new(); // pageReady 전에 들어온 백그라운드 로드 요청
    private string? _activeRoomId;
    private double _fontSizePt = -1; // -1 = config에서 아직 읽지 않음
    private readonly Action<string> _themeChangedHandler;

    private const double PtToPx = 96.0 / 72.0;

    /// <summary>현재 유효 터미널 폰트 크기(px). 미설정이면 WT config 기본값.</summary>
    public double EffectiveFontSizePx
    {
        get
        {
            if (_fontSizePt > 0) return Math.Round(_fontSizePt * PtToPx, 1);
            var saved = DevezCode.Services.SettingsService.LoadTerminalFontSizePt();
            if (saved > 0) return Math.Round(saved * PtToPx, 1);
            return TerminalSessionManager.Instance.Config.FontSizePx;
        }
    }

    /// <summary>roomId → 현재 JS와 배선된 세션 (재시작 시 교체 감지용).</summary>
    private readonly Dictionary<string, TerminalSession> _wired = new();

    // ── 출력 배칭(coalescing) ──────────────────────────────────────
    // ConPTY-Read 백그라운드 스레드가 청크마다 OutputReceived 를 때리는데, 매 청크 BeginInvoke 하면
    // 다세션 동시 출력 시 UI 스레드 디스패처 큐가 폭증한다. roomId 별로 청크를 모아 한 번만 flush 를
    // 예약(coalesce)하면, UI 가 바쁠 때 여러 청크가 한 틱에 합쳐져 ScanForReady·PostJson·JS write 가
    // 1회로 줄어든다. UI 가 한가하면 거의 즉시 비워져 지연은 사실상 없다. _outLock 으로 BG/UI 동기화.
    private readonly object _outLock = new();
    private readonly Dictionary<string, List<byte[]>> _outPending = new();
    private readonly HashSet<string> _outScheduled = new();

    /// <summary>claude 등 풀스크린 TUI가 떠서(alt-screen 진입) 준비된 방. UI 스레드에서만 접근.</summary>
    private readonly HashSet<string> _ready = new();
    /// <summary>alt-screen 시퀀스 감지용 방별 누적 버퍼 (청크 경계 분할 대비). UI 스레드에서만 접근.</summary>
    private readonly Dictionary<string, string> _readyScan = new();
    /// <summary>alt-screen 진입 후 출력이 잠잠해지길 기다리는 방별 타이머(=실제 프롬프트 렌더 완료 근사). UI 스레드.</summary>
    private readonly Dictionary<string, System.Windows.Threading.DispatcherTimer> _settleTimers = new();
    /// <summary>TerminalReady 를 이미 통지한 방(중복 통지 방지). UI 스레드.</summary>
    private readonly HashSet<string> _readyNotified = new();
    /// <summary>alt-screen 진입 후 이만큼 추가 출력이 없으면 "준비 완료"로 본다.</summary>
    private static readonly TimeSpan SettleQuiet = TimeSpan.FromMilliseconds(300);
    /// <summary>alt-screen 진입 시각(Environment.TickCount). UI 스레드.</summary>
    private readonly Dictionary<string, int> _altSeenTick = new();
    /// <summary>alt-screen 진입 후 출력이 계속 흘러도(스피너/시계 등 끊임없는 redraw) 이 시각이 지나면
    /// 무조건 준비 완료로 본다 — settle 이 영원히 안 떨어져 오버레이가 20s 타임아웃까지 남는 것 방지.</summary>
    private const int MaxSettleAfterAltMs = 700;
    /// <summary>인라인 TUI(gjc): 첫 출력 시각. 준비 마커가 안 올 때 폴백 타임아웃 기준.</summary>
    private readonly Dictionary<string, int> _inlineFirstOutTick = new();
    /// <summary>인라인 TUI 준비 마커(\e[?2004h/\e[?2026h)가 안 와도 이만큼 지나면 준비로 본다(무한 스피너 방지).</summary>
    private const int InlineReadyFallbackMs = 8000;

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public TerminalHostView()
    {
        // 테마 변경 시 xterm 색만 라이브 갱신. 세션 재시작(claude/opencode 모두 시작 시 테마 로드)은
        // MainWindow.ReloadAllSessionsForTheme 가 커밋 시점에 일괄 처리한다.
        _themeChangedHandler = _ => PushCurrentTheme();
        App.ThemeChanged += _themeChangedHandler;
    }

    /// <summary>해당 방의 claude 화면이 이미 떠서 안정화까지 끝났는지(로딩 불필요).</summary>
    public bool IsReady(string roomId) => _readyNotified.Contains(roomId);
    /// <summary>세션 생성 없이 WebView2/xterm 페이지만 미리 띄워 첫 세션 클릭의 콜드스타트 비용을 앞당긴다.</summary>
    public void PrewarmWebView()
    {
        if (_disposed || _initStarted) return;
        _initStarted = true;
        _ = InitWebViewAsync();
    }

    /// <summary>방의 에이전트 ID(opencode 등) — JS 가 컨테이너 패딩 등 에이전트별 스타일에 사용.</summary>
    private static string AgentFor(string roomId) => DevezCode.Services.SettingsService.LoadAgentForRoom(roomId);

    /// <summary>해당 방의 터미널을 표시 (필요 시 WebView2 초기화·세션 생성).</summary>
    public async void ShowTerminal(string roomId)
    {
        _activeRoomId = roomId;
        if (!_initStarted)
        {
            _initStarted = true;
            await InitWebViewAsync();
        }
        if (_pageReady) { PostJson(new { type = "show", roomId, agent = AgentFor(roomId) }); PinBottomIfInline(roomId); }
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
        if (_pageReady) PostJson(new { type = "preload", roomId, agent = AgentFor(roomId) });
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

        // 인라인 렌더 에이전트(gjc 등)는 alt-screen 시퀀스가 없다. 단 첫 출력을 준비로 보면
        // cmd/ConPTY 초기화 노이즈(\e[?9001h\e[?1004h 등)가 gjc 본체보다 먼저 나와, 스피너가 빈 화면에서
        // 꺼진 뒤 gjc 가 수백ms~수초 후 실제 페인트하는 갭이 보인다. gjc 가 입력/첫 프레임을 그릴 때 내는
        // 마커로 실제 준비를 판정한다: \e[?2004h(bracketed paste=입력 준비) / \e[?2026h(synchronized update=프레임).
        // (cmd 초기화는 ?9001/?1004 만 써서 안 걸린다.)
        if (DevezCode.Services.AgentRegistry.Find(AgentFor(roomId))?.InlineTui == true)
        {
            bool marker = text.Contains("[?2004h") || text.Contains("[?2026h");
            // 폴백: 마커가 안 오는 변종/환경에서도 첫 출력 후 일정 시간 지나면 준비로(무한 스피너 방지).
            if (!_inlineFirstOutTick.TryGetValue(roomId, out var first))
                _inlineFirstOutTick[roomId] = first = Environment.TickCount;
            bool fallback = unchecked(Environment.TickCount - first) >= InlineReadyFallbackMs;
            if (marker || fallback)
            {
                _ready.Add(roomId);
                _readyScan.Remove(roomId);
                _inlineFirstOutTick.Remove(roomId);
                _altSeenTick[roomId] = Environment.TickCount;
                BumpSettle(roomId);
                return;
            }
            _readyScan[roomId] = text.Length > 512 ? text[^512..] : text;
            return;
        }

        if (text.Contains("[?1049h") || text.Contains("[?47h")) // 풀스크린 TUI(claude 등) 시작
        {
            _ready.Add(roomId);
            _readyScan.Remove(roomId);
            _altSeenTick[roomId] = Environment.TickCount;
            BumpSettle(roomId); // 즉시 통지하지 않고, 출력이 멎을 때까지 대기(단 MaxSettleAfterAltMs 상한)
            return;
        }
        _readyScan[roomId] = text.Length > 512 ? text[^512..] : text; // 버퍼 과다 방지
    }

    /// <summary>alt-screen 진입 후 출력이 올 때마다 안정화 타이머를 리셋. 만료되면(출력이 멎으면) TerminalReady 통지.</summary>
    private void BumpSettle(string roomId)
    {
        if (_readyNotified.Contains(roomId)) return; // 이미 통지함
        // alt-screen 진입 후 상한 초과 → 출력이 계속돼도 즉시 통지(끊임없는 redraw 로 settle 못 떨어지는 경우).
        // 인라인 TUI(gjc)는 resume 시 대화를 재생하며 스크롤이 튀는데, 그 구간을 로딩 오버레이로 덮으려면
        // 상한을 넉넉히 둬 출력이 멎을 때(SettleQuiet)까지 기다린다(짧은 cap 이면 재생 도중 오버레이가 걷혀 튐이 보임).
        int cap = DevezCode.Services.AgentRegistry.Find(AgentFor(roomId))?.InlineTui == true ? 6000 : MaxSettleAfterAltMs;
        if (_altSeenTick.TryGetValue(roomId, out var seen) &&
            unchecked(Environment.TickCount - seen) >= cap)
        {
            if (_settleTimers.Remove(roomId, out var existing)) existing.Stop();
            NotifyReady(roomId);
            return;
        }
        if (!_settleTimers.TryGetValue(roomId, out var t))
        {
            t = new System.Windows.Threading.DispatcherTimer { Interval = SettleQuiet };
            t.Tick += (_, _) =>
            {
                t.Stop();
                _settleTimers.Remove(roomId);
                NotifyReady(roomId);
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
        // 지역 참조 사용 — 초기화 도중 Dispose 가 _webView 를 null 로 만들어도 NRE 없이 안전하게 진행.
        var webView = new WebView2();
        try
        {
            _webView = webView;
            Content = webView;

            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevezCode", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            if (_disposed) return; // 초기화 중 앱 종료 — Dispose 가 webView 를 정리하므로 더 진행하지 않음
            await webView.EnsureCoreWebView2Async(env);
            if (_disposed) return;
            // 리사이즈(패널 접기/펴기) 중 WebView2 가 흰색으로 클리어했다 다시 그리며
            // 깜빡이는 것을 막는다 — 페인트 전 기본 배경을 터미널 배경(#0C0C0C)에 맞춤.
            webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C);
            // 초기화 완료 시 호스트가 숨겨진 상태라면 WPF 렌더 큐를 비워
            // 새로 생성된 HWND에 Collapsed 상태가 반영되기 전 한 프레임 튀는 현상을 방지한다.
            if (!IsVisible)
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            if (_disposed) return;

            var core = webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false; // F5 새로고침 등 차단 (터미널 보호)
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // 외부 OS 드래그(탐색기/이미지 등)는 자식 HWND 가 OLE Drop 을 거부 →
            // HwndSource(부모) 로 fall-through → WebView2 의 WPF Drop 이벤트로 변환되어 들어온다.
            // - AllowExternalDrop: OS OLE Drop 비활성화 (fall-through 트리거)
            // - AllowDrop: WPF Drop 이벤트 활성화
            webView.AllowExternalDrop = false;
            webView.AllowDrop = true;
            webView.Drop += OnWebViewDrop;
            webView.DragOver += OnWebViewDragOver;

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
                case "interact":
                    UserInteracted?.Invoke();
                    break;
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
                    var inputRoom = root.GetProperty("roomId").GetString()!;
                    // 단독 ESC = 응답 취소(인터럽트) 의도. agent 가 idle 신호를 안 줘도 스피너가
                    // 무한정 도는 것을 막기 위해 즉시 busy 해제를 요청한다(입력은 그대로 전달해 실제 취소도 수행).
                    if (data == "\x1b") InterruptRequested?.Invoke(inputRoom);
                    // 선택지 제출(Enter / 숫자키) — 메뉴를 답한 것이므로 입력 대기 ❗ 해제 신호.
                    // (화살표 등 네비게이션은 제외 → 아직 답 안 한 상태.)
                    else if (data is "\r" or "\n" or "\r\n" || (data.Length == 1 && data[0] >= '1' && data[0] <= '9'))
                        MenuInputSubmitted?.Invoke(inputRoom);
                    TerminalSessionManager.Instance.Get(inputRoom)?.Write(data);
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
                    // 클립보드 읽기도 후킹 앱 환경에서 블록될 수 있으므로 STA 스레드에서 수행하고,
                    // 결과 post 만 UI 스레드로 되돌린다(붙여넣기는 사용자 조작이라 약간의 비동기 무해).
                    RunClipboardSta(() =>
                    {
                        var imagePath = SaveClipboardImage();
                        var text = imagePath == null ? GetClipboardText() : null;
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (imagePath != null)
                                PostJson(new { type = "paste", roomId, data = imagePath });
                            else if (!string.IsNullOrEmpty(text))
                                PostJson(new { type = "paste", roomId, data = text });
                        });
                    });
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
            accent = CurrentAccentHex(), // 로딩 스피너 색 = 앱 PrimaryBrush (devez 스타일)
            fontFamily = cfg.FontFamily,
            fontSize = fontSizePx,
            windowsBuild = Environment.OSVersion.Version.Build, // xterm windowsPty 휴리스틱 판정용
            // WebGL(GPU) 렌더러 사용 여부 — 원격/CRD 세션은 App.OnStartup 이 SoftwareOnly 로 강제하므로
            // 그 경우 끈다(SwiftShader 소프트 GL 은 DOM 보다 느림). 로컬에선 GPU 렌더로 활성 탭 비용 절감.
            enableWebgl = System.Windows.Media.RenderOptions.ProcessRenderMode != System.Windows.Interop.RenderMode.SoftwareOnly,
        });
        var pending = _pendingShowRoomId ?? _activeRoomId;
        _pendingShowRoomId = null;
        if (pending != null) { PostJson(new { type = "show", roomId = pending, agent = AgentFor(pending) }); PinBottomIfInline(pending); }

        // 콜드스타트 동안 보류된 로딩 스피너 적용
        if (_pendingLoading) { _pendingLoading = false; PostJson(new { type = "loading", on = true }); }

        // 보류된 백그라운드 로드 처리 (show 로 이미 만들어진 방은 JS preload 가 스킵)
        foreach (var r in _pendingPreload)
            if (r != pending) PostJson(new { type = "preload", roomId = r, agent = AgentFor(r) });
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
            // 청크를 roomId 큐에 모으고, flush 가 아직 예약 안 됐을 때만 1회 예약(coalesce).
            bool schedule;
            lock (_outLock)
            {
                if (!_outPending.TryGetValue(roomId, out var list))
                    _outPending[roomId] = list = new List<byte[]>();
                list.Add(bytes);
                schedule = _outScheduled.Add(roomId); // 이미 있으면 false → 중복 예약 안 함
            }
            if (schedule) Dispatcher.BeginInvoke(() => FlushOutput(roomId));
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

    /// <summary>UI 스레드: roomId 큐에 모인 청크들을 하나로 합쳐 ScanForReady·PostJson 을 1회만 수행.</summary>
    private void FlushOutput(string roomId)
    {
        byte[] merged;
        lock (_outLock)
        {
            _outScheduled.Remove(roomId);
            if (!_outPending.Remove(roomId, out var list) || list.Count == 0) return;
            if (list.Count == 1) merged = list[0];
            else
            {
                int total = 0;
                foreach (var b in list) total += b.Length;
                merged = new byte[total];
                int off = 0;
                foreach (var b in list) { Buffer.BlockCopy(b, 0, merged, off, b.Length); off += b.Length; }
            }
        }
        ScanForReady(roomId, merged); // claude 화면이 뜨면 로딩 스피너 종료(누적 버퍼라 합쳐도 동일 판정)
        PostJson(new { type = "output", roomId, data = Convert.ToBase64String(merged) });
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
        FontSizePxChanged?.Invoke(px);
    }

    /// <summary>폰트 크기를 WT 설정 기본값으로 초기화 (Ctrl+0). 영구 저장.</summary>
    public void ResetFontSize()
    {
        _fontSizePt = Math.Round(TerminalSessionManager.Instance.Config.FontSizePx / PtToPx);
        _fontSizePt = Math.Max(6, Math.Min(72, _fontSizePt));
        double px = Math.Round(_fontSizePt * PtToPx, 1);
        PostJson(new { type = "adjustFontSize", size = px });
        DevezCode.Services.SettingsService.SaveTerminalFontSizePt((int)_fontSizePt);
        FontSizePxChanged?.Invoke(px);
    }

    /// <summary>클립보드에 텍스트 기록 (잠금 충돌 대비 재시도).
    /// SetText 는 지연 렌더링(copy=false)이라 WebView2(별도 Edge 프로세스) 환경에서
    /// 실제 데이터가 클립보드에 안 올라오는 경우가 있어, 즉시 flush 하는
    /// SetDataObject(text, true) 로 기록한다(OleFlushClipboard).</summary>
    private static void SetClipboardText(string text)
    {
        // 클립보드 매니저/백신/RDP 리디렉션이 붙은 PC에서 OleFlushClipboard(copy=true) 가
        // 뷰어 체인 응답을 동기 대기하며 수백ms~수초 블록될 수 있다. copy-on-select 는
        // 드래그 선택할 때마다 발동하므로 UI 스레드에서 하면 드래그가 통째로 멈춘다.
        // 전용 STA 스레드로 오프로드해 UI 스레드는 즉시 반환(재시도 sleep 도 여기서 소화).
        RunClipboardSta(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                try { System.Windows.Clipboard.SetDataObject(text, true); return; }
                catch { System.Threading.Thread.Sleep(40); }
            }
        });
    }

    /// <summary>클립보드 조작을 전용 STA 백그라운드 스레드에서 실행(UI 스레드 블로킹 방지).
    /// 클립보드 API 는 STA 를 요구하므로 ApartmentState.STA 로 띄운다.</summary>
    private static void RunClipboardSta(Action action)
    {
        var t = new System.Threading.Thread(() => { try { action(); } catch { } })
        {
            IsBackground = true,
        };
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
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

    /// <summary>다음 N회의 출력 쓰기에서 xterm.js 스크롤을 억제(슬래시 명령 자동주입 시 사용).</summary>
    public void SuppressScroll(int count = 5) => PostJson(new { type = "suppressScroll", count });

    /// <summary>ms 동안 출력 쓰기 후 맨 아래로 고정 — 인라인 TUI(gjc) open 직후 최신 화면을 보이게(짧은 창).</summary>
    public void PinBottom(int ms = 2000) => PostJson(new { type = "pinBottom", ms });

    /// <summary>TerminalReady 통지(중복 방지).</summary>
    private void NotifyReady(string roomId)
    {
        if (_readyNotified.Add(roomId))
        {
            TerminalReady?.Invoke(roomId);
            // 자동 시작으로 열린 세션이면 대기 중이던 Discord 메시지를 주입한다.
            DevezCode.Services.DiscordBotService.Instance.NotifySessionReady(roomId);
        }
    }

    /// <summary>인라인 TUI(gjc) 방이면 show 직후 짧은 창 동안 스크롤을 맨 아래로 고정(open 시 최신 표시).
    /// 이후엔 일반 동작 — gjc 는 멀티플렉서 모드(STY)로 스크롤백을 보존하므로 휠로 과거 대화를 스크롤할 수 있다.</summary>
    private void PinBottomIfInline(string roomId)
    {
        if (DevezCode.Services.AgentRegistry.Find(AgentFor(roomId))?.InlineTui == true)
            PinBottom(1500);
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
        PostJson(new { type = "theme", theme = scheme, accent = CurrentAccentHex() });
    }

    /// <summary>현재 테마의 PrimaryColor 를 #RRGGBB 로. 로딩 스피너 색(devez 스타일)에 사용.</summary>
    private static string CurrentAccentHex()
    {
        if (System.Windows.Application.Current?.TryFindResource("PrimaryColor") is System.Windows.Media.Color c)
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        return "#2563eb";
    }

    /// <summary>세션 로딩 스피너(웹 레이어) 표시/숨김. WebView2 는 HwndHost 라 WPF 오버레이로는
    /// 터미널을 못 덮으므로 스피너를 웹 안에서 띄운다(터미널 위에 항상 보임).</summary>
    public void SetLoading(bool on)
    {
        // 콜드스타트: pageReady 전이면 보류했다가 OnPageReady 에서 flush (web 스피너 유실 방지)
        if (!_pageReady) { _pendingLoading = on; return; }
        PostJson(new { type = "loading", on });
    }

    /// <summary>방 삭제 시 호출 — 방별 배선·준비 상태와 JS 쪽 xterm 인스턴스를 정리.
    /// ConPTY 셸 세션은 TerminalSessionManager.DisposeRoom 이 별도로 정리한다.</summary>
    public void CloseTerminal(string roomId)
    {
        _wired.Remove(roomId);
        _ready.Remove(roomId);
        _readyScan.Remove(roomId);
        _readyNotified.Remove(roomId);
        _altSeenTick.Remove(roomId);
        _inlineFirstOutTick.Remove(roomId);
        if (_settleTimers.Remove(roomId, out var st)) st.Stop();
        _pendingPreload.Remove(roomId);
        lock (_outLock) { _outPending.Remove(roomId); _outScheduled.Remove(roomId); }
        if (_activeRoomId == roomId) _activeRoomId = null;
        if (_pendingShowRoomId == roomId) _pendingShowRoomId = null;
        PostJson(new { type = "dispose", roomId }); // JS xterm 인스턴스·DOM 해제
    }

    private bool _disposed;

    // ── 외부 드래그 앤 드롭 (탐색기/이미지 등 → 활성 세션 터미널로 @경로 입력) ──────────
    /// <summary>드롭 허용 확장자 (이미지 + 텍스트/소스/문서). 바이너리(.exe/.zip 등)는 토큰으로 의미가 없어 제외.</summary>
    private static readonly HashSet<string> DropExts = new(StringComparer.OrdinalIgnoreCase)
    {
        // 이미지
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".avif", ".tif", ".tiff",
        // 텍스트/소스/문서
        ".txt", ".md", ".markdown", ".rst", ".adoc",
        ".json", ".jsonc", ".yaml", ".yml", ".toml", ".ini", ".conf", ".config", ".env", ".editorconfig", ".props", ".targets",
        ".xml", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".vue", ".svelte", ".razor", ".cshtml",
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".java", ".kt", ".kts", ".swift", ".go", ".rs",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".py", ".rb", ".php", ".lua", ".dart", ".fs", ".fsi",
        ".vb", ".sql", ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".bat", ".cmd",
        ".gitignore", ".gitattributes", ".gitmodules", ".dockerignore",
    };

    private void OnWebViewDragOver(object sender, DragEventArgs e)
    {
        // 활성 room 이 살아있고, 허용 확장자 파일이 있을 때만 Copy 커서.
        var room = _activeRoomId;
        if (string.IsNullOrEmpty(room)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var sess = TerminalSessionManager.Instance.Get(room);
        if (sess is not { IsAlive: true }) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0 || !files.Any(IsDroppableFile))
        { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnWebViewDrop(object sender, DragEventArgs e)
    {
        var room = _activeRoomId;
        if (string.IsNullOrEmpty(room)) return;
        var sess = TerminalSessionManager.Instance.Get(room);
        if (sess is not { IsAlive: true }) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;

        var accepted = files.Where(IsDroppableFile).Select(p => "@" + p).ToArray();
        if (accepted.Length == 0) return;

        // @경로 는 claude/codex/opencode 가 직접 읽어 첨부. 엔터는 자동으로 안 누름 — 사용자가 확인 후 직접 Enter.
        sess.Write(string.Join("\r", accepted) + "\r");
        e.Handled = true;
    }

    private static bool IsDroppableFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            var name = Path.GetFileName(path);
            if (DropExts.Contains(name)) return true; // .gitignore, Dockerfile 등 확장자 없는 파일
            var ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && DropExts.Contains(ext);
        }
        catch { return false; }
    }

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
        _altSeenTick.Clear();
        foreach (var t in _settleTimers.Values) t.Stop();
        _settleTimers.Clear();
        lock (_outLock) { _outPending.Clear(); _outScheduled.Clear(); }
    }
}
