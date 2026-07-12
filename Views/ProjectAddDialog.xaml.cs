using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DevezCode.Views;

public enum ProjectAddKind
{
    Folder,
    Project,
}

/// <summary>프로젝트 영역에 논리 폴더 또는 작업 프로젝트를 추가할 때 항목 종류를 고르는 다이얼로그.</summary>
public partial class ProjectAddDialog : Window
{
    private ProjectAddKind? _selectedKind;

    private ProjectAddDialog()
    {
        InitializeComponent();
        ContentRoot.SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += (_, _) => ApplyRoundedClip();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _selectedKind = null;
            DialogResult = false;
            e.Handled = true;
        };
    }

    private void ApplyRoundedClip()
    {
        double w = ContentRoot.ActualWidth, h = ContentRoot.ActualHeight;
        if (w <= 0 || h <= 0) return;
        ContentRoot.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 13, 13);
    }

    public static ProjectAddKind? Pick(Window owner)
    {
        var dialog = new ProjectAddDialog { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._selectedKind : null;
    }

    private void FolderCard_Click(object sender, RoutedEventArgs e) => Complete(ProjectAddKind.Folder);

    private void ProjectCard_Click(object sender, RoutedEventArgs e) => Complete(ProjectAddKind.Project);

    private void Complete(ProjectAddKind kind)
    {
        _selectedKind = kind;
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _selectedKind = null;
        DialogResult = false;
        Close();
    }
}
