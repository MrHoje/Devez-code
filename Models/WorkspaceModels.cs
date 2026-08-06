using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
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
public enum TabKind { Session, File, Browser, DocumentGroup }

/// <summary>분할(2분할) 시 이 프로젝트가 어느 패널에 떠 있는지. 사이드바 카드의 패널 배지 표시에 사용.
/// None=어느 패널에도 없음(또는 비분할), Left=좌 패널(PaneA), Right=우 패널(PaneB).</summary>
public enum PaneRole { None, Left, Right }

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

    /// <summary>세션 탭 전용 직접 숨김 상태. true면 탭 스트립에서 숨기고 터미널/기록은 보존한다.
    /// 부모/자식의 숨김은 서로 전파하지 않는다. 트리 전체가 숨김일 때만 사이드바 하단으로 이동한다.</summary>
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
    /// <summary>사이드바 검색 중 이 세션 행을 표시할지 여부. 런타임 UI 상태이며 저장하지 않는다.</summary>
    private bool _isSearchVisible = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSearchVisible { get => _isSearchVisible; set => Set(ref _isSearchVisible, value); }

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
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value)) return;
            if (value) StartBusyElapsed();
            else ResetBusyElapsed();
            // 완료 펄스는 setter 원시 전이(서브에이전트 드레인 flap 포함)가 아니라
            // EmitSessionFinished 확정 지점에서만 켠다 → 완료기록과 동일 게이트.
        }
    }

    private DateTimeOffset? _busyStartedAt;
    private DispatcherTimer? _busyElapsedTimer;
    private string _busyElapsedToolTip = "";

    /// <summary>세션 작업 시작 후 경과시간. 사이드바·워크스페이스 탭 스피너 Tooltip 용.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string BusyElapsedToolTip
    {
        get => _busyElapsedToolTip;
        private set => Set(ref _busyElapsedToolTip, value);
    }

    private void StartBusyElapsed()
    {
        _busyStartedAt = DateTimeOffset.UtcNow;
        BusyElapsedToolTip = FormatBusyElapsed(0);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        _busyElapsedTimer ??= new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => UpdateBusyElapsed(),
            dispatcher);
        _busyElapsedTimer.Start();
    }

    private void UpdateBusyElapsed()
    {
        if (!_isBusy || _busyStartedAt == null) return;
        var seconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - _busyStartedAt.Value).TotalSeconds);
        BusyElapsedToolTip = FormatBusyElapsed(seconds);
    }

    private void ResetBusyElapsed()
    {
        _busyElapsedTimer?.Stop();
        _busyStartedAt = null;
        BusyElapsedToolTip = "";
    }

    private static string FormatBusyElapsed(long totalSeconds)
    {
        if (totalSeconds < 60) return $"작업 중 · {totalSeconds}초";
        if (totalSeconds < 3600) return $"작업 중 · {totalSeconds / 60}분 {totalSeconds % 60}초";
        return $"작업 중 · {totalSeconds / 3600}시간 {(totalSeconds % 3600) / 60}분";
    }

    /// <summary>선택지/권한 응답 대기 중인지. true=스피너 대신 ❗(느낌표)를 표시(busy 중이라도 우선).
    /// 선택지·권한을 구분하지 않고 통틀어 '입력 대기'로 다룬다.
    /// claude=Notification/PermissionRequest 훅, opencode=question.asked, gjc=jsonl 'ask'.</summary>
    private bool _isWaitingChoice;
    public bool IsWaitingChoice
    {
        get => _isWaitingChoice;
        // 대기 펄스도 EmitSessionWaiting 확정 지점에서만 켠다(setter 즉발 금지).
        set => Set(ref _isWaitingChoice, value);
    }

    private bool _isCompletionPulsing;

    /// <summary>입력 대기 또는 busy 완료를 사용자가 확인할 때까지 탭·세션 행 펄스를 표시한다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCompletionPulsing
    {
        get => _isCompletionPulsing;
        private set => Set(ref _isCompletionPulsing, value);
    }

    /// <summary>탭·세션 행에 완료/대기 알림 펄스를 켠다. 이미 보고 있는 세션(IsActive)에는 표시하지 않는다.
    /// busy/waiting setter 원시 전이가 아니라 완료·대기 확정 지점(EmitSessionFinished/EmitSessionWaiting)에서만
    /// 호출해야 한다 — 서브에이전트 드레인 flap 같은 가짜 idle 에 깜빡이지 않도록.</summary>
    public void TriggerAttentionPulse()
    {
        // 이미 보고 있는 세션에는 표시하지 않는다.
        if (IsActive) return;

        // 펄스는 공용 위상 시계(Behaviors.AttentionPulse)가 재생하므로 재트리거용 false→true 토글이 필요 없다.
        // 이미 켜져 있으면 그대로 이어가고(끊김 없음), 새로 켜질 땐 시계 위상에 스냅해 다른 펄스와 박자가 맞는다.
        IsCompletionPulsing = true;
    }

    /// <summary>탭을 열거나 다시 클릭했을 때 지속 중인 완료 펄스를 해제한다.</summary>
    public void AcknowledgeCompletionPulse() => IsCompletionPulsing = false;

    /// <summary>마지막으로 보낸 프롬프트(요약 1줄). busy 훅이 떨군 lastmsg 파일에서 갱신. 상단 헤더에 표시.</summary>
    private string _lastMessage = "";
    public string LastMessage { get => _lastMessage; set => Set(ref _lastMessage, value); }

    /// <summary>Claude GUI composer의 전송 전 초안. 탭 전환 동안만 유지하며 workspace.json에는 저장하지 않는다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ComposerDraft { get; set; } = "";

    /// <summary>이 세션이 현재 워크스페이스 패널에서 활성(보고 있는) 세션인지 여부.
    /// 좌측 트리에서 PrimaryBrush 배경 하이라이트에 사용.</summary>
    private bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    /// <summary>이 세션이 잠겼는지 여부. 잠기면 사이드바/탭헤더 우클릭 메뉴에서 삭제·닫기를 숨기고
    /// 동일 위치에 "잠금 해제"를 표시한다. 프로젝트 삭제 시 잠긴 세션이 있으면 차단.</summary>
    private bool _isLocked;
    public bool IsLocked { get => _isLocked; set => Set(ref _isLocked, value); }
    /// <summary>같은 세션 ID가 외부 터미널에서 실행 중인지. 재시작 후에도 내부 중복 실행을 막기 위해 저장한다.</summary>
    private bool _isExternal;
    public bool IsExternal { get => _isExternal; set => Set(ref _isExternal, value); }
    /// <summary>사이드바 Ctrl+클릭 다중 선택 UI 상태. 런타임 전용이며 저장하지 않는다.</summary>
    private bool _isMultiSelectMode;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMultiSelectMode { get => _isMultiSelectMode; set => Set(ref _isMultiSelectMode, value); }

    private bool _isMultiSelected;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMultiSelected { get => _isMultiSelected; set => Set(ref _isMultiSelected, value); }

    /// <summary>사이드바 세션 트리의 직접 부모 ID. null이면 최상위 세션. workspace.json에 영속.</summary>
    private string? _parentSessionId;
    public string? ParentSessionId
    {
        get => _parentSessionId;
        set => Set(ref _parentSessionId, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    /// <summary>부모 세션의 자식 행을 사이드바에서 펼칠지 여부. workspace.json에 영속.</summary>
    private bool _areSessionChildrenExpanded = true;
    public bool AreSessionChildrenExpanded
    {
        get => _areSessionChildrenExpanded;
        set => Set(ref _areSessionChildrenExpanded, value);
    }

    /// <summary>이 세션 자체가 숨김이면 true. 탭/프로세스 표시 판정용 런타임 파생 상태.</summary>
    private bool _isEffectivelyHidden;
    public bool IsEffectivelyHidden { get => _isEffectivelyHidden; private set => Set(ref _isEffectivelyHidden, value); }

    /// <summary>이 세션이 속한 트리 전체가 숨김이면 true. 사이드바 하단 숨김 목록 이동 판정용.</summary>
    private bool _isSidebarGloballyHidden;
    public bool IsSidebarGloballyHidden { get => _isSidebarGloballyHidden; private set => Set(ref _isSidebarGloballyHidden, value); }

    /// <summary>세션 드래그 중, 이 세션이 자식 편입 대상이 될 수 있어 행 우측에 편입 화살표를 보여줄지 여부.
    /// 화살표 위에서만 자식 편입이 동작하고 나머지 영역은 순서 이동이다. 런타임 전용(비영속).</summary>
    private bool _isChildDropHintVisible;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsChildDropHintVisible { get => _isChildDropHintVisible; set => Set(ref _isChildDropHintVisible, value); }

    /// <summary>편입 화살표 위에 커서가 올라가 자식 편입이 대기 중인지 여부(화살표 강조). 런타임 전용(비영속).</summary>
    private bool _isChildDropHintActive;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsChildDropHintActive { get => _isChildDropHintActive; set => Set(ref _isChildDropHintActive, value); }

    private bool _hasSessionChildren;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSessionChildren { get => _hasSessionChildren; private set => Set(ref _hasSessionChildren, value); }

    private int _sessionChildCount;
    [System.Text.Json.Serialization.JsonIgnore]
    public int SessionChildCount { get => _sessionChildCount; private set => Set(ref _sessionChildCount, value); }

    private bool _isSessionTreeVisible = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSessionTreeVisible { get => _isSessionTreeVisible; private set => Set(ref _isSessionTreeVisible, value); }

    private int _treeDepth;
    public int TreeDepth
    {
        get => _treeDepth;
        private set
        {
            if (!Set(ref _treeDepth, value)) return;
            OnPropertyChanged(nameof(TreeIndent));
        }
    }

    private bool _hasSessionParent;
    public bool HasSessionParent { get => _hasSessionParent; private set => Set(ref _hasSessionParent, value); }

    /// <summary>사이드바 자식 세션 행과 연결선의 트리 들여쓰기 폭.</summary>
    public System.Windows.GridLength TreeIndent => new(TreeDepth * 19d);

    /// <summary>분할로 부모와 반대 패널 그룹에 표시될 때, 행 위(원래 부모 자리)에 라벨로 보여줄 부모 세션.
    /// 같은 부모의 연속 자식 묶음에는 첫 자식에만 설정된다. 런타임 파생 상태(비영속).</summary>
    private SessionItem? _detachedParentSession;
    [System.Text.Json.Serialization.JsonIgnore]
    public SessionItem? DetachedParentSession
    {
        get => _detachedParentSession;
        internal set
        {
            if (Set(ref _detachedParentSession, value))
                OnPropertyChanged(nameof(HasDetachedParentLabel));
        }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasDetachedParentLabel => _detachedParentSession != null;

    internal void ApplyTreePresentation(
        int depth,
        bool hasParent,
        int childCount,
        bool treeVisible,
        bool effectivelyHidden,
        bool globallyHidden)
    {
        TreeDepth = Math.Max(0, depth);
        HasSessionParent = hasParent;
        HasSessionChildren = childCount > 0;
        SessionChildCount = childCount;
        IsSessionTreeVisible = treeVisible;
        IsEffectivelyHidden = effectivelyHidden;
        IsSidebarGloballyHidden = globallyHidden;
    }
}

/// <summary>우측 세션 완료 기록 패널에 쌓는 런타임 완료 이벤트. 최신 항목이 위에 표시된다.</summary>
public sealed class SessionCompletionRecord : NotifyBase
{
    public required string SessionId { get; init; }
    // 이름 변경 시 완료기록 라이브 갱신 위해 settable + 알림. (폴더/세션 rename → 기록에도 반영)
    private string _sessionName = "";
    public required string SessionName { get => _sessionName; set => Set(ref _sessionName, value); }
    private string _projectName = "";
    public required string ProjectName
    {
        get => _projectName;
        set { if (Set(ref _projectName, value)) OnPropertyChanged(nameof(ProjectVisibility)); }
    }
    public required string AgentId { get; init; }
    /// <summary>테마 변경 시 아이콘 Source 바인딩을 재평가시키는 트리거(opencode 등 흑/백 변형용).
    /// 값은 그대로, PropertyChanged(AgentId)만 발생시켜 AgentImageConverter 를 다시 돌린다.</summary>
    public void RefreshAgentIcon() => OnPropertyChanged(nameof(AgentId));
    public string LastMessage { get; set; } = "";
    public DateTime CompletedAt { get; init; } = DateTime.Now;
    /// <summary>사용자가 이 기록을 확인했는지 여부. 카드 클릭 또는 해당 세션 직접 열기 시 true.</summary>
    private bool _isRead;
    public bool IsRead { get => _isRead; set => Set(ref _isRead, value); }

    /// <summary>사용자가 이 기록을 체크(표시)했는지 여부. 우클릭 메뉴로 토글.
    /// true 면 카드의 에이전트 아이콘 왼쪽에 테마색 체크 아이콘을 표시한다.</summary>
    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    private bool _isFirstInHistory;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFirstInHistory { get => _isFirstInHistory; set => Set(ref _isFirstInHistory, value); }

    private bool _isLastInHistory;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLastInHistory { get => _isLastInHistory; set => Set(ref _isLastInHistory, value); }

    /// <summary>이 기록의 프로젝트가 더 이상 존재하지 않는지(삭제됨). 파생 상태 — 직렬화 대상 아님.
    /// true 면 프로젝트명에 취소선(strikeout)을 그린다.</summary>
    private bool _projectMissing;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ProjectMissing { get => _projectMissing; set { if (Set(ref _projectMissing, value)) OnPropertyChanged(nameof(ProjectDecoration)); } }

    /// <summary>이 세션이 어느 프로젝트 카드에도 더 이상 추적되지 않는지(탭 닫힘/삭제). 파생 상태 — 직렬화 대상 아님.
    /// true 면 세션명에 취소선(strikeout)을 그린다.</summary>
    private bool _sessionMissing;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SessionMissing { get => _sessionMissing; set { if (Set(ref _sessionMissing, value)) OnPropertyChanged(nameof(SessionDecoration)); } }

    /// <summary>프로젝트명 취소선(삭제된 프로젝트) — null 이면 장식 없음.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.TextDecorationCollection? ProjectDecoration
        => _projectMissing ? System.Windows.TextDecorations.Strikethrough : null;

    /// <summary>세션명 취소선(추적 안 되는 세션) — null 이면 장식 없음.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.TextDecorationCollection? SessionDecoration
        => _sessionMissing ? System.Windows.TextDecorations.Strikethrough : null;

    public string TimeText => CompletedAt.ToString("HH:mm");
    public System.Windows.Visibility ProjectVisibility
        => string.IsNullOrWhiteSpace(ProjectName) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility LastMessageVisibility
        => string.IsNullOrWhiteSpace(LastMessage) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}

/// <summary>파일 편집기 탭. 파일 탐색기에서 텍스트 파일을 더블클릭하면 새로 열린다.
/// 각 탭은 자신만의 FileEditorView 인스턴스를 갖고, 닫히면 디스크에서 제거(영속 X).</summary>
public sealed class FileTabItem : TabItemBase
{
    public override TabKind Kind => TabKind.File;
    public override string Title => string.IsNullOrEmpty(FilePath)
        ? "파일"
        : (IsDiff ? "Diff - " : "") + Path.GetFileName(FilePath);

    /// <summary>diff 뷰용 파일 탭인지. true 면 제목에 표시하고 workspace.json 영속화에서 제외(전환형).</summary>
    public bool IsDiff { get; init; }

    /// <summary>편집 대상 절대 경로. 비교는 OrdinalIgnoreCase.</summary>
    public string FilePath { get; init; } = "";

    /// <summary>이 파일 탭이 현재 어느 패널에서 활성(보고 있는)인지 — 사이드바 카드 하이라이트용(세션 IsActive 대응).</summary>
    private bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    private bool _isDocumentGrouped;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDocumentGrouped
    {
        get => _isDocumentGrouped;
        internal set => Set(ref _isDocumentGrouped, value);
    }

    private bool _usesAlternateDocumentGroupColor;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesAlternateDocumentGroupColor
    {
        get => _usesAlternateDocumentGroupColor;
        internal set => Set(ref _usesAlternateDocumentGroupColor, value);
    }

    private bool _startsDocumentGroup;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool StartsDocumentGroup
    {
        get => _startsDocumentGroup;
        internal set => Set(ref _startsDocumentGroup, value);
    }

    /// <summary>탭마다 1개의 파일 편집기 인스턴스. 콘텐츠 호스트에 그대로 붙여 렌더한다.</summary>
    public IFileTabEditor Editor { get; init; } = new FileEditorView();

    /// <summary>탭을 닫을 때 외부에서 호출: dirty 확인 후 비로소 제거해도 되는지 결과를 받는다.</summary>
    public event EventHandler? CloseRequested;

    internal void RaiseCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>프로젝트 카드에서 열린 문서를 묶어 보여주는 폴더형 그룹.
/// 중앙 탭 컬렉션에는 들어가지 않고, Documents 가 가리키는 실제 파일 탭만 이동한다.</summary>
public sealed class DocumentGroupItem : TabItemBase
{
    public override TabKind Kind => TabKind.DocumentGroup;
    public override string Title => Name;

    private string _name = "문서 그룹";
    public string Name
    {
        get => _name;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "문서 그룹" : value.Trim();
            if (!Set(ref _name, normalized)) return;
            OnPropertyChanged(nameof(Title));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value)) return;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public ObservableCollection<FileTabItem> Documents { get; } = new();
    public int DocumentCount => Documents.Count;
    public string DocumentCountText => $"{DocumentCount}개";

    public event EventHandler? Changed;

    public DocumentGroupItem()
    {
        Documents.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DocumentCount));
            OnPropertyChanged(nameof(DocumentCountText));
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }
}

/// <summary>파일 탭 복원 전까지 문서 그룹 정보를 임시 보관하는 workspace 로드 스냅샷.</summary>
public sealed class DocumentGroupSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "문서 그룹";
    public bool IsExpanded { get; set; } = true;
    public List<string> FilePaths { get; set; } = new();
}

/// <summary>중앙 영역에 표시하는 WebView2 브라우저 탭. 탭 ID를 키로 방문 기록을 로컬 저장한다.</summary>
public sealed class BrowserTabItem : TabItemBase
{
    public override TabKind Kind => TabKind.Browser;
    public override string Title => Name;

    private string _name = "웹 브라우저";
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) OnPropertyChanged(nameof(Title)); }
    }

    /// <summary>settings.json 의 브라우저 URL/히스토리 저장 키. 프로젝트 경로와 분리해 탭별 상태를 유지한다.</summary>
    public string PersistenceKey => "__workspace_browser_tab__:" + Id;

    /// <summary>탭마다 독립 WebView2 인스턴스와 방문 기록을 가진다.</summary>
    public BrowserHostView Browser { get; init; } = new();

    /// <summary>이 탭이 특정 세션(방)의 전용 자동화 브라우저면 그 roomId. 일반 탭은 null.
    /// 세션이 MCP 브라우저 도구를 처음 쓸 때 생성되며, 비활성 동안 화면 밖에 파킹돼 백그라운드로 동작한다.</summary>
    public string? AutomationRoomId { get; set; }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
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

/// <summary>프로젝트 영역의 논리 폴더. 프로젝트 목록과 검색 표시 상태는 런타임에서 구성된다.</summary>
public sealed class ProjectFolderItem : NotifyBase
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _iconKey = FolderIconCatalog.DefaultKey;
    public string IconKey
    {
        get => _iconKey;
        set
        {
            if (Set(ref _iconKey, FolderIconCatalog.Normalize(value)))
                OnPropertyChanged(nameof(IconGeometry));
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Media.Geometry? IconGeometry =>
        System.Windows.Application.Current?.TryFindResource(IconKey) as System.Windows.Media.Geometry
        ?? System.Windows.Application.Current?.TryFindResource(FolderIconCatalog.DefaultKey) as System.Windows.Media.Geometry;

    private int _rootOrder = int.MaxValue;
    public int RootOrder { get => _rootOrder; set => Set(ref _rootOrder, Math.Max(0, value)); }

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    // 폴더 내부 프로젝트 목록의 열 수 선호(true=좌/우 2열, false=1열). workspace.json 영속.
    // 실제 적용은 전역 "프로젝트 목록 열 수"가 2일 때만(ColumnToggleAvailable). 전역 1열이면 항상 1열.
    private bool _twoColumn = true;
    public bool TwoColumn { get => _twoColumn; set { if (Set(ref _twoColumn, value)) RecomputeColumns(); } }

    // 전역 열 수가 2인지 — 열 토글 버튼 가시성 + 2열 적용 게이트. 런타임 전용(영속 안 함).
    private bool _columnToggleAvailable;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ColumnToggleAvailable { get => _columnToggleAvailable; set { if (Set(ref _columnToggleAvailable, value)) RecomputeColumns(); } }

    // 내부 패널이 실제로 쓰는 열 수(1/2) — 폴더 body ItemsControl.Tag 바인딩 대상. 런타임 전용.
    private int _effectiveColumns = 1;
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveColumns { get => _effectiveColumns; private set => Set(ref _effectiveColumns, value); }

    private void RecomputeColumns() => EffectiveColumns = ColumnToggleAvailable && TwoColumn ? 2 : 1;

    // 2열 보기에서 1열(반폭) 폴더가 놓인 컬럼(0=좌, 1=우). 드래그로 변경, workspace.json 영속.
    // 2열(전체폭) 폴더에선 무시. ProjectItem.Column 과 동일 역할.
    private int _column;
    public int Column { get => _column; set => Set(ref _column, value == 1 ? 1 : 0); }

    private string? _archivedAt;
    public string? ArchivedAt
    {
        get => _archivedAt;
        set
        {
            if (!Set(ref _archivedAt, string.IsNullOrWhiteSpace(value) ? null : value)) return;
            OnPropertyChanged(nameof(IsArchived));
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public bool IsArchived => ArchivedAt != null;
    public bool IsActive => ArchivedAt == null;

    [System.Text.Json.Serialization.JsonIgnore]
    public ObservableCollection<ProjectItem> Projects { get; } = new();

    private bool _hasBusySession;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasBusySession
    {
        get => _hasBusySession;
        private set => Set(ref _hasBusySession, value);
    }

    private bool _hasSelectedProject;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSelectedProject
    {
        get => _hasSelectedProject;
        private set => Set(ref _hasSelectedProject, value);
    }

    private string _projectCountText = "0개";
    [System.Text.Json.Serialization.JsonIgnore]
    public string ProjectCountText
    {
        get => _projectCountText;
        private set => Set(ref _projectCountText, value);
    }

    internal void UpdateSummary(
        IReadOnlyCollection<ProjectItem> allProjects,
        int visibleCount,
        bool hasQuery)
    {
        HasBusySession = allProjects.Any(project => project.HasBusySession);
        HasSelectedProject = allProjects.Any(project => project.IsSelected);
        ProjectCountText = hasQuery && visibleCount < allProjects.Count
            ? $"{visibleCount}/{allProjects.Count}개"
            : $"{allProjects.Count}개";
    }

    private bool _isSearchVisible = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSearchVisible { get => _isSearchVisible; set => Set(ref _isSearchVisible, value); }
}

/// <summary>프로젝트 카드에 저장 가능한 마커 색상 키.</summary>
public static class ProjectMarkerPalette
{
    public const string None = "none";
    public const string Red = "red";
    public const string Orange = "orange";
    public const string Yellow = "yellow";
    public const string Green = "green";
    public const string Teal = "teal";
    public const string Blue = "blue";
    public const string Purple = "purple";
    public const string Pink = "pink";

    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        Red, Orange, Yellow, Green, Teal, Blue, Purple, Pink,
    };

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return Values.Contains(normalized) ? normalized : null;
    }
}

/// <summary>좌측 트리의 프로젝트(= 디렉터리). 하위에 탭(세션/파일) 목록을 가진다.</summary>
public sealed class ProjectItem : NotifyBase
{
    public string Path { get; init; } = "";
    private string? _folderId;
    public string? FolderId
    {
        get => _folderId;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (Set(ref _folderId, normalized))
            {
                OnPropertyChanged(nameof(HasProjectFolder));
                OnPropertyChanged(nameof(CanArchive));
                OnPropertyChanged(nameof(CanUnarchive));
            }
        }
    }

    public bool HasProjectFolder => FolderId != null;
    public bool CanArchive => IsActive && !HasProjectFolder;
    public bool CanUnarchive => IsArchived && !HasProjectFolder;

    private int _rootOrder = int.MaxValue;
    public int RootOrder { get => _rootOrder; set => Set(ref _rootOrder, Math.Max(0, value)); }

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

    private string? _markerColor;
    /// <summary>프로젝트 카드 왼쪽에 표시할 마커 색상 키. null이면 마커 없음.</summary>
    public string? MarkerColor
    {
        get => _markerColor;
        set
        {
            if (!Set(ref _markerColor, ProjectMarkerPalette.Normalize(value))) return;
            OnPropertyChanged(nameof(HasMarker));
            OnPropertyChanged(nameof(MarkerMenuHeader));
        }
    }

    public bool HasMarker => _markerColor != null;
    public string MarkerMenuHeader => HasMarker ? "마커 수정" : "마커 추가";

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>2분할 시 이 프로젝트가 떠 있는 패널(좌/우). 비분할이거나 어느 패널에도 없으면 None.
    /// 사이드바 카드 헤더의 패널 배지(좌/우 칸 하이라이트) 가시성·방향 분기에 사용. 영속 대상 아님.</summary>
    private PaneRole _paneRole;
    public PaneRole PaneRole { get => _paneRole; set => Set(ref _paneRole, value); }

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
        set
        {
            if (!Set(ref _archivedAt, value)) return;
            OnPropertyChanged(nameof(IsArchived));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ArchivedDateDisplay));
            OnPropertyChanged(nameof(CanArchive));
            OnPropertyChanged(nameof(CanUnarchive));
        }
    }

    /// <summary>보관함 소속 여부.</summary>
    public bool IsArchived => !string.IsNullOrEmpty(ArchivedAt);

    /// <summary>활성(비보관) 여부 — 컨텍스트 메뉴 항목 가시성 분기용.</summary>
    public bool IsActive => string.IsNullOrEmpty(ArchivedAt);

    /// <summary>보관함 카드 표시용 "보관: yyyy-MM-dd" (활성이면 빈 문자열).</summary>
    public string ArchivedDateDisplay => string.IsNullOrEmpty(ArchivedAt) ? ""
        : "보관: " + (ArchivedAt.Length >= 10 ? ArchivedAt[..10] : ArchivedAt);

    /// <summary>중앙 탭 스트립에 그대로 바인딩되는 통합 컬렉션(세션 + 파일 + 브라우저 탭).
    /// 사이드바는 Sessions(동기 뷰)로 세션만 골라 렌더한다.</summary>
    public ObservableCollection<TabItemBase> Tabs { get; } = new();

    /// <summary>프로젝트 카드에서 열린 문서들을 폴더처럼 묶는 그룹. 실제 탭은 Tabs 에 그대로 유지된다.</summary>
    public ObservableCollection<DocumentGroupItem> DocumentGroups { get; } = new();

    /// <summary>재시작 복원용 — workspace.json 에서 읽은 "직전에 열려 있던 파일 탭 경로" 목록.
    /// 모델엔 임시 보관만 하고(직렬화 대상 아님), 시작 시 WorkspacePaneView.RestoreFileTabs 가
    /// 한 번 소비해 실제 FileTabItem 으로 만든다.</summary>
    public List<string> PendingOpenFiles { get; set; } = new();

    /// <summary>재시작 복원용 — 저장 시점의 전체 탭 순서(세션+문서+브라우저, "S:id"/"F:path"/"B:id"). 시작 시
    /// 세션+파일+브라우저 탭이 모두 복원된 뒤 이 순서로 Tabs 를 1회 재배열한다(문서가 끝으로 몰려 끼임 순서를 잃는 것 방지).</summary>
    public List<string> PendingTabOrder { get; set; } = new();

    /// <summary>파일 탭이 생성된 뒤 DocumentGroups 로 복원할 임시 상태.</summary>
    public List<DocumentGroupSnapshot> PendingDocumentGroups { get; set; } = new();

    /// <summary>이 프로젝트에서 마지막으로 활성화했던 탭 참조. 형식: "S:&lt;세션ID&gt;", "F:&lt;파일경로&gt;", "B:&lt;브라우저ID&gt;".
    /// 프로젝트를 다시 선택할 때 이 탭을 복원한다(없거나 못 찾으면 기본 우선순위로 폴백). workspace.json 에 영속.</summary>
    public string? LastActiveTabRef { get; set; }

    /// <summary>이 프로젝트가 메인 패널에 뜰 때 분할(2패널)을 함께 켜둘지. 탭바 분할 토글 버튼으로 설정.
    /// 프로젝트를 다시 열면 이 값대로 분할 상태가 복원된다. workspace.json 에 영속.</summary>
    public bool SplitEnabled { get; set; }
    /// <summary>분할 파트너 프로젝트 경로(파트너 세션이 없을 때 폴백). workspace.json 에 영속.</summary>
    public string? SplitPartnerProjectPath { get; set; }
    /// <summary>분할 파트너 세션 ID(있으면 우선). workspace.json 에 영속.</summary>
    public string? SplitPartnerSessionId { get; set; }
    /// <summary>분할 파트너 파일 경로(우측 패널이 파일 탭이었을 때). 세션ID보다 우선순위 낮음. workspace.json 에 영속.</summary>
    public string? SplitPartnerFilePath { get; set; }

    /// <summary>같은 프로젝트를 분할했을 때 우측 패널에 격리해 둔 탭들의 참조 목록("S:&lt;id&gt;"/"F:&lt;path&gt;"/"B:&lt;id&gt;").
    /// 파트너 하나만이 아니라 우측 전체 탭 집합을 복원하기 위함(비면 파트너 필드로 폴백). workspace.json 에 영속.</summary>
    public List<string> SplitRightTabRefs { get; set; } = new();

    // ── 사이드바 카드: '현재 실제 분할된' 프로젝트만 좌/우 패널 탭 그룹으로 보인다. 그룹 내용은 MainWindow 가
    // 라이브 패널 상태(각 패널의 실제 표시 탭)로 밀어넣는다(ApplyLiveGroups). 비분할이면 LeftItems=전체 탭.
    /// <summary>카드 상단 그룹 = 비분할이면 전체 탭, 분할이면 '좌측 패널' 탭(세션+열린 문서). 사이드바 바인딩.</summary>
    public ObservableCollection<TabItemBase> LeftItems { get; } = new();
    /// <summary>카드 하단 그룹 = 분할일 때 '우측 패널' 탭(세션+열린 문서). 비분할이면 빈다. 사이드바 바인딩.</summary>
    public ObservableCollection<TabItemBase> RightItems { get; } = new();
    private List<TabItemBase>? _liveLeftSource;
    private List<TabItemBase>? _liveRightSource;

    private bool _isSplitView;
    /// <summary>이 프로젝트가 지금 실제로 분할(좌/우) 표시 중인지 — 카드가 좌/우 그룹 라벨/우측그룹을 보일지 분기.</summary>
    public bool IsSplitView { get => _isSplitView; private set => Set(ref _isSplitView, value); }
    private bool _hasRightItems;
    /// <summary>우측 그룹에 표시할 탭이 있는지 — 우측 그룹+세퍼레이터+라벨 가시성.</summary>
    public bool HasRightItems { get => _hasRightItems; private set => Set(ref _hasRightItems, value); }

    /// <summary>라이브 분할 그룹 적용 — MainWindow 가 좌/우 패널의 실제 표시 탭으로 밀어넣는다(세션+문서).</summary>
    public void ApplyLiveGroups(IReadOnlyList<TabItemBase> left, IReadOnlyList<TabItemBase> right)
    {
        _liveLeftSource = left.ToList();
        _liveRightSource = right.ToList();
        HasRightItems = right.Count > 0;
        IsSplitView = right.Count > 0;
        RefreshSidebarGroups();
    }

    /// <summary>비분할(단일 목록)로 되돌림 — 상단 그룹=전체 탭, 우측 그룹 비움.</summary>
    public void ClearLiveGroups()
    {
        _liveLeftSource = null;
        _liveRightSource = null;
        HasRightItems = false;
        IsSplitView = false;
        RefreshSidebarGroups();
    }

    /// <summary>desired 와 최소 변경(제거/삽입/이동)으로 동기화. Clear+전체 재추가를 쓰면 Count 가
    /// 순간 0 이 되어 HasHiddenSessions/ShowHiddenGroup 이 false 로 튀고, 숨김 그룹 TreeExpander 가
    /// 접힘→펼침 애니를 매번 재생해 그룹 전체가 사라졌다 자라나는 깜빡임이 생긴다(탭 X 숨기기·숨김 해제 시).</summary>
    private static void SyncObservable<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        if (target.Count == desired.Count)
        {
            bool identical = true;
            for (int i = 0; i < target.Count; i++)
            {
                if (ReferenceEquals(target[i], desired[i])) continue;
                identical = false;
                break;
            }
            if (identical) return;
        }

        var desiredSet = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!desiredSet.Contains(target[i])) target.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;

            int cur = -1;
            for (int j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], desired[i])) { cur = j; break; }
            if (cur < 0) target.Insert(i, desired[i]);
            else target.Move(cur, i);
        }
    }
    /// <summary>우측 패널에서 활성이던 탭 참조. LastActiveTabRef 는 좌측 활성 탭용. workspace.json 에 영속.</summary>
    public string? SplitRightActiveRef { get; set; }

    /// <summary>사이드바 호환을 위한 세션 전용 동기 뷰(ObservableCollection).
    /// Tabs.CollectionChanged 에서 SessionItem 만 추려 추가/제거한다 → 사이드바 바인딩이 즉시 갱신.</summary>
    public ObservableCollection<SessionItem> Sessions { get; } = new();
    private string _sidebarSearchQuery = "";
    private bool _showAllSidebarSessions = true;
    private string _sessionSearchQuery = "";
    private bool _isSessionSearchOpen;

    /// <summary>프로젝트 카드 내부 세션 검색어. 런타임 UI 상태이며 저장하지 않는다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SessionSearchQuery
    {
        get => _sessionSearchQuery;
        set
        {
            var query = value ?? "";
            if (!Set(ref _sessionSearchQuery, query)) return;
            OnPropertyChanged(nameof(HasSessionSearchQuery));
            OnPropertyChanged(nameof(ShowHiddenSessionsInCurrentView));
            OnPropertyChanged(nameof(ShowHiddenGroup));
            foreach (var session in Sessions)
                ApplySidebarSearch(session);
            RefreshSidebarGroups();
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSessionSearchQuery => _sessionSearchQuery.Length > 0;

    /// <summary>프로젝트 카드 내부 세션 검색창의 열림 상태. 런타임 UI 상태이며 저장하지 않는다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSessionSearchOpen
    {
        get => _isSessionSearchOpen;
        set => Set(ref _isSessionSearchOpen, value);
    }

    /// <summary>
    /// 프로젝트/폴더 이름이 검색어와 일치하면 전체 세션을, 세션 이름만 일치하면 해당 세션만 표시한다.
    /// 검색 조건을 보관해 검색 중 추가되거나 이름이 바뀐 세션에도 같은 필터를 적용한다.
    /// </summary>
    public void ApplySidebarSearch(string query, bool showAllSessions)
    {
        var normalizedQuery = query ?? "";
        bool queryChanged = !StringComparer.Ordinal.Equals(_sidebarSearchQuery, normalizedQuery);
        _sidebarSearchQuery = normalizedQuery;
        _showAllSidebarSessions = showAllSessions;
        if (queryChanged)
        {
            OnPropertyChanged(nameof(ShowHiddenSessionsInCurrentView));
            OnPropertyChanged(nameof(ShowHiddenGroup));
        }
        foreach (var session in Sessions)
            ApplySidebarSearch(session);
        RefreshSidebarGroups(); // IsSearchVisible 이 라벨 캐리어 선정에 쓰이므로 재계산
    }

    private void ApplySidebarSearch(SessionItem session)
        => session.IsSearchVisible =
            (_showAllSidebarSessions ||
             session.Name.Contains(_sidebarSearchQuery, StringComparison.OrdinalIgnoreCase)) &&
            (_sessionSearchQuery.Length == 0 ||
             session.Name.Contains(_sessionSearchQuery, StringComparison.OrdinalIgnoreCase));

    /// <summary>구성원 전체가 숨겨진 최상위 세션 트리를 모은 뷰. 일부 자식이 보이면 트리 전체를
    /// 원래 위치에 유지하고 숨긴 자식만 부모 아래에서 후순위로 표시한다.</summary>
    public ObservableCollection<SessionItem> HiddenSessions { get; } = new();
    /// <summary>숨김 세션이 하나라도 있는지 — 숨김 그룹 세퍼레이터/표시 여부.</summary>
    public bool HasHiddenSessions => HiddenSessions.Count > 0;
    /// <summary>평소 숨김 표시 토글이 켜졌거나 검색 중이면 숨김 세션도 현재 결과에 포함한다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowHiddenSessionsInCurrentView =>
        ShowHiddenSessions || _sidebarSearchQuery.Length > 0 || _sessionSearchQuery.Length > 0;
    /// <summary>현재 보기에서 숨김 세션을 표시하고 실제 숨김 세션이 있을 때만 숨김 그룹을 보인다.</summary>
    public bool ShowHiddenGroup => ShowHiddenSessionsInCurrentView && HasHiddenSessions;

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

    private bool _showHiddenSessions = true;
    public bool ShowHiddenSessions
    {
        get => _showHiddenSessions;
        set
        {
            if (!Set(ref _showHiddenSessions, value)) return;
            OnPropertyChanged(nameof(ShowHiddenSessionsInCurrentView));
            OnPropertyChanged(nameof(ShowHiddenGroup));
            RefreshSidebarGroups(); // 접힘 여부가 라벨 캐리어 선정에 쓰이므로 재계산
        }
    }

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
                foreach (SessionItem s in e.NewItems)
                {
                    s.PropertyChanged += OnSessionPropChanged;
                    ApplySidebarSearch(s);
                }
            RaiseSessionStatus();
            RefreshSessionTree();
        };
        HiddenSessions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHiddenSessions));
            OnPropertyChanged(nameof(ShowHiddenGroup));
        };

        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFiles));
        DocumentGroups.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
                foreach (DocumentGroupItem group in e.OldItems) group.Changed -= DocumentGroup_Changed;
            if (e.NewItems != null)
                foreach (DocumentGroupItem group in e.NewItems) group.Changed += DocumentGroup_Changed;
            SyncDocumentGroupMembership();
            RefreshSidebarGroups();
        };

        // 탭 추가/제거/이동 → 비분할이면 단일 목록(LeftItems=전체 탭) 자동 갱신.
        // 분할(IsSplitView) 중이면 그룹 내용은 MainWindow(RefreshCardGroups)가 라이브로 주입하지만,
        // '삭제'만은 즉시 반영한다 — 안 그러면 탭 헤더에선 지워져도 카드엔 남는다(새로고침 누락).
        Tabs.CollectionChanged += (_, e) =>
        {
            NormalizeDocumentGroupsAfterTabsChanged(e);
            SyncDocumentGroupMembership();
            if (!IsSplitView) { RefreshSidebarGroups(); return; }
            if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset)
            {
                var live = new HashSet<TabItemBase>(Tabs);
                _liveLeftSource?.RemoveAll(t => !live.Contains(t));
                _liveRightSource?.RemoveAll(t => !live.Contains(t));
            }
            RefreshSidebarGroups();
        };
        RefreshSidebarGroups();
    }

    private bool _movingDocumentGroup;

    private void DocumentGroup_Changed(object? sender, EventArgs e)
    {
        SyncDocumentGroupMembership();
        RefreshSidebarGroups();
    }

    private void SyncDocumentGroupMembership()
    {
        var orderedGroups = DocumentGroups
            .Select(group => new
            {
                Group = group,
                FirstTabIndex = group.Documents
                    .Select(Tabs.IndexOf)
                    .Where(index => index >= 0)
                    .DefaultIfEmpty(int.MaxValue)
                    .Min(),
            })
            .OrderBy(item => item.FirstTabIndex)
            .ToList();
        var groupColors = orderedGroups
            .Select((item, index) => new { item.Group, Alternate = index % 2 == 1 })
            .ToDictionary(item => item.Group, item => item.Alternate);
        var groupStarts = orderedGroups
            .Select(item => item.Group.Documents
                .Where(Tabs.Contains)
                .OrderBy(Tabs.IndexOf)
                .FirstOrDefault())
            .Where(file => file != null)
            .ToHashSet();

        foreach (var file in Tabs.OfType<FileTabItem>())
        {
            var group = DocumentGroupOf(file);
            file.IsDocumentGrouped = group != null;
            file.UsesAlternateDocumentGroupColor =
                group != null && groupColors.GetValueOrDefault(group);
            file.StartsDocumentGroup = groupStarts.Contains(file);
        }
    }

    private void NormalizeDocumentGroupsAfterTabsChanged(NotifyCollectionChangedEventArgs e)
    {
        foreach (var group in DocumentGroups.ToList())
        {
            foreach (var file in group.Documents.Where(file => !Tabs.Contains(file)).ToList())
                group.Documents.Remove(file);
            if (group.Documents.Count == 0)
                DocumentGroups.Remove(group);
            else
                ReorderGroupDocuments(group);
        }

        if (_movingDocumentGroup || e.Action != NotifyCollectionChangedAction.Move) return;

        var moved = e.NewItems?.OfType<TabItemBase>().FirstOrDefault();
        if (moved is FileTabItem { IsDiff: false } movedDocument)
        {
            int movedIndex = Tabs.IndexOf(movedDocument);
            var targetGroup = DocumentGroups
                .Where(group => !group.Documents.Contains(movedDocument))
                .Select(group => new
                {
                    Group = group,
                    Indexes = group.Documents
                        .Select(Tabs.IndexOf)
                        .Where(index => index >= 0)
                        .Order()
                        .ToList(),
                })
                .FirstOrDefault(candidate =>
                    candidate.Indexes.Count >= 2
                    && movedIndex > candidate.Indexes[0]
                    && movedIndex < candidate.Indexes[^1])
                ?.Group;

            if (targetGroup != null)
            {
                var previousGroup = DocumentGroupOf(movedDocument);
                previousGroup?.Documents.Remove(movedDocument);
                if (previousGroup?.Documents.Count == 0)
                    DocumentGroups.Remove(previousGroup);

                targetGroup.Documents.Add(movedDocument);
                ReorderGroupDocuments(targetGroup);
            }
        }

        foreach (var group in DocumentGroups.ToList())
        {
            var indexes = group.Documents.Select(Tabs.IndexOf).Where(index => index >= 0).Order().ToList();
            bool contiguous = indexes.Count < 2 || indexes[^1] - indexes[0] + 1 == indexes.Count;
            if (contiguous) continue;

            if (moved is FileTabItem movedFile && group.Documents.Contains(movedFile))
                group.Documents.Remove(movedFile);
            else
                DocumentGroups.Remove(group);
        }
    }

    private void ReorderGroupDocuments(DocumentGroupItem group)
    {
        var ordered = group.Documents.OrderBy(Tabs.IndexOf).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int current = group.Documents.IndexOf(ordered[i]);
            if (current != i) group.Documents.Move(current, i);
        }
    }

    public DocumentGroupItem? DocumentGroupOf(FileTabItem file)
        => DocumentGroups.FirstOrDefault(group => group.Documents.Contains(file));

    public bool CanAddDocumentToGroup(DocumentGroupItem group, FileTabItem file)
    {
        if (file.IsDiff || !DocumentGroups.Contains(group) || !Tabs.Contains(file)
            || group.Documents.Contains(file))
            return false;
        return true;
    }

    public DocumentGroupItem? CreateDocumentGroup(FileTabItem file, string name)
    {
        if (file.IsDiff || !Tabs.Contains(file)) return null;
        RemoveDocumentFromGroup(file);
        var group = new DocumentGroupItem { Name = name };
        group.Documents.Add(file);
        DocumentGroups.Add(group);
        return group;
    }

    public bool AddDocumentToGroup(DocumentGroupItem group, FileTabItem file)
    {
        if (!CanAddDocumentToGroup(group, file)) return false;

        RemoveDocumentFromGroup(file);
        var members = group.Documents.Where(Tabs.Contains).OrderBy(Tabs.IndexOf).ToList();
        if (members.Count > 0)
        {
            var desired = Tabs.Where(tab => !ReferenceEquals(tab, file)).ToList();
            int anchor = desired.IndexOf(members[^1]);
            if (anchor >= 0)
            {
                desired.Insert(anchor + 1, file);
                ApplyDocumentGroupTabOrder(desired);
            }
        }
        group.Documents.Add(file);
        ReorderGroupDocuments(group);
        RefreshSidebarGroups();
        return true;
    }

    private List<FileTabItem> DocumentGroupSideCandidates(DocumentGroupItem group, bool before)
    {
        if (!DocumentGroups.Contains(group)) return new();
        var members = group.Documents.Where(Tabs.Contains).OrderBy(Tabs.IndexOf).ToList();
        if (members.Count == 0) return new();

        int boundary = before ? Tabs.IndexOf(members[0]) : Tabs.IndexOf(members[^1]);
        return Tabs.OfType<FileTabItem>()
            .Where(file => !file.IsDiff
                           && !group.Documents.Contains(file)
                           && (before ? Tabs.IndexOf(file) < boundary : Tabs.IndexOf(file) > boundary))
            .OrderBy(Tabs.IndexOf)
            .ToList();
    }

    public int CountDocumentsOnGroupSide(DocumentGroupItem group, bool before)
        => DocumentGroupSideCandidates(group, before).Count;

    public int AddDocumentsOnGroupSide(DocumentGroupItem group, bool before)
    {
        if (!DocumentGroups.Contains(group)) return 0;
        var members = group.Documents.Where(Tabs.Contains).OrderBy(Tabs.IndexOf).ToList();
        var candidates = DocumentGroupSideCandidates(group, before);
        if (members.Count == 0 || candidates.Count == 0) return 0;

        var moving = members.Concat(candidates).Cast<TabItemBase>().ToHashSet();
        int groupAnchor = Tabs.IndexOf(members[0]);
        int insertAt = Tabs.Take(groupAnchor).Count(tab => !moving.Contains(tab));
        var block = (before ? candidates.Concat(members) : members.Concat(candidates))
            .Cast<TabItemBase>()
            .ToList();
        var desired = Tabs.Where(tab => !moving.Contains(tab)).ToList();
        desired.InsertRange(insertAt, block);

        foreach (var file in candidates)
            DocumentGroupOf(file)?.Documents.Remove(file);
        foreach (var emptyGroup in DocumentGroups
                     .Where(item => !ReferenceEquals(item, group) && item.Documents.Count == 0)
                     .ToList())
            DocumentGroups.Remove(emptyGroup);
        foreach (var file in candidates)
            group.Documents.Add(file);

        ApplyDocumentGroupTabOrder(desired);
        return candidates.Count;
    }

    public bool RemoveDocumentFromGroup(FileTabItem file)
    {
        var group = DocumentGroupOf(file);
        if (group == null) return false;
        group.Documents.Remove(file);
        if (group.Documents.Count == 0) DocumentGroups.Remove(group);
        RefreshSidebarGroups();
        return true;
    }

    public bool RemoveDocumentGroup(DocumentGroupItem group)
    {
        if (!DocumentGroups.Contains(group)) return false;
        DocumentGroups.Remove(group);
        return true;
    }

    public bool MoveDocumentGroupRelativeToItem(DocumentGroupItem group, TabItemBase target, bool after)
    {
        if (!DocumentGroups.Contains(group) || ReferenceEquals(group, target)) return false;

        var members = group.Documents.Where(Tabs.Contains).OrderBy(Tabs.IndexOf).Cast<TabItemBase>().ToList();
        if (members.Count == 0) return false;
        var memberSet = members.ToHashSet();

        List<TabItemBase> targetBlock = target switch
        {
            DocumentGroupItem targetGroup when DocumentGroups.Contains(targetGroup) =>
                targetGroup.Documents.Where(Tabs.Contains).OrderBy(Tabs.IndexOf).Cast<TabItemBase>().ToList(),
            SessionItem session when SessionParentOf(session) == null =>
                GetSessionSubtree(session).Where(Tabs.Contains).Cast<TabItemBase>().ToList(),
            _ when Tabs.Contains(target) => new List<TabItemBase> { target },
            _ => new List<TabItemBase>(),
        };
        if (targetBlock.Count == 0 || targetBlock.Any(memberSet.Contains)) return false;

        var desired = Tabs.Where(tab => !memberSet.Contains(tab)).ToList();
        var targetIndexes = targetBlock.Select(tab => desired.IndexOf(tab)).Where(index => index >= 0).ToList();
        if (targetIndexes.Count == 0) return false;
        int insertAt = after ? targetIndexes.Max() + 1 : targetIndexes.Min();
        desired.InsertRange(insertAt, members);
        if (Tabs.SequenceEqual(desired)) return false;

        ApplyDocumentGroupTabOrder(desired);
        return true;
    }

    private void ApplyDocumentGroupTabOrder(IReadOnlyList<TabItemBase> desired)
    {
        _movingDocumentGroup = true;
        try { SyncObservable(Tabs, desired); }
        finally { _movingDocumentGroup = false; }
        foreach (var group in DocumentGroups) ReorderGroupDocuments(group);
        RefreshSidebarGroups();
    }

    public void RestorePendingDocumentGroups()
    {
        var pending = PendingDocumentGroups;
        PendingDocumentGroups = new();
        if (pending.Count == 0) return;

        var filesByPath = Tabs.OfType<FileTabItem>()
            .Where(file => !file.IsDiff)
            .GroupBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<FileTabItem>();
        foreach (var snapshot in pending)
        {
            var files = snapshot.FilePaths
                .Where(filesByPath.ContainsKey)
                .Select(path => filesByPath[path])
                .Where(claimed.Add)
                .OrderBy(Tabs.IndexOf)
                .ToList();
            if (files.Count == 0) continue;

            var group = new DocumentGroupItem
            {
                Id = string.IsNullOrWhiteSpace(snapshot.Id) ? Guid.NewGuid().ToString("N") : snapshot.Id,
                Name = snapshot.Name,
                IsExpanded = snapshot.IsExpanded,
            };
            foreach (var file in files) group.Documents.Add(file);
            DocumentGroups.Add(group);
        }
        RefreshSidebarGroups();
    }

    private void OnSessionPropChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionItem.IsAlive) or nameof(SessionItem.IsBusy))
            RaiseSessionStatus();
        if (e.PropertyName == nameof(SessionItem.Name) && sender is SessionItem session)
        {
            ApplySidebarSearch(session);
            // 검색 필터 중이면 이름 변경이 IsSearchVisible(라벨 캐리어 선정 조건)을 바꿀 수 있다.
            if (!_showAllSidebarSessions || _sessionSearchQuery.Length > 0) RefreshSidebarGroups();
        }
        if (e.PropertyName is nameof(SessionItem.Hidden)
            or nameof(SessionItem.ParentSessionId)
            or nameof(SessionItem.AreSessionChildrenExpanded))
            RefreshSessionTree();
    }

    /// <summary>부모 관계·직접 숨김 상태에서 깊이와 트리 전체 숨김 여부를 계산하고 표시 컬렉션을 갱신.</summary>
    public void RefreshSessionTree()
    {
        var byId = Sessions.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var presentations = new List<(SessionItem Session, int Depth, bool HasParent, int ChildCount, bool TreeVisible, SessionItem Root)>();
        foreach (var session in Sessions)
        {
            int depth = 0;
            bool treeVisible = true;
            bool hasParent = false;
            var root = session;
            var current = session;
            var visited = new HashSet<string>(StringComparer.Ordinal) { session.Id };

            while (!string.IsNullOrEmpty(current.ParentSessionId)
                   && byId.TryGetValue(current.ParentSessionId, out var parent)
                   && visited.Add(parent.Id))
            {
                hasParent = true;
                depth++;
                treeVisible &= parent.AreSessionChildrenExpanded;
                root = parent;
                current = parent;
            }

            int childCount = Sessions.Count(child =>
                StringComparer.Ordinal.Equals(child.ParentSessionId, session.Id));
            presentations.Add((session, depth, hasParent, childCount, treeVisible, root));
        }

        var rootHasVisibleSession = presentations
            .GroupBy(p => p.Root)
            .ToDictionary(group => group.Key, group => group.Any(p => !p.Session.Hidden));
        foreach (var p in presentations)
        {
            p.Session.ApplyTreePresentation(
                p.Depth,
                p.HasParent,
                p.ChildCount,
                p.TreeVisible,
                p.Session.Hidden,
                !rootHasVisibleSession[p.Root]);
        }

        SyncObservable(HiddenSessions, BuildHiddenTreeOrder());
        RefreshSidebarGroups();
    }

    /// <summary>로드 완료 후 끊어진 부모 참조와 순환 관계를 제거.</summary>
    public void NormalizeSessionTree()
    {
        var byId = Sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var session in Sessions)
        {
            if (string.IsNullOrEmpty(session.ParentSessionId)) continue;
            if (!byId.ContainsKey(session.ParentSessionId) || session.ParentSessionId == session.Id)
            {
                session.ParentSessionId = null;
                continue;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal) { session.Id };
            var current = session;
            while (!string.IsNullOrEmpty(current.ParentSessionId)
                   && byId.TryGetValue(current.ParentSessionId, out var parent))
            {
                if (!visited.Add(parent.Id))
                {
                    session.ParentSessionId = null;
                    break;
                }
                current = parent;
            }
        }

        // 트리는 1단계만 허용. 기존/손상 데이터의 손자 세션은 최상위 조상의 직접 자식으로 승격.
        foreach (var session in Sessions)
        {
            var parent = SessionParentOf(session);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (parent != null && !string.IsNullOrEmpty(parent.ParentSessionId) && visited.Add(parent.Id))
                parent = SessionParentOf(parent);
            if (parent != null && session.ParentSessionId != parent.Id)
                session.ParentSessionId = parent.Id;
        }
        RefreshSessionTree();
    }

    public SessionItem? SessionParentOf(SessionItem session)
        => string.IsNullOrEmpty(session.ParentSessionId)
            ? null
            : Sessions.FirstOrDefault(s => s.Id == session.ParentSessionId);

    public IReadOnlyList<SessionItem> GetSessionSubtree(SessionItem root)
    {
        var result = new List<SessionItem>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Add(SessionItem item)
        {
            if (!visited.Add(item.Id)) return;
            result.Add(item);
            foreach (var child in Sessions.Where(s => s.ParentSessionId == item.Id)) Add(child);
        }
        if (Sessions.Contains(root)) Add(root);
        return result;
    }

    public bool CanSetSessionParent(SessionItem child, SessionItem parent)
        => Sessions.Contains(child) && Sessions.Contains(parent)
           && !ReferenceEquals(child, parent)
           && string.IsNullOrEmpty(child.ParentSessionId)
           && string.IsNullOrEmpty(parent.ParentSessionId)
           && !Sessions.Any(s => s.ParentSessionId == child.Id)
           && !GetSessionSubtree(child).Contains(parent);

    /// <summary>child 서브트리를 parent의 마지막 자식으로 붙이고 세션 탭 상대 순서도 트리 순서로 맞춤.</summary>
    public bool SetSessionParent(SessionItem child, SessionItem parent)
    {
        if (!CanSetSessionParent(child, parent)) return false;
        var subtree = GetSessionSubtree(child).ToList();
        var order = Sessions.Where(s => !subtree.Contains(s)).ToList();
        bool relationChanged = child.ParentSessionId != parent.Id;
        child.ParentSessionId = parent.Id;

        int insertAt = !child.Hidden
            ? order.FindIndex(s =>
                StringComparer.Ordinal.Equals(s.ParentSessionId, parent.Id) && s.Hidden)
            : -1;
        if (insertAt < 0)
            insertAt = order.FindLastIndex(s => ReferenceEquals(s, parent) || IsDescendantOf(s, parent)) + 1;
        if (insertAt < 0) insertAt = order.Count;
        order.InsertRange(Math.Clamp(insertAt, 0, order.Count), subtree);
        ApplySessionOrder(FlattenSessionOrder(order));
        MakeSessionSubtreeContiguous(parent);
        RefreshSessionTree();
        return relationChanged || order.Count > 0;
    }

    /// <summary>같은 부모를 가진 자식 세션의 순서만 바꾸고 부모 관계는 유지한다.</summary>
    public bool MoveSessionWithinSiblings(
        SessionItem source,
        SessionItem target,
        bool after,
        IReadOnlyCollection<SessionItem>? visibleSiblings = null)
    {
        string? parentId = source.ParentSessionId;
        if (string.IsNullOrEmpty(parentId)
            || !Sessions.Contains(source)
            || !Sessions.Contains(target)
            || !StringComparer.Ordinal.Equals(parentId, target.ParentSessionId))
            return false;

        var visibleSet = visibleSiblings?.ToHashSet();
        bool IsMovable(SessionItem session) =>
            StringComparer.Ordinal.Equals(session.ParentSessionId, parentId)
            && (visibleSet == null || visibleSet.Contains(session));
        var siblings = Sessions
            .Where(IsMovable)
            .ToList();
        int oldIndex = siblings.IndexOf(source);
        if (oldIndex < 0) return false;

        siblings.RemoveAt(oldIndex);
        int targetIndex = siblings.IndexOf(target);
        if (targetIndex < 0) return false;

        int newIndex = targetIndex + (after ? 1 : 0);
        if (newIndex == oldIndex) return false;
        siblings.Insert(newIndex, source);

        var desiredSessions = Sessions.ToList();
        int siblingIndex = 0;
        for (int i = 0; i < desiredSessions.Count; i++)
            if (IsMovable(desiredSessions[i]))
                desiredSessions[i] = siblings[siblingIndex++];

        ApplySessionOrder(desiredSessions);
        RefreshSessionTree();
        return true;
    }

    /// <summary>최상위 세션은 자식 서브트리를 한 블록으로 유지한 채 다른 최상위 행 앞/뒤로 이동한다.</summary>
    public bool MoveSessionSubtreeRelativeToTab(
        SessionItem source,
        TabItemBase target,
        bool after)
    {
        if (!Sessions.Contains(source)
            || !Tabs.Contains(target)
            || ReferenceEquals(source, target)
            || target is SessionItem { ParentSessionId: not null })
            return false;

        var subtree = GetSessionSubtree(source).ToList();
        if (target is SessionItem targetSession && subtree.Contains(targetSession)) return false;

        bool relationChanged = !string.IsNullOrEmpty(source.ParentSessionId);
        source.ParentSessionId = null;
        return MoveTopLevelTabBlock(source, target, after, relationChanged);
    }

    /// <summary>문서/브라우저 행을 사이드바의 최상위 항목 앞뒤로 옮긴다.
    /// 대상이 부모 세션이면 자식 서브트리 전체를 한 블록으로 보고 그 바깥에 배치한다.</summary>
    public bool MoveStandaloneTabRelativeToTab(
        TabItemBase source,
        TabItemBase target,
        bool after)
    {
        if (source is SessionItem
            || !Tabs.Contains(source)
            || !Tabs.Contains(target)
            || ReferenceEquals(source, target))
            return false;

        return MoveTopLevelTabBlock(source, target, after, treeChanged: false);
    }

    /// <summary>사이드바 표시 순서와 같은 최상위 블록 목록을 만든다.</summary>
    private List<List<TabItemBase>> BuildTopLevelTabBlocks()
    {
        var blocks = new List<List<TabItemBase>>();
        var emitted = new HashSet<TabItemBase>();
        foreach (var tab in Tabs)
        {
            if (emitted.Contains(tab)) continue;
            if (tab is SessionItem session)
            {
                if (SessionParentOf(session) != null) continue;
                var block = GetSessionSubtree(session)
                    .Where(item => Tabs.Contains(item))
                    .Cast<TabItemBase>()
                    .ToList();
                foreach (var item in block) emitted.Add(item);
                blocks.Add(block);
                continue;
            }

            emitted.Add(tab);
            blocks.Add(new List<TabItemBase> { tab });
        }

        // 손상된 부모 참조 등으로 루트에서 도달하지 못한 항목도 유실하지 않는다.
        foreach (var tab in Tabs)
            if (emitted.Add(tab)) blocks.Add(new List<TabItemBase> { tab });
        return blocks;
    }

    private bool MoveTopLevelTabBlock(
        TabItemBase source,
        TabItemBase target,
        bool after,
        bool treeChanged)
    {
        var blocks = BuildTopLevelTabBlocks();
        var sourceBlock = blocks.FirstOrDefault(block => block.Contains(source));
        var targetBlock = blocks.FirstOrDefault(block => block.Contains(target));
        if (sourceBlock == null || targetBlock == null || ReferenceEquals(sourceBlock, targetBlock))
        {
            if (treeChanged) RefreshSessionTree();
            return treeChanged;
        }

        // 실제로 관계된 소스/대상 두 블록만 연속 배치한다. 나머지 탭의 상대 순서는 유지해
        // 분할 반대 패널을 불필요하게 재정렬하지 않으면서, 부모와 자식 사이에 문서가 끼는 것도 막는다.
        var affected = sourceBlock.Concat(targetBlock).ToHashSet();
        // 자식 탭이 원시 Tabs 순서에서 부모보다 앞서 있어도, 사이드바 최상위 행인 target 위치를
        // 블록의 기준점으로 삼아야 그 사이의 문서/반대 패널 탭을 건너뛰지 않는다.
        int targetAnchor = Tabs.IndexOf(target);
        int insertAt = Tabs.Take(targetAnchor).Count(tab => !affected.Contains(tab));
        var desiredTabs = Tabs.Where(tab => !affected.Contains(tab)).ToList();
        var orderedBlocks = after
            ? targetBlock.Concat(sourceBlock)
            : sourceBlock.Concat(targetBlock);
        desiredTabs.InsertRange(insertAt, orderedBlocks);

        bool orderChanged = !Tabs.SequenceEqual(desiredTabs);
        if (orderChanged) SyncObservable(Tabs, desiredTabs);
        if (treeChanged) RefreshSessionTree();
        return orderChanged || treeChanged;
    }

    private void MakeSessionSubtreeContiguous(SessionItem root)
    {
        var subtree = GetSessionSubtree(root)
            .Where(item => Tabs.Contains(item))
            .Cast<TabItemBase>()
            .ToList();
        if (subtree.Count < 2) return;

        var subtreeSet = subtree.ToHashSet();
        int anchor = Tabs.IndexOf(root);
        int insertAt = Tabs.Take(anchor).Count(tab => !subtreeSet.Contains(tab));
        var desiredTabs = Tabs.Where(tab => !subtreeSet.Contains(tab)).ToList();
        desiredTabs.InsertRange(insertAt, subtree);
        if (!Tabs.SequenceEqual(desiredTabs)) SyncObservable(Tabs, desiredTabs);
    }


    /// <summary>움직이지 않고 놓은 자식도 원래 부모 서브트리 바로 뒤의 최상위 세션으로 분리.</summary>
    public bool DetachSessionAsRoot(SessionItem source)
    {
        if (!Sessions.Contains(source) || string.IsNullOrEmpty(source.ParentSessionId)) return false;
        var oldParent = SessionParentOf(source);
        source.ParentSessionId = null;
        if (oldParent != null)
            return MoveTopLevelTabBlock(source, oldParent, after: true, treeChanged: true);

        RefreshSessionTree();
        return true;
    }

    /// <summary>클릭한 세션만 숨김 해제한다. 부모/자식 숨김 상태와 관계는 유지한다.
    /// 트리 전체가 숨김이었을 때(하단 숨김 그룹에서 복원)만 트리 블록을 탭 목록 끝으로 옮긴다 —
    /// 일부가 이미 보이는 트리까지 끝으로 옮기면 보이던 부모 블록이 카드 상단에서 빠지면서
    /// 그 아래 문서 탭이 카드 맨 위로 튀어오른다(제자리 해제가 맞다).</summary>
    public IReadOnlyList<SessionItem> UnhideSessionPath(SessionItem session)
    {
        if (!Sessions.Contains(session) || !session.Hidden) return Array.Empty<SessionItem>();
        // Hidden setter 가 RefreshSessionTree 로 IsSidebarGloballyHidden 을 즉시 재계산하므로 해제 전에 판정.
        bool wholeTreeWasHidden = session.IsSidebarGloballyHidden;
        session.Hidden = false;

        if (wholeTreeWasHidden)
        {
            var blocks = BuildTopLevelTabBlocks();
            var sourceBlock = blocks.FirstOrDefault(block => block.Contains(session));
            var lastOtherBlock = blocks.LastOrDefault(block => !ReferenceEquals(block, sourceBlock));
            if (lastOtherBlock != null)
                MoveTopLevelTabBlock(session, lastOtherBlock[0], after: true, treeChanged: false);
        }

        return new[] { session };
    }

    /// <summary>새로 숨김 그룹에 들어온 세션 트리 블록을 기존 숨김 목록의 맨 위/아래로 옮긴다.
    /// 일반 세션과 문서의 상대 순서는 바꾸지 않는다.</summary>
    public void PlaceNewlyHiddenSession(SessionItem session, bool onTop)
    {
        if (!Sessions.Contains(session) || !session.IsSidebarGloballyHidden) return;

        var blocks = BuildTopLevelTabBlocks();
        var sourceBlock = blocks.FirstOrDefault(block => block.Contains(session));
        if (sourceBlock == null) return;

        var otherHiddenBlocks = blocks
            .Where(block => !ReferenceEquals(block, sourceBlock)
                && block.OfType<SessionItem>().Any(item => item.IsSidebarGloballyHidden))
            .ToList();
        if (otherHiddenBlocks.Count == 0) return;

        var targetBlock = onTop ? otherHiddenBlocks[0] : otherHiddenBlocks[^1];
        MoveTopLevelTabBlock(session, targetBlock[0], after: !onTop, treeChanged: false);
    }

    private bool IsDescendantOf(SessionItem candidate, SessionItem ancestor)
    {
        var current = candidate;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(current.ParentSessionId) && visited.Add(current.Id))
        {
            var parent = SessionParentOf(current);
            if (parent == null) return false;
            if (ReferenceEquals(parent, ancestor)) return true;
            current = parent;
        }
        return false;
    }

    private List<SessionItem> FlattenSessionOrder(IReadOnlyList<SessionItem> baseOrder)
    {
        var set = baseOrder.ToHashSet();
        var result = new List<SessionItem>();
        var emitted = new HashSet<SessionItem>();
        void Emit(SessionItem session)
        {
            if (!emitted.Add(session)) return;
            result.Add(session);
            foreach (var child in baseOrder.Where(s => s.ParentSessionId == session.Id)) Emit(child);
        }
        foreach (var session in baseOrder)
            if (SessionParentOf(session) is not { } parent || !set.Contains(parent)) Emit(session);
        foreach (var session in baseOrder) Emit(session);
        return result;
    }

    private void ApplySessionOrder(IReadOnlyList<SessionItem> desiredSessions)
    {
        int sessionIndex = 0;
        var desiredTabs = Tabs.Select(tab => tab is SessionItem ? desiredSessions[sessionIndex++] : tab).ToList();
        SyncObservable(Tabs, desiredTabs);
    }

    private List<SessionItem> BuildHiddenTreeOrder()
    {
        var hidden = Sessions.Where(s => s.IsSidebarGloballyHidden).ToList();
        var hiddenSet = hidden.ToHashSet();
        var result = new List<SessionItem>();
        var emitted = new HashSet<SessionItem>();
        void Emit(SessionItem session)
        {
            if (!emitted.Add(session)) return;
            result.Add(session);
            var children = hidden.Where(s => s.ParentSessionId == session.Id)
                .OrderBy(s => s.Hidden ? 1 : 0);
            foreach (var child in children) Emit(child);
        }
        foreach (var session in hidden)
            if (SessionParentOf(session) is not { } parent || !hiddenSet.Contains(parent)) Emit(session);
        foreach (var session in hidden) Emit(session);
        return result;
    }

    private void RefreshSidebarGroups()
    {
        IReadOnlyList<TabItemBase> leftSource;
        IReadOnlyList<TabItemBase> rightSource;
        if (IsSplitView && _liveLeftSource != null && _liveRightSource != null)
        {
            var leftSet = _liveLeftSource.ToHashSet();
            var rightSet = _liveRightSource.ToHashSet();
            leftSource = Tabs.Where(leftSet.Contains).ToList();
            rightSource = Tabs.Where(rightSet.Contains).ToList();
        }
        else
        {
            leftSource = Tabs.ToList();
            rightSource = Array.Empty<TabItemBase>();
        }
        var left = BuildSidebarItems(leftSource);
        var right = BuildSidebarItems(rightSource);
        ApplyDetachedParentLabels(left, right);
        SyncObservable(LeftItems, left);
        SyncObservable(RightItems, right);
    }

    /// <summary>부모가 반대 그룹에 표시 중인 자식 세션 위(원래 부모 자리)에 보더 없는 부모 이름 라벨을 표시하도록
    /// DetachedParentSession 을 마킹. 같은 부모의 연속 자식 묶음에는 첫 자식에만 붙인다(부모 행은 트리에 한 번).</summary>
    private void ApplyDetachedParentLabels(List<TabItemBase> left, List<TabItemBase> right)
    {
        var unvisited = Sessions.ToHashSet();
        var sessionsById = Sessions
            .GroupBy(session => session.Id)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Mark(left, right);
        Mark(right, left);
        foreach (var session in unvisited) session.DetachedParentSession = null;

        // 카드 그룹에서 실제로 접히는 행(검색 불일치·숨김 세션+표시 토글 꺼짐)은 라벨을 붙여도
        // 안 보이므로 캐리어에서 제외하고 다음 표시 형제에게 넘긴다. 접힌 행은 run 을 끊지 않는다.
        bool RowVisible(SessionItem s) => s.IsSearchVisible && (!s.Hidden || ShowHiddenSessionsInCurrentView);

        void Mark(List<TabItemBase> group, List<TabItemBase> other)
        {
            var here = group.OfType<SessionItem>().ToHashSet();
            var there = other.OfType<SessionItem>().ToHashSet();
            SessionItem? prevLabelParent = null;
            foreach (var item in group)
            {
                if (item is not SessionItem session) { prevLabelParent = null; continue; }
                unvisited.Remove(session);
                if (!RowVisible(session)) { session.DetachedParentSession = null; continue; }
                var parent = !string.IsNullOrEmpty(session.ParentSessionId) &&
                             sessionsById.TryGetValue(session.ParentSessionId, out var foundParent)
                    ? foundParent
                    : null;
                bool detached = parent != null && !here.Contains(parent) && there.Contains(parent);
                session.DetachedParentSession =
                    detached && !ReferenceEquals(parent, prevLabelParent) ? parent : null;
                prevLabelParent = detached ? parent : null;
            }
        }
    }

    private List<TabItemBase> BuildSidebarItems(IReadOnlyList<TabItemBase> source)
    {
        var sourceSessions = source.OfType<SessionItem>()
            .Where(s => !s.IsSidebarGloballyHidden).ToList();
        var sessionSet = sourceSessions.ToHashSet();
        var sessionsById = sourceSessions
            .GroupBy(session => session.Id)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var childrenByParentId = sourceSessions
            .Where(session => !string.IsNullOrEmpty(session.ParentSessionId))
            .GroupBy(session => session.ParentSessionId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(session => session.Hidden ? 1 : 0).ToList(),
                StringComparer.Ordinal);
        var result = new List<TabItemBase>();
        var emitted = new HashSet<SessionItem>();
        var groupedDocuments = DocumentGroups
            .Where(group => group.Documents.Count > 0)
            .SelectMany(group => group.Documents.Select(file => (file, group)))
            .ToDictionary(pair => pair.file, pair => pair.group);
        var groupAnchor = DocumentGroups
            .Where(group => group.Documents.Count > 0)
            .ToDictionary(
                group => group,
                group => group.Documents.Where(Tabs.Contains).OrderBy(Tabs.IndexOf).FirstOrDefault());
        var emittedGroups = new HashSet<DocumentGroupItem>();

        void Emit(SessionItem session)
        {
            if (!emitted.Add(session)) return;
            result.Add(session);
            if (childrenByParentId.TryGetValue(session.Id, out var children))
                foreach (var child in children) Emit(child);
        }

        foreach (var item in source)
        {
            if (item is FileTabItem file && groupedDocuments.TryGetValue(file, out var group))
            {
                if (ReferenceEquals(groupAnchor[group], file) && emittedGroups.Add(group))
                    result.Add(group);
                continue;
            }
            if (item is not SessionItem session) { result.Add(item); continue; }
            if (!sessionSet.Contains(session)) continue;
            if (!string.IsNullOrEmpty(session.ParentSessionId) &&
                sessionsById.TryGetValue(session.ParentSessionId, out var parent) &&
                sessionSet.Contains(parent)) continue;
            Emit(session);
        }
        foreach (var session in sourceSessions) Emit(session);
        return result;
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
