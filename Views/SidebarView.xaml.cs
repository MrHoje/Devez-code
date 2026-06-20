using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;

namespace DevezCode.Views;

/// <summary>좌측 사이드바 — 프로젝트(디렉터리) → 세션 트리. 동작은 이벤트로 MainWindow에 위임.</summary>
public partial class SidebarView : UserControl
{
    public SidebarView() => InitializeComponent();

    public event Action? AddProjectRequested;
    public event Action<ProjectItem>? ProjectSelected;
    public event Action<ProjectItem>? AddSessionRequested;
    public event Action<ProjectItem>? ProjectDeleteRequested;
    public event Action<ProjectItem>? ProjectIconChangeRequested;
    /// <summary>드래그로 프로젝트 순서가 바뀐 뒤 발생(영속 저장용).</summary>
    public event Action? ProjectsReordered;
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

    // ── 프로젝트 드래그 순서변경 ──────────────────────────────────
    private Point _dragStart;
    private ProjectItem? _dragCandidate;
    private bool _isDragging;
    private bool _didDrag;

    private void ProjectRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = (sender as FrameworkElement)?.DataContext as ProjectItem;
        _isDragging = false;
    }

    private void ProjectRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging || _dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var diff = _dragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _isDragging = true;
        _didDrag = true; // 이어지는 MouseLeftButtonUp(Project_Click) 무시용
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(ProjectItem), _dragCandidate), DragDropEffects.Move);
        }
        finally
        {
            _isDragging = false;
            _dragCandidate = null;
            DropIndicator.Clear();
        }
    }

    private void ProjectRow_DragOver(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectItem target } fe
            && e.Data.GetData(typeof(ProjectItem)) is ProjectItem src && !ReferenceEquals(src, target))
        {
            e.Effects = DragDropEffects.Move;
            bool atTop = e.GetPosition(fe).Y < fe.ActualHeight / 2;
            DropIndicator.Show(fe, atTop);
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void ProjectRow_DragLeave(object sender, DragEventArgs e)
    {
        // 행 밖으로 완전히 나가면 표시 제거 (자식으로의 이동은 무시)
        if (sender is FrameworkElement fe && !fe.IsMouseOver) DropIndicator.Clear();
    }

    private void ProjectRow_Drop(object sender, DragEventArgs e)
    {
        DropIndicator.Clear();
        if (sender is not FrameworkElement { DataContext: ProjectItem target } fe) return;
        if (e.Data.GetData(typeof(ProjectItem)) is not ProjectItem src || ReferenceEquals(src, target)) return;

        int from = Projects.IndexOf(src);
        int to = Projects.IndexOf(target);
        if (from < 0 || to < 0) return;

        bool atTop = e.GetPosition(fe).Y < fe.ActualHeight / 2;
        int insert = atTop ? to : to + 1;
        if (from < insert) insert--;            // src 제거로 인한 인덱스 시프트 보정
        insert = Math.Max(0, Math.Min(Projects.Count - 1, insert));
        if (insert == from) return;

        Projects.Move(from, insert);
        ProjectsReordered?.Invoke();
        e.Handled = true;
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

    private void Session_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionItem s }) SessionSelected?.Invoke(s);
    }

    private void SessionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionDeleteRequested?.Invoke(s);
    }

    /// <summary>이벤트 소스에서 데이터 항목을 얻는다. 컨텍스트 메뉴 항목은 Tag, 행 요소는 DataContext.</summary>
    private static T? ItemOf<T>(object sender) where T : class
        => sender is FrameworkElement fe ? (fe.Tag ?? fe.DataContext) as T : null;
}
