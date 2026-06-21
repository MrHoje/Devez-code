using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.CompilerServices;
using DevezCode.Views;

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

/// <summary>중앙 탭 종류. 탭 아이콘·콘텐츠 분기에 사용.</summary>
public enum TabKind { Session, File }

/// <summary>중앙 탭(터미널/파일 편집기)의 공통 베이스. 탭 스트립 + 콘텐츠 호스트 양쪽에서 사용.</summary>
public abstract class TabItemBase : NotifyBase
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>탭 헤더에 표시할 텍스트(파일명/세션명).</summary>
    public abstract string Title { get; }

    /// <summary>터미널/파일 등 탭 콘텐츠 종류. XAML DataTemplate 트리거·콘텐츠 호스트 분기에서 사용.</summary>
    public abstract TabKind Kind { get; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>세션 탭 전용: 탭에서만 숨김. 파일 탭은 사용하지 않음(파일 탭은 X 시 제거).
    /// true 면 탭 스트립에서 Collapse, 세션 자체(터미널/기록)는 보존.
    /// 사이드바에서 해당 세션을 클릭하면 다시 false 로 풀려 탭이 복귀한다.</summary>
    private bool _hidden;
    public bool Hidden { get => _hidden; set => Set(ref _hidden, value); }
}

/// <summary>좌측 트리의 세션(= 중앙 터미널 탭 1개). roomId 로 ConPTY 세션·xterm 인스턴스를 식별.</summary>
public sealed class SessionItem : TabItemBase
{
    public override TabKind Kind => TabKind.Session;
    public override string Title => Name;

    private string _name = "세션";
    public string Name { get => _name; set { if (Set(ref _name, value)) OnPropertyChanged(nameof(Title)); } }

    /// <summary>이 세션이 사용할 에이전트 ID. 빈 값/누락이면 SettingsService.LoadAgentForRoom 으로 폴백.</summary>
    public string AgentId { get; set; } = "";

    /// <summary>터미널(ConPTY) 세션이 살아있는지. true=테마색 점, false=회색 점.</summary>
    private bool _isAlive;
    public bool IsAlive { get => _isAlive; set => Set(ref _isAlive, value); }

    /// <summary>claude 가 요청 처리 중인지(응답 대기). true=좌측 트리에 스피너 표시.</summary>
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    /// <summary>마지막으로 보낸 프롬프트(요약 1줄). busy 훅이 떨군 lastmsg 파일에서 갱신. 상단 헤더에 표시.</summary>
    private string _lastMessage = "";
    public string LastMessage { get => _lastMessage; set => Set(ref _lastMessage, value); }
}

/// <summary>파일 편집기 탭. 파일 탐색기에서 텍스트 파일을 더블클릭하면 새로 열린다.
/// 각 탭은 자신만의 FileEditorView 인스턴스를 갖고, 닫히면 디스크에서 제거(영속 X).</summary>
public sealed class FileTabItem : TabItemBase
{
    public override TabKind Kind => TabKind.File;
    public override string Title => string.IsNullOrEmpty(FilePath) ? "파일" : Path.GetFileName(FilePath);

    /// <summary>편집 대상 절대 경로. 비교는 OrdinalIgnoreCase.</summary>
    public string FilePath { get; init; } = "";

    /// <summary>탭마다 1개의 FileEditorView 인스턴스. 콘텐츠 호스트에 그대로 붙여 렌더한다.</summary>
    public FileEditorView Editor { get; init; } = new();

    /// <summary>탭을 닫을 때 외부에서 호출: dirty 확인 후 비로소 제거해도 되는지 결과를 받는다.</summary>
    public event EventHandler? CloseRequested;

    internal void RaiseCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>좌측 트리의 프로젝트(= 디렉터리). 하위에 탭(세션/파일) 목록을 가진다.</summary>
public sealed class ProjectItem : NotifyBase
{
    public string Path { get; init; } = "";

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>중앙 탭 스트립에 그대로 바인딩되는 통합 컬렉션(세션 + 파일 탭).
    /// 사이드바는 Sessions(동기 뷰)로 세션만 골라 렌더한다.</summary>
    public ObservableCollection<TabItemBase> Tabs { get; } = new();

    /// <summary>사이드바 호환을 위한 세션 전용 동기 뷰(ObservableCollection).
    /// Tabs.CollectionChanged 에서 SessionItem 만 추려 추가/제거한다 → 사이드바 바인딩이 즉시 갱신.</summary>
    public ObservableCollection<SessionItem> Sessions { get; } = new();

    public ProjectItem()
    {
        // Tabs → Sessions 단방향 동기. 역방향은 코드가 항상 Tabs 에만 추가/제거하도록 강제.
        Tabs.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (TabItemBase t in e.NewItems)
                    if (t is SessionItem s) Sessions.Add(s);
            if (e.OldItems != null)
                foreach (TabItemBase t in e.OldItems)
                    if (t is SessionItem s) Sessions.Remove(s);
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                Sessions.Clear();
                foreach (var s in Tabs.OfType<SessionItem>()) Sessions.Add(s);
            }
        };
    }

    public static ProjectItem FromPath(string path)
    {
        var name = new DirectoryInfo(path.TrimEnd('\\', '/')).Name;
        if (string.IsNullOrEmpty(name)) name = path; // 드라이브 루트 등
        return new ProjectItem { Path = path, Name = name };
    }
}
