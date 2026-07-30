using System.Collections.ObjectModel;
using System.IO;

namespace DevezCode.Models;

/// <summary>우측 파일 탐색기 트리 노드. 폴더는 펼칠 때 자식을 지연 로딩한다.</summary>
public sealed class FileNode : NotifyBase
{
    private static readonly FileNode Placeholder = new() { Name = "" };

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _fullPath = "";
    public string FullPath { get => _fullPath; set => Set(ref _fullPath, value); }
    public bool IsDirectory { get; init; }

    /// <summary>상위 노드(루트 노드면 null). 컨텍스트 메뉴 작업 후 트리 갱신에 사용.</summary>
    public FileNode? Parent { get; set; }

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
        => FromDirectory(path, null);

    private static FileNode FromDirectory(string path, FileNode? parent)
    {
        var node = new FileNode
        {
            Name = new DirectoryInfo(path.TrimEnd('\\', '/')).Name is { Length: > 0 } n ? n : path,
            FullPath = path,
            IsDirectory = true,
            Parent = parent,
        };
        node.Children.Add(Placeholder); // 펼치기 화살표 표시용 더미
        return node;
    }

    private bool _loaded;
    private void LoadChildren()
    {
        if (_loaded) return;
        _loaded = true;
        if (!ReconcileDirectory(Children, FullPath, this))
            _loaded = false;
    }

    /// <summary>디렉터리의 바로 아래 항목을 디스크와 맞춘다. 같은 경로의 기존 노드는 재사용해
    /// 하위 폴더의 펼침 상태와 지연 로딩 결과가 유지되도록 한다.</summary>
    public static bool ReconcileDirectory(
        ObservableCollection<FileNode> target,
        string directory,
        FileNode? parent = null)
    {
        List<(string Path, bool IsDirectory)> entries;
        try
        {
            entries = Directory.EnumerateDirectories(directory)
                .Where(d => !IsHidden(d))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .Select(path => (Path: path, IsDirectory: true))
                .Concat(Directory.EnumerateFiles(directory)
                .Where(f => !IsHidden(f))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .Select(path => (Path: path, IsDirectory: false)))
                .ToList();
        }
        catch { return false; }

        var existing = target
            .Where(node => !ReferenceEquals(node, Placeholder))
            .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var desired = new List<FileNode>(entries.Count);
        foreach (var entry in entries)
        {
            if (existing.TryGetValue(entry.Path, out var node) &&
                node.IsDirectory == entry.IsDirectory)
            {
                node.UpdatePathCasing(entry.Path);
                node.Parent = parent;
                desired.Add(node);
                continue;
            }

            desired.Add(entry.IsDirectory
                ? FromDirectory(entry.Path, parent)
                : new FileNode
                {
                    Name = Path.GetFileName(entry.Path),
                    FullPath = entry.Path,
                    IsDirectory = false,
                    Parent = parent,
                });
        }

        var desiredSet = desired.ToHashSet();
        for (int i = target.Count - 1; i >= 0; i--)
            if (!desiredSet.Contains(target[i]))
                target.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                continue;
            int current = target.IndexOf(desired[i]);
            if (current < 0) target.Insert(i, desired[i]);
            else if (current != i) target.Move(current, i);
        }
        return true;
    }

    /// <summary>Windows에서 대소문자만 바뀐 rename은 같은 노드로 인식하되 표시명과 실제 경로 표기는
    /// 새 casing으로 맞춘다. 이미 로드된 하위 노드도 같은 접두 경로를 사용하도록 함께 갱신한다.</summary>
    private void UpdatePathCasing(string path)
    {
        var previousPath = FullPath;
        Name = Path.GetFileName(path);
        FullPath = path;
        if (!IsDirectory || string.IsNullOrEmpty(previousPath)
            || string.Equals(previousPath, path, StringComparison.Ordinal))
            return;

        foreach (var child in Children)
        {
            if (ReferenceEquals(child, Placeholder) || string.IsNullOrEmpty(child.FullPath)) continue;
            string relative;
            try { relative = Path.GetRelativePath(previousPath, child.FullPath); }
            catch { continue; }
            child.UpdatePathCasing(Path.Combine(path, relative));
        }
    }

    /// <summary>디스크 상태로 자식 목록을 다시 읽는다(이름변경·삭제·붙여넣기 후 갱신).</summary>
    public void Refresh()
    {
        if (!IsDirectory) return;
        _loaded = false;
        if (_isExpanded) LoadChildren();
        else { Children.Clear(); Children.Add(Placeholder); }
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
