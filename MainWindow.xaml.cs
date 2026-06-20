using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;
using DevezCode.Services.Terminal;
using DevezCode.Views;

namespace DevezCode;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProjectItem> _projects;
    private readonly TerminalHostView _terminal = new();
    private ProjectItem? _activeProject;   // 중앙 탭 = 이 프로젝트의 Sessions
    private SessionItem? _activeSession;
    private readonly PerfMonitorService _perfMonitor = new();
    private readonly StatusLineService _statusLine = new();
    private readonly SessionBusyService _sessionBusy = new();
    private readonly SessionLastMessageService _sessionLastMsg = new();

    /// <summary>현재 로딩 스피너를 띄운 세션. claude 화면이 뜨거나(=TerminalReady) 타임아웃에 해제.</summary>
    private string? _loadingRoomId;
    private System.Windows.Threading.DispatcherTimer? _loadingTimeout;

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowPlacement();   // 마지막 창 위치/크기/최대화 복원 (없으면 CenterScreen 유지)
        StateChanged += (_, _) => UpdateMaximizeMargin();

        _projects = WorkspaceStore.Load();
        Sidebar.Projects = _projects;
        TerminalHostContainer.Content = _terminal;

        Sidebar.AddProjectRequested    += AddProject;
        Sidebar.ProjectSelected        += SelectProject;
        Sidebar.AddSessionRequested    += AddSession;
        Sidebar.ProjectDeleteRequested += DeleteProject;
        Sidebar.ProjectIconChangeRequested += ChangeProjectIcon;
        Sidebar.ProjectsReordered += () => WorkspaceStore.Save(_projects);
        Sidebar.SessionsReordered += OnSidebarSessionsReordered;
        Sidebar.SessionSelected        += OpenSession;
        Sidebar.SessionDeleteRequested += DeleteSession;
        Sidebar.SessionRenameRequested += RenameSession;
        Sidebar.SessionStopTrackingRequested += StopTrackingSession;

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
            Dispatcher.InvokeAsync(() => { var s = FindSession(id); if (s != null) s.IsBusy = busy; });

        // 마지막 보낸 메시지: busy 훅이 떨군 lastmsg 파일을 감시 → 세션에 반영(헤더 부제 라이브 갱신).
        _sessionLastMsg.MessageChanged += (id, msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                var s = FindSession(id);
                if (s == null) return;
                s.LastMessage = msg;
                if (ReferenceEquals(s, _activeSession)) UpdateEmptyState(); // 활성 세션이면 헤더 즉시 갱신
            });

        // 테마 변경 시 선택 탭 seam 색(PanelBrush)을 재계산(frozen brush라 자동 갱신 안 됨)
        App.ThemeChanged += OnThemeChanged_UpdateSeam;

        // 파일 탐색기에서 텍스트 파일 더블클릭 → 중앙 분할 편집기 패널 열기 / 닫기 시 접기
        FileExplorer.FileOpenRequested += (_, path) => OpenFileInEditor(path);
        FileEditor.CloseRequested += (_, _) => CloseEditor();

        UpdateEmptyState();
        UpdateStatus();
        RestorePanelStates();

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
            StartStatusLine();
            _sessionBusy.Start();
            _sessionLastMsg.Start();
            RestoreLastSession();
            CheckHookSetup(); // 훅 미설치/구버전이면 상단 배너로 원클릭 설정 안내
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
            _sessionLastMsg.Dispose();
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
                          .Sessions.FirstOrDefault(s => s.Id == sessionId)
                      ?? _projects.SelectMany(p => p.Sessions).FirstOrDefault(s => s.Id == sessionId);
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

    /// <summary>설정(로컬, SettingsService)에 따라 성능 모니터 칩(푸터) 표시/숨김.</summary>
    private void ApplyPerfMonitorVisibility()
    {
        PerfChipGroup.Visibility =
            SettingsService.LoadShowPerfMonitorBar() ? Visibility.Visible : Visibility.Collapsed;
        UpdateFooterDivider();
    }

    /// <summary>푸터 구분선(|)은 CPU/RAM 칩과 한도 표시가 둘 다 보일 때만 노출.</summary>
    private void UpdateFooterDivider()
        => FooterDivider.Visibility =
            (PerfChipGroup.Visibility == Visibility.Visible && RateLimitPanel.Visibility == Visibility.Visible)
            ? Visibility.Visible : Visibility.Collapsed;

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

    // ── 좌·우 패널 접기/펼치기 (devez 이식: 프레임 동기 폭 애니메이션) ──────
    // 접을 때 폭을 기억해 두고 컬럼을 0으로 줄였다가, 펼칠 때 복원한다.
    // 접힘 상태는 아이콘 stroke 를 강조(Primary)해 표시. 펼침 EaseInOut / 접힘 EaseIn.
    private bool   _leftCollapsed;
    private bool   _rightCollapsed;
    private double _sidebarWidth = 262;
    private double _fileExpWidth = 300;
    private Action? _leftAnimCancel;
    private Action? _rightAnimCancel;

    private void LeftPanelBtn_Click(object sender, RoutedEventArgs e)
    {
        _leftAnimCancel?.Invoke();
        if (_leftCollapsed)
        {
            _leftCollapsed = false;
            Sidebar.Visibility = Visibility.Visible;
            SidebarSplitterCol.Width = new GridLength(4);
            SidebarCol.Width = new GridLength(0);
            _leftAnimCancel = AnimateColumn(SidebarCol, _sidebarWidth, 260, easeIn: false,
                onComplete: () => SidebarCol.MinWidth = 190, cacheTarget: Sidebar);
        }
        else
        {
            _leftCollapsed = true;
            _sidebarWidth = SidebarCol.Width.IsAbsolute ? SidebarCol.Width.Value : SidebarCol.ActualWidth;
            SidebarCol.MinWidth = 0;
            SidebarSplitterCol.Width = new GridLength(0);
            _leftAnimCancel = AnimateColumn(SidebarCol, 0, 220, easeIn: true,
                onComplete: () => Sidebar.Visibility = Visibility.Collapsed, cacheTarget: Sidebar);
        }
        SettingsService.SaveLeftPanel(_leftCollapsed, _sidebarWidth);
        UpdatePanelToggleVisual();
    }

    private void RightPanelBtn_Click(object sender, RoutedEventArgs e)
    {
        _rightAnimCancel?.Invoke();
        if (_rightCollapsed)
        {
            _rightCollapsed = false;
            FileExplorer.Visibility = Visibility.Visible;
            FileExpSplitterCol.Width = new GridLength(4);
            FileExpCol.Width = new GridLength(0);
            _rightAnimCancel = AnimateColumn(FileExpCol, _fileExpWidth, 260, easeIn: false,
                onComplete: () => FileExpCol.MinWidth = 200, cacheTarget: FileExplorer);

            // 접기 전 열려 있던 파일 뷰도 함께 복원(폭 확장)
            if (_editorOpenBeforeCollapse)
            {
                _editorOpenBeforeCollapse = false;
                RestoreEditorColumn();
            }
        }
        else
        {
            _rightCollapsed = true;
            _fileExpWidth = FileExpCol.Width.IsAbsolute ? FileExpCol.Width.Value : FileExpCol.ActualWidth;
            FileExpCol.MinWidth = 0;
            FileExpSplitterCol.Width = new GridLength(0);
            _rightAnimCancel = AnimateColumn(FileExpCol, 0, 220, easeIn: true,
                onComplete: () => FileExplorer.Visibility = Visibility.Collapsed, cacheTarget: FileExplorer);

            // 파일 뷰가 열려 있으면 같이 숨김(내용 보존 — 펼칠 때 복원)
            _editorOpenBeforeCollapse = FileEditor.IsOpen && EditorColIsOpen();
            if (_editorOpenBeforeCollapse) HideEditorColumn();
        }
        SettingsService.SaveRightPanel(_rightCollapsed, _fileExpWidth);
        UpdatePanelToggleVisual();
    }

    /// <summary>저장된 패널 접힘 상태를 시작 시 즉시(애니메이션 없이) 복원한다.</summary>
    private void RestorePanelStates()
    {
        _sidebarWidth = SettingsService.LoadSidebarWidth();
        _fileExpWidth = SettingsService.LoadFileExpWidth();

        if (SettingsService.LoadLeftPanelCollapsed())
        {
            _leftCollapsed = true;
            SidebarCol.MinWidth = 0;
            SidebarCol.Width = new GridLength(0);
            SidebarSplitterCol.Width = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
        }
        if (SettingsService.LoadRightPanelCollapsed())
        {
            _rightCollapsed = true;
            FileExpCol.MinWidth = 0;
            FileExpCol.Width = new GridLength(0);
            FileExpSplitterCol.Width = new GridLength(0);
            FileExplorer.Visibility = Visibility.Collapsed;
        }
        UpdatePanelToggleVisual();
    }

    private void UpdatePanelToggleVisual()
    {
        var muted   = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        var primary = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
        LeftPanelIcon.Stroke  = _leftCollapsed  ? primary : muted;
        RightPanelIcon.Stroke = _rightCollapsed ? primary : muted;
    }

    // 프레임 동기(CompositionTarget.Rendering) 컬럼 폭 애니메이션. DispatcherTimer 는
    // 프레임 클럭과 어긋나 끊김이 생겨, devez 처럼 렌더 펄스에 맞춰 갱신한다.
    private static Action AnimateColumn(ColumnDefinition col, double toWidth, int durationMs,
        bool easeIn, Action? onComplete = null, UIElement? cacheTarget = null)
    {
        var from = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool cancelled = false;

        if (cacheTarget != null)
            cacheTarget.CacheMode = new System.Windows.Media.BitmapCache();

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
            col.Width = new GridLength(from + (toWidth - from) * easedT);
            if (t >= 1.0)
            {
                System.Windows.Media.CompositionTarget.Rendering -= handler!;
                col.Width = new GridLength(toWidth);
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

        await ApplyUpdateAsync(info);
    }

    private async Task ApplyUpdateAsync(UpdateInfo info)
    {
        _updateInProgress = true;
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
        // 프로젝트 연결 시 기본 세션 1개 자동 생성
        var session = new SessionItem { Name = "세션 1" };
        proj.Sessions.Add(session);
        SettingsService.SaveClaudeCodeRoomDir(session.Id, proj.Path);
        _projects.Add(proj);
        WorkspaceStore.Save(_projects);
        SelectProject(proj);   // 탭을 이 프로젝트 세션으로 교체 + 기본 세션 활성화
        UpdateStatus();
    }

    /// <summary>활성 프로젝트 전환 — 중앙 탭을 그 프로젝트의 세션들로 교체(같은 컬렉션 바인딩). 세션 활성화는 안 함.
    /// 프로젝트 재선택 시 탭 X 로 숨겼던 세션들을 모두 다시 보이게 한다(임시 뷰 상태 리셋).</summary>
    private void SetActiveProject(ProjectItem proj)
    {
        _activeProject = proj;
        foreach (var p in _projects) p.IsSelected = ReferenceEquals(p, proj);
        foreach (var s in proj.Sessions) s.Hidden = false;
        TabsHost.ItemsSource = proj.Sessions;
        FileExplorer.ShowDirectory(proj.Path);
    }

    /// <summary>프로젝트 선택(행 클릭) — 탭 교체 후 세션 하나 활성화(이전 활성 or 첫 세션).</summary>
    private void SelectProject(ProjectItem proj)
    {
        SetActiveProject(proj);
        var target = (_activeSession != null && proj.Sessions.Contains(_activeSession))
            ? _activeSession
            : proj.Sessions.FirstOrDefault();
        if (target != null) ActivateSession(target);
        else ClearActiveSession();
        // 나머지 세션은 미리 띄우지 않는다(과거엔 모두 백그라운드 spawn → 프로젝트 선택 시 CPU 폭증).
        // 마지막 보던 세션 하나만 활성화하고, 다른 세션은 사용자가 탭/사이드바에서 클릭할 때 lazy 생성된다.
    }

    private void ChangeProjectIcon(ProjectItem proj)
    {
        var result = IconPickerDialog.Show(proj.IconKey, proj.IconColor, proj.Name);
        if (result is not { } r) return;
        proj.IconKey = r.Icon ?? "IconBox";
        proj.IconColor = r.Color;
        WorkspaceStore.Save(_projects);
    }

    private void DeleteProject(ProjectItem proj)
    {
        if (!ConfirmDialog.Show("프로젝트 제거",
                $"'{proj.Name}' 프로젝트를 목록에서 제거할까요?\n(디스크의 실제 파일은 삭제되지 않습니다.)",
                okLabel: "제거", danger: true))
            return;

        foreach (var s in proj.Sessions.ToList()) DisposeSessionProcess(s, purge: false);
        _projects.Remove(proj);
        WorkspaceStore.Save(_projects);

        if (ReferenceEquals(_activeProject, proj))
        {
            _activeProject = null;
            _activeSession = null;
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
        if (proj == null || proj.Sessions.Count == 0) return;
        int i = index < 0 ? proj.Sessions.Count - 1 : index;
        if (i < 0 || i >= proj.Sessions.Count) return; // WT: 없는 탭 번호면 아무 동작 안 함
        OpenSession(proj.Sessions[i]);
    }

    /// <summary>활성 프로젝트 내 세션을 순환 전환 (Ctrl+Tab / Ctrl+Shift+Tab).</summary>
    private void CycleSession(int dir)
    {
        var proj = _activeProject;
        if (proj == null || _activeSession == null || proj.Sessions.Count < 2) return;
        int idx = proj.Sessions.IndexOf(_activeSession);
        if (idx < 0) return;
        int n = proj.Sessions.Count;
        OpenSession(proj.Sessions[((idx + dir) % n + n) % n]);
    }

    /// <summary>"세션 N" 다음 번호를 만든다 — 기존 세션 이름에서 최대 N을 찾아 +1.
    /// Count+1 방식과 달리 중간 세션을 삭제해도 번호가 겹치지 않는다.</summary>
    private static string NextSessionName(ProjectItem proj)
    {
        int max = 0;
        var rx = new System.Text.RegularExpressions.Regex(@"^세션\s+(\d+)$");
        foreach (var s in proj.Sessions)
        {
            var m = rx.Match(s.Name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
        }
        return $"세션 {max + 1}";
    }

    private void AddSession(ProjectItem proj)
    {
        var session = new SessionItem { Name = NextSessionName(proj) };
        proj.Sessions.Add(session);
        proj.IsExpanded = true;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, proj.Path);
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

    /// <summary>세션 활성화 — 선택 표시 + 중앙 터미널 표시(claude 실행).</summary>
    private void ActivateSession(SessionItem session)
    {
        var parent = ParentOf(session);
        if (parent == null) return;

        // claude 가 항상 프로젝트 디렉터리에서 실행되도록 매핑 보장
        SettingsService.SaveClaudeCodeRoomDir(session.Id, parent.Path);

        _activeSession = session;
        foreach (var p in _projects)
            foreach (var s in p.Sessions)
                s.IsSelected = ReferenceEquals(s, session);

        session.IsAlive = true; // 낙관적 — 실패 시 SessionExited 이벤트로 회색
        // 마지막 활성 세션 즉시 저장 → 강제 종료/크래시 후 재시작에도 이 세션으로 복원
        SettingsService.SaveLastActive(parent.Path, session.Id);
        // 세션 실행은 훅 자산을 최신으로 재생성하므로(있던 배너는 더 이상 불필요) 배너를 닫는다.
        if (HookSetupBanner.Visibility == Visibility.Visible) HookSetupBanner.Visibility = Visibility.Collapsed;
        // 이미 claude 화면이 떠 있는 세션이면 로딩 없이, 아니면 스피너 표시 후 ShowTerminal.
        if (_terminal.IsReady(session.Id)) HideSessionLoading();
        else ShowSessionLoading(session.Id);
        _terminal.ShowTerminal(session.Id);
        _terminal.FocusTerminal();
        UpdateEmptyState();
        EnsureSelectedTabVisible(session); // 선택 탭이 가려져 있으면 보이게 스크롤
    }

    /// <summary>세션 로딩 스피너 표시 — claude 화면이 뜰 때까지. 안전장치로 일정 시간 후 자동 해제.</summary>
    private void ShowSessionLoading(string roomId)
    {
        _loadingRoomId = roomId;
        TerminalLoadingOverlay.Visibility = Visibility.Visible;

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
    }

    /// <summary>해당 방이 현재 로딩 중이던 세션이면 스피너 숨김.</summary>
    private void HideSessionLoadingIf(string roomId)
    {
        if (_loadingRoomId == roomId) HideSessionLoading();
    }

    private void ClearActiveSession()
    {
        _activeSession = null;
        HideSessionLoading();
        foreach (var p in _projects)
            foreach (var s in p.Sessions)
                s.IsSelected = false;
        UpdateEmptyState();
        UpdateSelectedTabSeam(); // 활성 세션 없음 → seam 숨김
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
        int idx = parent?.Sessions.IndexOf(session) ?? -1;

        DisposeSessionProcess(session, purge);
        parent?.Sessions.Remove(session);
        WorkspaceStore.Save(_projects);

        if (wasActive)
        {
            SessionItem? next = null;
            if (parent != null && parent.Sessions.Count > 0)
                next = parent.Sessions[Math.Min(idx, parent.Sessions.Count - 1)];
            if (next != null) ActivateSession(next);
            else ClearActiveSession();
        }
        UpdateStatus();
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
            else       TerminalSessionManager.Instance.DisposeRoom(session.Id);
        }
        catch { /* ignore */ }
        SettingsService.RemoveClaudeCodeRoomDir(session.Id);
    }

    private ProjectItem? ParentOf(SessionItem session)
        => _projects.FirstOrDefault(p => p.Sessions.Contains(session));

    private SessionItem? FindSession(string id)
        => _projects.SelectMany(p => p.Sessions).FirstOrDefault(s => s.Id == id);

    // ── 탭 이벤트 ─────────────────────────────────────────────────
    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (_tabDidDrag) { _tabDidDrag = false; return; } // 드래그 직후 클릭 무시
        if (sender is FrameworkElement { DataContext: SessionItem s }) OpenSession(s);
    }

    /// <summary>탭 X = 탭에서만 숨김(세션·터미널·기록은 그대로). 프로젝트 재선택 시 자동 복귀.
    /// 활성 세션 탭을 숨기면 다음 세션으로 포커스를 옮겨 헤더가 비지 않게 한다.</summary>
    private void TabHide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionItem s }) return;
        s.Hidden = true;
        // 숨긴 탭이 활성 세션이면 다른 세션으로 포커스 이동 (없으면 헤더만 비움)
        if (ReferenceEquals(_activeSession, s))
        {
            var parent = ParentOf(s);
            var next = parent?.Sessions.FirstOrDefault(x => x != s && !x.Hidden);
            if (next != null) ActivateSession(next);
            else ClearActiveSession();
        }
    }

    // ── 세션 헤더 액션 버튼: 우클릭 메뉴와 동일 동작 (추적 중지 / 종료) ──
    private void SessionHeaderStopTracking_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession != null) StopTrackingSession(_activeSession);
    }

    private void SessionHeaderDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession != null) DeleteSession(_activeSession);
    }

    // ── 탭 드래그 순서변경 (가로) + 사이드바 세션 순서 양방향 동기화 ──────────
    private Point _tabPressOrigin;
    private SessionItem? _pendingTab;
    private ReorderDrag<SessionItem>? _tabDrag;
    private bool _tabDidDrag;

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabPressOrigin = e.GetPosition(TabsHost);
        _pendingTab = (sender as FrameworkElement)?.DataContext as SessionItem;
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
        if (td != null) await td.FinishAsync(commit: true);
    }

    private void TryStartTabDrag(SessionItem s)
    {
        var coll = _activeProject?.Sessions;
        if (coll == null) return;
        var rows = new List<(SessionItem, FrameworkElement)>();
        foreach (var t in coll)
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(t) is FrameworkElement fe)
                rows.Add((t, fe));
        var src = rows.FirstOrDefault(r => ReferenceEquals(r.Item1, s));
        if (src.Item2 == null) return;

        _tabDrag = ReorderDrag<SessionItem>.TryStart(TabsHost, rows, s, src.Item2,
            (sess, hostTarget, _) =>
            {
                // 탭 = 활성 프로젝트의 Sessions(동일 컬렉션) → 여기서 옮기면 좌측 리스트도 함께 바뀐다.
                var c = _activeProject?.Sessions;
                if (c != null)
                {
                    int from = c.IndexOf(sess);
                    if (from >= 0)
                    {
                        int to = Math.Clamp(hostTarget, 0, c.Count - 1);
                        if (to != from) { c.Move(from, to); WorkspaceStore.Save(_projects); }
                    }
                }
                return Task.CompletedTask;
            },
            exactFollow: true, horizontal: true);
        if (_tabDrag != null) { _tabDidDrag = true; TabsHost.CaptureMouse(); }
        _pendingTab = null;
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

    private void OnThemeChanged_UpdateSeam(string _) => Dispatcher.BeginInvoke(new Action(UpdateSelectedTabSeam));

    /// <summary>선택 탭 하단 보더를 PanelBrush 로 덮어 세션 헤더와 경계선 없이 매끄럽게 잇는다
    /// (devez SelectedTabSeam — 스크롤뷰어 밖 정적 오버레이라 탭처럼 클립되지 않음).</summary>
    private void UpdateSelectedTabSeam()
    {
        if (SelectedTabSeam == null || TabBar == null || TabsHost == null) return;
        if (_tabDrag != null) return; // 드래그 중에는 컨테이너 transform이 이동 중이라 재계산하지 않음
        if (_activeSession == null ||
            TabsHost.ItemContainerGenerator.ContainerFromItem(_activeSession) is not FrameworkElement container)
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
    private void EnsureSelectedTabVisible(SessionItem session)
    {
        if (TabScroller == null) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            UpdateSelectedTabSeam(); // 스크롤이 안 일어나도 seam은 갱신
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(session) is not FrameworkElement fe) return;
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
        bool hasActive = _activeSession != null;
        EmptyState.Visibility = hasActive ? Visibility.Collapsed : Visibility.Visible;
        TerminalHostContainer.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;

        // 세션 타이틀 영역 (devez HeaderBar): 활성 세션의 제목 + 프로젝트 경로 표시
        SessionHeaderBar.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        if (hasActive)
        {
            // 헤더엔 마지막 보낸 메시지만 표시(메시지 없으면 세션 이름). 폭 넘치면 …로 잘리고 호버 시 전체 툴팁.
            var msg = _activeSession!.LastMessage;
            var text = string.IsNullOrEmpty(msg) ? _activeSession.Name : msg;
            SessionHeaderTitle.Text = text;
            SessionHeaderTitle.ToolTip = string.IsNullOrEmpty(msg) ? null : msg;
        }
    }

    // 푸터 좌측 상태 텍스트는 제거됨(한도 표시로 대체). 호출부 유지를 위해 no-op 로 남긴다.
    private void UpdateStatus() { }

    // ── 설정창 ────────────────────────────────────────────────────
    // 테마 전환은 타이틀바 버튼에서 설정창(SettingsDialog) 안으로 이동했다.
    private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        await SuspendTerminalWithSnapshotAsync(); // 오버레이가 WebView2 뒤로 묻히지 않게 가림
        var dlg = new Views.SettingsDialog();
        dlg.CloseRequested += (_, _) =>
        {
            OverlayHost.Children.Remove(dlg);
            ResumeTerminal();
        };
        // 라이브 미리보기: 설정창 저장 전이라도 토글 값(on)대로 칩을 즉시 보였다/숨긴다.
        dlg.PerfMonitorBarChanged += (_, on) =>
            PerfChipGroup.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        OverlayHost.Children.Add(dlg);
    }

    // ── 인앱 파일 편집기 (중앙 분할 패널) ──────────────────────────
    // 편집기는 터미널과 나란히 별도 열(EditorCol)에 위치하므로 WebView2 airspace 문제는 없다.
    // 펼침/접힘은 좌·우 패널과 동일하게 AnimateColumn 으로 컬럼 폭을 애니메이트한다.
    private Action? _editorAnimCancel;
    private double _editorWidth;
    // 오른쪽 패널을 접을 때 파일 뷰가 열려 있었는지 — 펼칠 때 같이 복원하기 위함.
    private bool _editorOpenBeforeCollapse;

    private bool EditorColIsOpen() =>
        EditorCol.Width.IsAbsolute ? EditorCol.Width.Value > 0 : EditorCol.ActualWidth > 0;

    /// <summary>파일 뷰를 폭 0으로 접되 내용·IsOpen 은 유지(우측 패널 종속 숨김).</summary>
    private void HideEditorColumn()
    {
        _editorAnimCancel?.Invoke();
        _editorWidth = EditorCol.Width.IsAbsolute ? EditorCol.Width.Value : EditorCol.ActualWidth;
        EditorCol.MinWidth = 0;
        EditorSplitterCol.Width = new GridLength(0);
        _editorAnimCancel = AnimateColumn(EditorCol, 0, 220, easeIn: true,
            onComplete: () => FileEditor.Visibility = Visibility.Collapsed, cacheTarget: FileEditor);
    }

    /// <summary>종속 숨김했던 파일 뷰를 다시 펼친다(폭 확장).</summary>
    private void RestoreEditorColumn()
    {
        _editorAnimCancel?.Invoke();
        FileEditor.Visibility = Visibility.Visible;
        EditorSplitterCol.Width = new GridLength(4);
        EditorCol.Width = new GridLength(0);
        double target = _editorWidth > 0 ? _editorWidth : Math.Max(280, CenterArea.ActualWidth * 0.5);
        _editorAnimCancel = AnimateColumn(EditorCol, target, 260, easeIn: false,
            onComplete: () => EditorCol.MinWidth = 240, cacheTarget: FileEditor);
    }

    /// <summary>파일 탐색기에서 받은 텍스트 파일을 중앙 분할 편집기 패널로 연다.</summary>
    private void OpenFileInEditor(string path)
    {
        bool wasOpen = FileEditor.IsOpen;
        if (!FileEditor.Open(path)) return; // 로드 실패(대용량·확인 취소 등)
        if (wasOpen) return;                // 이미 열려 있으면 내용만 교체

        // 우측 패널 목표폭 = 중앙 영역의 절반(좌측 터미널 MinWidth 280은 그리드가 보장)
        _editorWidth = Math.Max(280, CenterArea.ActualWidth * 0.5);
        _editorAnimCancel?.Invoke();
        EditorSplitterCol.Width = new GridLength(4);
        EditorCol.MinWidth = 0;
        EditorCol.Width = new GridLength(0);
        _editorAnimCancel = AnimateColumn(EditorCol, _editorWidth, 260, easeIn: false,
            onComplete: () => EditorCol.MinWidth = 240, cacheTarget: FileEditor);
    }

    /// <summary>편집기 패널을 접고(폭 0 애니메이션) 내용을 비운다.</summary>
    private void CloseEditor()
    {
        _editorAnimCancel?.Invoke();
        EditorCol.MinWidth = 0;
        EditorSplitterCol.Width = new GridLength(0);
        _editorAnimCancel = AnimateColumn(EditorCol, 0, 220, easeIn: true,
            onComplete: () => FileEditor.Reset(), cacheTarget: FileEditor);
    }

    /// <summary>airspace 우회: 터미널 WebView2를 PNG 스냅샷으로 대체하고 Collapse.
    /// 오버레이(설정창 등)가 항상 맨 앞에 그려지는 WebView2 뒤로 묻히는 것을 막는다.</summary>
    private async Task SuspendTerminalWithSnapshotAsync()
    {
        await FileExplorer.SuspendBrowserAsync(); // 우측 브라우저(WebView2)도 오버레이 뒤로 묻히지 않게 숨김
        if (_activeSession == null) return; // 터미널이 안 떠 있으면 불필요
        var snap = await _terminal.CaptureSnapshotAsync();
        if (snap != null)
        {
            TerminalSnapshot.Source = snap;
            TerminalSnapshot.Visibility = Visibility.Visible;
        }
        TerminalHostContainer.Visibility = Visibility.Collapsed;
    }

    /// <summary>오버레이가 닫힌 뒤 WebView2 터미널을 다시 표시하고 스냅샷을 제거.</summary>
    private void ResumeTerminal()
    {
        FileExplorer.ResumeBrowser();
        if (_activeSession != null)
            TerminalHostContainer.Visibility = Visibility.Visible;
        TerminalSnapshot.Visibility = Visibility.Collapsed;
        TerminalSnapshot.Source = null;
    }

    // ── 타이틀바 ──────────────────────────────────────────────────
    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // 최대화 시 7px 여백 (WPF 최대화 오버플로 보정 — devez ImageViewerDialog 정합)
    private void UpdateMaximizeMargin()
        => RootGrid.Margin = WindowState == WindowState.Maximized
            ? new Thickness(7) : new Thickness(0);

    private void MaxBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
