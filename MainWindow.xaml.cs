using System.Collections.ObjectModel;
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
    private readonly ObservableCollection<ProjectItem> _projects;
    // 보관함 프로젝트(archived_at 있음) — 활성 목록과 분리 관리. WorkspaceStore 가 함께 영속.
    private readonly ObservableCollection<ProjectItem> _archivedProjects;
    private readonly ObservableCollection<SessionCompletionRecord> _sessionDoneRecords = new();
    // 중앙 워크스페이스 패널들(분할 시 2개). _focusedPane = 사이드바/파일탐색기/단축키가 향하는 패널.
    private readonly List<WorkspacePaneView> _panes = new();
    private WorkspacePaneView _focusedPane = null!;   // 생성자에서 PaneA 로 초기화

    // 좌/우 위치 교환은 콘텐츠 이동 없이 패널의 물리 컬럼만 맞바꿔 표현한다(터미널 재부착=세션 재로딩 방지).
    // _panesSwapped=false → PaneA 가 좌(col0)/PaneB 가 우(col2), true → 반대. 비분할 시엔 항상 false 로 정규화.
    private bool _panesSwapped;
    private WorkspacePaneView LeftPane  => _panesSwapped ? PaneB : PaneA;
    private WorkspacePaneView RightPane => _panesSwapped ? PaneA : PaneB;
    private string? _explorerDir;                      // 우측 파일탐색기가 보고 있는 경로(중복 ShowDirectory 방지)
    private readonly PerfMonitorService _perfMonitor = new();
    // 계정 사용량: statusLine 훅(세션 활성 시 거의 실시간) + OAuth API(세션 없어도 3분 주기) 두 소스를 병합.
    private readonly StatusLineService _statusLine = new();
    private readonly UsageApiService _usageApi = new();
    private Models.RateLimitSnapshot? _rlMerged; // 두 소스를 합친 푸터 표시값
    // 추가 provider 사용량(푸터): codex(openai) + opencode-go.
    private readonly CodexUsageService _codex = new();
    private readonly OpenCodeGoUsageService _openCodeGo = new();
    // 사용량 팝오버(우측 사이드바)용 최신 스냅샷 보관 — 데이터 있는 provider 만 카드로 노출.
    private Models.ProviderUsage? _lastCodex;
    private Models.ProviderUsage? _lastGo;
    private readonly SessionBusyService _sessionBusy = new();
    // claude statusLine 훅이 떨군 방별 실제 model/effort 를 감시해 메타바 콤보에 라이브 연동.
    private readonly ModelEffortService _modelEffort = new();
    private readonly SessionLastMessageService _sessionLastMsg = new();
    // codex — Claude 와 동일하게 ~/.codex/hooks.json 으로 lastmsg/busy/session_id 추적.
    private readonly CodexHookService _codexHook = new();
    // 비-Claude 비-codex (opencode/gjc) 의 last prompt 추적. codex 는 위 훅 서비스가 처리.
    private readonly AgentLastMessageService _agentLastMsg = new();
    // opencode — 플러그인이 lastmsg\<room>.txt 에 저장한 user prompt 를 FileSystemWatcher 로 즉시 반영 (claude 와 동일 패턴).
    private readonly OpenCodeLastMessageService _opencodeLastMsg = new();
    // opencode — 플러그인이 busy\<room>.txt 에 저장한 처리중 상태를 감시해 스피너 연동 (claude busy hook 과 동일 패턴).
    private readonly OpenCodeBusyService _opencodeBusy = new();
    // gjc(가재코드) — 훅 미지원. 방별 세션 .jsonl 을 폴링해 마지막 user 메시지를 헤더에 반영.
    private readonly GajaeLastMessageService _gajaeLastMsg = new();

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowPlacement();   // 마지막 창 위치/크기/최대화 복원 (없으면 CenterScreen 유지)
        SessionHistoryList.ItemsSource = _sessionDoneRecords;
        // 영속된 완료 기록 복원 (설정에 저장된 최신순 목록).
        var saved = SettingsService.LoadSessionHistoryRecords();
        if (saved != null && saved.Count > 0)
        {
            foreach (var r in saved) _sessionDoneRecords.Add(r);
        }
        UpdateSessionHistoryEmpty();

        _projects = WorkspaceStore.Load(out var archived);
        UpdateSessionBusyDisplay();
        _archivedProjects = archived;
        Sidebar.Projects = _projects;
        Sidebar.ArchivedProjects = _archivedProjects;
        SetupPane(PaneA);
        SetupPane(PaneB);   // 분할 전엔 숨김(XAML Collapsed). 분할 시 노출.
        PaneB.IsRightPane = true;   // 분할 시 우측 패널 — 탭바 버튼이 X(분할 닫기)로 표시됨.
        CenterSplit.SizeChanged += (_, _) => UpdatePaneFocusVisual(animate: false);
        _focusedPane = PaneA;
        _ = MarkdownWysiwygHost.PrewarmAsync();

        Sidebar.AddProjectRequested    += AddProject;
        Sidebar.ProjectSelected        += SelectProject;
        Sidebar.AddSessionRequested    += AddSession;
        Sidebar.ProjectDeleteRequested += DeleteProject;
        Sidebar.ProjectRenameRequested += RenameProject;
        Sidebar.ShowInLeftPaneRequested  += p => ShowProjectInPane(p, left: true);
        Sidebar.ShowInRightPaneRequested += p => ShowProjectInPane(p, left: false);
        Sidebar.ProjectArchiveRequested += ArchiveProject;
        Sidebar.ProjectUnarchiveRequested += UnarchiveProject;
        Sidebar.AddProjectFileRequested += AddProjectFile;
        Sidebar.ProjectFileSelected    += OpenProjectFile;
        Sidebar.ProjectFileRemoveRequested += RemoveProjectFile;
        Sidebar.ProjectFileRenameRequested += RenameProjectFile;
        Sidebar.ProjectsReordered += () => WorkspaceStore.Save(_projects);
        Sidebar.ProjectExpandChanged += () => WorkspaceStore.Save(_projects);
        Sidebar.SessionsReordered += OnSidebarSessionsReordered;
        Sidebar.SessionSelected        += OpenSession;
        Sidebar.SessionDeleteRequested += DeleteSession;
        Sidebar.SessionRenameRequested += RenameSession;
        Sidebar.SessionStopTrackingRequested += StopTrackingSession;
        Sidebar.UpdateClicked += OpenUpdatePopup; // 좌측 하단 업데이트 버튼 → 노트 팝업 → 설치

        // 세션 요청 처리중 스피너: claude 훅(busy-hook.ps1)이 떨군 상태 파일을 감시 (clude-blinker 방식).
        _sessionBusy.BusyChanged += (id, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(id);
                bool was = s?.IsBusy ?? false;
                if (s != null) s.IsBusy = busy;
                NotifyIfSessionFinished(s, was, busy);
                UpdateSessionBusyDisplay();
                if (!busy) foreach (var pane in _panes) pane.FlushPendingModelEffort(id);
            });

        // statusLine 훅이 떨군 방별 실제 model/effort → 해당 세션을 보여주는 패널 콤보를 라이브 갱신.
        _modelEffort.Changed += (roomId, modelId, effortLevel) =>
            Dispatcher.InvokeAsync(() => { foreach (var pane in _panes) pane.NotifyModelEffortChanged(roomId); });

        // 마지막 보낸 메시지: busy 훅이 떨군 lastmsg 파일을 감시 → 세션에 반영(헤더 부제 라이브 갱신).
        _sessionLastMsg.MessageChanged += (id, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(id);
                if (s == null) return;
                if (!ApplyHeaderMessage(s, msg)) return;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });

        // 비-Claude 비-codex 에이전트(opencode/gjc) — (workingDir, lastPrompt) 이벤트로 같은 디렉터리 세션 모두 갱신.
        _agentLastMsg.LastPromptChanged += (workingDir, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var norm = System.IO.Path.GetFullPath(workingDir).TrimEnd('\\', '/');
                foreach (var p in _projects)
                {
                    var pNorm = System.IO.Path.GetFullPath(p.Path).TrimEnd('\\', '/');
                    if (!string.Equals(pNorm, norm, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var s in p.Tabs.OfType<SessionItem>())
                    {
                        if (!ApplyHeaderMessage(s, msg)) continue;
                        foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
                    }
                }
            });

        // opencode — 플러그인이 떨군 lastmsg 파일을 즉시 반영(claude 와 동일 패턴, 3초 폴링 대기 X).
        _opencodeLastMsg.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(roomId);
                if (s == null) return;
                if (!ApplyHeaderMessage(s, msg)) return;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });

        // gjc — 세션 .jsonl 폴링 결과를 roomId 키로 즉시 반영 (opencode lastmsg 와 동일 처리).
        _gajaeLastMsg.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(roomId);
                if (s == null) return;
                if (!ApplyHeaderMessage(s, msg)) return;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });

        // opencode — 플러그인이 떨군 busy 파일 감시 → 스피너 (claude 와 동일).
        _opencodeBusy.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(roomId);
                bool was = s?.IsBusy ?? false;
                if (s != null) s.IsBusy = busy;
                NotifyIfSessionFinished(s, was, busy);
                UpdateSessionBusyDisplay();
                if (!busy) foreach (var pane in _panes) pane.FlushPendingModelEffort(roomId);
            });

        // 가재코드 — 세션 .jsonl 폴링으로 busy 판정 → 스피너 (GajaeLastMessageService 가 lastmsg 와 함께 emit).
        _gajaeLastMsg.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(roomId);
                bool was = s?.IsBusy ?? false;
                if (s != null) s.IsBusy = busy;
                NotifyIfSessionFinished(s, was, busy);
                UpdateSessionBusyDisplay();
            });

        // codex — Claude 와 동일하게 roomId 키로 즉시 갱신 (폴링 X).
        _codexHook.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(roomId);
                if (s == null) return;
                s.LastMessage = msg;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });
        _codexHook.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(roomId);
                bool was = s?.IsBusy ?? false;
                if (s != null) s.IsBusy = busy;
                NotifyIfSessionFinished(s, was, busy);
                UpdateSessionBusyDisplay();
            });
        _codexHook.CodexSessionChanged += (roomId, sid) =>
            Dispatcher.InvokeAsync(() => SettingsService.SaveCodexRoomSession(roomId, sid));

        // 테마 변경 시 좌·우 패널 토글 아이콘 brush 재계산(seam 은 각 패널이 자체 처리)
        App.ThemeChanged += OnThemeChanged_UpdatePanels;

        // 파일 탐색기에서 텍스트 파일 더블클릭 → 포커스 패널의 새 파일 탭으로 열기
        FileExplorer.FileOpenRequested += (_, path) => _focusedPane.OpenFileAsTab(path);

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
            _modelEffort.Start();
            _sessionLastMsg.Start();
            // codex 훅 — 시작 시 스크립트/hooks.json 자동 설치. 사용자가 codex 첫 실행 시 trust 필요.
            CodexHookInstaller.EnsureScriptInstalled();
            CodexHookInstaller.InstallHooksJson();
            _codexHook.Start();
            // opencode 플러그인 — 매 시작 시 ~/.config\opencode\plugin\devezcode-room-tracker.js 갱신.
            // session.created/updated → sessions\<room>.txt (세션 ID 복원용)
            // message.updated( role=user ) → lastmsg\<room>.txt (헤더 타이틀 즉시 표시)
            OpenCodePluginInstaller.EnsureInstalled();
            _opencodeLastMsg.Start();
            _opencodeBusy.Start();
            // 가재코드 — 세션 .jsonl 폴링으로 헤더 lastmsg + 스피너 busy 둘 다 처리(확장/훅 불필요).
            _gajaeLastMsg.Start();
            _agentLastMsg.Start();
            RestoreOpenFiles();  // 직전에 열려 있던 파일 편집기 탭 복원(세션 활성화보다 먼저 → 활성 탭은 세션 유지)
            RestoreLastSession();
            RestoreSplitState(); // 직전 실행이 분할 상태였으면 패널 B 복원
            CheckHookSetup(); // 훅 미설치/구버전이면 상단 배너로 원클릭 설정 안내
            ApplyFileExpMinWidth(); // 탭 버튼 4개 온전히 보이는 폭을 패널 최소 폭으로
            ApplySidePanelButtonVisibility();
            // 전역 단축키: (기본)한자 + 좌/우 방향키 → 포커스 패널의 세션 탭 이전/다음 이동.
            // 우리 앱이 포그라운드가 아니어도(다른 앱/터미널 점유 중에도) 동작 — 전환 후 창을 앞으로.
            var (hkMod, hkPrev, hkNext) = SettingsService.LoadTabHotkey();
            GlobalTabHotkey.Configure(hkMod, hkPrev, hkNext);
            GlobalTabHotkey.Install(next =>
            {
                _focusedPane?.CycleActiveSession(next);
                BringToForegroundFromHotkey();
            });
        };

        // 창 위치/크기는 닫히기 직전(Closing)에 저장한다 — RestoreBounds 가 유효한 시점.
        Closing += OnWindowClosing;

        Closed += (_, _) =>
        {
            // 정상 종료: 마지막 활성 프로젝트/세션 기억 + 클린 종료 플래그 set
            SettingsService.SaveLastActive(_focusedPane.ActiveProject?.Path, _focusedPane.ActiveSession?.Id);
            SettingsService.SaveCleanShutdown(true);
            GlobalTabHotkey.Uninstall();
            App.ThemeChanged -= OnThemeChanged_UpdatePanels;
            foreach (var pane in _panes) pane.DisposeTerminal();
            _perfMonitor.Dispose();
            _statusLine.Dispose();
            _usageApi.Dispose();
            _codex.Dispose();
            _openCodeGo.Dispose();
            _sessionBusy.Dispose();
            _modelEffort.Dispose();
            _sessionLastMsg.Dispose();
            _codexHook.Dispose();
            _opencodeLastMsg.Dispose();
            _opencodeBusy.Dispose();
            _gajaeLastMsg.Dispose();
            _agentLastMsg.Dispose();
            FileExplorer.DisposeBrowser();
        };
    }

    /// <summary>마지막 실행의 창 위치/크기/최대화를 복원. 저장값이 화면 밖이면(모니터 분리·해상도 변경) 무시.</summary>
    private void RestoreWindowPlacement()
    {
        var (l, t, w, h, max) = SettingsService.LoadWindowPlacement();
        if (l is double ll && t is double tt && w is double ww && h is double hh && ww > 0 && hh > 0)
        {
            var saved = new Rect(ll, tt, ww, hh);
            var virt  = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                 SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var inter = Rect.Intersect(saved, virt);
            // 창이 화면과 충분히 겹칠 때만 복원(완전히 화면 밖이면 CenterScreen 으로 둔다).
            if (!inter.IsEmpty && inter.Width >= 100 && inter.Height >= 60)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = ll; Top = tt; Width = ww; Height = hh;
            }
        }
        if (max)
        {
            // 전체화면 복원: Maximized 로 만들면 Left/Top 이 무시돼 주 모니터로 최대화됨 →
            // EnterFullScreen 이 잘못된 모니터를 잡는다. 일반 bounds(Normal) 유지 후
            // OnSourceInitialized 에서 전체화면 진입 → MonitorFromWindow 가 올바른 모니터 감지.
            if (_useFullScreen) _restoreFullScreen = true;
            else WindowState = WindowState.Maximized;
        }
    }

    /// <summary>현재 창 위치/크기/최대화를 저장. 최대화·최소화 상태여도 RestoreBounds 로 일반 크기를 기록.</summary>
    private void SaveWindowPlacement()
    {
        bool max = WindowState == WindowState.Maximized || _inFullScreen;
        // 전체화면 중엔 현재 bounds 가 모니터 전체이므로, 진입 전 일반 bounds 를 저장.
        var b = _inFullScreen ? _preFsBounds : RestoreBounds;
        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0)
            b = new Rect(Left, Top, ActualWidth, ActualHeight);
        SettingsService.SaveWindowPlacement(b.Left, b.Top, b.Width, b.Height, max);
    }

    private bool _shuttingDown;

    /// <summary>창 종료 가로채기: 살아있는 세션이 있으면 닫기를 보류하고, 오버레이를 띄운 채
    /// 모든 세션을 graceful 종료(claude/codex transcript flush 기회)한 뒤 실제로 닫는다.</summary>
    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowPlacement();

        if (_shuttingDown) return; // 2차 진입(graceful 완료 후 Close()) — 그대로 종료 허용
        if (!TerminalSessionManager.Instance.HasLiveSessions()) return; // 닫을 세션 없음

        e.Cancel = true;
        _shuttingDown = true;
        HidePanePick();
        // WebView2(터미널/md 에디터/브라우저)는 HWND 라 WPF 오버레이를 가린다(airspace).
        // 종료 직전 화면을 스냅샷으로 캡처해 깔고 WebView 를 치운 뒤 "세션 닫는 중" 오버레이를 그 위에 띄운다.
        try { await SuspendTerminalWithSnapshotAsync(blankCurtain: false); }
        catch { /* best effort */ }
        // 스냅샷이 실제로 한 프레임 그려진 뒤 오버레이를 올린다 → WebView 가 사라진 직후 빈 배경이 비치는 깜빡임 제거.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        ShutdownOverlay.Visibility = Visibility.Visible;
        try { await TerminalSessionManager.Instance.GracefulShutdownAllAsync(1500); }
        catch { /* best effort */ }
        Close(); // _shuttingDown=true 라 재진입 시 즉시 종료
    }

    // ── 훅 연동 설정 배너 ─────────────────────────────────────────
    /// <summary>세션 상태(스피너)용 claude 훅이 미설치/구버전이면 상단 배너를 띄운다.</summary>
    private void CheckHookSetup()
    {
        if (!TerminalSessionManager.HookAssetsHealthy())
            HookSetupBanner.Visibility = Visibility.Visible;
    }

    private void HookSetupBtn_Click(object sender, RoutedEventArgs e)
    {
        TerminalSessionManager.EnsureHookAssets();
        HookSetupBanner.Visibility = Visibility.Collapsed;
        // 이미 떠 있는 세션은 다음 실행부터 적용된다(새 세션·재시작 시 자동 반영).
    }

    private void HookBannerDismiss_Click(object sender, RoutedEventArgs e)
        => HookSetupBanner.Visibility = Visibility.Collapsed;

    /// <summary>시작 시 마지막 세션/프로젝트를 자동으로 열지 않는다 — 사용자는 늘 프로젝트 미선택 상태로
    /// 시작하길 원함(세션을 보다 끄든 파일을 보다 끄든 동일). CleanShutdown 마커만 갱신한다.
    /// 직전에 열려 있던 파일 탭은 RestoreOpenFiles 가 '탭만' 복원하며 활성화(포커스)는 하지 않는다.</summary>
    private void RestoreLastSession()
    {
        // 옵션이 켜져 있으면 마지막으로 보던 프로젝트(가능하면 세션까지)를 자동으로 복원한다. 기본 false.
        if (SettingsService.LoadAutoLoadLastProject())
        {
            var (projPath, sessId) = SettingsService.LoadLastActive();
            var proj = projPath != null ? _projects.FirstOrDefault(p => p.Path == projPath) : null;
            if (proj != null)
            {
                var sess = sessId != null ? proj.Tabs.OfType<SessionItem>().FirstOrDefault(s => s.Id == sessId) : null;
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
    }

    // ── 계정 사용량 (statusLine 훅 + OAuth API 병합 → 푸터) ─────────────
    // 두 소스 스냅샷을 RateLimitSnapshot.Merge 로 합쳐(새 윈도우 채택·동일 윈도우 비후퇴) 푸터에 표시.
    private void StartStatusLine()
    {
        _statusLine.SnapshotUpdated += OnRlSnapshot;
        _usageApi.SnapshotUpdated  += OnRlSnapshot;
        _statusLine.Start();
        _usageApi.Start();

        // codex·opencode-go 사용량 폴링 → 푸터 패널(데이터 오면 CodexPanel/GoPanel 자동 표시).
        _codex.Updated      += u => Dispatcher.InvokeAsync(() => ApplyProviderUsage(u));
        _openCodeGo.Updated += u => Dispatcher.InvokeAsync(() => ApplyProviderUsage(u));
        _codex.Start();
        _openCodeGo.Start();

    }

    /// <summary>표시 가능한(데이터 있는) provider 만 사용량 카드로 변환. Claude → Codex → OpenCode Go 순.</summary>
    private IReadOnlyList<Models.UsageCardVM> BuildUsageCards()
    {
        var cards = new List<Models.UsageCardVM>();

        // Claude — RateLimitSnapshot(5시간/주간). 사이드바는 연결된 provider 를 항상 표시(토글 무관).
        if (_rlMerged is { HasData: true } rl)
        {
            var rows = new List<Models.UsageRowVM>();
            AddRow(rows, "5시간", rl.FiveHourPercent, rl.FiveHourResetsAt, isShortWindow: true);
            AddRow(rows, "주간", rl.SevenDayPercent, rl.SevenDayResetsAt, isShortWindow: false);
            cards.Add(new Models.UsageCardVM
            {
                Name = "Claude",
                IconPath = "pack://application:,,,/Resources/Images/ShellPresets/claude_code.png",
                Rows = rows,
            });
        }

        AddProviderCard(cards, _lastCodex, "Codex",
            "pack://application:,,,/Resources/Images/ShellPresets/codex.png");
        AddProviderCard(cards, _lastGo, "OpenCode Go",
            "pack://application:,,,/Resources/Images/ShellPresets/opencode_icon_white_50.png");

        return cards;
    }

    /// <summary>ProviderUsage(codex/go) → 카드. 데이터 없으면(미연결/오류) 건너뛴다.</summary>
    private void AddProviderCard(List<Models.UsageCardVM> cards, Models.ProviderUsage? u, string name, string iconPath)
    {
        if (u is not { HasData: true }) return;
        var rows = new List<Models.UsageRowVM>();
        AddRow(rows, "5시간", u.Primary?.UsedPercent, u.Primary?.ResetsAt, isShortWindow: true);
        AddRow(rows, "주간", u.Weekly?.UsedPercent, u.Weekly?.ResetsAt, isShortWindow: false);
        AddRow(rows, "월간", u.Monthly?.UsedPercent, u.Monthly?.ResetsAt, isShortWindow: false);
        cards.Add(new Models.UsageCardVM { Name = name, Plan = u.PlanLabel, IconPath = iconPath, Rows = rows });
    }

    /// <summary>사용률 값이 있을 때만 행을 추가. 단기 윈도우는 "남은 시간", 그 외는 "초기화 일시"로 안내.</summary>
    private void AddRow(List<Models.UsageRowVM> rows, string label, double? percent, DateTimeOffset? resetsAt, bool isShortWindow)
    {
        if (percent is not double p) return;
        var c = Math.Clamp(p, 0, 100);
        string reset = isShortWindow
            ? (resetsAt != null ? $"↻ {FormatRemaining(resetsAt)}" : "")
            : (resetsAt != null ? $"↻ {FormatResetDate(resetsAt)}" : "");
        rows.Add(new Models.UsageRowVM
        {
            Label = label,
            PercentText = $"{p:F0}%",
            ResetText = reset,
            BarWidth = UsageBarTrack * c / 100.0,
            BarBrush = RlBrush(c),
        });
    }

    private const double UsageBarTrack = 102; // 사용량 패널 막대 트랙 폭(XAML 과 일치)

    private void OnRlSnapshot(Models.RateLimitSnapshot snap)
        => Dispatcher.InvokeAsync(() =>
        {
            _rlMerged = Models.RateLimitSnapshot.Merge(_rlMerged, snap);
            if (_rlMerged != null) ApplyRateLimit(_rlMerged); // 하단 푸터
            RefreshUsagePanelIfVisible();                     // 우측 사이드바
        });

    /// <summary>사용량 사이드바가 열려 있으면 최신 스냅샷으로 카드를 다시 빌드 — 시작 시/폴링 시 자동 반영.</summary>
    private void RefreshUsagePanelIfVisible()
    {
        if (_usageOpen) SetSidebarUsageCards(BuildUsageCards());
    }

    /// <summary>최우측 사용량 사이드바 카드 채우기. 비면 안내 문구 표시.</summary>
    private void SetSidebarUsageCards(IReadOnlyList<Models.UsageCardVM> cards)
    {
        SidebarUsageList.ItemsSource = cards;
        SidebarUsageEmpty.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SidebarUsageUpdated.Text = cards.Count == 0 ? "" : $"{DateTime.Now:HH:mm} 기준";
    }

    /// <summary>codex/opencode-go 스냅샷 저장 후 하단 푸터 + 우측 사이드바 갱신.</summary>
    private void ApplyProviderUsage(Models.ProviderUsage u)
    {
        if (u.Provider == "codex")
        {
            _lastCodex = u;
            SetProviderPanel(CodexPanel, CxFiveLabel, CxFiveBar, CxFivePct, CxSevenBar, CxSevenPct, u, "Codex");
        }
        else if (u.Provider == "opencode-go")
        {
            _lastGo = u;
            SetProviderPanel(GoPanel, GoFiveLabel, GoFiveBar, GoFivePct, GoSevenBar, GoSevenPct, u, "OpenCode Go", GoMonthBar, GoMonthPct);
        }
        RefreshUsagePanelIfVisible();
    }

    /* ── 하단 푸터 계정 사용량 (우측 사이드바와 별개; 설정의 '하단 푸터 표시' 토글로 provider별 on/off) ── */

    private const double RlTrackWidth = 56;


    /// <summary>Claude rate limit 을 하단 푸터에 반영. 데이터 없거나 설정 off 면 숨김.</summary>
    private void ApplyRateLimit(Models.RateLimitSnapshot snap)
    {
        if (!snap.HasData || !SettingsService.LoadShowFooterClaude())
        { RateLimitPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); return; }
        RateLimitPanel.Visibility = Visibility.Visible;
        UpdateFooterDivider();
        SetBar(RlFiveLabel, RlFiveBar, RlFivePct,
               FormatRemainingShort(snap.FiveHourResetsAt) ?? "5시간", snap.FiveHourPercent);
        SetBar(RlSevenLabel, RlSevenBar, RlSevenPct, "주간", snap.SevenDayPercent);
        RateLimitPanel.ToolTip = BuildRlTooltip(snap);
    }

    /// <summary>codex/go 사용량을 해당 푸터 패널에 반영. 데이터 없거나 설정 off 면 숨김.</summary>
    private void SetProviderPanel(System.Windows.Controls.StackPanel panel,
        TextBlock fLabel, Border fBar, TextBlock fPct, Border wBar, TextBlock wPct,
        Models.ProviderUsage u, string name, Border? mBar = null, TextBlock? mPct = null)
    {
        bool show = u.Provider == "codex" ? SettingsService.LoadShowFooterCodex() : SettingsService.LoadShowFooterGo();
        if (!u.HasData || !show) { panel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); return; }
        panel.Visibility = Visibility.Visible;
        SetBar(fLabel, fBar, fPct, FormatRemainingShort(u.Primary?.ResetsAt) ?? "5h", u.Primary?.UsedPercent);
        SetWindowBar(wBar, wPct, u.Weekly?.UsedPercent);
        if (mBar != null && mPct != null) SetWindowBar(mBar, mPct, u.Monthly?.UsedPercent);
        panel.ToolTip = BuildProviderTooltip(u, name);
        UpdateFooterDivider();
    }

    /// <summary>한도 막대 1세트 갱신 — 라벨 / 채움 너비·색 / 퍼센트.</summary>
    private void SetBar(TextBlock label, Border bar, TextBlock pctText, string labelText, double? pct)
    {
        label.Text = labelText;
        if (pct is double v)
        {
            var c = Math.Clamp(v, 0, 100);
            bar.Width = RlTrackWidth * c / 100.0;
            bar.Background = RlBrush(c);
            pctText.Text = $"{v:F0}%";
        }
        else { bar.Width = 0; pctText.Text = "--"; }
    }

    /// <summary>고정 라벨 윈도우 막대(주간·월간) 갱신.</summary>
    private void SetWindowBar(Border bar, TextBlock pct, double? usedPercent)
    {
        if (usedPercent is double p) { var c = Math.Clamp(p, 0, 100); bar.Width = RlTrackWidth * c / 100.0; bar.Background = RlBrush(c); pct.Text = $"{p:F0}%"; }
        else { bar.Width = 0; pct.Text = "--"; }
    }

    /// <summary>provider 패널 사이 리딩 구분선(|) 동적 표시 — 앞 패널이 보일 때만.</summary>
    private void UpdateFooterDivider()
    {
        bool claude = RateLimitPanel.Visibility == Visibility.Visible;
        bool codex = CodexPanel.Visibility == Visibility.Visible;
        if (CxLeadDivider != null) CxLeadDivider.Visibility = claude ? Visibility.Visible : Visibility.Collapsed;
        if (GoLeadDivider != null) GoLeadDivider.Visibility = (claude || codex) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BuildRlTooltip(Models.RateLimitSnapshot snap)
    {
        var sb = new System.Text.StringBuilder();
        if (snap.FiveHourPercent is double f)
            sb.Append($"5시간 한도 {f:F0}%  ·  초기화까지 {FormatRemaining(snap.FiveHourResetsAt)}");
        if (snap.SevenDayPercent is double w)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append($"주간 한도 {w:F0}%  ·  초기화 {FormatResetDate(snap.SevenDayResetsAt)}");
        }
        return sb.ToString();
    }

    private static string BuildProviderTooltip(Models.ProviderUsage u, string name)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(name);
        if (u.PlanLabel != null) sb.Append("  ·  ").Append(u.PlanLabel);
        if (u.Primary?.UsedPercent is double p)
            sb.Append($"\n5시간 한도 {p:F0}%  ·  초기화까지 {FormatRemaining(u.Primary.ResetsAt)}");
        if (u.Weekly?.UsedPercent is double w)
            sb.Append($"\n주간 한도 {w:F0}%  ·  초기화 {FormatResetDate(u.Weekly.ResetsAt)}");
        if (u.Monthly?.UsedPercent is double m)
            sb.Append($"\n월간 한도 {m:F0}%  ·  초기화 {FormatResetDate(u.Monthly.ResetsAt)}");
        if (u.Error != null) sb.Append('\n').Append(u.Error);
        return sb.ToString();
    }

    /// <summary>남은 시간 짧은 표기 ("1시간24분" / "24분" / "곧"). 초기화 시각 없으면 null.</summary>
    private static string? FormatRemainingShort(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not DateTimeOffset r) return null;
        var span = r.ToLocalTime() - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "곧";
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}시간{span.Minutes}분" : $"{span.Minutes}분";
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
        if (win.Captured) _codex.RefreshNow();
    }

    /// <summary>Claude(claude.ai) OAuth 로그인 창을 띄우고 성공 시 사용량을 즉시 갱신.</summary>
    private void LoginClaude()
    {
        var win = new Views.ClaudeLoginWindow(this);
        win.ShowDialog();
        if (win.Captured) _usageApi.RefreshNow();
    }

    /// <summary>설정의 '하단 푸터 표시' 토글 변경 시 — 마지막 스냅샷으로 각 푸터 패널 가시성을 다시 평가.
    /// (우측 사이드바는 토글과 무관하게 연결된 provider 를 항상 표시하므로 별도 갱신만.)</summary>
    public void ApplyFooterUsageVisibility()
    {
        if (_rlMerged != null) ApplyRateLimit(_rlMerged);
        else { RateLimitPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); }
        if (_lastCodex != null) ApplyProviderUsage(_lastCodex); else { CodexPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); }
        if (_lastGo != null)    ApplyProviderUsage(_lastGo);    else { GoPanel.Visibility    = Visibility.Collapsed; UpdateFooterDivider(); }
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
    private double _sidebarMinWidth = 190;  // 프로젝트 1열=190, 2열=380. ApplyProjectColumns 가 갱신.
    private double _fileExpWidth = 300;
    private double _fileExpMinWidth;       // 탭 버튼 4개가 온전히 보이는 최소 폭(런타임 측정)
    private Action? _leftAnimCancel;
    private Action? _rightAnimCancel;

    // ── 반응형: 좁은 창에서 우측 패널을 오버레이 드로어로 ───────────────
    // 창 폭이 이 값 미만이면 우측 패널(탐색기/DIFF)을 레이아웃에서 빼고, 토글 시
    // 중앙 위로 떠오르는 오버레이로 띄운다(devez 즐겨찾기 패널 패턴). 이상이면 도킹.
    private const double NarrowThreshold = 1100;
    private bool? _narrow;                 // null=미초기화. 폭 변화로 모드 전환 감지
    private bool _rightOverlayOpen;        // 좁은 창에서 오버레이가 열려 있는지
    private readonly System.Windows.Media.TranslateTransform _rightT = new();
    private Action? _overlayAnimCancel;

    // GridSplitter 수동 드래그
    private void PaneSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => UpdatePaneFocusVisual(animate: false);
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

    // 패널 토글은 AnimatePanelAndSplitter 로 부드럽게 0/원래 폭을 보간한다(200ms, EaseIn).
    // 자식 컨트롤(Sidebar/FileExplorer)을 cacheTarget 으로 잡아 매 프레임 폭이 바뀌어도
    // 내부 트리·WebView2 가 다시 그려지는 깜빡임을 BitmapCache 로 차단한다 — HideEditorColumn 과 동일 패턴.
    /// <summary>좌/우/사용량 패널 토글로 중앙 `*` 컬럼이 리사이즈될 때, 보이는 워크스페이스 패널의
    /// 터미널 WebView2 를 스냅샷으로 정지해 매 프레임 reflow 깜빡임을 막는다(분할 애니메이션과 동일 처리).</summary>
    private Task FreezeWorkspaceTerminalsAsync()
        => Task.WhenAll(_panes.Where(p => p.Visibility == Visibility.Visible)
                              .Select(p => p.SuspendTerminalOnlyAsync(anchorTopLeft: true)));

    private void UnfreezeWorkspaceTerminals()
    {
        foreach (var p in _panes) p.ResumeTerminalOnly();
    }

    private async void LeftPanelBtn_Click(object sender, RoutedEventArgs e)
    {
        _leftAnimCancel?.Invoke();
        await FreezeWorkspaceTerminalsAsync();
        if (_leftCollapsed)
        {
            _leftCollapsed = false;
            Sidebar.Visibility = Visibility.Visible;
            SetMinWidth(_sidebarMinWidth, SidebarCol, FooterSidebarCol);
            _leftAnimCancel = AnimatePanelAndSplitter(
                SidebarCol, _sidebarWidth,
                SidebarSplitterCol, 4,
                durationMs: 200, easeIn: false,
                colMirrors: new[] { FooterSidebarCol },
                splitterMirrors: Array.Empty<ColumnDefinition>(),
                cacheTarget: Sidebar,
                onComplete: () => { _leftAnimCancel = null; UnfreezeWorkspaceTerminals(); });
        }
        else
        {
            _leftCollapsed = true;
            _sidebarWidth = SidebarCol.Width.IsAbsolute ? SidebarCol.Width.Value : SidebarCol.ActualWidth;
            SetMinWidth(0, SidebarCol, FooterSidebarCol);
            _leftAnimCancel = AnimatePanelAndSplitter(
                SidebarCol, 0,
                SidebarSplitterCol, 0,
                durationMs: 200, easeIn: true,
                colMirrors: new[] { FooterSidebarCol },
                splitterMirrors: Array.Empty<ColumnDefinition>(),
                cacheTarget: Sidebar,
                onComplete: () =>
                {
                    Sidebar.Visibility = Visibility.Collapsed;
                    _leftAnimCancel = null;
                    UnfreezeWorkspaceTerminals();
                });
        }
        SettingsService.SaveLeftPanel(_leftCollapsed, _sidebarWidth);
        UpdatePanelToggleVisual();
    }

    private async void RightPanelBtn_Click(object sender, RoutedEventArgs e)
    {
        // 좁은 창: 도킹 대신 오버레이 드로어를 토글한다.
        if (_narrow == true)
        {
            if (_rightOverlayOpen) CloseRightOverlay();
            else _ = OpenRightOverlay();
            return;
        }

        _rightAnimCancel?.Invoke();
        await FreezeWorkspaceTerminalsAsync();
        if (_rightCollapsed)
        {
            _rightCollapsed = false;
            FileExplorer.Visibility = Visibility.Visible;
            SetMinWidth(0, FileExpCol, FooterFileExpCol);
            _rightAnimCancel = AnimatePanelAndSplitter(
                FileExpCol, _fileExpWidth,
                FileExpSplitterCol, 4,
                durationMs: 200, easeIn: false,
                colMirrors: new[] { FooterFileExpCol },
                splitterMirrors: new[] { FooterFileExpSplitterCol },
                cacheTarget: FileExplorer,
                onComplete: () =>
                {
                    // 확장 완료 → 탭 버튼 폭을 최소 폭으로 복원(접힘 직전 0 으로 내렸던 것).
                    if (_fileExpMinWidth > 0) SetMinWidth(_fileExpMinWidth, FileExpCol, FooterFileExpCol);
                    _rightAnimCancel = null;
                    UnfreezeWorkspaceTerminals();
                    UpdateUsageSidebarBorder(); // 채널 복원 완료 후 사용량 보더 복구
                });
        }
        else
        {
            _rightCollapsed = true;
            _fileExpWidth = FileExpCol.Width.IsAbsolute ? FileExpCol.Width.Value : FileExpCol.ActualWidth;
            SetMinWidth(0, FileExpCol, FooterFileExpCol);
            _rightAnimCancel = AnimatePanelAndSplitter(
                FileExpCol, 0,
                FileExpSplitterCol, 0,
                durationMs: 200, easeIn: true,
                colMirrors: new[] { FooterFileExpCol },
                splitterMirrors: new[] { FooterFileExpSplitterCol },
                cacheTarget: FileExplorer,
                onComplete: () =>
                {
                    FileExplorer.Visibility = Visibility.Collapsed;
                    _rightAnimCancel = null;
                    UnfreezeWorkspaceTerminals();
                    UpdateUsageSidebarBorder(); // 패널이 완전히 닫힌 뒤에 사용량 보더 보정(미리 사라지지 않게)
                });
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
    private Action? _usageAnimCancel;
    private Action? _sessionHistoryAnimCancel;

    private void UsagePanelBtn_Click(object sender, RoutedEventArgs e)
        => SetUsagePanelOpen(!_usageOpen, persist: true, animate: true);

    private void SessionHistoryPanelBtn_Click(object sender, RoutedEventArgs e)
        => SetSessionHistoryPanelOpen(!_sessionHistoryOpen, persist: true, animate: true);

    /// <summary>F1 — 계정 사용량 사이드바 토글.</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F1)
        {
            SetUsagePanelOpen(!_usageOpen, persist: true, animate: true);
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.Escape && _projectTargetPickerWindow != null)
        {
            HidePanePick();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>최우측 사용량 사이드바를 펼치거나 접는다. animate=true 면 좌·우 패널과 같은 폭 트윈.</summary>
    private void SetUsagePanelOpen(bool open, bool persist, bool animate = false)
    {
        _usageOpen = open;
        if (open) SetSidebarUsageCards(BuildUsageCards()); // 펼칠 때 최신 스냅샷으로 카드 빌드

        _usageAnimCancel?.Invoke();
        _usageAnimCancel = null;
        if (animate)
        {
            _ = FreezeThenAnimateUsageAsync(open);
        }
        else
        {
            UsageCol.Width = new GridLength(open ? UsagePanelWidth : 0);
        }

        if (persist) SettingsService.SaveUsagePanelOpen(open);
        UpdatePanelToggleVisual();
        UpdateUsageSidebarBorder();
    }

    /// <summary>사용량 패널 트윈 전 중앙 터미널을 스냅샷 정지 → 완료 시 복원(reflow 깜빡임 방지).</summary>
    private async Task FreezeThenAnimateUsageAsync(bool open)
    {
        await FreezeWorkspaceTerminalsAsync();
        _usageAnimCancel = AnimateColumn(
            UsageCol, open ? UsagePanelWidth : 0,
            durationMs: 200, easeIn: !open,
            cacheTarget: UsageSidebar,
            onComplete: () => { _usageAnimCancel = null; UnfreezeWorkspaceTerminals(); });
    }

    /// <summary>세션 완료 기록 사이드바를 펼치거나 접는다. 계정 사용량 패널과 같은 우측 보조 패널 패턴.</summary>
    private void SetSessionHistoryPanelOpen(bool open, bool persist, bool animate = false)
    {
        _sessionHistoryOpen = open;
        double targetWidth = open ? SettingsService.LoadSessionHistoryWidth() : 0;

        _sessionHistoryAnimCancel?.Invoke();
        _sessionHistoryAnimCancel = null;
        if (animate)
        {
            _ = FreezeThenAnimateSessionHistoryAsync(open, targetWidth);
        }
        else
        {
            SessionHistoryCol.Width = new GridLength(targetWidth);
        }

        if (persist) SettingsService.SaveSessionHistoryPanelOpen(open);
        UpdatePanelToggleVisual();
        UpdateUsageSidebarBorder();
    }

    private async Task FreezeThenAnimateSessionHistoryAsync(bool open, double targetWidth)
    {
        await FreezeWorkspaceTerminalsAsync();
        _sessionHistoryAnimCancel = AnimateColumn(
            SessionHistoryCol, targetWidth,
            durationMs: 200, easeIn: !open,
            cacheTarget: SessionHistorySidebar,
            onComplete: () => { _sessionHistoryAnimCancel = null; UnfreezeWorkspaceTerminals(); });
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
        _sidebarMinWidth = cols == 2 ? 380 : 190;
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
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            SetForegroundWindow(h);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Activate();
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>탭 버튼 4개가 온전히 보이는 폭을 측정해 우측 패널(확장 상태)의 최소 폭으로 적용.
    /// 접힘/오버레이(좁은 창) 상태에서는 적용하지 않는다(접기 애니메이션은 MinWidth 0 필요).</summary>
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

    // 프레임 동기(CompositionTarget.Rendering) 컬럼 폭 애니메이션. DispatcherTimer 는
    // 프레임 클럭과 어긋나 끊김이 생겨, devez 처럼 렌더 펄스에 맞춰 갱신한다.
    // mirrors: col 과 SharedSizeGroup 으로 폭을 공유하는 헤더/푸터 컬럼들. 공유 그룹은
    // 멤버 중 최대 폭을 채택하므로, col 만 0 으로 줄여도 헤더/푸터의 고정 폭(262/300)이
    // 남아 영역이 안 줄어든다. 같은 폭을 미러 컬럼에도 매 프레임 써 줘야 실제로 접힌다.
    // clampCol: 폭을 col 과 함께 움직이되 clampMin 아래로는 안 내려가는 컬럼(헤더 메타용).
    private static Action AnimateColumn(ColumnDefinition col, double toWidth, int durationMs,
        bool easeIn, Action? onComplete = null, UIElement? cacheTarget = null,
        ColumnDefinition[]? mirrors = null, ColumnDefinition? clampCol = null, double clampMin = 0)
    {
        var from = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool cancelled = false;

        if (cacheTarget != null)
            cacheTarget.CacheMode = new System.Windows.Media.BitmapCache();

        void SetWidth(GridLength gl)
        {
            col.Width = gl;
            if (mirrors != null) foreach (var m in mirrors) m.Width = gl;
            if (clampCol != null) clampCol.Width = new GridLength(Math.Max(gl.Value, clampMin));
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (cancelled)
            {
                System.Windows.Media.CompositionTarget.Rendering -= handler!;
                return;
            }
            var t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)durationMs);
            var easedT = easeIn ? EaseIn(t) : EaseInOut(t);
            SetWidth(new GridLength(from + (toWidth - from) * easedT));
            if (t >= 1.0)
            {
                System.Windows.Media.CompositionTarget.Rendering -= handler!;
                SetWidth(new GridLength(toWidth));
                if (cacheTarget != null) cacheTarget.CacheMode = null;
                onComplete?.Invoke();
            }
        };
        System.Windows.Media.CompositionTarget.Rendering += handler;

        return () =>
        {
            if (cancelled) return;
            cancelled = true;
            if (cacheTarget != null) cacheTarget.CacheMode = null;
            System.Windows.Media.CompositionTarget.Rendering -= handler;
        };
    }

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private static double EaseIn(double t) => t * t * t;

    // 좌·우 패널 토글 전용: 패널 + 미러(헤더/푸터) + 스플리터 + 스플리터 미러를 한 트윈으로 묶어
    // 완벽히 동기화된 애니메이션을 준다. HideEditorColumn 처럼 cacheTarget 으로 자식 컨트롤을
    // BitmapCache 로 잡아 WebView2/탐색기가 폭 변화에 따라 글자나 트리를 다시 그리는 깜빡임을
    // 차단한다. duration·easeIn 은 토글 방향에 따라 호출부에서 결정.
    private static Action AnimatePanelAndSplitter(
        ColumnDefinition col, double toCol,
        ColumnDefinition splitter, double toSplitter,
        int durationMs, bool easeIn,
        ColumnDefinition[] colMirrors, ColumnDefinition[] splitterMirrors,
        UIElement? cacheTarget, Action? onComplete = null)
    {
        double fromCol = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;
        double fromSpl = splitter.Width.IsAbsolute ? splitter.Width.Value : splitter.ActualWidth;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool cancelled = false;
        if (cacheTarget != null) cacheTarget.CacheMode = new System.Windows.Media.BitmapCache();

        void SetAll(double cw, double spw)
        {
            var cgl = new GridLength(cw);
            var sgl = new GridLength(spw);
            col.Width = cgl;
            foreach (var m in colMirrors) m.Width = cgl;
            splitter.Width = sgl;
            foreach (var m in splitterMirrors) m.Width = sgl;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (cancelled)
            {
                System.Windows.Media.CompositionTarget.Rendering -= handler!;
                return;
            }
            var t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)durationMs);
            var easedT = easeIn ? EaseIn(t) : EaseInOut(t);
            SetAll(fromCol + (toCol - fromCol) * easedT,
                   fromSpl + (toSplitter - fromSpl) * easedT);
            if (t >= 1.0)
            {
                System.Windows.Media.CompositionTarget.Rendering -= handler!;
                SetAll(toCol, toSplitter);
                if (cacheTarget != null) cacheTarget.CacheMode = null;
                onComplete?.Invoke();
            }
        };
        System.Windows.Media.CompositionTarget.Rendering += handler;

        return () =>
        {
            if (cancelled) return;
            cancelled = true;
            if (cacheTarget != null) cacheTarget.CacheMode = null;
            System.Windows.Media.CompositionTarget.Rendering -= handler;
        };
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

    private async Task CheckUpdateAsync()
    {
        if (_updateInProgress) return;
        _lastUpdateCheckUtc = DateTime.UtcNow;

        var info = await UpdateService.CheckAsync();
        if (info is null || _updateInProgress) return;
        _pendingUpdate = info;

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

        var noteLines = string.IsNullOrWhiteSpace(info.Notes)
            ? ""
            : "\n\n" + string.Join("\n",
                info.Notes.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => "· " + l.Trim().TrimStart('•', ' ', '\t').Trim())
                          .Where(l => l.Length > 2));

        var prefix = info.IsUrgent ? "[긴급] " : "";
        if (!ConfirmDialog.Show(
                $"{prefix}새 버전 {info.Version}",
                $"새 버전이 있습니다. 지금 업데이트할까요?{noteLines}",
                okLabel: "업데이트",
                iconKey: "IconDownload"))
            return;

        _ = ApplyUpdateAsync(info);
    }

    private async Task ApplyUpdateAsync(UpdateInfo info)
    {
        _updateInProgress = true;
        Sidebar.HideUpdateButton(); // 설치 진행 중에는 버튼 숨김
        if (!_usageOpen) SetUsagePanelOpen(true, persist: false); // 진행률은 사용량 사이드바에 표시 — 보이도록 펼침
        UsageUpdateProgress.Visibility = Visibility.Visible;
        var progress = new Progress<double>(v =>
        {
            UsageUpdateProgress.Visibility = Visibility.Visible;
            UsageUpdateText.Text = $"업데이트 다운로드 중… {v:P0}";
            UsageUpdateBar.Width = UsageUpdateTrack.ActualWidth * Math.Clamp(v, 0, 1);
        });
        try
        {
            await UpdateService.DownloadAndRelaunchAsync(info, progress);
            // 성공 시 앱이 종료/재실행되므로 이 아래로는 도달하지 않는다.
        }
        catch
        {
            _updateInProgress = false;
            UsageUpdateProgress.Visibility = Visibility.Collapsed; // 진행률 제거
            if (_pendingUpdate != null) Sidebar.ShowUpdateButton(_pendingUpdate.Version); // 실패 → 버튼 복원
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
        if (_projects.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            ConfirmDialog.Alert("알림", "이미 추가된 프로젝트입니다.");
            return;
        }
        var proj = ProjectItem.FromPath(path);
        // 프로젝트 연결 시 기본 세션 1개 자동 생성. 사용 가능한 에이전트가 1개면 그걸로, 아니면(2개+) 피커 표시.
        var available = AgentRegistry.GetEnabledAndInstalled();
        if (available.Count == 0)
        {
            ConfirmDialog.Alert("에이전트 없음",
                "사용 가능한 에이전트가 없습니다.\n설정 → 에이전트 에서 하나 이상 활성화해 주세요.");
            return;
        }
        string defaultAgentId = available.Count == 1
            ? available[0].Id
            : (AgentPickerDialog.Pick(this, available, proj.Path) ?? AgentRegistry.DefaultAgentId);
        var session = new SessionItem { Name = "세션 1", AgentId = defaultAgentId };
        proj.Tabs.Add(session);
        SettingsService.SaveClaudeCodeRoomDir(session.Id, proj.Path);
        SettingsService.SaveAgentForRoom(session.Id, defaultAgentId);
        _projects.Add(proj);
        WorkspaceStore.Save(_projects);
        SelectProject(proj);   // 탭을 이 프로젝트 세션으로 교체 + 기본 세션 활성화
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
        pane.FocusRequested += OnPaneFocusRequested;
        pane.ActiveChanged += OnPaneActiveChanged;
        pane.SplitToggleRequested += OnPaneSplitToggle;
        _panes.Add(pane);
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

        if (ReferenceEquals(pane, _focusedPane)) SyncShellToFocusedPane();
        // 패널 B 의 활성이 바뀌면 분할 복원용 상태를 갱신(패널 A 는 SyncShell 의 last-active 가 담당).
        if (ReferenceEquals(pane, RightPane)) PersistSplitState();
        UpdatePaneRoles();
    }

    /// <summary>프로젝트 메뉴 "왼쪽/오른쪽 패널에 표시". 분할 시에만 동작.
    /// 대상이 반대 패널에 떠 있으면 좌우를 맞바꾸고(swap), 아니면 해당 패널에 띄운다.</summary>
    private void ShowProjectInPane(ProjectItem p, bool left)
    {
        if (!_splitActive) return;
        var target = left ? LeftPane : RightPane;
        var other  = left ? RightPane : LeftPane;
        if (ReferenceEquals(target.ActiveProject, p)) return;   // 이미 그 패널 — 변화 없음(메뉴도 비활성)

        if (ReferenceEquals(other.ActiveProject, p))
        {
            // 반대 패널에 이미 떠 있는 프로젝트 → 좌/우 위치만 교환(콘텐츠 이동 X = 세션 재로딩 없음).
            // 목표 패널이 비어 있었으면 빈 패널이 반대로 넘어가 '이동', 차 있었으면 '맞바꿈'이 된다.
            SwapPanePositions();
            _focusedPane = other;   // p 가 들어 있던 패널 — 스왑 후 목표 슬롯으로 이동한다.
            SyncShellToFocusedPane();
            UpdatePaneRoles();
            PersistSplitState();
            // 컬럼 재배치 후 레이아웃이 반영된 뒤 포커스 라인을 갱신.
            Dispatcher.BeginInvoke(() => UpdatePaneFocusVisual(), System.Windows.Threading.DispatcherPriority.Render);
            return;
        }

        // 어느 패널에도 없던 프로젝트 → 목표 패널에 새로 연다(이건 실제 로딩이라 불가피).
        target.SelectProject(p);
        _focusedPane = target;
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
        UpdatePaneRoles();
        PersistSplitState();
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
        => SettingsService.SaveSplitState(_splitActive, RightPane.ActiveProject?.Path, RightPane.ActiveSession?.Id);

    /// <summary>포커스 패널의 활성 프로젝트/세션을 셸(파일탐색기·사이드바·last-active)에 반영.</summary>
    private void SyncShellToFocusedPane()
    {
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
        if (_focusedPane.ActiveSession != null && HookSetupBanner.Visibility == Visibility.Visible)
            HookSetupBanner.Visibility = Visibility.Collapsed;
    }

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

    /// <summary>중앙 패널 분할/해제 토글. 분할 시 패널 B 노출 후 두 번째 프로젝트를 자동으로 연다.</summary>
    private void OnPaneSplitToggle(WorkspacePaneView pane)
    {
        if (_splitActive) DisableSplit();
        else EnableSplit();
    }

    // 상단 타이틀바 분할 토글 버튼(좌측 패널 버튼 오른쪽).
    private void SplitToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_splitActive) DisableSplit();
        else EnableSplit();
    }

    /// <summary>분할 토글 버튼: 분할 중이면 테마색 강조 + '분할 닫기' 툴팁, 아니면 기본.</summary>
    private void UpdateSplitToggleVisual()
    {
        SplitToggleIcon.Data = (System.Windows.Media.Geometry)FindResource(_splitActive ? "IconPanelLeftClose" : "IconPanelLeftOpen");
        // 구체 브러시를 박으면 DynamicResource 추적이 끊겨 테마 변경을 못 따라간다 → SetResourceReference 로 동적 연결 유지.
        SplitToggleIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, _splitActive ? "PrimaryBrush" : "TextMutedBrush");
        SplitToggleBtn.ToolTip = _splitActive ? "분할 닫기" : "패널 분할";
    }


    private void EnableSplit(ProjectItem? bProject = null, SessionItem? bSession = null, bool animate = true)
    {
        if (_splitActive) return;
        _splitActive = true;

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

        foreach (var p in _panes) p.SetSplitActive(true);

        // 패널 B 포커스로 전환 → 이후 사이드바 클릭이 B 로 향한다.
        _focusedPane = PaneB;
        if (bSession != null) PaneB.OpenSession(bSession);
        else if (bProject != null) PaneB.SelectProject(bProject);
        else SyncShellToFocusedPane();   // 사용자 토글 시 B 는 빈 패널 — 직접 프로젝트를 고르게 한다.
        UpdatePaneRoles();
        Sidebar.IsSplitActive = true;
        UpdateSplitToggleVisual();
        PersistSplitState();

        if (animate)
            _ = AnimateSplitOpenAsync();
        else
        {
            PaneBCol.Width = new GridLength(1, GridUnitType.Star);
            PaneB.SetEmptyTextWrapping(true);
            UpdatePaneFocusVisual();
        }
    }

    /// <summary>분할 펼침: 두 패널 터미널을 스냅샷으로 정지(WebView2 매 프레임 리사이즈 깜빡임 방지)한 뒤
    /// PaneB 를 0%→50% 로 펼치고, 완료 시 라이브 터미널로 복원한다.</summary>
    private async Task AnimateSplitOpenAsync()
    {
        await Task.WhenAll(PaneA.SuspendTerminalOnlyAsync(anchorTopLeft: true), PaneB.SuspendTerminalOnlyAsync(anchorTopLeft: true));
        PaneB.SetEmptyTextWrapping(false); // 펼침 애니메이션 중 줄바꿈 방지
        AnimatePaneSplit(0, 1, () =>
        {
            PaneBCol.Width = new GridLength(1, GridUnitType.Star);
            PaneB.SetEmptyTextWrapping(true); // 완전히 펼쳐진 후에만 줄바꿈
            PaneA.ResumeTerminalOnly();
            PaneB.ResumeTerminalOnly();
            UpdatePaneFocusVisual(animate: false);
        });
    }

    /// <summary>시작 시 저장된 분할 상태 복원 — 패널 B 프로젝트/세션을 열고 포커스는 A 로 되돌린다.</summary>
    private void RestoreSplitState()
    {
        // 분할(좌우 분리) 레이아웃은 옵션과 무관하게 항상 복원한다.
        var (active, bProjPath, bSessId) = SettingsService.LoadSplitState();
        if (!active) return;

        // 단, 패널 B 의 프로젝트/세션 복원은 "프로젝트 자동 로드" 옵션이 켜진 경우에만.
        // 옵션이 꺼져 있으면 빈 분할 패널로 시작한다(미선택 상태).
        ProjectItem? bProj = null;
        SessionItem? bSess = null;
        if (SettingsService.LoadAutoLoadLastProject())
        {
            bProj = _projects.FirstOrDefault(p => p.Path == bProjPath);
            bSess = bProj?.Tabs.OfType<SessionItem>().FirstOrDefault(s => s.Id == bSessId)
                    ?? _projects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == bSessId);
        }
        EnableSplit(bProj, bSess, animate: false);   // 시작 시 복원은 애니메이션 없이 즉시.

        _focusedPane = PaneA;
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
    }

    private void DisableSplit()
    {
        if (!_splitActive) return;
        _splitActive = false;

        // 좌측(주) 패널 콘텐츠를 유지, 우측은 버린다. 스왑 상태에서는 유지 콘텐츠가 PaneB 에 있을 수 있다.
        var keep = LeftPane;
        var drop = RightPane;
        bool swapped = !ReferenceEquals(keep, PaneA);
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

        _focusedPane = PaneA;
        foreach (var p in _panes) p.SetSplitActive(false);
        SyncShellToFocusedPane();
        UpdatePaneRoles();
        Sidebar.IsSplitActive = false;
        UpdateSplitToggleVisual();

        _ = AnimateSplitCloseAsync(swapped);
    }

    /// <summary>분할 접힘: 두 패널 터미널을 스냅샷으로 정지한 뒤 PaneB 를 50%→0% 로 접고,
    /// 완료 시 PaneB 숨김·세션 배선 해제·컬럼 정규화 후 PaneA 를 라이브로 복원한다.</summary>
    private async Task AnimateSplitCloseAsync(bool swapped)
    {
        await Task.WhenAll(PaneA.SuspendTerminalOnlyAsync(anchorTopLeft: true), PaneB.SuspendTerminalOnlyAsync(anchorTopLeft: true));
        PaneB.SetEmptyTextWrapping(false); // 접힘 애니메이션 중 줄바꿈 방지
        AnimatePaneSplit(1, 0, () =>
        {
            if (!swapped) PaneB.ClearForHide();   // 비스왑: 축소 완료 후 세션/터미널 배선 해제(컬렉션·ConPTY·기록은 보존).
            PaneB.Visibility = Visibility.Collapsed;
            PaneSplitter.Visibility = Visibility.Collapsed;
            PaneBCol.Width = new GridLength(0);
            PaneSplitterCol.Width = new GridLength(0);
            PaneACol.Width = new GridLength(1, GridUnitType.Star);
            PaneB.ResumeTerminalOnly();   // 숨겨질 PaneB 의 스냅샷 오버레이 정리(다음 분할 때 라이브 위에 안 남도록).
            PaneA.ResumeTerminalOnly();
            UpdatePaneFocusVisual(animate: false);
            PersistSplitState();
        });
    }

    /// <summary>분할 중일 때 포커스된 패널을 4면 테마색 보더(각 패널의 FocusFrame)로 표시한다.
    /// 단일 패널이면 모두 끈다. 보더는 패널 레이아웃을 따르므로 별도 위치 계산이 필요 없다.</summary>
    private void UpdatePaneFocusVisual(bool animate = true)
    {
        foreach (var p in _panes)
            p.SetFocusedVisual(false);

        if (!_splitActive) return;

        _focusedPane.SetFocusedVisual(true);
    }

    private SessionItem? FindSession(string id)
        => _projects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == id);

    private void AddSessionCompletionRecord(SessionItem s)
    {
        var proj = _projects.FirstOrDefault(p => p.Tabs.Contains(s));
        var projName = proj != null
            ? System.IO.Path.GetFileName(proj.Path.TrimEnd('\\', '/'))
            : "";
        var sessName = string.IsNullOrWhiteSpace(s.Name) ? "세션" : s.Name;

        _sessionDoneRecords.Insert(0, new SessionCompletionRecord
        {
            SessionId = s.Id,
            SessionName = sessName,
            ProjectName = projName,
            AgentId = s.AgentId,
            LastMessage = s.LastMessage?.Trim() ?? "",
            CompletedAt = DateTime.Now,
        });

        while (_sessionDoneRecords.Count > MaxSessionDoneRecords)
            _sessionDoneRecords.RemoveAt(_sessionDoneRecords.Count - 1);
        SettingsService.SaveSessionHistoryRecords(
            new List<SessionCompletionRecord>(_sessionDoneRecords), MaxSessionDoneRecords);
        UpdateSessionHistoryEmpty();
    }


    private void UpdateSessionBusyDisplay()
    {
        int count = 0;
        foreach (var p in _projects)
            foreach (var t in p.Tabs)
                if (t is SessionItem { IsBusy: true }) count++;
        if (count > 0)
        {
            SessionBusySpinner.Visibility = Visibility.Visible;
            SessionBusyLabel.Text = $"진행중인 세션 {count}개";
        }
        else
        {
            SessionBusySpinner.Visibility = Visibility.Collapsed;
            SessionBusyLabel.Text = "진행중인 세션 없음.";
        }
    }

    private void SessionHistoryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        SessionHistoryFadeTop.Visibility = sv.VerticalOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionHistoryFadeBottom.Visibility = sv.VerticalOffset < sv.ScrollableHeight ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSessionHistoryEmpty()
        => SessionHistoryEmpty.Visibility = _sessionDoneRecords.Count == 0 ? Visibility.Visible : Visibility.Collapsed;



    private void SessionHistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionCompletionRecord r) return;
        var s = FindSession(r.SessionId);
        if (s == null) return;
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
    private void NotifyIfSessionFinished(SessionItem? s, bool wasBusy, bool nowBusy)
    {
        if (s == null || !wasBusy || nowBusy) return;
        AddSessionCompletionRecord(s);
        if (!SettingsService.LoadNotifySessionDoneEnabled()) return;

        var proj = _projects.FirstOrDefault(p => p.Tabs.Contains(s));
        var projName = proj != null
            ? System.IO.Path.GetFileName(proj.Path.TrimEnd('\\', '/'))
            : "";
        var sessName = string.IsNullOrWhiteSpace(s.Name) ? "세션" : s.Name;
        // 제목(큰 글씨)=프로젝트명, 본문(작은 글씨)=세션명 · 상태. 프로젝트명 없으면 세션명을 제목으로.
        var title = string.IsNullOrEmpty(projName) ? sessName : projName;
        var body  = string.IsNullOrEmpty(projName) ? "응답 완료" : $"{sessName} · 응답 완료";

        App.ShowNotification(title, body, () =>
        {
            try { Activate(); OpenSession(s); } catch { /* best effort */ }
        });
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
            return false; // 다른 슬래시 명령은 헤더 변경 X
        }
        if (s.LastMessage == m) return false;
        s.LastMessage = m;
        return true;
    }

    // ── 사이드바 액션 → 포커스 패널로 위임 ────────────────────────────

    private void SelectProject(ProjectItem proj) => SelectProjectFromSidebar(proj);

    private void SelectProjectFromSidebar(ProjectItem proj)
    {
        var existingPane = _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveProject, proj));
        if (existingPane != null)
        {
            FocusPaneOnly(existingPane);
            return;
        }

        if (_splitActive)
        {
            if (PaneA.ActiveProject == null)
            {
                SelectProjectIntoPane(PaneA, proj);
                return;
            }

            if (PaneB.ActiveProject == null)
            {
                SelectProjectIntoPane(PaneB, proj);
                return;
            }

            ShowProjectTargetPicker(pane => SelectProjectIntoPane(pane, proj));
            return;
        }

        SelectProjectIntoPane(_focusedPane, proj);
    }

    /// <summary>사이드바에서 세션 클릭 → 프로젝트 선택과 동일한 패널 타게팅 규칙을 따른다.
    /// (이미 열린 패널 포커스 → 빈 패널 채움 → 둘 다 차 있으면 피커). 단 프로젝트 대신 세션을 활성화한다.</summary>
    private void OpenSession(SessionItem session)
    {
        MarkSessionRead(session.Id);
        var parent = _projects.Concat(_archivedProjects).FirstOrDefault(p => p.Tabs.Contains(session));
        if (parent == null) { OpenSessionIntoPane(_focusedPane, session); return; }

        var existingPane = _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveProject, parent));
        if (existingPane != null)
        {
            OpenSessionIntoPane(existingPane, session);
            return;
        }

        if (_splitActive)
        {
            if (PaneA.ActiveProject == null) { OpenSessionIntoPane(PaneA, session); return; }
            if (PaneB.ActiveProject == null) { OpenSessionIntoPane(PaneB, session); return; }
            ShowProjectTargetPicker(pane => OpenSessionIntoPane(pane, session));
            return;
        }

        OpenSessionIntoPane(_focusedPane, session);
    }

    private void OpenSessionIntoPane(WorkspacePaneView pane, SessionItem session)
    {
        _focusedPane = pane;
        pane.OpenSession(session);
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
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
        pane.SelectProject(proj);
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
    }

    private void FocusPaneOnly(WorkspacePaneView pane)
    {
        _focusedPane = pane;
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
    }

    private ProjectTargetPickerWindow? _projectTargetPickerWindow;

    // 별도 최상위 창으로 띄운다(터미널 WebView2 HWND 위에 합성). 중앙 패널(CenterSplit) 영역에
    // 정확히 겹치도록 위치·크기를 잡고, 좌/우 컬럼 폭은 실제 PaneA/Gap/PaneB 폭으로 채운다.
    private void ShowProjectTargetPicker(Action<WorkspacePaneView> onSelect)
    {
        _projectTargetPickerWindow?.Close();

        var topLeft = CenterSplit.PointToScreen(new System.Windows.Point(0, 0));
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                ?? System.Windows.Media.Matrix.Identity;
        var dip = m.Transform(topLeft); // 화면 픽셀 → DIP

        var win = new ProjectTargetPickerWindow { Owner = this };
        // CenterSplit 의 실제 컬럼 GridLength 를 그대로 복사 → 동일 폭에서 좌/우가 패널과 정확히 일치.
        win.Configure(LeftPane.ActiveProject?.Name ?? "빈 패널", RightPane.ActiveProject?.Name ?? "빈 패널",
                      LeftPane.ActiveProject?.Path ?? "", RightPane.ActiveProject?.Path ?? "",
                      PaneACol.Width, PaneSplitterCol.Width, PaneBCol.Width);
        win.Left = dip.X;
        win.Top = dip.Y;
        win.Width = CenterSplit.ActualWidth;
        win.Height = CenterSplit.ActualHeight;
        win.PaneSelected += pane => onSelect(pane == "A" ? LeftPane : RightPane);
        win.Closed += PanePicker_Closed;
        _projectTargetPickerWindow = win;
        // picker 는 포커스를 안 가져가므로 취소(바깥 클릭·Esc)는 메인 창에서 감지한다.
        PreviewMouseDown += PanePicker_OutsideMouseDown;
        win.Show();
    }

    // 메인 창 영역(사이드바 등) 클릭 = picker 바깥 클릭 → 취소. picker 반쪽 클릭은 별도 창이라 여기로 안 온다.
    private void PanePicker_OutsideMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => HidePanePick();

    private void PanePicker_Closed(object? sender, EventArgs e)
    {
        PreviewMouseDown -= PanePicker_OutsideMouseDown;
        if (ReferenceEquals(_projectTargetPickerWindow, sender)) _projectTargetPickerWindow = null;
    }

    private void HidePanePick() => _projectTargetPickerWindow?.Close();

    private void AddSession(ProjectItem proj) => _focusedPane.AddSession(proj);
    private void RenameSession(SessionItem session) => PaneFor(session).RenameSession(session);
    private void DeleteSession(SessionItem session) => PaneFor(session).DeleteSession(session);
    private void StopTrackingSession(SessionItem session) => PaneFor(session).StopTrackingSession(session);

    // ── 공개 API (외부 뷰가 호출) ─────────────────────────────────────
    /// <summary>작업 큐 → 포커스 패널의 활성 세션에 텍스트 전송.</summary>
    public bool SendTextToActiveSession(string text) => _focusedPane.SendTextToActiveSession(text);

    /// <summary>MCP 저장 후 활성 Claude 세션 재시작.</summary>
    public bool TryRestartActiveClaudeSession() => _focusedPane.TryRestartActiveClaudeSession();

    /// <summary>테마 변경 — 모든 패널 세션 재로드.</summary>
    public void ReloadAllSessionsForTheme()
    {
        foreach (var pane in _panes) pane.ReloadAllSessionsForTheme();
    }

    // ── 프로젝트 ──────────────────────────────────────────────────
    private void DeleteProject(ProjectItem proj)
    {
        bool fromArchive = _archivedProjects.Contains(proj);
        if (!ConfirmDialog.Show("프로젝트 제거",
                $"'{proj.Name}' 프로젝트를 목록에서 제거할까요?\n(디스크의 실제 파일은 삭제되지 않습니다.)",
                okLabel: "제거", danger: true))
            return;

        foreach (var s in proj.Tabs.OfType<SessionItem>().ToList())
            foreach (var pane in _panes) pane.DisposeSessionProcess(s, purge: false);

        if (fromArchive) _archivedProjects.Remove(proj);
        else _projects.Remove(proj);
        SettingsService.RemoveBrowserLastUrl(proj.Path);
        WorkspaceStore.Save(_projects, _archivedProjects);

        // 보관 항목 제거는 중앙 패널과 무관(이미 패널에 없음).
        if (!fromArchive)
        {
            var next = _projects.FirstOrDefault();
            foreach (var pane in _panes) pane.OnProjectRemoved(proj, next);
        }
        UpdateStatus();
    }

    /// <summary>프로젝트 이름 변경 — 표시 이름만 바꾸고 경로/세션은 그대로. 활성·보관 양쪽 모두 영속 저장.</summary>
    private void RenameProject(ProjectItem proj)
    {
        var name = PromptDialog.Show("프로젝트 이름 변경", "새 이름을 입력하세요.",
                                     defaultValue: proj.Name, maxLength: 60);
        if (string.IsNullOrWhiteSpace(name) || name == proj.Name) return;
        proj.Name = name;
        WorkspaceStore.Save(_projects, _archivedProjects);
    }

    /// <summary>프로젝트 보관 — 활성 목록에서 빼 보관함으로. 세션 프로세스는 정지하되 기록은 보존(devez 정합).</summary>
    private void ArchiveProject(ProjectItem proj)
    {
        if (!_projects.Contains(proj)) return;

        foreach (var s in proj.Tabs.OfType<SessionItem>().ToList())
            foreach (var pane in _panes) pane.DisposeSessionProcess(s, purge: false);

        proj.ArchivedAt = DateTime.UtcNow.ToString("o");
        _projects.Remove(proj);
        if (!_archivedProjects.Contains(proj)) _archivedProjects.Add(proj);
        WorkspaceStore.Save(_projects, _archivedProjects);

        var next = _projects.FirstOrDefault();
        foreach (var pane in _panes) pane.OnProjectRemoved(proj, next);
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
    private void OnSidebarSessionsReordered(ProjectItem _) => WorkspaceStore.Save(_projects);

    // 푸터 좌측 상태 텍스트는 제거됨(한도 표시로 대체). 호출부 유지를 위해 no-op.
    private void UpdateStatus() { }

    /// <summary>테마 변경 시 패널 토글 아이콘 + 하단 푸터 막대색 + 우측 사용량 사이드바 카드를 재갱신한다.
    /// (푸터/사이드바는 캐시된 데이터로 SetBar → RlBrush(c) → FindResource 를 다시 태워 새 테마색을 즉시 반영)</summary>
    private void OnThemeChanged_UpdatePanels(string _) => Dispatcher.BeginInvoke(new Action(() =>
    {
        UpdatePanelToggleVisual();
        ApplyFooterUsageVisibility();
    }));

    // ── 설정창 / MCP (오버레이) ───────────────────────────────────────
    private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        await SuspendTerminalWithSnapshotAsync(blankCurtain: true);   // 터미널을 숨기고 단색 커튼(배경색)만 보이게.
        var dlg = new Views.SettingsWindow { Owner = this };
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
        dlg.Closed += (_, _) => ResumeTerminal();
        dlg.ShowDialog();
    }

    // ── airspace 우회 (오버레이가 뜰 때 터미널 WebView2 정지) ────────────
    /// <summary>모든 패널 터미널 + 우측 브라우저를 정지(스냅샷/커튼). 설정·MCP 오버레이용.</summary>
    private async Task SuspendTerminalWithSnapshotAsync(bool blankCurtain = false)
    {
        await FileExplorer.SuspendBrowserAsync();
        foreach (var pane in _panes) await pane.SuspendTerminalWithSnapshotAsync(blankCurtain);
    }

    private void ResumeTerminal()
    {
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
    private Rect _preFsBounds;         // 전체화면 진입 전 일반 창 bounds(복원용)

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _mainHwnd = new WindowInteropHelper(this).Handle;
        if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(WndProc);
        EnableDwmTransitions(_mainHwnd); // 최대화/복원 시 DWM 부드러운 전환 활성화
        ApplyCornerPreference();          // 최대화 시 각진 모서리(둥근 모서리가 화면 모서리를 깎는 문제 방지)
        ApplyMaximizeMargin();            // 최대화 시 프레임 두께만큼 마진 보정(가장자리 잘림 방지)
        StateChanged += OnStateChangedForFullScreen;
        Activated   += (_, _) => UpdateFullScreenTopmost();
        Deactivated += (_, _) => { UpdateFullScreenTopmost(); HidePanePick(); };
        // 시작 시 전체화면 복원: 저장된 일반 bounds 위치(=올바른 모니터)에서 전체화면 진입.
        if (_restoreFullScreen) { _restoreFullScreen = false; EnterFullScreen(); }
        else if (_useFullScreen && WindowState == WindowState.Maximized) EnterFullScreen();
    }


    private void OnStateChangedForFullScreen(object? sender, EventArgs e)
    {
        ApplyCornerPreference();
        ApplyMaximizeMargin();
        if (_fsGuard) return;
        // 전체화면 설정 ON 상태에서 최대화 요청(버튼·더블클릭·시스템) → 수동 전체화면으로 전환.
        if (_useFullScreen && WindowState == WindowState.Maximized && !_inFullScreen)
            EnterFullScreen();
    }

    /// <summary>WS_MAXIMIZE 없이 모니터 전체를 채우는 수동 전체화면 진입(작업표시줄까지 덮음).</summary>
    private void EnterFullScreen()
    {
        if (_mainHwnd == IntPtr.Zero) return;
        if (!TryGetMonitorDip(out var monitor, out _)) return;
        var target = OverCover(monitor); // 가장자리 틈 방지 ±1px
        _preFsBounds = RestoreBounds;   // 진입 전 일반 창 bounds
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

    }


    /// <summary>전체화면 해제 → 진입 전 창 크기를 현재 모니터 작업영역 중앙에 배치(애니메이션 없음).</summary>
    private void ExitFullScreen()
    {
        if (!_inFullScreen) return;
        _inFullScreen = false;
        Topmost = false;
        ResizeMode = ResizeMode.CanResize;
        double w = (!_preFsBounds.IsEmpty && _preFsBounds.Width  > 0) ? _preFsBounds.Width  : ActualWidth;
        double h = (!_preFsBounds.IsEmpty && _preFsBounds.Height > 0) ? _preFsBounds.Height : ActualHeight;
        var area = TryGetMonitorDip(out _, out var work) ? work : new Rect(Left, Top, w, h);
        // 작업영역 중앙. 창이 더 크면 작업영역 안으로 클램프.
        w = Math.Min(w, area.Width);
        h = Math.Min(h, area.Height);
        SetBoundsInstant(new Rect(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h));
        ApplyCornerPreference();
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

    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MOVE = 0xF010;
    private const int HTCAPTION = 2;

    // 전체화면 중 캡션 누름 추적: 실제 드래그일 때만 축소, 단순 클릭은 무시, 더블클릭은 시간차로 직접 판정.
    private bool _fsCapPending;
    private int _fsCapDownTick;
    private POINT _fsCapDownPt;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 상단바(비클라이언트 캡션) 클릭은 WPF PreviewMouseDown 으로 안 잡히므로 여기서 picker 를 닫는다.
        if ((msg == WM_NCLBUTTONDOWN || msg == WM_NCLBUTTONDBLCLK) && _projectTargetPickerWindow != null)
            HidePanePick();

        if (msg == WM_GETMINMAXINFO) { WmGetMinMaxInfo(lParam); handled = true; }
        // 전체화면 ON 이면 상단바 더블클릭(캡션 더블클릭)도 기본 최대화 대신 전체화면 토글.
        else if (msg == WM_NCLBUTTONDBLCLK && _useFullScreen) { ToggleMaximizeOrFullScreen(); handled = true; }
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
        // 캡처 중 충분히 움직이면 드래그로 판정 → 축소 후 네이티브 이동 루프로 인계.
        else if (msg == WM_MOUSEMOVE && _fsCapPending && _inFullScreen)
        {
            GetCursorPos(out var p);
            if (Math.Abs(p.X - _fsCapDownPt.X) > GetSystemMetrics(SM_CXDRAG)
                || Math.Abs(p.Y - _fsCapDownPt.Y) > GetSystemMetrics(SM_CYDRAG))
            {
                _fsCapPending = false; _fsCapDownTick = 0;
                handled = true;
                RestoreFromFullScreenAndDrag(p.X, p.Y);
            }
        }
        // 움직임 없이 떼면 단순 클릭 — 아무 동작 안 함(다음 down 과의 시간차로 더블클릭 판정).
        else if (msg == WM_LBUTTONUP && _fsCapPending)
        {
            _fsCapPending = false; ReleaseCapture(); handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>전체화면 중 캡션을 드래그하면 진입 전 일반 크기로 복원하고, 커서가 타이틀바 위에
    /// 오도록 창을 재배치한 뒤 네이티브 이동 루프(SC_MOVE)로 드래그를 이어간다.</summary>
    private void RestoreFromFullScreenAndDrag(int screenPxX, int screenPxY)
    {
        if (!_inFullScreen) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = screenPxX / dpi.DpiScaleX, cy = screenPxY / dpi.DpiScaleY;
        double w = (!_preFsBounds.IsEmpty && _preFsBounds.Width  > 0) ? _preFsBounds.Width  : 960;
        double h = (!_preFsBounds.IsEmpty && _preFsBounds.Height > 0) ? _preFsBounds.Height : 640;

        _inFullScreen = false;
        Topmost = false;
        ResizeMode = ResizeMode.CanResize;
        SetBoundsInstant(new Rect(cx - w / 2, cy - 16, w, h));
        ApplyCornerPreference();

        // SC_MOVE: 합성 NCLBUTTONDOWN 을 안 써(더블클릭 오인 방지) 커서 따라 이동 시작.
        ReleaseCapture();
        SendMessage(_mainHwnd, WM_SYSCOMMAND, (IntPtr)(SC_MOVE | 0x0002), IntPtr.Zero);
    }

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

        // 도킹 모드: 우측 패널이 창 밖으로 잘리지 않게 폭을 클램프(중앙 최소 폭 보장).
        const double centerMin = 360;
        double splitters = SidebarSplitterCol.ActualWidth + FileExpSplitterCol.ActualWidth;
        double maxFileExp = avail - SidebarCol.ActualWidth - splitters - centerMin;
        if (maxFileExp < FileExpCol.MinWidth) maxFileExp = FileExpCol.MinWidth;
        double current = FileExpCol.Width.IsAbsolute ? FileExpCol.Width.Value : FileExpCol.ActualWidth;
        if (current > maxFileExp + 0.5)
            FileExpCol.Width = new GridLength(maxFileExp);
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
        _rightAnimCancel?.Invoke();
        _overlayAnimCancel?.Invoke();
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
        _overlayAnimCancel?.Invoke();
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

    /// <summary>좁은 창에서 우측 패널을 오버레이로 연다(우측에서 슬라이드 인 + 스크림).
    /// 중앙 터미널 WebView2 는 native HWND 라 WPF 오버레이를 뚫고 올라오므로 스냅샷으로 정지한다.</summary>
    private async Task OpenRightOverlay()
    {
        _overlayAnimCancel?.Invoke();
        await SuspendTerminalOnlyAsync();   // airspace 우회: 터미널을 스냅샷으로 정지
        double w = OverlayWidth();
        RightOverlayPanel.Width = w;
        ReparentToOverlay();
        RightOverlayHost.Visibility = Visibility.Visible;
        _rightOverlayOpen = true;
        _overlayAnimCancel = AnimateOverlayX(w, 0, 200, easeIn: false);
        UpdatePanelToggleVisual();
        UpdateUsageSidebarBorder();
    }

    /// <summary>오버레이를 닫는다(우측으로 슬라이드 아웃 후 숨김 + 도킹 위치로 복귀).</summary>
    private void CloseRightOverlay()
    {
        _overlayAnimCancel?.Invoke();
        double w = RightOverlayPanel.ActualWidth > 0 ? RightOverlayPanel.ActualWidth : OverlayWidth();
        _rightOverlayOpen = false;
        _overlayAnimCancel = AnimateOverlayX(0, w, 180, easeIn: true, onComplete: () =>
        {
            RightOverlayHost.Visibility = Visibility.Collapsed;
            _rightT.X = 0;
            DockFileExplorer();
            FileExplorer.Visibility = Visibility.Collapsed; // 좁은 창에서는 닫힘=숨김
            ResumeTerminalOnly();                            // 터미널 복원
            UpdateUsageSidebarBorder();
        });
        UpdatePanelToggleVisual();
    }

    private void RightScrim_Click(object sender, MouseButtonEventArgs e) => CloseRightOverlay();

    /// <summary>오버레이 슬라이드 애니메이션(TranslateTransform.X) — 렌더 펄스 동기.</summary>
    private Action AnimateOverlayX(double from, double to, int durationMs, bool easeIn, Action? onComplete = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool cancelled = false;
        _rightT.X = from;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (cancelled) { System.Windows.Media.CompositionTarget.Rendering -= handler!; return; }
            var t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)durationMs);
            var eased = easeIn ? EaseIn(t) : EaseInOut(t);
            _rightT.X = from + (to - from) * eased;
            if (t >= 1.0)
            {
                System.Windows.Media.CompositionTarget.Rendering -= handler!;
                _rightT.X = to;
                onComplete?.Invoke();
            }
        };
        System.Windows.Media.CompositionTarget.Rendering += handler;
        return () => { if (!cancelled) { cancelled = true; System.Windows.Media.CompositionTarget.Rendering -= handler; } };
    }

    private void MaxBtn_Click(object sender, RoutedEventArgs e) => ToggleMaximizeOrFullScreen();

    /// <summary>최대화 버튼·상단바 더블클릭 공통 토글. 전체화면 설정 ON 이면 Maximized 상태를
    /// 거치지 않고 Normal 에서 바로 전체화면 진입/해제(최대화→복원 2단 애니메이션 제거).</summary>
    private void ToggleMaximizeOrFullScreen()
    {
        if (_useFullScreen)
        {
            if (_inFullScreen) ExitFullScreen(); else EnterFullScreen();
            return;
        }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

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
