using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace DevezCode.Models;

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }
}

/// <summary>좌측 트리의 세션(= 중앙 터미널 탭 1개). roomId 로 ConPTY 세션·xterm 인스턴스를 식별.</summary>
public sealed class SessionItem : NotifyBase
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    private string _name = "세션";
    public string Name { get => _name; set => Set(ref _name, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>좌측 트리의 프로젝트(= 디렉터리). 하위에 세션 목록을 가진다.</summary>
public sealed class ProjectItem : NotifyBase
{
    public string Path { get; init; } = "";

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public ObservableCollection<SessionItem> Sessions { get; } = new();

    public static ProjectItem FromPath(string path)
    {
        var name = new DirectoryInfo(path.TrimEnd('\\', '/')).Name;
        if (string.IsNullOrEmpty(name)) name = path; // 드라이브 루트 등
        return new ProjectItem { Path = path, Name = name };
    }
}
