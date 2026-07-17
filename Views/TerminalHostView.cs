using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    /// <summary>폴더 신뢰 확인 화면을 감지했을 때 발생. 호출자가 경로를 검증한 뒤 승인 여부를 결정한다.</summary>
    public event Action<string>? TrustPromptDetected;
    /// <summary>방의 ConPTY 세션이 생성/배선되어 살아있음. roomId 전달.</summary>
    public event Action<string>? SessionStarted;
    /// <summary>사용자가 단독 ESC 로 응답 취소를 요청. roomId 전달 — 구독자가 busy 스피너를 끈다.</summary>
    public event Action<string>? InterruptRequested;
    /// <summary>선택지 메뉴에 제출 입력(Enter/숫자키)이 들어옴 — 입력 대기 ❗ 해제용.</summary>
    public event Action<string>? MenuInputSubmitted;
    /// <summary>방의 셸 프로세스가 종료됨(끊김/죽음). roomId 전달.</summary>
    public event Action<string>? SessionExited;
    /// <summary>claude 가 /exit·Ctrl+C 로 끝나 배치 재진입 루프가 곧 재실행함 — 스피너로 덮을 시점. roomId 전달.</summary>
    public event Action<string>? SessionRestarting;
    /// <summary>터미널에서 세션(탭) 단축키 발생 — name: newSession/closeSession/nextSession/prevSession/gotoSession.
    /// gotoSession 일 때 index = 0-기준 세션 번호(-1 = 마지막), 그 외엔 의미 없음.</summary>
    public event Action<string, int>? SessionActionRequested;
    /// <summary>터미널 폰트 크기(px)가 바뀜(Ctrl+휠/리셋/초기화). 세션 헤더 타이틀 동기화용.</summary>
    public event Action<double>? FontSizePxChanged;
    /// <summary>사용자가 WebView2 터미널 표면을 클릭/조작함. WPF PreviewMouseDown 이 HWND 경계를 넘지 못해 별도 통지한다.</summary>
    public event Action? UserInteracted;
    /// <summary>사용자가 특정 방의 터미널을 실제로 조작함. 유휴 종료 타이머 갱신용.</summary>
    public event Action<string>? SessionActivity;
    /// <summary>터미널 출력에서 파일 경로를 Ctrl+클릭 → 에디터 탭으로 열기 요청.</summary>
    public event Action<string>? FileOpenRequested;
    /// <summary>synced reveal 준비 완료(폭 안정·fit·재동기 끝, 커튼은 아직 유지) — 셸이 양쪽 준비를 모아 동시에 걷는다.</summary>
    public event Action? RevealPrepared;
    /// <summary>web 로딩 커버가 DOM 에 반영·페인트됨 — 셸이 이 ACK 후에 터미널 HWND 를 unpark 해 콜드 세션
    /// unpark repaint 가 커버 위에서 일어나게 한다(PostJson↔WPF 프레임 비동기로 생기는 커버 레이스 제거).</summary>
    public event Action? LoadingShown;

    private WebView2? _webView;
    private bool _initStarted;
    private bool _pageReady;
    private (double w, double h, string? label)? _pendingLoading; // pageReady 전 SetLoading(true) 보류(기대 크기·문구 포함, 콜드스타트 첫 세션 스피너)
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

    /// <summary>opencode 자동 재시작 폭주 방지: roomId → (10초 창 내 재시작 횟수, 창 시작 tick).</summary>
    private readonly Dictionary<string, (int Count, long WindowStartTick)> _autoRestart = new();

    /// <summary>세션 종료 시 앱이 새 ConPTY 로 자동 재시작할 방인가(배치 루프 대신 앱레벨 재진입).
    /// opencode: 배치 루프 시 0xc0000142 크래시 회피. codex: Ctrl+C·/exit 종료 후 같은 세션 resume 으로
    /// 자동 복귀(배치 종료 시 exit → onExited → 여기서 재시작, 로딩커버로 스피너 표시).</summary>
    private static bool IsAutoReenterRoom(string roomId)
    {
        try
        {
            var a = DevezCode.Services.SettingsService.LoadAgentForRoom(roomId);
            return string.Equals(a, "opencode", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "codex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "grok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "antigravity", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>자동 재시작 허용 여부. 10초 창 안에서 3회까지만 — 그 이상은 시작 실패 반복으로 보고
    /// 멈춰 종료 프롬프트를 띄운다(무한 크래시 루프 방지). 세션이 10초 넘게 살았다 끝나면 창이 리셋된다.</summary>
    private bool AllowAutoRestart(string roomId)
    {
        long now = Environment.TickCount64;
        if (_autoRestart.TryGetValue(roomId, out var s) && now - s.WindowStartTick < 10_000)
        {
            if (s.Count >= 3) return false;
            _autoRestart[roomId] = (s.Count + 1, s.WindowStartTick);
        }
        else
        {
            _autoRestart[roomId] = (1, now);
        }
        return true;
    }

    /// <summary>roomId → (배선된 세션, OutputReceived 핸들러, Exited 핸들러). CloseTerminal/재배선 시
    /// 반드시 이 핸들러를 detach 해야 한다. 안 하면 같은 세션에 핸들러가 누적돼(패널 이동 왕복 등)
    /// 출력이 2·3배로 post → 화면에 글자가 여러 번 찍힌다("두 번 입력됨"의 실체).</summary>
    private readonly Dictionary<string, (TerminalSession Session, Action<byte[]> OnOutput, Action OnExited)> _sessionHandlers = new();

    // ── 출력 배칭(coalescing) ──────────────────────────────────────
    // ConPTY-Read 백그라운드 스레드가 청크마다 OutputReceived 를 때리는데, 매 청크 BeginInvoke 하면
    // 다세션 동시 출력 시 UI 스레드 디스패처 큐가 폭증한다. roomId 별로 청크를 모아 한 번만 flush 를
    // 예약(coalesce)하면, UI 가 바쁠 때 여러 청크가 한 틱에 합쳐져 ScanForReady·PostJson·JS write 가
    // 1회로 줄어든다. UI 가 한가하면 거의 즉시 비워져 지연은 사실상 없다. _outLock 으로 BG/UI 동기화.
    private readonly object _outLock = new();
    private readonly Dictionary<string, List<byte[]>> _outPending = new();
    private readonly HashSet<string> _outScheduled = new();
    /// <summary>Codex 렌더 확인 요청 이후 새 출력 도착 여부를 판별하는 방별 세대.</summary>
    private readonly Dictionary<string, long> _outputGenerations = new();
    /// <summary>Grok SGR(CSI ... m)이 ConPTY 출력 청크 경계에서 잘렸을 때 다음 flush까지 보관.</summary>
    private readonly Dictionary<string, byte[]> _grokCsiTails = new();

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
    private static readonly object ClipboardWriteLock = new();
    /// <summary>alt-screen 진입 시각(Environment.TickCount). UI 스레드.</summary>
    private readonly Dictionary<string, int> _altSeenTick = new();
    /// <summary>alt-screen 진입 후 출력이 계속 흘러도(스피너/시계 등 끊임없는 redraw) 이 시각이 지나면
    /// 무조건 준비 완료로 본다 — settle 이 영원히 안 떨어져 오버레이가 20s 타임아웃까지 남는 것 방지.</summary>
    private const int MaxSettleAfterAltMs = 700;
    /// <summary>인라인 TUI(gjc): 첫 출력 시각. 준비 마커가 안 올 때 폴백 타임아웃 기준.</summary>
    private readonly Dictionary<string, int> _inlineFirstOutTick = new();
    /// <summary>풀스크린 방별 준비 폴백 1회성 타이머(alt-screen 미감지 대비). UI 스레드.</summary>
    private readonly Dictionary<string, System.Windows.Threading.DispatcherTimer> _fullscreenFallbackTimers = new();
    /// <summary>인라인 TUI 준비 마커(\e[?2004h/\e[?2026h)가 안 와도 이만큼 지나면 준비로 본다(무한 스피너 방지).</summary>
    private const int InlineReadyFallbackMs = 8000;
    /// <summary>풀스크린 TUI(claude 등)에서 alt-screen 시퀀스가 안 걸려도 첫 출력 후 이만큼 지나면 준비로 본다.
    /// 정상 환경은 alt-screen 이 수백ms~2초 내 잡혀 여기 도달 안 함. 감지 실패 환경에서만 트립 → 무한 재스피너 방지.</summary>
    private const int FullscreenReadyFallbackMs = 6000;

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
        // 배치 재진입 신호(claude/gjc /exit·Ctrl+C 후 루프 재실행 직전) → 로딩 커버. Dispose 에서 해제.
        TerminalSessionManager.RoomReentering += OnRoomReentering;
    }

    /// <summary>해당 방의 claude 화면이 이미 떠서 안정화까지 끝났는지(로딩 불필요).</summary>
    public bool IsReady(string roomId) => _readyNotified.Contains(roomId);
    /// <summary>terminal.html 페이지가 로드·초기화 완료됐는지(콜드스타트 진단용).</summary>
    public bool IsPageReady => _pageReady;
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
        DevezCode.Services.DiagLog.Write($"ShowTerminal room={roomId} pageReady={_pageReady} readyNotified={_readyNotified.Contains(roomId)}");
        _activeRoomId = roomId;
        if (!_initStarted)
        {
            _initStarted = true;
            await InitWebViewAsync();
        }
        if (_pageReady) { PostJson(new { type = "show", roomId, agent = AgentFor(roomId), fontSize = RoomFontSizeOverridePx(roomId) }); PinBottomIfInline(roomId); }
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
        if (_pageReady) PostJson(new { type = "preload", roomId, agent = AgentFor(roomId), fontSize = RoomFontSizeOverridePx(roomId) });
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

        // 신뢰 프롬프트("이 폴더를 신뢰?", 사용자 입력 대기)는 alt-screen(\e[?1049h) 없이 커서 위치지정으로
        // 인라인 렌더된다 → [?1049h 기반 준비 판정에 안 걸려 스피너 오버레이가 프롬프트를 덮고 오래 회전한다.
        // 게다가 단어 사이 공백이 \e[1C(커서 전진) escape 라 원시 스트림엔 "trust this folder" 같은 연속
        // 문자열이 없다(실측). 그래서 escape 를 정규화(커서전진→공백, 나머지 제거)한 뒤 매칭한다.
        // 시그니처가 보이면 즉시 준비로 간주해 오버레이를 내린다(입력 대기라 더 흐를 출력 없음. 응답 후 뜨는
        // 실제 TUI 는 이미 ready 라 재통지 없이 정상 렌더). 문구는 claude 2.1.x 실측(둘 중 하나만 걸려도 동작).
        if (text.Contains("trust", StringComparison.OrdinalIgnoreCase)) // 값싼 사전필터 — 콜드스타트 초기 구간에만 도달
        {
            var norm = StripAnsi(text).ToLowerInvariant();
            if (norm.Contains("trust this folder") || norm.Contains("one you trust") ||
                norm.Contains("trust the contents of this directory") || norm.Contains("trust this directory"))
            {
                _ready.Add(roomId);
                _readyScan.Remove(roomId);
                _inlineFirstOutTick.Remove(roomId);
                _altSeenTick[roomId] = Environment.TickCount;
                TrustPromptDetected?.Invoke(roomId);
                NotifyReady(roomId);
                return;
            }
        }

        // 인라인 렌더 에이전트(gjc 등)는 alt-screen 시퀀스가 없다. 단 첫 출력을 준비로 보면
        // cmd/ConPTY 초기화 노이즈(\e[?9001h\e[?1004h 등)가 gjc 본체보다 먼저 나와, 스피너가 빈 화면에서
        // 꺼진 뒤 gjc 가 수백ms~수초 후 실제 페인트하는 갭이 보인다. gjc 가 입력/첫 프레임을 그릴 때 내는
        // 마커로 실제 준비를 판정한다: 일반 인라인 TUI 는 \e[?2004h(입력 준비)/\e[?2026h(프레임 시작),
        // Codex 는 복원 본문보다 먼저 입력 준비·프레임 시작을 내므로 \e[?2026l(프레임 종료)을 사용한다.
        // (cmd 초기화는 ?9001/?1004 만 써서 안 걸린다.)
        if (DevezCode.Services.AgentRegistry.Find(AgentFor(roomId))?.InlineTui == true)
        {
            var agentId = AgentFor(roomId);
            // Codex 의 ?2004h 는 입력 가능, ?2026h 는 프레임 '시작'일 뿐이라 이때 ready 로 잡으면
            // 커버가 먼저 걷히고 빈 화면의 커서만 노출된다. 동기화 프레임 종료(?2026l)까지 확인한다.
            bool marker = agentId == "codex"
                ? text.Contains("[?2026l")
                : text.Contains("[?2004h") || text.Contains("[?2026h");
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
            DevezCode.Services.DiagLog.Write($"alt-screen detected room={roomId}");
            _ready.Add(roomId);
            _readyScan.Remove(roomId);
            _inlineFirstOutTick.Remove(roomId);
            if (_fullscreenFallbackTimers.Remove(roomId, out var ftt)) ftt.Stop();
            _altSeenTick[roomId] = Environment.TickCount;
            BumpSettle(roomId); // 즉시 통지하지 않고, 출력이 멎을 때까지 대기(단 MaxSettleAfterAltMs 상한)
            return;
        }

        // 폴백: 풀스크린인데 alt-screen 시퀀스가 안 걸리는 환경(청크 경계로 잘림·변종 시퀀스·배선 전
        // 도착 등)에서도 준비로 확정한다. 이게 없으면 그 방은 영영 ready 를 못 찍어 20초 로딩 타임아웃으로만
        // 스피너가 꺼지고, 재진입할 때마다 다시 스피너가 뜬다. (인라인 TUI 는 위에서 같은 보호를 받는다.)
        // 첫 출력 시 1회성 타이머를 걸어, 이후 출력이 잠잠해져 ScanForReady 가 다시 안 불려도 발화하게 한다.
        ArmFullscreenReadyFallback(roomId);
        _readyScan[roomId] = text.Length > 512 ? text[^512..] : text; // 버퍼 과다 방지
    }

    /// <summary>재시작(재진입) 직전 준비 상태 재무장 — 새 TUI 의 준비 신호가 TerminalReady 를 다시 발화하게 한다.</summary>
    private void ResetReadyForRestart(string roomId)
    {
        _ready.Remove(roomId);
        _readyNotified.Remove(roomId);
        _readyScan.Remove(roomId);
        _altSeenTick.Remove(roomId);
        _inlineFirstOutTick.Remove(roomId);
        if (_settleTimers.Remove(roomId, out var st)) st.Stop();
        if (_fullscreenFallbackTimers.Remove(roomId, out var ft)) ft.Stop(); // 잔존 타이머 조기 발화 방지
    }

    /// <summary>배치 재진입 플래그(claude·gjc — TerminalSessionManager.RoomReentering, FSW 스레드) 처리.
    /// ready 였던 방만 재시작으로 간주(첫 실행·연속 재시도 중복은 무시 — 재무장 직후라 ready 가 아님).
    /// 준비 상태를 재무장하고 SessionRestarting 을 발화해 UI 가 로딩 커버로 재실행 과정을 가리게 한다.</summary>
    private void OnRoomReentering(string roomId)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !_wired.ContainsKey(roomId)) return; // 이 뷰가 배선한 방만
            if (!_readyNotified.Contains(roomId)) return;
            DevezCode.Services.DiagLog.Write($"재진입 플래그 감지 → 재시작 커버 room={roomId}");
            ResetReadyForRestart(roomId);
            ArmFullscreenReadyFallback(roomId); // 재실행 실패(giveup 프롬프트 등) 시 무한 스피너 방지
            SessionRestarting?.Invoke(roomId);
        });
    }

    /// <summary>풀스크린 방에서 alt-screen 이 안 잡히는 경우를 대비한 1회성 준비 폴백 타이머(중복 무시).
    /// FullscreenReadyFallbackMs 후에도 여전히 ready 가 아니면 강제로 준비 확정 → 무한 재스피너 방지.</summary>
    private void ArmFullscreenReadyFallback(string roomId)
    {
        if (_fullscreenFallbackTimers.ContainsKey(roomId)) return; // 이미 무장됨
        var t = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FullscreenReadyFallbackMs),
        };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _fullscreenFallbackTimers.Remove(roomId);
            if (_ready.Contains(roomId) || _readyNotified.Contains(roomId)) return; // 그 사이 정상 감지됨
            DevezCode.Services.DiagLog.Write($"fullscreen ready FALLBACK room={roomId} (alt-screen 미감지 {FullscreenReadyFallbackMs}ms 경과)");
            _ready.Add(roomId);
            _readyScan.Remove(roomId);
            _altSeenTick[roomId] = Environment.TickCount;
            NotifyReady(roomId);
        };
        _fullscreenFallbackTimers[roomId] = t;
        t.Start();
    }

    // 신뢰 프롬프트 매칭용 ANSI 정규화. 커서 전진(\e[nC)은 화면상 공백이므로 공백으로, OSC/그 외 CSI 는 제거.
    // 커서 전진을 '먼저' 공백 치환해야 한다(일반 CSI 제거가 종결바이트 C 를 먼저 먹으면 공백이 사라져 단어가 붙는다).
    private static readonly System.Text.RegularExpressions.Regex _reOsc =
        new(@"\x1b\][^\x1b\x07]*(?:\x07|\x1b\\)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _reCursorFwd =
        new(@"\x1b\[[0-9]*C", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _reCsi =
        new(@"\x1b\[[0-9;?]*[ -/]*[@-~]", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static string StripAnsi(string s)
    {
        s = _reOsc.Replace(s, string.Empty);
        s = _reCursorFwd.Replace(s, " ");
        s = _reCsi.Replace(s, string.Empty);
        return s;
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
                if (AgentFor(roomId) == "codex") RequestReadyPaint(roomId);
                else NotifyReady(roomId);
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
        // 콜드스타트 흰 배경 제거 + clear 프레임 은닉: WebView2 는 CoreWebView2 초기화·terminal.html 페인트
        // 전(및 HWND 리사이즈 시)까지 DefaultBackgroundColor 로 clear 한다. 하드코딩 대신 '현재 터미널 테마
        // 배경색'으로 맞춰, unpark/리사이즈 clear 프레임이 실제 배경과 동일해 안 튀게 한다(라이트 테마 등에서도).
        webView.DefaultBackgroundColor = TerminalBgColor();
        try
        {
            _webView = webView;
            Content = webView;
            DevezCode.Services.DiagLog.Write($"InitWebView: start (IsVisible={IsVisible})");

            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevezCode", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            if (_disposed) return; // 초기화 중 앱 종료 — Dispose 가 webView 를 정리하므로 더 진행하지 않음
            await webView.EnsureCoreWebView2Async(env);
            if (_disposed) return;
            DevezCode.Services.DiagLog.Write($"InitWebView: EnsureCore done (IsVisible={IsVisible})");
            // (DefaultBackgroundColor/Background 는 생성 직후 이미 #0C0C0C 로 설정됨 — 콜드스타트 흰 배경 방지)
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
                case "openFile":
                {
                    var path = root.GetProperty("path").GetString() ?? "";
                    if (!string.IsNullOrEmpty(path))
                        FileOpenRequested?.Invoke(path);
                    break;
                }
                case "interact":
                    UserInteracted?.Invoke();
                    if (_activeRoomId != null) SessionActivity?.Invoke(_activeRoomId);
                    break;
                case "revealPrepared":
                    RevealPrepared?.Invoke();
                    break;
                case "loadingShown":
                    LoadingShown?.Invoke();
                    break;
                case "readyPainted":
                {
                    var roomId = root.GetProperty("roomId").GetString()!;
                    var generation = root.GetProperty("generation").GetInt64();
                    bool hasContent = root.GetProperty("hasContent").GetBoolean();
                    if (_ready.Contains(roomId) && !_readyNotified.Contains(roomId) &&
                        _outputGenerations.TryGetValue(roomId, out var current) && current == generation)
                    {
                        if (hasContent) NotifyReady(roomId);
                        else BumpSettle(roomId); // 커서뿐이면 다음 페인트/출력을 기다린다.
                    }
                    break;
                }
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
                    SessionActivity?.Invoke(inputRoom);
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
                {
                    var roomId = root.GetProperty("roomId").GetString()!;
                    var cols = root.GetProperty("cols").GetInt32();
                    var rows = root.GetProperty("rows").GetInt32();
                    TerminalSessionManager.Instance.Get(roomId)?.Resize(cols, rows);
                    break;
                }
                case "diag": // 웹 레이어 진단 로그 → diag.log (codex 팝업 스윕 등)
                    DevezCode.Services.DiagLog.Write("[web] " + (root.TryGetProperty("msg", out var dm) ? dm.GetString() : ""));
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
                        var (imagePath, text) = ReadClipboardForPaste();
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
                case "confirmPaste":
                {
                    // JS 가 "메인 버퍼(=에이전트 아닌 맨 셸) + 개행 포함 붙여넣기"(줄마다 즉시 실행 위험)를
                    // 감지해 넘긴 확인 요청. doc 은 이 핸들러 종료 시 dispose 되므로 문자열만 먼저 뽑아
                    // 캡처한 뒤 모달을 Dispatcher 로 미룬다(메시지 처리 중 재진입/doc 무효화 회피).
                    var pasteRoom = root.GetProperty("roomId").GetString()!;
                    var pasteData = root.GetProperty("data").GetString() ?? "";
                    Dispatcher.BeginInvoke(() =>
                    {
                        var ok = ConfirmDialog.Show(
                            "여러 줄 붙여넣기",
                            "지금 이 터미널은 에이전트가 아니라 셸 프롬프트 상태입니다.\n" +
                            "여러 줄을 붙여넣으면 줄마다 명령으로 즉시 실행되어 '>' 같은 문자가\n" +
                            "예상치 못한 파일을 만들 수 있습니다. 그래도 붙여넣을까요?",
                            okLabel: "붙여넣기",
                            iconKey: "IconTriangleAlert",
                            danger: true);
                        if (ok) PostJson(new { type = "paste", roomId = pasteRoom, data = pasteData, force = true });
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
        DevezCode.Services.DiagLog.Write($"InitWebView: pageReady (IsVisible={IsVisible})");
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

        // 콜드스타트 동안 보류된 로딩 커버를 show 보다 먼저 적용한다. 반대 순서면 빠른 codex 가
        // 포커스되며 커서가 한 프레임 노출된 뒤에야 커버가 켜진다.
        if (_pendingLoading is { } pl)
        {
            _pendingLoading = null;
            PostJson(new { type = "loading", on = true, expectW = pl.w, expectH = pl.h, label = pl.label });
        }
        if (pending != null) { PostJson(new { type = "show", roomId = pending, agent = AgentFor(pending), fontSize = RoomFontSizeOverridePx(pending) }); PinBottomIfInline(pending); }

        // 보류된 백그라운드 로드 처리 (show 로 이미 만들어진 방은 JS preload 가 스킵)
        foreach (var r in _pendingPreload)
            if (r != pending) PostJson(new { type = "preload", roomId = r, agent = AgentFor(r), fontSize = RoomFontSizeOverridePx(r) });
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
        // 숨김 graceful 종료 진행 중 — 지금 배선하면 죽어가는 세션에 재부착돼 종료 트랜스크립트
        // ("Resume this session with…")가 재생되고 "[세션 종료됨]" 죽은 방으로 굳는다(프리로드·뒤늦은
        // 생성 요청 경로). 종료 완료 후 OnHideStopFinished 가 남은 방을 정리하고 새로 생성(resume)한다.
        if (TerminalSessionManager.Instance.IsGracefulStopping(roomId)) return;

        TerminalSession session;
        try
        {
            using (DevezCode.Services.DiagLog.Time($"WireSession.GetOrCreate room={roomId}"))
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

        // 이 방에 이전 핸들러가 남아있으면(다른 세션이었든 같은 세션이었든) 먼저 detach.
        // 안 하면 OutputReceived 에 핸들러가 누적돼 출력이 2·3배로 중복 post 된다.
        DetachSessionHandlers(roomId);
        _wired[roomId] = session;

        // 셸 첫 출력(준비 완료 신호) 이후에 claude 커맨드 전송 — PSReadLine 초기화 완료 보장
        var initialCmd = TerminalSessionManager.Instance.GetInitialCommand(roomId);
        bool cmdSent = initialCmd == null; // 커맨드 없으면 전송 불필요

        Action<byte[]> onOutput = bytes =>
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
        Action onExited = () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                // 이 세션이 이미 새 세션으로 교체되었으면(테마 재시작 등) 종료 프롬프트를 띄우지 않는다.
                // Exited 는 WaitForExit 스레드에서 뒤늦게 와서 "restarted" 이후 도착할 수 있다.
                if (!_wired.TryGetValue(roomId, out var cur) || !ReferenceEquals(cur, session)) return;

                // opencode: opencode.exe(bun TUI) 는 종료된 ConPTY 에서 in-place 재기동 시 0xc0000142 라
                // 배치 루프를 못 쓴다(claude/gjc 는 .exe 라 배치 루프로 자체 재진입). 대신 세션이 끝나면
                // 여기서 "새 ConPTY 세션"으로 같은 세션을 resume 해 자동 재시작한다. 진짜 종료는 방 닫기.
                // 폭주 방지: 짧은 시간 내 반복 실패면 멈추고 종료 프롬프트를 띄운다(AllowAutoRestart).
                // 앱 종료(graceful shutdown) 중의 Exited 는 우리가 죽인 것 — 재진입하면 종료 중에
                // 새 codex/opencode 가 떠 하드킬·세션ID 오염 레이스가 된다. IsShuttingDown 이면 스킵.
                if (IsAutoReenterRoom(roomId) && !TerminalSessionManager.Instance.IsShuttingDown && AllowAutoRestart(roomId))
                {
                    DevezCode.Services.DiagLog.Write($"opencode 세션 종료 → 앱 자동 재시작(새 세션 resume) room={roomId}");
                    // 준비 상태 재무장 + 로딩 커버 — 안 하면 stale ready 로 커버가 즉시 걷혀 부팅 출력이 보인다.
                    // 새 opencode 의 준비 신호(alt-screen/settle)가 TerminalReady 로 커버를 다시 걷는다.
                    ResetReadyForRestart(roomId);
                    SessionRestarting?.Invoke(roomId);
                    WireSession(roomId, 120, 30);            // 죽은 세션 → GetOrCreate 가 새 ConPTY 로 만들며 resume
                    PostJson(new { type = "restarted", roomId }); // xterm 클리어 + JS 가 실제 크기로 resize
                    return;
                }
                PostJson(new { type = "exited", roomId });
                SessionExited?.Invoke(roomId); // 끊김/죽음 → 회색 점
            });
        };
        session.OutputReceived += onOutput;
        session.Exited += onExited;
        _sessionHandlers[roomId] = (session, onOutput, onExited);

        // 재배선(이미 돌던 세션에 새 xterm 연결 — 전체 재시작 직후 클릭·패널 이동·배선 유실 등):
        // 시작 시 지나간 alt-screen 신호는 다시 오지 않으므로 여기서 준비 상태를 복원하고,
        // 리사이즈로 ConPTY 가 화면 전체를 다시 그리게 한다. 안 하면 그 방은 영영 ready 가
        // 못 돼 진입할 때마다 로딩 스피너가 타임아웃까지 돌고, 화면도 우연한 리사이즈 전까지 빈 채 남는다.
        if (session.HasPriorOutput && !_readyNotified.Contains(roomId))
        {
            DevezCode.Services.DiagLog.Write($"WireSession reattach room={roomId} — ready 복원 + resize kick");
            _ready.Add(roomId);
            _readyScan.Remove(roomId);
            _inlineFirstOutTick.Remove(roomId);
            _altSeenTick[roomId] = Environment.TickCount;
            BumpSettle(roomId); // 킥 리페인트 출력이 잠잠해지면(무출력이어도 SettleQuiet 후) 통지
            // 이 방으로 전환한 새 xterm(다른 패널 등)의 실제 크기가 세션의 마지막 크기와 다르면
            // 그 자체가 진짜 리사이즈 이벤트라 ConPTY 가 자연히 리페인트한다.
            // 크기가 같을 때만 "-1 후 원복" 킥으로 강제 신호를 만든다 — 이 트릭은 ConPTY 내부에서
            // 스크롤백을 좁은 폭으로 한 번 리플로우했다 되돌리는 과정이라, 크기가 실제로 다른데도
            // 매번 걸면 그 리플로우 왕복 자체가 줄바꿈이 어긋나거나 내용이 잘리는 부작용을 낸다
            // (패널 간 이동을 반복하면 계속 걸려 누적됨 — 사용자 리포트: "이동하면 내용 잘리고
            // 줄바꿈 이상해짐").
            if (session.Cols == cols && session.Rows == rows)
            {
                session.Resize(Math.Max(2, cols - 1), rows);
                session.Resize(cols, rows);
            }
            else
            {
                session.Resize(cols, rows);
            }
        }
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
        _outputGenerations[roomId] = _outputGenerations.TryGetValue(roomId, out var generation)
            ? generation + 1
            : 1;
        ScanForReady(roomId, merged); // claude 화면이 뜨면 로딩 스피너 종료(누적 버퍼라 합쳐도 동일 판정)
        // soft 테마에서 claude 가 클래스명/식별자 등에 쓰는 색 — 실측 결과 테마 override 토큰도,
        // ANSI 팔레트(Blue)도 아닌 claude 내부 고정 truecolor 상수(RGB 87,105,247)였다
        // (DEVEZCODE_TERM_LOG=1 raw dump 로 확인: "\e[38;2;87;105;247mApp"). 팔레트로는 손댈 수
        // 없어 출력 바이트에서 그 이스케이프만 우리 soft 그린으로 직접 치환한다. ASCII 이스케이프라
        // UTF-8 멀티바이트(한글) 와 겹칠 일이 없어 문자열 디코딩 없이 바이트 매칭만으로 안전하다.
        if (DevezCode.App.CurrentTheme == "soft")
            merged = RecolorClaudeIdentifierBlue(merged);
        // codex: 고정 다크 배경인 사용자 메시지·diff 영역을 앱 테마에 맞게 치환한다.
        // 상태바(39,39,39)는 보존. OSC 10/11 색상 질의는 xterm 대신 즉시 프록시 응답.
        if (AgentFor(roomId) == "codex")
        {
            merged = AnswerCodexColorQueries(roomId, merged);
            merged = RecolorCodexBackgrounds(merged);
        }
        // Grok: 중립 배경은 앱 스킴에 상대 매핑하고, 밝은 테마의 truecolor/ANSI 전경·의미색도
        // DevezCode soft/minimal 팔레트로 역할별 매핑한다. dark 전경색은 GrokNight 원본 유지.
        if (AgentFor(roomId) == "grok")
            merged = RecolorGrokTerminalColors(roomId, merged);
        else
            _grokCsiTails.Remove(roomId);
        PostJson(new { type = "output", roomId, data = Convert.ToBase64String(merged) });
    }

    // claude 가 클래스명/식별자 하이라이트에 쓰는 고정 truecolor(RGB 87,105,247, 테마 무관 상수) →
    // soft 브랜드 그린(ANSI Blue 와 동일한 92,140,74)으로 바이트 단위 치환.
    private static readonly byte[] _identifierBlueEscape =
        System.Text.Encoding.ASCII.GetBytes("\x1b[38;2;87;105;247m");
    private static readonly byte[] _identifierGreenEscape =
        System.Text.Encoding.ASCII.GetBytes("\x1b[38;2;92;140;74m");

    private static byte[] RecolorClaudeIdentifierBlue(byte[] data)
    {
        int firstHit = IndexOfBytes(data, _identifierBlueEscape, 0);
        if (firstHit < 0) return data; // 대부분의 청크엔 없음 — 흔한 경로를 빠르게 통과

        var result = new List<byte>(data.Length);
        int i = 0;
        while (i < data.Length)
        {
            if (i + _identifierBlueEscape.Length <= data.Length && MatchesAt(data, i, _identifierBlueEscape))
            {
                result.AddRange(_identifierGreenEscape);
                i += _identifierBlueEscape.Length;
            }
            else
            {
                result.Add(data[i]);
                i++;
            }
        }
        return result.ToArray();
    }

    // codex 가 '사용자 메시지 박스' 배경에 쓰는 고정 truecolor(rgb 41,41,41, 테마 무관 UI 상수).
    private static readonly byte[] _codexUserMsgBgSrc =
        System.Text.Encoding.ASCII.GetBytes("\x1b[48;2;41;41;41m");
    private static readonly byte[] _codexDiffAddedBgSrc =
        System.Text.Encoding.ASCII.GetBytes("\x1b[48;2;33;58;43m");
    private static readonly byte[] _codexDiffRemovedBgSrc =
        System.Text.Encoding.ASCII.GetBytes("\x1b[48;2;74;34;29m");
    private static readonly byte[] _codexLegacyDiffAddedBgSrc =
        System.Text.Encoding.ASCII.GetBytes("\x1b[48;2;48;58;48m");
    private static readonly byte[] _codexLegacyDiffRemovedBgSrc =
        System.Text.Encoding.ASCII.GetBytes("\x1b[48;2;58;48;48m");

    // 앱 테마별 '내 메시지' 박스 배경. 밝은 테마에서는 currentLine보다 반 단계 진하게 잡아
    // 프롬프트·입력 영역이 터미널 바탕과 뭉개지지 않게 한다.
    // codex 는 xterm 스킴(DevezCode Dark/Soft/Minimal)을 따라 배경·기본전경이 테마별로 바뀌므로
    // (soft/minimal 은 밝은 배경+어두운 글자) 밝은 박스에서도 글자가 읽힌다.
    // 스킴 매핑(App.xaml.cs)과 동일 규칙: dark→Dark, soft→Soft, 그 외→Minimal.
    private static byte[] CodexUserMsgBgTarget() =>
        System.Text.Encoding.ASCII.GetBytes(DevezCode.App.CurrentTheme switch
        {
            "dark" => "\x1b[48;2;39;39;39m",     // gjc currentLine #272727
            "soft" => "\x1b[48;2;231;224;213m",  // soft #E7E0D5
            _      => "\x1b[48;2;234;240;245m",  // minimal #EAF0F5
        });

    // ── codex OSC 10/11 색상 질의 프록시 응답 ──────────────────────────────
    // codex(0.144+)는 시작 직후 OSC 11(배경색) 질의(ESC]11;?)로 터미널 라이트/다크를 판별해
    // 컴포저(입력 영역) 색을 고른다. 앱 콜드 스타트의 세션 복원 경로에선 xterm(WebView2)이
    // 아직 부팅 중이라 응답이 수 초 늦고, codex 는 타임아웃 → 라이트 팔레트 폴백(다크 테마에
    // 흰 입력 영역). xterm 을 기다리지 않고 여기서 즉시 현재 스킴 색으로 응답한다.
    // 질의 바이트는 스트림에서 삼킨다 — 남겨 두면 xterm 도 (늦게) 응답해 이중 응답이 되고,
    // 소비자가 사라진 늦은 응답은 컴포저에 텍스트로 샌다(openai/codex#5107 유형).
    // 질의가 flush 청크 경계에서 쪼개진 경우만 매칭 실패로 xterm 경로에 넘어간다(드묾, 무해).
    private byte[] AnswerCodexColorQueries(string roomId, byte[] data)
    {
        List<byte>? kept = null; // 질의를 만나기 전까지는 무할당 통과(흔한 경로)
        int copied = 0, i = 0;
        while (i < data.Length)
        {
            // ESC ] 1 (0|1) ; ? (BEL | ESC \)
            if (data[i] != 0x1B || i + 7 > data.Length
                || data[i + 1] != (byte)']' || data[i + 2] != (byte)'1'
                || (data[i + 3] != (byte)'0' && data[i + 3] != (byte)'1')
                || data[i + 4] != (byte)';' || data[i + 5] != (byte)'?')
            { i++; continue; }

            int end; bool bel;
            if (data[i + 6] == 0x07) { end = i + 7; bel = true; }
            else if (data[i + 6] == 0x1B && i + 8 <= data.Length && data[i + 7] == (byte)'\\') { end = i + 8; bel = false; }
            else { i++; continue; }

            RespondCodexColorQuery(roomId, background: data[i + 3] == (byte)'1', bel);
            kept ??= new List<byte>(data.Length);
            for (int k = copied; k < i; k++) kept.Add(data[k]);
            copied = end;
            i = end;
        }
        if (kept == null) return data;
        for (int k = copied; k < data.Length; k++) kept.Add(data[k]);
        return kept.ToArray();
    }

    /// <summary>OSC 10(전경)/11(배경) 질의에 현재 xterm 스킴 색으로 즉시 응답. 종결자는 질의와 동일 형식.</summary>
    private void RespondCodexColorQuery(string roomId, bool background, bool bel)
    {
        if (!_wired.TryGetValue(roomId, out var session)) return;
        // 확정 저장된 테마의 스킴으로 응답. 라이브 Config.Scheme(설정창 미리보기 포함)를 쓰면
        // 미리보기 중 codex 가 재질의 시 preview 색으로 컴포저를 굳혀, 취소해도 안 돌아온다.
        var scheme = DevezCode.App.SchemeForTheme(DevezCode.App.CommittedTheme);
        string hex = background ? scheme.Background : scheme.Foreground;
        System.Drawing.Color c;
        try { c = System.Drawing.ColorTranslator.FromHtml(string.IsNullOrWhiteSpace(hex) ? "#0C0C0C" : hex); }
        catch { c = System.Drawing.Color.FromArgb(0x0C, 0x0C, 0x0C); }
        // xterm 관례의 16bit/채널 표기 — 8bit 값을 두 번 이어 붙인다(0x1F → 1f1f).
        string rgb = $"rgb:{c.R:x2}{c.R:x2}/{c.G:x2}{c.G:x2}/{c.B:x2}{c.B:x2}";
        string reply = "\x1b]" + (background ? "11" : "10") + ";" + rgb + (bel ? "\a" : "\x1b\\");
        session.Write(reply);
        DevezCode.Services.DiagLog.Write($"codex OSC {(background ? 11 : 10)} 색상 질의 → 프록시 응답 {rgb} room={roomId}");
    }

    private static byte[] RecolorCodexBackgrounds(byte[] data)
    {
        data = ReplaceCodexBackground(data, _codexUserMsgBgSrc, CodexUserMsgBgTarget());
        if (DevezCode.App.CurrentTheme == "dark") return data;

        bool isSoft = DevezCode.App.CurrentTheme == "soft";
        var added = System.Text.Encoding.ASCII.GetBytes(isSoft
            ? "\x1b[48;2;222;236;214m"  // Claude soft diffAdded #DEECD6
            : "\x1b[48;2;219;234;254m"); // Claude minimal diffAdded #DBEAFE
        var removed = System.Text.Encoding.ASCII.GetBytes(isSoft
            ? "\x1b[48;2;242;214;214m"  // Claude soft diffRemoved #F2D6D6
            : "\x1b[48;2;254;226;226m"); // Claude minimal diffRemoved #FEE2E2

        data = ReplaceCodexBackground(data, _codexDiffAddedBgSrc, added);
        data = ReplaceCodexBackground(data, _codexLegacyDiffAddedBgSrc, added);
        data = ReplaceCodexBackground(data, _codexDiffRemovedBgSrc, removed);
        return ReplaceCodexBackground(data, _codexLegacyDiffRemovedBgSrc, removed);
    }

    private static byte[] ReplaceCodexBackground(byte[] data, byte[] source, byte[] target)
    {
        if (IndexOfBytes(data, source, 0) < 0) return data;
        var result = new List<byte>(data.Length);
        int i = 0;
        while (i < data.Length)
        {
            if (i + source.Length <= data.Length && MatchesAt(data, i, source))
            {
                result.AddRange(target);
                i += source.Length;
            }
            else { result.Add(data[i]); i++; }
        }
        return result.ToArray();
    }

    // SGR truecolor 전경/배경의 표준 표기 + xterm 256색.
    private static readonly Regex GrokFgRgbSemicolon = new(
        @"(?<!\d)38;2;(?<r>\d{1,3});(?<g>\d{1,3});(?<b>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokFgRgbColonWithSpace = new(
        @"(?<!\d)38:2::(?<r>\d{1,3}):(?<g>\d{1,3}):(?<b>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokFgRgbColonWithColorSpace = new(
        @"(?<!\d)38:2:\d+:(?<r>\d{1,3}):(?<g>\d{1,3}):(?<b>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokFgRgbColon = new(
        @"(?<!\d)38:2:(?<r>\d{1,3}):(?<g>\d{1,3}):(?<b>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokFgIndexed = new(
        @"(?<!\d)38;5;(?<index>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokFgIndexedColon = new(
        @"(?<!\d)38:5:(?<index>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokBgRgbSemicolon = new(
        @"(?<!\d)48;2;(?<r>\d{1,3});(?<g>\d{1,3});(?<b>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokBgRgbColonWithSpace = new(
        @"(?<!\d)48:2::(?<r>\d{1,3}):(?<g>\d{1,3}):(?<b>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokBgRgbColonWithColorSpace = new(
        @"(?<!\d)48:2:\d+:(?<r>\d{1,3}):(?<g>\d{1,3}):(?<b>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokBgRgbColon = new(
        @"(?<!\d)48:2:(?<r>\d{1,3}):(?<g>\d{1,3}):(?<b>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokBgIndexed = new(
        @"(?<!\d)48;5;(?<index>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex GrokBgIndexedColon = new(
        @"(?<!\d)48:5:(?<index>\d{1,3})(?!\d)", RegexOptions.Compiled);

    /// <summary>GrokNight/GrokDay의 저채도 배경 계층을 DevezCode 배경 기준으로 평행 이동한다.
    /// soft/minimal에서는 GrokDay의 고정 truecolor 전경도 DevezCode 의미색으로 매핑한다.</summary>
    private byte[] RecolorGrokTerminalColors(string roomId, byte[] data)
    {
        byte[] input;
        if (_grokCsiTails.Remove(roomId, out var tail) && tail.Length > 0)
        {
            input = new byte[tail.Length + data.Length];
            Buffer.BlockCopy(tail, 0, input, 0, tail.Length);
            Buffer.BlockCopy(data, 0, input, tail.Length, data.Length);
        }
        else input = data;

        var output = new List<byte>(input.Length + 16);
        int i = 0;
        while (i < input.Length)
        {
            if (input[i] != 0x1b)
            {
                output.Add(input[i++]);
                continue;
            }

            // ESC 자체가 마지막 바이트면 다음 flush에서 CSI/OSC 여부를 판정한다.
            if (i + 1 >= input.Length)
            {
                _grokCsiTails[roomId] = input[i..];
                break;
            }
            if (input[i + 1] != (byte)'[')
            {
                output.Add(input[i++]);
                continue;
            }

            int end = i + 2;
            while (end < input.Length && !(input[end] >= 0x40 && input[end] <= 0x7e)) end++;
            if (end >= input.Length)
            {
                // 정상 CSI는 수십 바이트 이내다. 비정상 장문을 무한 보관하지 않고 원문 통과.
                if (input.Length - i <= 128) _grokCsiTails[roomId] = input[i..];
                else output.AddRange(input.AsSpan(i).ToArray());
                break;
            }

            if (input[end] == (byte)'m')
            {
                var sgr = Encoding.ASCII.GetString(input, i, end - i + 1);
                output.AddRange(Encoding.ASCII.GetBytes(TransformGrokColorSgr(sgr)));
            }
            else
            {
                for (int p = i; p <= end; p++) output.Add(input[p]);
            }
            i = end + 1;
        }
        return output.ToArray();
    }

    private static string TransformGrokColorSgr(string sgr)
    {
        bool hasExtendedColor = sgr.Contains("38;", StringComparison.Ordinal)
            || sgr.Contains("38:", StringComparison.Ordinal)
            || sgr.Contains("48;", StringComparison.Ordinal)
            || sgr.Contains("48:", StringComparison.Ordinal);
        if (!hasExtendedColor)
            return DevezCode.App.CurrentTheme == "dark" ? sgr : RemapGrokAnsiPurple(sgr);

        string ReplaceBackgroundRgb(Match match)
        {
            if (!TryByteGroup(match, "r", out var r) || !TryByteGroup(match, "g", out var g)
                || !TryByteGroup(match, "b", out var b) || !TryMapGrokNeutralBackground(r, g, b, out var mapped))
                return match.Value;
            return $"48;2;{mapped.R};{mapped.G};{mapped.B}";
        }

        // semicolon을 먼저 처리해야 colon→semicolon 변환 결과가 같은 호출에서 이중 변환되지 않는다.
        var result = GrokBgRgbSemicolon.Replace(sgr, ReplaceBackgroundRgb);
        result = GrokBgRgbColonWithSpace.Replace(result, ReplaceBackgroundRgb);
        result = GrokBgRgbColonWithColorSpace.Replace(result, ReplaceBackgroundRgb);
        result = GrokBgRgbColon.Replace(result, ReplaceBackgroundRgb);
        string ReplaceBackgroundIndexed(Match match)
        {
            if (!int.TryParse(match.Groups["index"].Value, out var index) || index is < 16 or > 255
                || !TryXterm256Rgb(index, out var r, out var g, out var b)
                || !TryMapGrokNeutralBackground(r, g, b, out var mapped,
                    DevezCode.App.CurrentTheme == "dark" ? 18 : 238))
                return match.Value;
            return $"48;2;{mapped.R};{mapped.G};{mapped.B}";
        }
        result = GrokBgIndexed.Replace(result, ReplaceBackgroundIndexed);
        result = GrokBgIndexedColon.Replace(result, ReplaceBackgroundIndexed);

        if (DevezCode.App.CurrentTheme == "dark") return result;

        string ReplaceForegroundRgb(Match match)
        {
            if (!TryByteGroup(match, "r", out var r) || !TryByteGroup(match, "g", out var g)
                || !TryByteGroup(match, "b", out var b) || !TryMapGrokForeground(r, g, b, out var mapped))
                return match.Value;
            return $"38;2;{mapped.R};{mapped.G};{mapped.B}";
        }

        result = GrokFgRgbSemicolon.Replace(result, ReplaceForegroundRgb);
        result = GrokFgRgbColonWithSpace.Replace(result, ReplaceForegroundRgb);
        result = GrokFgRgbColonWithColorSpace.Replace(result, ReplaceForegroundRgb);
        result = GrokFgRgbColon.Replace(result, ReplaceForegroundRgb);
        string ReplaceForegroundIndexed(Match match)
        {
            if (!int.TryParse(match.Groups["index"].Value, out var index) || index is < 16 or > 255
                || !TryXterm256Rgb(index, out var r, out var g, out var b)
                || !TryMapGrokForeground(r, g, b, out var mapped))
                return match.Value;
            return $"38;2;{mapped.R};{mapped.G};{mapped.B}";
        }
        result = GrokFgIndexed.Replace(result, ReplaceForegroundIndexed);
        result = GrokFgIndexedColon.Replace(result, ReplaceForegroundIndexed);
        return RemapGrokAnsiPurple(result);
    }

    /// <summary>GrokDay의 주 강조색인 ANSI magenta(35/95)를 앱의 주 강조색인 blue(34/94) 슬롯으로 옮긴다.
    /// RGB 구성값 안의 35/95를 건드리지 않도록 SGR 파라미터를 순회하며 extended color 구간은 건너뛴다.</summary>
    private static string RemapGrokAnsiPurple(string sgr)
    {
        int bracket = sgr.IndexOf('[');
        int end = sgr.LastIndexOf('m');
        if (bracket < 0 || end <= bracket + 1) return sgr;

        var parameters = sgr[(bracket + 1)..end].Split(';');
        bool changed = false;
        for (int i = 0; i < parameters.Length; i++)
        {
            if ((parameters[i] == "38" || parameters[i] == "48") && i + 1 < parameters.Length)
            {
                if (parameters[i + 1] == "2") { i = Math.Min(i + 4, parameters.Length - 1); continue; }
                if (parameters[i + 1] == "5") { i = Math.Min(i + 2, parameters.Length - 1); continue; }
            }
            if (parameters[i] == "35") { parameters[i] = "34"; changed = true; }
            else if (parameters[i] == "95") { parameters[i] = "94"; changed = true; }
        }
        return changed ? sgr[..(bracket + 1)] + string.Join(';', parameters) + sgr[end..] : sgr;
    }

    private static bool TryMapGrokForeground(int r, int g, int b, out (int R, int G, int B) mapped)
    {
        mapped = default;
        if (DevezCode.App.CurrentTheme == "dark"
            || (uint)r > 255 || (uint)g > 255 || (uint)b > 255) return false;

        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        int chroma = max - min;
        var scheme = TerminalSessionManager.Instance.Config.Scheme;

        // GrokDay 0.2.99의 본문 #262626 ~ 비활성 #CFCFCF 중립 램프를 현재 스킴의
        // Foreground→Background 사이로 옮겨 soft는 따뜻한 회색, minimal은 청회색 계층을 만든다.
        if (chroma <= 12)
        {
            int level = (r + g + b) / 3;
            if (level is < 24 or > 220
                || !TryParseRgbHex(scheme.Foreground, out var foreground)
                || !TryParseRgbHex(scheme.Background, out var background)) return false;

            double t = Math.Clamp((level - 38) / 200.0, 0, 1);
            mapped = (
                (int)Math.Round(foreground.R + (background.R - foreground.R) * t),
                (int)Math.Round(foreground.G + (background.G - foreground.G) * t),
                (int)Math.Round(foreground.B + (background.B - foreground.B) * t));
            return true;
        }

        // GrokDay가 직접 내보내는 truecolor 의미색을 색상 역할로 분류한다. 정확한 원본 RGB가
        // 버전에서 바뀌어도 hue 역할은 유지되므로 Codex식 고정값 치환보다 업데이트에 강하다.
        if (chroma < 24) return false;
        double hue = RgbHue(r, g, b, max, chroma);
        bool bright = max >= 185;
        // GrokDay는 보라 계열을 주 강조색으로 쓰지만 DevezCode의 주 강조색 슬롯은 Blue다.
        // 따라서 soft에서는 초록, minimal에서는 파랑으로 바뀌어 앱 테마와 같은 인상을 준다.
        string target = hue < 20 || hue >= 340 ? (bright ? scheme.BrightRed : scheme.Red)
            : hue < 70  ? (bright ? scheme.BrightYellow : scheme.Yellow)
            : hue < 170 ? (bright ? scheme.BrightGreen : scheme.Green)
            : hue < 200 ? (bright ? scheme.BrightCyan : scheme.Cyan)
            : hue < 255 ? (bright ? scheme.BrightBlue : scheme.Blue)
            :              (bright ? scheme.BrightBlue : scheme.Blue);
        return TryParseRgbHex(target, out mapped);
    }

    private static double RgbHue(int r, int g, int b, int max, int chroma)
    {
        if (chroma == 0) return 0;
        double hue = max == r ? 60.0 * ((g - b) / (double)chroma % 6)
            : max == g ? 60.0 * ((b - r) / (double)chroma + 2)
            :            60.0 * ((r - g) / (double)chroma + 4);
        return hue < 0 ? hue + 360 : hue;
    }

    private static bool TryParseRgbHex(string? hex, out (int R, int G, int B) rgb)
    {
        rgb = default;
        hex = hex?.Trim();
        if (string.IsNullOrEmpty(hex) || hex.Length < 7 || hex[0] != '#') return false;
        var style = System.Globalization.NumberStyles.HexNumber;
        if (!int.TryParse(hex.AsSpan(1, 2), style, null, out var r)
            || !int.TryParse(hex.AsSpan(3, 2), style, null, out var g)
            || !int.TryParse(hex.AsSpan(5, 2), style, null, out var b)) return false;
        rgb = (r, g, b);
        return true;
    }

    private static bool TryMapGrokNeutralBackground(
        int r, int g, int b, out (int R, int G, int B) mapped, int? sourceBaseOverride = null)
    {
        mapped = default;
        if ((uint)r > 255 || (uint)g > 255 || (uint)b > 255) return false;

        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        int chroma = max - min;
        // 어두운 diff 배경(#303a30 등)은 절대 RGB 차가 작아도 상대 채도가 높다.
        if (chroma > 12 || (max > 0 && chroma / (double)max > 0.14)) return false;

        bool dark = DevezCode.App.CurrentTheme == "dark";
        if (dark ? max > 96 : min < 180) return false; // 활성 내장 테마의 중립 배경군만 허용

        var target = SchemeBackgroundRgb();
        // Grok 0.2.93의 실제 ConPTY 출력에서 캔버스는 GrokNight #141414,
        // GrokDay #EEEEEE를 쓴다. 터미널 기본 배경처럼 보여도 명시된 SGR 색이다.
        // 이 기준 표면만 DevezCode Background에 정확히 붙이고, 다른 중립 패널은 상대 명도차를 보존한다.
        int sourceBase = sourceBaseOverride ?? (dark ? 20 : 238);
        int neutralLevel = (r + g + b) / 3;
        bool baseSurface = Math.Abs(neutralLevel - sourceBase) <= 2;
        if (baseSurface)
        {
            mapped = target;
            return true;
        }

        // GrokDay의 패널은 캔버스보다 20~26 단계나 어두워 밝은 앱 테마에서 과하게 튄다.
        // Codex/Claude 밝은 테마처럼 절반 대비만 유지해 diff·프롬프트 박스를 부드럽게 만든다.
        double contrast = dark ? 1.0 : 0.5;
        mapped = (
            Math.Clamp((int)Math.Round(target.R + (r - sourceBase) * contrast), 0, 255),
            Math.Clamp((int)Math.Round(target.G + (g - sourceBase) * contrast), 0, 255),
            Math.Clamp((int)Math.Round(target.B + (b - sourceBase) * contrast), 0, 255));
        return true;
    }

    private static (int R, int G, int B) SchemeBackgroundRgb()
    {
        try
        {
            var hex = TerminalSessionManager.Instance.Config.Scheme.Background?.Trim();
            if (!string.IsNullOrEmpty(hex) && hex.Length >= 7 && hex[0] == '#'
                && int.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                && int.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                && int.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                return (r, g, b);
        }
        catch { }
        return DevezCode.App.CurrentTheme switch
        {
            "dark" => (31, 31, 30),
            "soft" => (242, 237, 230),
            _ => (248, 250, 252),
        };
    }

    private static bool TryByteGroup(Match match, string name, out int value) =>
        int.TryParse(match.Groups[name].Value, out value) && value is >= 0 and <= 255;

    private static bool TryXterm256Rgb(int index, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (index is >= 232 and <= 255)
        {
            r = g = b = 8 + (index - 232) * 10;
            return true;
        }
        if (index is < 16 or > 231) return false;
        int cube = index - 16;
        int[] levels = { 0, 95, 135, 175, 215, 255 };
        r = levels[cube / 36];
        g = levels[(cube / 6) % 6];
        b = levels[cube % 6];
        return true;
    }

    private static int IndexOfBytes(byte[] data, byte[] pattern, int start)
    {
        for (int i = start; i + pattern.Length <= data.Length; i++)
            if (MatchesAt(data, i, pattern)) return i;
        return -1;
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
    {
        for (int j = 0; j < pattern.Length; j++)
            if (data[offset + j] != pattern[j]) return false;
        return true;
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

    /// <summary>지정한 방(roomId)의 폰트 크기만 pt 단위로 절대 지정(콤보박스 선택 등).
    /// 전역 기본값(Ctrl+휠/Ctrl+0)과 무관하게 그 방에만 적용, 방별로 영구 저장.</summary>
    public void SetRoomFontSizePt(string roomId, int pt)
    {
        pt = Math.Max(6, Math.Min(72, pt));
        double px = Math.Round(pt * PtToPx, 1);
        PostJson(new { type = "setRoomFontSize", roomId, size = px });
        DevezCode.Services.SettingsService.SaveTerminalRoomFontSizePt(roomId, pt);
        if (roomId == _activeRoomId) FontSizePxChanged?.Invoke(px);
    }

    /// <summary>해당 방의 유효 폰트 크기(px). 방별 지정이 없으면 전역 기본값.</summary>
    public double RoomEffectiveFontSizePx(string roomId)
    {
        var saved = DevezCode.Services.SettingsService.LoadTerminalRoomFontSizePt(roomId);
        return saved.HasValue ? Math.Round(saved.Value * PtToPx, 1) : EffectiveFontSizePx;
    }

    /// <summary>show/preload 시 xterm 생성에 넘길 방별 폰트 override(px). 지정 없으면 null → JS 가 전역 cfg.fontSize 사용.</summary>
    private static double? RoomFontSizeOverridePx(string roomId)
    {
        var saved = DevezCode.Services.SettingsService.LoadTerminalRoomFontSizePt(roomId);
        return saved.HasValue ? Math.Round(saved.Value * PtToPx, 1) : null;
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
    /// WPF Clipboard.SetDataObject(text, true) 는 OleFlushClipboard 경로라 clipboard viewer chain 을
    /// 동기 호출해 RDP/클립보드 매니저/백신 프로세스와 교착될 수 있다. 텍스트는 Win32
    /// CF_UNICODETEXT 로 직접 기록해 OLE flush 를 피한다.</summary>
    private static void SetClipboardText(string text)
    {
        var owner = GetClipboardOwnerHandle();

        // 클립보드가 다른 프로세스에 점유돼 있으면 OpenClipboard 가 실패한다. UI 스레드는 즉시 반환하고
        // 백그라운드 STA 에서 짧게만 재시도한다. DevezCode 내부 copy-on-select 연타끼리는 직렬화해
        // 우리 프로세스가 스스로 클립보드 점유 경쟁을 만들지 않게 한다.
        RunClipboardSta(() =>
        {
            lock (ClipboardWriteLock)
            {
                for (int i = 0; i < 6; i++)
                {
                    if (TrySetClipboardUnicodeText(text, owner)) return;
                    System.Threading.Thread.Sleep(25 + i * 15);
                }
            }
        });
    }

    private static IntPtr GetClipboardOwnerHandle()
    {
        try
        {
            var app = Application.Current;
            if (app?.MainWindow != null)
            {
                if (app.Dispatcher.CheckAccess())
                    return new WindowInteropHelper(app.MainWindow).Handle;

                return app.Dispatcher.Invoke(() => new WindowInteropHelper(app.MainWindow).Handle);
            }
        }
        catch { }

        try { return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
        catch { return IntPtr.Zero; }
    }

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static bool TrySetClipboardUnicodeText(string text, IntPtr owner)
    {
        if (owner == IntPtr.Zero) return false;

        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        var hGlobal = IntPtr.Zero;
        var locked = IntPtr.Zero;
        var opened = false;

        try
        {
            hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (hGlobal == IntPtr.Zero) return false;

            locked = GlobalLock(hGlobal);
            if (locked == IntPtr.Zero) return false;

            Marshal.Copy(bytes, 0, locked, bytes.Length);
            GlobalUnlock(hGlobal);
            locked = IntPtr.Zero;

            if (!OpenClipboard(owner)) return false;
            opened = true;

            if (!EmptyClipboard()) return false;
            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero) return false;

            hGlobal = IntPtr.Zero; // ownership transferred to the clipboard
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (locked != IntPtr.Zero) GlobalUnlock(hGlobal);
            if (opened) CloseClipboard();
            if (hGlobal != IntPtr.Zero) GlobalFree(hGlobal);
        }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>붙여넣기용 클립보드 읽기. 클립보드를 1회만 열고(OLE GetDataObject) 그 스냅샷에서
    /// 이미지→텍스트 순으로 읽는다. 기존엔 ContainsText+GetText / ContainsImage+GetImage 로 매번
    /// 2회씩 열어 클립보드 매니저·백신·RDP 리디렉션이 물린 PC 에서 두 번째 열기가 실패 → 빈값 →
    /// Ctrl+V 먹통이 잦았다. 열기 횟수를 절반으로 줄이고 재시도를 넉넉히(STA 스레드라 대기 무해).
    /// 이미지면 (파일경로, null), 텍스트면 (null, 텍스트), 없으면 (null, null).</summary>
    private static (string? imagePath, string? text) ReadClipboardForPaste()
    {
        for (int i = 0; i < 8; i++)
        {
            try
            {
                var data = System.Windows.Clipboard.GetDataObject();
                if (data == null) return (null, null);

                // 이미지 우선(claude 이미지 첨부) — 저장 성공 시 파일 경로 반환.
                // WPF DataFormats.Bitmap(CF_BITMAP/HBITMAP)은 캡처 도구의 delayed-rendering
                // 클립보드에서 무효 핸들→검은 화면을 반환하는 경우가 있어, 실제 픽셀 바이트인
                // CF_DIB 를 직접 읽어 BMP 파일 헤더를 씌워 디코딩한다(핸들이 아니라 raw 데이터라 무관).
                // 단, raw OpenClipboard 는 텍스트 붙여넣기에서 클립보드를 한 번 더 여는 셈이라
                // (원래 이 함수가 고치려던 "두 번 열어서 실패" 문제 재발), 실제 이미지가 있을 때만 시도한다.
                if (data.GetDataPresent(System.Windows.DataFormats.Bitmap))
                {
                    var dibBytes = GetClipboardDibBytes();
                    if (dibBytes != null)
                    {
                        using var bmp = DibToBitmap(dibBytes);
                        if (bmp != null)
                        {
                            var path = SaveBitmap(bmp);
                            if (path != null) return (path, null);
                        }
                    }
                }

                if (data.GetDataPresent(System.Windows.DataFormats.UnicodeText))
                    return (null, data.GetData(System.Windows.DataFormats.UnicodeText) as string ?? "");
                if (data.GetDataPresent(System.Windows.DataFormats.Text))
                    return (null, data.GetData(System.Windows.DataFormats.Text) as string ?? "");

                return (null, null); // 열기는 됐으나 텍스트·이미지 없음 → 재시도 불필요
            }
            catch { System.Threading.Thread.Sleep(30); } // 경합 — 잠깐 뒤 재시도
        }
        return (null, null);
    }

    private const uint CF_DIB = 8;

    /// <summary>클립보드의 CF_DIB(raw 픽셀 바이트) 를 읽는다. CF_BITMAP(HBITMAP)과 달리 핸들이 아니라
    /// 실제 데이터라 delayed-rendering 캡처 도구에서도 안전하다. 없으면 null.</summary>
    private static byte[]? GetClipboardDibBytes()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var hMem = GetClipboardData(CF_DIB);
            if (hMem == IntPtr.Zero) return null;
            var size = (int)GlobalSize(hMem);
            if (size <= 0) return null;
            var ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, size);
                return bytes;
            }
            finally { GlobalUnlock(hMem); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>CF_DIB(BITMAPINFOHEADER+팔레트+픽셀) 바이트에 BITMAPFILEHEADER(14바이트) 를 씌워
    /// 표준 BMP 로 만들고 디코딩한다. 실패 시 null.</summary>
    private static System.Drawing.Bitmap? DibToBitmap(byte[] dib)
    {
        try
        {
            if (dib.Length < 40) return null;
            int headerSize = BitConverter.ToInt32(dib, 0);
            short bitCount = BitConverter.ToInt16(dib, 14);
            int compression = BitConverter.ToInt32(dib, 16);
            int clrUsed = BitConverter.ToInt32(dib, 32);
            // BI_BITFIELDS(3) + 기본 40바이트 헤더면 RGB 마스크 3개(DWORD)가 헤더 뒤에 추가로 붙는다.
            int maskBytes = (compression == 3 && headerSize == 40) ? 12 : 0;
            int paletteEntries = clrUsed > 0 ? clrUsed : (bitCount <= 8 ? (1 << bitCount) : 0);
            int offBits = 14 + headerSize + maskBytes + paletteEntries * 4;

            // System.Drawing.Bitmap(Stream) 은 지연 디코딩 시 스트림을 계속 참조할 수 있어(GDI+ 요구사항:
            // "Bitmap 생명주기 동안 스트림을 열어둬야 한다") using 으로 여기서 닫으면 안 된다 —
            // 스트림을 미리 닫으면 이후 Save() 시점에 "매개 변수가 잘못되었습니다" 예외가 날 수 있다.
            var bmpBytes = new byte[14 + dib.Length];
            bmpBytes[0] = (byte)'B'; bmpBytes[1] = (byte)'M';
            BitConverter.GetBytes(14 + dib.Length).CopyTo(bmpBytes, 2);
            BitConverter.GetBytes(0).CopyTo(bmpBytes, 6);
            BitConverter.GetBytes(offBits).CopyTo(bmpBytes, 10);
            Buffer.BlockCopy(dib, 0, bmpBytes, 14, dib.Length);
            return new System.Drawing.Bitmap(new System.IO.MemoryStream(bmpBytes));
        }
        catch { return null; }
    }

    /// <summary>System.Drawing.Bitmap 을 %TEMP%\DevezCode\clipboard\ 에 PNG 로 저장 후 경로 반환. 실패 시 null.</summary>
    private static string? SaveBitmap(System.Drawing.Bitmap bmp)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DevezCode", "clipboard");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMddHHmmssfff}.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
        catch { return null; }
    }

    /// <summary>다음 N회의 출력 쓰기에서 xterm.js 스크롤을 억제(슬래시 명령 자동주입 시 사용).</summary>
    public void SuppressScroll(int count = 5) => PostJson(new { type = "suppressScroll", count });

    /// <summary>에이전트 응답 완료 후 xterm/WebView2 의 잔류 IME 조합 상태를 정리한다.
    /// JS 쪽에서 현재 활성·포커스된 해당 방인지 재확인하므로 비활성 패널 호출은 무해하다.</summary>
    public void ResetImeAfterResponse(string roomId)
    {
        if (_pageReady) PostJson(new { type = "resetImeAfterResponse", roomId });
    }

    /// <summary>Collapsed(0폭) 였다 되살아난(preserve) 패널을 자연스럽게 복원한다. JS 에서 컨테이너를 잠깐
    /// 투명(opacity:0, 아래 터미널 배경색만 노출)으로 덮은 뒤, 폭이 최종값으로 확정될 때까지 기다려 fit +
    /// resize-kick(cols-1→cols)으로 ConPTY 를 xterm 의 실제 cols 로 재동기(SIGWINCH→TUI 재렌더)하고,
    /// 안정되면 부드럽게 fade-in 한다. → 틀어진 중간 프레임이 안 보이고 최종 화면만 나타난다.
    /// (WebView2 는 HwndHost 라 WPF 오버레이로 못 덮으므로 반드시 웹 레이어 안에서 가린다.)</summary>
    public void ResyncOnReturn(string roomId) => PostJson(new { type = "returnResync", roomId });

    /// <summary>분할 열림/닫힘 전환 직전 호출 — 웹 레이어 단색 커튼으로 터미널을 즉시 덮어 이후 리사이즈
    /// 리플로우 깜빡임을 감춘다. RevealAfterTransition 으로 걷는다(누락돼도 2s 뒤 자동 해제).</summary>
    public void CoverForTransition() => PostJson(new { type = "xferCover" });

    /// <summary>전환 커버를 단색 대신 '캡처 이미지'로 띄운다. HWND 를 Collapsed 로 숨겼다 되살릴 때(사이드패널
    /// 토글 등) 커버 이미지가 직전 WPF 스냅샷과 동일 내용이라 handoff 가 무깜빡이고, RevealAfterTransition 시
    /// 최종 폭 터미널로 크로스페이드된다. imgW/imgH 는 캡처 시점 컨테이너 크기(DIP=CSS px)로 좌상단 고정 표시.
    /// stretch=true 면 좌상단 px 고정 대신 뷰포트에 맞춰 늘린다 — 전체화면 토글처럼 창 전체가 한 번에
    /// 크게 변하는 전환용(px 고정은 창이 커질 때 캡처 밖 영역이 배경색만 남아 '비어' 보인다).</summary>
    public void CoverForTransitionImage(byte[] png, double imgW, double imgH, bool stretch = false)
        => PostJson(new { type = "xferCover", image = "data:image/png;base64," + Convert.ToBase64String(png), imgW, imgH, stretch });

    /// <summary>전환 후 호출 — 레이아웃이 최종 폭으로 확정되면 fit 으로 재측정(→ConPTY resize→TUI 재렌더)한
    /// 뒤 커튼을 fade-out 한다. roomId 는 fit 대상(활성 세션). 없으면 그냥 커튼만 걷는다.</summary>
    /// <summary>expectWidth: C# 이 UpdateLayout 으로 확정한 전환 후 최종 폭(px). JS 는 컨테이너 clientWidth 가
    /// 이 목표에 근접할 때까지 기다렸다 fit 한다 — 전체폭→절반 전환의 중간 전체폭 plateau(HWND 지연)를 건너뛰기 위함.</summary>
    /// <summary>bounce=true: post-hoc 전환(리사이즈가 커버 '전'에 이미 발생 — OS 주도 최대화/복원 등)용.
    /// reveal 의 fit 이 무변화면 same-size 킥은 no-op 이라 재방출이 없어 무방비 리사이즈의 tear 가
    /// 고착될 수 있다 → rows-1→rows 바운스로 커버 아래서 깨끗한 전체 재방출을 강제한다.</summary>
    public void RevealAfterTransition(string? roomId, bool kick = false, double expectWidth = 0, bool bounce = false)
        => PostJson(new { type = "xferReveal", roomId, kick, expectWidth, bounce });

    /// <summary>동시(synced) reveal 준비 — 폭 안정·fit·재동기까지만 하고 커튼은 유지한 채 RevealPrepared 를 낸다.
    /// 셸이 좌우 모두의 준비를 받으면 FadeNow 로 동시에 걷는다(느린 쪽 기준으로 함께 표시).</summary>
    public void PrepareRevealSynced(string? roomId, bool kick, double expectWidth)
        => PostJson(new { type = "xferReveal", roomId, kick, expectWidth, synced = true });

    /// <summary>커튼을 즉시 fade-out(synced reveal 의 최종 단계).</summary>
    public void FadeNow() => PostJson(new { type = "fadeNow" });

    /// <summary>ms 동안 출력 쓰기 후 맨 아래로 고정 — 인라인 TUI(gjc) open 직후 최신 화면을 보이게(짧은 창).</summary>
    public void PinBottom(int ms = 2000) => PostJson(new { type = "pinBottom", ms });

    /// <summary>TerminalReady 통지(중복 방지).</summary>
    private void NotifyReady(string roomId)
    {
        if (_readyNotified.Add(roomId))
        {
            DevezCode.Services.DiagLog.Write($"NotifyReady room={roomId}");
            TerminalReady?.Invoke(roomId);
        }
    }

    /// <summary>Codex 출력 정지 후 xterm 쓰기와 브라우저 페인트가 끝났는지 확인한다.</summary>
    private void RequestReadyPaint(string roomId)
    {
        if (!_outputGenerations.TryGetValue(roomId, out var generation)) return;
        PostJson(new { type = "readyPaintProbe", roomId, generation });
    }

    /// <summary>인라인 TUI(gjc) 방이면 show 직후 짧은 창 동안 스크롤을 맨 아래로 고정(open 시 최신 표시).
    /// 이후엔 일반 동작 — gjc 는 멀티플렉서 모드(STY)로 스크롤백을 보존하므로 휠로 과거 대화를 스크롤할 수 있다.</summary>
    private void PinBottomIfInline(string roomId)
    {
        if (DevezCode.Services.AgentRegistry.Find(AgentFor(roomId))?.InlineTui == true)
            PinBottom(1500);
    }

    /// <summary>현재 터미널 화면을 PNG 바이트로 캡처. WPF 스냅샷·웹 커버 이미지 공용(한 번만 캡처).</summary>
    public async Task<byte[]?> CapturePngAsync()
    {
        if (_webView?.CoreWebView2 == null) return null;
        try
        {
            using var ms = new MemoryStream();
            await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>현재 터미널 화면을 PNG 스냅샷(BitmapSource)으로 반환. airspace 우회용.</summary>
    public async Task<System.Windows.Media.Imaging.BitmapSource?> CaptureSnapshotAsync()
    {
        var png = await CapturePngAsync();
        return png == null ? null : BitmapFromPng(png);
    }

    /// <summary>PNG 바이트 → 동결(freeze)된 BitmapSource. UI 스레드 밖에서도 안전하게 재사용.</summary>
    public static System.Windows.Media.Imaging.BitmapSource? BitmapFromPng(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
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
        // clear 색도 새 테마 배경으로 갱신 — 이후 리사이즈/unpark clear 프레임이 바뀐 배경과 일치.
        if (_webView != null) { try { _webView.DefaultBackgroundColor = TerminalBgColor(); } catch { } }
        if (!_pageReady) return; // pageReady 시 OnPageReady 가 init 으로 보내줌
        var scheme = TerminalSessionManager.Instance.Config.Scheme;
        PostJson(new { type = "theme", theme = scheme, accent = CurrentAccentHex() });
    }

    /// <summary>현재 터미널 테마 배경색(#RRGGBB) → 불투명 Color. WebView2 DefaultBackgroundColor(렌더 전·
    /// 리사이즈 clear 색)를 실제 배경과 맞춰 clear 프레임이 안 튀게 한다. 파싱 실패 시 #0C0C0C 폴백.</summary>
    private static System.Drawing.Color TerminalBgColor()
    {
        try
        {
            var hex = TerminalSessionManager.Instance.Config.Scheme.Background;
            if (!string.IsNullOrWhiteSpace(hex))
            {
                var c = System.Drawing.ColorTranslator.FromHtml(hex);
                return System.Drawing.Color.FromArgb(0xFF, c.R, c.G, c.B);
            }
        }
        catch { }
        return System.Drawing.Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C);
    }

    /// <summary>현재 테마의 PrimaryColor 를 #RRGGBB 로. 로딩 스피너 색(devez 스타일)에 사용.</summary>
    private static string CurrentAccentHex()
    {
        if (System.Windows.Application.Current?.TryFindResource("PrimaryColor") is System.Windows.Media.Color c)
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        return "#2563eb";
    }

    /// <summary>세션 로딩 스피너(웹 레이어) 표시/숨김. WebView2 는 HwndHost 라 WPF 오버레이로는
    /// 터미널을 못 덮으므로 스피너를 웹 안에서 띄운다(터미널 위에 항상 보임).
    /// expectW/H: 셸이 확정한 최종 레이아웃 크기(DIP=CSS px). JS 가 스피너 카드를 그 중앙 px 에
    /// 앵커해, HWND 리사이즈 지연으로 뷰포트가 stale 인 동안에도 스피너가 옆/아래로 튀지 않는다.
    /// label: 스피너 아래 문구 — 생략 시 기본("세션 여는 중…"). 종료 대기 등 다른 문구가 필요할 때 지정.</summary>
    public void SetLoading(bool on, double expectW = 0, double expectH = 0, string? label = null)
    {
        // 콜드스타트: pageReady 전이면 보류했다가 OnPageReady 에서 flush (web 스피너 유실 방지)
        if (!_pageReady) { _pendingLoading = on ? (expectW, expectH, label) : null; return; }
        PostJson(new { type = "loading", on, expectW, expectH, label });
    }

    /// <summary>방 삭제 시 호출 — 방별 배선·준비 상태와 JS 쪽 xterm 인스턴스를 정리.
    /// ConPTY 셸 세션은 TerminalSessionManager.DisposeRoom 이 별도로 정리한다.</summary>
    public void CloseTerminal(string roomId)
    {
        DevezCode.Services.DiagLog.Write($"CloseTerminal room={roomId} (ready state dropped)");
        DetachSessionHandlers(roomId); // OutputReceived/Exited 핸들러 detach (누적 중복 post 방지)
        _wired.Remove(roomId);
        _ready.Remove(roomId);
        _readyScan.Remove(roomId);
        _readyNotified.Remove(roomId);
        _outputGenerations.Remove(roomId);
        _altSeenTick.Remove(roomId);
        _inlineFirstOutTick.Remove(roomId);
        if (_settleTimers.Remove(roomId, out var st)) st.Stop();
        if (_fullscreenFallbackTimers.Remove(roomId, out var ft)) ft.Stop();
        _pendingPreload.Remove(roomId);
        _grokCsiTails.Remove(roomId);
        lock (_outLock) { _outPending.Remove(roomId); _outScheduled.Remove(roomId); }
        if (_activeRoomId == roomId) _activeRoomId = null;
        if (_pendingShowRoomId == roomId) _pendingShowRoomId = null;
        PostJson(new { type = "dispose", roomId }); // JS xterm 인스턴스·DOM 해제
    }

    /// <summary>이 방에 배선돼 있던 OutputReceived/Exited 핸들러를 세션에서 detach.
    /// 세션 자체(ConPTY)는 건드리지 않는다 — 이 TerminalHostView 의 구독만 끊는다.</summary>
    private void DetachSessionHandlers(string roomId)
    {
        _grokCsiTails.Remove(roomId);
        if (!_sessionHandlers.Remove(roomId, out var h)) return;
        h.Session.OutputReceived -= h.OnOutput;
        h.Session.Exited -= h.OnExited;
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
        try { TerminalSessionManager.RoomReentering -= OnRoomReentering; } catch { }
        try
        {
            if (_webView?.CoreWebView2 != null)
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
        catch (Exception) { }
        try { _webView?.Dispose(); } catch (Exception) { }
        _webView = null;
        // 모든 세션 핸들러 detach — 이 뷰가 죽어도 세션은 살아있을 수 있으므로(분할 등) 반드시 구독 해제.
        foreach (var h in _sessionHandlers.Values)
        {
            try { h.Session.OutputReceived -= h.OnOutput; h.Session.Exited -= h.OnExited; } catch { }
        }
        _sessionHandlers.Clear();
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
