using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DevezCode.Models;

/// <summary>SCM 변경 트리 노드 — 폴더(IsFolder=true, Children) 또는 파일 리프(Change != null).</summary>
public sealed class ScmTreeNode : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public bool IsFolder { get; init; }
    public ObservableCollection<ScmTreeNode> Children { get; } = new();
    /// <summary>파일 리프면 해당 변경, 폴더면 null.</summary>
    public GitChange? Change { get; init; }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); }
    }

    /// <summary>파일 리프의 상태 글자(폴더면 빈 문자열).</summary>
    public string Status => Change?.Status ?? "";
    /// <summary>파일 리프의 상태 색.</summary>
    public string StatusColor => Change?.StatusColor ?? "#8B949E";

    public event PropertyChangedEventHandler? PropertyChanged;
}
