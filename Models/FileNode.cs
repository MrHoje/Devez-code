using System.Collections.ObjectModel;
using System.IO;

namespace DevezCode.Models;

/// <summary>우측 파일 탐색기 트리 노드. 폴더는 펼칠 때 자식을 지연 로딩한다.</summary>
public sealed class FileNode : NotifyBase
{
    private static readonly FileNode Placeholder = new() { Name = "" };

    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }

    public ObservableCollection<FileNode> Children { get; } = new();

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value) LoadChildren();
        }
    }

    public static FileNode FromDirectory(string path)
    {
        var node = new FileNode
        {
            Name = new DirectoryInfo(path.TrimEnd('\\', '/')).Name is { Length: > 0 } n ? n : path,
            FullPath = path,
            IsDirectory = true,
        };
        node.Children.Add(Placeholder); // 펼치기 화살표 표시용 더미
        return node;
    }

    private bool _loaded;
    private void LoadChildren()
    {
        if (_loaded) return;
        _loaded = true;
        Children.Clear();
        try
        {
            var dirs = Directory.EnumerateDirectories(FullPath)
                .Where(d => !IsHidden(d))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                var child = new FileNode { Name = Path.GetFileName(d), FullPath = d, IsDirectory = true };
                child.Children.Add(Placeholder);
                Children.Add(child);
            }
            var files = Directory.EnumerateFiles(FullPath)
                .Where(f => !IsHidden(f))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
                Children.Add(new FileNode { Name = Path.GetFileName(f), FullPath = f, IsDirectory = false });
        }
        catch { /* 접근 거부 등 무시 */ }
    }

    private static bool IsHidden(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System);
        }
        catch { return false; }
    }
}
