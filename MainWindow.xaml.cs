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
        Sidebar.SessionSelected        += OpenSession;
        Sidebar.SessionDeleteRequested += DeleteSession;

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
