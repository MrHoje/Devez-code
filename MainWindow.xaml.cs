using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly TerminalHostView _terminal = new();
    private ProjectItem? _activeProject;   // 중앙 탭 = 이 프로젝트의 Tabs
    private SessionItem? _activeSession;   // 활성 탭이 세션 탭일 때만 세팅 (터미널용 상태)
    private TabItemBase? _activeTab;       // 현재 활성 탭(세션 or 파일). null = 미선택
    private readonly PerfMonitorService _perfMonitor = new();
    private readonly StatusLineService _statusLine = new();
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

    /// <summary>현재 로딩 스피너를 띄운 세션. claude 화면이 뜨거나(=TerminalReady) 타임아웃에 해제.</summary>
    private string? _loadingRoomId;
    private System.Windows.Threading.DispatcherTimer? _loadingTimeout;

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowPlacement();   // 마지막 창 위치/크기/최대화 복원 (없으면 CenterScreen 유지)

        _projects = WorkspaceStore.Load();
        Sidebar.Projects = _projects;
        TerminalHostContainer.Content = _terminal;

        Sidebar.AddProjectRequested    += AddProject;
        Sidebar.ProjectSelected        += SelectProject;
        Sidebar.AddSessionRequested    += AddSession;
        Sidebar.ProjectDeleteRequested += DeleteProject;
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

        // 중앙 탭 드래그 순서변경(가로) — devez ReorderDrag
        TabsHost.PreviewMouseMove += TabsHost_PreviewMouseMove;
        TabsHost.PreviewMouseLeftButtonUp += async (_, _) => await EndTabDragAsync();
        TabsHost.LostMouseCapture += async (_, _) => await EndTabDragAsync();

        _terminal.SessionStarted += id => { var s = FindSession(id); if (s != null) s.IsAlive = true; };
        _terminal.SessionExited  += id => { var s = FindSession(id); if (s != null) { s.IsAlive = false; s.IsBusy = false; } HideSessionLoadingIf(id); };
        // claude 화면이 완전히 뜨면(alt-screen) 로딩 스피너 종료
        _terminal.TerminalReady  += id => HideSessionLoadingIf(id);
        // 터미널 단축키(Ctrl+Shift+T/W, Ctrl+Tab) → 세션 추가/닫기/전환
        _terminal.SessionActionRequested += OnTerminalSessionAction;

        // 세션 요청 처리중 스피너: claude 훅(busy-hook.ps1)이 떨군 상태 파일을 감시 (clude-blinker 방식).
        _sessionBusy.BusyChanged += (id, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(id);
                if (s != null) s.IsBusy = busy;
                if (!busy) FlushPendingModelEffort(id); // 응답 종료 → 보류된 model/effort 적용
            });

        // statusLine 훅이 떨군 방별 실제 model/effort → 활성 세션이면 콤보를 그 값으로 라이브 갱신.
        _modelEffort.Changed += (roomId, modelId, effortLevel) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (_activeSession != null && _activeSession.Id == roomId) RefreshModelEffortDock();
            });

        // 마지막 보낸 메시지: busy 훅이 떨군 lastmsg 파일을 감시 → 세션에 반영(헤더 부제 라이브 갱신).
        _sessionLastMsg.MessageChanged += (id, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(id);
                if (s == null) return;
                s.LastMessage = msg;
                if (ReferenceEquals(s, _activeSession)) UpdateEmptyState(); // 활성 세션이면 헤더 즉시 갱신
            });

        // 비-Claude 비-codex 에이전트(opencode/gjc) — (workingDir, lastPrompt) 이벤트로 같은 디렉터리 세션 모두 갱신.
        _agentLastMsg.LastPromptChanged += (workingDir, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                bool anyActive = false;
                var norm = System.IO.Path.GetFullPath(workingDir).TrimEnd('\\', '/');
                foreach (var p in _projects)
                {
                    var pNorm = System.IO.Path.GetFullPath(p.Path).TrimEnd('\\', '/');
                    if (!string.Equals(pNorm, norm, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var s in p.Tabs.OfType<SessionItem>())
                    {
                        s.LastMessage = msg;
                        if (ReferenceEquals(s, _activeSession)) anyActive = true;
                    }
                }
                if (anyActive) UpdateEmptyState();
            });

        // opencode — 플러그인이 떨군 lastmsg 파일을 즉시 반영(claude 와 동일 패턴, 3초 폴링 대기 X).
        _opencodeLastMsg.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(roomId);
                if (s == null) return;
                s.LastMessage = msg;
                if (ReferenceEquals(s, _activeSession)) UpdateEmptyState();
            });

        // codex — Claude 와 동일하게 roomId 키로 즉시 갱신 (폴링 X).
        _codexHook.MessageChanged += (roomId, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(roomId);
                if (s == null) return;
                s.LastMessage = msg;
                if (ReferenceEquals(s, _activeSession)) UpdateEmptyState();
            });
        _codexHook.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() => { var s = FindSession(roomId); if (s != null) s.IsBusy = busy; });
        _codexHook.CodexSessionChanged += (roomId, sid) =>
            Dispatcher.InvokeAsync(() => SettingsService.SaveCodexRoomSession(roomId, sid));

        // 테마 변경 시 선택 탭 seam 색(PanelBrush)을 재계산(frozen brush라 자동 갱신 안 됨)
        App.ThemeChanged += OnThemeChanged_UpdateSeam;

        // 파일 탐색기에서 텍스트 파일 더블클릭 → 새 파일 탭으로 열기
        FileExplorer.FileOpenRequested += (_, path) => OpenFileAsTab(path);

        UpdateEmptyState();
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
            _agentLastMsg.Start();
            RestoreLastSession();
            CheckHookSetup(); // 훅 미설치/구버전이면 상단 배너로 원클릭 설정 안내
            ApplyFileExpMinWidth(); // 탭 버튼 4개 온전히 보이는 폭을 패널 최소 폭으로
            ApplySidePanelButtonVisibility();
        };

        // 창 위치/크기는 닫히기 직전(Closing)에 저장한다 — RestoreBounds 가 유효한 시점.
        Closing += (_, _) => SaveWindowPlacement();

        Closed += (_, _) =>
        {
            // 정상 종료: 마지막 활성 프로젝트/세션 기억 + 클린 종료 플래그 set
            SettingsService.SaveLastActive(_activeProject?.Path, _activeSession?.Id);
            SettingsService.SaveCleanShutdown(true);
            App.ThemeChanged -= OnThemeChanged_UpdateSeam;
            _perfMonitor.Dispose();
            _statusLine.Dispose();
            _sessionBusy.Dispose();
            _modelEffort.Dispose();
            _sessionLastMsg.Dispose();
            _codexHook.Dispose();
            _opencodeLastMsg.Dispose();
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
        if (max) WindowState = WindowState.Maximized;
    }

    /// <summary>현재 창 위치/크기/최대화를 저장. 최대화·최소화 상태여도 RestoreBounds 로 일반 크기를 기록.</summary>
    private void SaveWindowPlacement()
    {
        bool max = WindowState == WindowState.Maximized;
        var b = RestoreBounds;
        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0)
            b = new Rect(Left, Top, ActualWidth, ActualHeight);
        SettingsService.SaveWindowPlacement(b.Left, b.Top, b.Width, b.Height, max);
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

    /// <summary>직전이 정상 종료였다면 마지막으로 보고 있던 세션을 복원한다(크래시 시엔 복원하지 않음).</summary>
    private void RestoreLastSession()
    {
        // 마지막 활성 세션은 활성화 시점에 즉시 저장되므로(ActivateSession), 강제 종료·크래시
        // 후에도 복원한다. (clean-shutdown 게이트는 제거 — 사용자는 재시작 시 늘 복원되길 기대.)
        SettingsService.SaveCleanShutdown(false);

        var (projPath, sessionId) = SettingsService.LoadLastActive();
        if (string.IsNullOrEmpty(sessionId)) return;

        // 저장된 프로젝트 우선, 없으면 전체에서 세션ID로 탐색
        var session = _projects.FirstOrDefault(p => p.Path == projPath)?
                          .Tabs.OfType<SessionItem>().FirstOrDefault(s => s.Id == sessionId)
                      ?? _projects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == sessionId);
        if (session != null) OpenSession(session);
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

    // ── 계정 사용량 (statusLine 훅 → ratelimit.json → 푸터) ─────────────
    // claude 세션의 statusLine 훅이 떨군 rate_limits(계정 전역값)를 감시해 푸터에 표시한다.
    private void StartStatusLine()
    {
        _statusLine.SnapshotUpdated += snap =>
            Dispatcher.InvokeAsync(() => ApplyRateLimit(snap));
        _statusLine.Start();
    }

    private void ApplyRateLimit(Models.RateLimitSnapshot snap)
    {
        if (!snap.HasData) { RateLimitPanel.Visibility = Visibility.Collapsed; UpdateFooterDivider(); return; }
        RateLimitPanel.Visibility = Visibility.Visible;
        UpdateFooterDivider();
        // 5시간 칸 라벨은 남은시간(없으면 "5시간"), 주간은 고정 라벨.
        SetBar(RlFiveLabel, RlFiveBar, RlFivePct,
               FormatRemainingShort(snap.FiveHourResetsAt) ?? "5시간", snap.FiveHourPercent);
        SetBar(RlSevenLabel, RlSevenBar, RlSevenPct, "주간", snap.SevenDayPercent);
        RateLimitPanel.ToolTip = BuildRlTooltip(snap);
    }

    /// <summary>rate limit 툴팁 — 사용률 + 초기화 시각/남은 시간.</summary>
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

    private const double RlTrackWidth = 56;

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

    /// <summary>남은 시간 짧은 표기 ("1시간24분" / "24분" / "곧"). 초기화 시각 없으면 null.</summary>
    private static string? FormatRemainingShort(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not DateTimeOffset r) return null;
        var span = r.ToLocalTime() - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "곧";
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}시간{span.Minutes}분" : $"{span.Minutes}분";
    }

    /// <summary>성능 모니터 칩(푸터) 표시. 토글이 제거되어 항상 표시.</summary>
    private void ApplyPerfMonitorVisibility()
    {
        PerfChipGroup.Visibility = Visibility.Visible;
        UpdateFooterDivider();
    }

    /// <summary>푸터 구분선 — PerfChipGroup 과 RateLimitPanel 이 다른 컬럼으로 분리되어 더 이상 필요 없음.
    /// 호출부 호환을 위해 no-op 로 남겨둔다.</summary>
    private void UpdateFooterDivider() { }

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
    private void LeftPanelBtn_Click(object sender, RoutedEventArgs e)
    {
        _leftAnimCancel?.Invoke();
        if (_leftCollapsed)
        {
            _leftCollapsed = false;
            Sidebar.Visibility = Visibility.Visible;
            SetMinWidth(190, SidebarCol, FooterSidebarCol);
            _leftAnimCancel = AnimatePanelAndSplitter(
                SidebarCol, _sidebarWidth,
                SidebarSplitterCol, 4,
                durationMs: 200, easeIn: false,
                colMirrors: new[] { FooterSidebarCol },
                splitterMirrors: Array.Empty<ColumnDefinition>(),
                cacheTarget: Sidebar,
                onComplete: () => _leftAnimCancel = null);
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
                });
        }
        SettingsService.SaveLeftPanel(_leftCollapsed, _sidebarWidth);
        UpdatePanelToggleVisual();
    }

    private void RightPanelBtn_Click(object sender, RoutedEventArgs e)
    {
        // 좁은 창: 도킹 대신 오버레이 드로어를 토글한다.
        if (_narrow == true)
        {
            if (_rightOverlayOpen) CloseRightOverlay();
            else _ = OpenRightOverlay();
            return;
        }

        _rightAnimCancel?.Invoke();
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
                });
        }
        SettingsService.SaveRightPanel(_rightCollapsed, _fileExpWidth);
        UpdatePanelToggleVisual();
    }

    /// <summary>설정에 저장된 사이드 패널 뷰 전환 버튼 표시 여부를 우측 패널에 반영한다.</summary>
    public void ApplySidePanelButtonVisibility()
    {
        FileExplorer.ApplyTabButtonVisibility();
    }

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
        UpdatePanelToggleVisual();
    }

    private void UpdatePanelToggleVisual()
    {
        var muted   = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        var primary = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
        LeftPanelIcon.Stroke  = _leftCollapsed  ? primary : muted;
        // 좁은 창에서는 오버레이가 닫혀 있으면(=숨김) 강조 표시.
        bool rightHidden = _narrow == true ? !_rightOverlayOpen : _rightCollapsed;
        RightPanelIcon.Stroke = rightHidden ? primary : muted;
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
        var progress = new Progress<double>(v =>
        {
            RateLimitPanel.Visibility = Visibility.Visible;
            RlFiveLabel.Text = $"업데이트 다운로드 중… {v:P0}";
            RlFivePct.Text = "";
            RlFiveBar.Width = RlTrackWidth * Math.Clamp(v, 0, 1);
            RlFiveBar.Background = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
            // 진행 표시 중엔 주간 칸은 비워 혼동을 줄인다.
            RlSevenLabel.Text = ""; RlSevenPct.Text = ""; RlSevenBar.Width = 0;
        });
        try
        {
            await UpdateService.DownloadAndRelaunchAsync(info, progress);
            // 성공 시 앱이 종료/재실행되므로 이 아래로는 도달하지 않는다.
        }
        catch
        {
            _updateInProgress = false;
            RateLimitPanel.Visibility = Visibility.Collapsed; // 진행률 제거 — 다음 한도 스냅샷에 복원
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
            var psi = new System.Diagnostics.ProcessStartInfo(file.FilePath) { UseShellExecute = true };
            if (file.RunAsAdmin) psi.Verb = "runas";
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

    /// <summary>활성 프로젝트 전환 — 중앙 탭을 그 프로젝트의 탭들로 교체(같은 컬렉션 바인딩). 세션 활성화는 안 함.
    /// Hidden 플래그는 보존 — 탭 X 로 숨긴 세션은 프로젝트 재진입 시에도 그대로 숨김 상태 유지.</summary>
    private void SetActiveProject(ProjectItem proj)
    {
        _activeProject = proj;
        foreach (var p in _projects) p.IsSelected = ReferenceEquals(p, proj);
        TabsHost.ItemsSource = proj.Tabs;
        FileExplorer.ShowDirectory(proj.Path);
        // 디렉토리 버블 갱신
        if (ProjectPathText != null)
        {
            ProjectPathText.Text = proj.Path;
            ProjectPathText.ToolTip = proj.Path;
        }
        if (ProjectNameText != null)
        {
            ProjectNameText.Text = proj.Name;
            ProjectNameText.ToolTip = proj.Name;
        }
        UpdateProjectBranchBubble(proj); // 브렌치 버블 갱신
        // 기본은 선택된 세션만 실행. 옵션이 켜진 경우에만 기존처럼 모든 세션을 미리 띄운다.
        if (SettingsService.LoadPreloadAllProjectSessions())
            PreloadProjectSessions(proj, except: null);
    }

    /// <summary>타이틀 바 브렌치 그룹 갱신 — 현재 git 브렌치.
    /// 브렌치는 비동기로 조회(외부 프로세스라 UI 블로킹 방지). git 저장소가 아니면 숨김.</summary>
    private void UpdateProjectBranchBubble(ProjectItem? proj)
    {
        if (proj == null || string.IsNullOrEmpty(proj.Path) || BranchGroup == null)
        {
            BranchGroup.Visibility = Visibility.Collapsed;
            return;
        }
        // 비동기 조회. 이전 요청이 진행 중일 수 있으니 _projectCts 로 취소.
        _projectCts?.Cancel();
        _projectCts = new System.Threading.CancellationTokenSource();
        _ = LoadBranchAsync(proj.Path, _projectCts.Token);
    }

    private System.Threading.CancellationTokenSource? _projectCts;

    /// <summary>git 브렌치 조회 — UI 스레드에서 결과 반영. 실패/비저장소면 버블 숨김.</summary>
    private async Task LoadBranchAsync(string repoDir, System.Threading.CancellationToken ct)
    {
        string? branch = null;
        try
        {
            if (await GitService.IsRepoAsync(repoDir))
            {
                var r = await GitService.RunAsync(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
                if (r.Ok)
                {
                    var name = r.Output.Trim();
                    if (!string.IsNullOrEmpty(name) && name != "HEAD") branch = name;
                }
            }
        }
        catch { /* git 미설치 등 */ }
        if (ct.IsCancellationRequested) return;
        await Dispatcher.InvokeAsync(() =>
        {
            BranchGroup.Visibility = branch != null ? Visibility.Visible : Visibility.Collapsed;
            if (branch != null) ProjectBranchText.Text = branch;
        });
    }

    /// <summary>프로젝트 선택(행 클릭) — 탭 교체 후 세션 하나 활성화(이전 활성 or 첫 세션).</summary>
    private void SelectProject(ProjectItem proj)
    {
        SetActiveProject(proj);
        // 프로젝트 헤더 클릭 진입 — Hidden 플래그를 보존한다 (unHide=false).
        // 활성 세션 후보는 항상 비숨김으로만 — 숨겨진 세션을 ActivateSession 으로 보내면
        // 터미널에는 활성화되지만 탭 스트립에는 보이지 않는 어색한 상태가 된다.
        SessionItem? target = null;
        if (_activeSession != null && proj.Tabs.Contains(_activeSession) && !_activeSession.Hidden)
            target = _activeSession;
        else
            target = proj.Tabs.OfType<SessionItem>().FirstOrDefault(s => !s.Hidden);
        if (target != null) ActivateSession(target, unHide: false);
        else ClearActiveSession();
        // 나머지 세션 preload 는 옵션이 켜진 경우 SetActiveProject 가 이미 처리했다.
    }

    /// <summary>프로젝트의 모든 세션을 백그라운드로 미리 생성(preload). 활성 세션(except)은 제외.</summary>
    private void PreloadProjectSessions(ProjectItem proj, SessionItem? except)
    {
        foreach (var s in proj.Tabs.OfType<SessionItem>())
        {
            if (ReferenceEquals(s, except) || s.Hidden) continue;
            SettingsService.SaveClaudeCodeRoomDir(s.Id, proj.Path); // 항상 프로젝트 폴더에서 실행되도록 보장
            _terminal.PreloadTerminal(s.Id);
        }
    }

    private void DeleteProject(ProjectItem proj)
    {
        if (!ConfirmDialog.Show("프로젝트 제거",
                $"'{proj.Name}' 프로젝트를 목록에서 제거할까요?\n(디스크의 실제 파일은 삭제되지 않습니다.)",
                okLabel: "제거", danger: true))
            return;

        foreach (var s in proj.Tabs.OfType<SessionItem>().ToList()) DisposeSessionProcess(s, purge: false);
        _projects.Remove(proj);
        SettingsService.RemoveBrowserLastUrl(proj.Path);
        WorkspaceStore.Save(_projects);

        if (ReferenceEquals(_activeProject, proj))
        {
            _activeProject = null;
            _activeSession = null;
            _activeTab = null;
            var next = _projects.FirstOrDefault();
            if (next != null) SelectProject(next);
            else { TabsHost.ItemsSource = null; ClearActiveSession(); }
        }
        UpdateStatus();
    }

    // ── 세션 ──────────────────────────────────────────────────────
    /// <summary>터미널 단축키에서 올라온 세션 액션 처리 (UI 스레드 보장).</summary>
    private void OnTerminalSessionAction(string name, int index) => Dispatcher.BeginInvoke(() =>
    {
        switch (name)
        {
            case "newSession":   if (_activeProject != null) AddSession(_activeProject); break;
            // Ctrl+Shift+W = 닫기 → 목록에서만 제거(기록 .jsonl 보존). 영구 삭제는 명시적 UI 전용.
            case "closeSession": if (_activeSession != null) StopTrackingSession(_activeSession); break;
            case "nextSession":  CycleSession(+1); break;
            case "prevSession":  CycleSession(-1); break;
            case "gotoSession":  GotoSession(index); break;
        }
    });

    /// <summary>활성 프로젝트의 N번째 세션으로 직접 이동 (Ctrl+Alt+1~9). index=-1 이면 마지막. 범위 밖이면 무시.</summary>
    private void GotoSession(int index)
    {
        var proj = _activeProject;
        var sessionTabs = proj?.Tabs.OfType<SessionItem>().ToList();
        if (sessionTabs == null || sessionTabs.Count == 0) return;
        int i = index < 0 ? sessionTabs.Count - 1 : index;
        if (i < 0 || i >= sessionTabs.Count) return; // WT: 없는 탭 번호면 아무 동작 안 함
        OpenSession(sessionTabs[i]);
    }

    /// <summary>활성 프로젝트 내 세션을 순환 전환 (Ctrl+Tab / Ctrl+Shift+Tab).</summary>
    private void CycleSession(int dir)
    {
        var proj = _activeProject;
        var sessionTabs = proj?.Tabs.OfType<SessionItem>().ToList();
        if (proj == null || _activeSession == null || sessionTabs == null || sessionTabs.Count < 2) return;
        int idx = sessionTabs.IndexOf(_activeSession);
        if (idx < 0) return;
        int n = sessionTabs.Count;
        OpenSession(sessionTabs[((idx + dir) % n + n) % n]);
    }

    /// <summary>"세션 N" 다음 번호를 만든다 — 기존 세션 이름에서 최대 N을 찾아 +1.
    /// Count+1 방식과 달리 중간 세션을 삭제해도 번호가 겹치지 않는다.</summary>
    private static string NextSessionName(ProjectItem proj)
    {
        int max = 0;
        var rx = new System.Text.RegularExpressions.Regex(@"^세션\s+(\d+)$");
        foreach (var s in proj.Tabs.OfType<SessionItem>())
        {
            var m = rx.Match(s.Name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
        }
        return $"세션 {max + 1}";
    }

    private void AddSession(ProjectItem proj)
    {
        // 사용 가능한 에이전트(설치 + 활성화) 선택. 0개면 추가 불가, 1개면 피커 생략.
        var available = AgentRegistry.GetEnabledAndInstalled();
        if (available.Count == 0)
        {
            ConfirmDialog.Alert("에이전트 없음",
                "사용 가능한 에이전트가 없습니다.\n설정 → 에이전트 에서 하나 이상 활성화해 주세요.");
            return;
        }
        string agentId;
        if (available.Count == 1)
        {
            agentId = available[0].Id;
        }
        else
        {
            var picked = AgentPickerDialog.Pick(this, available, proj.Path);
            if (picked == null) return; // 취소
            agentId = picked;
        }

        var session = new SessionItem { Name = NextSessionName(proj), AgentId = agentId };
        proj.Tabs.Add(session);
        proj.IsExpanded = true;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, proj.Path);
        SettingsService.SaveAgentForRoom(session.Id, agentId);
        WorkspaceStore.Save(_projects);
        // 선택된(활성) 프로젝트의 세션만 화면에 띄운다. 다른 프로젝트엔 트리에 추가만.
        if (ReferenceEquals(_activeProject, proj)) OpenSession(session);
        UpdateStatus();
    }

    /// <summary>세션 클릭(사이드바/탭) — 필요하면 프로젝트 전환 후 해당 세션 활성화.</summary>
    private void OpenSession(SessionItem session)
    {
        var parent = ParentOf(session);
        if (parent == null) return;
        if (!ReferenceEquals(_activeProject, parent)) SetActiveProject(parent);
        ActivateSession(session);
    }

    /// <summary>세션 활성화 — 선택 표시 + 중앙 터미널 표시(claude 실행).
    /// unHide=false 이면 탭 X 로 숨겨둔 세션이라도 Hidden 플래그를 건드리지 않는다
    /// (프로젝트 헤더 클릭으로 진입할 때 — 마지막 활성 세션이 숨겨져 있어도 그대로 유지).</summary>
    private void ActivateSession(SessionItem session, bool unHide = true)
    {
        var parent = ParentOf(session);
        if (parent == null) return;

        // 사이드바 세션 클릭 (unHide=true): 탭 X 로 숨겨둔 세션을 다시 선택하면 탭도 복귀.
        // WorkspaceModels.cs 의 HiddenIcon→StatusDot 트리거도 이 플래그 하나에 연동된다.
        if (unHide) session.Hidden = false;

        // claude 가 항상 프로젝트 디렉터리에서 실행되도록 매핑 보장
        SettingsService.SaveClaudeCodeRoomDir(session.Id, parent.Path);

        _activeTab = session;
        _activeSession = session;
        foreach (var p in _projects)
            foreach (var t in p.Tabs)
                t.IsSelected = ReferenceEquals(t, session);

        session.IsAlive = true; // 낙관적 — 실패 시 SessionExited 이벤트로 회색
        // 마지막 활성 세션 즉시 저장 → 강제 종료/크래시 후 재시작에도 이 세션으로 복원
        SettingsService.SaveLastActive(parent.Path, session.Id);
        // 비-Claude 에이전트는 last prompt 추적 시작. Claude 는 hook 으로 별도 처리.
        var sessionAgentId = string.IsNullOrEmpty(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;
        if (sessionAgentId != "claude")
            _agentLastMsg.TrackSession(parent.Path, sessionAgentId);
        // 세션 실행은 훅 자산을 최신으로 재생성하므로(있던 배너는 더 이상 불필요) 배너를 닫는다.
        if (HookSetupBanner.Visibility == Visibility.Visible) HookSetupBanner.Visibility = Visibility.Collapsed;
        // 이미 claude 화면이 떠 있는 세션이면 로딩 없이, 아니면 스피너 표시 후 ShowTerminal.
        if (_terminal.IsReady(session.Id)) HideSessionLoading();
        else ShowSessionLoading(session.Id);
        _terminal.ShowTerminal(session.Id);
        _terminal.FocusTerminal();
        UpdateEmptyState();
        EnsureSelectedTabVisible(session); // 선택 탭이 가려져 있으면 보이게 스크롤
        RefreshModelEffortDock(); // 메타바 model/effort dock 을 이 세션 값으로 갱신
    }

    /// <summary>작업 큐 → 현재 활성 세션 터미널에 텍스트를 입력하고 Enter 로 전송.
    /// 활성 탭이 세션이 아니거나 세션이 죽어 있으면 false.</summary>
    public bool SendTextToActiveSession(string text)
    {
        var id = _activeSession?.Id;
        if (string.IsNullOrEmpty(id)) return false;
        var session = TerminalSessionManager.Instance.Get(id);
        if (session is not { IsAlive: true }) return false;

        session.Write(text);
        session.Write("\r"); // Enter (ConPTY)
        _terminal.ShowTerminal(id);
        _terminal.FocusTerminal();
        return true;
    }

    /// <summary>파일 탭 활성화 — FileEditorHostContainer 에 해당 탭의 에디터를 붙이고 표시.
    /// 이미 같은 에디터가 붙어 있으면 그냥 표시만 갱신한다.</summary>
    private void ActivateFileTab(FileTabItem tab)
    {
        var parent = ParentOfTab(tab);
        if (parent == null) return;

        _activeTab = tab;
        _activeSession = null; // 활성 세션 아님 — 터미널 상태 이벤트 무시
        foreach (var p in _projects)
            foreach (var t in p.Tabs)
                t.IsSelected = ReferenceEquals(t, tab);

        HideSessionLoading(); // 파일 탭이 로딩 스피너를 물려받지 않도록
        if (!ReferenceEquals(FileEditorHostContainer.Content, tab.Editor.AsControl()))
            FileEditorHostContainer.Content = tab.Editor.AsControl();
        UpdateEmptyState();
        EnsureSelectedTabVisible(tab);
        tab.Editor.Focus(); // 포커스 이동
        RefreshModelEffortDock(); // 파일 탭 → 세션 아님 → dock 숨김
    }

    // ── 메타바 model/effort dock ─────────────────────────────────────────
    // claude 세션에만 노출. 선택 시 방별 설정 저장(다음 콜드스타트/ resume 시 --model/--effort 런치 플래그로도 적용)
    // + 살아있는 TUI 에 슬래시(/model·/effort)를 라이브 주입 → 재시작·스피너 없이 즉시 전환.
    // 응답 처리중(IsBusy)에 바꾸면 입력이 프롬프트에 섞이지 않게 종료 후 주입(_pendingModel/_pendingEffort).
    private static readonly ModelEffortOption[] ModelOptions =
    {
        new("Opus 4.8", "opus"), new("Sonnet 4.6", "sonnet"),
        new("Haiku 4.5", "haiku"), new("Fable 5", "fable"),
    };
    private static readonly ModelEffortOption[] EffortOptions =
    {
        new("low", "low"), new("medium", "medium"), new("high", "high"),
        new("xhigh", "xhigh"), new("max", "max"),
    };
    // 미설정 세션의 표시 기본값(claude 실제 기본 추정). 사용자가 한 번 고르면 그 값이 정확한 적용값.
    private const string DefaultModelValue = "opus";
    private const string DefaultEffortValue = "high";

    private bool _suppressModelEffort;            // 프로그램적 SelectedValue 설정 시 변경 핸들러 억제
    // busy 중 변경 → idle 시 주입할 보류 값(roomId → model/effort). 같은 항목 재변경은 마지막 값으로 덮어씀.
    private readonly Dictionary<string, string> _pendingModel = new();
    private readonly Dictionary<string, string> _pendingEffort = new();

    /// <summary>활성 세션 기준으로 dock 표시/값 갱신. claude 세션이 아니면 숨김.</summary>
    private void RefreshModelEffortDock()
    {
        if (ModelEffortDock == null) return;
        var s = _activeSession;
        var agentId = s == null ? null : (string.IsNullOrEmpty(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId);
        bool isClaude = s != null && agentId == "claude";
        ModelEffortDock.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
        if (!isClaude) return;

        // 표시 우선순위: statusLine 이 보고한 실제 세션값 > 저장된 선택 > 기본 추정.
        var (liveModelId, liveEffort) = _modelEffort.Read(s!.Id);
        var model  = ModelIdToValue(liveModelId) ?? SettingsService.LoadClaudeCodeRoomModel(s.Id)  ?? DefaultModelValue;
        var effort = (IsKnownEffort(liveEffort) ? liveEffort : null) ?? SettingsService.LoadClaudeCodeRoomEffort(s.Id) ?? DefaultEffortValue;

        _suppressModelEffort = true;
        try
        {
            if (ModelCombo.ItemsSource == null) ModelCombo.ItemsSource = ModelOptions;
            if (EffortCombo.ItemsSource == null) EffortCombo.ItemsSource = EffortOptions;
            ModelCombo.SelectedValue  = model;
            EffortCombo.SelectedValue = effort;
        }
        finally { _suppressModelEffort = false; }
    }

    /// <summary>claude statusLine 의 model.id("claude-opus-4-8" 등)를 콤보 값(opus/sonnet/haiku/fable)으로 매핑.</summary>
    private static string? ModelIdToValue(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var s = id.ToLowerInvariant();
        if (s.Contains("opus"))   return "opus";
        if (s.Contains("sonnet")) return "sonnet";
        if (s.Contains("haiku"))  return "haiku";
        if (s.Contains("fable") || s.Contains("mythos")) return "fable";
        return null;
    }

    private static bool IsKnownEffort(string? e)
        => e is "low" or "medium" or "high" or "xhigh" or "max";

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => OnModelEffortPicked(isModel: true);
    private void EffortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => OnModelEffortPicked(isModel: false);

    private void OnModelEffortPicked(bool isModel)
    {
        if (_suppressModelEffort) return;
        var s = _activeSession;
        if (s == null) return;
        var val = (isModel ? ModelCombo : EffortCombo).SelectedValue as string;
        if (string.IsNullOrEmpty(val)) return;

        if (isModel) SettingsService.SaveClaudeCodeRoomModel(s.Id, val);
        else         SettingsService.SaveClaudeCodeRoomEffort(s.Id, val);

        if (s.IsBusy)
            (isModel ? _pendingModel : _pendingEffort)[s.Id] = val; // 응답중 — 종료 후 주입
        else
            SendModelEffortSlash(s.Id, isModel, val);               // 라이브 즉시 주입
    }

    /// <summary>응답 종료(busy→idle) 시 보류된 model/effort 변경을 라이브 주입.</summary>
    private void FlushPendingModelEffort(string roomId)
    {
        if (_pendingModel.Remove(roomId, out var m))  SendModelEffortSlash(roomId, isModel: true,  m);
        if (_pendingEffort.Remove(roomId, out var ef)) SendModelEffortSlash(roomId, isModel: false, ef);
    }

    /// <summary>살아있는 claude TUI 에 /model·/effort 슬래시를 주입해 재시작 없이 즉시 전환.
    /// 세션이 죽어있으면 무시(다음 콜드스타트 시 저장된 설정이 --model/--effort 런치 플래그로 적용됨).</summary>
    private void SendModelEffortSlash(string roomId, bool isModel, string value)
    {
        var sess = TerminalSessionManager.Instance.Get(roomId);
        if (sess is not { IsAlive: true }) return;
        // 출력 스크롤 튀김 방지 — command echo + response 가 돌아오는 동안 xterm scroll 을 억제
        _terminal.SuppressScroll(5);
        sess.Write((isModel ? "/model " : "/effort ") + value + "\r");
    }

    // ── 새 탭 + 버튼 ──────────────────────────────────────────────
    /// <summary>탭 스트립의 + 버튼 클릭. 활성 프로젝트가 있으면 그 프로젝트에, 없으면 먼저 추가 다이얼로그 흐름이 필요한데
    /// UX 단순화를 위해 활성 프로젝트가 없으면 경고 후 중단. AddSession 내부에서 에이전트 0개·다중 피커를 처리한다.</summary>
    private void NewTabBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject == null)
        {
            ConfirmDialog.Alert("프로젝트 없음",
                "먼저 왼쪽 사이드바에서 프로젝트를 추가하세요.");
            return;
        }
        AddSession(_activeProject);
    }

    /// <summary>세션 로딩 스피너 표시 — claude 화면이 뜰 때까지. 안전장치로 일정 시간 후 자동 해제.</summary>
    private void ShowSessionLoading(string roomId)
    {
        _loadingRoomId = roomId;
        TerminalLoadingOverlay.Visibility = Visibility.Visible; // 콜드스타트(WebView2 미렌더) 폴백
        _terminal.SetLoading(true);                             // 웹 레이어 스피너(터미널 위에 보임)

        _loadingTimeout?.Stop();
        _loadingTimeout ??= new System.Windows.Threading.DispatcherTimer();
        _loadingTimeout.Interval = TimeSpan.FromSeconds(20); // alt-screen 미진입(일반 셸 등) 대비 폴백
        _loadingTimeout.Tick -= LoadingTimeout_Tick;
        _loadingTimeout.Tick += LoadingTimeout_Tick;
        _loadingTimeout.Start();
    }

    private void LoadingTimeout_Tick(object? sender, EventArgs e) => HideSessionLoading();

    /// <summary>로딩 스피너 숨김.</summary>
    private void HideSessionLoading()
    {
        _loadingTimeout?.Stop();
        _loadingRoomId = null;
        TerminalLoadingOverlay.Visibility = Visibility.Collapsed;
        _terminal.SetLoading(false);
    }

    /// <summary>해당 방이 현재 로딩 중이던 세션이면 스피너 숨김.</summary>
    private void HideSessionLoadingIf(string roomId)
    {
        if (_loadingRoomId == roomId) HideSessionLoading();
    }

    private void ClearActiveSession()
    {
        _activeSession = null;
        _activeTab = null;
        if (FileEditorHostContainer != null) FileEditorHostContainer.Content = null;
        HideSessionLoading();
        foreach (var p in _projects)
            foreach (var t in p.Tabs)
                t.IsSelected = false;
        UpdateEmptyState();
        UpdateSelectedTabSeam(); // 활성 탭 없음 → seam 숨김
    }

    /// <summary>세션 이름 변경 — 우클릭 메뉴. devez PromptDialog 정합.</summary>
    private void RenameSession(SessionItem session)
    {
        var name = PromptDialog.Show("세션 이름 변경", "새 이름을 입력하세요.",
                                     defaultValue: session.Name, maxLength: 60);
        if (string.IsNullOrWhiteSpace(name) || name == session.Name) return;
        session.Name = name;
        WorkspaceStore.Save(_projects);
    }

    /// <summary>세션 영구 삭제 — 확인 후 ConPTY 프로세스·매핑 + claude 대화 기록(.jsonl)까지 디스크에서 제거.</summary>
    private void DeleteSession(SessionItem session)
    {
        if (!ConfirmDialog.Show("세션 삭제",
                $"'{session.Name}' 세션을 영구 삭제할까요?\n대화 기록(.jsonl)도 디스크에서 함께 삭제되며 복구할 수 없습니다.",
                okLabel: "삭제", danger: true))
            return;
        RemoveSession(session, purge: true);
    }

    /// <summary>세션 추적 중단 — 목록에서만 제거. claude 대화 기록(.jsonl)은 디스크에 보존된다.</summary>
    private void StopTrackingSession(SessionItem session)
    {
        if (!ConfirmDialog.Show("세션 추적 중단",
                $"'{session.Name}' 세션을 목록에서 제거할까요?\n대화 기록은 디스크에 그대로 보존됩니다.",
                okLabel: "중단"))
            return;
        RemoveSession(session, purge: false);
    }

    /// <summary>세션을 트리에서 제거하고 다음 세션을 활성화. purge=true 면 대화 기록까지 삭제, false 면 보존.</summary>
    private void RemoveSession(SessionItem session, bool purge)
    {
        var parent = ParentOf(session);
        bool wasActive = ReferenceEquals(_activeSession, session);
        int idx = parent?.Tabs.IndexOf(session) ?? -1;

        DisposeSessionProcess(session, purge);
        parent?.Tabs.Remove(session);
        WorkspaceStore.Save(_projects);

        if (wasActive)
        {
            SessionItem? next = null;
            if (parent != null)
                next = parent.Tabs.OfType<SessionItem>().FirstOrDefault(s => !s.Hidden);
            if (next != null) ActivateSession(next);
            else ClearActiveSession();
        }
        UpdateStatus();
    }

    /// <summary>파일 탭을 제거. dirty 면 FileEditorView 가 자체 확인 후 이벤트로 알려준다.
    /// 에디터가 콘텐츠 호스트에 붙어 있으면 분리한다.</summary>
    private void RemoveFileTab(FileTabItem tab)
    {
        var parent = ParentOfTab(tab);
        bool wasActive = ReferenceEquals(_activeTab, tab);
        if (FileEditorHostContainer.Content == tab.Editor.AsControl())
            FileEditorHostContainer.Content = null;
        if (tab.Editor is IDisposable disposable) disposable.Dispose();
        parent?.Tabs.Remove(tab);
        UpdateStatus();

        if (wasActive)
        {
            // 같은 프로젝트의 다른 탭(세션 우선)으로 포커스 이동
            TabItemBase? next = null;
            if (parent != null)
                next = parent.Tabs.OfType<SessionItem>().FirstOrDefault(s => !s.Hidden)
                       ?? parent.Tabs.FirstOrDefault(t => t != tab);
            if (next is SessionItem s) ActivateSession(s);
            else if (next is FileTabItem f) ActivateFileTab(f);
            else ClearActiveSession();
        }
    }

    /// <summary>세션의 터미널 프로세스·매핑을 정리(컬렉션은 건드리지 않음).
    /// purge=true 면 claude 대화 기록(.jsonl)까지 영구 삭제, false 면 기록을 디스크에 보존한다.</summary>
    private void DisposeSessionProcess(SessionItem session, bool purge = true)
    {
        var workingDir = SettingsService.LoadClaudeCodeRoomDir(session.Id);
        try { _terminal.CloseTerminal(session.Id); } catch { /* ignore */ }
        try
        {
            if (purge) TerminalSessionManager.Instance.PurgeRoom(session.Id, workingDir);
            else       TerminalSessionManager.Instance.DisposeRoom(session.Id, purgeTracking: false);
        }
        catch { /* ignore */ }
        // purge=false(재시작) 경로에서는 room dir 매핑을 보존 — ActivateSession 이 다시 저장하긴 하지만,
        // GetOrCreate 가 먼저 읽어야 resume 경로가 작동하므로 삭제하지 않는다.
        if (purge) SettingsService.RemoveClaudeCodeRoomDir(session.Id);
    }

    /// <summary>활성 Claude 세션을 재시작 — 디스크에 저장된 mcpServers 가 다시 로드된다.
    /// MCP 매니저의 저장 후 호출용. 비활성이거나 비-Claude 세션이면 false.</summary>
    public bool TryRestartActiveClaudeSession()
    {
        if (_activeSession == null) return false;
        RestartAllClaudeSessions();
        return true;
    }

    /// <summary>모든 Claude 세션의 ConPTY 를 재시작. 활성 세션은 즉시 다시 띄우고,
    /// 비활성 세션은 ConPTY 만 정리(다음 활성화 시 resume 으로 복원).
    /// 테마 변경 시 모든 세션이 새 테마를 적용하도록 보장.</summary>
    private async void RestartAllClaudeSessions()
    {
        var allClaudeSessions = _projects
            .SelectMany(p => p.Tabs)
            .OfType<SessionItem>()
            .Where(s =>
            {
                var aid = string.IsNullOrEmpty(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId;
                return aid == "claude";
            })
            .ToList();

        SessionItem? activeRestarted = null;
        foreach (var s in allClaudeSessions)
        {
            try
            {
                // JS xterm 인스턴스 + ConPTY 모두 정리.
                // xterm 인스턴스를 재사용하면 composition-view 의 IME 조합 상태가
                // 새 ConPTY 와 동기화되지 않아 한글이 한 글자씩 깨져 보이는 문제가 발생.
                // 매번 새 xterm 인스턴스를 만들어 IME 상태를 완전히 초기화.
                _terminal.CloseTerminal(s.Id);
                DisposeSessionProcess(s, purge: false);
                TerminalSessionManager.Instance.ClearDisposedRoom(s.Id);
                // 새 세션을 미리 만들어둔다 — 사용자가 키를 누를 때
                // Get(roomId) 가 null 을 반환해 input 이 유실되는 race 방지.
                TerminalSessionManager.Instance.GetOrCreate(s.Id, 120, 30);
            }
            catch { /* ignore */ }
        }

        // JS 가 dispose 메시지를 완전히 처리할 시간 확보.
        // createTerm 이 dispose 중인 인스턴스와 충돌하지 않도록.
        await Task.Delay(150);

        // 활성 세션은 즉시 다시 띄움
        if (_activeSession != null && allClaudeSessions.Contains(_activeSession))
        {
            ActivateSession(_activeSession);
            activeRestarted = _activeSession;
        }

        // 비활성 세션은 ConPTY 만 정리된 상태 — 다음 탭 클릭 시 ActivateSession → GetOrCreate →
        // 새 ConPTY + resume 으로 복원. 여기서 미리 띄우지 않음 (리소스 낭비 + 사용자가 안 보는 세션).
    }

    /// <summary>테마 변경 적용 — 모든 세션의 ConPTY 를 종료한 뒤 현재 프로젝트 세션을 다시 불러온다.
    /// claude(settings.local.json)·opencode(tui.json) 모두 시작 시점에 새 테마를 읽으므로 재시작이 필요.
    /// 활성 세션은 즉시 띄우고(스피너), 옵션이 켜진 경우에만 같은 프로젝트의 나머지를 preload 한다.</summary>
    public async void ReloadAllSessionsForTheme()
    {
        var active = _activeSession;
        var proj   = _activeProject;

        // 1) 모든 프로젝트의 세션 종료 — JS xterm 인스턴스 + ConPTY 모두 정리(IME 상태까지 초기화).
        foreach (var s in _projects.SelectMany(p => p.Tabs).OfType<SessionItem>().ToList())
        {
            try
            {
                DisposeSessionProcess(s, purge: false); // CloseTerminal + DisposeRoom(매핑 보존)
                TerminalSessionManager.Instance.ClearDisposedRoom(s.Id);
            }
            catch { /* ignore */ }
        }

        // JS 가 dispose 메시지를 처리할 시간 확보 (createTerm 이 dispose 중 인스턴스와 충돌 방지).
        await Task.Delay(150);

        // 2) 현재 프로젝트 세션 재로드. 활성 세션은 즉시 표시, 전체 로드 옵션이 켜진 경우 나머지도 preload.
        if (proj != null)
        {
            if (active != null && proj.Tabs.Contains(active))
                ActivateSession(active);
            if (SettingsService.LoadPreloadAllProjectSessions())
                PreloadProjectSessions(proj, except: active);
        }
        // 다른 프로젝트 세션은 정리만 된 상태 — 해당 프로젝트 선택 시 새 테마로 재생성된다.
    }

    private ProjectItem? ParentOf(SessionItem session)
        => _projects.FirstOrDefault(p => p.Tabs.Contains(session));

    /// <summary>모든 탭(세션+파일)이 속한 프로젝트를 찾는다.</summary>
    private ProjectItem? ParentOfTab(TabItemBase tab)
        => _projects.FirstOrDefault(p => p.Tabs.Contains(tab));

    private SessionItem? FindSession(string id)
        => _projects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == id);

    // ── 탭 이벤트 ─────────────────────────────────────────────────
    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (_tabDidDrag) { _tabDidDrag = false; return; } // 드래그 직후 클릭 무시
        if (sender is FrameworkElement { DataContext: TabItemBase tab })
        {
            if (tab is SessionItem s) OpenSession(s);
            else if (tab is FileTabItem f) ActivateFileTab(f);
        }
    }

    /// <summary>탭 X = 탭 닫기.
    /// 세션 탭: 탭에서만 숨김(세션·터미널·기록은 그대로). 사이드바에서 다시 선택하면 복귀.
    /// 파일 탭: dirty 확인 후 제거(에디터 인스턴스도 함께 폐기).
    /// 활성 탭을 닫으면 다음 탭(세션 우선)으로 포커스를 옮겨 헤더가 비지 않게 한다.</summary>
    private void TabHide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabItemBase tab }) return;

        if (tab is SessionItem s)
        {
            s.Hidden = true;
            if (ReferenceEquals(_activeSession, s))
            {
                var parent = ParentOf(s);
                var next = parent?.Tabs.OfType<SessionItem>().FirstOrDefault(x => x != s && !x.Hidden);
                if (next != null) ActivateSession(next);
                else ClearActiveSession();
            }
        }
        else if (tab is FileTabItem f)
        {
            // FileEditorView.RequestClose 가 dirty 확인 후 CloseRequested 를 발생시킨다.
            // MainWindow 가 그 이벤트를 받아 RemoveFileTab 을 호출하도록 탭 생성 시 연결한다.
            f.Editor.RequestClose();
        }
    }

    // ── 탭 드래그 순서변경 (가로) + 사이드바 세션 순서 양방향 동기화 ──────────
    private Point _tabPressOrigin;
    private TabItemBase? _pendingTab;
    private ReorderDrag<TabItemBase>? _tabDrag;
    private bool _tabDidDrag;
    // 드래그 동안 숨겨둔 선택 탭 하단 받침(TabFoot*) — 드롭 후 트리거가 다시 그리도록 복원.
    private readonly List<FrameworkElement> _hiddenTabFeet = new();
    // 드래그 대상이 '선택 탭'일 때만 true — 이때만 seam을 숨겨 하단 보더가 이어지게 한다.
    // (비선택 탭 드래그 중에는 선택 탭의 seam을 그대로 두거나 displacement 따라가게 한다)
    private bool _tabDragHidSeam;

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabPressOrigin = e.GetPosition(TabsHost);
        _pendingTab = (sender as FrameworkElement)?.DataContext as TabItemBase;
        _tabDidDrag = false;
    }

    private void TabsHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDrag != null) { _tabDrag.Update(e); return; }
        if (e.LeftButton != MouseButtonState.Pressed || _pendingTab == null) return;
        var diff = _tabPressOrigin - e.GetPosition(TabsHost);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        TryStartTabDrag(_pendingTab);
    }

    private async Task EndTabDragAsync()
    {
        var td = _tabDrag;
        _tabDrag = null;
        _pendingTab = null;
        if (Mouse.Captured == TabsHost) TabsHost.ReleaseMouseCapture();
        RestoreTabFeet(); // 받침·seam 복원 (드롭 후 트리거가 다시 그리게)
        if (td != null) await td.FinishAsync(commit: true);
    }

    private void TryStartTabDrag(TabItemBase s)
    {
        var coll = _activeProject?.Tabs;
        if (coll == null) return;
        // 슬롯 이동 대상은 받침(TabFoot*)까지 포함하는 부모 TabRoot 로 잡아, 밀려나는 선택 탭이
        // displacement 될 때 하단 라운드 받침도 함께 따라오게 한다. ghost 캡처/숨김 대상은
        // 받침 없는 TabBd(Border) 로 따로 지정 (devez 정합 — ghost 가 받침까지 잡지 않도록).
        var rows = new List<(TabItemBase, FrameworkElement)>();
        FrameworkElement? sourceBorder = null;
        FrameworkElement? selectedRoot = null;
        foreach (var t in coll)
        {
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(t) is FrameworkElement fe
                && FindTabBorder(fe) is FrameworkElement border
                && VisualTreeHelper.GetParent(border) is FrameworkElement root)
            {
                rows.Add((t, root));
                if (ReferenceEquals(t, s)) sourceBorder = border;
                if (ReferenceEquals(t, _activeTab)) selectedRoot = root;
            }
        }
        if (sourceBorder == null || rows.Count < 2) return;

        _tabDrag = ReorderDrag<TabItemBase>.TryStart(TabsHost, rows, s, sourceBorder,
            (tab, hostTarget, _) =>
            {
                // 탭 = 활성 프로젝트의 Tabs(동일 컬렉션) → 여기서 옮기면 좌측 리스트도 함께 바뀐다(세션 한정).
                var c = _activeProject?.Tabs;
                if (c != null)
                {
                    int from = c.IndexOf(tab);
                    if (from >= 0)
                    {
                        int to = Math.Clamp(hostTarget, 0, c.Count - 1);
                        if (to != from) { c.Move(from, to); WorkspaceStore.Save(_projects); }
                    }
                }
                return Task.CompletedTask;
            },
            exactFollow: true, horizontal: true, ghostSource: sourceBorder);
        if (_tabDrag != null)
        {
            _tabDidDrag = true;
            TabsHost.CaptureMouse();
            HideTabFeet(sourceBorder);          // 소스 탭 받침 4개 Path 숨김 (ghost 가 잡지 않도록)
            SetupDragSeam(s, selectedRoot);     // seam 숨김(소스=선택) 또는 displacement 따라가기
        }
        _pendingTab = null;
    }

    // 드래그하는 선택 탭의 하단 받침(곡선 받침/외곽선)을 숨긴다. ghost 는 TabBd 만 들어올리고
    // 받침 Path 들은 형제라 제자리에 남아 떠 보이기 때문. 드롭 후 RestoreTabFeet 에서 로컬값을
    // 지워 IsSelected 트리거가 다시 그리게 한다 (devez HideTabFeet 정합).
    private void HideTabFeet(FrameworkElement sourceBorder)
    {
        RestoreTabFeet();
        var root = VisualTreeHelper.GetParent(sourceBorder);
        if (root == null) return;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement
                { Name: "TabFootLeftFill" or "TabFootLeftLine"
                     or "TabFootRightFill" or "TabFootRightLine" } foot)
            {
                foot.Visibility = Visibility.Collapsed;
                _hiddenTabFeet.Add(foot);
            }
        }
    }

    private void RestoreTabFeet()
    {
        foreach (var foot in _hiddenTabFeet)
            foot.ClearValue(UIElement.VisibilityProperty); // IsSelected 트리거가 다시 제어
        _hiddenTabFeet.Clear();
        _tabDragHidSeam = false;
        if (SelectedTabSeam != null) SelectedTabSeam.RenderTransform = null; // 공유했던 displacement 해제
        // 드롭 후 리스트 재정렬·레이아웃이 끝난 뒤 정확한 위치로 seam 을 다시 그린다.
        Dispatcher.InvokeAsync(UpdateSelectedTabSeam, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // 드래그 중 seam(선택 탭 밑 보더선 가림막) 처리. seam 은 ScrollViewer 밖 정적 오버레이라
    // 탭 본문/받침과 달리 displacement 를 자동으로 따라가지 못한다.
    //  · 선택 탭 자신을 드래그 → seam 숨김(하단 보더가 쭉 이어짐).
    //  · 비선택 탭 드래그 → 밀려나는 선택 탭의 TabRoot displacement TranslateTransform 을
    //    seam 의 RenderTransform 으로 공유해, 같은 애니메이션으로 함께 움직이게 한다.
    private void SetupDragSeam(TabItemBase source, FrameworkElement? selectedRoot)
    {
        if (SelectedTabSeam == null) return;
        _tabDragHidSeam = true; // 드래그 동안 UpdateSelectedTabSeam 재계산 차단(위치/표시 고정)
        bool draggingSelected = ReferenceEquals(source, _activeTab);
        if (draggingSelected || selectedRoot == null)
        {
            SelectedTabSeam.RenderTransform = null;
            SelectedTabSeam.Visibility = Visibility.Collapsed;
        }
        else
        {
            // seam 은 이미 선택 탭 rest 위치에 표시 중 — 그 위치 기준으로 같은 delta 만큼 따라가게 한다.
            SelectedTabSeam.RenderTransform = GetOrCreateTranslate(selectedRoot);
        }
    }

    // ReorderDrag.EnsureTranslate 와 동일한 규칙으로 요소의 TranslateTransform 인스턴스를 얻는다.
    // 같은 인스턴스를 seam 과 공유하면 ReorderDrag 의 displacement 애니메이션이 seam 에도 그대로 적용된다.
    private static TranslateTransform GetOrCreateTranslate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        if (el.RenderTransform is TransformGroup g)
        {
            var ex = g.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (ex != null) return ex;
            var added = new TranslateTransform();
            g.Children.Add(added);
            return added;
        }
        var nt = new TranslateTransform();
        if (el.RenderTransform != null && el.RenderTransform != Transform.Identity)
        {
            var grp = new TransformGroup();
            grp.Children.Add(el.RenderTransform);
            grp.Children.Add(nt);
            el.RenderTransform = grp;
        }
        else el.RenderTransform = nt;
        return nt;
    }

    private static FrameworkElement? FindTabBorder(DependencyObject root)
    {
        if (root is FrameworkElement { Name: "TabBd" } fe) return fe;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            if (FindTabBorder(VisualTreeHelper.GetChild(root, i)) is FrameworkElement found)
                return found;
        }
        return null;
    }

    /// <summary>사이드바 세션 순서 변경 — 탭은 같은 컬렉션이라 자동 반영되므로 영속만 한다.</summary>
    private void OnSidebarSessionsReordered(ProjectItem _) => WorkspaceStore.Save(_projects);

    // ── 탭 오버플로우: 좌·우 스크롤 버튼 + 가장자리 페이드 + 빈 영역 드래그 (devez 정합) ──────────
    private bool? _fadeLeft, _fadeRight;
    private double _fadeWidth = -1;
    private Action? _tabScrollAnimCancel;
    private const double TabScrollStep = 168; // 한 탭 단위(MinWidth 131 + 받침/여백)

    private void TabScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateTabOverflowButtons();
        UpdateSelectedTabSeam();
    }

    private void OnThemeChanged_UpdateSeam(string _) => Dispatcher.BeginInvoke(new Action(() =>
    {
        UpdateSelectedTabSeam();
        // 좌/우 패널 토글 아이콘 brush 재계산 (캡처된 instance 가 stale 되므로)
        UpdatePanelToggleVisual();
    }));

    /// <summary>선택 탭 하단 보더를 PanelBrush 로 덮어 세션 헤더와 경계선 없이 매끄럽게 잇는다
    /// (devez SelectedTabSeam — 스크롤뷰어 밖 정적 오버레이라 탭처럼 클립되지 않음).</summary>
    private void UpdateSelectedTabSeam()
    {
        if (SelectedTabSeam == null || TabBar == null || TabsHost == null) return;
        // 드래그 중에는 seam을 SetupDragSeam이 직접 제어(숨김/transform 공유)하므로 재계산하지 않는다.
        if (_tabDragHidSeam) return;
        if (_activeTab == null ||
            TabsHost.ItemContainerGenerator.ContainerFromItem(_activeTab) is not FrameworkElement container)
        {
            SelectedTabSeam.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var pt = container.TransformToAncestor(TabBar).Transform(new Point(0, 0));
            const double seamExtra = 9.15;                 // 받침 곡선 안쪽까지만 덮음
            double bodyWidth = container.ActualWidth - 3;  // 컨테이너 폭에 우측 3px 간격(Margin) 포함
            double seamWidth = bodyWidth + seamExtra * 2;
            double seamLeft  = pt.X - seamExtra;

            // 선택 탭이 좌측으로 숨으면 seam이 스크롤뷰어 경계를 넘어 왼쪽 패널까지 라인을 지우므로 좌측 클램프.
            double clipLeft = TabScroller.TransformToAncestor(TabBar).Transform(new Point(0, 0)).X;
            if (clipLeft < 0) clipLeft = 0;
            bool clampedLeft = false;
            if (seamLeft < clipLeft)
            {
                seamWidth -= clipLeft - seamLeft;
                seamLeft = clipLeft;
                clampedLeft = true; // viewport에 잘린 단면 → 페이드 없이 꽉 채워야 보더색이 안 비친다
            }
            if (seamWidth <= 0) { SelectedTabSeam.Visibility = Visibility.Collapsed; return; }

            SelectedTabSeam.Width = seamWidth;
            SelectedTabSeam.Margin = new Thickness(seamLeft, 0, 0, 0);

            // 양 끝을 PanelBrush→투명으로 페이드해 하단 라인이 딱 끊기지 않고 부드럽게 사라지게 한다.
            var panelColor = (FindResource("PanelBrush") as System.Windows.Media.SolidColorBrush)?.Color
                             ?? System.Windows.Media.Colors.Black;
            var clearColor = System.Windows.Media.Color.FromArgb(0, panelColor.R, panelColor.G, panelColor.B);
            const double fadePx = 4;
            double f = seamWidth > 0 ? Math.Min(0.45, fadePx / seamWidth) : 0;
            var brush = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint   = new Point(1, 0)
            };
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(clampedLeft ? panelColor : clearColor, 0));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(panelColor, clampedLeft ? 0 : f));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(panelColor, 1 - f));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(clearColor, 1));
            brush.Freeze();
            SelectedTabSeam.Background = brush;
            SelectedTabSeam.Visibility = Visibility.Visible;
        }
        catch { SelectedTabSeam.Visibility = Visibility.Collapsed; }
    }

    /// <summary>탭이 넘치면 좌·우 버튼을 띄우고, 양 끝 스크롤 가능 여부에 따라 활성/페이드를 갱신.</summary>
    private void UpdateTabOverflowButtons()
    {
        if (TabScroller == null || TabNavGroup == null) return;
        bool overflow = TabScroller.ScrollableWidth > 0.5;
        bool canLeft  = TabScroller.HorizontalOffset > 0.5;
        bool canRight = TabScroller.HorizontalOffset < TabScroller.ScrollableWidth - 0.5;

        TabNavGroup.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (TabScrollLeftBtn  != null) TabScrollLeftBtn.IsEnabled  = canLeft;
        if (TabScrollRightBtn != null) TabScrollRightBtn.IsEnabled = canRight;

        ApplyTabEdgeFade(canLeft, canRight);
    }

    /// <summary>잘리는 쪽 가장자리를 투명→불투명 그라데이션 OpacityMask로 페이드 (devez ApplyTabEdgeFade).</summary>
    private void ApplyTabEdgeFade(bool fadeLeft, bool fadeRight)
    {
        double w = TabScroller.ActualWidth;
        // 상태·폭이 같으면 재계산 안 함(매 프레임 재렌더 방지)
        if (_fadeLeft == fadeLeft && _fadeRight == fadeRight && Math.Abs(_fadeWidth - w) < 0.5) return;
        _fadeLeft = fadeLeft; _fadeRight = fadeRight; _fadeWidth = w;

        if (!fadeLeft && !fadeRight) { TabScroller.OpacityMask = null; return; }

        double f = Math.Min(0.10, 28 / Math.Max(1, w)); // 페이드 폭: 화면 10% 또는 28px
        var mask = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint   = new Point(1, 0)
        };
        var black = System.Windows.Media.Colors.Black;
        var clear = System.Windows.Media.Colors.Transparent;
        mask.GradientStops.Add(new System.Windows.Media.GradientStop(fadeLeft ? clear : black, 0));
        mask.GradientStops.Add(new System.Windows.Media.GradientStop(black, fadeLeft ? f : 0));
        mask.GradientStops.Add(new System.Windows.Media.GradientStop(black, fadeRight ? 1 - f : 1));
        mask.GradientStops.Add(new System.Windows.Media.GradientStop(fadeRight ? clear : black, 1));
        mask.Freeze();
        TabScroller.OpacityMask = mask;
    }

    private void TabScrollLeft_Click(object sender, RoutedEventArgs e)
        => AnimateTabScroll(TabScroller.HorizontalOffset - TabScrollStep);

    private void TabScrollRight_Click(object sender, RoutedEventArgs e)
        => AnimateTabScroll(TabScroller.HorizontalOffset + TabScrollStep);

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroller.ScrollableWidth <= 0.5) return; // 오버플로우 없으면 세로 휠 그대로 둠
        _tabScrollAnimCancel?.Invoke(); _tabScrollAnimCancel = null;
        TabScroller.ScrollToHorizontalOffset(
            Math.Clamp(TabScroller.HorizontalOffset - e.Delta, 0, TabScroller.ScrollableWidth));
        e.Handled = true;
    }

    /// <summary>가로 스크롤 부드러운 애니메이션 (devez SmoothScroll, 프레임 동기 + EaseInOut).</summary>
    private void AnimateTabScroll(double to)
    {
        to = Math.Clamp(to, 0, TabScroller.ScrollableWidth);
        _tabScrollAnimCancel?.Invoke();
        var from = TabScroller.HorizontalOffset;
        if (Math.Abs(to - from) < 0.5) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int dur = 240;
        bool cancelled = false;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (cancelled) { System.Windows.Media.CompositionTarget.Rendering -= handler!; return; }
            var t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)dur);
            TabScroller.ScrollToHorizontalOffset(from + (to - from) * EaseInOut(t));
            if (t >= 1.0) System.Windows.Media.CompositionTarget.Rendering -= handler!;
        };
        System.Windows.Media.CompositionTarget.Rendering += handler;
        _tabScrollAnimCancel = () => { if (cancelled) return; cancelled = true; System.Windows.Media.CompositionTarget.Rendering -= handler; };
    }

    /// <summary>탭 빈 영역 드래그로 창 이동 + 더블클릭 최대화/복원 (devez: 탭 바가 캡션 역할).</summary>
    private void TabStrip_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { DragMove(); } catch { /* 이미 캡처 중 등 */ }
    }

    /// <summary>선택 탭이 뷰포트 밖이면 부드럽게 스크롤해 보이게 한다 (devez EnsureSelectedTabVisible).</summary>
    private void EnsureSelectedTabVisible(TabItemBase tab)
    {
        if (TabScroller == null) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            UpdateSelectedTabSeam(); // 스크롤이 안 일어나도 seam은 갱신
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement fe) return;
            if (TabScroller.ScrollableWidth <= 0.5) return;
            var tl = fe.TransformToAncestor(TabScroller).Transform(new Point(0, 0));
            double left  = tl.X + TabScroller.HorizontalOffset;
            double right = left + fe.ActualWidth;
            const double margin = 30; // 양 끝 페이드(약 28px) 여유
            if (left < TabScroller.HorizontalOffset + margin)
                AnimateTabScroll(left - margin);
            else if (right > TabScroller.HorizontalOffset + TabScroller.ViewportWidth - margin)
                AnimateTabScroll(right - TabScroller.ViewportWidth + margin);
        }));
    }

    // ── 상태/빈 화면 ─────────────────────────────────────────────
    private void UpdateEmptyState()
    {
        bool hasActive = _activeTab != null;

        // 콘텐츠 호스트: 활성 탭 종류에 따라 터미널/에디터 중 하나만 표시.
        if (_activeTab is SessionItem)
        {
            TerminalHostContainer.Visibility = Visibility.Visible;
            FileEditorHostContainer.Visibility = Visibility.Collapsed;
        }
        else if (_activeTab is FileTabItem)
        {
            TerminalHostContainer.Visibility = Visibility.Collapsed;
            FileEditorHostContainer.Visibility = Visibility.Visible;
        }
        else
        {
            TerminalHostContainer.Visibility = Visibility.Collapsed;
            FileEditorHostContainer.Visibility = Visibility.Collapsed;
        }

        EmptyState.Visibility = hasActive ? Visibility.Collapsed : Visibility.Visible;

        // 세션 타이틀 영역 (devez HeaderBar): 활성 탭의 제목 표시
        SessionHeaderBar.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        if (hasActive)
        {
            if (_activeTab is SessionItem sess)
            {
                // 헤더엔 마지막 보낸 메시지만 표시(메시지 없으면 세션 이름). 폭 넘치면 …로 잘리고 호버 시 전체 툴팁.
                var msg = sess.LastMessage;
                var hasMsg = !string.IsNullOrEmpty(msg);
                SessionHeaderTitle.Text = hasMsg ? msg : sess.Name;
                SessionHeaderTitle.ToolTip = hasMsg ? msg : null;
                LastMessageSep.Visibility = hasMsg ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (_activeTab is FileTabItem file)
            {
                SessionHeaderTitle.Text = file.Title;
                SessionHeaderTitle.ToolTip = file.FilePath;
                LastMessageSep.Visibility = Visibility.Collapsed;
            }
        }
    }

    // 푸터 좌측 상태 텍스트는 제거됨(한도 표시로 대체). 호출부 유지를 위해 no-op 로 남긴다.
    private void UpdateStatus() { }

    // ── 설정창 ────────────────────────────────────────────────────
    // 테마 전환은 타이틀바 버튼에서 설정창(SettingsWindow) 안으로 이동했다.
    // (devez 이식) UserControl(SettingsDialog)을 Borderless Window(SettingsWindow)에 담아 띄운다.
    private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        await SuspendTerminalWithSnapshotAsync(blankCurtain: true); // 스냅샷 대신 테마색 빈 배경(테마 변경 시 색 추종)
        var dlg = new Views.SettingsWindow { Owner = this };
        dlg.Closed += (_, _) => ResumeTerminal();
        dlg.ShowDialog();
    }

    // ── MCP 서버 관리 (오버레이) ──────────────────────────────────
    // 설정창의 MCP 카테고리에서도 같은 창을 띄울 수 있다 — 직접 진입 단축.
    private async void McpBtn_Click(object sender, RoutedEventArgs e)
    {
        await SuspendTerminalWithSnapshotAsync();
        var dlg = new Views.McpManagerWindow { Owner = this };
        dlg.Closed += (_, _) => ResumeTerminal();
        dlg.ShowDialog();
    }

    // ── 파일 탭 (탭 시스템과 통합) ───────────────────────────────
    /// <summary>파일 탐색기에서 받은 텍스트 파일을 새 탭으로 연다.
    /// 같은 파일이 이미 열려 있으면 그 탭을 활성화한다(중복 생성 X).</summary>
    private void OpenFileAsTab(string path)
    {
        if (_activeProject == null) return; // 프로젝트 미선택 시 무시
        if (string.IsNullOrEmpty(path)) return;

        // 같은 경로의 탭이 이미 있으면 활성화만
        var existing = _activeProject.Tabs.OfType<FileTabItem>()
            .FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ActivateFileTab(existing);
            return;
        }

        var tab = new FileTabItem { FilePath = path, Editor = CreateFileTabEditor(path) };
        if (!tab.Editor.LoadFile(path))
        {
            // 로드 실패(파일 없음/5MB 초과) → 탭 만들지 않음
            return;
        }
        // 에디터의 닫기 요청 → 탭 제거로 라우팅
        tab.Editor.CloseRequested += (_, _) => RemoveFileTab(tab);
        _activeProject.Tabs.Add(tab);
        ActivateFileTab(tab);
    }

    private static IFileTabEditor CreateFileTabEditor(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".md" or ".markdown" ? new Views.MarkdownFileEditorView() : new Views.FileEditorView();
    }

    /// <summary>airspace 우회: 터미널 WebView2를 PNG 스냅샷으로 대체하고 Collapse.
    /// 오버레이(설정창 등)가 항상 맨 앞에 그려지는 WebView2 뒤로 묻히는 것을 막는다.</summary>
    private async Task SuspendTerminalWithSnapshotAsync(bool blankCurtain = false)
    {
        await FileExplorer.SuspendBrowserAsync(); // 우측 브라우저(WebView2)도 오버레이 뒤로 묻히지 않게 숨김
        if (_activeSession == null) return; // 터미널이 안 떠 있으면 불필요
        if (blankCurtain)
        {
            // 설정창: 스냅샷 대신 테마색 빈 배경. 테마를 바꾸면 BgBrush 가 갱신돼 색이 함께 바뀐다.
            TerminalCurtain.Visibility = Visibility.Visible;
        }
        else
        {
            var snap = await _terminal.CaptureSnapshotAsync();
            if (snap != null)
            {
                TerminalSnapshot.Source = snap;
                TerminalSnapshot.Visibility = Visibility.Visible;
            }
        }
        TerminalHostContainer.Visibility = Visibility.Collapsed;
    }

    /// <summary>오버레이가 닫힌 뒤 WebView2 터미널을 다시 표시하고 스냅샷/커튼을 제거.</summary>
    private void ResumeTerminal()
    {
        FileExplorer.ResumeBrowser();
        if (_activeSession != null)
            TerminalHostContainer.Visibility = Visibility.Visible;
        TerminalSnapshot.Visibility = Visibility.Collapsed;
        TerminalSnapshot.Source = null;
        TerminalCurtain.Visibility = Visibility.Collapsed;
    }

    /// <summary>우측 오버레이 드로어용 터미널 전용 정지(스냅샷). FileExplorer 자체가 오버레이
    /// 본문이므로 그 브라우저는 정지하지 않는다 — 터미널(중앙) WebView2 만 airspace 우회.</summary>
    private async Task SuspendTerminalOnlyAsync()
    {
        if (_activeSession == null) return;
        var snap = await _terminal.CaptureSnapshotAsync();
        if (snap != null)
        {
            TerminalSnapshot.Source = snap;
            TerminalSnapshot.Visibility = Visibility.Visible;
        }
        TerminalHostContainer.Visibility = Visibility.Collapsed;
    }

    /// <summary>우측 오버레이가 닫힌 뒤 터미널만 복원.</summary>
    private void ResumeTerminalOnly()
    {
        if (_activeSession != null)
            TerminalHostContainer.Visibility = Visibility.Visible;
        TerminalSnapshot.Visibility = Visibility.Collapsed;
        TerminalSnapshot.Source = null;
    }

    // ── 타이틀바 ──────────────────────────────────────────────────
    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // ── 최대화 처리 (WM_GETMINMAXINFO) ───────────────────────────────
    // WindowStyle=None 창은 기본 최대화 시 작업영역을 넘쳐 가장자리가 잘린다.
    // 마진으로 보정하던 방식(복원 시 우측 클리핑 발생)을 버리고, 최대화 크기/위치를
    // 모니터 작업영역에 정확히 맞춰 오버플로 자체를 없앤다(표준 해법). 마진 불필요.
    private const int WM_GETMINMAXINFO = 0x0024;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _mainHwnd = new WindowInteropHelper(this).Handle;
        if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(WndProc);
        EnableDwmTransitions(_mainHwnd); // 최대화/복원 시 DWM 부드러운 전환 활성화
        ApplyCornerPreference();          // 최대화 시 각진 모서리(둥근 모서리가 화면 모서리를 깎는 문제 방지)
        ApplyMaximizeMargin();            // 최대화 시 프레임 두께만큼 마진 보정(가장자리 잘림 방지)
        StateChanged += (_, _) => { ApplyCornerPreference(); ApplyMaximizeMargin(); };
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

    private const int SM_CXFRAME = 32, SM_CYFRAME = 33, SM_CXPADDEDBORDER = 92;
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    private IntPtr _mainHwnd;

    /// <summary>WS_CAPTION 부여로 최대화 애니메이션을 살리면 Win11 둥근 모서리가 최대화 화면
    /// 모서리를 깎는다. 최대화 상태에서만 각진 모서리(DONOTROUND)로 전환해 잘림을 막는다.</summary>
    private void ApplyCornerPreference()
    {
        if (_mainHwnd == IntPtr.Zero) return;
        int pref = WindowState == WindowState.Maximized ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
        DwmSetWindowAttribute(_mainHwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO) { WmGetMinMaxInfo(lParam); handled = true; }
        return IntPtr.Zero;
    }

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

    private void MaxBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

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
