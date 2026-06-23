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
    // 보관함 프로젝트(archived_at 있음) — 활성 목록과 분리 관리. WorkspaceStore 가 함께 영속.
    private readonly ObservableCollection<ProjectItem> _archivedProjects;
    // 중앙 워크스페이스 패널들(분할 시 2개). _focusedPane = 사이드바/파일탐색기/단축키가 향하는 패널.
    private readonly List<WorkspacePaneView> _panes = new();
    private WorkspacePaneView _focusedPane = null!;   // 생성자에서 PaneA 로 초기화
    private string? _explorerDir;                      // 우측 파일탐색기가 보고 있는 경로(중복 ShowDirectory 방지)
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
    // opencode — 플러그인이 busy\<room>.txt 에 저장한 처리중 상태를 감시해 스피너 연동 (claude busy hook 과 동일 패턴).
    private readonly OpenCodeBusyService _opencodeBusy = new();

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowPlacement();   // 마지막 창 위치/크기/최대화 복원 (없으면 CenterScreen 유지)

        _projects = WorkspaceStore.Load(out var archived);
        _archivedProjects = archived;
        Sidebar.Projects = _projects;
        Sidebar.ArchivedProjects = _archivedProjects;
        SetupPane(PaneA);
        SetupPane(PaneB);   // 분할 전엔 숨김(XAML Collapsed). 분할 시 노출.
        _focusedPane = PaneA;
        _ = MarkdownWysiwygHost.PrewarmAsync();

        Sidebar.AddProjectRequested    += AddProject;
        Sidebar.ProjectSelected        += SelectProject;
        Sidebar.AddSessionRequested    += AddSession;
        Sidebar.ProjectDeleteRequested += DeleteProject;
        Sidebar.ProjectRenameRequested += RenameProject;
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
                if (s != null) s.IsBusy = busy;
                if (!busy) foreach (var pane in _panes) pane.FlushPendingModelEffort(id); // 응답 종료 → 보류된 model/effort 적용
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
                s.LastMessage = msg;
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
                        s.LastMessage = msg;
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
                s.LastMessage = msg;
                foreach (var pane in _panes) pane.NotifySessionStateChanged(s);
            });

        // opencode — 플러그인이 떨군 busy 파일 감시 → 스피너 (claude 와 동일).
        _opencodeBusy.BusyChanged += (roomId, busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(roomId);
                if (s != null) s.IsBusy = busy;
                if (!busy) foreach (var pane in _panes) pane.FlushPendingModelEffort(roomId);
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
            Dispatcher.InvokeAsync(() => { var s = FindSession(roomId); if (s != null) s.IsBusy = busy; });
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
            _agentLastMsg.Start();
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
            _sessionBusy.Dispose();
            _modelEffort.Dispose();
            _sessionLastMsg.Dispose();
            _codexHook.Dispose();
            _opencodeLastMsg.Dispose();
            _opencodeBusy.Dispose();
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
        // WebView2(터미널/브라우저)는 HWND 라 WPF 오버레이를 가린다(airspace).
        // 설정 오버레이와 동일하게 스냅샷+커튼으로 suspend 해 WebView 를 치운 뒤 오버레이를 띄운다.
        try { await SuspendTerminalWithSnapshotAsync(blankCurtain: true); }
        catch { /* best effort */ }
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

    /// <summary>설정(프로젝트 정보 헤더 숨기기)을 모든 워크스페이스 패널의 메타바에 반영.</summary>
    public void ApplyProjectInfoHeaderVisibility()
    {
        foreach (var pane in _panes) pane.ApplyProjectInfoHeaderVisibility();
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
        if (ReferenceEquals(pane, PaneB)) PersistSplitState();
    }

    /// <summary>세션이 현재 활성인 패널 → 없으면 그 세션의 프로젝트를 보여주는 패널 → 없으면 포커스 패널.</summary>
    private WorkspacePaneView PaneFor(SessionItem s)
        => _panes.FirstOrDefault(p => ReferenceEquals(p.ActiveSession, s))
           ?? _panes.FirstOrDefault(p => p.ActiveProject != null && p.ActiveProject.Tabs.Contains(s))
           ?? _focusedPane;

    private void PersistSplitState()
        => SettingsService.SaveSplitState(_splitActive, PaneB.ActiveProject?.Path, PaneB.ActiveSession?.Id);

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

    /// <summary>중앙 패널 분할/해제 토글. 분할 시 패널 B 노출 후 두 번째 프로젝트를 자동으로 연다.</summary>
    private void OnPaneSplitToggle(WorkspacePaneView pane)
    {
        if (_splitActive) DisableSplit();
        else EnableSplit();
    }

    private void EnableSplit(ProjectItem? bProject = null, SessionItem? bSession = null)
    {
        if (_splitActive) return;
        _splitActive = true;

        PaneSplitterCol.Width = new GridLength(4);
        PaneBCol.Width = new GridLength(1, GridUnitType.Star);
        PaneSplitter.Visibility = Visibility.Visible;
        PaneB.Visibility = Visibility.Visible;

        foreach (var p in _panes) p.SetSplitActive(true);

        // 패널 B 포커스로 전환 → 이후 사이드바 클릭이 B 로 향한다.
        _focusedPane = PaneB;
        if (bSession != null) PaneB.OpenSession(bSession);
        else if (bProject != null) PaneB.SelectProject(bProject);
        else
        {
            // 패널 A 가 아닌 다른 프로젝트가 있으면 자동으로 B 에 연다(없으면 빈 패널).
            var other = _projects.FirstOrDefault(p => !ReferenceEquals(p, PaneA.ActiveProject));
            if (other != null) PaneB.SelectProject(other);
            else SyncShellToFocusedPane();
        }
        UpdatePaneFocusVisual();
        PersistSplitState();
    }

    /// <summary>시작 시 저장된 분할 상태 복원 — 패널 B 프로젝트/세션을 열고 포커스는 A 로 되돌린다.</summary>
    private void RestoreSplitState()
    {
        var (active, bProjPath, bSessId) = SettingsService.LoadSplitState();
        if (!active) return;

        var bProj = _projects.FirstOrDefault(p => p.Path == bProjPath);
        var bSess = bProj?.Tabs.OfType<SessionItem>().FirstOrDefault(s => s.Id == bSessId)
                    ?? _projects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == bSessId);
        EnableSplit(bProj, bSess);

        _focusedPane = PaneA;
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
    }

    private void DisableSplit()
    {
        if (!_splitActive) return;
        _splitActive = false;

        // 포커스를 먼저 A 로 옮긴 뒤 B 를 정리해야 B 비우기가 셸의 last-active 를 건드리지 않는다.
        _focusedPane = PaneA;
        // 패널 B 의 세션/터미널 배선을 끊고(컬렉션·ConPTY·기록은 보존) 숨긴다.
        PaneB.ClearForHide();
        PaneB.Visibility = Visibility.Collapsed;
        PaneSplitter.Visibility = Visibility.Collapsed;
        PaneBCol.Width = new GridLength(0);
        PaneSplitterCol.Width = new GridLength(0);
        PaneACol.Width = new GridLength(1, GridUnitType.Star);

        foreach (var p in _panes) p.SetSplitActive(false);
        SyncShellToFocusedPane();
        UpdatePaneFocusVisual();
        PersistSplitState();
    }

    /// <summary>분할 중일 때 포커스 패널을 시각적으로 표시(상단 액센트). 단일 패널이면 표시 안 함.</summary>
    private void UpdatePaneFocusVisual()
    {
        foreach (var p in _panes)
            p.SetFocusedVisual(_splitActive && ReferenceEquals(p, _focusedPane));
    }

    private SessionItem? FindSession(string id)
        => _projects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == id);

    // ── 사이드바 액션 → 포커스 패널로 위임 ────────────────────────────
    private void SelectProject(ProjectItem proj) => _focusedPane.SelectProject(proj);
    private void OpenSession(SessionItem session) => _focusedPane.OpenSession(session);
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

    /// <summary>테마 변경 시 좌·우 패널 토글 아이콘 brush 재계산(seam 은 각 패널이 자체 처리).</summary>
    private void OnThemeChanged_UpdatePanels(string _) => Dispatcher.BeginInvoke(new Action(UpdatePanelToggleVisual));

    // ── 설정창 / MCP (오버레이) ───────────────────────────────────────
    private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        await SuspendTerminalWithSnapshotAsync(blankCurtain: true);
        var dlg = new Views.SettingsWindow { Owner = this };
        dlg.Closed += (_, _) => ResumeTerminal();
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
