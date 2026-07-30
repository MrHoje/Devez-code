using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DevezCode.Models;
using DevezCode.Services;
using DevezCode.Services.Terminal;
using DevezCode.Views;

namespace DevezCode;

/// <summary>메타바 model/effort 콤보 항목. ToString=Label 이라 ContentPresenter 가 라벨을 그린다. SelectedValuePath=Value.</summary>
public sealed record ModelEffortOption(string Label, string Value)
{
    public override string ToString() => Label;
}

public partial class MainWindow : Window
{
    public static readonly DependencyProperty ShowFullPromptProperty =
        DependencyProperty.Register(nameof(ShowFullPrompt), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool ShowFullPrompt
{
        get => (bool)GetValue(ShowFullPromptProperty);
        set => SetValue(ShowFullPromptProperty, value);
    }

    private readonly ObservableCollection<ProjectItem> _projects;
    // 보관함 프로젝트(archived_at 있음) — 활성 목록과 분리 관리. WorkspaceStore 가 함께 영속.
    private readonly ObservableCollection<ProjectItem> _archivedProjects;
    private readonly ObservableCollection<SessionCompletionRecord> _sessionDoneRecords = new();
    // 응답 대기(선택지) 중인 세션 카드 — 완료기록 위에 항상 표시. IsWaitingChoice 변화에 맞춰 동기화.
    private readonly ObservableCollection<SessionItem> _waitingSessions = new();
    // 중앙 워크스페이스 패널들(분할 시 2개). _focusedPane = 사이드바/파일탐색기/단축키가 향하는 패널.
    private readonly List<WorkspacePaneView> _panes = new();
    private WorkspacePaneView _focusedPane = null!;   // 생성자에서 PaneA 로 초기화
    // 테마 적용 중 추가 변경이 들어오면 현재 종료/복원을 겹쳐 실행하지 않는다. 실행 완료 시점에는
    // App.CurrentTheme의 최신 값으로 세션을 시작하므로 중간 요청은 자연스럽게 합쳐진다.
    private bool _themeReloadRunning;

    // 좌/우 위치 교환은 콘텐츠 이동 없이 패널의 물리 컬럼만 맞바꿔 표현한다(터미널 재부착=세션 재로딩 방지).
    // _panesSwapped=false → PaneA 가 좌(col0)/PaneB 가 우(col2), true → 반대. 비분할 시엔 항상 false 로 정규화.
    private bool _panesSwapped;
    private WorkspacePaneView LeftPane  => _panesSwapped ? PaneB : PaneA;
    private WorkspacePaneView RightPane => _panesSwapped ? PaneA : PaneB;
    private string? _explorerDir;                      // 우측 파일탐색기가 보고 있는 경로(중복 ShowDirectory 방지)
    private readonly PerfMonitorService _perfMonitor = new();
    private readonly ThermalMonitorService _thermalMonitor = new();
    // 계정 사용량: OAuth API 를 기준값으로 사용. statusLine 훅은 API 값이 오래됐을 때만 폴백.
    // 장기 실행 Claude daemon 은 로그인 전환 뒤에도 이전 계정의 rate_limits 를 계속 내보낼 수 있어
    // 여러 세션의 훅 값을 섞으면 주간 사용량이 82%/15%처럼 왕복한다.
    private readonly StatusLineService _statusLine = new();
    private readonly UsageApiService _usageApi = new();
    private static readonly TimeSpan ClaudeApiFreshness = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProviderUsageFreshness = TimeSpan.FromMinutes(10);
    private Models.RateLimitSnapshot? _rlHook;   // statusLine 훅 최신(폴백 전용)
    private Models.RateLimitSnapshot? _rlApi;    // OAuth API 최신(기준값)
    private Models.RateLimitSnapshot? _rlMerged; // 실제 푸터 표시값
    private readonly System.Windows.Threading.DispatcherTimer _claudeFreshnessTimer = new()
    {
        Interval = TimeSpan.FromSeconds(30),
    };
    private bool _claudeUsageStale;
    // 추가 provider 사용량(푸터): codex(openai) + opencode-go.
    private readonly CodexUsageService _codex = new();
    private readonly KimiUsageService _kimi = new();
    private readonly OpenCodeGoUsageService _openCodeGo = new();
    private readonly DeepSeekUsageService _deepSeek = new();
    private readonly GrokUsageService _grok = new();
    private readonly AntigravityUsageService _antigravityUsage = new();
    // 사용량 팝오버(우측 사이드바)용 최신 스냅샷 보관 — 데이터 있는 provider 만 카드로 노출.
    private Models.ProviderUsage? _lastCodex;
    private Models.ProviderUsage? _lastKimi;
    private Models.ProviderUsage? _lastGo;
    private Models.ProviderUsage? _lastDeepSeek;
    private Models.ProviderUsage? _lastGrok;
    private Models.ProviderUsage? _lastAntigravity;
    // 초기화권 소비 in-flight 락 — 확인~POST 완료까지 재클릭/재진입 차단.
    private bool _resetConsumeInFlight;
    private readonly SessionBusyService _sessionBusy = new();
    // 세션 간 지시 릴레이(/devez-relay:send-to) 수신부 — commands\<uuid>.json 감시 → 대상 세션 터미널에 주입.
    private readonly SessionCommandInboxService _sessionCommandInbox = new();
    // claude statusLine 훅이 떨군 방별 실제 model/effort 를 감시해 메타바 콤보에 라이브 연동.
    private readonly ModelEffortService _modelEffort = new();
    private readonly SessionLastMessageService _sessionLastMsg = new();
    // codex — Claude 와 동일하게 ~/.codex/hooks.json 으로 lastmsg/busy/session_id 추적.
    private readonly CodexHookService _codexHook = new();
    // grok — ~/.grok/hooks + 방별 상태 파일로 lastmsg/busy/session_id 추적 (codex 패턴).
    private readonly GrokHookService _grokHook = new();
    private readonly KimiHookService _kimiHook = new();
    private readonly DevezVibeStateService _devezVibeState = new();
    // antigravity(agy) — hooks.json 훅(busy/waiting/conversation_id) + transcript_full.jsonl 폴링
    // (빠른 idle 확정 + lastmsg). ask_question/ask_permission 은 waiting(❗)으로 분리한다.
    private readonly AntigravityHookService _antigravityHook = new();
    // 비-Claude 비-codex (opencode/gjc) 의 last prompt 추적. codex 는 위 훅 서비스가 처리.
    private readonly AgentLastMessageService _agentLastMsg = new();
    // opencode — 플러그인이 lastmsg\<room>.txt 에 저장한 user prompt 를 FileSystemWatcher 로 즉시 반영 (claude 와 동일 패턴).
    private readonly OpenCodeLastMessageService _opencodeLastMsg = new();
    // opencode — 플러그인이 busy\<room>.txt 에 저장한 처리중 상태를 감시해 스피너 연동 (claude busy hook 과 동일 패턴).
    private readonly OpenCodeBusyService _opencodeBusy = new();
    // gjc(가재코드) — 방별 세션 .jsonl을 증분 폴링해 lastmsg/busy/waiting 추적.
    private readonly GajaeLastMessageService _gajaeLastMsg = new();
    private readonly WakeSchedulerService _wakeScheduler;
    private readonly HashSet<string> _wakeRoomIds = new(StringComparer.Ordinal);
    // 화면에 표시하지 않은 세션만 대상으로 하는 보수적 유휴 종료기. 기본 설정 0=꺼짐.
    private readonly System.Windows.Threading.DispatcherTimer _idleSessionShutdownTimer = new()
    {
        Interval = TimeSpan.FromSeconds(30),
    };
    private readonly System.Windows.Threading.DispatcherTimer _externalSessionTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(150),
    };
    private readonly Dictionary<string, DateTime> _sessionLastActivityUtc = new(StringComparer.Ordinal);
    private int _idleSessionShutdownMinutes;
    private bool _idleSessionShutdownChecking;
    private bool _externalSessionChecking;
    private readonly HashSet<string> _externalSessionLaunches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _rejectedAgentEventLogs = new(StringComparer.Ordinal);

    static MainWindow()
    {
        // 모든 버튼류(Button/ToggleButton/RadioButton 등 ButtonBase)가 키보드 포커스를
        // 절대 가지지 못하게 한다. 버튼을 마우스로 클릭하면 포커스가 그대로 남아
        // 이후 Enter/Space 를 누를 때마다 같은 버튼이 계속 클릭되는 문제 방지.
        // (상단바/탭/하단 설정/클리너 등 모든 버튼 일괄. 마우스 클릭과 IsDefault/IsCancel 동작에는 영향 없음.)
        var buttonBase = typeof(System.Windows.Controls.Primitives.ButtonBase);
        // ① 포커스가 들어오려는 순간 취소 → 어떤 경로로도 키보드 포커스를 못 받음.
        EventManager.RegisterClassHandler(buttonBase,
            System.Windows.Input.Keyboard.PreviewGotKeyboardFocusEvent,
            new System.Windows.Input.KeyboardFocusChangedEventHandler((s, e) => e.Handled = true));
        // ② 보조: Focusable 자체도 끔(Tab 이동 대상에서도 제외).
        EventManager.RegisterClassHandler(buttonBase,
            System.Windows.FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, e) =>
            {
                if (s is System.Windows.Controls.Primitives.ButtonBase b) b.Focusable = false;
            }));
    }

    public MainWindow()
    {
        InitializeComponent();
        _wakeScheduler = new WakeSchedulerService(DispatchWakeAsync);

        // 하단 터미널 패널: 셸이 exit 로 끝나면 패널을 닫고 방을 정리 — 다음 토글에 새 pwsh.
        ShellTerminal.SessionExited += id =>
        {
            if (!string.Equals(id, ShellRoomId, StringComparison.Ordinal)) return;
            Dispatcher.BeginInvoke(async () =>
            {
                // 앱 종료 흐름(OnWindowClosing 의 DisposeRoom)이 유발한 Exited 는 무시 —
                // 여기서 패널을 닫으면 종료 스냅샷 도중 레이아웃이 출렁인다.
                if (_shuttingDown) return;
                ShellTerminal.CloseTerminal(ShellRoomId);
                TerminalSessionManager.Instance.DisposeRoom(ShellRoomId, purgeTracking: false);
                TerminalSessionManager.Instance.ClearDisposedRoom(ShellRoomId); // tombstone 해제 → 재생성 허용
                if (_shellPanelOpen && !_shellPanelBusy)
                {
                    _shellPanelBusy = true;
                    try { await ToggleShellPanelAsync(false); }
                    catch (Exception ex) { DiagLog.Write($"shell panel close-on-exit failed: {ex.Message}"); }
                    finally { _shellPanelBusy = false; }
                }
            });
        };
        _idleSessionShutdownTimer.Tick += async (_, _) => await CheckIdleSessionsAsync();
        _externalSessionTimer.Tick += (_, _) => CheckExternalSessions();
        CodexFooterIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.CodexIconUri));
        KimiFooterIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.KimiIconUri)); // 테마별(시작 시점 테마 반영)
        RestoreWindowPlacement();   // 마지막 창 위치/크기/최대화 복원 (없으면 CenterScreen 유지)
        SessionHistoryList.ItemsSource = _sessionDoneRecords;
        WaitingList.ItemsSource = _waitingSessions;
        // 영속된 완료 기록 복원 (설정에 저장된 최신순 목록).
        var saved = SettingsService.LoadSessionHistoryRecords();
        if (saved != null && saved.Count > 0)
        {
            foreach (var r in saved)
            {
                if (string.Equals(r.AgentId, "grok", StringComparison.OrdinalIgnoreCase))
                    r.LastMessage = GrokHookService.NormalizeLastMessage(r.LastMessage);
                _sessionDoneRecords.Add(r);
            }
        }
        UpdateSessionHistoryEmpty();
        // 세션 완료 기록 "전체보기"/"한줄만 보기" 상태 복원 (재시작 유지).
        ShowFullPrompt = SettingsService.LoadShowFullPrompt();

        _projects = WorkspaceStore.Load(out var archived);
        _archivedProjects = archived;
        ReconcileExternalSessions();
        UpdateSessionBusyDisplay();
        // 워크스페이스에 더 이상 없는(활성+보관 통틀어) roomId 의 claude 추적/캐시 파일 정리(3일 유예, GC).
        // 실제 대화 기록(.jsonl)은 안 건드림 — 앱 자체 북키핑 파일만.
        // WorkspaceStore 가 손상 복구로 빈 트리를 반환한 경우는 건너뛴다 — 그 상태에서 돌리면
        // 실제로 살아있는 방 전부가 유령으로 오판돼(활성+보관이 통째로 비어 보이므로) 며칠 뒤 다 지워진다.
        if (!WorkspaceStore.LastLoadDegraded)
        {
            TerminalSessionManager.ReconcileGhostRoomTracking(
                _projects.Concat(_archivedProjects)
                    .SelectMany(p => p.Tabs)
                    .OfType<SessionItem>()
                    .Select(s => s.Id));
            // 방별 폰트 고정 이전에 만든 세션들을 현재 전역 기본값으로 1회 고정한다(override 없는 것만).
            // 손상 로드 시엔 세션 트리가 비어 보일 수 있어 건너뛴다(다음 정상 로드에서 수행).
            SettingsService.MigrateExistingRoomFontSizesOnce(
                _projects.Concat(_archivedProjects)
                    .SelectMany(p => p.Tabs)
                    .OfType<SessionItem>()
                    .Select(s => s.Id),
                TerminalSessionManager.Instance.DefaultFontSizePt);
        }
        Sidebar.Projects = _projects;
        Sidebar.ArchivedProjects = _archivedProjects;
        SetupPane(PaneA);
        SetupPane(PaneB);   // 분할 전엔 숨김(XAML Collapsed). 분할 시 노출.
        // SCM 패널 → 포커스 패널에 diff 탭 열기 / git 상태 변경 시 브랜치 버블 갱신.
        FileExplorer.DiffFileActivated += (repo, rel, staged) =>
        {
            var proj = _focusedPane.ActiveProject;
            if (proj != null) _focusedPane.OpenDiffTab(proj, repo, rel, staged);
        };
        FileExplorer.GitStateChanged += repo =>
        {
            foreach (var pane in _panes) pane.RefreshBranchIfRepo(repo);
        };
        PaneB.IsRightPane = true;   // 분할 시 우측 패널 — 탭바 버튼이 X(분할 닫기)로 표시됨.
        CenterSplit.SizeChanged += (_, _) => UpdatePaneFocusVisual(animate: false);
        _focusedPane = PaneA;
        _ = MarkdownWysiwygHost.PrewarmAsync();

        Sidebar.AddProjectRequested    += AddProject;
        Sidebar.ProjectSelected        += SelectProject;
        Sidebar.AddSessionRequested    += AddSession;
        Sidebar.ProjectDeleteRequested += DeleteProject;
        Sidebar.ProjectRenameRequested += RenameProject;
        Sidebar.ProjectArchiveRequested += ArchiveProject;
        Sidebar.ProjectUnarchiveRequested += UnarchiveProject;
        Sidebar.GitRemoteOpenRequested += OpenGitRemote;
        Sidebar.AddProjectFileRequested += AddProjectFile;
        Sidebar.ProjectFileSelected    += OpenProjectFile;
        Sidebar.ProjectFileRemoveRequested += RemoveProjectFile;
        Sidebar.ProjectFileRenameRequested += RenameProjectFile;
        Sidebar.ProjectsReordered += () => WorkspaceStore.Save(_projects);
        Sidebar.ProjectExpandChanged += () => WorkspaceStore.Save(_projects);
        Sidebar.HiddenSessionVisibilityChanged += () => WorkspaceStore.Save(_projects);
        Sidebar.SessionsReordered += OnSidebarSessionsReordered;
        Sidebar.FilesReordered += _ => { RefreshCardGroups(); WorkspaceStore.Save(_projects); };
        Sidebar.SessionSelected        += OpenSessionFromSidebar;
        Sidebar.OpenDocSelected        += OpenDocFromSidebar;
        Sidebar.OpenDocCloseRequested  += CloseDocFromSidebar;
        Sidebar.SplitMovePresentationProvider = GetSidebarSplitMovePresentation;
        Sidebar.SplitMoveRequested += MoveSidebarTabAcrossSplit;
        Sidebar.BrowserTabSelected     += OpenBrowserFromSidebar;
        Sidebar.BrowserTabCloseRequested += CloseBrowserFromSidebar;
        Sidebar.BrowserTabRenameRequested += RenameBrowserFromSidebar;
        Sidebar.SessionDeleteRequested += DeleteSession;
        Sidebar.SessionRenameRequested += RenameSession;
        Sidebar.SessionStopTrackingRequested += StopTrackingSession;
        Sidebar.SessionHideRequested += HideSessionFromSidebar;
        Sidebar.SessionForkRequested += ForkSession;
        Sidebar.SessionExternalRequested += OpenSessionInExternalTerminal;
        Sidebar.SessionExportRequested += ExportSession;
        Sidebar.SessionLockRequested += ToggleSessionLock;
        Sidebar.SessionsDeleteRequested += DeleteSessions;
        Sidebar.SessionsStopTrackingRequested += StopTrackingSessions;
        Sidebar.SessionsHideRequested += HideSessionsFromSidebar;
        Sidebar.SessionsLockRequested += SetSessionsLocked;
        Sidebar.SessionManagerRequested += OpenSessionManager;
        Sidebar.UpdateClicked += OpenUpdatePopup; // 좌측 하단 업데이트 버튼 → 노트 팝업 → 설치

        // 세션 요청 처리중 스피너: claude 훅(busy-hook.ps1)이 떨군 상태 파일을 감시 (clude-blinker 방식).
        _sessionBusy.BusyChanged += (id, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(id, "claude", "busy");
                if (s == null) return;
                MarkSessionActivity(id);
                bool was = s?.IsBusy ?? false;
                if (s != null)
                {
                    s.IsBusy = busy;
                    // 턴 종료(busy=false) 보강 해제. 단 waiting 파일이 아직 permission/input 이면
                    // 권한·선택지 대기 중(busy 파일이 잠깐 idle 이거나 hard-need 알림)이므로 ❗ 유지.
                    // wait idle FSW 누락으로 ❗ 고착되는 경우는 SessionBusyService reconcile 이 수렴.
                    if (!busy && !IsClaudeWaitingFileActive(id)) s.IsWaitingChoice = false;
                }
                NotifyIfSessionFinished(s, was, busy, () => _sessionBusy.IsRoomActive(id),
                    () => SessionBusyService.ConsumeTurnEndMarker(id));
                UpdateSessionBusyDisplay();
                if (!busy) foreach (var pane in _panes) pane.FlushPendingModelEffort(id);
            });

        // 턴종료 마커 보조 경로: main 플래그가 유실된 방은 서브 드레인으로 busy 가 이미 idle 이라
        // 진짜 Stop 때 busy 전이가 없어 카드가 영영 안 나온다 → 마커 FSW 로 직접 발행을 보장.
        // 일반 방(busy=running 중 Stop)은 여기서 스킵되고 busy→idle 디바운스 경로가 마커를 소비한다.
        _sessionBusy.TurnEndMarker += id =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(id, "claude", "turn-end");
                if (s == null || s.IsBusy) return;           // busy 전이 경로가 처리(마커는 그쪽에서 소비)
                if (_sessionBusy.IsRoomActive(id)) return;   // 아직 활성 → 드레인 완료 idle 에서 소비
                if (!SessionBusyService.ConsumeTurnEndMarker(id)) return; // 이미 소비됨(중복 FSW/디바운스)
                EmitSessionFinished(s);                      // 마커=Stop 훅 실발화 → 정착 디바운스 불필요
            });

        _sessionBusy.WaitingChoiceChanged += (id, waiting) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(id, "claude", "waiting");
                if (s == null) return;
                MarkSessionActivity(id);
                bool wasWaiting = s?.IsWaitingChoice ?? false;
                if (s != null) s.IsWaitingChoice = waiting;
                NotifyIfSessionWaiting(s, wasWaiting, waiting);
                UpdateSessionBusyDisplay();
            });

        // statusLine 훅이 떨군 방별 실제 model/effort → 해당 세션을 보여주는 패널 콤보를 라이브 갱신.
        _modelEffort.Changed += (roomId, modelId, effortLevel) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (FindOwnedSession(roomId, "claude", "model-effort") == null) return;
                foreach (var pane in _panes) pane.NotifyModelEffortChanged(roomId);
            });

        // 마지막 보낸 메시지: busy 훅이 떨군 lastmsg 파일을 감시 → 세션에 반영(헤더 부제 라이브 갱신).
        _sessionLastMsg.MessageChanged += (id, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(id, "claude", "message");
                if (s == null) return;
                if (!ApplyHeaderMessage(s, msg)) return;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });

        // 비-Claude 비-codex 에이전트(opencode/gjc) — (workingDir, lastPrompt) 이벤트로 같은 디렉터리 세션 모두 갱신.
        _agentLastMsg.LastPromptChanged += (workingDir, sourceAgentId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var norm = System.IO.Path.GetFullPath(workingDir).TrimEnd('\\', '/');
                foreach (var p in _projects)
                {
                    var pNorm = System.IO.Path.GetFullPath(p.Path).TrimEnd('\\', '/');
                    if (!string.Equals(pNorm, norm, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var s in p.Tabs.OfType<SessionItem>())
                    {
                        if (!AgentEventOwnership.IsMatch(s.AgentId, sourceAgentId)) continue;
                        if (!ApplyHeaderMessage(s, msg)) continue;
                        foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
                    }
                }
            });

        // opencode — 플러그인이 떨군 lastmsg 파일을 즉시 반영(claude 와 동일 패턴, 3초 폴링 대기 X).
        _opencodeLastMsg.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "opencode", "message");
                if (s == null) return;
                if (!ApplyHeaderMessage(s, msg)) return;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });

        // gjc — 훅+세션 .jsonl 하이브리드 결과를 roomId 키로 즉시 반영 (opencode lastmsg 와 동일 처리).
        _gajaeLastMsg.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "gajae", "message");
                if (s == null) return;
                if (!ApplyHeaderMessage(s, msg)) return;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });

        // opencode — 플러그인이 떨군 busy 파일 감시 → 스피너 (claude 와 동일).
        // oh-my-openagent child 대기 중 parent idle 이 잠깐 튀면 플러그인이 다시 running 으로
        // 되돌리므로, 완료 확정은 정착 창 + IsRoomBusy 재확인을 거친다 (claude/gjc 와 동일).
        _opencodeBusy.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "opencode", "busy");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool was = s?.IsBusy ?? false;
                if (s != null)
                {
                    s.IsBusy = busy;
                    if (!busy) s.IsWaitingChoice = false; // 턴 종료 → ❗ 보강 해제(완료까지 박힘 방지)
                }
                NotifyIfSessionFinished(s, was, busy, () => _opencodeBusy.IsRoomBusy(roomId));
                UpdateSessionBusyDisplay();
                if (!busy) foreach (var pane in _panes) pane.FlushPendingModelEffort(roomId);
            });

        // opencode — 플러그인 question.asked → waiting 파일 감시 → 선택지 응답 대기 ❗.
        _opencodeBusy.WaitingChoiceChanged += (roomId, waiting) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "opencode", "waiting");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool wasWaiting = s?.IsWaitingChoice ?? false;
                if (s != null) s.IsWaitingChoice = waiting;
                NotifyIfSessionWaiting(s, wasWaiting, waiting);
                UpdateSessionBusyDisplay();
            });

        // 가재코드 — gjc 런타임 사이드카(1차) + JSONL 증분 폴링(폴백) 병합 busy 판정 → 스피너.
        // 완료 발행은 idle 의 근거에 따라 두 갈래:
        //  • 사이드카 agent_end(state=completed) 확정 idle → 디바운스 없이 즉시 발행.
        //    골 체이닝/새 프롬프트가 settle(1.2s) 안에 running 을 다시 써도 직전 완료 카드를
        //    삼키지 않는다(골마다 카드 1장 스펙). agent_end 는 프롬프트당 1회라 플랩이 아니다.
        //  • 그 외(jsonl 폴백·stale 폴백) idle → 정착 디바운스 + IsRoomBusy(강제 재평가) 재확인.
        //    auto-retry/컴팩션 재시작이 settle 안에 재무장하면 가짜 완료 카드를 억제.
        _gajaeLastMsg.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "gajae", "busy");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool was = s?.IsBusy ?? false;
                if (s != null)
                {
                    s.IsBusy = busy;
                    if (!busy) s.IsWaitingChoice = false; // 턴 종료 → ❗ 보강 해제(완료까지 박힘 방지)
                }
                Func<bool>? still = !busy && _gajaeLastMsg.IsIdleAuthoritative(roomId)
                    ? null // 확정 완료 — 즉시 발행 (정착 창의 재무장 취소 대상이 아님)
                    : () => _gajaeLastMsg.IsRoomBusy(roomId);
                NotifyIfSessionFinished(s, was, busy, still);
                UpdateSessionBusyDisplay();
            });

        // 가재코드 — goal 모드 골 단위 완료 백필(사이드카 미가동 방 한정, transcript 파싱).
        // 사이드카가 가동 중인 방은 골마다 agent_end → busy→idle 완료 카드가 이미 찍히므로 중복되지 않는다.
        _gajaeLastMsg.GoalCompleted += (roomId, objective, completedAt) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "gajae", "goal-completed");
                if (s == null) return;
                AddGoalCompletionRecord(s, objective, completedAt);
            });

        // 가재코드 — 'ask' 선택지 응답 대기(❗). gjc 는 스피너 라인이 출력 버퍼를 도배해 화면 폴링이 불가하므로
        // 훅 tool_call/result를 우선하고 jsonl의 'ask' 툴콜로 재검증한다(busy와 동일 경로).
        _gajaeLastMsg.WaitingChoiceChanged += (roomId, waiting) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "gajae", "waiting");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool wasWaiting = s?.IsWaitingChoice ?? false;
                if (s != null) s.IsWaitingChoice = waiting;
                NotifyIfSessionWaiting(s, wasWaiting, waiting);
                UpdateSessionBusyDisplay();
            });

        // codex — Claude 와 동일하게 roomId 키로 즉시 갱신 (폴링 X).

        _codexHook.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "codex", "message");
                if (s == null) return;
                s.LastMessage = msg;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });
        // turn_id fence 로 대부분 안정. 조기 idle 플랩 시 active 마커/툴 재무장 재확인.
        _codexHook.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "codex", "busy");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool was = s?.IsBusy ?? false;
                if (s != null)
                {
                    s.IsBusy = busy;
                    if (!busy) s.IsWaitingChoice = false;
                }
                NotifyIfSessionFinished(s, was, busy, () => _codexHook.IsRoomBusy(roomId));
                UpdateSessionBusyDisplay();
                if (!busy) foreach (var pane in _panes) pane.NotifyModelEffortChanged(roomId);
            });
        _codexHook.WaitingChoiceChanged += (roomId, waiting) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "codex", "waiting");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool wasWaiting = s?.IsWaitingChoice ?? false;
                if (s != null) s.IsWaitingChoice = waiting;
                // Codex 는 자동 승인으로 즉시 해소되는 PermissionRequest 도 내보낼 수 있다.
                // ❗ 표시는 즉시 반영하되 OS 알림만 잠깐 유예해 불필요한 깜빡임을 막는다.
                NotifyIfSessionWaiting(s, wasWaiting, waiting, notificationDelayMs: 1500);
                UpdateSessionBusyDisplay();
            });
        _codexHook.CodexSessionChanged += (roomId, sid) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (FindOwnedSession(roomId, "codex", "session") == null) return;
                SettingsService.SaveCodexRoomSession(roomId, sid);
                foreach (var pane in _panes) pane.NotifyModelEffortChanged(roomId);
            });

        // Grok — codex 와 동일 roomId 키 즉시 갱신.
        _grokHook.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "grok", "message");
                if (s == null) return;
                s.LastMessage = msg;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });
        // 조기 Stop/Notification idle 과 events 폴러 재무장이 경합하므로 정착 창 + 파일 재확인.
        _grokHook.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "grok", "busy");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool was = s?.IsBusy ?? false;
                if (s != null) s.IsBusy = busy;
                if (!busy && s != null) s.IsWaitingChoice = false;
                NotifyIfSessionFinished(s, was, busy, () => _grokHook.IsRoomBusy(roomId));
                UpdateSessionBusyDisplay();
                if (!busy) foreach (var pane in _panes) pane.NotifyModelEffortChanged(roomId);
            });
        _grokHook.WaitingChoiceChanged += (roomId, waiting) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "grok", "waiting");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool wasWaiting = s?.IsWaitingChoice ?? false;
                if (s != null) s.IsWaitingChoice = waiting;
                NotifyIfSessionWaiting(s, wasWaiting, waiting);
                UpdateSessionBusyDisplay();
            });
        _grokHook.GrokSessionChanged += (roomId, sid) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (FindOwnedSession(roomId, "grok", "session") == null) return;
                var workingDir = SettingsService.LoadClaudeCodeRoomDir(roomId);
                if (!GrokHookService.IsRootTrackedSession(roomId, sid)
                    || TerminalSessionManager.FindGrokChatHistoryPathForWorkingDirectory(sid, workingDir) == null)
                    return;
                SettingsService.SaveGrokRoomSession(roomId, sid);
                foreach (var pane in _panes) pane.NotifyModelEffortChanged(roomId);
            });

        // Kimi(kimi-code) — codex 와 동일 roomId 키 즉시 갱신 (훅 config.toml [[hooks]]).
        _kimiHook.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "kimi", "message");
                if (s == null) return;
                s.LastMessage = msg;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });
        // Pre/PostToolUse working 재무장이 정착 창 안에 오면 가짜 완료 카드 억제.
        _kimiHook.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "kimi", "busy");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool was = s?.IsBusy ?? false;
                if (s != null)
                {
                    s.IsBusy = busy;
                    if (!busy) s.IsWaitingChoice = false;
                }
                NotifyIfSessionFinished(s, was, busy, () => _kimiHook.IsRoomBusy(roomId));
                UpdateSessionBusyDisplay();
                if (!busy) foreach (var pane in _panes) pane.NotifyModelEffortChanged(roomId);
            });
        _kimiHook.WaitingChoiceChanged += (roomId, waiting) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "kimi", "waiting");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool wasWaiting = s?.IsWaitingChoice ?? false;
                if (s != null) s.IsWaitingChoice = waiting;
                NotifyIfSessionWaiting(s, wasWaiting, waiting, notificationDelayMs: 1500);
                UpdateSessionBusyDisplay();
            });
        _kimiHook.KimiSessionChanged += (roomId, sid) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (FindOwnedSession(roomId, "kimi", "session") == null) return;
                SettingsService.SaveKimiRoomSession(roomId, sid);
                foreach (var pane in _panes) pane.NotifyModelEffortChanged(roomId);
            });

        // Devez Vibe(dvz) — 훅 없이 CLI 자신이 상태 파일을 쓴다(kimi 와 같은 파일 규약).
        _devezVibeState.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "devezvibe", "message");
                if (s == null) return;
                s.LastMessage = msg;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });
        _devezVibeState.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "devezvibe", "busy");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool was = s?.IsBusy ?? false;
                if (s != null)
                {
                    s.IsBusy = busy;
                    if (!busy) s.IsWaitingChoice = false;
                }
                NotifyIfSessionFinished(s, was, busy, () => _devezVibeState.IsRoomBusy(roomId));
                UpdateSessionBusyDisplay();
                if (!busy) foreach (var pane in _panes) pane.NotifyModelEffortChanged(roomId);
            });
        _devezVibeState.WaitingChoiceChanged += (roomId, waiting) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "devezvibe", "waiting");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool wasWaiting = s?.IsWaitingChoice ?? false;
                if (s != null) s.IsWaitingChoice = waiting;
                NotifyIfSessionWaiting(s, wasWaiting, waiting, notificationDelayMs: 1500);
                UpdateSessionBusyDisplay();
            });
        _devezVibeState.SessionChanged += (roomId, sid) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (FindOwnedSession(roomId, "devezvibe", "session") == null) return;
                SettingsService.SaveDevezVibeRoomSession(roomId, sid);
                foreach (var pane in _panes) pane.NotifyModelEffortChanged(roomId);
            });

        // Antigravity(agy) — busy/선택지 대기 + conversation_id 라이브 저장 (grok 패턴).
        // lastmsg 는 transcript 폴링에서 추출.
        // transcript 폴러 재무장/completed 재확인 — 가짜 idle 완료 카드 억제.
        _antigravityHook.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "antigravity", "busy");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool was = s?.IsBusy ?? false;
                if (s != null)
                {
                    s.IsBusy = busy;
                    if (!busy) s.IsWaitingChoice = false;
                }
                NotifyIfSessionFinished(s, was, busy, () => _antigravityHook.IsRoomBusy(roomId));
                UpdateSessionBusyDisplay();
            });
        _antigravityHook.WaitingChoiceChanged += (roomId, waiting) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "antigravity", "waiting");
                if (s == null) return;
                MarkSessionActivity(roomId);
                bool wasWaiting = s?.IsWaitingChoice ?? false;
                if (s != null) s.IsWaitingChoice = waiting;
                NotifyIfSessionWaiting(s, wasWaiting, waiting);
                UpdateSessionBusyDisplay();
            });
        _antigravityHook.SessionChanged += (roomId, sid) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (FindOwnedSession(roomId, "antigravity", "session") == null) return;
                if (!AntigravityHookService.IsRootTrackedSession(roomId, sid)
                    || !TerminalSessionManager.AntigravityConversationExists(sid)) return;
                SettingsService.SaveAntigravityRoomSession(roomId, sid);
            });
        _antigravityHook.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindOwnedSession(roomId, "antigravity", "message");
                if (s == null) return;
                s.LastMessage = msg;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });

        // 테마 변경 시 좌·우 패널 토글 아이콘 brush 재계산(seam 은 각 패널이 자체 처리)
        App.ThemeChanged += OnThemeChanged_UpdatePanels;

        // 파일 탐색기에서 텍스트 파일 더블클릭 → 포커스 패널의 새 파일 탭으로 열기
        FileExplorer.FileOpenRequested += (_, path) => OpenFileFromExplorer(path);

        UpdateStatus();
        RestorePanelStates();
        RightOverlayPanel.RenderTransform = _rightT;   // 오버레이 슬라이드용

        // 실행 시 다른 앱(devez 등) 위로 확실히 올라오게 한다. topmost 토글로 전경 잠금 우회.
        Loaded += (_, _) =>
        {
            try
            {
                Activate();
                Topmost = true;
                Topmost = false;
            }
            catch { /* best effort */ }

            InitUpdates();
            StartPerfMonitor();
            // claude code 커스텀 statusline(~/.claude\statusline.js + settings.json statusLine) 보장.
            // 다른 PC 첫 실행 시 자동 설치되며, 이미 있으면 사용자 수정 보존(스킵).
            UserStatusLineInstaller.EnsureInstalled();
            StartStatusLine();
            _sessionBusy.Start();
            // /send-new: 인박스가 자식 세션 생성을 요청하면 UI 스레드에서 생성·시작한다.
            _sessionCommandInbox.ChildSessionRequested += (parentRoomId, sessionName, brief) =>
                Dispatcher.BeginInvoke(() => CreateChildAndDispatch(parentRoomId, sessionName, brief));
            _sessionCommandInbox.DormantSessionRequested += (roomId, message, submit, commandPath) =>
                Dispatcher.BeginInvoke(() => StartDormantSessionAndDispatch(roomId, message, submit, commandPath));
            _sessionCommandInbox.Start();  // 세션 간 지시 릴레이 수신 시작
            StartBusyDisplaySync();
            _modelEffort.Start();
            _sessionLastMsg.Start();
            // 전역 훅/플러그인을 매 시작마다 멱등 갱신한다. 일시적인 잠금·권한 실패나
            // 구버전 앱 재실행으로 설정이 되돌아가도 다음 시작에서 자동 복구한다.
            CodexHookInstaller.EnsureScriptInstalled();
            CodexHookInstaller.InstallHooksJson();
            _codexHook.Start();
            GrokHookInstaller.EnsureInstalled();
            _grokHook.Start();
            AntigravityHookInstaller.EnsureInstalled();
            _antigravityHook.Start();
            KimiHookInstaller.EnsureInstalled();
            _kimiHook.Start();
            _devezVibeState.Start();  // dvz 는 설치할 훅이 없다 — CLI 가 직접 기록한 파일만 감시
            OpenCodePluginInstaller.EnsureInstalled();
            _opencodeLastMsg.Start();
            _opencodeBusy.Start();
            // 가재코드 — gjc 런타임 사이드카(runtime-state.json) 1차 + 세션 .jsonl 폴링 폴백으로
            // 헤더/스피너/대기 처리. gjc 0.11 은 transcript 를 지연 flush 하므로(수 시간 실측)
            // 폴링만으로는 busy/완료가 그만큼 늦는다 — 사이드카가 이벤트 즉시 기록이라 이를 보정.
            _gajaeLastMsg.Start();
            _agentLastMsg.Start();
            RestoreOpenFiles();  // 직전에 열려 있던 파일 편집기 탭 복원(세션 활성화보다 먼저 → 활성 탭은 세션 유지)
            RestoreLastSession();
            WorkspaceStore.ExportSessionsIndex(_projects); // 세션 릴레이용 인덱스 시작 시 최신화
            ResetAllSessionBusy(); // 시작 시 모든 세션 IsBusy=false: 종료 전 진행 상태는 취소됨.
            RestoreSplitState(); // 직전 실행 시 분할 상태였으면 패널 B 복원
            _wakeScheduler.Start();
            _externalSessionTimer.Start();
            ApplyIdleSessionShutdownSettings();
            RefreshCardGroups(); // 시작 시에도 분할 설정 프로젝트 카드는 좌/우 파티션으로(영속 refs 기반)
            CheckHookSetup();
            ApplyFileExpMinWidth(); // 탭 버튼 4개 온전히 보이는 폭을 패널 최소 폭으로
            ApplySidePanelButtonVisibility();
            Dispatcher.BeginInvoke(() =>
            {
                PaneA.Terminal.PrewarmWebView();
                if (_splitActive && PaneB.IsVisible)
                    PaneB.Terminal.PrewarmWebView();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            // 전역 단축키: (기본)한자 + 방향키(위=좌, 아래=우) → 포커스 패널의 탭 이전/다음 이동(세션·파일·diff·브라우저 공통).
            // 우리 앱이 포그라운드가 아니어도(다른 앱/터미널 점유 중에도) 동작 — 전환 후 창을 앞으로.
            var (hkMod, hkPrev, hkNext) = SettingsService.LoadTabHotkey();
            GlobalTabHotkey.Configure(hkMod, hkPrev, hkNext);
            GlobalTabHotkey.Install(next =>
            {
                bool moved = _focusedPane?.CycleActiveSession(next) ?? false;
                // 포커스 패널 안에서 경계(맨 끝)라 못 옮겼으면, 분할 중일 때만 반대편 패널로 이동.
                // 좌측 마지막 탭에서 다음 → 우측 첫 탭 / 우측 첫 탭에서 이전 → 좌측 마지막 탭.
                // (그 반대 방향, 즉 우측 마지막에서 다음·좌측 첫 탭에서 이전은 더 갈 곳이 없어 정지.)
                if (!moved && _splitActive)
                {
                    var other = next && ReferenceEquals(_focusedPane, LeftPane) ? RightPane
                              : !next && ReferenceEquals(_focusedPane, RightPane) ? LeftPane
                              : null;
                    if (other != null)
                    {
                        _focusedPane = other;
                        other.SelectEdgeSession(first: next);
                        SyncShellToFocusedPane();
                        UpdatePaneFocusVisual();
                    }
                }
                BringToForegroundFromHotkey();
            });
        };

        // 창 위치/크기는 닫히기 직전(Closing)에 저장한다 — RestoreBounds 가 유효한 시점.
        Closing += OnWindowClosing;

        // + 이동/리사이즈/상태변경 후에도 디바운스 저장(크래시·강제 종료·로그오프로 Closing 을 못 타는 경우 대비).
        _placementSaveTimer.Tick += (_, _) => { _placementSaveTimer.Stop(); SaveWindowPlacement(); };
        LocationChanged += (_, _) => ScheduleWindowPlacementSave();
        SizeChanged     += (_, _) => ScheduleWindowPlacementSave();
        StateChanged    += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastNonMinState = WindowState;
            ScheduleWindowPlacementSave();
        };

        Closed += (_, _) =>
        {
            _idleSessionShutdownTimer.Stop();
            _externalSessionTimer.Stop();
            // 정상 종료: 분할 상태 + 마지막 활성 프로젝트/세션 기억 + 클린 종료 플래그 set
            if (_splitActive) PersistSplitState();
            SettingsService.SaveLastActive(_focusedPane.ActiveProject?.Path, _focusedPane.ActiveSession?.Id);
            SettingsService.SaveCleanShutdown(true);
            GlobalTabHotkey.Uninstall();
            App.ThemeChanged -= OnThemeChanged_UpdatePanels;
            foreach (var pane in _panes) pane.DisposeTerminal();
            foreach (var browser in _projects.Concat(_archivedProjects).SelectMany(p => p.Tabs).OfType<BrowserTabItem>())
                browser.Browser.DisposeAll();
            _perfMonitor.Dispose();
            _thermalMonitor.Dispose();
            _statusLine.Dispose();
            _claudeFreshnessTimer.Stop();
            _usageApi.Dispose();
            _codex.Dispose();
            _kimi.Dispose();
            _openCodeGo.Dispose();
            _deepSeek.Dispose();
            _grok.Dispose();
            _antigravityUsage.Dispose();
            _sessionBusy.Dispose();
            _modelEffort.Dispose();
            _sessionLastMsg.Dispose();
            _codexHook.Dispose();
            _grokHook.Dispose();
            _antigravityHook.Dispose();
            _kimiHook.Dispose();
            _devezVibeState.Dispose();
            _opencodeLastMsg.Dispose();
            _opencodeBusy.Dispose();
            _gajaeLastMsg.Dispose();
            _agentLastMsg.Dispose();
            FileExplorer.DisposeBrowser();
            _wakeScheduler.Dispose();
            WakeTerminal.Dispose();
            ShellTerminal.Dispose();
            TerminalSessionManager.Instance.DisposeRoom(ShellRoomId, purgeTracking: false);
            foreach (var roomId in _wakeRoomIds)
                TerminalSessionManager.Instance.DisposeRoom(roomId, purgeTracking: false);
        };
    }

    /// <summary>마지막 실행의 창 위치/크기/최대화를 복원. 저장값이 화면 밖이면(모니터 분리·해상도 변경)
    /// 버리지 않고 가상 화면 안으로 밀어 넣는다 — 통째로 무시하면 "위치 기억이 안 된다"로 보인다.</summary>
    private void RestoreWindowPlacement()
    {
        var (l, t, w, h, max) = SettingsService.LoadWindowPlacement();
        if (l is double ll && t is double tt && w is double ww && h is double hh
            && IsFinite(ll) && IsFinite(tt) && ww > 0 && hh > 0)
        {
            var saved = new Rect(ll, tt, ww, hh);
            var virt  = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                 SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var inter = Rect.Intersect(saved, virt);
            // 화면과 충분히 겹치지 않으면(모니터 제거·해상도 축소) 크기는 유지하고 위치만 화면 안으로 클램프.
            if (inter.IsEmpty || inter.Width < 100 || inter.Height < 60)
                saved = ClampToScreen(saved, virt);
            if (saved.Width > 0 && saved.Height > 0)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = saved.Left; Top = saved.Top; Width = saved.Width; Height = saved.Height;
            }
        }
        if (max)
        {
            // 최대화/전체화면 복원은 둘 다 hwnd 생성 뒤(OnSourceInitialized)로 미룬다.
            // ctor 에서 WindowState=Maximized 로 만들면 Left/Top 이 무시돼 주 모니터로 최대화되고,
            // EnterFullScreen 도 잘못된 모니터를 잡는다. 일반 bounds(Normal)로 hwnd 를 만든 뒤
            // 최대화/전체화면 진입 → MonitorFromWindow 가 올바른 모니터를 감지한다.
            if (_useFullScreen) _restoreFullScreen = true;
            else _restoreMaximized = true;
        }
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>rect 를 크기는 유지하며 가상 화면 안으로 이동(필요하면 화면 크기까지 축소).</summary>
    private static Rect ClampToScreen(Rect r, Rect virt)
    {
        double w = Math.Min(r.Width, virt.Width), h = Math.Min(r.Height, virt.Height);
        double x = Math.Min(Math.Max(r.Left, virt.Left), virt.Right - w);
        double y = Math.Min(Math.Max(r.Top, virt.Top), virt.Bottom - h);
        return new Rect(x, y, w, h);
    }

    /// <summary>창의 실제 화면 rect(DIP). 스냅(dock)·일반 상태의 "지금 보이는" 위치/크기다.</summary>
    private Rect CurrentWindowRectDip()
    {
        if (_mainHwnd != IntPtr.Zero && GetWindowRect(_mainHwnd, out var r))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY,
                            (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
        }
        return new Rect(Left, Top, ActualWidth, ActualHeight);
    }

    /// <summary>현재 창 위치/크기/최대화를 저장.
    /// - 전체화면: 진입 전 일반 bounds(<see cref="_preFsBounds"/>)
    /// - 최대화/최소화: RestoreBounds(그때만 일반 크기를 담는다)
    /// - 일반: 실제 창 rect. Windows 스냅(dock)된 창은 WindowState 가 Normal 인데 RestoreBounds(=Win32
    ///   rcNormalPosition)엔 스냅 *전* 크기가 남아 있어서, 그대로 저장하면 dock 위치가 기억되지 않는다.</summary>
    private void SaveWindowPlacement()
    {
        // 최소화 상태로 종료해도 최대화 기억은 유지(최소화 직전 상태로 판정).
        bool max = _inFullScreen || WindowState == WindowState.Maximized
                || (WindowState == WindowState.Minimized && _lastNonMinState == WindowState.Maximized);
        var b = _inFullScreen                        ? _preFsBounds
              : WindowState == WindowState.Normal    ? CurrentWindowRectDip()
                                                     : RestoreBounds;
        if (!IsValidBounds(b)) b = CurrentWindowRectDip();
        if (!IsValidBounds(b)) b = new Rect(Left, Top, ActualWidth, ActualHeight);
        if (!IsValidBounds(b)) return; // 쓸 수 있는 값이 없으면 기존 저장값을 덮지 않는다.
        SettingsService.SaveWindowPlacement(b.Left, b.Top, b.Width, b.Height, max);
    }

    private static bool IsValidBounds(Rect r)
        => !r.IsEmpty && IsFinite(r.Left) && IsFinite(r.Top)
        && IsFinite(r.Width) && IsFinite(r.Height) && r.Width > 0 && r.Height > 0;

    /// <summary>이동/리사이즈가 멎으면 창 배치를 저장(디바운스). 종료 시 저장만으로는 크래시·강제 종료·
    /// 로그오프에서 마지막 위치가 유실된다.</summary>
    private void ScheduleWindowPlacementSave()
    {
        if (!IsLoaded || _shuttingDown) return;
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private readonly System.Windows.Threading.DispatcherTimer _placementSaveTimer = new()
    { Interval = TimeSpan.FromSeconds(2) };

    private WindowState _lastNonMinState = WindowState.Normal; // 최소화 직전 상태(최대화 기억 보존용)

    private bool _shuttingDown;
    private bool _readyToClose; // 안전 정리(스냅샷/오버레이/graceful) 완료 후 우리가 부른 Close() 만 통과시킨다.

    /// <summary>'닫기 버튼으로 최소화' 설정이 켜져 있어도 이번 Close() 만은 실제 종료로 통과시킨다
    /// (닫기 버튼 우클릭 '완전히 종료', 업데이트 재시작 등). 최소화 가로채기 직전에 검사한다.</summary>
    public bool ForceQuit;

    /// <summary>'재시작하고 업데이트'로 종료 중인가 — 종료 오버레이에 '업데이트 후 자동으로 다시 실행됩니다'
    /// 안내를 함께 표시한다. <see cref="App.RestartForAgentUpdate"/> 가 Close() 직전에 설정한다.</summary>
    public bool RestartingForUpdate;

    /// <summary>창 종료 가로채기: 살아있는 세션이 있으면 닫기를 보류하고, 오버레이를 띄운 채
    /// 모든 세션을 graceful 종료(claude/codex transcript flush 기회)한 뒤 실제로 닫는다.</summary>
    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // '닫기 버튼으로 최소화' 설정: 실제 종료(ForceQuit·이미 종료 진행 중)가 아니면 종료를 취소하고 최소화만.
        if (!_shuttingDown && !ForceQuit && SettingsService.LoadMinimizeOnClose())
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            return;
        }

        // '닫기 시 종료' 모드에서 X/Alt+F4 로 닫을 때 실수 종료 방지 확인. 명시적 종료(ForceQuit: 우클릭 완전 종료·업데이트 재시작)·2차 진입은 건너뜀.
        if (!_shuttingDown && !ForceQuit
            && !Views.ConfirmDialog.Show("종료 확인",
                "DevezCode를 종료하시겠습니까?\n앱 내부 세션은 종료되며, 외부 터미널 세션은 유지됩니다.",
                okLabel: "종료", iconKey: "IconLogOut", danger: true))
        {
            e.Cancel = true;
            return;
        }

        SaveWindowPlacement();
        _placementSaveTimer.Stop(); // 종료 중 디바운스 저장이 뒤늦게 덮어쓰지 않도록
        _wakeScheduler.Stop();
        _idleSessionShutdownTimer.Stop();
        _externalSessionTimer.Stop();

        if (_shuttingDown)
        {
            // 2차 진입. 안전 정리가 아직 끝나지 않았는데(우리가 Close() 를 부르기 전) 들어온 것이면
            // 사용자의 재차 X 클릭·Alt+F4·작업표시줄 닫기이므로 반드시 취소한다. 여기서 그냥 통과시키면
            // 진행 중이던 스냅샷·오버레이 단계를 건너뛴 채 창이 닫혀(App.OnExit 폴백만 graceful 수행)
            // 안전 종료를 건너뛴 것처럼 '그냥 꺼진다'. 정리 완료(_readyToClose) 후 우리가 부른 Close() 만 통과.
            if (!_readyToClose) e.Cancel = true;
            return;
        }
        // 하단 터미널 패널(pwsh)은 graceful 종료 대상이 아니다 — flush 할 훅/transcript 가 없고
        // Ctrl+C 로 죽지도 않아 perGrace 타임아웃만 소진한다. 종료 확정 즉시 하드 정리.
        try { TerminalSessionManager.Instance.DisposeRoom(ShellRoomId, purgeTracking: false); } catch { }
        if (!TerminalSessionManager.Instance.HasSessionsToClose()) return; // 닫을 세션 없음(ConPTY 미생성)

        e.Cancel = true;
        _shuttingDown = true;
        // WebView2(터미널/md 에디터/브라우저)는 HWND 라 WPF 오버레이를 가린다(airspace).
        // 2단계 suspend: ①모든 패널이 스냅샷만 올리고(HWND 유지) → 스냅샷 present 대기 →
        // ②모든 HWND 를 '같은 프레임'에 일괄 숨김. 패널별 순차(캡처→hide) 방식은 HWND 가
        // 서로 다른 프레임에 사라져 팝이 여러 번 어긋나 보였다(종료 오버레이 직전 깜빡임).
        try
        {
            DevezCode.Services.DiagLog.Write("Shutdown: prepare snapshots");
            // WebView 가 응답불가면 스냅샷 준비가 무한정 멈출 수 있다. 재진입을 취소하도록 바꾼 뒤로는
            // 이 대기가 걸리면 X 로도 못 닫으므로, 상한을 두고 초과 시 그냥 숨김/종료로 넘어간다(best effort).
            // 순서(브라우저 suspend → 패널 스냅샷 → 프레임 present 대기)는 깜빡임 방지에 필요하므로 유지.
            // 하단 터미널 패널도 같은 2단계를 탄다 — 안 하면 라이브 HWND 가 종료 오버레이를 뚫고 보인다(airspace).
            async Task PrepareShellPanelSnapshotAsync()
            {
                if (ShellTerminalPanel.Visibility != Visibility.Visible) return;
                try
                {
                    var snap = await ShellTerminal.CaptureSnapshotAsync();
                    if (snap != null)
                    {
                        ShellTerminalSnapshot.Source = snap;
                        ShellTerminalSnapshot.Visibility = Visibility.Visible;
                    }
                }
                catch { /* 캡처 실패 시 그냥 숨김만 — best effort */ }
            }
            async Task PrepareAsync()
            {
                await FileExplorer.SuspendBrowserAsync();
                await Task.WhenAll(_panes.Select(p => p.PrepareShutdownSnapshotAsync())
                                         .Append(PrepareShellPanelSnapshotAsync()));
                await Views.WorkspacePaneView.WaitForFramesAsync(2); // 스냅샷 present 보장
            }
            var prep = PrepareAsync();
            if (await Task.WhenAny(prep, Task.Delay(3000)) == prep)
            {
                await prep; // 완료 → 예외였다면 기존처럼 catch 로 전파(CommitShutdownHide 스킵)
            }
            else
            {
                DevezCode.Services.DiagLog.Write("Shutdown: prepare timed out → 강제 진행");
                // 뒤늦게 fault 나도 UnobservedTaskException(crash.log 오염)이 되지 않게 관찰만 해 둔다.
                _ = prep.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            foreach (var p in _panes) p.CommitShutdownHide();
            if (ShellTerminalPanel.Visibility == Visibility.Visible)
                ShellTerminal.Visibility = Visibility.Collapsed; // 패널 HWND 도 같은 프레임에 숨김
            DevezCode.Services.DiagLog.Write("Shutdown: HWNDs hidden");
        }
        catch { /* best effort */ }
        // 스냅샷이 실제로 한 프레임 그려진 뒤 오버레이를 올린다 → WebView 가 사라진 직후 빈 배경이 비치는 깜빡임 제거.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        if (RestartingForUpdate) ShutdownRestartNote.Visibility = Visibility.Visible;
        ShutdownOverlay.Visibility = Visibility.Visible;
        try { await TerminalSessionManager.Instance.GracefulShutdownAllAsync(2500); }
        catch { /* best effort */ }
        _readyToClose = true; // 이제부터의 재진입(아래 Close())만 통과 — 그 전 재진입은 위에서 취소됨.
        Close();
    }

    private void CheckHookSetup()
    {
        if (!TerminalSessionManager.HookAssetsHealthy())
            TerminalSessionManager.EnsureHookAssets();
    }

    /// <summary>시작 시 마지막 세션/프로젝트를 자동으로 열지 않는다 — 사용자는 늘 프로젝트 미선택 상태로
    /// 시작하길 원함(세션을 보다 끄든 파일을 보다 끄든 동일). CleanShutdown 마커만 갱신한다.
    /// 직전에 열려 있던 파일 탭은 RestoreOpenFiles 가 '탭만' 복원하며 활성화(포커스)는 하지 않는다.</summary>
    private void RestoreLastSession()
    {
        // 옵션이 켜져 있으면 마지막으로 보던 프로젝트(가능하면 세션까지)를 자동으로 복원한다. 기본 false.
        // 분할 복원이 예정돼 있으면 여기서 열지 않는다 — 세션을 전체폭으로 열었다가 곧바로 분할(절반)로
        // 줄며 로딩 스피너 재타깃(중간에 한 번 깜빡)·전체폭 fit 리플로우가 생긴다.
        // RestoreSplitState_AfterLayout 이 좌/우 내용을 처음부터 최종(절반) 폭에서 모두 복원한다.
        var (splitActive, _, _, _, _, _) = SettingsService.LoadFullSplitState();
        if (SettingsService.LoadAutoLoadLastProject() && !splitActive)
        {
            var (projPath, sessId) = SettingsService.LoadLastActive();
            // 세션ID(전역 유일) 우선 — 같은 경로 중복 프로젝트도 정확히 그 세션의 소속을 찾는다.
            var sess = sessId != null
                ? _projects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == sessId)
                : null;
            var proj = sess != null ? _projects.FirstOrDefault(p => p.Tabs.Contains(sess))
                     : projPath != null ? _projects.FirstOrDefault(p => p.Path == projPath) : null;
            if (proj != null)
            {
                if (sess != null) _focusedPane.OpenSession(sess);
                else _focusedPane.SelectProject(proj);
            }
        }
        SettingsService.SaveCleanShutdown(false);
    }

    /// <summary>직전 실행에서 열려 있던 파일 편집기 탭들을 복원한다(PaneA 에 생성, 활성화는 안 함).
    /// 각 프로젝트의 PendingOpenFiles(workspace.json 에서 로드)를 1회 소비한다. 삭제된 파일은 건너뛴다.</summary>
    private void RestoreOpenFiles()
    {
        foreach (var proj in _projects)
        {
            if (proj.PendingOpenFiles.Count == 0) continue;
            PaneA.RestoreFileTabs(proj, proj.PendingOpenFiles);
            proj.PendingOpenFiles.Clear();
        }
        // 세션+파일 탭이 모두 생성된 뒤, 저장된 전체 순서로 Tabs 를 재배열(문서가 끝으로 몰려 끼임 순서 잃는 것 방지).
        foreach (var proj in _projects)
        {
            var order = proj.PendingTabOrder;
            proj.PendingTabOrder = new();
            if (order.Count == 0) continue;
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < order.Count; i++) rank[order[i]] = i;
            var sorted = proj.Tabs.OrderBy(t => rank.TryGetValue(WorkspacePaneView.RefOf(t), out var r) ? r : int.MaxValue).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int cur = proj.Tabs.IndexOf(sorted[i]);
                if (cur != i && cur >= 0) proj.Tabs.Move(cur, i);
            }
        }
    }

    // ── 성능 모니터 (헤더 CPU/RAM 칩, devez 이식) ──────────────────────
    // 3초 주기 측정 → 디스패처로 헤더 TextBlock 갱신. 사용률에 따라 색을 바꾼다
    // (≥80% Danger, ≥50% Today, 그 외 Text).
    private void StartPerfMonitor()
    {
        ApplyPerfMonitorVisibility();
        _perfMonitor.SnapshotUpdated += snap =>
            Dispatcher.InvokeAsync(() => ApplyPerfSnapshot(snap));
        _perfMonitor.Start();

        // 온도 모니터 — PerformanceCounter 로 CPU 온도 읽기. 실패 시 TMP 숨김.
        _thermalMonitor.SnapshotUpdated += snap =>
            Dispatcher.InvokeAsync(() => ApplyThermalSnap(snap));
        _thermalMonitor.Start();
    }

    private void ApplyThermalSnap(Models.ThermalSnapshot? snap)
    {
        if (snap?.CpuTemperature is float cpu)
        {
            PerfTempLabel.Visibility = Visibility.Visible;
            PerfTempText.Visibility = Visibility.Visible;
            PerfTempText.Text = $"{cpu:F0}°";
            PerfTempText.Foreground = cpu >= 85 ? (Brush)FindResource("DangerBrush")
                                    : cpu >= 70 ? (Brush)FindResource("WarningBrush")
                                    : (Brush)FindResource("SuccessBrush");
        }
        else
        {
            PerfTempLabel.Visibility = Visibility.Collapsed;
            PerfTempText.Visibility = Visibility.Collapsed;
        }
    }

    // ── 계정 사용량 (OAuth API 우선 + statusLine 훅 폴백 → 푸터) ────────
    private void StartStatusLine()
    {
        _statusLine.SnapshotUpdated += s => OnRlSnapshot(s, fromApi: false);
        _usageApi.SnapshotUpdated  += s => OnRlSnapshot(s, fromApi: true);
        _statusLine.Start();
        _usageApi.Start();
        _claudeFreshnessTimer.Tick += (_, _) => ReevaluateClaudeUsage();
        _claudeFreshnessTimer.Start();

        // codex·opencode-go·deepseek·grok 사용량 폴링 → 푸터 패널(데이터 오면 자동 표시).
        _codex.Updated       += u => Dispatcher.InvokeAsync(() => ApplyProviderUsage(u));
        _kimi.Updated        += u => Dispatcher.InvokeAsync(() => ApplyProviderUsage(u));
        _openCodeGo.Updated  += u => Dispatcher.InvokeAsync(() => ApplyProviderUsage(u));
        _deepSeek.Updated    += u => Dispatcher.InvokeAsync(() => ApplyProviderUsage(u));
        _grok.Updated        += u => Dispatcher.InvokeAsync(() => ApplyProviderUsage(u));
        _antigravityUsage.Updated += u => Dispatcher.InvokeAsync(() => ApplyProviderUsage(u));
        _codex.Start();
        _kimi.Start();
        _openCodeGo.Start();
        _deepSeek.Start();
        _grok.Start();
        _antigravityUsage.Start();
    }

    /// <summary>표시 가능한(데이터 있는) provider 만 사용량 카드로 변환. Claude → Codex → OpenCode Go 순.</summary>
    private IReadOnlyList<Models.UsageCardVM> BuildUsageCards()
    {
        var cards = new List<Models.UsageCardVM>();

        // Claude — RateLimitSnapshot(5시간/주간). 사이드바는 연결된 provider 를 항상 표시(토글 무관).
        if (_rlMerged is { HasData: true } rl)
        {
            var rows = new List<Models.UsageRowVM>();
            var showEst = SettingsService.LoadShowEstimate();
            AddRow(rows, "5시간", rl.FiveHourPercent, rl.FiveHourResetsAt, isShortWindow: true, showEstimate: showEst);
            AddRow(rows, "주간", rl.SevenDayPercent, rl.SevenDayResetsAt, isShortWindow: false, showEstimate: showEst);
            AddRow(rows, "Fable", rl.FableWeeklyPercent, rl.FableWeeklyResetsAt, isShortWindow: false, showEstimate: showEst);
            var claudePlan = UsageApiService.FormatPlanLabel(UsageApiService.ReadSubscriptionType());
            cards.Add(new Models.UsageCardVM
            {
                Name = "Claude",
                Plan = claudePlan,
                IconPath = "pack://application:,,,/Resources/Images/ShellPresets/claude_code.png",
                CapturedAt = new DateTimeOffset(rl.CapturedAt),
                IsStale = _claudeUsageStale,
                Rows = rows,
            });
        }

        AddProviderCard(cards, _lastCodex, "Codex", App.CodexIconUri);
        AddProviderCard(cards, _lastKimi, "Kimi", App.KimiIconUri);
        AddProviderCard(cards, _lastGo, "OpenCode Go", App.OpenCodeIconUri);
        AddGrokCard(cards);
        AddAntigravityCard(cards);
        AddDeepSeekCard(cards);

        return cards;
    }

    private void AddGrokCard(List<Models.UsageCardVM> cards)
    {
        if (_lastGrok is null) return;
        if (_lastGrok.HasData)
        {
            // Grok Build 는 주간 한도만 제공 — 5시간/월간 행은 만들지 않는다.
            var rows = new List<Models.UsageRowVM>();
            var showEst = SettingsService.LoadShowEstimate();
            AddRow(rows, "주간", _lastGrok.Weekly?.UsedPercent, _lastGrok.Weekly?.ResetsAt, isShortWindow: false, showEstimate: showEst);
            AddRow(rows, "월간", _lastGrok.Monthly?.UsedPercent, _lastGrok.Monthly?.ResetsAt, isShortWindow: false, showEstimate: showEst);
            cards.Add(new Models.UsageCardVM
            {
                Name = "Grok Build",
                Plan = _lastGrok.PlanLabel,
                IconPath = App.GrokIconUri,
                CapturedAt = _lastGrok.CapturedAt,
                IsStale = _lastGrok.CapturedAt < DateTimeOffset.Now - ProviderUsageFreshness,
                Rows = rows,
            });
            return;
        }
        if (string.IsNullOrEmpty(_lastGrok.Error)) return;
        cards.Add(new Models.UsageCardVM
        {
            Name = "Grok Build",
            Plan = _lastGrok.Error,
            IconPath = App.GrokIconUri,
            CapturedAt = _lastGrok.CapturedAt,
            IsStale = _lastGrok.CapturedAt < DateTimeOffset.Now - ProviderUsageFreshness,
            Rows = Array.Empty<Models.UsageRowVM>(),
        });
    }

    private void AddAntigravityCard(List<Models.UsageCardVM> cards)
    {
        if (_lastAntigravity is null) return;
        const string iconPath = "pack://application:,,,/Resources/Images/ShellPresets/anti.png";
        if (_lastAntigravity.HasData)
        {
            // agy 는 창 하나만 제공 — 서비스가 리셋 시각으로 5시간(Primary)/주간(Weekly)을 판별.
            var rows = new List<Models.UsageRowVM>();
            var showEst = SettingsService.LoadShowEstimate();
            AddRow(rows, "5시간", _lastAntigravity.Primary?.UsedPercent, _lastAntigravity.Primary?.ResetsAt, isShortWindow: true, showEstimate: showEst);
            AddRow(rows, "주간", _lastAntigravity.Weekly?.UsedPercent, _lastAntigravity.Weekly?.ResetsAt, isShortWindow: false, showEstimate: showEst);
            AddRow(rows, "월간 크레딧", _lastAntigravity.Monthly?.UsedPercent, _lastAntigravity.Monthly?.ResetsAt, isShortWindow: false, showEstimate: showEst);
            cards.Add(new Models.UsageCardVM
            {
                Name = "Antigravity",
                Plan = _lastAntigravity.PlanLabel,
                IconPath = iconPath,
                CapturedAt = _lastAntigravity.CapturedAt,
                IsStale = _lastAntigravity.CapturedAt < DateTimeOffset.Now - ProviderUsageFreshness,
                Rows = rows,
            });
            return;
        }
        if (string.IsNullOrEmpty(_lastAntigravity.Error)) return;
        cards.Add(new Models.UsageCardVM
        {
            Name = "Antigravity",
            Plan = _lastAntigravity.Error,
            IconPath = iconPath,
            CapturedAt = _lastAntigravity.CapturedAt,
            IsStale = _lastAntigravity.CapturedAt < DateTimeOffset.Now - ProviderUsageFreshness,
            Rows = Array.Empty<Models.UsageRowVM>(),
        });
    }

    /// <summary>DeepSeek 잔액 텍스트를 사이드바에 추가. 프로그래스바 없이 잔액만 표시.</summary>
    private void AddDeepSeekCard(List<Models.UsageCardVM> cards)
    {
        if (_lastDeepSeek is not { HasData: true } u || u.Balances.Count == 0) return;
        var b = u.Balances[0];
        var symbol = b.Currency switch { "CNY" => "¥", "USD" => "$", _ => b.Currency + " " };
        var rows = new List<Models.UsageRowVM>
        {
            new()
            {
                Label = "잔액",
                PercentText = $"{symbol}{b.TotalBalance}",
                BarWidth = 0,
                ShowBar = false,
                PercentMargin = new System.Windows.Thickness(-8, 0, 0, 0), // 막대 없음 — 잔액 라벨 바로 옆(한 칸 띄어쓰기)으로 당김
                ResetText = "",
            },
        };
        cards.Add(new Models.UsageCardVM
        {
            Name = "DeepSeek",
            Plan = null,
            IconPath = "pack://application:,,,/Resources/Images/ShellPresets/deepseek.png",
            CapturedAt = u.CapturedAt,
            IsStale = u.CapturedAt < DateTimeOffset.Now - ProviderUsageFreshness,
            Rows = rows,
        });
    }

    /// <summary>ProviderUsage(codex/go) → 카드. 데이터 없으면(미연결/오류) 건너뛴다.</summary>
    private void AddProviderCard(List<Models.UsageCardVM> cards, Models.ProviderUsage? u, string name, string iconPath)
    {
        if (u is not { HasData: true }) return;
        var rows = new List<Models.UsageRowVM>();
        var showEst = SettingsService.LoadShowEstimate();
        AddRow(rows, "5시간", u.Primary?.UsedPercent, u.Primary?.ResetsAt, isShortWindow: true, showEstimate: showEst);
        AddRow(rows, "주간", u.Weekly?.UsedPercent, u.Weekly?.ResetsAt, isShortWindow: false, showEstimate: showEst);
        AddRow(rows, "월간", u.Monthly?.UsedPercent, u.Monthly?.ResetsAt, isShortWindow: false, showEstimate: showEst);

        // codex 초기화권 정보 + [사용] 버튼 상태(쿨다운 반영)
        var credits = u.ResetCredits.Count > 0
            ? u.ResetCredits.Select(ToResetCreditRow).ToArray()
            : Array.Empty<Models.ResetCreditRowVM>();

        bool canConsume = false;
        string? resetTip = null;
        if (credits.Length > 0)
        {
            var cooldown = CodexUsageService.ResetCooldownRemaining();
            canConsume = cooldown == null;
            if (cooldown is { } rem)
            {
                var elapsed = CodexUsageService.ResetCooldownDuration - rem;
                resetTip =
                    $"약 {Math.Max(1, (int)elapsed.TotalMinutes)}분 전 초기화권을 사용했습니다.\n"
                    + $"중복 사용을 막기 위해 사용 후 1시간 동안 잠깁니다. (약 {Math.Max(1, (int)Math.Ceiling(rem.TotalMinutes))}분 후 다시 사용 가능)\n"
                    + "지금 더 필요하면 ChatGPT 웹사이트나 데스크톱 앱에서 사용할 수 있습니다.";
            }
        }

        cards.Add(new Models.UsageCardVM
        {
            Name = name,
            Plan = u.PlanLabel,
            IconPath = iconPath,
            CapturedAt = u.CapturedAt,
            IsStale = u.CapturedAt < DateTimeOffset.Now - ProviderUsageFreshness,
            Rows = rows,
            ResetCredits = credits,
            CanConsumeResetCredit = canConsume,
            ResetCreditTooltip = resetTip,
        });
    }

    private static Models.ResetCreditRowVM ToResetCreditRow(Models.ResetCredit c)
    {
        DateTimeOffset? expires = c.ExpiresAt;
        if (expires == null) return new Models.ResetCreditRowVM { ExpiryText = "—", DaysLeftText = "", IsExpiringSoon = false };

        var local = expires.Value.ToLocalTime();
        var expiry = local.ToString("M월 d일 HH:mm");
        var expiryStatus = FormatResetCreditExpiry(local, DateTimeOffset.Now);

        return new Models.ResetCreditRowVM
        {
            ExpiryText = $"~ {expiry}",
            DaysLeftText = expiryStatus.Text,
            IsExpiringSoon = expiryStatus.IsExpiringSoon,
        };
    }

    private static (string Text, bool IsExpiringSoon) FormatResetCreditExpiry(DateTimeOffset expiry, DateTimeOffset now)
    {
        if (expiry < now) return ("만료됨", true);

        var span = expiry - now;
        if (span.TotalDays < 1)
        {
            // 24시간 미만은 시/분 단위로 남은 시간을 표시한다.
            var text = span.TotalHours >= 1
                ? $"{(int)span.TotalHours}시간 {span.Minutes}분 후 만료"
                : $"{Math.Max(1, span.Minutes)}분 후 만료";
            return (text, true);
        }

        // 달력 날짜가 아니라 실제 남은 시간을 24시간 단위로 계산한다.
        var daysLeft = (int)span.TotalDays;
        return ($"{daysLeft}일 남음", daysLeft <= 3);
    }

    /// <summary>초기화권 [사용] 클릭 — 만료 최빠름 크레딧을 확인 후 명시 소비하고 결과를 고지한다.
    /// in-flight 락으로 재진입을 막고, 완료 후 카드를 재빌드해 쿨다운/개수를 반영한다.</summary>
    private async void ResetCreditUse_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_resetConsumeInFlight) return;
        var credits = _lastCodex?.ResetCredits;
        if (credits == null || credits.Count == 0) return;

        var pick = CodexUsageService.PickEarliestExpiring(credits);
        var expiryText = pick?.ExpiresAt is { } ex
            ? ex.ToLocalTime().ToString("M월 d일 HH:mm")
            : "만료정보 없음";

        if (!Views.ConfirmDialog.Show(
                "초기화권 사용",
                $"만료가 가장 빠른 초기화권(~ {expiryText})을 사용합니다.\n사용량 한도가 즉시 초기화되며 되돌릴 수 없습니다.",
                okLabel: "사용", danger: true))
            return;

        _resetConsumeInFlight = true;
        if (sender is System.Windows.Controls.Button b) b.IsEnabled = false;
        try
        {
            var reqId = System.Guid.NewGuid().ToString();
            var outcome = await _codex.ConsumeResetCreditAsync(pick?.Id, reqId);

            var (title, msg) = outcome switch
            {
                ConsumeOutcome.Reset or ConsumeOutcome.AlreadyRedeemed
                    => ("초기화 완료", "초기화권을 사용했습니다. 사용량 한도가 초기화되었습니다."),
                ConsumeOutcome.NothingToReset
                    => ("사용 안 됨", "초기화할 사용량이 없어 초기화권이 소모되지 않았습니다."),
                ConsumeOutcome.NoCredit
                    => ("사용 안 됨", "사용 가능한 초기화권이 없습니다."),
                _ => ("확인 필요", "결과를 확인하지 못했습니다. 사용량을 새로고침해 확인하세요."),
            };
            Views.ConfirmDialog.Alert(title, msg);
        }
        finally
        {
            _resetConsumeInFlight = false;
            if (_usageOpen) SetSidebarUsageCards(BuildUsageCards()); // 쿨다운/개수 반영해 버튼 상태 갱신
        }
    }

    /// <summary>사용률 값이 있을 때만 행을 추가. 단기 윈도우는 "남은 시간", 그 외는 "초기화 일시"로 안내.</summary>
    private void AddRow(List<Models.UsageRowVM> rows, string label, double? percent, DateTimeOffset? resetsAt, bool isShortWindow, bool showEstimate = false)
    {
        if (percent is not double p) return;
        var displayPercent = DisplayUsagePercent(p);
        var c = Math.Clamp(displayPercent, 0, 100);
        string reset = isShortWindow
            ? (resetsAt != null ? $"↻ {FormatRemaining(resetsAt)}" : "")
            : (resetsAt != null ? $"↻ {FormatResetDate(resetsAt)}" : "");
        string estimateText = "";
        System.Windows.Media.Brush? estimateBrush = null;
        if (showEstimate)
        {
            // label로 윈도우 길이 추정
            var duration = label switch
        {
                "5시간" => TimeSpan.FromHours(5),
                "주간" or "Fable" => TimeSpan.FromDays(7),
                "월간" => TimeSpan.FromDays(30),
                _ => TimeSpan.Zero,
            };
            if (duration > TimeSpan.Zero)
            {
                var est = EstimateLimitReached(percent, resetsAt, duration);
                var fmt = FormatEstimate(est, useDate: !isShortWindow);
                if (fmt != null)
       {
                    estimateText = fmt;
                    // 예상 소진 시각이 초기화 시각보다 이후면 여유(초록), 이전이면 부족(빨강)
                    if (resetsAt is DateTimeOffset resetTime && est is DateTimeOffset e)
                    {
                        try { estimateBrush = e >= resetTime
                            ? (System.Windows.Media.Brush)Application.Current.FindResource("SuccessBrush")
                            : (System.Windows.Media.Brush)Application.Current.FindResource("DangerBrush"); }
                        catch { /* 리소스 없으면 기본색 유지 */ }
    }
    }
    }
        }
        rows.Add(new Models.UsageRowVM
      {
            Label = label,
            PercentText = $"{displayPercent:F0}%",
            ResetText = reset,
            EstimateText = estimateText,
            EstimateBrush = estimateBrush,
            BarWidth = UsageBarTrack * c / 100.0,
            BarBrush = RlBrush(Math.Clamp(p, 0, 100)),
        });
   }

    private const double UsageBarTrack = 102; // 사용량 패널 막대 트랙 폭(XAML 과 일치)

    private void OnRlSnapshot(Models.RateLimitSnapshot snap, bool fromApi)
        => Dispatcher.InvokeAsync(() =>
        {
            if (fromApi) _rlApi = snap; else _rlHook = snap;
            ReevaluateClaudeUsage();
        });

    /// <summary>API를 계정 기준값으로 사용하고 훅은 API 성공 전 폴백으로만 사용한다.
    /// 시간 경과만으로도 stale/만료 상태가 바뀌므로 30초 타이머에서도 호출한다.</summary>
    private void ReevaluateClaudeUsage()
    {
        var source = _rlApi ?? _rlHook;
        if (source == null)
        {
            _rlMerged = null;
            _claudeUsageStale = false;
            RateLimitPanel.Visibility = Visibility.Collapsed;
            UpdateFooterDivider();
            if (_lastCodex != null)
                SetProviderPanel(CodexPanel, CxFiveLabel, CxFiveBar, CxFivePct,
                    CxSevenBar, CxSevenPct, _lastCodex, "Codex",
                    primaryGroup: CxFiveGroup, weeklyGroup: CxSevenGroup);
            RefreshUsagePanelIfVisible();
            return;
        }

        _claudeUsageStale = source.CapturedAt < DateTime.Now - ClaudeApiFreshness;
        var merged = RemoveExpiredClaudeWindows(source, DateTimeOffset.Now);
        _rlMerged = merged;
        ApplyRateLimit(merged);
        if (_lastCodex != null)
            SetProviderPanel(CodexPanel, CxFiveLabel, CxFiveBar, CxFivePct,
                CxSevenBar, CxSevenPct, _lastCodex, "Codex",
                primaryGroup: CxFiveGroup, weeklyGroup: CxSevenGroup);
        RefreshUsagePanelIfVisible();
    }

    private static Models.RateLimitSnapshot RemoveExpiredClaudeWindows(
        Models.RateLimitSnapshot source, DateTimeOffset now)
    {
        var fiveValid = source.FiveHourResetsAt is not { } fiveReset || fiveReset > now;
        var weekValid = source.SevenDayResetsAt is not { } weekReset || weekReset > now;
        var fableValid = source.FableWeeklyResetsAt is not { } fableReset || fableReset > now;
        return new Models.RateLimitSnapshot
        {
            CapturedAt = source.CapturedAt,
            FiveHourPercent = fiveValid ? source.FiveHourPercent : null,
            FiveHourResetsAt = fiveValid ? source.FiveHourResetsAt : null,
            SevenDayPercent = weekValid ? source.SevenDayPercent : null,
            SevenDayResetsAt = weekValid ? source.SevenDayResetsAt : null,
            FableWeeklyPercent = fableValid ? source.FableWeeklyPercent : null,
            FableWeeklyResetsAt = fableValid ? source.FableWeeklyResetsAt : null,
        };
    }

    /// <summary>사용량 사이드바가 열려 있으면 최신 스냅샷으로 카드를 다시 빌드 — 시작 시/폴링 시 자동 반영.</summary>
    internal void RefreshUsagePanelIfVisible()
    {
        if (_usageOpen) SetSidebarUsageCards(BuildUsageCards());
    }

    /// <summary>최우측 사용량 사이드바 카드 채우기. 비면 안내 문구 표시.</summary>
    private void SetSidebarUsageCards(IReadOnlyList<Models.UsageCardVM> cards)
    {
        SidebarUsageList.ItemsSource = cards;
        SidebarUsageEmpty.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (cards.Count == 0)
        {
            SidebarUsageUpdated.Text = "";
            return;
        }

        var capturedAt = cards
            .Where(card => card.CapturedAt.HasValue)
            .Select(card => card.CapturedAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.Now)
            .Min();
        SidebarUsageUpdated.Text = $"{capturedAt.ToLocalTime():HH:mm} 기준"
            + (cards.Any(card => card.IsStale) ? " · 갱신 지연" : "");
    }

    /// <summary>codex/opencode-go 스냅샷 저장 후 하단 푸터 + 우측 사이드바 갱신.</summary>
    private void ApplyProviderUsage(Models.ProviderUsage u)
    {
        // 연결 끊기 직전 시작된 비동기 폴링 결과가 늦게 도착해 카드를 되살리지 않게 막는다.
        if (!IsUsageProviderConnected(u.Provider))
        {
            ClearProviderUsage(u.Provider);
            return;
        }

        switch (u.Provider)
        {
            case "codex":
                _lastCodex = u;
                SetProviderPanel(CodexPanel, CxFiveLabel, CxFiveBar, CxFivePct,
                    CxSevenBar, CxSevenPct, u, "Codex",
                    primaryGroup: CxFiveGroup, weeklyGroup: CxSevenGroup);
                break;
            case "kimi":
                _lastKimi = u;
                SetProviderPanel(KimiPanel, KiFiveLabel, KiFiveBar, KiFivePct,
                    KiSevenBar, KiSevenPct, u, "Kimi",
                    primaryGroup: KiFiveGroup, weeklyGroup: KiSevenGroup);
                break;
            case "opencode-go":
                _lastGo = u;
                SetProviderPanel(GoPanel, GoFiveLabel, GoFiveBar, GoFivePct, GoSevenBar, GoSevenPct, u, "OpenCode Go", GoMonthBar, GoMonthPct);
                break;
            case "deepseek":
                _lastDeepSeek = u;
                ApplyDeepSeekFooter(u);
                break;
            case "grok":
                _lastGrok = u;
                ApplyGrokFooter(u);
                break;
            case "antigravity":
                _lastAntigravity = u;
                ApplyAntigravityFooter(u);
                break;
        }
        RefreshUsagePanelIfVisible();
    }

    /* ── DeepSeek 잔액 푸터 ──────────────────────────────────── */

    /// <summary>DeepSeek 잔액을 하단 푸터에 반영. percent 막대 대신 잔액 텍스트로 표시.</summary>
    private void ApplyDeepSeekFooter(Models.ProviderUsage u)
    {
        if (!u.HasData || u.Balances.Count == 0)
        { DeepSeekPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); return; }

        DeepSeekPanel.Visibility = Visibility.Visible;
        UpdateFooterDivider();

        var balance = u.Balances[0];
        // 통화 기호 매핑
        var symbol = balance.Currency switch
        {
            "CNY" => "¥",
            "USD" => "$",
            _ => balance.Currency + " ",
        };
        DeepSeekBalanceLabel.Text = $"잔액 {symbol}{balance.TotalBalance}";
        DeepSeekPanel.ToolTip = BuildDeepSeekTooltip(u);
    }

    private static string BuildDeepSeekTooltip(Models.ProviderUsage u)
    {
        if (u.Balances.Count == 0) return "DeepSeek";
        var b = u.Balances[0];
        var sb = new System.Text.StringBuilder();
        sb.Append("DeepSeek  ·  ").Append(b.Currency == "CNY" ? "위안" : "USD");
        sb.Append($"\n총 잔액: {b.TotalBalance}");
        if (u.Error != null) sb.Append('\n').Append(u.Error);
        return sb.ToString();
    }

    /// <summary>API 키 저장 직후 MainWindow 에서 즉시 폴링 (SettingsDialog 에서 호출).</summary>
    public void RefreshDeepSeekUsage() => _deepSeek.RefreshNow();

    /// <summary>Grok 토큰/설정 변경 직후 즉시 폴링.</summary>
    public void RefreshGrokUsage() => _grok.RefreshNow();

    /// <summary>Kimi 사용량 연결 상태 변경 직후 즉시 재렌더.</summary>
    public void RefreshKimiUsage() => _kimi.RefreshNow();

    /// <summary>DevezCode 사용량 연결만 끊는다. 외부 CLI 자격증명은 삭제하지 않는다.</summary>
    public void DisconnectUsageProvider(string provider)
    {
        switch (provider)
        {
            case "codex":
                CodexCredentialStore.Disconnect();
                break;
            case "opencode-go":
                OpenCodeGoCredentialStore.Disconnect();
                break;
            case "grok":
                _grok.Disconnect();
                break;
            case "antigravity":
                AntigravityCredentialStore.Disconnect();
                break;
            case "deepseek":
                DeepSeekCredentialStore.SaveApiKey(null);
                break;
            case "kimi":
                _kimi.Disconnect();
                break;
            default:
                return;
        }

        ClearProviderUsage(provider);
    }

    private void ClearProviderUsage(string provider)
    {
        switch (provider)
        {
            case "codex":
                _lastCodex = null;
                CodexPanel.Visibility = Visibility.Collapsed;
                break;
            case "opencode-go":
                _lastGo = null;
                GoPanel.Visibility = Visibility.Collapsed;
                break;
            case "grok":
                _lastGrok = null;
                GrokPanel.Visibility = Visibility.Collapsed;
                break;
            case "antigravity":
                _lastAntigravity = null;
                AntigravityPanel.Visibility = Visibility.Collapsed;
                break;
            case "deepseek":
                _lastDeepSeek = null;
                DeepSeekPanel.Visibility = Visibility.Collapsed;
                break;
            case "kimi":
                _lastKimi = null;
                KimiPanel.Visibility = Visibility.Collapsed;
                break;
        }

        UpdateFooterDivider();
        RefreshUsagePanelIfVisible();
    }

    private static bool IsUsageProviderConnected(string provider) => provider switch
    {
        "codex" => CodexUsageService.IsConnected(),
        "opencode-go" => OpenCodeGoCredentialStore.IsConnected(),
        "grok" => GrokUsageService.IsConnected(),
        "deepseek" => DeepSeekCredentialStore.IsConnected(),
        "antigravity" => AntigravityUsageService.IsConnected(),
        "kimi" => KimiUsageService.IsConnected(),
        _ => true,
    };

    /// <summary>Grok Build 주간 한도를 하단 푸터에 반영 (CLI /usage 와 동일 format=credits).</summary>
    private void ApplyGrokFooter(Models.ProviderUsage u)
    {
        if (!u.HasData && string.IsNullOrEmpty(u.Error))
        { GrokPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); return; }

        GrokPanel.Visibility = Visibility.Visible;
        UpdateFooterDivider();
        var pct = u.Weekly?.UsedPercent ?? u.Monthly?.UsedPercent;
        SetWindowBar(GrokWeekBar, GrokWeekPct, pct);
        GrokWeekLabel.Text = !string.IsNullOrEmpty(u.Error) ? "!"
            : u.Weekly != null ? "주간" : "월간";
        if (!string.IsNullOrEmpty(u.Error) && pct is null)
            GrokWeekPct.Text = "--";
        GrokPanel.ToolTip = BuildGrokTooltip(u);
    }

    private static string BuildGrokTooltip(Models.ProviderUsage u)
    {
        if (!string.IsNullOrEmpty(u.Error))
            return "Grok Build\n" + u.Error;
        var sb = new System.Text.StringBuilder("Grok Build");
        if (u.Weekly?.UsedPercent is double w)
            sb.Append($"\n주간 한도 {FormatUsagePercent(w)}");
        if (u.Monthly?.UsedPercent is double m)
            sb.Append($"\n월간 한도 {FormatUsagePercent(m)}");
        var reset = u.Weekly?.ResetsAt ?? u.Monthly?.ResetsAt;
        if (reset is DateTimeOffset r)
            sb.Append($"  ·  초기화 {FormatResetDate(r)}");
        return sb.ToString();
    }

    /// <summary>Antigravity(agy) 모델 한도 사용률을 하단 푸터에 반영. 창 종류는 플랜별로 달라
    /// 서비스가 리셋 시각으로 판별해 Primary(5시간)/Weekly(주간) 중 한쪽만 채운다 — grok 패턴.</summary>
    private void ApplyAntigravityFooter(Models.ProviderUsage u)
    {
        if (!u.HasData && string.IsNullOrEmpty(u.Error))
        { AntigravityPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); return; }

        AntigravityPanel.Visibility = Visibility.Visible;
        UpdateFooterDivider();
        var pct = u.Primary?.UsedPercent ?? u.Weekly?.UsedPercent;
        SetWindowBar(AntigravityFiveBar, AntigravityFivePct, pct);
        AntigravityFiveLabel.Text = !string.IsNullOrEmpty(u.Error) ? "!"
            : u.Primary != null ? (FormatRemainingShort(u.Primary.ResetsAt) ?? "5h")
            : "주간";
        if (!string.IsNullOrEmpty(u.Error) && pct is null)
            AntigravityFivePct.Text = "--";
        AntigravityPanel.ToolTip = BuildAntigravityTooltip(u);
    }

    private static string BuildAntigravityTooltip(Models.ProviderUsage u)
    {
        if (!string.IsNullOrEmpty(u.Error))
            return "Antigravity\n" + u.Error;
        var sb = new System.Text.StringBuilder("Antigravity");
        if (!string.IsNullOrEmpty(u.PlanLabel)) sb.Append("  ·  ").Append(u.PlanLabel);
        if (u.Primary?.UsedPercent is double p)
            sb.Append($"\n5시간 한도 {FormatUsagePercent(p)}  ·  초기화까지 {FormatRemaining(u.Primary.ResetsAt)}");
        if (u.Weekly?.UsedPercent is double w)
        {
            sb.Append($"\n주간 한도 {FormatUsagePercent(w)}");
            if (u.Weekly.ResetsAt is DateTimeOffset r)
                sb.Append($"  ·  초기화 {FormatResetDate(r)}");
        }
        if (u.Monthly?.UsedPercent is double m)
            sb.Append($"\n월간 크레딧 {FormatUsagePercent(m)}");
        return sb.ToString();
    }

    /// <summary>agy 토큰/설정 변경 직후 즉시 폴링.</summary>
    public void RefreshAntigravityUsage() => _antigravityUsage.RefreshNow();

    /* ── 하단 푸터 계정 사용량 (우측 사이드바와 별개; 연결된 사용량은 항상 표시) ── */

    private const double RlTrackWidth = 56;


    /// <summary>Claude rate limit 을 하단 푸터에 반영. 데이터가 없으면 숨김.</summary>
    private void ApplyRateLimit(Models.RateLimitSnapshot snap)
    {
        if (!snap.HasData)
        { RateLimitPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); return; }
        RateLimitPanel.Visibility = Visibility.Visible;
        UpdateFooterDivider();
        SetBar(RlFiveLabel, RlFiveBar, RlFivePct,
               FormatRemainingShort(snap.FiveHourResetsAt) ?? "5h", snap.FiveHourPercent);
        SetBar(RlSevenLabel, RlSevenBar, RlSevenPct, "주간", snap.SevenDayPercent);
        if (snap.FableWeeklyPercent is double fw)
        {
            RlFableGroup.Visibility = Visibility.Visible;
            SetWindowBar(RlFableBar, RlFablePct, fw);
        }
        else RlFableGroup.Visibility = Visibility.Collapsed;
        RateLimitPanel.ToolTip = BuildRlTooltip(snap)
            + (_claudeUsageStale ? $"\n갱신 지연 · 마지막 성공 {snap.CapturedAt:HH:mm}" : "");
    }

    /// <summary>codex/go 사용량을 해당 푸터 패널에 반영. 데이터가 없으면 숨김.</summary>
    private void SetProviderPanel(System.Windows.Controls.StackPanel panel,
        TextBlock fLabel, Border fBar, TextBlock fPct, Border wBar, TextBlock wPct,
        Models.ProviderUsage u, string name, Border? mBar = null, TextBlock? mPct = null,
        StackPanel? primaryGroup = null, StackPanel? weeklyGroup = null)
    {
        if (!u.HasData) { panel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); return; }
        panel.Visibility = Visibility.Visible;
        if (primaryGroup != null) primaryGroup.Visibility = u.Primary != null ? Visibility.Visible : Visibility.Collapsed;
        if (weeklyGroup != null)
        {
            weeklyGroup.Visibility = u.Weekly != null ? Visibility.Visible : Visibility.Collapsed;
            weeklyGroup.Margin = u.Primary != null
                ? new System.Windows.Thickness(14, 0, 0, 0)
                : new System.Windows.Thickness(0);
        }
        SetBar(fLabel, fBar, fPct, FormatRemainingShort(u.Primary?.ResetsAt) ?? "5h", u.Primary?.UsedPercent);
        SetWindowBar(wBar, wPct, u.Weekly?.UsedPercent);
        if (mBar != null && mPct != null) SetWindowBar(mBar, mPct, u.Monthly?.UsedPercent);
        panel.ToolTip = BuildProviderTooltip(u, name)
            + (u.CapturedAt < DateTimeOffset.Now - ProviderUsageFreshness
                ? $"\n갱신 지연 · 마지막 성공 {u.CapturedAt.ToLocalTime():HH:mm}"
                : "");
        UpdateFooterDivider();
    }

    /// <summary>한도 막대 1세트 갱신 — 라벨 / 채움 너비·색 / 퍼센트.</summary>
    private void SetBar(TextBlock label, Border bar, TextBlock pctText, string labelText, double? pct)
    {
        label.Text = labelText;
        if (pct is double v)
        {
            var displayPercent = DisplayUsagePercent(v);
            var c = Math.Clamp(displayPercent, 0, 100);
            bar.Width = RlTrackWidth * c / 100.0;
            bar.Background = RlBrush(Math.Clamp(v, 0, 100));
            pctText.Text = $"{displayPercent:F0}%";
        }
        else { bar.Width = 0; pctText.Text = "--"; }
    }

    /// <summary>고정 라벨 윈도우 막대(주간·월간) 갱신.</summary>
    private void SetWindowBar(Border bar, TextBlock pct, double? usedPercent)
    {
        if (usedPercent is double p)
        {
            var displayPercent = DisplayUsagePercent(p);
            var c = Math.Clamp(displayPercent, 0, 100);
            bar.Width = RlTrackWidth * c / 100.0;
            bar.Background = RlBrush(Math.Clamp(p, 0, 100));
            pct.Text = $"{displayPercent:F0}%";
        }
        else
        {
            bar.Width = 0;
            pct.Text = "--";
        }
    }

    /// <summary>provider 패널 사이 리딩 구분선(|) 동적 표시 — 앞 패널이 보일 때만.</summary>
    private void UpdateFooterDivider()
    {
        bool claude = RateLimitPanel.Visibility == Visibility.Visible;
        bool codex = CodexPanel.Visibility == Visibility.Visible;
        bool kimi = KimiPanel.Visibility == Visibility.Visible;
        bool go = GoPanel.Visibility == Visibility.Visible;
        bool grok = GrokPanel.Visibility == Visibility.Visible;
        bool antigravity = AntigravityPanel.Visibility == Visibility.Visible;
        // 푸터 패널 순서(XAML): claude → codex → kimi → go → grok → antigravity → deepseek.
        if (CxLeadDivider != null) CxLeadDivider.Visibility = claude ? Visibility.Visible : Visibility.Collapsed;
        if (KiLeadDivider != null) KiLeadDivider.Visibility = (claude || codex) ? Visibility.Visible : Visibility.Collapsed;
        if (GoLeadDivider != null) GoLeadDivider.Visibility = (claude || codex || kimi) ? Visibility.Visible : Visibility.Collapsed;
        if (GrokLeadDivider != null) GrokLeadDivider.Visibility = (claude || codex || kimi || go) ? Visibility.Visible : Visibility.Collapsed;
        if (AntigravityLeadDivider != null) AntigravityLeadDivider.Visibility = (claude || codex || kimi || go || grok) ? Visibility.Visible : Visibility.Collapsed;
        if (DeepSeekLeadDivider != null) DeepSeekLeadDivider.Visibility = (claude || codex || kimi || go || grok || antigravity) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BuildRlTooltip(Models.RateLimitSnapshot snap)
    {
        var planLabel = UsageApiService.FormatPlanLabel(UsageApiService.ReadSubscriptionType());
        var showEst = SettingsService.LoadShowEstimate();
        var sb = new System.Text.StringBuilder();
       sb.Append(planLabel);
        if (snap.FiveHourPercent is double f)
       {
            sb.Append($"\n5시간 한도 {FormatUsagePercent(f)}  ·  초기화까지 {FormatRemaining(snap.FiveHourResetsAt)}");
            if (showEst)
            {
                var est5h = EstimateLimitReached(f, snap.FiveHourResetsAt, TimeSpan.FromHours(5));
                var fmt5h = FormatEstimate(est5h);
                if (fmt5h != null) sb.Append($"  ·  예상 소진 {fmt5h}");
            }
       }
        if (snap.SevenDayPercent is double w)
       {
            sb.Append($"\n주간 한도 {FormatUsagePercent(w)}  ·  초기화 {FormatResetDate(snap.SevenDayResetsAt)}");
            if (showEst)
            {
                var est7d = EstimateLimitReached(w, snap.SevenDayResetsAt, TimeSpan.FromDays(7));
                var fmt7d = FormatEstimate(est7d, useDate: true);
                if (fmt7d != null) sb.Append($"  ·  예상 소진 {fmt7d}");
            }
       }
        if (snap.FableWeeklyPercent is double fwt)
            sb.Append($"\nFable 주간 한도 {FormatUsagePercent(fwt)}  ·  초기화 {FormatResetDate(snap.FableWeeklyResetsAt)}");
        return sb.ToString();
       }

    private static string BuildProviderTooltip(Models.ProviderUsage u, string name)
    {
        var showEst = SettingsService.LoadShowEstimate();
        var sb = new System.Text.StringBuilder();
        sb.Append(name);
        if (u.PlanLabel != null) sb.Append("  ·  ").Append(u.PlanLabel);
        if (u.Primary?.UsedPercent is double p)
        {
            sb.Append($"\n5시간 한도 {FormatUsagePercent(p)}  ·  초기화까지 {FormatRemaining(u.Primary.ResetsAt)}");
            if (showEst)
            {
                var estP = EstimateLimitReached(p, u.Primary.ResetsAt, TimeSpan.FromHours(5));
                var fmtP = FormatEstimate(estP);
                if (fmtP != null) sb.Append($"  ·  예상 소진 {fmtP}");
            }
        }
        if (u.Weekly?.UsedPercent is double w)
        {
            sb.Append($"\n주간 한도 {FormatUsagePercent(w)}  ·  초기화 {FormatResetDate(u.Weekly.ResetsAt)}");
            if (showEst)
            {
                var estW = EstimateLimitReached(w, u.Weekly.ResetsAt, TimeSpan.FromDays(7));
                var fmtW = FormatEstimate(estW, useDate: true);
                if (fmtW != null) sb.Append($"  ·  예상 소진 {fmtW}");
            }
        }
        if (u.Monthly?.UsedPercent is double m)
        {
            sb.Append($"\n월간 한도 {FormatUsagePercent(m)}  ·  초기화 {FormatResetDate(u.Monthly.ResetsAt)}");
            if (showEst)
            {
                var estM = EstimateLimitReached(m, u.Monthly.ResetsAt, TimeSpan.FromDays(30));
                var fmtM = FormatEstimate(estM, useDate: true);
                if (fmtM != null) sb.Append($"  ·  예상 소진 {fmtM}");
            }
        }
        if (u.Error != null) sb.Append('\n').Append(u.Error);

        // 초기화권 — codex 전용
        if (u.ResetCredits.Count > 0)
        {
            sb.Append($"\n\n초기화 {u.ResetCredits.Count}회 가능");
            var now = DateTimeOffset.Now;
            foreach (var c in u.ResetCredits)
            {
                var expiry = c.ExpiresAt?.ToLocalTime();
                var expiryStr = expiry != null ? expiry.Value.ToString("M월 d일 HH:mm") : "—";
                var daysStr = expiry == null ? "" : FormatResetCreditExpiry(expiry.Value, now).Text;
                sb.Append($"\n  ~ {expiryStr}  {daysStr}");
            }
        }

        return sb.ToString();
    }

    /// <summary>남은 시간 짧은 표기 ("1시간24분" / "24분" / "곧"). 초기화 시각 없으면 null.</summary>
    private static string? FormatRemainingShort(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not DateTimeOffset r) return null;
        var span = r.ToLocalTime() - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "곧";
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes}m" : $"{span.Minutes}m";
    }

    /// <summary>opencode.ai 로그인 창을 띄우고 성공 시 사용량을 즉시 갱신.</summary>
    private void LoginOpenCode()
    {
        var win = new Views.OpenCodeGoLoginWindow(this);
        win.ShowDialog();
        if (win.Captured) _openCodeGo.RefreshNow();
    }

    /// <summary>ChatGPT(Codex) OAuth 로그인 창을 띄우고 성공 시 사용량을 즉시 갱신.</summary>
    private void LoginCodex()
    {
        var win = new Views.CodexLoginWindow(this);
        win.ShowDialog();
        if (win.Captured)
        {
            _lastCodex = null;
            CodexPanel.Visibility = Visibility.Collapsed;
            UpdateFooterDivider();
            RefreshUsagePanelIfVisible();
            _codex.RefreshNow();
        }
    }

    /// <summary>Claude(claude.ai) OAuth 로그인 창을 띄우고 성공 시 사용량을 즉시 갱신.</summary>
    private void LoginClaude()
    {
        var win = new Views.ClaudeLoginWindow(this);
        win.ShowDialog();
        if (win.Captured)
        {
            _rlApi = null;
            _rlHook = null;
            ReevaluateClaudeUsage();
            _usageApi.RefreshNow();
        }
    }

    /// <summary>마지막 스냅샷으로 각 푸터 사용량 패널 가시성을 다시 평가.</summary>
    public void ApplyFooterUsageVisibility()
    {
        if (_rlMerged != null) ApplyRateLimit(_rlMerged);
        else { RateLimitPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); }
        if (_lastCodex != null) ApplyProviderUsage(_lastCodex); else { CodexPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); }
        if (_lastKimi != null)  ApplyProviderUsage(_lastKimi);  else { KimiPanel.Visibility  = Visibility.Collapsed; UpdateFooterDivider(); }
        if (_lastGo != null)       ApplyProviderUsage(_lastGo);       else { GoPanel.Visibility       = Visibility.Collapsed; UpdateFooterDivider(); }
        if (_lastGrok != null)     ApplyProviderUsage(_lastGrok);     else { GrokPanel.Visibility     = Visibility.Collapsed; UpdateFooterDivider(); }
        if (_lastAntigravity != null) ApplyProviderUsage(_lastAntigravity); else { AntigravityPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); }
        if (_lastDeepSeek != null) ApplyProviderUsage(_lastDeepSeek); else { DeepSeekPanel.Visibility  = Visibility.Collapsed; UpdateFooterDivider(); }
        RefreshUsagePanelIfVisible();
    }

    /// <summary>초기화까지 남은 시간 ("2시간 12분" / "분" / "곧").</summary>
    private static string FormatRemaining(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not DateTimeOffset r) return "—";
        var span = r.ToLocalTime() - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "곧";
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}시간 {span.Minutes}분"
            : $"{span.Minutes}분";
    }

    /// <summary>초기화 일자/시각 ("6월 24일 09:00").</summary>
    private static string FormatResetDate(DateTimeOffset? resetsAt)
        => resetsAt is DateTimeOffset r ? r.ToLocalTime().ToString("M월 d일 HH:mm") : "—";

    private static double DisplayUsagePercent(double usedPercent)
    {
        var used = Math.Clamp(usedPercent, 0, 100);
        return SettingsService.LoadShowRemainingUsage() ? 100 - used : used;
    }

    private static string FormatUsagePercent(double usedPercent)
        => SettingsService.LoadShowRemainingUsage()
            ? $"{DisplayUsagePercent(usedPercent):F0}% 남음"
            : $"{usedPercent:F0}%";

    /// <summary>현재 사용률과 초기화 시각으로, 지금까지의 평균 소비 속도가 유지된다고 가정할 때
    /// 한도(100%)에 도달하는 예상 시각을 계산한다. 충분한 데이터가 없으면 null.</summary>
    private static DateTimeOffset? EstimateLimitReached(double? usedPercent, DateTimeOffset? resetsAt, TimeSpan windowDuration)
    {
        if (usedPercent is not double p || p <= 0) return null;
        if (resetsAt is not DateTimeOffset reset) return null;
        var now = DateTimeOffset.Now;
        if (now >= reset) return null;

        var windowStart = reset - windowDuration;
        var elapsed = now - windowStart;
        if (elapsed <= TimeSpan.Zero) return null;

        var ratePerHour = p / elapsed.TotalHours;
        if (ratePerHour <= 0) return null;

        var remainingPct = 100.0 - p;
        var hoursToLimit = remainingPct / ratePerHour;
        return now.AddHours(hoursToLimit);
    }

    /// <summary>예상 한도 도달 시각을 표시 문자열로 변환. 예측 불가거나 너무 먼 미래면 null.</summary>
    /// <param name="useDate">true 면 날짜("7월 8일")로 표시, false 면 상대 시간("약 2시간 후").</param>
    private static string? FormatEstimate(DateTimeOffset? estimated, bool useDate = false)
    {
        if (estimated is not DateTimeOffset e) return null;
        var now = DateTimeOffset.Now;
        if (e <= now) return null;
        var remaining = e - now;
        if (remaining.TotalDays > 30 && !useDate) return null;
        if (remaining.TotalDays > 365) return null; // 1년 이상은 표시 안 함
        if (useDate && remaining.TotalDays >= 1)
            return "예상 " + e.ToLocalTime().ToString("M월 d일 H시 m분");
        return remaining.TotalDays >= 1
            ? $"예상 {(int)remaining.TotalDays}일 {remaining.Hours}시간 후"
            : remaining.TotalHours >= 1
                ? $"예상 {(int)remaining.TotalHours}시간 {remaining.Minutes}분 후"
                : $"예상 {Math.Max(1, remaining.Minutes)}분 후";
    }

    /// <summary>사용률 구간별 막대 색 — 기본(낮음) / 노랑 / 주황 / 빨강.</summary>
    private System.Windows.Media.Brush RlBrush(double pct)
    {
        if (pct >= 90) return (System.Windows.Media.Brush)FindResource("DangerBrush"); // 빨강
        if (pct >= 75) return (System.Windows.Media.Brush)FindResource("TodayBrush");  // 주황
        if (pct >= 50) return _rlYellow;                                               // 노랑
        return (System.Windows.Media.Brush)FindResource("TextMutedBrush");             // 기본색
    }

    private static readonly System.Windows.Media.Brush _rlYellow = MakeFrozen(0xEA, 0xB3, 0x08);

    private static System.Windows.Media.Brush MakeFrozen(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    /// <summary>성능 모니터 칩(타이틀바) 표시. 토글이 제거되어 항상 표시.</summary>
    private void ApplyPerfMonitorVisibility()
    {
        PerfChipGroup.Visibility = Visibility.Visible;
    }

    private void ApplyPerfSnapshot(Models.PerfSnapshot snap)
    {
        PerfCpuText.Text = $"{snap.CpuPercent:F1}%";
        PerfCpuText.Foreground = PerfBrush(snap.CpuPercent);

        if (snap.MemoryTotalMb > 0)
        {
            var usedGb = snap.MemoryUsedMb / 1024f;
            var pct    = (int)(snap.MemoryUsedMb / snap.MemoryTotalMb * 100);
            PerfRamText.Text = $"{usedGb:F1}G ({pct}%)";
            PerfRamText.Foreground = PerfBrush(snap.MemoryUsedMb / snap.MemoryTotalMb * 100f);
        }
        else
        {
            PerfRamText.Text = "--";
        }
    }

    private System.Windows.Media.Brush PerfBrush(float pct)
    {
        var key = pct >= 80f ? "DangerBrush" : pct >= 50f ? "TodayBrush" : "TextBrush";
        return (System.Windows.Media.Brush)FindResource(key);
    }

    private void PerfChipBtn_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    // ── 좌·우 패널 접기/펼치기 ──────────────────────────────────────────
    // 접을 때 폭을 기억해 두고 컬럼을 0으로, 펼칠 때 복원한다(즉시 — 애니메이션 없음).
    // 접힘 상태는 아이콘 stroke 를 강조(Primary)해 표시.
    private bool   _leftCollapsed;
    private bool   _rightCollapsed;
    private double _sidebarWidth = 262;
    // 1열=220: 헤더의 "프로젝트" 타이틀 + 활성필터 토글 + 아이콘 3개가 잘리지 않는 폭.
    private double _sidebarMinWidth = 220;  // 2열=380. ApplyProjectColumns 가 갱신.
    private double _fileExpWidth = 300;
    private double _fileExpMinWidth;       // 탭 버튼 4개가 온전히 보이는 최소 폭(런타임 측정)

    // ── 반응형: 좁은 창에서 우측 패널을 오버레이 드로어로 ───────────────
    // 창 폭이 이 값 미만이면 우측 패널(탐색기/DIFF)을 레이아웃에서 빼고, 토글 시
    // 중앙 위로 떠오르는 오버레이로 띄운다(devez 즐겨찾기 패널 패턴). 이상이면 도킹.
    private const double NarrowThreshold = 1100;
    private bool? _narrow;                 // null=미초기화. 폭 변화로 모드 전환 감지
    private bool _rightOverlayOpen;        // 좁은 창에서 오버레이가 열려 있는지
    private readonly System.Windows.Media.TranslateTransform _rightT = new();

    // GridSplitter 수동 드래그
    private void PaneSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => UpdatePaneFocusVisual(animate: false);

    private void PaneSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (e.Canceled) return;
        // GridSplitter는 star 컬럼을 드래그해도 star 단위를 유지한 채 값을 재분배한다(합계 보존).
        // 따라서 PaneBCol.Width.Value(star)를 그대로 저장하면 PaneA=1* 기준이 아니라 어긋나고,
        // 재시작마다 오차가 누적돼 결국 클램프 한계까지 드리프트한다.
        // 단위 타입에 의존하지 말고 실제 픽셀 폭으로 "PaneA=1* 기준" 비율(PaneB/PaneA)을 계산한다.
        double aw = PaneACol.ActualWidth, bw = PaneBCol.ActualWidth;
        double star = aw > 0 ? bw / aw : 1.0;
        SettingsService.SaveSplitBStar(star);
    }
    private void SidebarSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_leftCollapsed) return;
    }

    /// <summary>좌측 스플리터 드래그 끝 → 새 폭을 즉시 저장 (재실행 시 복원).</summary>
    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_leftCollapsed) return;
        if (e.Canceled) return;                       // Esc 등으로 드래그 취소 시 무시
        _sidebarWidth = SidebarCol.ActualWidth;
        SettingsService.SaveLeftPanel(_leftCollapsed, _sidebarWidth);
    }

    /// <summary>우측 스플리터 드래그 중 → footer 컬럼 폭을 라이브 동기화.
    /// FileExpCol/FooterFileExpCol 은 SharedSizeGroup(MainFileExp) 으로 묶여 공유폭=멤버 최대 픽셀폭이라,
    /// footer 가 따라오지 않으면 패널이 footer 폭 밑으로 못 줄어든다(좌측은 footer 가 그룹 밖이라 무관).</summary>
    private void FileExpSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_rightCollapsed) return;
        if (FileExpCol.Width.IsAbsolute) FooterFileExpCol.Width = FileExpCol.Width;
    }

    // 세션기록 스플리터 드래그 스냅샷(시작 시점 폭 고정 → GridSplitter 기본 동작과 충돌 방지).
    private double _histDragStartW;     // 드래그 시작 시 History 폭
    private double _histDragMaxW;       // 중앙 * 가 MinWidth 까지 양보 가능한 최대 History 폭
    private double _histDragFileExpW;   // 드래그 시작 시 FileExp 폭(매 프레임 복원)
    private double _histDragAccum;       // 시작 이후 누적 가로 이동량

    private void SessionHistorySplitter_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _histDragStartW = SessionHistoryCol.ActualWidth;
        _histDragFileExpW = _rightCollapsed ? 0
            : (FileExpCol.Width.IsAbsolute ? FileExpCol.Width.Value : FileExpCol.ActualWidth);
        _histDragMaxW = _histDragStartW + System.Math.Max(0, CenterCol.ActualWidth - CenterCol.MinWidth);
        _histDragAccum = 0;
    }

    private void SessionHistorySplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // 이 스플리터는 History 폭만 조정한다. 기본 PreviousAndNext 로는 이전 이웃(FileExp)이 따라
        // 넓어지므로, FileExp 는 시작 폭으로 고정하고 중앙 * 컬럼이 변화를 흡수하게 한다.
        // 드래그 오른쪽(+)=History 축소. 시작 스냅샷 기준 누적 이동량으로 계산해 GridSplitter 기본 변경을 덮어쓴다.
        _histDragAccum += e.HorizontalChange;
        double w = _histDragStartW - _histDragAccum;
        if (w < SessionHistoryCol.MinWidth) w = SessionHistoryCol.MinWidth;
        if (w > _histDragMaxW) w = _histDragMaxW;
        SessionHistoryCol.Width = new GridLength(w);
        FileExpCol.Width = new GridLength(_histDragFileExpW);   // FileExp 가 따라 넓어지지 않게 고정
    }

    /// <summary>우측 스플리터 드래그 끝 → 새 폭을 즉시 저장 (재실행 시 복원).</summary>
    private void FileExpSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_rightCollapsed) return;
        if (e.Canceled) return;
        _fileExpWidth = FileExpCol.ActualWidth;
        SettingsService.SaveRightPanel(_rightCollapsed, _fileExpWidth);
    }

    /// <summary>창 크기 전환 중 중앙 `*` 컬럼이 리사이즈될 때, 보이는 워크스페이스 패널의
    /// 터미널 WebView2 를 스냅샷으로 정지해 reflow 깜빡임을 막는다.</summary>
    private Task FreezeWorkspaceTerminalsAsync(bool stretchCover = false)
        => Task.WhenAll(_panes.Where(p => p.Visibility == Visibility.Visible)
                              .Select(p => p.SuspendTerminalOnlyAsync(anchorTopLeft: true, webCover: true, stretchCover: stretchCover)));

    private void UnfreezeWorkspaceTerminals()
    {
        // freeze 와 동일하게 '보이는' 패널만 reveal — 숨긴 패널에 불필요한 fit/재동기를 걸지 않는다.
        foreach (var p in _panes.Where(p => p.Visibility == Visibility.Visible))
            p.ResumeTerminalOnly(webCover: true);
    }

    private bool _panelCoverBusy; // 패널 토글 커버 진행 중(연타 무시 — 커버/리빌 순서 꼬임 방지)

    /// <summary>애니메이션 없는 즉시 패널 토글을 터미널 webCover 로 감싸 실행.
    /// 커버(캡처) 아래에서 컬럼 폭을 바꾸고, 최종 폭에서 fit·재동기 후 크로스페이드 —
    /// 즉시 토글의 터미널 reflow 깜빡임(claude 포함)을 감춘다. 세션 없으면 freeze/reveal 모두 no-op.</summary>
    private async void RunPanelToggleCovered(Action change)
    {
        if (_panelCoverBusy) return;
        _panelCoverBusy = true;
        try
        {
            await FreezeWorkspaceTerminalsAsync();
            change();
            UnfreezeWorkspaceTerminals();
        }
        finally { _panelCoverBusy = false; }
    }

    // ── 하단 터미널 패널 (에이전트 미연결 pwsh) ─────────────────────────
    private const string ShellRoomId = "devezcode-shell-terminal";
    private bool _shellPanelOpen;
    private bool _shellPanelBusy; // 토글 연타/exit 경합 무시

    private async void ShellTerminalBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_shellPanelBusy) return;
        _shellPanelBusy = true;
        try { await ToggleShellPanelAsync(!_shellPanelOpen); }
        catch (Exception ex) { DiagLog.Write($"shell panel toggle failed: {ex.Message}"); }
        finally { _shellPanelBusy = false; }
    }

    /// <summary>하단 터미널 패널 즉시 토글(사이드 패널과 동일 방식 — 애니메이션 없음).
    /// 워크스페이스 터미널은 webCover 로 정지(리사이즈 경로 — .knowledge/webview2-airspace 문서),
    /// 패널 자신은 지연 생성(첫 열기에 ShowTerminal). 열림 시 세션영역 하단 보더를 함께 토글해
    /// 하단 보더 + 4px 채널 + 패널 상단 보더의 더블라인 채널(devez 관례)을 만든다.</summary>
    private async Task ToggleShellPanelAsync(bool open)
    {
        _shellPanelOpen = open;
        await FreezeWorkspaceTerminalsAsync();
        try
        {
            if (open)
            {
                SettingsService.SaveAgentForRoom(ShellRoomId, "shell"); // LaunchSession 의 shell 분기로 라우팅
                ShellTerminalPanel.Visibility = Visibility.Visible;
                ShellPanelSplitter.Visibility = Visibility.Visible;
                ShellTerminal.ShowTerminal(ShellRoomId); // 살아있으면 재사용, 없으면 새 pwsh (지연 생성+유지)
                // 저장 높이가 현재 창보다 크면 클램프 (Row0 MinHeight=220 + 스플리터 4px 확보)
                double maxH = Math.Max(120, CenterSplit.ActualHeight - 220 - 4);
                ShellPanelRow.Height = new GridLength(Math.Min(SettingsService.LoadShellTerminalHeight(), maxH));
                ShellPanelRow.MinHeight = 120; // 스플리터 드래그 하한
            }
            else
            {
                ShellPanelRow.MinHeight = 0; // MinHeight 가 남으면 Height=0 이 클램프되어 안 닫힌다
                ShellPanelRow.Height = new GridLength(0);
                ShellTerminalPanel.Visibility = Visibility.Collapsed; // HwndHost 는 Collapsed 로만 숨김
                ShellPanelSplitter.Visibility = Visibility.Collapsed;
            }
            // 세션영역(패널 위 콘텐츠) 하단 1px 보더 — 패널이 열렸을 때만 (airspace 상 오버레이 불가, 레이아웃 보더로 처리)
            var paneBottom = new Thickness(0, 0, 0, open ? 1 : 0);
            PaneA.BorderThickness = paneBottom;
            PaneB.BorderThickness = paneBottom;
        }
        finally
        {
            UpdateLayout(); // webCover resume 전 최종 폭 확정 (expectWidth 정확성)
            UnfreezeWorkspaceTerminals();
            UpdateShellToggleVisual();
            if (open) ShellTerminal.FocusTerminal(); // 열리면 바로 입력 가능 (pageReady 전이면 내부 보류 후 적용)
        }
    }

    /// <summary>토글 버튼 아이콘 색 — 열림 = PrimaryBrush, 닫힘 = TextMutedBrush (테마 추종).</summary>
    private void UpdateShellToggleVisual()
        => ShellTerminalBtnIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            _shellPanelOpen ? "PrimaryBrush" : "TextMutedBrush");

    /// <summary>스플리터 드래그 끝 → 새 높이 저장 (재실행 시 복원).</summary>
    private void ShellPanelSplitter_DragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (e.Canceled || !_shellPanelOpen) return;
        SettingsService.SaveShellTerminalHeight(ShellPanelRow.ActualHeight);
    }

    /// <summary>타이틀 "DevezCode" 세 번 클릭 → GPU/소프트웨어 렌더 즉시 전환(설정 영속, UI 알림 없음).</summary>
    private void TitleLogo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 3) return;
        e.Handled = true;
        App.ToggleRenderMode();
    }

    /// <summary>
    /// 앱 최상단 타이틀바에 외부 Explorer 파일을 드롭하면 현재 포커스 패널의 파일 탭으로 연다.
    /// 타이틀바 밖의 드롭은 처리하지 않아 터미널의 기존 경로 입력 동작을 그대로 둔다.
    /// </summary>
    private void TitleBarFileOpen_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool canDrop = _focusedPane.ActiveProject != null && GetTitleBarDroppedFiles(e).Length > 0;
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void TitleBarFileOpen_PreviewDrop(object sender, DragEventArgs e)
    {
        var files = GetTitleBarDroppedFiles(e);
        e.Handled = true;
        _focusedPane.DismissFileDropOverlay();
        if (_focusedPane.ActiveProject == null || files.Length == 0) return;

        foreach (var path in files)
            _focusedPane.OpenFileAsTab(path);
    }

    private static string[] GetTitleBarDroppedFiles(DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
                e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
                return [];

            return paths
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void LeftPanelBtn_Click(object sender, RoutedEventArgs e) => RunPanelToggleCovered(ToggleLeftPanel);

    private void ToggleLeftPanel()
    {
        if (_leftCollapsed)
        {
            _leftCollapsed = false;
            Sidebar.Visibility = Visibility.Visible;
            SetMinWidth(_sidebarMinWidth, SidebarCol, FooterSidebarCol);
            SidebarCol.Width = new GridLength(_sidebarWidth);
            FooterSidebarCol.Width = new GridLength(_sidebarWidth);
            SetSplitterWidth(SidebarSplitterCol, 4);
        }
        else
        {
            _leftCollapsed = true;
            _sidebarWidth = SidebarCol.Width.IsAbsolute ? SidebarCol.Width.Value : SidebarCol.ActualWidth;
            SetMinWidth(0, SidebarCol, FooterSidebarCol);
            SidebarCol.Width = new GridLength(0);
            FooterSidebarCol.Width = new GridLength(0);
            SetSplitterWidth(SidebarSplitterCol, 0);
            Sidebar.Visibility = Visibility.Collapsed;
        }
        SettingsService.SaveLeftPanel(_leftCollapsed, _sidebarWidth);
        UpdatePanelToggleVisual();
    }

    private void RightPanelBtn_Click(object sender, RoutedEventArgs e)
    {
        // 좁은 창: 도킹 대신 오버레이 드로어를 토글한다(자체 스냅샷 정지 경로 사용 — 커버 불필요).
        if (_narrow == true)
        {
            if (_rightOverlayOpen) CloseRightOverlay();
            else _ = OpenRightOverlay();
            return;
        }
        RunPanelToggleCovered(ToggleRightPanel);
    }

    private void ToggleRightPanel()
    {
        if (_rightCollapsed)
        {
            _rightCollapsed = false;
            FileExplorer.Visibility = Visibility.Visible;
            // 우측 완료기록/사용량이 열려 있으면 그만큼 자리를 비워, 파일탐색기를 남는 폭까지만 편다.
            double openTarget = Math.Min(_fileExpWidth, ComputeMaxFileExpWidth());
            FileExpCol.Width = new GridLength(openTarget);
            FooterFileExpCol.Width = new GridLength(openTarget);
            SetSplitterWidth(FileExpSplitterCol, 4, FooterFileExpSplitterCol);
            if (_fileExpMinWidth > 0) SetMinWidth(_fileExpMinWidth, FileExpCol, FooterFileExpCol);
            UpdateUsageSidebarBorder();
        }
        else
        {
            _rightCollapsed = true;
            _fileExpWidth = FileExpCol.Width.IsAbsolute ? FileExpCol.Width.Value : FileExpCol.ActualWidth;
            SetMinWidth(0, FileExpCol, FooterFileExpCol);
            FileExpCol.Width = new GridLength(0);
            FooterFileExpCol.Width = new GridLength(0);
            SetSplitterWidth(FileExpSplitterCol, 0, FooterFileExpSplitterCol);
            FileExplorer.Visibility = Visibility.Collapsed;
            UpdateUsageSidebarBorder();
        }
        SettingsService.SaveRightPanel(_rightCollapsed, _fileExpWidth);
        UpdatePanelToggleVisual();
    }

    private void SessionHistorySplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (e.Canceled) return;
        if (SessionHistoryCol.Width.IsAbsolute && SessionHistoryCol.Width.Value > 0)
            SettingsService.SaveSessionHistoryWidth(SessionHistoryCol.Width.Value);
    }

    // ── 최우측 계정 사용량 사이드바 토글 ────────────────────────────────
    private const double UsagePanelWidth = 218; // 좌여백16+라벨34+막대(6+102)+%여백10+"100%"≈32 → 우여백 16
    private const int MaxSessionDoneRecords = 30;
    private bool _usageOpen;
    private bool _sessionHistoryOpen;

    private void UsagePanelBtn_Click(object sender, RoutedEventArgs e)
        => RunPanelToggleCovered(() => SetUsagePanelOpen(!_usageOpen, persist: true));

    private void SessionHistoryPanelBtn_Click(object sender, RoutedEventArgs e)
        => RunPanelToggleCovered(() => SetSessionHistoryPanelOpen(!_sessionHistoryOpen, persist: true));

    /// <summary>F1~F4 — 패널 토글 단축키.</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && Sidebar.HasSessionMultiSelection)
        {
            Sidebar.ClearSessionMultiSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.F1)
        {
            LeftPanelBtn_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.F2)
        {
            RightPanelBtn_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.F3)
        {
            RunPanelToggleCovered(() => SetSessionHistoryPanelOpen(!_sessionHistoryOpen, persist: true));
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.F4)
        {
            RunPanelToggleCovered(() => SetUsagePanelOpen(!_usageOpen, persist: true));
            e.Handled = true;
            return;
        }
        // [테스트] Ctrl+Shift+U 를 짧게 두 번 → 가짜 업데이트(사이드바 버튼 + 팝업 진행률) 확인.
        if (e.Key == System.Windows.Input.Key.U
            && (System.Windows.Input.Keyboard.Modifiers
                & (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
               == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
        {
            var now = DateTime.UtcNow;
            if ((now - _lastTestUpdateKeyUtc).TotalMilliseconds <= 700)
            {
                _lastTestUpdateKeyUtc = DateTime.MinValue;
                TriggerTestUpdate();
            }
            else _lastTestUpdateKeyUtc = now;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>최우측 사용량 사이드바를 즉시 펼치거나 접는다.</summary>
    private void SetUsagePanelOpen(bool open, bool persist)
    {
        _usageOpen = open;
        if (open) SetSidebarUsageCards(BuildUsageCards()); // 펼칠 때 최신 스냅샷으로 카드 빌드

        UsageCol.Width = new GridLength(open ? UsagePanelWidth : 0);

        if (persist) SettingsService.SaveUsagePanelOpen(open);
        if (open) ClampFileExpToFit(); // 사용량 패널 자리 확보를 위해 파일탐색기를 남는 폭까지 줄임
        UpdatePanelToggleVisual();
        UpdateUsageSidebarBorder();
    }

    /// <summary>세션 완료 기록 사이드바를 즉시 펼치거나 접는다.</summary>
    private void SetSessionHistoryPanelOpen(bool open, bool persist)
    {
        _sessionHistoryOpen = open;
        double targetWidth = open ? SettingsService.LoadSessionHistoryWidth() : 0;

        // 닫힘 시 스플리터를 숨겨 잡아끌어 다시 펼치는 것을 막는다(폭 0 이어도 히트 가능 방지).
        // 4px 채널(세퍼레이터/여백)도 접을 때 0 으로 줄여 우측 끝에 빈 띠가 남지 않게 한다.
        SessionHistorySplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SessionHistorySplitterCol.Width = new GridLength(open ? 4 : 0);
        // 파일탐색기↔세션기록 채널을 양쪽 1px 라인(더블라인)으로 만든다 — 세션기록이 열리면
        // 파일탐색기에 우측 보더도 그린다(세션기록 좌측 보더 + 4px 간격 + 파일탐색기 우측 보더).
        // 닫히면 파일탐색기는 다시 좌측 보더만 두어 우측 끝이 깔끔하게 끝난다.
        FileExplorer.BorderThickness = new Thickness(1, 0, open ? 1 : 0, 0);

        // 닫을 때 MinWidth(150)가 남아 폭 0 으로 줘도 완전히 안 닫히므로 닫힘 시 0으로 내린다.
        // 드래그 클램프(MinWidth 사용)는 열린 상태에서만 동작하므로 리사이즈 동작엔 영향 없음.
        if (!open) SessionHistoryCol.MinWidth = 0;

        SessionHistoryCol.Width = new GridLength(targetWidth);
        if (open) SessionHistoryCol.MinWidth = 150;

        if (persist) SettingsService.SaveSessionHistoryPanelOpen(open);
        if (open) ClampFileExpToFit(); // 완료기록 패널 자리 확보를 위해 파일탐색기를 남는 폭까지 줄임
        UpdatePanelToggleVisual();
        UpdateUsageSidebarBorder();
    }

    /// <summary>설정에 저장된 사이드 패널 뷰 전환 버튼 표시 여부를 우측 패널에 반영한다.</summary>
    public void ApplySidePanelButtonVisibility()
    {
        FileExplorer.ApplyTabButtonVisibility();
    }

    /// <summary>설정(프로젝트 정보 헤더 숨기기)을 모든 워크스페이스 패널의 메타바에 반영.</summary>
    public void ApplyProjectInfoHeaderVisibility()
    {
        foreach (var pane in _panes) pane.ApplyProjectInfoHeaderVisibility();
    }

    /// <summary>프로젝트 목록 열 수(1/2) 적용 — 사이드바 레이아웃 + 좌측 패널 최소/현재 폭.
    /// 2열이면 최소너비를 2배(380)로 올리고, 현재 폭이 더 좁으면 확장한다.</summary>
    public void ApplyProjectColumns(int cols)
    {
        cols = cols == 2 ? 2 : 1;
        _sidebarMinWidth = cols == 2 ? 380 : 220;
        Sidebar.ApplyProjectColumns(cols);
        if (_leftCollapsed) return; // 접힌 상태에선 폭 0 유지(펼칠 때 _sidebarMinWidth 적용됨)

        SetMinWidth(_sidebarMinWidth, SidebarCol, FooterSidebarCol);
        if (_sidebarWidth < _sidebarMinWidth)
        {
            _sidebarWidth = _sidebarMinWidth;
            SidebarCol.Width = new GridLength(_sidebarWidth);
            FooterSidebarCol.Width = new GridLength(_sidebarWidth);
            SettingsService.SaveLeftPanel(_leftCollapsed, _sidebarWidth);
        }
    }

    private const int VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>전역 단축키(한자+방향키)로 탭 전환 시 창을 앞으로. devez Alt 트릭으로 포그라운드 잠금 우회.</summary>
    private void BringToForegroundFromHotkey()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            // 이미 우리 창이 포그라운드면 아무것도 하지 않는다. Alt 트릭(VK_MENU 탭)이 "Alt 단독 누름"으로
            // 해석돼 메뉴 모드에 진입하면 이후 ↑/↓ 에 시스템 메뉴가 열리고, Activate() 가 WebView2(터미널)
            // 포커스를 빼앗아 한글 조합이 창 좌상단 기본 IME 위치에 뜬다.
            if (WindowState != WindowState.Minimized && GetAncestor(GetForegroundWindow(), GA_ROOT) == h)
                return;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            SetForegroundWindow(h);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Activate();
        }
        catch { }
    }

    private const uint GA_ROOT = 2;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    /// <summary>탭 버튼 4개가 온전히 보이는 폭을 측정해 우측 패널(확장 상태)의 최소 폭으로 적용.
    /// 접힘/오버레이(좁은 창) 상태에서는 폭 0 유지를 위해 적용하지 않는다.</summary>
    private void ApplyFileExpMinWidth()
    {
        _fileExpMinWidth = 190; // 좌측 패널과 동일 최소 폭
        if (!_rightCollapsed && _narrow != true)
            SetMinWidth(_fileExpMinWidth, FileExpCol, FooterFileExpCol);
    }

    // 공유 그룹 멤버들의 MinWidth 를 한꺼번에 설정. 미러 컬럼에 MinWidth 가 남아 있으면
    // 폭을 0 으로 줘도 공유 그룹이 최소폭으로 버텨 완전히 안 접힌다.
    private static void SetMinWidth(double min, params ColumnDefinition[] cols)
    {
        foreach (var c in cols) c.MinWidth = min;
    }

    // 공유 그룹(MainSidebarSplitter/MainFileExpSplitter) 멤버 스플리터 폭을 한꺼번에 설정.
    // 푸터에서 FooterSidebarSplitterCol 이 제거되어 좌측은 미러 없음 — mirror1/2 옵셔널.
    private static void SetSplitterWidth(ColumnDefinition col, double w,
        ColumnDefinition? mirror1 = null, ColumnDefinition? mirror2 = null)
    {
        var gl = new GridLength(w);
        col.Width = gl;
        if (mirror1 != null) mirror1.Width = gl;
        if (mirror2 != null) mirror2.Width = gl;
    }

    /// <summary>저장된 패널 접힘 상태를 시작 시 즉시(애니메이션 없이) 복원한다.</summary>
    private void RestorePanelStates()
    {
        _sidebarWidth = SettingsService.LoadSidebarWidth();
        _fileExpWidth = SettingsService.LoadFileExpWidth();

        if (SettingsService.LoadLeftPanelCollapsed())
        {
            _leftCollapsed = true;
            SetMinWidth(0, SidebarCol, FooterSidebarCol);
            SidebarCol.Width = new GridLength(0);
            FooterSidebarCol.Width = new GridLength(0);
            SetSplitterWidth(SidebarSplitterCol, 0);
            Sidebar.Visibility = Visibility.Collapsed;
        }
        else
        {
            SidebarCol.Width = new GridLength(_sidebarWidth);
            FooterSidebarCol.Width = new GridLength(_sidebarWidth);
        }
        if (SettingsService.LoadRightPanelCollapsed())
        {
            _rightCollapsed = true;
            SetMinWidth(0, FileExpCol, FooterFileExpCol);
            FileExpCol.Width = new GridLength(0);
            FooterFileExpCol.Width = new GridLength(0);
            SetSplitterWidth(FileExpSplitterCol, 0, FooterFileExpSplitterCol);
            FileExplorer.Visibility = Visibility.Collapsed;
        }
        else
        {
            FileExpCol.Width = new GridLength(_fileExpWidth);
            FooterFileExpCol.Width = new GridLength(_fileExpWidth);
        }
        SetUsagePanelOpen(SettingsService.LoadUsagePanelOpen(), persist: false); // 사용량 사이드바 상태 복원
        SetSessionHistoryPanelOpen(SettingsService.LoadSessionHistoryPanelOpen(), persist: false); // 완료 기록 사이드바 상태 복원
        UpdatePanelToggleVisual();
        ApplyProjectColumns(SettingsService.LoadProjectColumns()); // 저장된 열 수 복원(최소/현재 폭 반영)
    }

    private void UpdatePanelToggleVisual()
    {
        var muted   = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        var primary = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
        // 좌·우 패널 토글은 접힘 상태여도 테마색 강조하지 않는다 — 항상 muted.
        LeftPanelIcon.Stroke  = muted;
        bool rightHidden = _narrow == true ? !_rightOverlayOpen : _rightCollapsed;
        RightPanelIcon.Stroke = muted;
        // 사용량 사이드바는 '펼침' 상태일 때만 강조(열려 있음 표시).
        UsagePanelIcon.Stroke = _usageOpen ? primary : muted;
        SessionHistoryPanelIcon.Stroke = _sessionHistoryOpen ? primary : muted;
        // 우측 패널이 접혔으면 파일탐색기 스플리터 비활성화 — 빈(폭 0) 패널이 드래그로 열리는 것 방지.
        FileExpSplitter.IsEnabled = !rightHidden;
    }

    /// <summary>파일탐색기가 접히면 그 사이 채널이 사라져 중앙 패널 우측 보더와 가장 안쪽 보조
    /// 패널의 좌측 보더가 맞붙어 2중선이 된다 → 접힘 시 그 패널의 좌측 보더 제거.
    /// 현재 순서: [FileExplorer] col4 → [SessionHistory] col5 → [Usage] col6 이므로
    /// 접힘 시 열려 있는 첫 번째 보조 패널의 좌측 보더를 제거한다.</summary>
    private void UpdateUsageSidebarBorder()
    {
        UpdateCenterRightBorder();
        bool rightHidden = _narrow == true ? !_rightOverlayOpen : _rightCollapsed;
        if (!rightHidden)
        {
            SessionHistorySidebar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            UsageSidebar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            return;
        }

        // 우측 패널 접힘: 열린 첫 번째 패널의 좌측 보더를 투명하게(중앙 패널 우측 보더와 2중선 방지).
        if (_sessionHistoryOpen)
        {
            // SessionHistory 는 자체 좌측 보더를 항상 유지(중앙 패널과의 4px 채널 = 앱 공통 더블라인 룩).
            // 투명 처리하면 세로 세퍼레이터가 사라지므로 LineBrush 고정.
            SessionHistorySidebar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            UsageSidebar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        }
        else if (_usageOpen)
        {
            // Usage(col6)가 중앙과 맞닿음(SessionHistory는 접힘).
            UsageSidebar.BorderBrush = System.Windows.Media.Brushes.Transparent;
            SessionHistorySidebar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        }
        else
        {
            // 둘 다 접힘 — 무관.
            SessionHistorySidebar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            UsageSidebar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        }

    }

    /// <summary>우측에 도킹된 패널(파일탐색기/세션기록/사용량)이 하나도 없으면 최우측 중앙
    /// 패널의 우측 보더(스플리터 좌측 세퍼레이터)를 꺼서 떠 있는 세로선을 없앤다. 하나라도
    /// 열려 있으면 채널 형성을 위해 켠다. narrow 에선 파일탐색기가 오버레이라 도킹으로 안 친다.</summary>
    private void UpdateCenterRightBorder()
    {
        bool fileExpDockedOpen = _narrow == true ? false : !_rightCollapsed;
        bool anyRightOpen = fileExpDockedOpen || _sessionHistoryOpen || _usageOpen;
        var rightmost = _splitActive ? RightPane : PaneA;
        foreach (var p in _panes) p.SetRightChannelBorder(true);
        rightmost.SetRightChannelBorder(anyRightOpen);
    }

    // ── 자동 업데이트 ──────────────────────────────────────────────
    // DevezCode 는 Supabase 가 없어 실시간 푸시 대신 폴링으로 근실시간 알림을 낸다:
    //   시작 직후 1회 + 30분 주기 + 창 활성화 시(10분 스로틀).
    private UpdateInfo? _pendingUpdate;
    private bool _updateInProgress;
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    private void InitUpdates()
    {
        // 자동 교체 실패로 임시 exe 가 재실행된 경우: 수동 재설치 안내 후 종료 유도.
        if (App.UpdateFailedRelaunch)
        {
            ShowUpdateFailedNotice();
            return;
        }

        _ = CheckUpdateAsync();

        _updateTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMinutes(30) };
        _updateTimer.Tick += (_, _) => _ = CheckUpdateAsync();
        _updateTimer.Start();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 창에 다시 포커스가 올 때마다 확인하되 10분 스로틀(R2 과다 조회 방지).
        if ((DateTime.UtcNow - _lastUpdateCheckUtc) > TimeSpan.FromMinutes(10))
            _ = CheckUpdateAsync();
    }

    // [테스트] Ctrl+Shift+U 로 켜지는 가짜 업데이트 모드. OpenUpdatePopup 에서 소비 후 해제.
    private bool _testUpdateMode;
    private DateTime _lastTestUpdateKeyUtc = DateTime.MinValue; // 더블 Ctrl+Shift+U 감지

    /// <summary>[테스트] 가짜 업데이트를 감지한 것처럼 사이드바 버튼을 띄운다.
    /// 버튼(또는 긴급 팝업) → OpenUpdatePopup 에서 실제 다운로드 대신 진행률만 시뮬레이션.</summary>
    private void TriggerTestUpdate()
    {
        if (_updateInProgress) return;
        _testUpdateMode = true;
        var testReleases = new[]
        {
            new UpdateReleaseNote("9.9.9", string.Join("\n", Enumerable.Range(1, 3).Select(i => $"업데이트 노트 테스트 {i:00}"))),
        };
        _pendingUpdate = new UpdateInfo(
            Version: "9.9.9",
            Url: "https://example.com/test",
            Notes: string.Join("\n", Enumerable.Range(1, 3).Select(i => $"업데이트 노트 테스트 {i:00}")),
            Releases: testReleases);
        Sidebar.ShowUpdateButton(_pendingUpdate.Version);
        // 사이드바 하단 업데이트 버튼을 클릭하면 OpenUpdatePopup → 팝업 진행률까지 확인된다.
    }

    /// <summary>[테스트] 0→100% 진행률을 천천히 보고하는 가짜 다운로드(실제 파일 없음).</summary>
    private static async Task FakeUpdateDownloadAsync(IProgress<double> progress)
    {
        for (int i = 0; i <= 25; i++)
        {
            progress.Report(i / 25.0);
            await Task.Delay(120);
        }
    }

    private async Task CheckUpdateAsync()
    {
        if (_updateInProgress) return;
        _lastUpdateCheckUtc = DateTime.UtcNow;

        var info = await UpdateService.CheckAsync();
        if (info is null || _updateInProgress) return;
        _pendingUpdate = info;
        _testUpdateMode = false; // 실제 업데이트가 확정되면 테스트 모드 해제(가짜 다운로드 방지)

        // devez 정합: 강제 팝업 대신 좌측 패널 하단에 업데이트 버튼을 띄운다(비강제).
        Sidebar.ShowUpdateButton(info.Version);
        // 긴급 업데이트는 즉시 팝업까지 띄워 주의를 끈다.
        if (info.IsUrgent) OpenUpdatePopup();
    }

    /// <summary>업데이트 노트 팝업 → "업데이트" 선택 시 설치. 사이드바 버튼/긴급 감지에서 호출.</summary>
    private void OpenUpdatePopup()
    {
        var info = _pendingUpdate;
        if (info is null || _updateInProgress) return;

        var releases = UpdateService.GetReleaseNotesSince(UpdateService.CurrentVersion, info);
        var showVersionHeadings = info.Releases is { Count: > 0 };
        var formattedReleases = releases.Select(release =>
        {
            var lines = string.Join("\n",
                release.Notes.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => "· " + l.Trim().TrimStart('•', ' ', '\t').Trim())
                    .Where(l => l.Length > 2));
            if (string.IsNullOrWhiteSpace(lines)) return "";
            return showVersionHeadings ? $"v{release.Version}\n{lines}" : lines;
        }).Where(text => text.Length > 0);
        var formattedNotes = string.Join("\n\n", formattedReleases);
        var noteLines = formattedNotes.Length == 0 ? "" : "\n\n" + formattedNotes;

        var prefix = info.IsUrgent ? "[긴급] " : "";

        _updateInProgress = true;
        Sidebar.HideUpdateButton(); // 진행 중에는 사이드바 버튼 숨김

        // [테스트] Ctrl+Shift+U 로 띄운 가짜 업데이트면 실제 다운로드 대신 진행률만 시뮬레이션한다.
        bool test = _testUpdateMode;
        _testUpdateMode = false;
        Func<IProgress<double>, Task> download = test
            ? FakeUpdateDownloadAsync
            : progress => UpdateService.DownloadAndRelaunchAsync(info, progress);

        // 노트 확인 → "업데이트" 클릭 시 같은 팝업 안에서 진행률을 표시하며 다운로드한다.
        // 성공 시 앱이 종료·재실행되므로 ShowUpdate 는 반환되지 않는다(취소/실패만 내려온다).
        var outcome = ConfirmDialog.ShowUpdate(
            $"{prefix}새 버전 {info.Version}",
            $"새 버전이 있습니다. 지금 업데이트할까요?{noteLines}",
            download,
            okLabel: "업데이트");

        _updateInProgress = false;
        if (test) { _pendingUpdate = null; Sidebar.HideUpdateButton(); return; } // 테스트: 뒷정리만
        Sidebar.ShowUpdateButton(info.Version); // 취소·실패 → 버튼 복원

        if (outcome == UpdateOutcome.ElevationDenied)
        {
            // Program Files 설치본은 exe 교체에 승격이 필요하다. 사용자가 UAC 를 취소한 것이므로
            // 다운로드 안내가 아니라 권한 승인 후 재시도를 안내한다(브라우저 재다운로드는 해결책이 아님).
            if (ConfirmDialog.Show(
                    "관리자 권한 필요",
                    "업데이트 적용에 관리자 권한이 필요합니다.\n" +
                    "다시 시도한 뒤 표시되는 권한 요청 창에서 \"예\"를 선택해 주세요.",
                    okLabel: "다시 시도", iconKey: "IconShield"))
                OpenUpdatePopup();
            return;
        }

        if (outcome == UpdateOutcome.Failed)
        {
            // 자동 업데이트 실패 → 브라우저로 직접 다운로드 유도.
            if (ConfirmDialog.Show(
                    "업데이트 오류",
                    "자동 업데이트에 실패했습니다.\n브라우저에서 직접 다운로드하시겠습니까?",
                    okLabel: "다운로드", iconKey: "IconDownload"))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(info.Url) { UseShellExecute = true });
        }
    }


    private void ShowUpdateFailedNotice()
    {
        if (ConfirmDialog.Show(
                "업데이트 미적용",
                "자동 업데이트가 보안 프로그램·권한 문제로 적용되지 않았습니다.\n" +
                "설치 파일을 받아 수동으로 다시 설치해 주세요.",
                okLabel: "설치 파일 받기", iconKey: "IconDownload"))
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(UpdateService.InstallerUrl) { UseShellExecute = true });
    }

    // ── 프로젝트 ──────────────────────────────────────────────────
    private void AddProject()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "프로젝트 디렉터리 선택" };
        if (picker.ShowDialog(this) != true) return;
        var path = picker.FolderName;
        // 같은 경로 중복 등록 허용 — 경로키 설정(작업큐/브라우저URL)은 중복끼리 공유.
        // 복원/조회는 전역 유일한 세션ID 기준이라 충돌 없음.
        var proj = ProjectItem.FromPath(path);
        // 프로젝트만 등록하고 세션은 만들지 않는다. (세션은 사용자가 직접 추가)
        _projects.Add(proj);
        WorkspaceStore.Save(_projects);
        SelectProject(proj);
        UpdateStatus();
    }

    /// <summary>프로젝트 메뉴 "바로가기 추가" — devez 스타일 팝업(이름/파일/관리자 실행)으로 등록(영속).</summary>
    private void AddProjectFile(ProjectItem proj)
    {
        var initialDir = !string.IsNullOrEmpty(proj.Path) && System.IO.Directory.Exists(proj.Path) ? proj.Path : null;
        var r = ShortcutDialog.ShowCreate(this, initialDir);
        if (r == null) return;
        proj.AddShortcut(r.Path, r.Name, r.RunAsAdmin);
        WorkspaceStore.Save(_projects);
    }

    /// <summary>바로가기 이름 변경 — devez PromptDialog 정합. 표시명 갱신 후 저장.</summary>
    private void RenameProjectFile(ProjectFile file)
    {
        var name = PromptDialog.Show("바로가기 이름 변경", "새 이름을 입력하세요.",
                                     defaultValue: file.DisplayName, maxLength: 60);
        if (string.IsNullOrWhiteSpace(name) || name == file.Name) return;
        file.Name = name;
        WorkspaceStore.Save(_projects);
    }

    /// <summary>바로가기 제거 — 해당 프로젝트 Files 에서 삭제 후 영속 저장.</summary>
    private void RemoveProjectFile(ProjectFile file)
    {
        var proj = _projects.FirstOrDefault(p => p.Files.Contains(file));
        if (proj == null) return;
        proj.Files.Remove(file);
        WorkspaceStore.Save(_projects);
    }

    /// <summary>등록된 바로가기 클릭 — 대상 파일을 외부 실행(관리자 플래그면 runas 로 UAC 승격).</summary>
    private void OpenProjectFile(ProjectFile file)
    {
        if (string.IsNullOrEmpty(file.FilePath) || !System.IO.File.Exists(file.FilePath))
        {
            ConfirmDialog.Alert("바로가기", "대상 파일을 찾을 수 없습니다.\n경로가 이동/삭제됐는지 확인해 주세요.");
            return;
        }
        try
        {
            System.Diagnostics.ProcessStartInfo psi;
            if (file.RunAsAdmin)
            {
                // runas verb 는 실행파일에만 등록됨 — .sln/.docx 등 문서 타입엔 없어서 직접 승격 불가.
                // 승격된 cmd 를 경유해 start 로 열면 연결 프로그램이 관리자 권한을 상속받는다.
                psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c start \"\" \"{file.FilePath}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                };
            }
            else
            {
                psi = new System.Diagnostics.ProcessStartInfo(file.FilePath) { UseShellExecute = true };
            }
            System.Diagnostics.Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 사용자가 UAC 를 취소(1223)하거나 실행 실패 — 조용히 무시.
        }
        catch (Exception ex)
        {
            ConfirmDialog.Alert("바로가기", $"실행에 실패했습니다.\n{ex.Message}");
        }
    }

    // ── 워크스페이스 패널 셸 연동 ────────────────────────────────────
    /// <summary>패널을 셸에 배선 — 공유 컬렉션/서비스 주입 + 이벤트 구독.</summary>
    private void SetupPane(WorkspacePaneView pane)
    {
        pane.Projects = _projects;
        pane.ArchivedProjects = _archivedProjects;
        pane.ModelEffort = _modelEffort;
        pane.AgentLastMsg = _agentLastMsg;
        // 다른 패널이 활성으로 보여주는 세션은 이 패널의 프리로드에서 제외한다. 같은 프로젝트를 분할하면
        // 양쪽이 같은 세션 집합을 프리로드하는데, 활성 세션을 다른 패널이 기본(전체) 폭으로 먼저 만들어버리면
        // 보여주는 패널이 최종 폭으로 reattach 하며 리플로우돼 깨진다. 활성 세션은 그 패널이 최종 폭에서 생성하게 둔다.
        pane.IsSessionActiveElsewhere = s => _panes.Any(p => !ReferenceEquals(p, pane) && ReferenceEquals(p.ActiveSession, s));
        pane.FocusRequested += OnPaneFocusRequested;
        pane.ActiveChanged += OnPaneActiveChanged;
        pane.SplitToggleRequested += OnPaneSplitToggle;
        pane.SplitViewRequested += OnPaneSplitViewRequested;
        pane.ExportSessionRequested += ExportSession;
        pane.ExternalSessionRequested += OpenSessionInExternalTerminal;
        pane.ReturnExternalSessionRequested += ReturnSessionFromExternal;
        pane.ToggleSessionLockRequested += ToggleSessionLock;
        pane.HideSessionRequested += HideSessionFromSidebar;
        pane.HideStopFinished += OnPaneHideStopFinished;
        pane.StopTrackingSessionRequested += StopTrackingSession;
        pane.DeleteSessionRequested += DeleteSession;
        pane.TabDragHoverMoved = OnTabDragHoverMoved;
        pane.TryCommitCrossDrop = OnTryCommitCrossTabDrop;
        pane.SetPanesTabDragActive = on => { LeftPane.SetTabDragActive(on); RightPane.SetTabDragActive(on); };
        pane.RevealPrepared += OnPaneRevealPrepared;
        pane.IsolatedTabOpened += OnPaneIsolatedTabOpened;
        pane.FileTabCloseRequested += OnFileTabCloseRequested;
        pane.BrowserTabCloseRequested += OnBrowserTabCloseRequested;
        pane.SessionTerminalReady += id =>
        {
            _sessionCommandInbox.NotifyTerminalReady(id);
            MarkSessionActivity(id);
        };
        pane.SessionActivity += MarkSessionActivity;
        TerminalSessionManager.Instance.AgentModelCatalogRefreshRequested += pane.NotifyAgentModelCatalogRefreshRequested;
        _panes.Add(pane);
    }

    /// <summary>파일 탭 닫기(탭 X·에디터 버튼·컨텍스트 메뉴)를 '실제로 표시(활성) 중인 패널'로 라우팅한다.
    /// 에디터 CloseRequested 는 탭을 만든 패널에 묶여 있어, 그 탭이 분할로 다른 패널에 옮겨져 있으면
    /// 생성 패널에서 닫아도 표시 패널이 갱신되지 않는다(이웃 세션 미선택·타이틀 잔류). 활성 패널 우선,
    /// 없으면 그 탭을 보여주는 패널, 그것도 없으면 포커스 패널에서 닫는다.</summary>
    private void OnFileTabCloseRequested(FileTabItem tab)
    {
        var pane = _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveTab, tab))
                   ?? _panes.FirstOrDefault(p => p.ShowsTab(tab))
                   ?? _focusedPane;
        pane.CloseFileTab(tab);
        RefreshCardGroups();
    }

    private void OnPaneFocusRequested(WorkspacePaneView pane)
    {
        if (ReferenceEquals(_focusedPane, pane)) return;
        _focusedPane = pane;
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
    }

    private bool _inOwnershipRouting;

    private void OnPaneActiveChanged(WorkspacePaneView pane)
    {
        // 세션 소유권: 한 세션은 한 패널에서만 표시. 다른 패널이 같은 세션을 들고 있으면 놓아준다.
        if (!_inOwnershipRouting)
        {
            var s = pane.ActiveSession;
            if (s != null)
            {
                _inOwnershipRouting = true;
                try
                {
                    foreach (var other in _panes)
                        if (!ReferenceEquals(other, pane)) other.ReleaseSessionIfActive(s);
                }
                finally { _inOwnershipRouting = false; }
            }
        }

        // 분할 중 포커스 패널이 마지막 탭을 닫아 비면(활성 탭 없음) 내용이 남은 반대 패널로 포커스를
        // 넘긴다 — 우측에서 유일한 파일/세션을 닫았을 때 좌측 세션이 자동 선택되고 셸(사이드바·헤더
        // 타이틀)이 갱신되게 한다. 안 그러면 포커스가 빈 패널에 머물러 좌측 세션이 선택되지 않고
        // 직전 파일 타이틀이 그대로 남는다.
        bool refocused = false;
        if (_splitActive && ReferenceEquals(pane, _focusedPane) && pane.ActiveTab == null)
        {
            var other = _panes.FirstOrDefault(p => !ReferenceEquals(p, pane) && p.ActiveTab != null);
            if (other != null) { _focusedPane = other; refocused = true; }
        }

        if (refocused) { SyncShellToFocusedPane(); UpdatePaneFocusVisual(); }
        else if (ReferenceEquals(pane, _focusedPane)) SyncShellToFocusedPane();
        // 패널 B 의 활성이 바뀌면 분할 복원용 상태를 갱신(패널 A 는 SyncShell 의 last-active 가 담당).
        if (ReferenceEquals(pane, RightPane)) PersistSplitState();
        UpdatePaneRoles();
    }

    /// <summary>두 패널의 물리 컬럼(좌 col0 / 우 col2)을 맞바꿔 좌/우 위치만 교환한다.
    /// 콘텐츠·터미널은 각 패널에 그대로 남으므로 세션 재로딩이 없다. 분할 중에만 호출.</summary>
    private void SwapPanePositions()
    {
        int ca = Grid.GetColumn(PaneA), cb = Grid.GetColumn(PaneB);
        Grid.SetColumn(PaneA, cb);
        Grid.SetColumn(PaneB, ca);
        _panesSwapped = !_panesSwapped;

        // 우측 패널 표식(탭바 X=분할 닫기 버튼)을 새 좌/우에 맞춰 갱신.
        PaneA.IsRightPane = ReferenceEquals(RightPane, PaneA);
        PaneB.IsRightPane = ReferenceEquals(RightPane, PaneB);
        foreach (var pn in _panes) pn.SetSplitActive(_splitActive);
        UpdateCenterRightBorder();   // 최우측 패널이 바뀌었으니 우측 보더 재배치
        PaneA.RefreshSplitIndicator(); // 분할 토글 버튼은 우측 패널에만 보임 — 좌우 바뀌었으니 갱신
        PaneB.RefreshSplitIndicator();
        PersistSplitState();
    }

    /// <summary>분할 중 각 프로젝트가 떠 있는 패널(좌=PaneA / 우=PaneB)을 PaneRole 에 반영.
    /// 사이드바 카드 헤더의 패널 배지가 이 값으로 좌/우 칸을 하이라이트한다. 비분할이면 전부 None.</summary>
    private void UpdatePaneRoles()
    {
        var left = _splitActive ? LeftPane.ActiveProject : null;
        var right = _splitActive ? RightPane.ActiveProject : null;
        foreach (var p in _projects.Concat(_archivedProjects))
            p.PaneRole = ReferenceEquals(p, left) ? PaneRole.Left
                       : ReferenceEquals(p, right) ? PaneRole.Right
                       : PaneRole.None;
    }

    /// <summary>세션이 현재 활성인 패널 → 없으면 그 세션의 프로젝트를 보여주는 패널 → 없으면 포커스 패널.</summary>
    private WorkspacePaneView PaneFor(SessionItem s)
        => _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveSession, s))
           ?? _panes.FirstOrDefault(p => p.ActiveProject != null && p.ActiveProject.Tabs.Contains(s))
           ?? _focusedPane;

    private void PersistSplitState()
    {
        // 우측이 비었으면(마지막 탭을 닫음) 파트너 경로를 저장하지 않는다 — 경로만 남기면 복원 시
        // '같은 폴더를 가리키는 다른 프로젝트 항목'이 파트너로 잡혀 엉뚱한 프로젝트 세션이 우측에 열린다.
        string? rightPartnerPath = RightPane.ActiveTab == null ? null : RightPane.ActiveProject?.Path;
        SettingsService.SaveFullSplitState(
            _splitActive,
            LeftPane.ActiveProject?.Path, LeftPane.ActiveSession?.Id,
            rightPartnerPath, RightPane.ActiveSession?.Id,
            _panesSwapped);

        // 프로젝트 단위 분할 기억 — 메인(좌측) 프로젝트가 분할 켬 상태면 현재 파트너를 계속 갱신해
        // 다음에 이 프로젝트를 열 때(SelectProjectIntoPane) 같은 파트너로 분할이 되살아나게 한다.
        var leftProj = LeftPane.ActiveProject;
        if (_splitActive && leftProj != null && leftProj.SplitEnabled)
        {
            leftProj.SplitPartnerProjectPath = rightPartnerPath;
            leftProj.SplitPartnerSessionId = RightPane.ActiveSession?.Id;
            // 우측이 파일 탭이면(ActiveSession=null) 파일 경로를 파트너로 저장 — 안 하면 복원 시
            // 세션ID/파일 둘 다 없어 자기 프로젝트를 통째로 우측에 여는 중복 버그가 난다.
            leftProj.SplitPartnerFilePath = RightPane.ActiveSession == null
                ? (RightPane.ActiveTab as FileTabItem)?.FilePath : null;

            // 같은 프로젝트를 분할(분할 보기로 여러 탭을 우측에 격리)한 경우: 우측 전체 탭 집합 + 좌/우
            // 활성 탭을 저장해 복원 시 그대로 되살린다(파트너 하나만 복원돼 나머지가 좌측으로 쏠리던 문제 해결).
            if (ReferenceEquals(RightPane.ActiveProject, leftProj))
            {
                // 우측 전용 집합 = 우측에 보이지만 좌측엔 안 보이는 탭. 우측이 화이트리스트(_isolatedTabs)든
                // 블랙리스트든(격리가 풀렸어도) 실제 표시 기준이라 견고하다. (CurrentIsolatedRefs 는 격리 해제 시
                // 빈 값이라 우측 탭이 하나만 복원되던 문제가 있었음.)
                var leftVisible = new HashSet<string>(LeftPane.VisibleTabRefs());
                leftProj.SplitRightTabRefs = RightPane.VisibleTabRefs().Where(r => !leftVisible.Contains(r)).ToList();
                leftProj.SplitRightActiveRef = RightPane.ActiveTabRef();
                leftProj.LastActiveTabRef = LeftPane.ActiveTabRef(); // 좌측 활성 탭(우측 활성은 위 별도 필드)
            }
            else
            {
                leftProj.SplitRightTabRefs = new(); // 다른 프로젝트 파트너 → 파트너 경로로 복원
                leftProj.SplitRightActiveRef = null;
            }
            WorkspaceStore.Save(_projects, _archivedProjects);
        }
    }

    /// <summary>사이드바 카드의 좌/우 그룹을 '현재 실제 분할 상태'로 갱신. 같은 프로젝트를 좌/우 패널이 함께
    /// 표시할 때만 그 프로젝트를 좌=좌패널 표시탭 / 우=우패널 표시탭으로 나눠 보이고, 나머지는 단일 목록.
    /// (영속 플래그가 아니라 라이브 패널 상태 기준 — 클릭/드래그가 실제 패널과 정확히 일치.)</summary>
    private void RefreshCardGroups()
    {
        ProjectItem? liveSplitProj = null;
        if (_splitActive && LeftPane.ActiveProject != null && ReferenceEquals(LeftPane.ActiveProject, RightPane.ActiveProject))
            liveSplitProj = LeftPane.ActiveProject;
        foreach (var p in _projects.Concat(_archivedProjects))
        {
            if (ReferenceEquals(p, liveSplitProj))
            {
                // 현재 실제 분할된 프로젝트: 라이브 패널 상태로 파티션(가장 정확). 한 세션은 한 그룹에만 —
                // 우측 그룹 = '우측에만' 있는 탭(좌측엔 없는 것), 좌측 그룹 = 좌측 표시 탭.
                var left = LeftPane.VisibleTabsInOrder();
                var leftSet = new HashSet<TabItemBase>(left);
                var right = RightPane.VisibleTabsInOrder().Where(t => !leftSet.Contains(t)).ToList();
                p.ApplyLiveGroups(left, right);
            }
            else if (p.SplitEnabled && p.SplitRightTabRefs.Count > 0)
            {
                // 분할 설정된 프로젝트는 지금 활성 분할이 아니어도(시작 직후·다른 프로젝트 선택 중) 영속 우측
                // refs 로 좌/우 파티션을 계속 보여준다. right = refs 에 든 탭, left = 나머지(각 탭 한 그룹에만).
                var rightSet = new HashSet<string>(p.SplitRightTabRefs, StringComparer.OrdinalIgnoreCase);
                var left = p.Tabs.Where(t => !rightSet.Contains(WorkspacePaneView.RefOf(t))).ToList();
                var right = p.Tabs.Where(t => rightSet.Contains(WorkspacePaneView.RefOf(t))).ToList();
                p.ApplyLiveGroups(left, right);
            }
            else
                p.ClearLiveGroups();
        }
    }

    /// <summary>포커스 패널의 활성 프로젝트/세션을 셸(파일탐색기·사이드바·last-active)에 반영.</summary>
    private void SyncShellToFocusedPane()
    {
        RefreshCardGroups();
        var proj = _focusedPane.ActiveProject;
        // 보관함 프로젝트도 패널에 띄울 수 있으므로 활성+보관 양쪽을 순회해야 강조가 정확히 옮겨간다.
        // (_projects 만 돌면 보관 프로젝트는 선택돼도 강조 안 되고, 선택 해제도 안 됨)
        foreach (var p in _projects) p.IsSelected = ReferenceEquals(p, proj);
        foreach (var p in _archivedProjects) p.IsSelected = ReferenceEquals(p, proj);
        if (proj?.Path != _explorerDir)
        {
            _explorerDir = proj?.Path;
            if (proj != null) FileExplorer.ShowDirectory(proj.Path);
        }
        SettingsService.SaveLastActive(proj?.Path, _focusedPane.ActiveSession?.Id);
    }

    private const double SplitPaneMinWidth = 30;
    private bool _splitActive;

    // 분할 펼침/접힘 애니메이션 진행도(0=합쳐짐, 1=완전 분할). GridLength 는 직접 애니메이션이 안 되므로
    // 이 double DP 를 애니메이션하고 콜백에서 PaneB 컬럼의 star 폭을 갱신한다.
    private static readonly DependencyProperty PaneSplitProgressProperty =
        DependencyProperty.Register(nameof(PaneSplitProgress), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, OnPaneSplitProgressChanged));

    private double PaneSplitProgress
    {
        get => (double)GetValue(PaneSplitProgressProperty);
        set => SetValue(PaneSplitProgressProperty, value);
    }

    private static void OnPaneSplitProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var w = (MainWindow)d;
        // PaneA 는 1* 고정, PaneB 는 p* → 비율 p/(1+p). p:0→1 이면 0%→50% 로 부드럽게 자란다.
        w.PaneBCol.Width = new GridLength((double)e.NewValue, GridUnitType.Star);
    }

    /// <summary>PaneB 컬럼을 from→to(0~1) 로 애니메이션. 완료 시 onComplete 호출.</summary>
    private void AnimatePaneSplit(double from, double to, Action onComplete)
    {
        PaneSplitProgress = from;
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(210),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(PaneSplitProgressProperty, null);   // 애니메이션 해제 → 이후 스플리터 드래그가 자유롭게 폭을 바꿀 수 있다.
            onComplete();
        };
        BeginAnimation(PaneSplitProgressProperty, anim);
    }

    /// <summary>탭바 분할 토글 버튼(양쪽 패널에 있음). 분할 중이면 어느 쪽에서 눌러도 분할 자체를 닫고,
    /// 남는 프로젝트(LeftPane 이 될 콘텐츠)의 SplitEnabled 를 꺼서 "닫아도 유지" 상태를 반영한다.
    /// 분할 전이면 이 패널의 활성 프로젝트 기준으로 분할을 켜고 SplitEnabled=true 로 기억한다.</summary>
    private void OnPaneSplitToggle(WorkspacePaneView pane)
    {
        if (_splitActive)
        {
            var keep = LeftPane.ActiveProject; // DisableSplit 이후 유지되는 프로젝트
            DisableSplit();
            if (keep != null)
            {
                // 사용자가 '분할 닫기' 버튼으로 직접 닫은 경우 — 다음에 다시 열 때 우측을 복원하지 않고 비운 채 연다.
                // (다른 비분할 프로젝트로 전환해 자동으로 닫히는 경우는 복원돼야 하므로, 이 버튼 경로에서만 파트너/우측 집합을 지운다.)
                keep.SplitEnabled = false;
                keep.SplitPartnerProjectPath = null;
                keep.SplitPartnerSessionId = null;
                keep.SplitPartnerFilePath = null;
                keep.SplitRightTabRefs = new();
                keep.SplitRightActiveRef = null;
                WorkspaceStore.Save(_projects, _archivedProjects);
            }
        }
        else
        {
            var proj = pane.ActiveProject;
            if (proj == null) return;
            proj.SplitEnabled = true;
            EnableSplitForProject(proj);
            WorkspaceStore.Save(_projects, _archivedProjects);
        }
        RefreshCardGroups(); // 분할/닫기 후 카드 좌/우 그룹 재계산(닫기 시 SplitEnabled=false 반영 뒤여야 함)
        PaneA.RefreshSplitIndicator();
        PaneB.RefreshSplitIndicator();
    }

    /// <summary>proj 에 저장된 분할 파트너를 찾는다. 우선순위: 같은/다른 프로젝트의 세션 → 같은 프로젝트의 파일 →
    /// 다른 프로젝트(통째). 같은 프로젝트 자신을 세션/파일 없이 가리키는 값은 무의미하므로 project=null 로 정규화.</summary>
    private (ProjectItem? project, SessionItem? session, string? filePath) ResolveSplitPartner(ProjectItem proj)
    {
        SessionItem? partnerSession = null;
        if (!string.IsNullOrEmpty(proj.SplitPartnerSessionId))
            partnerSession = _projects.Concat(_archivedProjects).SelectMany(p => p.Tabs)
                .OfType<SessionItem>().FirstOrDefault(s => s.Id == proj.SplitPartnerSessionId);
        // 그 사이 숨겨진 세션은 파트너로 쓰지 않는다 — OpenSession 이 unHide 로 되살려 "숨긴 세션이 저절로 열림"이 된다.
        if (partnerSession != null && partnerSession.IsEffectivelyHidden) partnerSession = null;
        if (partnerSession != null) return (null, partnerSession, null);

        // 세션이 없으면 같은 프로젝트의 파일 파트너(우측이 파일 탭이었던 경우) 시도.
        if (!string.IsNullOrEmpty(proj.SplitPartnerFilePath) && System.IO.File.Exists(proj.SplitPartnerFilePath))
            return (null, null, proj.SplitPartnerFilePath);

        // 마지막으로 "다른" 프로젝트 통째 파트너. 자기 자신이면 무의미 → 무시.
        ProjectItem? partnerProj = null;
        if (!string.IsNullOrEmpty(proj.SplitPartnerProjectPath))
            partnerProj = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Path == proj.SplitPartnerProjectPath);
        // 자기자신 판정은 '경로'로 한다 — 같은 폴더를 등록한 다른 프로젝트 항목이 파트너로 잡히면
        // 우측에 그 프로젝트가 통째로 열려 엉뚱한(숨김 포함) 세션이 로드된다.
        if (partnerProj != null
            && (ReferenceEquals(partnerProj, proj)
                || string.Equals(partnerProj.Path, proj.Path, StringComparison.OrdinalIgnoreCase)))
            partnerProj = null;
        return (partnerProj, null, null);
    }

    /// <summary>proj 의 저장된 분할 파트너를 찾아 분할을 켠다(비분할 상태에서 호출). 파트너를 못 찾으면 빈 우측으로 연다.
    /// animate=false 면 펼침 슬라이드 없이 즉시 분할 레이아웃으로(다른 프로젝트로 전환 시).</summary>
    private void EnableSplitForProject(ProjectItem proj, bool animate = true)
    {
        var (partnerProj, partnerSession, partnerFile) = ResolveSplitPartner(proj);
        // 우측 탭 집합/활성 refs 를 EnableSplit 전에 스냅샷한다 — EnableSplit 의 세션 활성화가 중간
        // PersistSplitState 를 유발해(아직 격리 전 상태) refs 를 빈 값으로 덮어쓸 수 있기 때문.
        var (rightRefs, rightActive, leftActive) = SnapshotSplitRefs(proj);
        // 세션·다른프로젝트 파트너는 EnableSplit 이 바로 우측에 띄운다. 파일 파트너는 빈 우측으로 연 뒤 아래에서 연다.
        EnableSplit(bProject: partnerProj, bSession: partnerSession, animate: animate);
        if (!RestoreSameProjectSplit(proj, rightRefs, rightActive, leftActive)) RestorePartner(proj, partnerSession, partnerFile);
        // 우측 reveal 은 호출부(SelectProjectIntoPane/OpenSessionIntoPane)가 좌측과 함께 동시(synced)로 처리한다.
    }

    /// <summary>이미 분할된 상태에서 좌측 프로젝트가 다른 "분할 사용" 프로젝트로 바뀔 때, 우측 패널
    /// 내용을 새 프로젝트의 저장된 파트너로 교체한다.</summary>
    private void ApplyPartnerToPaneB(ProjectItem proj)
    {
        var (partnerProj, partnerSession, partnerFile) = ResolveSplitPartner(proj);
        var (rightRefs, rightActive, leftActive) = SnapshotSplitRefs(proj);
        if (partnerSession != null) RightPane.OpenSession(partnerSession);
        else if (partnerProj != null) RightPane.SelectProject(partnerProj);
        if (!RestoreSameProjectSplit(proj, rightRefs, rightActive, leftActive)) RestorePartner(proj, partnerSession, partnerFile);
    }

    /// <summary>복원 시작 전 프로젝트의 분할 refs 를 스냅샷 — 이후 활성화가 유발하는 중간 PersistSplitState 가
    /// (아직 격리 전 상태를 보고) refs 를 빈 값으로 덮어써도, 이 스냅샷으로 올바르게 복원한다.</summary>
    private (List<string> rightRefs, string? rightActive, string? leftActive) SnapshotSplitRefs(ProjectItem proj)
        => (proj.SplitRightTabRefs?.ToList() ?? new(), proj.SplitRightActiveRef, proj.LastActiveTabRef);

    /// <summary>같은 프로젝트 분할 복원 — 저장된 우측 탭 집합(SplitRightTabRefs)을 우측에 모두 격리하고
    /// 좌측에선 숨긴 뒤, 좌/우 활성 탭을 각각 복원한다(파트너 하나만 복원돼 나머지가 좌측으로 쏠리던 문제 해결).
    /// 저장된 우측 집합이 없으면 false → 호출부가 기존 단일 파트너 복원(RestorePartner)으로 폴백.</summary>
    private bool RestoreSameProjectSplit(ProjectItem proj, List<string> rightRefs, string? rightActive, string? leftActive)
    {
        if (rightRefs == null || rightRefs.Count == 0) return false;
        var activeRef = !string.IsNullOrEmpty(rightActive) ? rightActive : rightRefs[0];
        // 우측이 이 프로젝트를 표시하도록 우측 활성 탭을 먼저 연다(세션/파일).
        OpenRefInPane(RightPane, proj, activeRef);
        if (!ReferenceEquals(RightPane.ActiveProject, proj)) return false;
        // 우측 전체 탭 격리 + 좌측 숨김.
        foreach (var r in rightRefs)
        {
            var tab = RightPane.FindTabByRef(r);
            if (tab == null && r.StartsWith("F:")) { RightPane.OpenFileTabForPartner(proj, r[2..]); tab = RightPane.FindTabByRef(r); }
            if (tab == null) continue;
            RightPane.IsolateTab(tab);
            LeftPane.HideTabInPane(tab);
        }
        RightPane.ActivateByRef(activeRef);   // 우측 활성 탭 복원
        // 좌측 활성 탭 복원(가장 왼쪽이 아니라 직전 선택 탭) — 단, leftActive 가 방금 위 루프에서
        // 우측으로 격리되며 좌측에 숨겨진 탭이면 활성화하면 안 된다. 그 탭은 좌측 탭바엔 안 보이는데
        // (HideTabInPane 이 이미 다른 탭으로 교체/비움 처리함) 여기서 그대로 재활성화하면 좌측 헤더
        // (브랜치/모델/effort)와 터미널이 우측에 보이는 탭 내용으로 다시 채워지는 유령 상태가 된다.
        var leftTab = LeftPane.FindTabByRef(leftActive);
        if (leftTab != null && LeftPane.ShowsTab(leftTab)) LeftPane.ActivateByRef(leftActive);
        // 복원 중 중간 PersistSplitState 가 빈 값으로 덮었을 refs 를 스냅샷으로 되돌려 다음 복원도 성공하게 한다.
        proj.SplitRightTabRefs = rightRefs;
        proj.SplitRightActiveRef = rightActive;
        WorkspaceStore.Save(_projects, _archivedProjects);
        return true;
    }

    /// <summary>참조("S:id"/"F:path"/"B:id")가 가리키는 탭을 지정 패널에서 연다(우측 복원용).</summary>
    private void OpenRefInPane(WorkspacePaneView pane, ProjectItem proj, string? @ref)
    {
        if (string.IsNullOrEmpty(@ref) || @ref!.Length < 2 || @ref[1] != ':') return;
        var key = @ref[2..];
        if (@ref[0] == 'S')
        {
            // 숨겨진 세션은 복원 대상에서 제외 — 열면 unHide 로 되살아나 "숨긴 세션이 저절로 열림"이 된다.
            var s = _projects.Concat(_archivedProjects).SelectMany(p => p.Tabs).OfType<SessionItem>()
                .FirstOrDefault(x => x.Id == key && !x.IsEffectivelyHidden);
            if (s != null) pane.OpenSession(s);
        }
        else if (@ref[0] == 'F')
        {
            pane.OpenFileTabForPartner(proj, key);
        }
        else if (@ref[0] == 'B')
        {
            pane.OpenBrowserTabForPartner(proj, key);
        }
    }

    /// <summary>분할 파트너가 "같은 프로젝트(proj)"의 세션/파일/브라우저면 = "분할 보기"로 그 탭만 우측에 띄웠던 상태다.
    /// 격리/숨김 필터는 휘발성이라 프로젝트를 떠나면 사라지므로 복원 시 재현한다:
    /// 우측은 그 탭만 격리, 좌측은 그 탭 숨김. 파일 파트너는 우측 패널에 파일을 새로 열어 활성화한 뒤 격리한다.</summary>
    private void RestorePartner(ProjectItem proj, SessionItem? partnerSession, string? partnerFile)
    {
        TabItemBase? partnerTab = null;
        if (partnerSession != null)
        {
            var pp = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(partnerSession));
            if (!ReferenceEquals(pp, proj)) return; // 다른 프로젝트 세션 파트너 → 통째 표시(격리 안 함)
            partnerTab = partnerSession;
        }
        else if (partnerFile != null)
        {
            partnerTab = RightPane.OpenFileTabForPartner(proj, partnerFile);
        }
        if (partnerTab == null) return;

        RightPane.IsolateTab(partnerTab);
        LeftPane.HideTabInPane(partnerTab);
    }

    /// <summary>탭 헤더 우클릭 → "분할 보기"/"이동" → 분할 생성(비분할 시) 또는 반대쪽 패널로 이동(분할 중).
    /// 원본 패널의 탭바에서는 탭을 숨기고(HideTabInPane), 세션/ConPTY·파일 에디터는 그대로 둔 채
    /// 반대쪽 패널에 띄운다. 세션·파일·브라우저 탭을 지원한다.
    /// 반대쪽이 이미 격리(화이트리스트) 중이었거나 프로젝트가 바뀌면, 원래 보이던 화이트리스트를
    /// 스냅샷해뒀다가 이동 후 그대로 다시 얹고 새 탭을 더한다(누적 — 기존 탭들이 사라지지 않고
    /// 뒤에 쌓임). 반대쪽이 이미 같은 프로젝트를 "격리 없이" 보여주던 중이면(전체 탭 목록 공유)
    /// 그 목록에 이미 포함돼 있으므로 격리를 걸지 않는다.
    /// 이동한 탭은 어느 경우든 그 프로젝트의 탭 목록 맨 끝으로 옮겨 반대쪽 탭바 가장 오른쪽에
    /// 보이게 한다(이미 보여지고 있던 케이스도 포함 — 안 그러면 "이동"했는데 위치가 그대로라
    /// 아무 변화도 없어 보인다).</summary>
    /// <summary>탭 드래그 중 — 커서가 반대 패널 위면 그 패널에 삽입 프리뷰(탭 밀기)를 그리고 true(크로스 중) 반환.
    /// 아니면 반대 패널 프리뷰 해제 후 false(소스 패널 내부 재정렬 프리뷰 진행).</summary>
    private bool OnTabDragHoverMoved(WorkspacePaneView source, Point screen, double ghostWidth)
    {
        if (!_splitActive) return false;
        var other = ReferenceEquals(source, LeftPane) ? RightPane : LeftPane;
        if (ReferenceEquals(other, source)) return false;

        if (ReferenceEquals(PaneAtScreen(screen), other))
        {
            other.ShowInsertPreview(screen, ghostWidth + 3); // 탭 간격(3) 포함
            return true;
        }
        other.ClearInsertPreview();
        return false;
    }

    /// <summary>탭 드롭 — 커서가 반대 패널 위면 그 패널로 이동(우클릭 "이동"과 동일) + 삽입 위치 정렬 후 true.
    /// 아니면 false(소스 패널 내부 재정렬은 ReorderDrag 커밋이 처리).</summary>
    private bool OnTryCommitCrossTabDrop(WorkspacePaneView source, TabItemBase tab, Point screen)
    {
        LeftPane.ClearInsertPreview(); RightPane.ClearInsertPreview(); // 프리뷰 항상 해제
        if (!_splitActive) return false;
        var other = ReferenceEquals(source, LeftPane) ? RightPane : LeftPane;
        if (ReferenceEquals(other, source) || !ReferenceEquals(PaneAtScreen(screen), other)) return false;

        int idx = other.InsertIndexAtScreenX(screen, tab); // 이동 전 대상 탭들 기준 삽입 위치
        OnPaneSplitViewRequested(source, tab);             // 반대 패널로 이동(격리 재배선 + refresh/persist)
        other.ReorderVisibleTab(tab, idx);                 // 그 패널의 삽입 위치로 정렬
        RefreshCardGroups();
        LeftPane.RefreshSelectedTabSeam(); RightPane.RefreshSelectedTabSeam();
        WorkspaceStore.Save(_projects);
        return true;
    }

    /// <summary>screen 좌표가 들어있는 패널(분할 중 좌/우). 없으면 null.</summary>
    private WorkspacePaneView? PaneAtScreen(Point screen)
    {
        foreach (var p in new[] { LeftPane, RightPane })
        {
            if (!p.IsVisible) continue;
            var loc = p.PointFromScreen(screen);
            if (loc.X >= 0 && loc.Y >= 0 && loc.X <= p.ActualWidth && loc.Y <= p.ActualHeight) return p;
        }
        return null;
    }

    private void OnPaneSplitViewRequested(WorkspacePaneView pane, TabItemBase tab)
    {
        if (_splitActive)
        {
            var target = ReferenceEquals(pane, LeftPane) ? RightPane : LeftPane;
            if (ReferenceEquals(target, pane)) return;
            var homeProject = pane.ActiveProject; // 탭이 실제로 속한 프로젝트(패널이 바뀌어도 그대로)
            bool sameProject = ReferenceEquals(target.ActiveProject, homeProject);

            // 원본 패널에서 이동 탭 제거(원본이 그 탭을 활성 중이었으면 다른 탭으로 대체).
            pane.HideTabInPane(tab);

            if (sameProject)
            {
                // 대상이 이미 이 프로젝트를 보여줌 → 대상의 "선택된 탭"은 건드리지 않고 탭만 노출한다.
                // (격리 중이면 화이트리스트에 추가, 전체 모드면 블랙리스트 해제 — 활성 탭 변경 없음.)
                // OpenSession/OpenFileTab 을 부르지 않으므로 대상 격리도 안 풀린다 → 스냅샷 복원 불필요.
                target.UnhideTabInPane(tab);
                // 단, 도착 패널이 비어 있으면(열린 탭 없음) 이동된 게 그 패널의 첫(유일) 탭이므로 바로 활성화해
                // 연다 — 세션이든 파일(md 등)이든. 안 그러면 탭만 생기고 빈 화면. UnhideTabInPane 으로 이미
                // 화이트리스트에 들어가 격리도 안 풀린다. 이 경우엔 방금 연 탭을 볼 수 있게 포커스를 대상으로 옮긴다.
                if (target.ActiveTab == null)
                {
                    if (tab is SessionItem movedFirstS) target.OpenSession(movedFirstS);
                    else if (tab is FileTabItem movedFirstF) target.OpenFileTab(movedFirstF);
                    else if (tab is BrowserTabItem movedFirstB) target.OpenBrowserTab(movedFirstB);
                    _focusedPane = target;
                }
                // 그 외엔 포커스를 원본에 그대로 둔다(대상 활성 탭을 안 바꾸므로).
            }
            else
            {
                // 대상이 다른 프로젝트/빈 패널 → 그 프로젝트로 전환이 불가피하고 이동 탭이 활성화된다.
                // 이동 탭만 격리해 대상엔 그 탭만 보이게 한다(전체 목록 복사 방지).
                if (tab is SessionItem session) target.OpenSession(session);
                else if (tab is FileTabItem file) target.OpenFileTab(file);
                else if (tab is BrowserTabItem browser) target.OpenBrowserTab(browser);
                else return;
                target.IsolateTab(tab);
                _focusedPane = target;
            }

            if (homeProject != null) MoveTabToEnd(tab, homeProject);
            target.ScrollTabIntoView(tab);

            SyncShellToFocusedPane();
            UpdatePaneFocusVisual();
            PersistSplitState();
            return;
        }

        // "분할 보기": 원본 패널(pane)은 이동 탭을 내주고 남은 탭(예: 세션1)이 활성화되며 전체→절반으로
        // 리사이즈된다. 그 세션 전환 + 리사이즈 리플로우(줄 깨짐)를 커튼으로 감췄다 최종 폭에서 재동기·fade-in.
        pane.CoverForTransition();
        pane.HideTabInPane(tab);
        // animate:false — 커튼으로 가려 슬라이드 애니메이션이 안 보이므로 즉시 분할해 최종 폭을 확정한다
        // (RevealAfterTransition 의 UpdateLayout 이 정확한 목표 폭을 읽어야 중간 전체폭 plateau 를 건너뛴다).
        if (tab is SessionItem s)
        {
            EnableSplit(bSession: s, animate: false);
            PaneB.IsolateTab(s);
        }
        else if (tab is FileTabItem f)
        {
            EnableSplit(animate: false);
            PaneB.OpenFileTab(f);
            PaneB.IsolateTab(f);
        }
        else if (tab is BrowserTabItem browser)
        {
            EnableSplit(animate: false);
            PaneB.OpenBrowserTab(browser);
            PaneB.IsolateTab(browser);
        }
        if (pane.ActiveProject != null) MoveTabToEnd(tab, pane.ActiveProject);
        PaneB.ScrollTabIntoView(tab);
        // 남은 세션은 이 패널에 처음 표시되며 살아있는 ConPTY 에 재배선된다 → kick 으로 SIGWINCH 강제
        // 리페인트해 스크롤/뷰포트 정지 프레임을 막는다.
        pane.RevealAfterTransition(kick: true);

        // 버튼 토글로 켠 분할과 동일하게 영속 — 메인(좌측) 프로젝트를 "분할 사용"으로 표시하고
        // 파트너를 기록(PersistSplitState가 갱신)해, 재선택/재시작 시 같은 분할이 복원되게 한다.
        var mainProj = LeftPane.ActiveProject;
        if (mainProj != null)
        {
            mainProj.SplitEnabled = true;
            PersistSplitState();
            PaneA.RefreshSplitIndicator();
            PaneB.RefreshSplitIndicator();
        }
    }

    private (string Header, string IconKey) GetSidebarSplitMovePresentation(TabItemBase tab)
    {
        var pane = FindLivePaneForSidebarTab(tab);
        if (!_splitActive || pane == null)
            return ("분할 보기", "IconPanelLeftOpen");
        return ReferenceEquals(pane, RightPane)
            ? ("왼쪽으로 이동", "IconChevronLeft")
            : ("오른쪽으로 이동", "IconChevronRight");
    }

    private void MoveSidebarTabAcrossSplit(TabItemBase tab)
    {
        var source = FindLivePaneForSidebarTab(tab);
        bool wasAlreadyVisible = source != null;

        if (source == null)
        {
            if (tab is SessionItem session) OpenSessionFromSidebar(session);
            else if (tab is FileTabItem file) OpenDocFromSidebar(file);
            else return;
            source = FindLivePaneForSidebarTab(tab);
        }
        if (source == null) return;

        if (!wasAlreadyVisible && _splitActive && ReferenceEquals(source, RightPane))
            return;

        OnPaneSplitViewRequested(source, tab);
        RefreshCardGroups();
    }

    private WorkspacePaneView? FindLivePaneForSidebarTab(TabItemBase tab)
    {
        var project = _projects.Concat(_archivedProjects)
            .FirstOrDefault(candidate => candidate.Tabs.Contains(tab));
        if (project == null) return null;

        if (_splitActive)
        {
            if (project.RightItems.Contains(tab)
                && ReferenceEquals(RightPane.ActiveProject, project)
                && RightPane.ShowsTab(tab))
                return RightPane;
            if (project.LeftItems.Contains(tab)
                && ReferenceEquals(LeftPane.ActiveProject, project)
                && LeftPane.ShowsTab(tab))
                return LeftPane;
        }

        var visible = new[] { LeftPane, RightPane }
            .Where(pane => ReferenceEquals(pane.ActiveProject, project) && pane.ShowsTab(tab))
            .ToList();
        if (visible.Count == 0) return null;
        if (visible.Contains(_focusedPane)) return _focusedPane;
        var active = visible.FirstOrDefault(pane => ReferenceEquals(pane.ActiveTab, tab));
        return active ?? visible[0];
    }

    /// <summary>파일탐색기 더블클릭 → 파일 탭 열기. 분할 중이면 더블클릭 파일을 '우측' 패널에 연다:
    /// 우측이 같은 프로젝트를 보여주면 그대로, 우측이 그 프로젝트를 아직 안 띄웠으면(빈 우측/세션0) 격리
    /// 모드로 프로젝트를 활성화한 뒤 연다(그래서 우측 세션이 없어도 파일이 열린다). 우측이 '다른' 프로젝트를
    /// 보여주는 크로스-프로젝트 분할이면 파일 소속이 어긋나므로 포커스 패널에 연다. 비분할이면 포커스 패널.</summary>
    private void OpenFileFromExplorer(string path)
    {
        // 파일이 속한 프로젝트 = 탐색기가 보여주는 프로젝트(포커스 패널 활성, 없으면 좌/우 폴백).
        var proj = _focusedPane.ActiveProject ?? LeftPane.ActiveProject ?? RightPane.ActiveProject;

        var target = _focusedPane;
        if (_splitActive && proj != null)
        {
            if (ReferenceEquals(RightPane.ActiveProject, proj))
                target = RightPane;                       // 우측이 같은 프로젝트 → 우측에(격리 유지)
            else if (RightPane.ActiveProject == null)
            {
                target = RightPane;                       // 빈 우측 → 프로젝트를 격리 모드로 활성화 후 연다
                target.CoverForTransition();              // 격리 활성화(렌더) 전에 덮기
                RightPane.ActivateProjectIsolated(proj);
            }
            // else: 우측이 다른 프로젝트 → 포커스 패널에 연다(파일 소속 불일치 방지)
        }

        // 프로젝트 선택 열기처럼 커튼으로 감싼다 — md 에디터 호스트 0×0→full 그로우(검정 flash) +
        // 콜드 로드 스피너를 감추고, RevealAfterTransition 이 md 준비될 때까지 기다렸다 fade.
        target.CoverForTransition();
        var tab = target.OpenFileAsTab(path);
        if (tab != null && !ReferenceEquals(_focusedPane, target))
        {
            _focusedPane = target;                        // 연 패널로 포커스 이동
            SyncShellToFocusedPane();
            UpdatePaneFocusVisual();
        }
        target.RevealAfterTransition();                   // md 콜드 로드 대기 후 커튼 fade(준비됐으면 즉시)
    }

    private IEnumerable<Models.QuickOpenItem> BuildQuickOpenItems()
    {
        var allProjects = _projects.Concat(_archivedProjects);
        var items = new List<Models.QuickOpenItem>(Math.Max(256, allProjects.Sum(p => 4 + p.Tabs.Count + p.Files.Count)));
        var seenFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in allProjects)
        {
            var projectName = string.IsNullOrWhiteSpace(project.Name) ? project.Path : project.Name;
            items.Add(new Models.QuickOpenItem(
                Models.QuickOpenKind.Project,
                projectName,
                project.Path,
                project));

            foreach (var session in project.Tabs.OfType<SessionItem>())
            {
                items.Add(new Models.QuickOpenItem(
                    Models.QuickOpenKind.Session,
                    session.Name,
                    projectName,
                    project,
                    session));
            }

            foreach (var file in project.Files)
            {
                if (string.IsNullOrWhiteSpace(file.FilePath)) continue;
                if (!seenFilePaths.Add(file.FilePath)) continue;
                var fileName = System.IO.Path.GetFileName(file.FilePath);
                items.Add(new Models.QuickOpenItem(
                    Models.QuickOpenKind.File,
                    fileName,
                    $"{projectName}\n{file.FilePath}",
                    project,
                    filePath: file.FilePath));
            }

            foreach (var tab in project.Tabs.OfType<FileTabItem>())
            {
                if (string.IsNullOrWhiteSpace(tab.FilePath)) continue;
                if (!seenFilePaths.Add(tab.FilePath)) continue;
                var fileName = System.IO.Path.GetFileName(tab.FilePath);
                items.Add(new Models.QuickOpenItem(
                    Models.QuickOpenKind.File,
                    fileName,
                    $"{projectName}\n{tab.FilePath}",
                    project,
                    filePath: tab.FilePath));
            }
        }

        return items;
    }

    private void OpenQuickOpenItem(Models.QuickOpenItem item)
    {
        switch (item.Kind)
        {
            case Models.QuickOpenKind.Project:
                if (item.Project != null) SelectProject(item.Project);
                break;
            case Models.QuickOpenKind.Session:
                if (item.Session != null) OpenSession(item.Session);
                break;
            case Models.QuickOpenKind.File:
                if (item.Project == null || string.IsNullOrWhiteSpace(item.FilePath)) break;
                OpenQuickOpenFile(item.Project, item.FilePath);
                break;
        }
    }

    private void OpenQuickOpenFile(ProjectItem project, string filePath)
    {
        var existingPane = _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveProject, project));
        if (existingPane != null)
        {
            FocusPaneOnly(existingPane);
            if (_focusedPane.OpenFileAsTab(filePath) is null) return;
            _focusedPane.RevealAfterTransition();
            return;
        }

        // 포커스 패널에 프로젝트를 올린 뒤 기존 파일 오픈 루틴을 재사용.
        if (!ReferenceEquals(_focusedPane.ActiveProject, project))
            _focusedPane.SelectProject(project);

        OpenFileFromExplorer(filePath);
    }

    /// <summary>격리(분할 파트너) 패널에서 탭(파일/세션)이 새로 열림 → 반대 패널에서 그 탭을 숨긴다.
    /// 탭은 공유 proj.Tabs 에 추가되므로, 안 숨기면 '전체 표시' 쪽 패널에도 함께 떠 좌우 양쪽에 보인다.
    /// (분할 보기의 IsolateTab+HideTabInPane 패턴과 동일 — 여는 패널은 이미 격리를 유지하므로 파트너 숨김만.)</summary>
    private void OnPaneIsolatedTabOpened(WorkspacePaneView pane, TabItemBase tab)
    {
        if (!_splitActive) return;
        var partner = ReferenceEquals(pane, LeftPane) ? RightPane : LeftPane;
        if (ReferenceEquals(partner, pane)) return;
        partner.HideTabInPane(tab);
        RefreshCardGroups();
        PersistSplitState(); // 우측 격리 집합 변화 반영(재시작/재선택 복원용)
    }

    /// <summary>탭을 그 프로젝트의 Tabs 컬렉션 맨 끝으로 옮긴다(탭바 가장 오른쪽에 보이게).</summary>
    private static void MoveTabToEnd(TabItemBase tab, ProjectItem proj)
    {
        int idx = proj.Tabs.IndexOf(tab);
        if (idx >= 0 && idx != proj.Tabs.Count - 1) proj.Tabs.Move(idx, proj.Tabs.Count - 1);
    }



    private void EnableSplit(ProjectItem? bProject = null, SessionItem? bSession = null, bool animate = true, bool persist = true)
    {
        if (_splitActive) return;
        _splitActive = true;

        // 안정된 분할 상태에서는 양쪽 모두 30px 아래로 줄지 않는다.
        // PaneB 는 펼침 애니메이션의 0px 시작을 보존하기 위해 완료 시 MinWidth 를 적용한다.
        PaneACol.MinWidth = SplitPaneMinWidth;
        PaneBCol.MinWidth = animate ? 0 : SplitPaneMinWidth;

        // 분할 진입은 항상 정규 배치(PaneA=좌/PaneB=우)에서 시작. (DisableSplit 가 이미 정규화하지만 방어적으로 보장)
        _panesSwapped = false;
        Grid.SetColumn(PaneA, 0);
        Grid.SetColumn(PaneB, 2);
        PaneA.IsRightPane = false;
        PaneB.IsRightPane = true;

        PaneSplitterCol.Width = new GridLength(4);
        PaneACol.Width = new GridLength(1, GridUnitType.Star);
        PaneSplitter.Visibility = Visibility.Visible;
        PaneB.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => PaneB.Terminal.PrewarmWebView(),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        foreach (var p in _panes) p.SetSplitActive(true);
        UpdateCenterRightBorder();   // 최우측이 PaneB 로 바뀜

        // 패널 B 포커스로 전환 → 이후 사이드바 클릭이 B 로 향한다.
        _focusedPane = PaneB;
        if (bSession != null) PaneB.OpenSession(bSession);
        else if (bProject != null) PaneB.SelectProject(bProject);
        else if (persist) SyncShellToFocusedPane();   // 사용자 토글 시 B 는 빈 패널 — 직접 프로젝트를 고르게 한다.
        UpdatePaneRoles();
        PaneA.RefreshSplitIndicator();
        PaneB.RefreshSplitIndicator();

        if (animate)
        {
            _ = AnimateSplitOpenAsync();
        }
        else
        {
            PaneBCol.Width = new GridLength(SettingsService.LoadSplitBStar(), GridUnitType.Star);
            PaneB.SetEmptyTextWrapping(true);
            UpdatePaneFocusVisual();
        }

        if (persist) PersistSplitState();
    }

    /// <summary>분할 펼침: 두 패널 터미널을 webCover(HWND 유지+DOM 커버)로 정지한 뒤
    /// PaneB 를 0%→저장된 비율로 펼치고, 완료 시 라이브 터미널로 크로스페이드 복원한다.</summary>
    private async Task AnimateSplitOpenAsync()
    {
        await Task.WhenAll(
            PaneA.SuspendTerminalOnlyAsync(anchorTopLeft: true, webCover: true, captureSplitWide: true),
            PaneB.SuspendTerminalOnlyAsync(anchorTopLeft: true, webCover: true));
        PaneB.SetEmptyTextWrapping(false); // 펼침 애니메이션 중 줄바꿈 방지
        double targetStar = SettingsService.LoadSplitBStar();
        AnimatePaneSplit(0, targetStar, () =>
        {
            PaneBCol.Width = new GridLength(targetStar, GridUnitType.Star);
            if (_splitActive) PaneBCol.MinWidth = SplitPaneMinWidth;
            PaneB.SetEmptyTextWrapping(true); // 완전히 펼쳐진 후에만 줄바꿈
            PaneA.ResumeTerminalOnly(webCover: true);
            PaneB.ResumeTerminalOnly(webCover: true);
            UpdatePaneFocusVisual(animate: false);
        });
    }

    /// <summary>시작 시 저장된 분할 상태 복원 — 양쪽 패널 프로젝트/세션 + 비율 + 스왑을 복원한다.
    /// "프로젝트 자동 로드" 옵션이 꺼져 있으면 분할 레이아웃 자체를 만들지 않고 단일 패널(미선택)로 시작한다.</summary>
    private void RestoreSplitState()
    {
        if (!SettingsService.LoadAutoLoadLastProject()) return;

        var (active, aProjPath, aSessId, bProjPath, bSessId, swapped) = SettingsService.LoadFullSplitState();
        if (!active) return;

        // 세션ID(전역 유일) 우선 조회 → 프로젝트는 그 세션의 소속으로 역산. 경로는 폴백.
        var aSess = aSessId != null ? _projects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == aSessId) : null;
        var aProj = aSess != null ? _projects.FirstOrDefault(p => p.Tabs.Contains(aSess))
              : _projects.FirstOrDefault(p => p.Path == aProjPath);
        var bSess = bSessId != null ? _projects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == bSessId) : null;
        // 경로 폴백은 좌측과 같은 경로면 '좌측 프로젝트 객체'를 쓴다 — 같은 폴더를 등록한 다른 프로젝트
        // 항목이 잡히면 우측에 엉뚱한 프로젝트가 열린다(같은 프로젝트 분할 복원도 깨짐).
        var bProj = bSess != null ? _projects.FirstOrDefault(p => p.Tabs.Contains(bSess))
              : (aProj != null && string.Equals(aProjPath, bProjPath, StringComparison.OrdinalIgnoreCase)
                    ? aProj
                    : _projects.FirstOrDefault(p => p.Path == bProjPath));

        // EnableSplit: 레이아웃만 생성(persist=false로 PersistSplitState/SyncShellToFocusedPane 스킵)
        EnableSplit(null, null, animate: false, persist: false);
        // EnableSplit이 PaneBCol star를 저장된 값으로 설정했지만 레이아웃이 아직 반영 안 됨 → Loaded에서 처리
        Dispatcher.BeginInvoke(() => RestoreSplitState_AfterLayout(aProj, aSess, bProj, bSess, swapped),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>레이아웃 적용 후 내용물과 스왑을 복원한다.</summary>
    private void RestoreSplitState_AfterLayout(ProjectItem? aProj, SessionItem? aSess, ProjectItem? bProj, SessionItem? bSess, bool swapped)
    {
        // 복원 시 좌측 패널(기존 메인 웹뷰)은 세션을 전체폭으로 fit 했다가 절반으로 줄어 리플로우돼 깨진다.
        // 두 패널을 커튼으로 덮어 전환 중 중간(전체) 폭 fit 을 억제하고, 폭이 절반으로 안정된 뒤 fit·재동기·fade.
        PaneA.CoverForTransition();
        PaneB.CoverForTransition();
        // 같은 프로젝트 분할이면 우측 refs 를 OpenSession(중간 PersistSplitState 유발) 전에 스냅샷.
        var restoreSnap = (aProj != null && ReferenceEquals(aProj, bProj)) ? SnapshotSplitRefs(aProj!)
                                                                          : (new List<string>(), (string?)null, (string?)null);
        if (!swapped)
        {
            // 기본: PaneA=좌(저장된 left 내용), PaneB=우(저장된 right 내용)
            if (aSess != null) PaneA.OpenSession(aSess);
            else if (aProj != null) PaneA.SelectProject(aProj);
            if (bSess != null) PaneB.OpenSession(bSess);
            else if (bProj != null) PaneB.SelectProject(bProj);
        }
        else
        {
            // 스왑 상태: PaneB가 좌측, PaneA가 우측에 오도록 내용물과 컬럼을 맞바꾼다.
            // left 내용 → PaneB(스왑 후 좌측), right 내용 → PaneA(스왑 후 우측)
            if (aSess != null) PaneB.OpenSession(aSess);
            else if (aProj != null) PaneB.SelectProject(aProj);
            if (bSess != null) PaneA.OpenSession(bSess);
            else if (bProj != null) PaneA.SelectProject(bProj);
            SwapPanePositions(); // 컬럼 교환 + _panesSwapped=true
        }

        // 좌/우가 같은 프로젝트면(= "분할 보기"로 한 탭만 우측에 띄웠던 상태) 우측은 그 탭만 격리,
        // 좌측은 그 탭 숨김으로 복원한다. 안 하면 양쪽 다 전체 탭이 뜬다(격리는 휘발성이라 재시작 시 소실).
        // FullSplitState 는 세션ID/프로젝트경로만 저장하므로, 우측이 파일이었던 경우(bSess=null &&
        // 좌우 동일 프로젝트)는 프로젝트에 기억된 SplitPartnerFilePath 로 파일을 열어 격리한다.
        if (aProj != null && ReferenceEquals(aProj, bProj))
        {
            // 우측 전체 탭 집합 복원(스냅샷 사용). 저장분이 없으면 기존 단일 파트너 격리로 폴백.
            if (!RestoreSameProjectSplit(aProj, restoreSnap.Item1, restoreSnap.Item2, restoreSnap.Item3))
            {
                if (bSess != null)
                {
                    RightPane.IsolateTab(bSess);
                    LeftPane.HideTabInPane(bSess);
                }
                else if (!string.IsNullOrEmpty(aProj.SplitPartnerFilePath) && System.IO.File.Exists(aProj.SplitPartnerFilePath)
                         && RightPane.OpenFileTabForPartner(aProj, aProj.SplitPartnerFilePath) is { } pf)
                {
                    RightPane.IsolateTab(pf);
                    LeftPane.HideTabInPane(pf);
                }
            }
        }

        _focusedPane = PaneA;
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
        // 좌우가 각자 다른 타이밍에 뜨지 않게, 둘 다 준비되면 동시에 커튼을 걷는다(느린 쪽 기준).
        RevealPanesSynced();
        // 복원 완료 시점에 분할 상태 저장(Loaded 이후 올바른 내용물로 PersistSplitState가 불리게)
        PersistSplitState();
    }

    private void DisableSplit(bool animate = true)
    {
        if (!_splitActive) return;
        _splitActive = false;

        // PaneB 가 접힘 애니메이션과 비분할 최종 상태에서 0px까지 내려갈 수 있게 제한을 먼저 해제한다.
        PaneACol.MinWidth = 0;
        PaneBCol.MinWidth = 0;

        // 분할을 '사용자가 직접' 닫으면(animate=true) 너비 비율을 초기화 → 다음에 다시 열 때 1:1 로 시작.
        // 프로젝트 전환(animate=false)으로 잠시 닫힐 땐 보존한다 — 초기화하면 분할 프로젝트로 돌아올 때
        // 모든 세션이 떠나기 전과 다른 폭으로 열려 ConPTY 리플로우(스크롤 튐·줄 갈라짐)를 일으킨다.
        if (animate) SettingsService.SaveSplitBStar(1.0);

        // 좌측(주) 패널 콘텐츠를 유지, 우측은 버린다. 스왑 상태에서는 유지 콘텐츠가 PaneB 에 있을 수 있다.
        var keep = LeftPane;
        var drop = RightPane;
        bool swapped = !ReferenceEquals(keep, PaneA);

        // "분할 보기"로 keep 패널이 숨겼던 탭들 — 분할 해제 후 최종 홈(PaneA)의 탭바 맨 끝에 이어붙인다.
        var (hiddenTabs, wasIsolated) = keep.CaptureSplitCloseState();

        if (swapped)
        {
            // 비분할의 주 패널은 PaneA 이므로, 스왑으로 PaneB 에 있던 유지 콘텐츠를 PaneA 로 되돌린다(애니메이션 동안 좌측에 보이도록 먼저 처리).
            // (분할 해제 시점이라 한 번의 재부착은 허용 — '단순 위치 변경'이 아니다.)
            var proj = keep.ActiveProject;
            var sess = keep.ActiveSession;
            drop.ClearForHide();   // PaneA(=drop) 비우기
            keep.ClearForHide();   // PaneB(=keep) 콘텐츠 떼기
            if (sess != null) PaneA.OpenSession(sess);
            else if (proj != null) PaneA.SelectProject(proj);
            // 컬럼 정규화: PaneA=col0, PaneB=col2.
            Grid.SetColumn(PaneA, 0);
            Grid.SetColumn(PaneB, 2);
            _panesSwapped = false;
            PaneA.IsRightPane = false;
            PaneB.IsRightPane = true;
        }
        // 비스왑일 땐 PaneB 의 콘텐츠를 접힘 애니메이션이 끝난 뒤 정리한다(축소되며 사라지는 인상).

        PaneA.ApplySplitCloseState(hiddenTabs, wasIsolated);

        _focusedPane = PaneA;
        foreach (var p in _panes) p.SetSplitActive(false);
        UpdateCenterRightBorder();   // 최우측이 다시 PaneA 로 정규화됨
        SyncShellToFocusedPane();
        UpdatePaneRoles();
        PaneA.RefreshSplitIndicator();
        PaneB.RefreshSplitIndicator();

        _ = AnimateSplitCloseAsync(swapped, animate);
    }

    /// <summary>분할 접힘: 두 패널 터미널을 스냅샷으로 정지한 뒤 PaneB 를 50%→0% 로 접고,
    /// 완료 시 PaneB 숨김·세션 배선 해제·컬럼 정규화 후 PaneA 를 라이브로 복원한다.
    /// animate=false 면 슬라이드 없이 즉시 최종 레이아웃으로 축소한다(다른 프로젝트로 전환 시).</summary>
    private async Task AnimateSplitCloseAsync(bool swapped, bool animate = true)
    {
        void Finish()
        {
            // PaneB 를 먼저 숨긴다(커버가 올라온 채로). 순서가 중요: ClearForHide→FadeNow 가 커버를 먼저 걷으면
            // collapse 전 한 프레임 동안 우측 라이브 터미널(전환 시 웹 커버로 덮여 있던 50% 폭)이 번쩍인다
            // (= 분할 프로젝트를 처음 떠날 때의 깜빡임). 숨긴 뒤 커버를 걷으면 그 제거가 화면에 안 보인다.
            PaneB.Visibility = Visibility.Collapsed;
            PaneSplitter.Visibility = Visibility.Collapsed;
            PaneBCol.Width = new GridLength(0);
            PaneSplitterCol.Width = new GridLength(0);
            PaneACol.Width = new GridLength(1, GridUnitType.Star);
            // 비스왑: 숨긴 뒤 상태만 비우되 xterm/ready 는 보존(disposeTerminal:false) — 좌측 패널처럼
            // 터미널을 살려두면 다른(비분할) 프로젝트에 갔다 이 분할로 돌아올 때 스피너·리로드·스크롤 튐 없이
            // 즉시 재활성화된다. (스왑은 유지 콘텐츠를 PaneA 로 재부착하므로 더블 배선 방지 위해 위에서 dispose 함.)
            if (!swapped) PaneB.ClearForHide(disposeTerminal: false);
            PaneB.ResumeTerminalOnly();   // 숨겨진 PaneB 의 스냅샷 오버레이 정리(다음 분할 때 라이브 위에 안 남도록). PaneB 는 hide 되므로 collapse 경로 유지.
            // 살아남는 claude 화면은 분할 열기 직전 전체폭 셀 스냅샷과 비교해, 최종폭 재렌더가 새로 만든
            // 큰 내부 공백만 로컬 xterm 버퍼에서 복원한다. clear/입력/세션 재시작은 하지 않는다.
            PaneA.ResumeTerminalOnly(webCover: true, recoverWiden: true);
            UpdatePaneFocusVisual(animate: false);
            PersistSplitState();
        }

        if (!animate) { Finish(); return; }

        // PaneA: webCover(HWND 유지) — 50%→100% 리사이즈 중 재합성 플래시 없음. PaneB: collapse 경로 유지(어차피 hide/정리 대상, Finish 순서 의존).
        await Task.WhenAll(PaneA.SuspendTerminalOnlyAsync(anchorTopLeft: true, webCover: true), PaneB.SuspendTerminalOnlyAsync(anchorTopLeft: true));
        PaneB.SetEmptyTextWrapping(false); // 접힘 애니메이션 중 줄바꿈 방지
        AnimatePaneSplit(1, 0, Finish);
    }

    /// <summary>분할 중일 때 포커스된 패널을 1px 테마색 프레임으로 표시한다.
    /// 단일 패널이면 모두 끈다.</summary>
    private void UpdatePaneFocusVisual(bool animate = true)
    {
        foreach (var p in _panes)
            p.SetFocusedVisual(false);

        if (!_splitActive) return;

        _focusedPane.SetFocusedVisual(true);
    }

    private SessionItem? FindSession(string id)
        => _projects.Concat(_archivedProjects).SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == id);

    /// <summary>방 상태 이벤트는 roomId만으로 신뢰하지 않는다. 이벤트 소스와 방의 실제 에이전트가
    /// 일치할 때만 UI·세션ID·완료기록을 변경한다.</summary>
    private SessionItem? FindOwnedSession(string roomId, string sourceAgentId, string eventName)
    {
        var session = FindSession(roomId);
        var ownerAgentId = session == null
            ? null
            : string.IsNullOrWhiteSpace(session.AgentId)
                ? SettingsService.LoadAgentForRoom(roomId)
                : session.AgentId;
        if (session != null && AgentEventOwnership.IsMatch(ownerAgentId, sourceAgentId))
            return session;

        var logKey = $"{roomId}\n{sourceAgentId}\n{eventName}";
        var now = DateTime.UtcNow;
        if (!_rejectedAgentEventLogs.TryGetValue(logKey, out var last)
            || now - last >= TimeSpan.FromSeconds(30))
        {
            _rejectedAgentEventLogs[logKey] = now;
            DiagLog.Write(
                $"tracking event rejected room={roomId} owner={ownerAgentId ?? "<missing>"} source={sourceAgentId} event={eventName}");
        }
        return null;
    }

    /// <summary>앱 재시작 시 외부 lock/ticket과 저장 상태를 맞추고 내부 중복 실행을 막는다.</summary>
    private void ReconcileExternalSessions()
    {
        bool changed = false;
        foreach (var session in _projects.Concat(_archivedProjects)
                     .SelectMany(project => project.Tabs).OfType<SessionItem>())
        {
            var state = ExternalSessionService.GetState(session.Id);
            if (state == ExternalSessionState.Stopped)
            {
                if (session.IsExternal)
                {
                    session.IsExternal = false;
                    TerminalSessionManager.Instance.ClearDisposedRoom(session.Id);
                    changed = true;
                }
                ExternalSessionService.CleanupStoppedSession(session.Id);
                continue;
            }

            if (!session.IsExternal)
            {
                session.IsExternal = true;
                changed = true;
            }
            session.IsAlive = false;
            session.IsBusy = false;
            session.IsWaitingChoice = false;
            TerminalSessionManager.Instance.DisposeRoom(session.Id, purgeTracking: false);
        }

        if (changed)
            WorkspaceStore.Save(_projects, _archivedProjects);
    }

    /// <summary>외부 터미널 종료를 lock 해제로 감지해 해당 방을 다시 내부에서 열 수 있게 한다.</summary>
    private void CheckExternalSessions()
    {
        if (_externalSessionChecking) return;
        _externalSessionChecking = true;
        try
        {
            bool changed = false;
            foreach (var session in _projects.Concat(_archivedProjects)
                         .SelectMany(project => project.Tabs).OfType<SessionItem>()
                         .Where(session => session.IsExternal).ToList())
            {
                // lock 상태보다 먼저 읽고, stopped로 바뀐 경우 한 번 더 읽는다. 프록시는 로그를
                // 완전히 flush한 뒤 lock을 놓으므로 이 순서면 종료 경계의 마지막 바이트도 보존된다.
                foreach (var pane in _panes)
                    pane.PumpExternalSessionOutput(session);

                if (ExternalSessionService.GetState(session.Id) != ExternalSessionState.Stopped)
                {
                    if (TerminalSessionManager.Instance.Get(session.Id) != null)
                        TerminalSessionManager.Instance.DisposeRoom(session.Id, purgeTracking: false);
                    continue;
                }

                foreach (var pane in _panes)
                    pane.PumpExternalSessionOutput(session);
                // 종료 순간 UI가 잠시 뒤처졌다면 다음 tick에도 계속 읽고, 모든 실제 미러가
                // 파일 끝까지 도달한 뒤에만 로그를 정리한다.
                if (_panes.Any(pane => !pane.IsExternalSessionOutputCaughtUp(session)))
                    continue;
                session.IsExternal = false;
                session.IsAlive = false;
                session.IsBusy = false;
                session.IsWaitingChoice = false;
                TerminalSessionManager.Instance.ClearDisposedRoom(session.Id);
                foreach (var pane in _panes)
                    pane.OnExternalSessionEnded(session);
                ExternalSessionService.CleanupStoppedSession(session.Id);
                changed = true;
            }

            if (!changed) return;
            WorkspaceStore.Save(_projects, _archivedProjects);
            UpdateSessionBusyDisplay();
            RefreshCardGroups();
        }
        finally
        {
            _externalSessionChecking = false;
        }
    }

    private async void OpenSessionInExternalTerminal(SessionItem session)
    {
        if (session.IsExternal || !_externalSessionLaunches.Add(session.Id)) return;
        try
        {
            await OpenSessionInExternalTerminalCore(session);
        }
        finally
        {
            _externalSessionLaunches.Remove(session.Id);
        }
    }

    private async Task OpenSessionInExternalTerminalCore(SessionItem session)
    {
        if (session.IsExternal) return;
        if (session.IsBusy)
        {
            ConfirmDialog.Alert("외부 터미널로 열기",
                "응답이 완료된 후 외부 터미널로 열 수 있습니다.",
                iconKey: "IconInfo");
            return;
        }
        var project = _projects.Concat(_archivedProjects)
            .FirstOrDefault(item => item.Tabs.Contains(session));
        if (project == null) return;
        if (!Directory.Exists(project.Path))
        {
            ConfirmDialog.Alert("외부 터미널로 열기",
                "프로젝트 폴더를 찾을 수 없습니다.", iconKey: "IconTriangleAlert");
            return;
        }
        // WT 없어도 일반 콘솔(powershell)로 폴백하므로 별도 차단하지 않는다.

        var agentId = string.IsNullOrWhiteSpace(session.AgentId)
            ? SettingsService.LoadAgentForRoom(session.Id)
            : session.AgentId;
        var agent = AgentRegistry.Find(agentId) ?? AgentRegistry.GetDefault();
        var launchError = ExternalSessionService.GetLaunchError(session.Id, agent.Id);
        if (launchError != null)
        {
            ConfirmDialog.Alert("외부 터미널로 열기",
                launchError, iconKey: "IconTriangleAlert");
            return;
        }

        var liveTerminal = TerminalSessionManager.Instance.Get(session.Id);
        int preferredCols = liveTerminal?.Cols ?? 120;
        int preferredRows = liveTerminal?.Rows ?? 30;
        if (session.IsBusy)
        {
            ConfirmDialog.Alert("외부 터미널로 열기",
                "응답이 완료된 후 외부 터미널로 열 수 있습니다.",
                iconKey: "IconInfo");
            return;
        }

        string token;
        try
        {
            token = ExternalSessionService.PrepareLaunch(session.Id);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Alert("외부 터미널로 열기",
                $"외부 세션을 준비하지 못했습니다.\n{ex.Message}", iconKey: "IconTriangleAlert");
            return;
        }

        session.IsExternal = true;
        session.IsAlive = false;
        session.IsBusy = false;
        session.IsWaitingChoice = false;
        WorkspaceStore.Save(_projects, _archivedProjects);

        try
        {
            await Task.WhenAll(_panes.Select(pane => pane.SetSessionExternalAsync(session)));
            foreach (var pane in _panes) pane.SetExternalLaunching(session); // 열리는 동안 스피너
            UpdateSessionBusyDisplay();
            await TerminalSessionManager.Instance.GracefulDisposeRoomsAsync(new[] { session.Id });
            TerminalSessionManager.Instance.DisposeRoom(session.Id, purgeTracking: false);

            var result = await ExternalSessionService.LaunchAsync(
                session.Id,
                session.Name,
                agent.Id,
                project.Path,
                token,
                preferredCols,
                preferredRows,
                App.CommittedTheme);
            if (result.Success)
            {
                DiagLog.Write($"ExternalSession started room={session.Id} agent={agent.Id}");
                foreach (var pane in _panes)
                    pane.MarkExternalSessionReady(session); // 스피너 → 실행 중(버튼)
                return;
            }

            if (ExternalSessionService.CancelLaunch(session.Id))
            {
                DiagLog.Write($"ExternalSession started after timeout room={session.Id} agent={agent.Id}");
                foreach (var pane in _panes)
                    pane.MarkExternalSessionReady(session);
                return;
            }
            session.IsExternal = false;
            TerminalSessionManager.Instance.ClearDisposedRoom(session.Id);
            WorkspaceStore.Save(_projects, _archivedProjects);
            foreach (var pane in _panes)
                pane.OnExternalSessionEnded(session);
            ConfirmDialog.Alert("외부 터미널로 열기", result.Error ?? "외부 세션을 시작하지 못했습니다.",
                iconKey: "IconTriangleAlert");
        }
        catch (Exception ex)
        {
            if (ExternalSessionService.CancelLaunch(session.Id))
            {
                DiagLog.Write($"ExternalSession started after launch error room={session.Id} agent={agent.Id}");
                foreach (var pane in _panes)
                    pane.MarkExternalSessionReady(session);
                return;
            }
            session.IsExternal = false;
            TerminalSessionManager.Instance.ClearDisposedRoom(session.Id);
            WorkspaceStore.Save(_projects, _archivedProjects);
            foreach (var pane in _panes)
                pane.OnExternalSessionEnded(session);
            ConfirmDialog.Alert("외부 터미널로 열기",
                $"외부 세션을 시작하지 못했습니다.\n{ex.Message}", iconKey: "IconTriangleAlert");
        }
        finally
        {
            UpdateSessionBusyDisplay();
            RefreshCardGroups();
        }
    }

    /// <summary>오버레이 "인앱으로 가져오기" — 외부 프록시에 종료를 요청한다. 프록시가 lock 을 놓으면
    /// CheckExternalSessions 가 감지해 내부 resume(OnExternalSessionEnded)로 복귀시킨다.</summary>
    private void ReturnSessionFromExternal(SessionItem session)
    {
        if (!session.IsExternal) return;
        ExternalSessionService.RequestReturn(session.Id);
        DiagLog.Write($"ExternalSession return requested room={session.Id}");
        CheckExternalSessions(); // 타이머 tick 을 기다리지 않고 즉시 한 번 확인(반응성)
    }

    /// <summary>/send-new: 부모(A) 세션의 자식 세션을 만들어 열고, 부팅 완료되면 브리핑을 주입한다(InjectWhenReady).
    /// 자식은 부모와 같은 프로젝트·같은 에이전트의 fresh 세션(코드베이스·CLAUDE.md 자동 확보)이며,
    /// 완료되면 부모 A에게 완료 알림이 돌아온다(콜백). 인박스 이벤트에서 UI 스레드로 마샬링돼 호출된다.</summary>
    private void CreateChildAndDispatch(string parentRoomId, string sessionName, string brief)
    {
        try
        {
            var parent = FindSession(parentRoomId);
            if (parent == null) return;
            var proj = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(parent));
            if (proj == null) return;

            var agentId = string.IsNullOrEmpty(parent.AgentId)
                ? SettingsService.LoadAgentForRoom(parent.Id) : parent.AgentId;

            // 자식 이름 = A가 지시 내용으로 지은 짧은 이름(≤15자). 비면 기본명 폴백.
            var name = (sessionName ?? "").Trim();
            if (name.Length > 15) name = name.Substring(0, 15);
            if (string.IsNullOrEmpty(name)) name = "위임 작업";

            var child = new SessionItem
            {
                Name = name,
                AgentId = agentId,
                ParentSessionId = parent.Id,
            };
            proj.Tabs.Add(child);
            proj.IsExpanded = true;
            SettingsService.SaveClaudeCodeRoomDir(child.Id, proj.Path);
            SettingsService.SaveAgentForRoom(child.Id, agentId);
            WorkspaceStore.Save(_projects);

            // 부모가 표시된 패널에서 터미널만 백그라운드 시작한다. 현재 탭 선택과 포커스는 유지.
            var hostPane = _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveSession, parent))
                ?? _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveProject, proj))
                ?? _focusedPane;
            hostPane.PreloadSession(child);
            _sessionCommandInbox.InjectWhenReady(child.Id, brief);
        }
        catch { /* best-effort */ }
    }

    /// <summary>/dvzc:send 대상이 꺼져 있으면 선택·포커스를 바꾸지 않고 기존 세션을 복원한 뒤 지시한다.
    /// 명령 파일은 실제 터미널 주입이 성공한 뒤에만 인박스가 삭제하므로 앱 재시작에도 보존된다.</summary>
    private void StartDormantSessionAndDispatch(string roomId, string message, bool submit, string commandPath)
    {
        try
        {
            var target = FindSession(roomId);
            var proj = target == null ? null : _projects.FirstOrDefault(p => p.Tabs.Contains(target));
            if (target == null || proj == null)
            {
                _sessionCommandInbox.DiscardPendingCommand(commandPath);
                return;
            }

            var agentId = string.IsNullOrEmpty(target.AgentId)
                ? SettingsService.LoadAgentForRoom(target.Id) : target.AgentId;
            if (string.IsNullOrEmpty(agentId)) agentId = AgentRegistry.DefaultAgentId;

            var running = TerminalSessionManager.Instance.Get(target.Id) is { IsAlive: true };
            var terminalReady = _panes.Any(p => p.IsTerminalReady(target.Id));
            // 새 시작 신호의 기준 시각을 preload 전에 캡처해야 이전 실행의 추적 파일을 준비 완료로 오인하지 않는다.
            _sessionCommandInbox.InjectPendingWhenReady(
                target.Id, message, submit, commandPath, agentId, requireFreshReadySignal: !terminalReady);

            if (running) return;

            var hostPane = _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveProject, proj)) ?? _focusedPane;
            hostPane.PreloadSession(target);
        }
        catch
        {
            // 복구 가능한 일시 오류일 수 있으므로 명령 파일을 폐기하지 않는다.
            _sessionCommandInbox.RetryPendingCommand(commandPath);
        }
    }

    /// <summary>완료기록 헤더("진행중/응답 대기 중 N개")·대기 카드를 IsBusy/IsWaitingChoice 와 주기 동기화.
    /// 대기/진행 감지는 모두 훅·이벤트(Notification/jsonl/플러그인)가 담당하고, 여기선 화면 스크래핑 없이
    /// 표시만 맞춘다 — Esc/세션 종료 등 이벤트 밖 경로에서 플래그가 바뀌어도 헤더가 즉시 따라오게.</summary>
    private void StartBusyDisplaySync()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) => { UpdateSessionBusyDisplay(); RefreshSessionHistoryTracking(); }; // 동일값 set 은 no-op → 깜빡임/비용 없음
        t.Start();
    }

    /// <summary>완료기록 카드의 취소선 상태를 현재 워크스페이스와 동기화한다.
    /// - 세션이 어느 프로젝트 카드에도 없으면(탭 닫힘/삭제) → 세션명 취소선
    /// - 프로젝트가 활성/보관 어디에도 없으면(삭제됨) → 프로젝트명 취소선
    /// 파생 상태라 저장하지 않고, 이벤트 밖 경로도 커버되게 500ms 주기로 맞춘다(동일값 set 은 no-op).</summary>
    private void RefreshSessionHistoryTracking()
    {
        foreach (var r in _sessionDoneRecords)
        {
            r.SessionMissing = FindSession(r.SessionId) == null;

            if (string.IsNullOrWhiteSpace(r.ProjectName))
                r.ProjectMissing = false;
            else
                r.ProjectMissing = !_projects.Concat(_archivedProjects).Any(p =>
                    string.Equals(p.Name, r.ProjectName, StringComparison.Ordinal) ||
                    string.Equals(System.IO.Path.GetFileName(p.Path.TrimEnd('\\', '/')), r.ProjectName, StringComparison.Ordinal));
        }
    }

    private void AddSessionCompletionRecord(SessionItem s)
    {
        var proj = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(s));
        var projName = proj?.Name ?? "";
        var sessName = string.IsNullOrWhiteSpace(s.Name) ? "세션" : s.Name;

        _sessionDoneRecords.Insert(0, new SessionCompletionRecord
        {
            SessionId = s.Id,
            SessionName = sessName,
            ProjectName = projName,
            AgentId = s.AgentId,
            LastMessage = string.Equals(s.AgentId, "grok", StringComparison.OrdinalIgnoreCase)
                ? GrokHookService.NormalizeLastMessage(s.LastMessage ?? "")
                : s.LastMessage?.Trim() ?? "",
            CompletedAt = DateTime.Now,
        });

        while (_sessionDoneRecords.Count > MaxSessionDoneRecords)
            _sessionDoneRecords.RemoveAt(_sessionDoneRecords.Count - 1);
        SettingsService.SaveSessionHistoryRecords(
            new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
        UpdateSessionHistoryEmpty();
    }

    /// <summary>goal 모드 골 단위 완료 카드(transcript 백필). 일반 완료 카드와 같은 목록에 쌓이되
    /// 내용은 사용자 프롬프트가 아니라 완료된 골의 목표문, 시각은 transcript 기록 시각을 쓴다.
    /// 지연 flush 로 뒤늦게 파싱된 과거 완료라 토스트/작업표시줄 알림은 내지 않는다.</summary>
    private void AddGoalCompletionRecord(SessionItem s, string objective, DateTime completedAt)
    {
        var proj = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(s));
        var projName = proj?.Name ?? "";
        var sessName = string.IsNullOrWhiteSpace(s.Name) ? "세션" : s.Name;

        _sessionDoneRecords.Insert(0, new SessionCompletionRecord
        {
            SessionId = s.Id,
            SessionName = sessName,
            ProjectName = projName,
            AgentId = s.AgentId,
            LastMessage = objective,
            CompletedAt = completedAt,
        });

        while (_sessionDoneRecords.Count > MaxSessionDoneRecords)
            _sessionDoneRecords.RemoveAt(_sessionDoneRecords.Count - 1);
        SettingsService.SaveSessionHistoryRecords(
            new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
        UpdateSessionHistoryEmpty();
    }


    /// <summary>시작 시 모든 세션의 IsBusy 를 false 로 초기화. 프로그램 종료 시 진행 중이던 상태는 취소됨.</summary>
    private void ResetAllSessionBusy()
    {
        foreach (var p in _projects.Concat(_archivedProjects))
            foreach (var t in p.Tabs)
                if (t is SessionItem s) { s.IsBusy = false; s.IsWaitingChoice = false; }
    }

    private void UpdateSessionBusyDisplay()
    {
        int count = 0, waiting = 0;
        foreach (var p in _projects.Concat(_archivedProjects))
            foreach (var t in p.Tabs)
                if (t is SessionItem s)
                {
                    // 응답 대기 중인 세션은 진행중에서 빼고 대기로만 센다.
                    if (s.IsWaitingChoice) waiting++;
                    else if (s.IsBusy) count++;
                }
        if (count > 0)
        {
            SessionBusySpinner.Visibility = Visibility.Visible;
            SessionBusyLabel.Text = $"진행중인 세션 {count}개";
            SessionBusyRow.Visibility = Visibility.Visible;
        }
        else
        {
            SessionBusySpinner.Visibility = Visibility.Collapsed;
            SessionBusyLabel.Text = "진행중인 세션 없음.";
            // 진행중이 없어도 응답 대기 세션이 있으면 "없음." 줄은 숨긴다(대기 줄만 표시).
            SessionBusyRow.Visibility = waiting > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        // 응답 대기(선택지) 세션이 있을 때만 둘째 줄 노출 → 이때만 헤더 높이가 늘어난다.
        if (waiting > 0)
        {
            SessionWaitingLabel.Text = $"응답 대기 중 {waiting}개";
            SessionWaitingRow.Visibility = Visibility.Visible;
            // 진행중 줄이 보일 때만 위 간격(4px). 대기만 단독이면 0 → 가운데 정렬에서 안 밀림.
            SessionWaitingRow.Margin = SessionBusyRow.Visibility == Visibility.Visible
                ? new Thickness(0, 4, 0, 0) : new Thickness(0);
        }
        else
        {
            SessionWaitingRow.Visibility = Visibility.Collapsed;
        }

        SyncWaitingCards();
    }

    /// <summary>응답 대기(선택지) 세션 카드 목록을 IsWaitingChoice 상태에 맞춰 동기화한다.
    /// 완료기록 위에 항상 표시, 둘 다 있으면 세퍼레이터로 구분. 변화가 있을 때만 컬렉션을 건드려 깜빡임 방지.</summary>
    private void SyncWaitingCards()
    {
        // 더 이상 대기 아님(또는 죽은 세션) → 제거.
        for (int i = _waitingSessions.Count - 1; i >= 0; i--)
            if (!_waitingSessions[i].IsWaitingChoice) _waitingSessions.RemoveAt(i);
        // 새로 대기 진입한 세션 → 최신이 위로 오도록 맨 앞에 삽입.
        foreach (var p in _projects)
            foreach (var t in p.Tabs.OfType<SessionItem>())
                if (t.IsWaitingChoice && !_waitingSessions.Contains(t))
                    _waitingSessions.Insert(0, t);

        bool any = _waitingSessions.Count > 0;
        WaitingList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        WaitingSeparator.Visibility = (any && _sessionDoneRecords.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SessionHistoryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        const double edgeTolerance = 1.0;
        bool top = sv.VerticalOffset > edgeTolerance;
        bool bottom = sv.VerticalOffset < sv.ScrollableHeight - edgeTolerance;
        SessionHistoryFadeTop.Visibility = top ? Visibility.Visible : Visibility.Collapsed;
        SessionHistoryFadeBottom.Visibility = bottom ? Visibility.Visible : Visibility.Collapsed;
        // 상하 spacer 가 콘텐츠와 함께 스크롤된다.
        // 맨 위/아래에서만 8px 여백이 보이고 중간에서는 카드가 패널 경계에 붙으며 extent 는 변하지 않는다.
    }

    private void UpdateSessionHistoryEmpty()
        => SessionHistoryEmpty.Visibility = _sessionDoneRecords.Count == 0 ? Visibility.Visible : Visibility.Collapsed;



    private void SessionHistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionCompletionRecord r) return;
        var s = FindSession(r.SessionId);
        if (s == null)
        {
            // 세션 삭제됨 → 열 수 없으니 클릭만으로 읽음 처리(하이라이트 제거)
            if (!r.IsRead)
            {
                r.IsRead = true;
                SettingsService.SaveSessionHistoryRecords(
                    new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
            }
            return;
        }
        try { Activate(); OpenSession(s); } catch { /* best effort */ }
    }

    /// <summary>완료기록 카드의 x 버튼 → 해당 기록 한 건만 제거하고 저장. (카드 클릭=세션 열기와 분리)</summary>
    private void HistoryDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 MouseLeftButtonUp(세션 열기)로 버블링 방지
        if ((sender as FrameworkElement)?.DataContext is not SessionCompletionRecord r) return;
        _sessionDoneRecords.Remove(r);
        SettingsService.SaveSessionHistoryRecords(
            new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
        UpdateSessionHistoryEmpty();
    }

    /// <summary>완료기록 카드 우클릭 메뉴의 체크/체크 해제 → 표시 상태 토글 후 저장.</summary>
    private void HistoryCheck_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SessionCompletionRecord r) return;
        r.IsChecked = !r.IsChecked;
        SettingsService.SaveSessionHistoryRecords(
            new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
    }

    /// <summary>응답 대기 카드 클릭 → 해당 세션을 열어 선택지에 답할 수 있게 한다.</summary>
    private void WaitingCard_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionItem s) return;
        try { Activate(); OpenSession(s); } catch { /* best effort */ }
    }

    private void ClearHistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionDoneRecords.Count == 0) return;
        if (!ConfirmDialog.Show("기록 지우기",
                "세션 완료기록을 초기화하시겠습니까?",
                okLabel: "지우기", danger: true))
            return;

        _sessionDoneRecords.Clear();
        SettingsService.SaveSessionHistoryRecords(
            new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
        UpdateSessionHistoryEmpty();
    }

    private void TogglePromptBtn_Click(object sender, RoutedEventArgs e)
    {
        bool next = !ShowFullPrompt;
        ShowFullPrompt = next;
        SettingsService.SaveShowFullPrompt(next);
    }

    private void MarkAllReadBtn_Click(object sender, RoutedEventArgs e)
    {
        bool changed = false;
        foreach (var r in _sessionDoneRecords)
        {
            if (!r.IsRead) { r.IsRead = true; changed = true; }
        }
        if (changed)
            SettingsService.SaveSessionHistoryRecords(
                new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
        UpdateSessionHistoryEmpty();
    }

    /// <summary>세션이 busy(true)→idle(false)로 바뀐 순간(=스피너 멈춤=응답 완료)에 종료 토스트를 띄운다.
    /// 설정에서 꺼져 있으면 무시. 클릭 시 해당 세션을 포커스 패널에 연다.</summary>
    // busy→idle 전이 완료기록 디바운스: 메인 Stop 훅이 SubagentStart 보다 먼저 발화하면 순간 idle 이
    // emit 돼 가짜 완료기록이 찍힌다(직후 substart 가 running 재무장 → 스피너 재진입). 짧게 정착 대기 후
    // 그때도 여전히 idle 이면 진짜 완료로 기록한다. 정착 창 내 running 재무장 시 취소(레이스는 완료 아님).
    private readonly Dictionary<SessionItem, System.Windows.Threading.DispatcherTimer> _finishDebounce = new();
    private readonly Dictionary<SessionItem, System.Windows.Threading.DispatcherTimer> _waitingNotificationDebounce = new();
    // 정착 창: 메인 Stop 훅 프로세스와 SubagentStart 훅 프로세스 발화 간극(보통 <1s)을 덮는다.
    // 이 시간이 지나면 SubagentStart 가 run 파일을 이미 썼을 것이므로 만료 시점의 파일시스템 진실이 확정적.
    private const int FinishSettleMs = 1200;

    private bool _taskbarAttentionActive;

    /// <param name="isStillActive">만료 시점에 방이 실제로 활성인지 파일시스템 진실로 재확인하는 함수(claude 전용).
    /// null 이면 IsBusy 플래그만 사용. BusyChanged(true) 이벤트가 누락돼도 원본을 직접 봐서 오판을 막는다.</param>
    private void NotifyIfSessionFinished(SessionItem? s, bool wasBusy, bool nowBusy, Func<bool>? isStillActive = null,
        Func<bool>? isRealFinish = null)
    {
        if (s == null) return;
        if (nowBusy)
        {
            // running 재무장 → 대기중이던(레이스성) 완료기록 취소. 이미 확정 발행된 카드는 지우지 않는다 —
            // 완료 카드는 프롬프트 완료마다 히스토리로 쌓이는 것이 spec 이고, 플랩 방지는 정착 디바운스가
            // 발행 "전"에 담당한다. (예전엔 busy 재진입 시 그 세션 카드를 전부 삭제해 기록이 안 쌓였다.)
            if (_finishDebounce.TryGetValue(s, out var pending)) { pending.Stop(); _finishDebounce.Remove(s); }
            return;
        }
        if (!wasBusy) return; // busy→idle 전이 아님
        // claude/gjc/opencode/grok/codex/kimi/antigravity: 가짜 idle 플랩 가능
        // → isStillActive 있으면 정착 창 + 파일/진실 재확인. 없는 소스만 즉시 확정.
        if (isStillActive == null)
        {
            if (isRealFinish == null || isRealFinish()) EmitSessionFinished(s);
            return;
        }
        if (_finishDebounce.TryGetValue(s, out var ex)) ex.Stop();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FinishSettleMs) };
        timer.Tick += (_, __) =>
        {
            timer.Stop();
            _finishDebounce.Remove(s);
            // 재무장 OR 파일시스템상 아직 활성(서브 run 파일/메인 플래그 존재) → 실제 완료 아님.
            if (s.IsBusy || (isStillActive?.Invoke() ?? false)) return;
            // isRealFinish(claude=턴종료 마커 소비): Stop/SessionEnd 가 실제 발화한 idle 만 완료로 확정.
            // main 플래그 유실 방에서 서브 드레인 공백이 idle 로 새어 카드가 Task 마다 찍히던 회귀 차단
            // (실측: 한 프롬프트에 12장). 마커 없음 = 메인 턴 아직 안 끝남 → 무발행.
            if (isRealFinish != null && !isRealFinish())
            {
                DevezCode.Services.DiagLog.Write($"busy[{s.Id}] 완료카드 스킵: 턴종료 마커 없음(서브 드레인 flap)");
                return;
            }
            EmitSessionFinished(s);
        };
        _finishDebounce[s] = timer;
        timer.Start();
    }

    private void EmitSessionFinished(SessionItem s)
    {
        // Claude/Codex/OpenCode/Gajae/Grok/Antigravity 공통 busy→idle 확정 지점.
        // 응답 리페인트 경계에서 WebView2 compositionend 가 누락되면 다음 한글이 중복될 수 있으므로,
        // 현재 이 방의 터미널에 실제 포커스가 있는 패널만 JS 에서 blur→focus 초기화한다.
        foreach (var pane in _panes) pane.Terminal.ResetImeAfterResponse(s.Id);
        AddSessionCompletionRecord(s);
        s.TriggerAttentionPulse(); // 탭·세션 행 완료 펄스(완료기록과 동일 게이트 — 서브 드레인 flap 제외)
        var proj = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(s));
        RequestTaskbarAttention();
        if (!SettingsService.LoadNotifySessionDoneEnabled()) return;

        var projName = proj?.Name ?? "";
        var sessName = string.IsNullOrWhiteSpace(s.Name) ? "세션" : s.Name;
        // 제목(큰 글씨)=프로젝트명, 본문(작은 글씨)=세션명 · 상태. 프로젝트명 없으면 세션명을 제목으로.
        var title = string.IsNullOrEmpty(projName) ? sessName : projName;
        var body  = string.IsNullOrEmpty(projName) ? "응답 완료" : $"{sessName} · 응답 완료";

        App.ShowNotification(title, body, () =>
        {
            try { Activate(); OpenSession(s); } catch { /* best effort */ }
        });
    }

    private void NotifyIfSessionWaiting(SessionItem? s, bool wasWaiting, bool nowWaiting, int notificationDelayMs = 0)
    {
        if (s == null) return;
        if (!nowWaiting)
        {
            if (_waitingNotificationDebounce.TryGetValue(s, out var pending))
            {
                pending.Stop();
                _waitingNotificationDebounce.Remove(s);
            }
            return;
        }
        if (wasWaiting) return;
        if (notificationDelayMs > 0)
        {
            if (_waitingNotificationDebounce.TryGetValue(s, out var existing)) existing.Stop();
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(notificationDelayMs),
            };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                _waitingNotificationDebounce.Remove(s);
                if (s.IsWaitingChoice) EmitSessionWaiting(s);
            };
            _waitingNotificationDebounce[s] = timer;
            timer.Start();
            return;
        }
        EmitSessionWaiting(s);
    }

    private void EmitSessionWaiting(SessionItem s)
    {
        s.TriggerAttentionPulse(); // 입력 대기 진입 펄스(확정 지점에서만 — setter 즉발 금지)
        RequestTaskbarAttention();
        if (!SettingsService.LoadNotifySessionDoneEnabled()) return;

        var proj = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(s));
        var projName = proj?.Name ?? "";
        var sessName = string.IsNullOrWhiteSpace(s.Name) ? "세션" : s.Name;
        var title = string.IsNullOrEmpty(projName) ? sessName : projName;
        var body  = string.IsNullOrEmpty(projName) ? "응답 대기" : $"{sessName} · 응답 대기";

        App.ShowNotification(title, body, () =>
        {
            try { Activate(); OpenSession(s); } catch { /* best effort */ }
        });
    }

    /// <summary>창이 비활성일 때 세션 완료/응답대기 발생을 작업표시줄 깜빡임으로 알린다.
    /// 포커스를 훔치거나 Topmost 를 건드리지 않아 전체화면 모드와 다른 전체화면 앱을 방해하지 않는다.</summary>
    private void RequestTaskbarAttention()
    {
        if (IsActive) return;
        var hwnd = _mainHwnd != IntPtr.Zero ? _mainHwnd : new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (IsWindowVisibleToUser(hwnd)) return; // 카카오톡식: 창이 보이면 깜빡이지 않고, 최소화/가려졌을 때만 알린다.

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = 0,
            dwTimeout = 0,
        };
        if (FlashWindowEx(ref info)) _taskbarAttentionActive = true;
    }

    private void StopTaskbarAttention()
    {
        if (!_taskbarAttentionActive) return;
        var hwnd = _mainHwnd != IntPtr.Zero ? _mainHwnd : new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_STOP,
            uCount = 0,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
        _taskbarAttentionActive = false;
    }

    /// <summary>헤더에 표시할 마지막 메시지 반영. true=헤더 갱신 필요.
    /// /clear·/new 는 타이틀로 복귀(빈 값), 그 외 슬래시 명령(/...)은 헤더 유지(무시).</summary>
    private static bool ApplyHeaderMessage(SessionItem s, string? msg)
    {
        var m = msg?.Trim() ?? "";
        if (m.StartsWith("/"))
        {
            if (m.Equals("/clear", StringComparison.OrdinalIgnoreCase) ||
                m.Equals("/new", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(s.LastMessage)) return false;
                s.LastMessage = "";
                return true;
            }
            // /compact 등 다른 슬래시 명령도 입력 그대로 표시(완료기록에 남김)
            if (s.LastMessage == m) return false;
            s.LastMessage = m;
            return true;
        }
        if (s.LastMessage == m) return false;
        s.LastMessage = m;
        return true;
    }

    // ── 사이드바 액션 → 포커스 패널로 위임 ────────────────────────────

    private void SelectProject(ProjectItem proj) => SelectProjectFromSidebar(proj);

    private void OpenGitRemote(ProjectItem project, string url)
    {
        if (SettingsService.LoadTerminalUrlOpenTarget() == TerminalUrlOpenTarget.DefaultBrowser)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            catch
            {
                ConfirmDialog.Alert("Git 저장소 웹에서 열기", "원격 저장소를 열 수 없습니다.",
                    iconKey: "IconTriangleAlert");
            }
            return;
        }

        SelectProjectFromSidebar(project);
        var pane = _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveProject, project)) ?? _focusedPane;
        _focusedPane = pane;
        var tabName = string.IsNullOrWhiteSpace(project.Name) ? "Git 저장소" : $"Git · {project.Name}";
        var tab = pane.AddBrowserTab(project, initialName: tabName, promptForName: false);
        tab?.Browser.NavigateToUrl(url);
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
    }

    private void SelectProjectFromSidebar(ProjectItem proj)
    {
        // 이미 어느 패널에 떠 있으면 그 패널로 포커스만(재로딩 없음).
        var existingPane = _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveProject, proj));
        DevezCode.Services.DiagLog.Write($"SelectProjectFromSidebar proj={proj.Name} existingPane={(existingPane == null ? "none" : (ReferenceEquals(existingPane, PaneA) ? "PaneA" : "PaneB"))} PaneA={PaneA.ActiveProject?.Name ?? "null"} PaneB={PaneB.ActiveProject?.Name ?? "null"} _splitActive={_splitActive}");
        if (existingPane != null) { FocusPaneOnly(existingPane); return; }

        // 새 프로젝트는 항상 메인(좌측) 패널에 연다. 분할 여부는 그 프로젝트의 SplitEnabled 로
        // SelectProjectIntoPane→ApplyProjectSplitForMainPane 이 결정한다(분할로 띄우거나 단일로).
        SelectProjectIntoPane(LeftPane, proj);
    }

    /// <summary>사이드바에서 세션 클릭 → 이미 떠 있는 패널이면 그 패널에서 활성화, 아니면 그 세션의
    /// 프로젝트를 메인(좌측) 패널에 열고 세션을 활성화한다. 분할 여부는 프로젝트의 SplitEnabled 로 결정.</summary>
    private void OpenSession(SessionItem session)
    {
        MarkSessionRead(session.Id);
        var parent = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(session));
        if (session.IsEffectivelyHidden && parent != null)
        {
            var newlyVisible = parent.UnhideSessionPath(session);
            foreach (var pane in _panes)
                foreach (var visible in newlyVisible)
                    pane.CancelSessionHide(visible.Id);
            WorkspaceStore.Save(_projects, _archivedProjects);
            RefreshCardGroups();
        }

        // 이 세션의 프로젝트가 이미 어느 패널에 떠 있으면 그 패널에서 세션만 활성화(재로딩 없음).
        // 같은 프로젝트를 분할한 경우엔 세션이 실제로 보이는 패널(예: 우측 격리)을 우선 고른다 —
        // 안 그러면 항상 좌측을 골라 우측 세션이 안 열린다.
        var existingPane = _panes.FirstOrDefault(p => p.ShowsTab(session))
            ?? (parent != null ? _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveProject, parent)) : null);
        if (existingPane != null) { OpenSessionIntoPane(existingPane, session, isNewProjectLoad: false); return; }

        // 새 프로젝트는 메인(좌측) 패널에 연다.
        OpenSessionIntoPane(LeftPane, session, isNewProjectLoad: true);
    }

    /// <summary>사이드바 카드에서 세션 클릭 — '오른쪽' 그룹이면 우측 격리를 유지한 채 연다(활성화가 격리를
    /// 풀어 우측에 전체 세션이 쏟아지는 것 방지). 숨김 세션 그룹(사이드바 맨 아래)에서 다시 불러오는
    /// 경우엔 이전에 어느 패널에 있었든 상관없이, 분할 중이면 항상 좌측 패널의 가장 오른쪽(마지막) 탭으로
    /// 들어온다. 그 외엔 기존 OpenSession 라우팅.</summary>
    private void OpenSessionFromSidebar(SessionItem s)
    {
        var parent = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(s));
        bool reopeningHidden = s.IsEffectivelyHidden;
        if (reopeningHidden && parent != null)
        {
            var newlyVisible = parent.UnhideSessionPath(s);
            foreach (var pane in _panes)
                foreach (var visible in newlyVisible)
                    pane.CancelSessionHide(visible.Id);
            WorkspaceStore.Save(_projects, _archivedProjects);
            RefreshCardGroups();
        }
        EnsureProjectSplitOpen(parent);

        if (reopeningHidden && _splitActive)
        {
            // 숨기기 전 우측 패널 소속이었어도 복원 세션은 항상 좌측으로 이동한다.
            // 트리 전체 숨김에서 복원한 경우만 UnhideSessionPath 가 탭 순서를 끝으로 옮긴다
            // (부모가 보이는 트리의 숨김 자식은 제자리 해제 — 부모 아래 원래 위치에 표시).
            if (ReferenceEquals(RightPane.ActiveProject, parent)) RightPane.HideTabInPane(s);
            if (ReferenceEquals(LeftPane.ActiveProject, parent)) LeftPane.UnhideTabInPane(s);
            OpenSessionIntoPane(LeftPane, s, isNewProjectLoad: false);
            RefreshCardGroups();
            PersistSplitState();
            return;
        }

        EnsureRightGroupIsolation(s);
        OpenSession(s);
    }

    /// <summary>분할 설정 프로젝트의 카드 항목(세션/문서)을 클릭했는데 지금 그 프로젝트가 분할 표시 중이 아니면,
    /// 먼저 프로젝트를 분할로 연다(그냥 열면 단일 패널로 떠 분할이 안 됨). 이미 분할 표시 중이면 그대로.</summary>
    private void EnsureProjectSplitOpen(ProjectItem? parent)
    {
        if (parent == null || !parent.SplitEnabled) return;
        bool splitAsParent = _splitActive
            && ReferenceEquals(LeftPane.ActiveProject, parent) && ReferenceEquals(RightPane.ActiveProject, parent);
        if (!splitAsParent) SelectProjectFromSidebar(parent); // 프로젝트 선택 = ApplyProjectSplitForMainPane 로 분할 적용
    }

    /// <summary>사이드바 카드의 '오른쪽' 그룹 항목(세션/문서) 클릭 시, 활성화 전에 우측 패널 격리 화이트리스트에
    /// 넣고 좌측에선 숨긴다 → 이후 ActivateSession/ActivateFileTab 의 ClearIsolationIfMismatch 가 격리를 풀지
    /// 않아(우측 전체세션 노출·분할 붕괴 방지) 우측 격리가 그대로 유지된다. 우측이 그 프로젝트를 격리 표시
    /// 중일 때만 동작.</summary>
    private void EnsureRightGroupIsolation(TabItemBase item)
    {
        if (!_splitActive) return;
        var parent = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(item));
        if (parent == null || !parent.RightItems.Contains(item)) return;
        if (!ReferenceEquals(RightPane.ActiveProject, parent)) return;
        if (RightPane.CurrentIsolatedTabs() == null) return; // 우측이 격리 상태일 때만
        RightPane.IsolateTab(item);
        LeftPane.HideTabInPane(item);
        PersistSplitState();
    }

    /// <summary>사이드바 카드의 열린 문서(파일 탭) 클릭 → 그 문서가 실제로 보이는 패널(분할 좌/우)에서 활성화.
    /// 어느 패널에도 없으면 그 프로젝트를 띄운 패널, 그것도 없으면 포커스 패널에 연다.</summary>
    private void OpenDocFromSidebar(FileTabItem doc)
    {
        var parent = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(doc));
        if (parent != null && !_panes.Any(p => ReferenceEquals(p.ActiveProject, parent)))
            // 현재 어느 패널에도 안 뜬 '다른 프로젝트'의 문서 → 그 프로젝트를 메인으로 선택(분할/단일은 그 프로젝트
            // 설정대로 전환). 안 그러면 포커스 패널에 그 프로젝트가 통째로 로드돼 현재 분할이 깨진다.
            SelectProjectFromSidebar(parent);
        else
            EnsureProjectSplitOpen(parent); // 이미 뜬(분할설정) 프로젝트면 분할 보장
        EnsureRightGroupIsolation(doc); // 우측 그룹 문서면 우측 격리 유지(분할 붕괴 방지)
        var pane = _panes.FirstOrDefault(p => p.ShowsTab(doc))
            ?? (parent != null ? _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveProject, parent)) : null)
            ?? _focusedPane;
        _focusedPane = pane;
        pane.OpenFileTab(doc);
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
    }

    /// <summary>사이드바 카드 문서 우클릭 "문서 닫기" → 그 문서가 보이는 패널에서 닫는다(활성 탭이면 이웃으로 교체).</summary>
    private void CloseDocFromSidebar(FileTabItem doc)
    {
        var pane = _panes.FirstOrDefault(p => p.ShowsTab(doc)) ?? _focusedPane;
        pane.CloseFileTab(doc);
        RefreshCardGroups();
    }

    private void OnBrowserTabCloseRequested(BrowserTabItem tab)
    {
        var pane = _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveTab, tab))
                   ?? _panes.FirstOrDefault(p => p.ShowsTab(tab))
                   ?? _focusedPane;
        pane.CloseBrowserTab(tab);
        RefreshCardGroups();
    }

    private void OpenBrowserFromSidebar(BrowserTabItem browser)
    {
        var parent = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(browser));
        if (parent != null && !_panes.Any(p => ReferenceEquals(p.ActiveProject, parent)))
            SelectProjectFromSidebar(parent);
        else
            EnsureProjectSplitOpen(parent);
        EnsureRightGroupIsolation(browser);
        var pane = _panes.FirstOrDefault(p => p.ShowsTab(browser))
            ?? (parent != null ? _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveProject, parent)) : null)
            ?? _focusedPane;
        _focusedPane = pane;
        pane.OpenBrowserTab(browser);
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
    }

    private void CloseBrowserFromSidebar(BrowserTabItem browser)
    {
        OnBrowserTabCloseRequested(browser);
    }

    private void RenameBrowserFromSidebar(BrowserTabItem browser)
        => _focusedPane.RenameBrowserTab(browser);

    private void OpenSessionIntoPane(WorkspacePaneView pane, SessionItem session, bool isNewProjectLoad)
    {
        _focusedPane = pane;
        // 새 프로젝트를 좌측에 여는 경우만 커튼으로 덮는다. 이미 열린 프로젝트의 세션 탭 전환은 즉시(페이드 X).
        bool cover = isNewProjectLoad && ReferenceEquals(pane, LeftPane);
        var parent = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(session));
        bool coverRight = cover && (_splitActive || (parent != null && parent.SplitEnabled));
        // 파일→세션 탭 전환의 리플로우 커버는 ActivateSession 이 자체 처리(사이드바·탭클릭 두 경로 모두 커버).
        if (cover) pane.CoverForTransition();
        if (coverRight) RightPane.CoverForTransition();
        pane.OpenSession(session);
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
        if (ReferenceEquals(pane, LeftPane) && pane.ActiveProject != null)
            ApplyProjectSplitForMainPane(pane.ActiveProject);
        if (cover)
        {
            if (_splitActive) RevealPanesSynced();
            else pane.RevealAfterTransition();
        }
        PaneA.RefreshSplitIndicator();
        PaneB.RefreshSplitIndicator();
    }

    /// <summary>세션 ID 로 완료 기록 중 미확인 항목을 읽음 처리하고 저장.</summary>
    private void MarkSessionRead(string sessionId)
    {
        bool changed = false;
        foreach (var r in _sessionDoneRecords)
        {
            if (r.SessionId == sessionId && !r.IsRead)
            {
                r.IsRead = true;
                changed = true;
            }
        }
        if (changed)
            SettingsService.SaveSessionHistoryRecords(
                new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
    }

    private void SelectProjectIntoPane(WorkspacePaneView pane, ProjectItem proj)
    {
        _focusedPane = pane;
        // 프로젝트 전환은 항상 커튼으로 덮었다 fade-in — 리사이즈 리플로우를 감추고 전환 페이드를 균일하게.
        // 분할 프로젝트면 우측도 미리 덮어, 좌우가 각자 다른 타이밍에 뜨지 않고 느린 쪽 기준으로 함께 나타난다.
        bool leftMain = ReferenceEquals(pane, LeftPane);
        // 우측도 커버: 지금 분할이라 우측 내용이 사라질 때(→ 좌측과 동시에 사라지게) 또는 대상이 분할이라 우측이 새로 뜰 때.
        bool coverRight = leftMain && (_splitActive || proj.SplitEnabled);
        if (leftMain) pane.CoverForTransition();
        if (coverRight) RightPane.CoverForTransition();
        pane.SelectProject(proj);
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
        if (leftMain) ApplyProjectSplitForMainPane(proj);
        if (leftMain)
        {
            if (_splitActive) RevealPanesSynced();       // 좌우 동시(느린 쪽 기준)
            else pane.RevealAfterTransition();           // 단일 패널
        }
        PaneA.RefreshSplitIndicator();
        PaneB.RefreshSplitIndicator();
    }

    // ── synced reveal 조율: 좌우 패널이 각자 다른 시점에 뜨지 않게, 둘 다 준비되면 동시에 커튼을 걷는다 ──
    private readonly HashSet<WorkspacePaneView> _revealPending = new();
    private System.Windows.Threading.DispatcherTimer? _revealTimeout;

    /// <summary>좌우 패널을 동시에 reveal — 각 패널이 폭 안정·fit·재동기까지 준비하고(커튼 유지), 둘 다 준비되면
    /// FadeAllRevealNow 로 함께 걷는다. 한쪽이 보고 안 해도 타임아웃(2.5s)에 강제로 함께 걷는다.</summary>
    private void RevealPanesSynced()
    {
        _revealPending.Clear();
        _revealPending.Add(LeftPane);
        _revealPending.Add(RightPane);
        LeftPane.PrepareRevealSynced(kick: true);
        RightPane.PrepareRevealSynced(kick: true);
        if (_revealPending.Count == 0) return; // 둘 다 즉시 준비 완료(파일/빈 패널) — 이미 함께 걷음
        _revealTimeout ??= new System.Windows.Threading.DispatcherTimer();
        _revealTimeout.Stop();
        _revealTimeout.Interval = TimeSpan.FromMilliseconds(2500);
        _revealTimeout.Tick -= RevealTimeout_Tick;
        _revealTimeout.Tick += RevealTimeout_Tick;
        _revealTimeout.Start();
    }

    private void RevealTimeout_Tick(object? s, EventArgs e) { _revealTimeout?.Stop(); FadeAllRevealNow(); }

    private void OnPaneRevealPrepared(WorkspacePaneView pane)
    {
        if (!_revealPending.Remove(pane)) return;
        if (_revealPending.Count == 0) { _revealTimeout?.Stop(); FadeAllRevealNow(); }
    }

    private void FadeAllRevealNow()
    {
        _revealPending.Clear();
        LeftPane.FadeRevealNow();
        RightPane.FadeRevealNow();
    }

    /// <summary>메인(좌측) 패널의 프로젝트가 바뀌었을 때 그 프로젝트의 분할 설정을 반영한다.
    /// SplitEnabled 면 분할로(이미 분할 중이면 우측을 저장된 파트너로 교체), 아니면 분할을 닫아 단일로.</summary>
    private void ApplyProjectSplitForMainPane(ProjectItem proj)
    {
        DevezCode.Services.DiagLog.Write($"ApplyProjectSplitForMainPane proj={proj.Name} SplitEnabled={proj.SplitEnabled} _splitActive={_splitActive}");
        if (proj.SplitEnabled)
        {
            if (_splitActive) ApplyPartnerToPaneB(proj);
            else EnableSplitForProject(proj, animate: false); // 전환 — 펼침 슬라이드 없이 즉시 분할로.
        }
        else if (_splitActive)
        {
            DisableSplit(animate: false); // 다른(비분할) 프로젝트로 전환 — 슬라이드 없이 즉시 단일 패널로 축소.
        }
        // 격리 복원(RestoreSameProjectSplit)이 끝난 뒤 카드 그룹을 다시 계산 — 진입 흐름에서 SyncShell→
        // RefreshCardGroups 가 격리 '전에' 먼저 불려 카드가 단일로 보이던 문제 방지(패널은 분할됐는데 카드만 단일).
        RefreshCardGroups();
    }

    private void FocusPaneOnly(WorkspacePaneView pane)
    {
        _focusedPane = pane;
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
    }

    private void AddSession(ProjectItem proj)
    {
        var session = _focusedPane.AddSession(proj);
        if (session != null) OpenSession(session);
    }

    private void RenameSession(SessionItem session) { PaneFor(session).RenameSession(session); SyncRecordsForSessionRename(session); }
    private void DeleteSession(SessionItem session)
    {
        var project = ProjectFor(session);
        if (project == null) return;
        if (session.IsExternal)
        {
            ConfirmDialog.Alert("세션 삭제 불가",
                "외부 터미널에서 실행 중인 세션입니다.\n외부 탭을 닫은 후 다시 시도하세요.",
                iconKey: "IconExternalLink");
            return;
        }
        if (!EnsureSessionSubtreeUnlocked(new[] { session }, "세션 삭제")) return;
        var children = project.GetSessionSubtree(session).Skip(1).ToList();
        string childNotice = children.Count > 0
            ? $"\n하위 세션 {children.Count}개는 삭제하지 않고 최상위로 이동합니다."
            : "";
        if (!ConfirmDialog.Show("세션 삭제",
                $"'{session.Name}' 세션을 영구 삭제할까요?{childNotice}\n이 세션의 대화 기록(.jsonl)도 디스크에서 함께 삭제되며 복구할 수 없습니다.",
                okLabel: "삭제", danger: true))
            return;
        foreach (var child in children) child.ParentSessionId = null;
        RemoveSessionSubtree(project, new[] { session }, purge: true);
    }

    private void DeleteSessions(IReadOnlyList<SessionItem> sessions)
    {
        var targets = sessions.Distinct().Where(session => ProjectFor(session) != null).ToList();
        if (targets.Count == 0) return;
        if (targets.Any(session => session.IsExternal))
        {
            ConfirmDialog.Alert("세션 삭제 불가",
                "외부 터미널에서 실행 중인 세션이 포함되어 있습니다.",
                iconKey: "IconExternalLink");
            return;
        }
        if (!EnsureSessionSubtreeUnlocked(targets, "세션 삭제")) return;

        var selected = targets.ToHashSet();
        var detached = targets
            .SelectMany(session => ProjectFor(session)!.GetSessionSubtree(session).Skip(1))
            .Where(session => !selected.Contains(session))
            .Distinct()
            .ToList();
        string childNotice = detached.Count > 0
            ? $"\n선택하지 않은 하위 세션 {detached.Count}개는 삭제하지 않고 최상위로 이동합니다."
            : "";
        if (!ConfirmDialog.Show("세션 일괄 삭제",
                $"선택한 세션 {targets.Count}개를 영구 삭제할까요?{childNotice}\n대화 기록(.jsonl)도 디스크에서 함께 삭제되며 복구할 수 없습니다.",
                okLabel: "모두 삭제", danger: true))
            return;

        foreach (var child in detached) child.ParentSessionId = null;
        foreach (var group in targets.GroupBy(ProjectFor))
            if (group.Key is { } project)
                RemoveSessionSubtree(project, group.OrderBy(project.Tabs.IndexOf).ToList(), purge: true);
        Sidebar.ClearSessionMultiSelection();
    }

    /// <summary>세션 이름 변경 → 동일 SessionId 의 완료 기록 카드 이름도 동기화하고 저장.</summary>
    private void SyncRecordsForSessionRename(SessionItem session)
    {
        bool changed = false;
        foreach (var r in _sessionDoneRecords)
            if (r.SessionId == session.Id && r.SessionName != session.Name) { r.SessionName = session.Name; changed = true; }
        if (changed)
            SettingsService.SaveSessionHistoryRecords(
                new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
    }

    private void StopTrackingSession(SessionItem session)
    {
        var project = ProjectFor(session);
        if (project == null) return;
        if (session.IsExternal)
        {
            ConfirmDialog.Alert("세션 닫기 불가",
                "외부 터미널에서 실행 중인 세션입니다.\n외부 탭을 닫은 후 다시 시도하세요.",
                iconKey: "IconExternalLink");
            return;
        }
        if (!EnsureSessionSubtreeUnlocked(new[] { session }, "세션 추적 중단")) return;
        var children = project.GetSessionSubtree(session).Skip(1).ToList();
        string childNotice = children.Count > 0
            ? $"\n하위 세션 {children.Count}개는 목록에 남기고 최상위로 이동합니다."
            : "";
        if (!ConfirmDialog.Show("세션 추적 중단",
                $"'{session.Name}' 세션을 목록에서 제거할까요?{childNotice}\n이 세션의 대화 기록은 디스크에 그대로 보존됩니다.",
                okLabel: "닫기"))
            return;
        foreach (var child in children) child.ParentSessionId = null;
        RemoveSessionSubtree(project, new[] { session }, purge: false);
    }

    private void StopTrackingSessions(IReadOnlyList<SessionItem> sessions)
    {
        var targets = sessions.Distinct().Where(session => ProjectFor(session) != null).ToList();
        if (targets.Count == 0) return;
        if (targets.Any(session => session.IsExternal))
        {
            ConfirmDialog.Alert("세션 닫기 불가",
                "외부 터미널에서 실행 중인 세션이 포함되어 있습니다.",
                iconKey: "IconExternalLink");
            return;
        }
        if (!EnsureSessionSubtreeUnlocked(targets, "세션 추적 중단")) return;

        var selected = targets.ToHashSet();
        var detached = targets
            .SelectMany(session => ProjectFor(session)!.GetSessionSubtree(session).Skip(1))
            .Where(session => !selected.Contains(session))
            .Distinct()
            .ToList();
        string childNotice = detached.Count > 0
            ? $"\n선택하지 않은 하위 세션 {detached.Count}개는 목록에 남기고 최상위로 이동합니다."
            : "";
        if (!ConfirmDialog.Show("세션 일괄 추적 중단",
                $"선택한 세션 {targets.Count}개를 목록에서 제거할까요?{childNotice}\n대화 기록은 디스크에 그대로 보존됩니다.",
                okLabel: "모두 닫기"))
            return;

        foreach (var child in detached) child.ParentSessionId = null;
        foreach (var group in targets.GroupBy(ProjectFor))
            if (group.Key is { } project)
                RemoveSessionSubtree(project, group.OrderBy(project.Tabs.IndexOf).ToList(), purge: false);
        Sidebar.ClearSessionMultiSelection();
    }

    private void HideSessionFromSidebar(SessionItem session)
    {
        if (session.Hidden) return;
        var project = ProjectFor(session);
        if (project == null) return;
        var owner = PaneFor(session);

        session.Hidden = true;
        project.PlaceNewlyHiddenSession(session, SettingsService.LoadHiddenSessionInsertionOnTop());
        foreach (var pane in _panes) pane.OnSessionsHidden(new[] { session });
        owner.ScheduleSessionHide(session);

        ReturnHiddenChildToParentPane(project, session);

        // 숨김 graceful 종료는 owner 패널의 방(xterm)만 닫는다 — 다른 패널 WebView 에 남은 방은
        // 종료 후 "[세션 종료됨 — Enter로 재시작]" 죽은 방이 되고, 재오픈(항상 좌측 패널)이 그 방에
        // 붙으면 자동 resume 이 안 된다. 숨김 시점에 owner 외 패널의 방을 미리 정리해 재오픈이
        // 항상 "방 없음 → 새로 생성(reattach/resume)" 경로를 타게 한다. 프로세스가 이미 죽어
        // graceful 종료가 no-op 이면 owner 방도 죽은 방이므로 함께 정리한다.
        bool aliveNow = TerminalSessionManager.Instance.Get(session.Id) is { IsAlive: true };
        foreach (var pane in _panes)
            if (!aliveNow || !ReferenceEquals(pane, owner))
                pane.CloseTerminalRoom(session.Id);

        RefreshCardGroups();
        WorkspaceStore.Save(_projects, _archivedProjects);
    }

    private void HideSessionsFromSidebar(IReadOnlyList<SessionItem> sessions)
    {
        foreach (var session in sessions.Distinct().Where(session => !session.Hidden).ToList())
            HideSessionFromSidebar(session);
        Sidebar.ClearSessionMultiSelection();
    }

    /// <summary>프로젝트 카드 "세션 관리자" — 세션 일괄 관리 팝업. 위험 동작은 기존 검증
    /// 메서드(확인창·하위세션·잠금 검사 포함)에 그대로 위임한다.</summary>
    private void OpenSessionManager(ProjectItem project)
    {
        var dialog = new SessionManagerDialog(
            project,
            onHide: HideSessionsFromSidebar,
            onClose: StopTrackingSessions,
            onDelete: DeleteSessions)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    /// <summary>어느 패널에서든 숨김 graceful 종료가 끝나면 모든 패널로 중계 — 각 패널이 종료 중 생긴
    /// 죽은/빈 방을 정리하고, 종료 중 그 세션을 열어 대기하던 패널은 새 세션으로 resume 재연결한다.</summary>
    private void OnPaneHideStopFinished(SessionItem session)
    {
        foreach (var pane in _panes) pane.OnHideStopFinished(session);
    }

    /// <summary>숨긴 자식 세션이 분할로 부모와 반대 패널에 있으면 즉시 부모 패널 소속으로 되돌린다 —
    /// 사이드바 카드에서 프로젝트 전환을 기다리지 않고 바로 원래 부모 아래(숨김 상태)로 보이게.</summary>
    private void ReturnHiddenChildToParentPane(ProjectItem project, SessionItem session)
    {
        if (project.SessionParentOf(session) is not { } parentSession) return;

        // 라이브 분할(같은 프로젝트 좌/우 표시) 중이면 패널 필터 소속을 직접 이동.
        if (_splitActive && ReferenceEquals(LeftPane.ActiveProject, project)
            && ReferenceEquals(RightPane.ActiveProject, project))
        {
            var sessionPane = _panes.FirstOrDefault(p => p.ShowsTab(session));
            var parentPane = _panes.FirstOrDefault(p => p.ShowsTab(parentSession));
            if (sessionPane == null || parentPane == null || ReferenceEquals(sessionPane, parentPane))
                return;
            sessionPane.HideTabInPane(session);
            parentPane.UnhideTabInPane(session);
            PersistSplitState(); // SplitRightTabRefs 갱신 — 복원 시 반대 패널로 되돌아가지 않게
            return;
        }

        // 비활성(배경) 분할 프로젝트는 영속 refs(우측 집합)만 부모 쪽으로 맞춘다.
        if (project.SplitRightTabRefs.Count == 0) return;
        string childRef = WorkspacePaneView.RefOf(session);
        string parentRef = WorkspacePaneView.RefOf(parentSession);
        bool childRight = project.SplitRightTabRefs.Contains(childRef, StringComparer.OrdinalIgnoreCase);
        bool parentRight = project.SplitRightTabRefs.Contains(parentRef, StringComparer.OrdinalIgnoreCase);
        if (childRight == parentRight) return;
        if (childRight)
            project.SplitRightTabRefs.RemoveAll(r => StringComparer.OrdinalIgnoreCase.Equals(r, childRef));
        else
            project.SplitRightTabRefs.Add(childRef);
    }

    private ProjectItem? ProjectFor(SessionItem session)
        => _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(session));

    private bool EnsureSessionSubtreeUnlocked(IReadOnlyCollection<SessionItem> subtree, string action)
    {
        var locked = subtree.FirstOrDefault(s => s.IsLocked);
        if (locked == null) return true;
        bool unlock = ConfirmDialog.AlertWithLink(
            $"{action} 불가",
            $"'{locked.Name}' 세션이 잠겨 있습니다.\n잠금을 해제한 후 다시 시도하세요.",
            linkLabel: "잠금 해제 후 계속");
        if (!unlock) return false;
        foreach (var item in subtree.Where(s => s.IsLocked)) item.IsLocked = false;
        WorkspaceStore.Save(_projects, _archivedProjects);
        return true;
    }

    private void RemoveSessionSubtree(ProjectItem project, IReadOnlyList<SessionItem> subtree, bool purge)
    {
        if (subtree.Count == 0) return;
        var owners = subtree.ToDictionary(s => s, PaneFor);
        int removedIndex = subtree.Select(project.Tabs.IndexOf).Where(i => i >= 0).DefaultIfEmpty(0).Min();

        // 응답 대기(❗) 중인 세션을 닫기/삭제하면 완료기록으로 내리고, 대기 플래그를 꺼 대기 카드도 즉시 제거한다.
        foreach (var item in subtree)
            if (item.IsWaitingChoice) { AddSessionCompletionRecord(item); item.IsWaitingChoice = false; }

        foreach (var pane in _panes)
            foreach (var item in subtree)
                pane.CancelSessionHide(item.Id);
        foreach (var item in subtree)
            owners[item].DisposeSessionProcess(item, purge);

        foreach (var item in subtree.OrderByDescending(project.Tabs.IndexOf))
            project.Tabs.Remove(item);
        project.NormalizeSessionTree();

        foreach (var pane in _panes) pane.OnSessionsRemoved(project, subtree, removedIndex);
        RefreshCardGroups();
        PaneA.RefreshSelectedTabSeam();
        PaneB.RefreshSelectedTabSeam();
        WorkspaceStore.Save(_projects, _archivedProjects);
    }

    private void ForkSession(SessionItem session) => PaneFor(session).ForkSession(session);

    private void ToggleSessionLock(SessionItem session)
    {
        session.IsLocked = !session.IsLocked;
        WorkspaceStore.Save(_projects, _archivedProjects);
    }

    private void SetSessionsLocked(IReadOnlyList<SessionItem> sessions, bool locked)
    {
        bool changed = false;
        foreach (var session in sessions.Distinct())
        {
            if (session.IsLocked == locked) continue;
            session.IsLocked = locked;
            changed = true;
        }
        if (changed)
            WorkspaceStore.Save(_projects, _archivedProjects);
    }

    /// <summary>세션 대화를 마크다운(.md)으로 내보낸다(옵션 A: user/assistant 텍스트만). 파싱은 백그라운드에서
    /// (opencode 는 CLI export 스폰), 그 뒤 저장 위치를 물어 UTF-8(BOM)로 저장.</summary>
    private async void ExportSession(SessionItem session)
    {
        var roomId = session.Id;
        var name = session.Name;
        var agentId = string.IsNullOrEmpty(session.AgentId)
            ? SettingsService.LoadAgentForRoom(roomId) : session.AgentId;
        var cwd = SettingsService.LoadClaudeCodeRoomDir(roomId);

        string? md = await System.Threading.Tasks.Task.Run(() =>
        {
            try { return SessionExporter.BuildMarkdown(roomId, agentId ?? "", name, cwd); }
            catch { return null; }
        });

        if (string.IsNullOrWhiteSpace(md))
        {
            ConfirmDialog.Alert("내보내기 실패",
                "이 세션에서 내보낼 대화를 찾지 못했습니다.\n(대화가 없거나 지원되지 않는 에이전트일 수 있어요.)");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "세션 대화 내보내기",
            FileName = SafeExportFileName(name),
            Filter = "Markdown (*.md)|*.md|텍스트 (*.txt)|*.txt",
            DefaultExt = ".md",
        };
        if (dlg.ShowDialog(this) == true)
        {
            try { System.IO.File.WriteAllText(dlg.FileName, md, new System.Text.UTF8Encoding(true)); }
            catch (Exception ex) { ConfirmDialog.Alert("저장 실패", ex.Message); }
        }
    }

    private static string SafeExportFileName(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "session" : name;
        foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, '_');
        return baseName + ".md";
    }

    // ── 비활성 세션 자동 종료 ─────────────────────────────────────

    private void MarkSessionActivity(string roomId)
    {
        if (!string.IsNullOrWhiteSpace(roomId))
            _sessionLastActivityUtc[roomId] = DateTime.UtcNow;
    }

    /// <summary>설정 저장 직후/앱 시작 시 적용. 켜거나 시간을 바꾸면 기존 세션의 시계를 지금부터 다시 재어
    /// 저장 직후 예상치 못한 즉시 종료가 발생하지 않게 한다.</summary>
    public void ApplyIdleSessionShutdownSettings()
    {
        _idleSessionShutdownTimer.Stop();
        _idleSessionShutdownMinutes = SettingsService.LoadIdleSessionShutdownMinutes();
        _sessionLastActivityUtc.Clear();
        if (_idleSessionShutdownMinutes <= 0)
        {
            DiagLog.Write("IdleSessionShutdown disabled");
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var session in AllWorkspaceSessions().Where(s => s.IsAlive))
            _sessionLastActivityUtc[session.Id] = now;
        _idleSessionShutdownTimer.Start();
        DiagLog.Write($"IdleSessionShutdown enabled minutes={_idleSessionShutdownMinutes}");
    }

    private IEnumerable<SessionItem> AllWorkspaceSessions()
        => _projects.Concat(_archivedProjects).SelectMany(p => p.Tabs).OfType<SessionItem>();

    private bool IsSessionDisplayed(SessionItem session)
        => _panes.Any(p => p.IsVisible && ReferenceEquals(p.ActiveSession, session));

    private bool CanStopIdleSession(SessionItem session, DateTime nowUtc)
    {
        if (_shuttingDown || _themeReloadRunning || _idleSessionShutdownMinutes <= 0
            || session.IsExternal || !session.IsAlive || session.IsEffectivelyHidden || session.IsLocked
            || session.IsBusy || session.IsWaitingChoice || IsSessionDisplayed(session)
            || _sessionCommandInbox.HasPendingInjection(session.Id))
            return false;

        if (TerminalSessionManager.Instance.Get(session.Id) is not { IsAlive: true }
            || TerminalSessionManager.Instance.IsGracefulStopping(session.Id))
            return false;

        var agentId = string.IsNullOrWhiteSpace(session.AgentId)
            ? SettingsService.LoadAgentForRoom(session.Id) : session.AgentId;
        if (string.Equals(agentId, "claude", StringComparison.OrdinalIgnoreCase)
            && _sessionBusy.IsRoomActive(session.Id))
            return false;
        if (HasBusyOrWaitingTrackingFile(agentId, session.Id))
            return false;

        if (!_sessionLastActivityUtc.TryGetValue(session.Id, out var lastActivity))
        {
            _sessionLastActivityUtc[session.Id] = nowUtc;
            return false;
        }
        if (nowUtc - lastActivity < TimeSpan.FromMinutes(_idleSessionShutdownMinutes))
            return false;

        return TerminalSessionManager.CanSafelyResumeRoom(session.Id);
    }

    /// <summary>claude waiting 파일이 선택지/권한 대기 값인지. busy=false 보강 해제 시 ❗ 유지 판단용.</summary>
    private static bool IsClaudeWaitingFileActive(string roomId)
    {
        try
        {
            var safe = new string(roomId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            if (safe.Length == 0) return false;
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "claude", "waiting", safe + ".txt");
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var v = reader.ReadToEnd().Trim();
            return v.Equals("waiting", StringComparison.OrdinalIgnoreCase)
                || v.Equals("permission", StringComparison.OrdinalIgnoreCase)
                || v.Equals("input", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>FileSystemWatcher 이벤트가 UI 큐에 아직 도착하지 않은 짧은 경합도 막는 최종 디스크 확인.
    /// stale running은 종료를 보류하는 안전 방향으로만 작용하며 각 훅 서비스가 시작 시 정리한다.</summary>
    private static bool HasBusyOrWaitingTrackingFile(string? agentId, string roomId)
    {
        var agent = (agentId ?? "claude").Trim().ToLowerInvariant();
        // 폴더명 = agentId 규약. devezvibe 도 %APPDATA%\DevezCode\devezvibe\{busy,waiting} 로 같은 모양이다.
        if (agent is not ("codex" or "opencode" or "grok" or "antigravity" or "kimi" or "devezvibe")) return false;
        var safe = new string(roomId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe.Length == 0) return true;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DevezCode", agent);
        var busy = ReadState("busy");
        return (busy?.StartsWith("running", StringComparison.Ordinal) ?? false)
            || ReadState("waiting") is "waiting" or "permission" or "input";

        string? ReadState(string folder)
        {
            try
            {
                var path = Path.Combine(root, folder, safe + ".txt");
                if (!File.Exists(path)) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Trim().ToLowerInvariant();
            }
            catch { return "running"; } // 판독 실패도 이번 종료는 보류한다.
        }
    }

    private async Task CheckIdleSessionsAsync()
    {
        if (_idleSessionShutdownChecking || _shuttingDown || _idleSessionShutdownMinutes <= 0) return;
        _idleSessionShutdownChecking = true;
        try
        {
            var existing = AllWorkspaceSessions().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _sessionLastActivityUtc.Keys.Where(id => !existing.Contains(id)).ToList())
                _sessionLastActivityUtc.Remove(stale);

            var now = DateTime.UtcNow;
            var candidate = AllWorkspaceSessions()
                .Where(s => CanStopIdleSession(s, now))
                .OrderBy(s => _sessionLastActivityUtc[s.Id])
                .FirstOrDefault();
            if (candidate == null) return;

            // 타이머 만료와 사용자 클릭/예약 주입이 겹치는 경합을 흡수한다. 한 번에 한 방만 처리한다.
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (!CanStopIdleSession(candidate, DateTime.UtcNow)) return;
            await StopIdleSessionAsync(candidate);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"IdleSessionShutdown check failed: {ex.Message}");
        }
        finally
        {
            _idleSessionShutdownChecking = false;
        }
    }

    private async Task StopIdleSessionAsync(SessionItem session)
    {
        var roomId = session.Id;
        var idleMinutes = _sessionLastActivityUtc.TryGetValue(roomId, out var last)
            ? (DateTime.UtcNow - last).TotalMinutes : 0;
        DiagLog.Write($"IdleSessionShutdown start room={roomId} agent={session.AgentId} idleMin={idleMinutes:F1}");

        // 양쪽 xterm 배선을 먼저 끊어 Exited 자동 재진입을 막는다. GracefulDispose가 종료중 플래그를
        // 세운 뒤 await하므로 이후 사용자가 열면 WorkspacePane이 완료 후 resume 대기 경로를 탄다.
        foreach (var pane in _panes) pane.CloseTerminalRoom(roomId);
        session.IsAlive = false;
        session.IsBusy = false;
        session.IsWaitingChoice = false;
        try
        {
            await TerminalSessionManager.Instance.GracefulDisposeRoomsAsync(new[] { roomId });
        }
        catch (Exception ex)
        {
            DiagLog.Write($"IdleSessionShutdown dispose failed room={roomId}: {ex.Message}");
        }
        finally
        {
            try { TerminalSessionManager.Instance.ClearDisposedRoom(roomId); } catch { }
            _sessionLastActivityUtc.Remove(roomId);
            if (ReferenceEquals(FindSession(roomId), session))
            {
                session.IsAlive = false;
                session.IsBusy = false;
                session.IsWaitingChoice = false;
                OnPaneHideStopFinished(session);
            }
            UpdateSessionBusyDisplay();
        }
        DiagLog.Write($"IdleSessionShutdown complete room={roomId}");
    }

    // ── 공개 API (외부 뷰가 호출) ─────────────────────────────────────
    /// <summary>작업 큐 → 포커스 패널의 활성 세션에 텍스트 전송.</summary>
    public bool SendTextToActiveSession(string text) => _focusedPane.SendTextToActiveSession(text);

    /// <summary>MCP 저장 후 활성 Claude 세션 재시작.</summary>
    public bool TryRestartActiveClaudeSession() => _focusedPane.TryRestartActiveClaudeSession();

    /// <summary>테마 변경 — 모든 패널의 배선을 먼저 끊고 전역 세션을 한 번만 종료한 뒤,
    /// 실제로 보이는 패널의 활성 세션만 다시 연다. 비활성 세션은 클릭 시 저장 ID로 resume 한다.</summary>
    public async void ReloadAllSessionsForTheme()
    {
        if (_themeReloadRunning)
            return;

        _themeReloadRunning = true;
        try
        {
            await ReloadAllSessionsForThemeOnceAsync();
        }
        catch (Exception ex)
        {
            DiagLog.Write($"ReloadAllSessionsForTheme failed: {ex.Message}");
        }
        finally
        {
            _themeReloadRunning = false;
        }
    }

    private async Task ReloadAllSessionsForThemeOnceAsync()
    {
        var allSessions = _projects.SelectMany(p => p.Tabs).OfType<SessionItem>()
            .Where(session => !session.IsExternal).ToList();
        if (allSessions.Count == 0) return;

        // 숨겨진 PaneB도 이전 분할 화면의 터미널 배선을 보존할 수 있다. 두 패널 모두 먼저 detach 해야
        // 종료 이벤트가 codex/opencode 자동 재진입으로 오인되지 않는다. 실제 ConPTY 종료는 아래서 1회만 한다.
        foreach (var pane in _panes)
        {
            try { pane.BeginThemeReload(allSessions); }
            catch (Exception ex) { DiagLog.Write($"BeginThemeReload failed: {ex.Message}"); }
        }

        try
        {
            await TerminalSessionManager.Instance.GracefulDisposeRoomsAsync(allSessions.Select(s => s.Id));
        }
        catch (Exception ex)
        {
            DiagLog.Write($"Theme GracefulDisposeRooms failed: {ex.Message}");
        }
        finally
        {
            // 종료 중 삭제된 방의 tombstone은 되살리지 않는다. 아직 워크스페이스에 있는 방만 재생성 허용.
            var remainingRoomIds = _projects.SelectMany(p => p.Tabs).OfType<SessionItem>()
                .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var s in allSessions)
            {
                if (!remainingRoomIds.Contains(s.Id)) continue;
                try { TerminalSessionManager.Instance.ClearDisposedRoom(s.Id); }
                catch { /* best effort */ }
                s.IsAlive = false;
                s.IsBusy = false;
                s.IsWaitingChoice = false;
            }

            // Dispose/Exited의 지연 콜백이 정리된 다음 새 ConPTY를 만들도록 짧게 양보한다.
            await Task.Delay(150);
            foreach (var pane in _panes)
            {
                try { pane.CompleteThemeReload(allSessions); }
                catch (Exception ex) { DiagLog.Write($"CompleteThemeReload failed: {ex.Message}"); }
            }
        }
    }

    // ── 프로젝트 ──────────────────────────────────────────────────
    private void DeleteProject(ProjectItem proj)
    {
        bool fromArchive = _archivedProjects.Contains(proj);

        var external = proj.Tabs.OfType<SessionItem>().FirstOrDefault(s => s.IsExternal);
        if (external != null)
        {
            ConfirmDialog.Alert("프로젝트 제거 불가",
                $"'{external.Name}' 세션이 외부 터미널에서 실행 중입니다.\n외부 탭을 닫은 후 다시 시도하세요.",
                iconKey: "IconExternalLink");
            return;
        }

        var locked = proj.Tabs.OfType<SessionItem>().FirstOrDefault(s => s.IsLocked);
        if (locked != null)
        {
            bool unlockAndDelete = ConfirmDialog.AlertWithLink(
                "프로젝트 제거 불가",
                $"'{locked.Name}' 세션이 잠겨 있습니다.\n잠금을 해제한 후 다시 시도하세요.",
                linkLabel: "잠금 해제 후 삭제");
            if (!unlockAndDelete) return;

            foreach (var s in proj.Tabs.OfType<SessionItem>().Where(s => s.IsLocked))
                s.IsLocked = false;
            WorkspaceStore.Save(_projects, _archivedProjects);
        }

        if (!ConfirmDialog.Show("프로젝트 제거",
                $"'{proj.Name}' 프로젝트를 목록에서 제거할까요?\n(디스크의 실제 파일은 삭제되지 않습니다.)",
                okLabel: "제거", danger: true, confirmText: proj.Name))
            return;

        foreach (var s in proj.Tabs.OfType<SessionItem>().ToList())
            foreach (var pane in _panes) pane.DisposeSessionProcess(s, purge: false);
        foreach (var browser in proj.Tabs.OfType<BrowserTabItem>().ToList())
        {
            browser.Browser.DisposeAll();
            SettingsService.RemoveBrowserLastUrl(browser.PersistenceKey);
        }

        if (fromArchive) _archivedProjects.Remove(proj);
        else _projects.Remove(proj);
        SettingsService.RemoveBrowserLastUrl(proj.Path);
        WorkspaceStore.Save(_projects, _archivedProjects);

        // 보관 항목 제거는 중앙 패널과 무관(이미 패널에 없음).
        // 삭제 후에는 다른 프로젝트를 자동 선택하지 않고 빈 상태로 둔다.
        if (!fromArchive)
            foreach (var pane in _panes) pane.OnProjectRemoved(proj, null);
        UpdateStatus();
    }

    /// <summary>프로젝트 이름 변경 — 표시 이름만 바꾸고 경로/세션은 그대로. 활성·보관 양쪽 모두 영속 저장.</summary>
    private void RenameProject(ProjectItem proj)
    {
        var name = PromptDialog.Show("프로젝트 이름 변경", "새 이름을 입력하세요.",
                                     defaultValue: proj.Name, maxLength: 60);
        if (string.IsNullOrWhiteSpace(name) || name == proj.Name) return;
        var oldName = proj.Name;
        proj.Name = name;
        WorkspaceStore.Save(_projects, _archivedProjects);

        // 동일 프로젝트명의 완료 기록 카드도 새 이름으로 동기화.
        bool changed = false;
        foreach (var r in _sessionDoneRecords)
            if (r.ProjectName == oldName) { r.ProjectName = name; changed = true; }
        if (changed)
            SettingsService.SaveSessionHistoryRecords(
                new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
    }

    /// <summary>프로젝트 보관 — 활성 목록에서 빼 보관함으로. 세션 프로세스는 정지하되 기록은 보존(devez 정합).</summary>
    private void ArchiveProject(ProjectItem proj)
    {
        if (!_projects.Contains(proj)) return;

        // 열려있는(활성) 프로젝트면 세션 유지·선택 전환 없이 카드만 보관함으로 옮긴다.
        bool isOpen = _panes.Any(p => ReferenceEquals(p.ActiveProject, proj));

        if (!isOpen)
            foreach (var s in proj.Tabs.OfType<SessionItem>().ToList())
                foreach (var pane in _panes) pane.DisposeSessionProcess(s, purge: false);

        proj.ArchivedAt = DateTime.UtcNow.ToString("o");
        _projects.Remove(proj);
        if (!_archivedProjects.Contains(proj)) _archivedProjects.Add(proj);
        WorkspaceStore.Save(_projects, _archivedProjects);

        if (!isOpen)
        {
            var next = _projects.FirstOrDefault();
            foreach (var pane in _panes) pane.OnProjectRemoved(proj, next);
        }
        UpdateStatus();
    }

    /// <summary>프로젝트 꺼내기 — 보관함에서 활성 목록으로 복귀(세션은 죽은 상태로 복원, 클릭 시 재기동).</summary>
    private void UnarchiveProject(ProjectItem proj)
    {
        if (!_archivedProjects.Contains(proj)) return;
        proj.ArchivedAt = null;
        _archivedProjects.Remove(proj);
        if (!_projects.Contains(proj)) _projects.Add(proj);
        WorkspaceStore.Save(_projects, _archivedProjects);
        UpdateStatus();
    }

    /// <summary>사이드바 세션 순서 변경 — 탭은 같은 컬렉션이라 자동 반영되므로 영속만 한다.</summary>
    private void OnSidebarSessionsReordered(ProjectItem _)
    {
        RefreshCardGroups();
        PaneA.RefreshSelectedTabSeam(); PaneB.RefreshSelectedTabSeam(); // 탭 순서 바뀜 → 선택 밑줄 위치 재계산
        WorkspaceStore.Save(_projects);
    }

    // 푸터 좌측 상태 텍스트는 제거됨(한도 표시로 대체). 호출부 유지를 위해 no-op.
    private void UpdateStatus() { }

    /// <summary>테마 변경 시 패널 토글 아이콘 + 하단 푸터 막대색 + 우측 사용량 사이드바 카드를 재갱신한다.
    /// (푸터/사이드바는 캐시된 데이터로 SetBar → RlBrush(c) → FindResource 를 다시 태워 새 테마색을 즉시 반영)</summary>
    private void OnThemeChanged_UpdatePanels(string _) => Dispatcher.BeginInvoke(new Action(() =>
    {
        UpdatePanelToggleVisual();
        ApplyFooterUsageVisibility();
        CodexFooterIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.CodexIconUri));
        GoFooterIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.OpenCodeIconUri)); // 테마별 흑백 아이콘
        GrokFooterIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.GrokIconUri));
        KimiFooterIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(App.KimiIconUri));   // 테마별 흑백 아이콘
        RefreshUsagePanelIfVisible();                                                                      // 사용량 카드 아이콘도 재빌드
        RefreshSessionHistoryIcons();                                                                      // 완료기록/대기 카드 opencode 아이콘도 재빌드
    }));

    /// <summary>완료기록(SessionHistoryList) 카드의 AgentId→아이콘 바인딩을 강제로 재평가한다.
    /// AgentId 는 불변(init) 값이라 값 자체는 안 바뀌므로, 테마 전환 시 컨버터가 재호출되지 않아
    /// codex/opencode 흑/백 아이콘이 즉시 반영되지 않는 문제를 SessionItem.RefreshAgentIcon() 과 동일한
    /// PropertyChanged(AgentId) 트리거 방식으로 해결(스크롤 위치 보존, 컨테이너 재생성 없음).
    /// WaitingList(SessionItem)는 WorkspacePaneView.OnThemeChanged_UpdateSeam 이 같은 인스턴스를
    /// 이미 갱신하므로 여기서는 손대지 않는다.</summary>
    private void RefreshSessionHistoryIcons()
    {
        foreach (var r in _sessionDoneRecords) r.RefreshAgentIcon();
    }

    // ── 설정창 / MCP (오버레이) ───────────────────────────────────────
    private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        await SuspendTerminalWithSnapshotAsync(blankCurtain: true);   // 터미널을 숨기고 단색 커튼(배경색)만 보이게.
        var dlg = new Views.SettingsWindow { Owner = this };
        dlg.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        dlg.Loaded += (_, _) => Views.WindowCenter.CenterOverOwner(dlg);
        dlg.Closed += (_, _) =>
        {
            ResumeTerminal();
            // 설정의 계정 사용량에서 로그인/재연결했을 수 있으니 즉시 갱신.
            _usageApi.RefreshNow();
            _codex.RefreshNow();
            _openCodeGo.RefreshNow();
        };
        dlg.ShowDialog();
    }

    private async void McpBtn_Click(object sender, RoutedEventArgs e)
    {
        await SuspendTerminalWithSnapshotAsync();
        var dlg = new Views.McpManagerWindow { Owner = this };
        dlg.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        dlg.Loaded += (_, _) => Views.WindowCenter.CenterOverOwner(dlg);
        dlg.Closed += (_, _) => ResumeTerminal();
        dlg.ShowDialog();
    }

    private async void McpControlBtn_Click(object sender, RoutedEventArgs e)
    {
        await SuspendTerminalWithSnapshotAsync();
        var dlg = new Views.McpControlWindow { Owner = this };
        dlg.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        dlg.Loaded += (_, _) => Views.WindowCenter.CenterOverOwner(dlg);
        dlg.Closed += (_, _) => ResumeTerminal();
        dlg.ShowDialog();
    }

    private void WakeControlBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new WakeSchedulerWindow(this, EnsureWakeTrustAsync);
        dlg.ShowDialog();
        if (dlg.Saved) _wakeScheduler.NotifySchedulesChanged();
    }

    public Task<bool> EnsureWakeTrustAsync(string agentId)
        => EnsureWakeTrustAsync(agentId, CancellationToken.None);

    private async Task<bool> EnsureWakeTrustAsync(string agentId, CancellationToken cancellationToken)
    {
        var agent = AgentRegistry.Find(agentId);
        if (agent == null || !AgentRegistry.IsInstalled(agent) ||
            (agent.Id != "claude" && agent.Id != "codex"))
            return false;
        if (WakeTrustService.IsTrusted(agent.Id)) return true;

        string directory = WakeTrustService.InstallDirectory;
        if (!Directory.Exists(directory)) return false;

        var pathBytes = System.Text.Encoding.UTF8.GetBytes(directory.ToUpperInvariant());
        var pathKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pathBytes))[..12];
        string roomId = $"devezcode-wake-trust-{agent.Id}-{pathKey.ToLowerInvariant()}";
        _wakeRoomIds.Add(roomId);

        // 이전 자동 확인이 신뢰 화면에서 멈췄다면 준비 상태까지 함께 버리고 새로 시작한다.
        WakeTerminal.CloseTerminal(roomId);
        TerminalSessionManager.Instance.DisposeRoom(roomId, purgeTracking: false);
        TerminalSessionManager.Instance.ClearDisposedRoom(roomId);
        SettingsService.RemoveClaudeCodeRoomDir(roomId);
        SettingsService.SaveClaudeCodeRoomDir(roomId, directory);
        SettingsService.SaveAgentForRoom(roomId, agent.Id);

        bool approvalSent = false;
        Action<string> trustPrompt = id =>
        {
            if (!string.Equals(id, roomId, StringComparison.Ordinal)) return;
            var session = TerminalSessionManager.Instance.Get(roomId);
            if (session is { IsAlive: true } && session.TryWrite("\r")) approvalSent = true;
        };
        WakeTerminal.TrustPromptDetected += trustPrompt;
        try
        {
            WakeTerminal.PreloadTerminal(roomId);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                if (WakeTrustService.IsTrusted(agent.Id)) return true;
                await Task.Delay(200, cancellationToken);
            }
            DiagLog.Write($"Wake trust timeout agent={agent.Id} approvalSent={approvalSent}");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DiagLog.Write($"Wake trust failed agent={agent.Id}: {ex.Message}");
            return false;
        }
        finally
        {
            WakeTerminal.TrustPromptDetected -= trustPrompt;
            WakeTerminal.CloseTerminal(roomId);
            TerminalSessionManager.Instance.DisposeRoom(roomId, purgeTracking: false);
            TerminalSessionManager.Instance.ClearDisposedRoom(roomId);
            SettingsService.RemoveClaudeCodeRoomDir(roomId);
        }
    }

    private async Task<WakeDispatchResult> DispatchWakeAsync(WakeScheduleEntry schedule, CancellationToken cancellationToken)
    {
        const string wakeMessage = "HI Good Morning";
        var provider = string.Equals(schedule.Provider, "codex", StringComparison.OrdinalIgnoreCase)
            ? "codex" : "claude";
        if (!await EnsureWakeTrustAsync(provider, cancellationToken))
            return WakeDispatchResult.Failed("DevezCode 설치 경로의 신뢰 설정을 자동으로 완료하지 못했습니다.");

        string directory = WakeTrustService.InstallDirectory;
        if (!System.IO.Directory.Exists(directory))
            return WakeDispatchResult.Failed("DevezCode 설치 경로를 찾을 수 없습니다.");

        var canonicalDirectory = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(directory));
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(canonicalDirectory.ToUpperInvariant());
        var pathKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pathBytes))[..12];
        string roomId = $"devezcode-wake-{provider}-{pathKey.ToLowerInvariant()}";
        _wakeRoomIds.Add(roomId);

        SettingsService.SaveClaudeCodeRoomDir(roomId, canonicalDirectory);
        SettingsService.SaveAgentForRoom(roomId, provider);

        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = new TaskCompletionSource<WakeDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputBuffer = new System.Text.StringBuilder();
        var outputLock = new object();
        TerminalSession? observedSession = null;
        bool sessionStartedSeen = false;
        Action<byte[]> outputReceived = bytes =>
        {
            lock (outputLock)
            {
                outputBuffer.Append(System.Text.Encoding.UTF8.GetString(bytes));
                if (outputBuffer.Length > 4096) outputBuffer.Remove(0, outputBuffer.Length - 4096);
                if (ContainsWakeAuthenticationError(outputBuffer.ToString()))
                    outcome.TrySetResult(WakeDispatchResult.Failed("로그인 또는 인증이 필요합니다."));
            }
        };
        Action sessionExited = () => outcome.TrySetResult(WakeDispatchResult.Failed("에이전트 프로세스가 종료되었습니다."));
        void ObserveSession(TerminalSession? session)
        {
            if (session == null || ReferenceEquals(observedSession, session)) return;
            if (observedSession != null)
            {
                observedSession.OutputReceived -= outputReceived;
                observedSession.Exited -= sessionExited;
            }
            observedSession = session;
            observedSession.OutputReceived += outputReceived;
            observedSession.Exited += sessionExited;
        }
        Action<string> sessionStarted = id =>
        {
            if (!string.Equals(id, roomId, StringComparison.Ordinal)) return;
            sessionStartedSeen = true;
            ObserveSession(TerminalSessionManager.Instance.Get(roomId));
        };
        Action<string> terminalReady = id => { if (string.Equals(id, roomId, StringComparison.Ordinal)) ready.TrySetResult("ready"); };
        Action<string> trustPrompt = id => { if (string.Equals(id, roomId, StringComparison.Ordinal)) ready.TrySetResult("trust"); };
        Action<string, string> messageChanged = (id, message) =>
        {
            if (string.Equals(id, roomId, StringComparison.Ordinal) &&
                string.Equals(message.Trim(), wakeMessage, StringComparison.OrdinalIgnoreCase))
                outcome.TrySetResult(WakeDispatchResult.Succeeded());
        };
        Action<string, bool> busyChanged = (id, busy) =>
        {
            if (busy && string.Equals(id, roomId, StringComparison.Ordinal))
                outcome.TrySetResult(WakeDispatchResult.Succeeded());
        };
        WakeTerminal.SessionStarted += sessionStarted;
        WakeTerminal.TerminalReady += terminalReady;
        WakeTerminal.TrustPromptDetected += trustPrompt;
        if (provider == "codex")
        {
            _codexHook.MessageChanged += messageChanged;
            _codexHook.BusyChanged += busyChanged;
        }
        else
        {
            _sessionLastMsg.MessageChanged += messageChanged;
            _sessionBusy.BusyChanged += busyChanged;
        }
        try
        {
            WakeTerminal.PreloadTerminal(roomId);
            if (WakeTerminal.IsReady(roomId)) ready.TrySetResult("ready");
            var startup = await Task.WhenAny(ready.Task, outcome.Task)
                .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            if (ReferenceEquals(startup, outcome.Task)) return await outcome.Task;
            var readiness = await ready.Task;
            cancellationToken.ThrowIfCancellationRequested();
            if (readiness == "trust")
            {
                WakeTerminal.CloseTerminal(roomId);
                TerminalSessionManager.Instance.DisposeRoom(roomId, purgeTracking: false);
                TerminalSessionManager.Instance.ClearDisposedRoom(roomId);
                return WakeDispatchResult.Failed("프로젝트 신뢰 확인이 필요합니다. 해당 프로젝트에서 에이전트를 한 번 직접 실행하세요.");
            }
            var session = TerminalSessionManager.Instance.Get(roomId);
            if (session is not { IsAlive: true }) return WakeDispatchResult.Failed("터미널을 시작하지 못했습니다.");
            ObserveSession(session);
            if (!session.TryWrite(wakeMessage + "\r"))
                return WakeDispatchResult.Failed("터미널 입력에 실패했습니다.");
            try { return await outcome.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            catch (TimeoutException) { return WakeDispatchResult.Failed("에이전트가 메시지 접수를 확인하지 않았습니다."); }
        }
        catch (TimeoutException) { return WakeDispatchResult.Failed(sessionStartedSeen ? "터미널 준비 시간이 초과되었습니다." : "터미널 세션을 시작하지 못했습니다."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return WakeDispatchResult.Failed(ex.Message); }
        finally
        {
            WakeTerminal.SessionStarted -= sessionStarted;
            WakeTerminal.TerminalReady -= terminalReady;
            WakeTerminal.TrustPromptDetected -= trustPrompt;
            if (provider == "codex")
            {
                _codexHook.MessageChanged -= messageChanged;
                _codexHook.BusyChanged -= busyChanged;
            }
            else
            {
                _sessionLastMsg.MessageChanged -= messageChanged;
                _sessionBusy.BusyChanged -= busyChanged;
            }
            if (observedSession != null)
            {
                observedSession.OutputReceived -= outputReceived;
                observedSession.Exited -= sessionExited;
            }
        }
    }

    private static bool ContainsWakeAuthenticationError(string output)
    {
        var text = output.ToLowerInvariant();
        return text.Contains("not logged in") || text.Contains("please log in") ||
               text.Contains("login required") || text.Contains("authentication failed") ||
               text.Contains("unauthorized") || text.Contains("invalid api key") ||
               text.Contains("sign in to") || text.Contains("please run /login") ||
               text.Contains("401 unauthorized");
    }

    private void PluginControlBtn_Click(object sender, RoutedEventArgs e)
    {
        // 플러그인 팝업은 테마를 바꾸지 않으므로 터미널을 숨길(suspend) 필요가 없다.
        // 별도 최상위 창이 정적 터미널 위를 그대로 덮는다. 깜빡임은 창이 첫 프레임을 완전히
        // 렌더한 뒤 뜨도록(PluginControlWindow 의 Opacity 0 → ContentRendered 페이드) 처리한다.
        var dlg = new Views.PluginControlWindow { Owner = this };
        dlg.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        dlg.Loaded += (_, _) => Views.WindowCenter.CenterOverOwner(dlg);
        dlg.ShowDialog();
    }

    // ── airspace 우회 (오버레이가 뜰 때 터미널 WebView2 정지) ────────────
    /// <summary>설정/MCP 오버레이로 터미널이 스냅샷+Collapsed 정지 중 — 이 동안 전체화면/최대화
    /// post-hoc 커버가 개입하면(설정의 전체화면 토글이 대표) reveal 의 EndCover 가 오버레이용
    /// 정지 상태(숨긴 컨테이너·커튼)를 되살려 상태가 꼬인다. 정지 중엔 커버 개입을 건너뛴다 —
    /// 오버레이가 화면을 가리고 있고, ResumeTerminal 후 window.resize→fit 이 크기를 회복한다.</summary>
    private bool _overlaySuspended;

    /// <summary>모든 패널 터미널 + 우측 브라우저를 정지(스냅샷/커튼). 설정·MCP 오버레이용.</summary>
    private async Task SuspendTerminalWithSnapshotAsync(bool blankCurtain = false)
    {
        _overlaySuspended = true;
        await FileExplorer.SuspendBrowserAsync();
        foreach (var pane in _panes) await pane.SuspendTerminalWithSnapshotAsync(blankCurtain);
    }

    private void ResumeTerminal()
    {
        _overlaySuspended = false;
        FileExplorer.ResumeBrowser();
        foreach (var pane in _panes) pane.ResumeTerminal();
    }

    /// <summary>우측 오버레이 드로어용 — 터미널만 스냅샷 정지(브라우저는 오버레이 본문이라 제외).</summary>
    private async Task SuspendTerminalOnlyAsync()
    {
        foreach (var pane in _panes) await pane.SuspendTerminalOnlyAsync();
    }

    private void ResumeTerminalOnly()
    {
        foreach (var pane in _panes) pane.ResumeTerminalOnly();
    }

    // ── 타이틀바 ──────────────────────────────────────────────────
    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // ── 최대화 처리 (WM_GETMINMAXINFO) ───────────────────────────────
    // WindowStyle=None 창은 기본 최대화 시 작업영역을 넘쳐 가장자리가 잘린다.
    // 마진으로 보정하던 방식(복원 시 우측 클리핑 발생)을 버리고, 최대화 크기/위치를
    // 모니터 작업영역에 정확히 맞춰 오버플로 자체를 없앤다(표준 해법). 마진 불필요.
    private const int WM_GETMINMAXINFO = 0x0024;

    // 최대화 시 작업표시줄까지 덮을지(전체화면). 설정에서 토글.
    // Windows 는 WS_MAXIMIZE 창을 전체화면으로 인식하지 않아 작업표시줄을 못 덮는다.
    // 따라서 전체화면은 Normal 상태로 모니터 전체 rect 를 채워 셸 전체화면 감지를 유도한다.
    private bool _useFullScreen = SettingsService.LoadUseFullScreen();
    private bool _inFullScreen;        // 현재 수동 전체화면 중
    private bool _fsGuard;             // WindowState 변경 재진입 방지
    private bool _restoreFullScreen;   // 시작 복원 시 전체화면 진입 예약(올바른 모니터 감지용)
    private bool _restoreMaximized;    // 시작 복원 시 최대화 예약(hwnd 생성 후 = 저장 위치의 모니터로 최대화)
    private Rect _preFsBounds;         // 전체화면 진입 전 일반 창 bounds(복원용)

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _mainHwnd = new WindowInteropHelper(this).Handle;
        if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(WndProc);
        EnableDwmTransitions(_mainHwnd); // 최대화/복원 시 DWM 부드러운 전환 활성화
        ApplyCornerPreference();          // 최대화 시 각진 모서리(둥근 모서리가 화면 모서리를 깎는 문제 방지)
        ApplyMaximizeMargin();            // 최대화 시 프레임 두께만큼 마진 보정(가장자리 잘림 방지)
        // 저장된 '최대화' 복원: hwnd 가 이미 저장 위치에 만들어졌으므로 그 모니터로 최대화된다.
        // (ctor 에서 Maximized 로 두면 Left/Top 이 무시돼 항상 주 모니터로 최대화됨.)
        // StateChanged 구독 전에 처리 → 시작 시 전환 커버가 헛돌지 않는다.
        if (_restoreMaximized)
        {
            _restoreMaximized = false;
            WindowState = WindowState.Maximized;
            ApplyCornerPreference();
            ApplyMaximizeMargin();
        }
        _lastWindowState = WindowState; // 시작 복원 상태 기준으로 초기화(첫 StateChanged 의 prev 오판 방지)
        StateChanged += OnStateChangedForFullScreen;
        Activated   += (_, _) => { StopTaskbarAttention(); UpdateFullScreenTopmost(); };
        Deactivated += (_, _) => UpdateFullScreenTopmost();
        // 시작 시 전체화면 복원: 저장된 일반 bounds 위치(=올바른 모니터)에서 전체화면 진입.
        if (_restoreFullScreen) { _restoreFullScreen = false; EnterFullScreen(); }
        else if (_useFullScreen && WindowState == WindowState.Maximized) EnterFullScreen();
        UpdateMaxBtnVisual();
    }


    private WindowState _lastWindowState = WindowState.Normal; // OS 주도 최대화/복원 감지용(StateChanged 는 이전 상태를 안 주므로 직접 추적)

    private void OnStateChangedForFullScreen(object? sender, EventArgs e)
    {
        var prev = _lastWindowState;
        _lastWindowState = WindowState;
        ApplyCornerPreference();
        ApplyMaximizeMargin();
        if (_fsGuard) return;
        // 전체화면 설정 ON 상태에서 최대화 요청(드래그 상단 스냅·Win+↑ 등 시스템 주도) → 수동 전체화면으로 전환.
        // 이 시점엔 OS 최대화 리사이즈가 이미 끝난 뒤라 사전 캡처가 불가(찍으면 틀어진 중간 화면) →
        // 단색(post-hoc) 커버로 감싼다. 조건은 change 시점 재확인 — await 사이 상태가 바뀌었으면 no-op.
        if (_useFullScreen && WindowState == WindowState.Maximized && !_inFullScreen)
        {
            // 오버레이(설정/MCP) 정지 중엔 커버 개입 없이 전환만 — reveal 이 정지 상태를 되살리는 꼬임 방지.
            if (_overlaySuspended) EnterFullScreen();
            else RunFullScreenTransitionCovered(() =>
            {
                if (_useFullScreen && WindowState == WindowState.Maximized && !_inFullScreen)
                    EnterFullScreen();
            }, solidCover: true);
        }
        // 전체화면 미사용: OS 주도 최대화/복원(드래그 상단 스냅, 최대화 상태에서 캡션 끌어내리기,
        // Win+화살표, 작업표시줄 등) — 리사이즈가 이미 일어난 뒤 통지되므로 post-hoc 단색 커버로
        // 재fit·ConPTY 재동기·하단 복원만 수행한다. 우리 래퍼가 주도한 전환(_fsCoverBusy)은 자체 처리.
        // (최소화↔복원은 크기가 안 변하므로 Normal↔Maximized 간 전환만 해당.)
        else if (!_overlaySuspended
              && ((prev == WindowState.Maximized && WindowState == WindowState.Normal)
               || (prev == WindowState.Normal && WindowState == WindowState.Maximized)))
            RunFullScreenTransitionCovered(() => { }, solidCover: true);
        UpdateMaxBtnVisual();
    }

    /// <summary>최대화/전체화면 여부에 따라 컨트롤박스 버튼 아이콘·툴팁 동기화(devez MaxRestoreGlyph 패턴).</summary>
    private void UpdateMaxBtnVisual()
    {
        if (MaxBtnIcon == null) return;
        bool maximized = WindowState == WindowState.Maximized || _inFullScreen;
        MaxBtnIcon.Data = (System.Windows.Media.Geometry)FindResource(maximized ? "IconWinRestore" : "IconWinMaximize");
        MaxBtn.ToolTip = maximized ? "이전 크기로" : "최대화";
    }

    /// <summary>WS_MAXIMIZE 없이 모니터 전체를 채우는 수동 전체화면 진입(작업표시줄까지 덮음).</summary>
    private void EnterFullScreen()
    {
        if (_mainHwnd == IntPtr.Zero) return;
        if (!TryGetMonitorDip(out var monitor, out _)) return;
        var target = OverCover(monitor); // 가장자리 틈 방지 ±1px
        // 진입 전 일반 창 bounds(해제 시 복원용). 수동 전체화면은 WindowState=Normal 을 유지하므로
        // 최대화 이력이 없으면 RestoreBounds 가 Empty → exit 가 전체화면 크기로 폴백되던 버그.
        // Maximized 일 때만 RestoreBounds(그때만 일반 크기를 담음), 그 외엔 현재 실제 창 크기를 직접 캡처.
        // 시작 복원 경로(OnSourceInitialized)에선 아직 레이아웃 전이라 ActualWidth/Height 가 0 →
        // 복원된 Width/Height 프로퍼티로 대체. (안 하면 MinWidth/MinHeight 로 붕괴돼 다음 종료 때
        // '전체화면 해제 시 크기'가 최소 크기로 저장된다.)
        double pw = ActualWidth  > 0 ? ActualWidth  : (IsFinite(Width)  ? Width  : 0);
        double ph = ActualHeight > 0 ? ActualHeight : (IsFinite(Height) ? Height : 0);
        _preFsBounds = (WindowState == WindowState.Maximized) ? RestoreBounds : new Rect(Left, Top, pw, ph);
        if (!IsValidBounds(_preFsBounds)) _preFsBounds = CurrentWindowRectDip();
        if (!IsValidBounds(_preFsBounds))
            _preFsBounds = new Rect(0, 0, Math.Max(ActualWidth, MinWidth), Math.Max(ActualHeight, MinHeight));
        _inFullScreen = true;
        // 작업표시줄은 WS_EX_TOPMOST 라 일반 창은 못 덮음(보조 모니터는 셸 전체화면 감지도 안 먹음).
        // WPF Topmost 속성으로 올려 z-order 로 확실히 덮는다.
        Topmost = true;
        ResizeMode = ResizeMode.NoResize; // 전체화면 중 창 리사이즈 금지
        if (RootChrome != null) RootChrome.Margin = default;
        if (WindowState == WindowState.Maximized)
        {
            // 시작 복원·스냅 등 이미 최대화 — WindowState=Maximized→Normal 은 WPF 가 이전 창 크기로
            // 지연 리사이즈를 큐에 넣으므로 그 *이후*(Background)에도 한 번 더 적용.
            _fsGuard = true;
            WindowState = WindowState.Normal;
            _fsGuard = false;
            Dispatcher.InvokeAsync(() => { if (_inFullScreen) SetBoundsInstant(target); },
                                   System.Windows.Threading.DispatcherPriority.Background);
        }
        SetBoundsInstant(target);
        ApplyCornerPreference();
        UpdateMaxBtnVisual();
    }


    /// <summary>전체화면 해제 → 현재 모니터 작업영역의 가로·세로 절반 크기로 중앙 배치(애니메이션 없음).</summary>
    private void ExitFullScreen()
    {
        if (!_inFullScreen) return;
        _inFullScreen = false;
        Topmost = false;
        ResizeMode = ResizeMode.CanResize;
        SetBoundsInstant(HalfCenteredOnMonitor());
        ApplyCornerPreference();
        UpdateMaxBtnVisual();
    }

    /// <summary>현재 창이 속한 모니터 작업영역의 가로·세로 70% 크기 + 중앙 위치 Rect.</summary>
    private Rect HalfCenteredOnMonitor()
    {
        var area = TryGetMonitorDip(out _, out var work) ? work : new Rect(Left, Top, ActualWidth, ActualHeight);
        double w = Math.Max(area.Width * 0.7, MinWidth), h = Math.Max(area.Height * 0.7, MinHeight);
        return new Rect(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
    }

    /// <summary>현재 창이 속한 모니터의 전체/작업 영역을 DIP Rect 로 반환.</summary>
    private bool TryGetMonitorDip(out Rect monitor, out Rect work)
    {
        monitor = Rect.Empty; work = Rect.Empty;
        var mh = MonitorFromWindow(_mainHwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mh == IntPtr.Zero || !GetMonitorInfo(mh, ref info)) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        Rect ToDip(RECT r) => new(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY,
                                  (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
        monitor = ToDip(info.rcMonitor); work = ToDip(info.rcWork);
        return true;
    }

    private static Rect OverCover(Rect r) => new(r.Left - 1, r.Top - 1, r.Width + 2, r.Height + 2);

    private void SetBoundsInstant(Rect r)
    {
        Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
    }

    private bool _fsCoverBusy; // 전체화면 전환 커버 진행 중(연타 무시용)

    /// <summary>전체화면 진입/해제를 터미널 webCover 로 감싸 실행. 전체화면 전환은 창 전체가 한 번에
    /// 즉시 리사이즈되는 가장 큰 리플로우인데, 커버 없이 하면 WebView2 가 클리어→재fit→TUI 비동기
    /// 재렌더를 마칠 때까지 터미널 내용이 비어 보인다(사이드패널/분할은 이미 커버로 가리는 것과 동일
    /// 증상). 캡처 커버 아래서 리사이즈하고 최종 크기에서 fit·재동기 후 크로스페이드한다.
    /// change 후 Background 우선순위까지 기다리는 이유: EnterFullScreen 이 Maximized 경유 시 최종
    /// bounds 를 Background 에서 한 번 더 적용하므로, 그 뒤에 reveal 해야 expectWidth 가 최종값이 된다.</summary>
    private async void RunFullScreenTransitionCovered(Action change, bool solidCover = false)
    {
        if (_fsCoverBusy) return; // 전환 중 연타 무시(커버/리빌 상태 꼬임 방지)
        _fsCoverBusy = true;
        try
        {
            var covered = _panes.Where(p => p.Visibility == Visibility.Visible).ToList();
            if (solidCover)
            {
                // 리사이즈가 이미 일어난 뒤 통지되는 경로(OS 주도 최대화/복원 — 드래그 스냅·Win+화살표 등):
                // 캡처는 이미 틀어진 중간 화면을 찍으므로 단색 커버를 즉시 덮는다(파일 커튼 포함).
                // 스크롤: JS 의 wasAtBottom 기록이 리사이즈 이후라 부정확할 수 있어, pinBottom 으로
                // 이어지는 TUI 재렌더 출력 동안 하단을 강제 유지한다(스크롤 튐 방지).
                foreach (var p in covered)
                {
                    p.CoverForTransition();
                    if (p.ActiveSession != null) p.Terminal.PinBottom(1500);
                }
            }
            else
            {
                // stretch: 창 전체가 한 번에 크게 변하므로 커버를 뷰포트에 맞춰 늘린다(OS 최대화 애니메이션 인상).
                // 좌상단 px 고정을 쓰면 커지는 쪽(오른쪽·아래)이 배경색만 남아 '비어' 보인다.
                await FreezeWorkspaceTerminalsAsync(stretchCover: true);
            }
            change();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            // bounce: post-hoc 경로는 리사이즈·재방출이 커버 '전'에 무방비로 일어났다 — reveal fit 이
            // 무변화면 재방출이 없어 tear 가 고착될 수 있으므로 커버 아래서 rows 바운스로 재방출을 강제.
            if (solidCover) foreach (var p in covered) p.RevealAfterTransition(kick: true, bounce: true);
            else UnfreezeWorkspaceTerminals();
        }
        finally { _fsCoverBusy = false; }
    }

    /// <summary>전체화면 중 활성/비활성에 따라 Topmost 토글 — 다른 창으로 전환 시엔 내려서
    /// 그 창이 보이도록(정상 동작), 다시 활성화되면 올려 작업표시줄을 덮는다.</summary>
    private void UpdateFullScreenTopmost()
    {
        if (!_inFullScreen) return;
        Topmost = IsActive;
    }


    /// <summary>WS_CAPTION + WS_THICKFRAME(ResizeMode=CanResize) 창은 최대화 시 표준 방식으로
    /// 프레임만큼 화면 밖으로 위치해 콘텐츠 가장자리가 잘린다. 최대화 상태에서만 루트에
    /// 프레임 두께(DPI 보정)만큼 마진을 줘 잘림을 막는다.</summary>
    private void ApplyMaximizeMargin()
    {
        if (RootChrome == null) return;
        if (WindowState == WindowState.Maximized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int pad = GetSystemMetrics(SM_CXPADDEDBORDER);
            double x = (GetSystemMetrics(SM_CXFRAME) + pad) / dpi.DpiScaleX;
            double y = (GetSystemMetrics(SM_CYFRAME) + pad) / dpi.DpiScaleY;
            RootChrome.Margin = new Thickness(x, y, x, y);
        }
        else RootChrome.Margin = default;
    }

    /// <summary>전체화면(작업표시줄 덮기) 설정 적용. 현재 최대화/전체화면 상태면 즉시 전환.</summary>
    public void ApplyFullScreen(bool useFullScreen)
    {
        _useFullScreen = useFullScreen;
        if (useFullScreen)
        {
            if (WindowState == WindowState.Maximized && !_inFullScreen) EnterFullScreen();
        }
        else if (_inFullScreen)
        {
            ExitFullScreen();
            WindowState = WindowState.Maximized; // 전체화면 해제 시 일반 최대화로
        }
    }

    private const int SM_CXFRAME = 32, SM_CYFRAME = 33, SM_CXPADDEDBORDER = 92;
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    private IntPtr _mainHwnd;

    /// <summary>WS_CAPTION 부여로 최대화 애니메이션을 살리면 Win11 둥근 모서리가 최대화 화면
    /// 모서리를 깎는다. 최대화 상태에서만 각진 모서리(DONOTROUND)로 전환해 잘림을 막는다.</summary>
    private void ApplyCornerPreference()
    {
        if (_mainHwnd == IntPtr.Zero) return;
        int pref = (WindowState == WindowState.Maximized || _inFullScreen) ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
        DwmSetWindowAttribute(_mainHwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONUP = 0x0202;
    private const int HTCAPTION = 2;

    // 전체화면 중 캡션 누름 추적: 실제 드래그일 때만 축소, 단순 클릭은 무시, 더블클릭은 시간차로 직접 판정.
    private bool _fsCapPending;
    private int _fsCapDownTick;
    private POINT _fsCapDownPt;
    // 축소 후 수동 이동 드래그 상태 — Windows SC_MOVE(복원+드래그 인계) 트릭은 실제 WS_MAXIMIZE 가
    // 아닌 우리 수동 전체화면(WindowState.Normal 을 모니터 크기로 채운 것)에서는 내부 휴리스틱이
    // 안 맞아 커서가 한참 움직여야 뒤늦게 붙는 데드존이 생겼다 — 그래서 SC_MOVE 에 맡기지 않고
    // 캡션을 쥔 오프셋을 고정해 두고 매 WM_MOUSEMOVE 마다 직접 SetWindowPos 로 따라가게 한다.
    private bool _fsDragging;
    private int _fsDragOffX, _fsDragOffY; // 창 좌상단 → 그랩 지점 오프셋(물리 px), 드래그 시작 시 고정

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO) { WmGetMinMaxInfo(lParam); handled = true; }
        // Alt 단독 탭/F10 의 메뉴 모드 진입 차단(lParam==0 인 SC_KEYMENU) — 전역 탭 단축키의 Alt 트릭이나
        // 실제 Alt 탭 후 ↑/↓ 화살표에 시스템 메뉴(이전 크기로~닫기)가 열리는 것을 막는다.
        // Alt+Space(lParam=' ')의 의도적 시스템 메뉴 호출은 통과.
        else if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU && lParam == IntPtr.Zero)
            handled = true;
        // 상단바(캡션) 더블클릭: 전체화면 ON 은 기본 최대화 대신 전체화면 토글(기존 동작, hit-test 무관),
        // OFF 도 OS 기본 최대화 대신 우리 토글로 가로채 사전 캡처 커버를 적용한다(캡션에 한정 —
        // 테두리 더블클릭의 OS 수직 최대화 등 기타 NC 동작은 보존).
        else if (msg == WM_NCLBUTTONDBLCLK && (_useFullScreen || wParam.ToInt32() == HTCAPTION))
        { ToggleMaximizeOrFullScreen(); handled = true; }
        // 전체화면 중 캡션 누름: down 에서 바로 처리하지 않고 캡처 후 드래그/클릭/더블클릭을 구분.
        else if (msg == WM_NCLBUTTONDOWN && wParam.ToInt32() == HTCAPTION && _inFullScreen)
        {
            handled = true;
            int sx = (short)(lParam.ToInt32() & 0xFFFF);
            int sy = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
            int tick = Environment.TickCount;
            bool dbl = (tick - _fsCapDownTick) <= GetDoubleClickTime()
                       && Math.Abs(sx - _fsCapDownPt.X) <= GetSystemMetrics(SM_CXDOUBLECLK)
                       && Math.Abs(sy - _fsCapDownPt.Y) <= GetSystemMetrics(SM_CYDOUBLECLK);
            if (dbl)
            {
                _fsCapPending = false; ReleaseCapture(); _fsCapDownTick = 0;
                ToggleMaximizeOrFullScreen(); // 전체화면 해제
            }
            else
            {
                _fsCapPending = true; _fsCapDownTick = tick; _fsCapDownPt = new POINT { X = sx, Y = sy };
                SetCapture(_mainHwnd);
            }
        }
        // 캡처 중 충분히 움직이면 드래그로 판정 → 축소하고 수동 드래그 상태로 전환(캡처 유지).
        else if (msg == WM_MOUSEMOVE && _fsCapPending && _inFullScreen)
        {
            GetCursorPos(out var p);
            if (Math.Abs(p.X - _fsCapDownPt.X) > GetSystemMetrics(SM_CXDRAG)
                || Math.Abs(p.Y - _fsCapDownPt.Y) > GetSystemMetrics(SM_CYDRAG))
            {
                _fsCapPending = false; _fsCapDownTick = 0;
                handled = true;
                BeginDragFromFullScreen(p.X, p.Y);
            }
        }
        // 수동 드래그 중: 매 이동마다 그랩 오프셋을 유지하며 직접 이동(리사이즈 없음) — 커서에 정확히 붙는다.
        else if (msg == WM_MOUSEMOVE && _fsDragging)
        {
            handled = true;
            GetCursorPos(out var p);
            SetWindowPos(_mainHwnd, IntPtr.Zero, p.X - _fsDragOffX, p.Y - _fsDragOffY, 0, 0,
                         SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
        }
        // 움직임 없이 떼면 단순 클릭 — 아무 동작 안 함(다음 down 과의 시간차로 더블클릭 판정).
        // 드래그 중이었다면 여기서 종료.
        else if (msg == WM_LBUTTONUP && (_fsCapPending || _fsDragging))
        {
            _fsCapPending = false; _fsDragging = false; ReleaseCapture(); handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>전체화면 중 캡션 드래그 시작 — 축소하되, 캡션을 잡은 지점이 새 창에서도 커서 아래
    /// 정확히 같은 자리(가로는 창폭 대비 비율, 세로는 캡션 상단 기준 절대 오프셋)에 오도록 재배치한 뒤
    /// 그랩 오프셋을 고정해 이후 WM_MOUSEMOVE 마다 직접 따라가게 한다.</summary>
    private void BeginDragFromFullScreen(int screenPxX, int screenPxY)
    {
        if (!_inFullScreen) return;
        // 단색 커버 즉시 post — 캡처(await)는 드래그 시작을 지연시켜 커서 추종이 어긋나므로 불가.
        // reveal 은 Background 로 예약: JS 쪽 reveal(폭 대기→fit→크로스페이드)은 WPF 드래그 루프와
        // 무관하게 진행되므로 드래그 중에도 축소된 크기로 터미널이 정상 복귀한다. 하단 복원은 pin.
        var dragCovered = _panes.Where(p => p.Visibility == Visibility.Visible).ToList();
        foreach (var p in dragCovered)
        {
            p.CoverForTransition();
            if (p.ActiveSession != null) p.Terminal.PinBottom(1500);
        }
        _inFullScreen = false;
        Topmost = false;
        ResizeMode = ResizeMode.CanResize;

        var target = HalfCenteredOnMonitor();
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(target.Width * dpi.DpiScaleX);
        int ph = (int)Math.Round(target.Height * dpi.DpiScaleY);
        int px = (int)Math.Round(target.X * dpi.DpiScaleX);
        int py = (int)Math.Round(target.Y * dpi.DpiScaleY);

        var mon = MonitorFromWindow(_mainHwnd, MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref info))
            {
                double monW = info.rcMonitor.Right - info.rcMonitor.Left;
                double ratioX = monW > 0 ? (_fsCapDownPt.X - info.rcMonitor.Left) / monW : 0.5;
                // 캡션 높이는 전체화면·창모드 동일 → 다운 지점의 세로 오프셋(윈도우 상단 기준)을
                // 스케일 없이 그대로 보존해야 커서가 캡션의 같은 지점에 정확히 붙는다.
                // (가로는 폭이 급격히 줄어드는 값이라 비율(ratioX)로 스케일해야 캡션 밖으로 안 벗어난다.)
                int yOffsetPhysical = _fsCapDownPt.Y - info.rcMonitor.Top;
                px = (int)Math.Round(screenPxX - ratioX * pw);
                py = screenPxY - yOffsetPhysical;
            }
        }

        SetWindowPos(_mainHwnd, IntPtr.Zero, px, py, pw, ph, SWP_NOZORDER | SWP_NOACTIVATE);
        ApplyCornerPreference();
        // 리사이즈 반영 후 커버 해제(fit·재동기·크로스페이드). 드래그 이동(WM_MOUSEMOVE)은 리사이즈가
        // 아니므로 reveal 뒤에도 터미널은 안정 상태를 유지한다. bounce: 커버 post 와 리사이즈가 경합해
        // 리사이즈가 커버보다 먼저 그려졌을 수 있는 경로 — fit 무변화 시 재방출을 강제해 tear 고착 방지.
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var p in dragCovered) p.RevealAfterTransition(kick: true, bounce: true);
        }, System.Windows.Threading.DispatcherPriority.Background);

        // 그랩 오프셋 고정 — 이후 WM_MOUSEMOVE 마다 이 오프셋만큼 커서에서 뺀 위치로 직접 이동.
        _fsDragOffX = screenPxX - px;
        _fsDragOffY = screenPxY - py;
        _fsDragging = true;
        // 캡처는 유지(ReleaseCapture 안 함) — WM_LBUTTONUP 까지 계속 이 창으로 마우스 메시지를 받아야 함.
    }

    private const uint SWP_NOZORDER = 0x0004, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37, SM_CXDRAG = 68, SM_CYDRAG = 69;
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern int GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>최대화 시 창이 모니터 작업영역에 정확히 맞도록 위치/크기 상한을 설정.
    /// XAML MinWidth/MinHeight 도 여기서 ptMinTrackSize 에 반영한다(WPF 내부 처리가
    /// handled=true 로 스킵되므로).</summary>
    private void WmGetMinMaxInfo(IntPtr lParam)
    {
        var monitor = MonitorFromWindow(_mainHwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var work = info.rcWork; var mon = info.rcMonitor;
        mmi.ptMaxPosition.X = work.Left - mon.Left;
        mmi.ptMaxPosition.Y = work.Top - mon.Top;
        mmi.ptMaxSize.X = work.Right - work.Left;
        mmi.ptMaxSize.Y = work.Bottom - work.Top;

        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.X = (int)(MinWidth * dpi.DpiScaleX);
        mmi.ptMinTrackSize.Y = (int)(MinHeight * dpi.DpiScaleY);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private const int MONITOR_DEFAULTTONEAREST = 0x2;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    private const uint FLASHW_STOP = 0x00000000;
    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);

    /// <summary>창이 사용자에게 실제로 보이는지 판정. 최소화면 false. 화면상 중앙/네 사분점 중 하나라도
    /// 우리 창(루트)이 최상단이면 true(=보임). 다른 창이 완전히 덮으면 false(=가려짐).</summary>
    private static bool IsWindowVisibleToUser(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) return false;
        if (!GetWindowRect(hwnd, out var r)) return true; // 판정 불가 시 보수적으로 보임 처리(깜빡임 억제)
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return false;
        var samples = new[]
        {
            new POINT { X = r.Left + w / 2, Y = r.Top + h / 2 }, // 중앙
            new POINT { X = r.Left + w / 4, Y = r.Top + h / 4 },
            new POINT { X = r.Left + w * 3 / 4, Y = r.Top + h / 4 },
            new POINT { X = r.Left + w / 4, Y = r.Top + h * 3 / 4 },
            new POINT { X = r.Left + w * 3 / 4, Y = r.Top + h * 3 / 4 },
        };
        foreach (var p in samples)
        {
            var top = GetAncestor(WindowFromPoint(p), 2 /*GA_ROOT*/);
            if (top == hwnd) return true;
        }
        return false;
    }

    /// <summary>창이 좁아질 때 우측 패널(탐색기/DIFF)이 화면 밖으로 잘리지 않게 폭을 가용 범위로 클램프.
    /// 최대화 상태에서 패널을 넓힌 뒤 창모드로 복원하면 고정 px 폭이 남아 오른쪽이 잘리던 문제를 막는다.</summary>
    private void BodyGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double avail = BodyGrid.ActualWidth;
        if (avail <= 0) return;

        // 폭이 임계값을 넘나들면 도킹 ↔ 오버레이 모드를 전환한다.
        // 이때 우측 패널의 "표시 여부"는 그대로 이어받는다(도킹 표시 ↔ 오버레이 열림,
        // 도킹 접힘 ↔ 오버레이 닫힘) — 리사이즈만으로 표시 상태가 바뀌지 않게.
        bool narrow = avail < NarrowThreshold;
        if (_narrow != narrow)
        {
            bool wasShown = _narrow == true ? _rightOverlayOpen : !_rightCollapsed;
            _narrow = narrow;
            if (narrow) EnterNarrowMode(wasShown);
            else        EnterWideMode(wasShown);
        }

        if (narrow)
        {
            // 열려 있는 오버레이는 창 폭에 맞춰 폭을 갱신한다.
            if (_rightOverlayOpen) RightOverlayPanel.Width = OverlayWidth();
            return;
        }

        // 도킹 모드: 파일탐색기가 중앙 최소폭 + 우측 보조 패널을 창 밖으로 밀어내지 않게 클램프.
        ClampFileExpToFit();
    }

    /// <summary>도킹 모드에서 파일탐색기가 가질 수 있는 최대 폭.
    /// 중앙 터미널 최소폭(360)과 오른쪽 보조 패널(완료기록/사용량)이 창 안에 남도록
    /// 그 폭을 미리 예약해 계산한다 → 파일탐색기가 우측 패널을 화면 밖으로 밀지 않는다.</summary>
    private double ComputeMaxFileExpWidth()
    {
        const double centerMin = 360;
        double avail = BodyGrid.ActualWidth;
        double reservedRight =
            (_sessionHistoryOpen ? SettingsService.LoadSessionHistoryWidth() + 4 : 0) // 4=완료기록 스플리터 채널
            + (_usageOpen ? UsagePanelWidth : 0);
        double splitters = SidebarSplitterCol.ActualWidth + (_rightCollapsed ? 0 : 4); // 파일탐색기 스플리터
        double floor = FileExpCol.MinWidth > 0 ? FileExpCol.MinWidth : 190;
        double max = avail - SidebarCol.ActualWidth - splitters - reservedRight - centerMin;
        return Math.Max(max, floor);
    }

    /// <summary>파일탐색기가 열려 있고 우측 보조 패널을 밀어낼 만큼 넓으면 남는 공간까지만 줄인다.
    /// 사용자가 선호한 폭(_fileExpWidth)은 보존하므로, 창이 넓어지거나 우측 패널을 닫은 뒤
    /// 파일탐색기를 다시 토글하면 원래 폭으로 복원된다.</summary>
    private void ClampFileExpToFit()
    {
        if (_narrow == true || _rightCollapsed) return;
        if (BodyGrid.ActualWidth <= 0) return;
        double max = ComputeMaxFileExpWidth();
        double current = FileExpCol.Width.IsAbsolute ? FileExpCol.Width.Value : FileExpCol.ActualWidth;
        if (current <= max + 0.5) return;

        // SharedSizeGroup 은 멤버 중 최대 폭을 채택하므로 푸터 미러도 함께 줄여야 실제로 줄어든다.
        FileExpCol.Width = new GridLength(max);
        FooterFileExpCol.Width = new GridLength(max);
    }

    /// <summary>오버레이 드로어 폭. 중앙이 일부 보이도록 창 폭에 따라 제한.
    /// 컬럼 분배에 영향받지 않도록 창(Window) 실제 폭을 기준으로 계산한다.</summary>
    private double OverlayWidth() => Math.Min(560, Math.Max(320, ActualWidth - 440));

    /// <summary>FileExplorer 를 본문 그리드의 우측 컬럼(도킹 위치)으로 되돌린다.</summary>
    private void DockFileExplorer()
    {
        if (ReferenceEquals(RightOverlayPanel.Child, FileExplorer)) RightOverlayPanel.Child = null;
        if (!BodyGrid.Children.Contains(FileExplorer))
        {
            Grid.SetColumn(FileExplorer, 4);
            Grid.SetColumnSpan(FileExplorer, 1);
            BodyGrid.Children.Add(FileExplorer);
        }
    }

    /// <summary>FileExplorer 를 오버레이 호스트(우측 드로어)로 재부모화한다.</summary>
    private void ReparentToOverlay()
    {
        if (BodyGrid.Children.Contains(FileExplorer)) BodyGrid.Children.Remove(FileExplorer);
        FileExplorer.Visibility = Visibility.Visible;
        if (!ReferenceEquals(RightOverlayPanel.Child, FileExplorer)) RightOverlayPanel.Child = FileExplorer;
    }

    /// <summary>도킹 → 좁은 창: 우측 컬럼을 빼고, 직전 표시 상태를 이어받는다
    /// (표시 중이었으면 오버레이로 계속 표시, 접혀 있었으면 오버레이도 닫힘).</summary>
    private void EnterNarrowMode(bool shown)
    {
        // 우측 컬럼 제거(폭 0). SharedSizeGroup 을 풀어야 폭 0 이 실제로 먹는다.
        FileExpSplitterCol.SharedSizeGroup = null;
        FileExpCol.SharedSizeGroup = null;
        FileExpSplitterCol.Width = new GridLength(0);
        FileExpCol.MinWidth = 0;
        FileExpCol.Width = new GridLength(0);

        if (shown)
        {
            _ = OpenRightOverlay();   // 표시 중이었으면 오버레이로 이어서 표시
        }
        else
        {
            _rightOverlayOpen = false;
            RightOverlayHost.Visibility = Visibility.Collapsed;
            DockFileExplorer();
            FileExplorer.Visibility = Visibility.Collapsed;
            UpdatePanelToggleVisual();
        }
    }

    /// <summary>좁은 창 → 도킹: 오버레이를 걷고 우측 컬럼으로 되돌리며 표시 상태를 이어받는다.</summary>
    private void EnterWideMode(bool shown)
    {
        _rightOverlayOpen = false;
        RightOverlayHost.Visibility = Visibility.Collapsed;
        ResumeTerminalOnly();   // 오버레이가 열린 채 넓어졌다면 터미널 복원
        _rightT.X = 0;
        DockFileExplorer();
        // SharedSizeGroup 복원(상태바 컬럼과 정렬).
        FileExpSplitterCol.SharedSizeGroup = "MainFileExpSplitter";
        FileExpCol.SharedSizeGroup = "MainFileExp";
        _rightCollapsed = !shown;   // 표시 상태 보존
        FileExplorer.Visibility = _rightCollapsed ? Visibility.Collapsed : Visibility.Visible;
        FileExpSplitterCol.Width = new GridLength(_rightCollapsed ? 0 : 4);
        SetMinWidth(_rightCollapsed ? 0 : _fileExpMinWidth, FileExpCol, FooterFileExpCol);
        FileExpCol.Width = new GridLength(_rightCollapsed ? 0 : _fileExpWidth);
        SettingsService.SaveRightPanel(_rightCollapsed, _fileExpWidth);
        UpdatePanelToggleVisual();
    }

    /// <summary>좁은 창에서 우측 패널 오버레이와 스크림을 즉시 연다.
    /// 중앙 터미널 WebView2 는 native HWND 라 WPF 오버레이를 뚫고 올라오므로 스냅샷으로 정지한다.</summary>
    private async Task OpenRightOverlay()
    {
        await SuspendTerminalOnlyAsync();   // airspace 우회: 터미널을 스냅샷으로 정지
        RightOverlayPanel.Width = OverlayWidth();
        ReparentToOverlay();
        _rightT.X = 0;
        RightOverlayHost.Visibility = Visibility.Visible;
        _rightOverlayOpen = true;
        UpdatePanelToggleVisual();
        UpdateUsageSidebarBorder();
    }

    /// <summary>오버레이를 즉시 닫고 도킹 위치로 복귀한다.</summary>
    private void CloseRightOverlay()
    {
        _rightOverlayOpen = false;
        RightOverlayHost.Visibility = Visibility.Collapsed;
        _rightT.X = 0;
        DockFileExplorer();
        FileExplorer.Visibility = Visibility.Collapsed; // 좁은 창에서는 닫힘=숨김
        ResumeTerminalOnly();                            // 터미널 복원
        UpdateUsageSidebarBorder();
        UpdatePanelToggleVisual();
    }

    private void RightScrim_Click(object sender, MouseButtonEventArgs e) => CloseRightOverlay();

    private void MaxBtn_Click(object sender, RoutedEventArgs e) => ToggleMaximizeOrFullScreen();

    /// <summary>최대화 버튼·상단바 더블클릭 공통 토글. 전체화면 설정 ON 이면 Maximized 상태를
    /// 거치지 않고 Normal 에서 바로 전체화면 진입/해제(최대화→복원 2단 애니메이션 제거).</summary>
    private void ToggleMaximizeOrFullScreen()
    {
        if (_useFullScreen)
        {
            // 커버가 올라온 뒤(change 시점) 상태를 다시 보고 토글 — await 사이 상태 변화에 안전.
            RunFullScreenTransitionCovered(() =>
            {
                if (_inFullScreen) ExitFullScreen(); else EnterFullScreen();
            });
            return;
        }
        // 전체화면 미사용: 일반 최대화/복원도 리사이즈 리플로우는 동일 → 사전 캡처 커버로 감싼다.
        // (여기서 바뀐 WindowState 의 StateChanged post-hoc 커버는 _fsCoverBusy 가드로 중복 방지.)
        RunFullScreenTransitionCovered(() =>
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        });
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>'닫기 버튼으로 최소화'가 켜져 있어도 강제로 실제 종료. (닫기 버튼 우클릭 메뉴)</summary>
    private void QuitApp_Click(object sender, RoutedEventArgs e) { ForceQuit = true; Close(); }

    // ── 보더리스 창에 DWM 최대화/복원 애니메이션 부활 ─────────────────
    // WindowStyle=None 창은 WS_CAPTION 이 없어 DWM 이 최대화/복원/최소화 전환 애니메이션을
    // 생략한다(즉시 변함). 캡션 스타일을 Win32 레벨에서 다시 부여하면 DWM 이 "일반 창"으로 보고
    // 부드러운 전환을 그려준다. 시각적 캡션/테두리는 WindowChrome 의 NCCALCSIZE 가 덮어 안 보인다.
    private const int GWL_STYLE   = -16;
    private const int WS_CAPTION  = 0x00C00000;

    private void EnableDwmTransitions(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style | WS_CAPTION);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
