using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevezCode.Models;

namespace DevezCode.Views;

/// <summary>좌측 사이드바 — 프로젝트(디렉터리) → 세션 트리. 동작은 이벤트로 MainWindow에 위임.</summary>
public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
        PreviewMouseMove += Sidebar_PreviewMouseMove;
        PreviewMouseLeftButtonUp += Sidebar_PreviewMouseUp;
        LostMouseCapture += Sidebar_LostCapture;
    }

    public event Action? AddProjectRequested;
    public event Action<ProjectItem>? ProjectSelected;
    public event Action<ProjectItem>? AddSessionRequested;
    public event Action<ProjectItem>? ProjectDeleteRequested;
    public event Action<ProjectItem>? ProjectIconChangeRequested;
    /// <summary>드래그로 프로젝트 순서가 바뀐 뒤 발생(영속 저장용).</summary>
    public event Action? ProjectsReordered;
    /// <summary>드래그로 특정 프로젝트의 세션 순서가 바뀐 뒤 발생(탭 동기화 + 영속용).</summary>
    public event Action<ProjectItem>? SessionsReordered;
    public event Action<SessionItem>? SessionSelected;
    public event Action<SessionItem>? SessionDeleteRequested;

    private ObservableCollection<ProjectItem>? _projects;
    public ObservableCollection<ProjectItem> Projects
    {
        get => _projects ??= new();
        set { _projects = value; ProjectsHost.ItemsSource = value; }
    }

    private void AddProject_Click(object sender, RoutedEventArgs e) => AddProjectRequested?.Invoke();

    private void Project_Click(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) { _didDrag = false; return; } // 드래그 직후의 클릭은 무시
        if (sender is FrameworkElement { DataContext: ProjectItem p })
        {
            p.IsExpanded = !p.IsExpanded;
            ProjectSelected?.Invoke(p);
        }
    }

    private void Session_Click(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) { _didDrag = false; return; }
        if (sender is FrameworkElement { DataContext: SessionItem s }) SessionSelected?.Invoke(s);
    }

    private void AddSession_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) AddSessionRequested?.Invoke(p);
    }

    private void ProjectDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) ProjectDeleteRequested?.Invoke(p);
    }

    private void ChangeProjectIcon_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) ProjectIconChangeRequested?.Invoke(p);
    }

    private void SessionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionDeleteRequested?.Invoke(s);
    }

    /// <summary>이벤트 소스에서 데이터 항목을 얻는다. 컨텍스트 메뉴 항목은 Tag, 행 요소는 DataContext.</summary>
    private static T? ItemOf<T>(object sender) where T : class
        => sender is FrameworkElement fe ? (fe.Tag ?? fe.DataContext) as T : null;

    // ── 드래그 순서변경 (devez ReorderDrag: 고스트 + 시프트 애니메이션) ──────────────
    private Point _pressOrigin;
    private ProjectItem? _pendingProject;
    private SessionItem? _pendingSession;
    private ReorderDrag<ProjectItem>? _projectDrag;
    private ReorderDrag<SessionItem>? _sessionDrag;
    private bool _didDrag;

    private void ProjectRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pendingProject = (sender as FrameworkElement)?.DataContext as ProjectItem;
        _pendingSession = null;
        _didDrag = false;
    }

    private void SessionRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pendingSession = (sender as FrameworkElement)?.DataContext as SessionItem;
        _pendingProject = null;
        _didDrag = false;
    }

    private void Sidebar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_projectDrag != null) { _projectDrag.Update(e); return; }
        if (_sessionDrag != null) { _sessionDrag.Update(e); return; }
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = _pressOrigin - e.GetPosition(this);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (_pendingProject != null) TryStartProjectDrag(_pendingProject);
        else if (_pendingSession != null) TryStartSessionDrag(_pendingSession);
    }

    private async void Sidebar_PreviewMouseUp(object sender, MouseButtonEventArgs e) => await EndDragAsync(commit: true);
    private async void Sidebar_LostCapture(object sender, MouseEventArgs e) => await EndDragAsync(commit: true);

    private async Task EndDragAsync(bool commit)
    {
        var pd = _projectDrag; var sd = _sessionDrag;
        _projectDrag = null; _sessionDrag = null;
        _pendingProject = null; _pendingSession = null;
        if (Mouse.Captured == this) ReleaseMouseCapture();
        if (pd != null) await pd.FinishAsync(commit);
        if (sd != null) await sd.FinishAsync(commit);
    }

    private void TryStartProjectDrag(ProjectItem p)
    {
        var rows = GetProjectRows().ToList();
        var src = rows.FirstOrDefault(r => ReferenceEquals(r.Item, p));
        if (src.Element == null) return;

        _projectDrag = ReorderDrag<ProjectItem>.TryStart(this, rows, p, src.Element,
            (s, hostTarget, _) =>
            {
                int from = Projects.IndexOf(s);
                if (from >= 0)
                {
                    int to = Math.Clamp(hostTarget, 0, Projects.Count - 1);
                    if (to != from) { Projects.Move(from, to); ProjectsReordered?.Invoke(); }
                }
                return Task.CompletedTask;
            });
        if (_projectDrag != null) { _didDrag = true; CaptureMouse(); }
        _pendingProject = null;
    }

    private void TryStartSessionDrag(SessionItem s)
    {
        var project = Projects.FirstOrDefault(pr => pr.Sessions.Contains(s));
        if (project == null) return;
        var rows = GetSessionRows(project).ToList();
        var src = rows.FirstOrDefault(r => ReferenceEquals(r.Item, s));
        if (src.Element == null) return;

        _sessionDrag = ReorderDrag<SessionItem>.TryStart(this, rows, s, src.Element,
            (sess, hostTarget, _) =>
            {
                int from = project.Sessions.IndexOf(sess);
                if (from >= 0)
                {
                    int to = Math.Clamp(hostTarget, 0, project.Sessions.Count - 1);
                    if (to != from) { project.Sessions.Move(from, to); SessionsReordered?.Invoke(project); }
                }
                return Task.CompletedTask;
            });
        if (_sessionDrag != null) { _didDrag = true; CaptureMouse(); }
        _pendingSession = null;
    }

    private IEnumerable<(ProjectItem Item, FrameworkElement Element)> GetProjectRows()
    {
        foreach (var p in Projects)
            if (ProjectsHost.ItemContainerGenerator.ContainerFromItem(p) is FrameworkElement fe)
                yield return (p, fe);
    }

    private IEnumerable<(SessionItem Item, FrameworkElement Element)> GetSessionRows(ProjectItem project)
    {
        if (ProjectsHost.ItemContainerGenerator.ContainerFromItem(project) is not DependencyObject pc)
            yield break;
        var inner = FindVisualChildren<ItemsControl>(pc).FirstOrDefault();
        if (inner == null) yield break;
        foreach (var s in project.Sessions)
            if (inner.ItemContainerGenerator.ContainerFromItem(s) is FrameworkElement fe)
                yield return (s, fe);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) yield break;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in FindVisualChildren<T>(c)) yield return x;
        }
    }
}
