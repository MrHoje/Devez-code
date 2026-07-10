using System.Windows;
using System.Windows.Input;

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
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _selectedKind = null;
            DialogResult = false;
            e.Handled = true;
        };
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
