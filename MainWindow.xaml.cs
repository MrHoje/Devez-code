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
        Sidebar.SessionSelected        += OpenSession;
        Sidebar.SessionDeleteRequested += DeleteSession;

        _terminal.SessionStarted += id => { var s = FindSession(id); if (s != null) s.IsAlive = true; };
        _terminal.SessionExited  += id => { var s = FindSession(id); if (s != null) s.IsAlive = false; };

        UpdateEmptyState();
        UpdateStatus();
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
        if (sender is FrameworkElement { DataContext: SessionItem s }) OpenSession(s);
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionItem s }) CloseTab(s);
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
