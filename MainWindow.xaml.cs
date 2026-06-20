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

    public MainWindow()
    {
        InitializeComponent();

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

        // 중앙 탭 드래그 순서변경(가로) — devez ReorderDrag
        TabsHost.PreviewMouseMove += TabsHost_PreviewMouseMove;
        TabsHost.PreviewMouseLeftButtonUp += async (_, _) => await EndTabDragAsync();
        TabsHost.LostMouseCapture += async (_, _) => await EndTabDragAsync();

        _terminal.SessionStarted += id => { var s = FindSession(id); if (s != null) s.IsAlive = true; };
        _terminal.SessionExited  += id => { var s = FindSession(id); if (s != null) s.IsAlive = false; };

        UpdateEmptyState();
        UpdateStatus();

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
            StatusText.Text = $"업데이트 다운로드 중… {v:P0}");
        try
        {
            await UpdateService.DownloadAndRelaunchAsync(info, progress);
            // 성공 시 앱이 종료/재실행되므로 이 아래로는 도달하지 않는다.
        }
        catch
        {
            _updateInProgress = false;
            UpdateStatus();
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

    /// <summary>활성 프로젝트 전환 — 중앙 탭을 그 프로젝트의 세션들로 교체(같은 컬렉션 바인딩). 세션 활성화는 안 함.</summary>
    private void SetActiveProject(ProjectItem proj)
    {
        _activeProject = proj;
        foreach (var p in _projects) p.IsSelected = ReferenceEquals(p, proj);
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

        foreach (var s in proj.Sessions.ToList()) DisposeSessionProcess(s);
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
    private void AddSession(ProjectItem proj)
    {
        var session = new SessionItem { Name = $"세션 {proj.Sessions.Count + 1}" };
        proj.Sessions.Add(session);
        proj.IsExpanded = true;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, proj.Path);
        WorkspaceStore.Save(_projects);
        OpenSession(session);   // 프로젝트 활성화 + 새 세션 활성화
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
        _terminal.ShowTerminal(session.Id);
        _terminal.FocusTerminal();
        UpdateEmptyState();
    }

    private void ClearActiveSession()
    {
        _activeSession = null;
        foreach (var p in _projects)
            foreach (var s in p.Sessions)
                s.IsSelected = false;
        UpdateEmptyState();
    }

    /// <summary>탭 닫기(X) = 세션 삭제 — 프로젝트 단위 탭 모델에선 탭이 곧 세션이므로 제거한다.</summary>
    private void CloseTab(SessionItem session) => DeleteSession(session);

    /// <summary>세션 영구 삭제 — ConPTY 프로세스·매핑 제거 + 컬렉션에서 제거.</summary>
    private void DeleteSession(SessionItem session)
    {
        var parent = ParentOf(session);
        bool wasActive = ReferenceEquals(_activeSession, session);
        int idx = parent?.Sessions.IndexOf(session) ?? -1;

        DisposeSessionProcess(session);
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

    /// <summary>세션의 터미널 프로세스·매핑만 정리(컬렉션은 건드리지 않음).</summary>
    private void DisposeSessionProcess(SessionItem session)
    {
        try { _terminal.CloseTerminal(session.Id); } catch { /* ignore */ }
        try { TerminalSessionManager.Instance.DisposeRoom(session.Id); } catch { /* ignore */ }
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

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionItem s }) CloseTab(s);
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

    // ── 상태/빈 화면 ─────────────────────────────────────────────
    private void UpdateEmptyState()
    {
        bool hasActive = _activeSession != null;
        EmptyState.Visibility = hasActive ? Visibility.Collapsed : Visibility.Visible;
        TerminalHostContainer.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatus()
    {
        int sessions = _projects.Sum(p => p.Sessions.Count);
        var claude = ClaudeCliDetector.IsInstalled() ? "claude CLI 감지됨" : "claude CLI 미설치";
        StatusText.Text = $"프로젝트 {_projects.Count} · 세션 {sessions} · {claude}";
    }

    // ── 타이틀바 ──────────────────────────────────────────────────
    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        var next = App.CurrentTheme switch { "dark" => "soft", "soft" => "minimal", _ => "dark" };
        ((App)Application.Current).SetTheme(next);
    }

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
