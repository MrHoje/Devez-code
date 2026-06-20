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
    private readonly ObservableCollection<SessionItem> _openTabs = new();
    private readonly TerminalHostView _terminal = new();
    private SessionItem? _activeSession;

    public MainWindow()
    {
        InitializeComponent();

        _projects = WorkspaceStore.Load();
        Sidebar.Projects = _projects;
        TabsHost.ItemsSource = _openTabs;
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
        };
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
        _projects.Add(proj);
        WorkspaceStore.Save(_projects);
        SelectProject(proj);
        UpdateStatus();
    }

    private void SelectProject(ProjectItem proj)
    {
        foreach (var p in _projects) p.IsSelected = ReferenceEquals(p, proj);
        FileExplorer.ShowDirectory(proj.Path);
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

        foreach (var s in proj.Sessions.ToList()) DisposeSession(s);
        _projects.Remove(proj);
        WorkspaceStore.Save(_projects);
        if (_activeSession != null && !_openTabs.Contains(_activeSession)) ClearActive();
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
        OpenSession(session);
        UpdateStatus();
    }

    private void OpenSession(SessionItem session)
    {
        var parent = ParentOf(session);
        if (parent == null) return;

        // claude 가 항상 프로젝트 디렉터리에서 실행되도록 매핑 보장(설정 유실 대비)
        SettingsService.SaveClaudeCodeRoomDir(session.Id, parent.Path);

        session.IsAlive = true; // 낙관적 — 실패 시 SessionExited 이벤트로 회색 처리
        if (!_openTabs.Contains(session)) _openTabs.Add(session);
        SetActive(session);
        SelectProject(parent);

        _terminal.ShowTerminal(session.Id);
        _terminal.FocusTerminal();
        UpdateEmptyState();
    }

    private void SetActive(SessionItem session)
    {
        _activeSession = session;
        foreach (var p in _projects)
            foreach (var s in p.Sessions)
                s.IsSelected = ReferenceEquals(s, session);
    }

    private void ClearActive()
    {
        _activeSession = null;
        foreach (var p in _projects)
            foreach (var s in p.Sessions)
                s.IsSelected = false;
        UpdateEmptyState();
    }

    /// <summary>탭 닫기 — xterm 인스턴스만 정리하고 ConPTY 세션은 살려둔다(재열기 시 복원).</summary>
    private void CloseTab(SessionItem session)
    {
        _openTabs.Remove(session);
        _terminal.CloseTerminal(session.Id);
        if (ReferenceEquals(_activeSession, session))
        {
            var next = _openTabs.LastOrDefault();
            if (next != null) OpenSession(next);
            else ClearActive();
        }
    }

    /// <summary>세션 영구 삭제 — ConPTY 프로세스·매핑까지 제거.</summary>
    private void DeleteSession(SessionItem session)
    {
        var parent = ParentOf(session);
        DisposeSession(session);
        parent?.Sessions.Remove(session);
        WorkspaceStore.Save(_projects);
        UpdateStatus();
    }

    private void DisposeSession(SessionItem session)
    {
        _openTabs.Remove(session);
        try { _terminal.CloseTerminal(session.Id); } catch { /* ignore */ }
        try { TerminalSessionManager.Instance.DisposeRoom(session.Id); } catch { /* ignore */ }
        SettingsService.RemoveClaudeCodeRoomDir(session.Id);
        if (ReferenceEquals(_activeSession, session))
        {
            var next = _openTabs.LastOrDefault();
            if (next != null) OpenSession(next);
            else ClearActive();
        }
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
        var rows = new List<(SessionItem, FrameworkElement)>();
        foreach (var t in _openTabs)
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(t) is FrameworkElement fe)
                rows.Add((t, fe));
        var src = rows.FirstOrDefault(r => ReferenceEquals(r.Item1, s));
        if (src.Item2 == null) return;

        _tabDrag = ReorderDrag<SessionItem>.TryStart(TabsHost, rows, s, src.Item2,
            (sess, hostTarget, _) =>
            {
                int from = _openTabs.IndexOf(sess);
                if (from >= 0)
                {
                    int to = Math.Clamp(hostTarget, 0, _openTabs.Count - 1);
                    if (to != from)
                    {
                        _openTabs.Move(from, to);
                        SyncProjectSessionsFromTabs(sess); // 탭 순서 → 좌측 세션 순서 반영
                        WorkspaceStore.Save(_projects);
                    }
                }
                return Task.CompletedTask;
            },
            exactFollow: true, horizontal: true);
        if (_tabDrag != null) { _tabDidDrag = true; TabsHost.CaptureMouse(); }
        _pendingTab = null;
    }

    /// <summary>사이드바에서 세션 순서가 바뀌면 중앙 탭 순서를 사이드바 순서(프로젝트→세션)에 맞춰 재정렬.</summary>
    private void OnSidebarSessionsReordered(ProjectItem _)
    {
        ResortOpenTabsToSidebar();
        WorkspaceStore.Save(_projects);
    }

    /// <summary>열린 탭들을 (프로젝트 순서, 프로젝트 내 세션 순서) 기준으로 재정렬.</summary>
    private void ResortOpenTabsToSidebar()
    {
        var ordered = _openTabs
            .OrderBy(s => { var p = ParentOf(s); return p == null ? int.MaxValue : _projects.IndexOf(p); })
            .ThenBy(s => ParentOf(s)?.Sessions.IndexOf(s) ?? 0)
            .ToList();
        ApplyOrder(_openTabs, ordered);
    }

    /// <summary>탭 순서를 기준으로, 옮겨진 세션이 속한 프로젝트의 세션 순서를 맞춘다(닫힌 세션 위치는 보존).</summary>
    private void SyncProjectSessionsFromTabs(SessionItem moved)
    {
        var p = ParentOf(moved);
        if (p == null) return;
        var openOrder = _openTabs.Where(s => p.Sessions.Contains(s)).ToList();
        if (openOrder.Count == 0) return;

        int oi = 0;
        var result = new List<SessionItem>();
        foreach (var s in p.Sessions)
            result.Add(openOrder.Contains(s) ? openOrder[oi++] : s);
        ApplyOrder(p.Sessions, result);
    }

    /// <summary>ObservableCollection 을 target 순서와 일치하도록 최소 Move 로 재배치.</summary>
    private static void ApplyOrder<T>(ObservableCollection<T> coll, List<T> target)
    {
        for (int i = 0; i < target.Count; i++)
        {
            int cur = coll.IndexOf(target[i]);
            if (cur >= 0 && cur != i) coll.Move(cur, i);
        }
    }

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
