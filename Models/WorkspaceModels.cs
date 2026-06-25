using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
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

    /// <summary>테마 변경 시 아이콘 Source 바인딩을 재평가시키는 트리거(opencode 등 흑/백 변형용).
    /// 값은 그대로, PropertyChanged(AgentId)만 발생시켜 AgentImageConverter 를 다시 돌린다.</summary>
    public void RefreshAgentIcon() => OnPropertyChanged(nameof(AgentId));

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

    /// <summary>탭마다 1개의 파일 편집기 인스턴스. 콘텐츠 호스트에 그대로 붙여 렌더한다.</summary>
    public IFileTabEditor Editor { get; init; } = new FileEditorView();

    /// <summary>탭을 닫을 때 외부에서 호출: dirty 확인 후 비로소 제거해도 되는지 결과를 받는다.</summary>
    public event EventHandler? CloseRequested;

    internal void RaiseCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>프로젝트에 등록한 바로가기. 대상 파일 경로 + 표시 이름 + 관리자 실행 여부를 보관하고
/// 프로젝트 메뉴의 "바로가기" 자식으로 노출 → 클릭 시 외부 실행(관리자면 runas). workspace.json 에 영속.</summary>
public sealed class ProjectFile : NotifyBase
{
    public string FilePath { get; set; } = "";
    private string _name = "";
    public string Name { get => _name; set { if (Set(ref _name, value)) OnPropertyChanged(nameof(DisplayName)); } }
    public bool RunAsAdmin { get; set; }

    /// <summary>메뉴 표시 이름 — 사용자 지정 이름이 비면 파일명으로 폴백.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name
        : (string.IsNullOrEmpty(FilePath) ? "" : Path.GetFileName(FilePath));

    /// <summary>대상 파일의 셸 아이콘(카드 바로가기 행 표시용). 경로 없으면 null.</summary>
    private System.Windows.Media.ImageSource? _icon;
    private bool _iconLoaded;
    public System.Windows.Media.ImageSource? Icon
    {
        get { if (!_iconLoaded) { _iconLoaded = true; _icon = Services.FileIconHelper.GetSmallIcon(FilePath); } return _icon; }
    }
}

/// <summary>좌측 트리의 프로젝트(= 디렉터리). 하위에 탭(세션/파일) 목록을 가진다.</summary>
public sealed class ProjectItem : NotifyBase
{
    public string Path { get; init; } = "";

    /// <summary>프로젝트에 등록한 파일 목록(메뉴 고정). 하나라도 있으면 메뉴가 "파일" 서브메뉴로 바뀐다.</summary>
    public ObservableCollection<ProjectFile> Files { get; } = new();

    /// <summary>등록된 파일이 하나라도 있는지 — 메뉴 표시 분기(파일 추가 ↔ 파일 서브메뉴).</summary>
    public bool HasFiles => Files.Count > 0;

    /// <summary>바로가기 추가/갱신. 같은 경로가 이미 있으면 이름·관리자 플래그만 갱신(중복 추가 안 함).
    /// 새로 추가됐으면 true, 기존 갱신/무효면 false.</summary>
    public bool AddShortcut(string path, string name, bool runAsAdmin)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var existing = Files.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Name = name;
            existing.RunAsAdmin = runAsAdmin;
            return false;
        }
        Files.Add(new ProjectFile { FilePath = path, Name = name, RunAsAdmin = runAsAdmin });
        return true;
    }

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>2열 보기에서 이 카드가 속한 컬럼(0=좌, 1=우). 1열 보기에선 무시(전부 한 줄로 쌓임).
    /// 드래그로만 바뀌며, 열 수를 1↔2로 토글해도 값은 보존(자동 재배치 금지). workspace.json 에 영속.</summary>
    private int _column;
    public int Column { get => _column; set => Set(ref _column, value == 1 ? 1 : 0); }

    /// <summary>보관 시각(ISO-8601 UTC). null/빈값=활성, 값 있으면 보관함 소속.
    /// devez 의 projects.archived_at(Supabase) 정합 — DevezCode 는 workspace.json 에 로컬 영속.</summary>
    private string? _archivedAt;
    public string? ArchivedAt
    {
        get => _archivedAt;
        set { if (Set(ref _archivedAt, value)) { OnPropertyChanged(nameof(IsArchived)); OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(ArchivedDateDisplay)); } }
    }

    /// <summary>보관함 소속 여부.</summary>
    public bool IsArchived => !string.IsNullOrEmpty(ArchivedAt);

    /// <summary>활성(비보관) 여부 — 컨텍스트 메뉴 항목 가시성 분기용.</summary>
    public bool IsActive => string.IsNullOrEmpty(ArchivedAt);

    /// <summary>보관함 카드 표시용 "보관: yyyy-MM-dd" (활성이면 빈 문자열).</summary>
    public string ArchivedDateDisplay => string.IsNullOrEmpty(ArchivedAt) ? ""
        : "보관: " + (ArchivedAt.Length >= 10 ? ArchivedAt[..10] : ArchivedAt);

    /// <summary>중앙 탭 스트립에 그대로 바인딩되는 통합 컬렉션(세션 + 파일 탭).
    /// 사이드바는 Sessions(동기 뷰)로 세션만 골라 렌더한다.</summary>
    public ObservableCollection<TabItemBase> Tabs { get; } = new();

    /// <summary>재시작 복원용 — workspace.json 에서 읽은 "직전에 열려 있던 파일 탭 경로" 목록.
    /// 모델엔 임시 보관만 하고(직렬화 대상 아님), 시작 시 WorkspacePaneView.RestoreFileTabs 가
    /// 한 번 소비해 실제 FileTabItem 으로 만든다.</summary>
    public List<string> PendingOpenFiles { get; set; } = new();

    /// <summary>이 프로젝트에서 마지막으로 활성화했던 탭 참조. 형식: "S:&lt;세션ID&gt;" 또는 "F:&lt;파일경로&gt;".
    /// 프로젝트를 다시 선택할 때 이 탭을 복원한다(없거나 못 찾으면 기본 우선순위로 폴백). workspace.json 에 영속.</summary>
    public string? LastActiveTabRef { get; set; }

    /// <summary>사이드바 호환을 위한 세션 전용 동기 뷰(ObservableCollection).
    /// Tabs.CollectionChanged 에서 SessionItem 만 추려 추가/제거한다 → 사이드바 바인딩이 즉시 갱신.</summary>
    public ObservableCollection<SessionItem> Sessions { get; } = new();

    // ── 프로젝트 카드 헤더의 집계 세션 상태 (펼치지 않아도 한눈에) ──
    /// <summary>이 프로젝트의 총 세션 수.</summary>
    public int SessionCount => Sessions.Count;
    /// <summary>살아있는(ConPTY 실행 중) 세션 수.</summary>
    public int AliveSessionCount => Sessions.Count(s => s.IsAlive);
    /// <summary>하나라도 살아있으면 true → 헤더 점을 테마색으로.</summary>
    public bool HasAliveSession => AliveSessionCount > 0;
    /// <summary>요청 처리 중인 세션이 하나라도 있으면 true.</summary>
    public bool HasBusySession => Sessions.Any(s => s.IsBusy);
    /// <summary>헤더 표시용 "살아있음/전체" (세션 없으면 빈 문자열).</summary>
    public string SessionStatusText => Sessions.Count == 0 ? "" : $"{AliveSessionCount}/{Sessions.Count}";

    public ProjectItem()
    {
        // Tabs → Sessions 단방향 동기. 역방향은 코드가 항상 Tabs 에만 추가/제거하도록 강제.
        // 단, Move 액션은 양쪽(탭 스트립·사이드바) 드래그 동기화를 위해 처리한다.
        Tabs.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Move)
            {
                // ObservableCollection.Move 는 OldItems/NewItems 가 비어 있다. 현재 Tabs 의 세션 순서로
                // Sessions 를 incremental Move 로 맞춘다 (한 번에 Reset 하면 UI 가 한꺼번에 리셋되어 깜빡임).
                var ordered = Tabs.OfType<SessionItem>().ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (i >= Sessions.Count) break;
                    if (!ReferenceEquals(Sessions[i], ordered[i]))
                    {
                        int current = Sessions.IndexOf(ordered[i]);
                        if (current > i) Sessions.Move(current, i);
                    }
                }
                return;
            }
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

        // 세션 추가/제거 및 각 세션의 IsAlive/IsBusy 변화 → 헤더 집계 상태 갱신.
        Sessions.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
                foreach (SessionItem s in e.OldItems) s.PropertyChanged -= OnSessionPropChanged;
            if (e.NewItems != null)
                foreach (SessionItem s in e.NewItems) s.PropertyChanged += OnSessionPropChanged;
            RaiseSessionStatus();
        };

        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFiles));
    }

    private void OnSessionPropChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionItem.IsAlive) or nameof(SessionItem.IsBusy))
            RaiseSessionStatus();
    }

    private void RaiseSessionStatus()
    {
        OnPropertyChanged(nameof(SessionCount));
        OnPropertyChanged(nameof(AliveSessionCount));
        OnPropertyChanged(nameof(HasAliveSession));
        OnPropertyChanged(nameof(HasBusySession));
        OnPropertyChanged(nameof(SessionStatusText));
    }

    /// <summary>세션을 세션 기준 새 인덱스(<paramref name="newSessionIndex"/>)로 이동.
    /// Tabs 안의 세션 상대 순서도 동기화하므로 사이드바에서 드래그해도 탭 스트립 위치가 따라가고,
    /// 그 반대도 마찬가지다(탭 스트립은 Tabs.Move 를 직접 호출 → Tabs.CollectionChanged Move 가 Sessions 동기).
    /// 파일 탭은 그 자리에 그대로 남는다(세션 사이를 가로지를 때만 자연스럽게 밀려난다).</summary>
    public void MoveSession(SessionItem session, int newSessionIndex)
    {
        var sessionTabs = Tabs.OfType<SessionItem>().ToList();
        int oldSessionIndex = sessionTabs.IndexOf(session);
        if (oldSessionIndex < 0) return;
        if (newSessionIndex < 0 || newSessionIndex >= sessionTabs.Count) return;
        if (oldSessionIndex == newSessionIndex) return;

        // target = 새 세션 순서에서 newSessionIndex 위치에 있어야 하는 세션.
        // Tabs 안에서 그 세션이 있는 자리에 session 을 삽입하면 (post-Move 인덱스 기준)
        // sessionTabs 의 순서가 newSessionIndex 가 되도록 자연스럽게 맞춰진다.
        // (위로/아래로/맨끝 모든 경우에 동일한 공식이 성립 — 아래 새 Tabs.CollectionChanged Move 핸들러와 세트.)
        var target = sessionTabs[newSessionIndex];
        int oldTabIndex = Tabs.IndexOf(session);
        int newTabIndex = Tabs.IndexOf(target);
        if (oldTabIndex == newTabIndex) return;

        Tabs.Move(oldTabIndex, newTabIndex);
        // Sessions 는 Tabs.CollectionChanged(Move 핸들러)가 incremental Move 로 동기화.
    }

    public static ProjectItem FromPath(string path)
    {
        var name = new DirectoryInfo(path.TrimEnd('\\', '/')).Name;
        if (string.IsNullOrEmpty(name)) name = path; // 드라이브 루트 등
        return new ProjectItem { Path = path, Name = name };
    }
}
