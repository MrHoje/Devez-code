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
        if (sender is FrameworkElement { DataContext: ProjectItem p })
        {
            p.IsExpanded = !p.IsExpanded;
            ProjectSelected?.Invoke(p);
        }
    }

    private void AddSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectItem p }) AddSessionRequested?.Invoke(p);
    }

    private void ProjectDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectItem p }) ProjectDeleteRequested?.Invoke(p);
    }

    private void Session_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionItem s }) SessionSelected?.Invoke(s);
    }

    private void SessionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionItem s }) SessionDeleteRequested?.Invoke(s);
    }
}
