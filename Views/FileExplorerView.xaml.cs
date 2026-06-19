using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;

namespace DevezCode.Views;

/// <summary>우측 파일 탐색기 — 선택된 프로젝트 디렉터리의 파일/폴더를 트리로 나열.</summary>
public partial class FileExplorerView : UserControl
{
    public FileExplorerView() => InitializeComponent();

    private string? _rootPath;

    /// <summary>탐색기를 지정한 디렉터리로 전환. null 이면 안내 문구.</summary>
    public void ShowDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            _rootPath = null;
            PathText.Text = "파일 탐색기";
            Tree.ItemsSource = null;
            return;
        }
        if (_rootPath == path) return;
        _rootPath = path;
        PathText.Text = path;

        var roots = new ObservableCollection<FileNode>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                if (!IsHidden(d)) roots.Add(FileNode.FromDirectory(d));
            foreach (var f in Directory.EnumerateFiles(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                if (!IsHidden(f)) roots.Add(new FileNode { Name = Path.GetFileName(f), FullPath = f, IsDirectory = false });
        }
        catch { /* 접근 거부 등 */ }
        Tree.ItemsSource = roots;
    }

    private static bool IsHidden(string p)
    {
        try { var a = File.GetAttributes(p); return a.HasFlag(FileAttributes.Hidden) || a.HasFlag(FileAttributes.System); }
        catch { return false; }
    }

    /// <summary>파일 더블클릭 → OS 기본 앱으로 열기.</summary>
    private void Node_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is FrameworkElement { DataContext: FileNode node } && !node.IsDirectory)
        {
            try { Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true }); }
            catch { /* 열 수 없는 파일 무시 */ }
            e.Handled = true;
        }
    }
}
