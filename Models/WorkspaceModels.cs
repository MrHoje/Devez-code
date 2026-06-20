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

    /// <summary>터미널(ConPTY) 세션이 살아있는지. true=테마색 점, false=회색 점.</summary>
    private bool _isAlive;
    public bool IsAlive { get => _isAlive; set => Set(ref _isAlive, value); }

    /// <summary>claude 가 요청 처리 중인지(응답 대기). true=좌측 트리에 스피너 표시.</summary>
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    /// <summary>마지막으로 보낸 프롬프트(요약 1줄). busy 훅이 떨군 lastmsg 파일에서 갱신. 상단 헤더에 표시.</summary>
    private string _lastMessage = "";
    public string LastMessage { get => _lastMessage; set => Set(ref _lastMessage, value); }

    /// <summary>탭에서만 숨김. true 면 탭 스트립에서 Collapse, 세션 자체(터미널/기록)는 보존.
    /// 프로젝트가 다시 선택되면 자동으로 false 로 리셋(임시 뷰 상태).</summary>
    private bool _hidden;
    public bool Hidden { get => _hidden; set => Set(ref _hidden, value); }
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

    /// <summary>프로젝트 아이콘 리소스 키(Icons.xaml의 IconXxx). 기본값 IconBox.</summary>
    private string _iconKey = "IconBox";
    public string IconKey { get => _iconKey; set => Set(ref _iconKey, string.IsNullOrEmpty(value) ? "IconBox" : value); }

    /// <summary>아이콘 색상 hex(#rrggbb). null/빈값이면 테마 기본색.</summary>
    private string? _iconColor;
    public string? IconColor { get => _iconColor; set => Set(ref _iconColor, string.IsNullOrEmpty(value) ? null : value); }

    public ObservableCollection<SessionItem> Sessions { get; } = new();

    public static ProjectItem FromPath(string path)
    {
        var name = new DirectoryInfo(path.TrimEnd('\\', '/')).Name;
        if (string.IsNullOrEmpty(name)) name = path; // 드라이브 루트 등
        return new ProjectItem { Path = path, Name = name };
    }
}
