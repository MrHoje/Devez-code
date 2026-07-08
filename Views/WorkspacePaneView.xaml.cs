using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DevezCode.Models;
using DevezCode.Services;
using DevezCode.Services.Terminal;

namespace DevezCode.Views;

/// <summary>
/// 중앙 워크스페이스 패널(메타바 + 탭 + 세션 헤더 + 터미널/에디터 콘텐츠).
/// 자체 TerminalHostView·활성 프로젝트/세션/탭 상태를 갖는 독립 단위라
/// MainWindow 가 두 개를 나란히 두면 두 프로젝트를 동시에 열 수 있다.
/// 사이드바·파일탐색기·푸터·훅 서비스는 MainWindow(셸)가 소유하고 이벤트로 연동한다.
/// </summary>
public partial class WorkspacePaneView : UserControl
{
    private readonly TerminalHostView _terminal = new();

    private ProjectItem? _activeProject;
    private SessionItem? _activeSession;
    private TabItemBase? _activeTab
    {
        get => (TabItemBase?)GetValue(SelectedTabProperty);
        set => SetValue(SelectedTabProperty, value);
    }

    public ProjectItem? ActiveProject => _activeProject;
    public SessionItem? ActiveSession => _activeSession;
    public TabItemBase? ActiveTab => _activeTab;

    /// <summary>이 패널에서 현재 선택된 탭. 탭 스트립 DataTemplate 이 MultiBinding(item vs SelectedTab)으로
    /// "이 패널에서 선택됨"을 판정한다 — 두 패널이 같은 프로젝트를 보여줘도 각자 독립적으로 강조된다.
    /// (기존 TabItemBase.IsSelected 공유 bool 은 상대 패널 선택을 덮어써 폐기.)</summary>
    public static readonly DependencyProperty SelectedTabProperty =
        DependencyProperty.Register(nameof(SelectedTab), typeof(TabItemBase), typeof(WorkspacePaneView),
            new PropertyMetadata(null));
    public TabItemBase? SelectedTab => (TabItemBase?)GetValue(SelectedTabProperty);

    /// <summary>MainWindow 가 소유한 공유 프로젝트 컬렉션. 생성 후 한 번 주입한다.</summary>
    public ObservableCollection<ProjectItem> Projects { get; set; } = new();
    /// <summary>보관함 프로젝트 — 세션/부모 조회 시 활성과 함께 검색해 보관 프로젝트도 정상 실행되게 한다.</summary>
    public ObservableCollection<ProjectItem>? ArchivedProjects { get; set; }
    /// <summary>활성 + 보관 전체 — ParentOf/FindSession 조회용.</summary>
    private IEnumerable<ProjectItem> AllProjects => ArchivedProjects == null ? Projects : Projects.Concat(ArchivedProjects);
    /// <summary>statusLine 훅이 보고한 방별 실제 model/effort 조회용(공유 서비스).</summary>
    public ModelEffortService? ModelEffort { get; set; }
    /// <summary>비-claude 에이전트 last prompt 추적용(공유 서비스).</summary>
    public AgentLastMessageService? AgentLastMsg { get; set; }
    /// <summary>이 세션이 다른 패널에서 활성으로 표시 중인지(셸이 주입). 프리로드에서 제외해 그 세션 ConPTY 를
    /// 표시 중인 패널이 최종 폭으로 생성하게 한다(같은 프로젝트 분할 시 폭 충돌·리플로우 방지).</summary>
    public Func<SessionItem, bool>? IsSessionActiveElsewhere { get; set; }

    /// <summary>사용자가 이 패널을 클릭/조작 → 포커스 패널로 지정 요청.</summary>
    public event Action<WorkspacePaneView>? FocusRequested;
    /// <summary>활성 프로젝트/세션/탭이 바뀜 → 셸이 파일탐색기·사이드바 하이라이트·last-active 저장을 갱신.</summary>
    public event Action<WorkspacePaneView>? ActiveChanged;
    /// <summary>분할 토글 버튼 클릭 → 셸이 분할/해제 처리.</summary>
    public event Action<WorkspacePaneView>? SplitToggleRequested;
    /// <summary>탭 헤더 우클릭 → "분할 보기" 클릭 → 셸이 분할 생성/반대쪽 패널로 이동 처리.
    /// 세션 탭·파일 탭 모두 지원.</summary>
    public event Action<WorkspacePaneView, TabItemBase>? SplitViewRequested;
    /// <summary>세션 탭 우클릭 "내보내기" → 셸이 대화를 .md 로 저장(프로젝트 카드 메뉴와 동일 동작).</summary>
    public event Action<SessionItem>? ExportSessionRequested;
    /// <summary>세션 탭 우클릭 "잠금/잠금 해제" → MainWindow 가 토글.</summary>
    public event Action<SessionItem>? ToggleSessionLockRequested;
    /// <summary>탭 드래그 중 매 이동 — 셸이 커서(screen)가 반대 패널 위면 그 패널에 삽입 프리뷰(탭 밀기)를 그리고
    /// true(크로스 중) 반환. 그러면 소스 패널은 자기 재정렬 프리뷰를 억제한다. ghostWidth=미는 폭.</summary>
    public Func<WorkspacePaneView, Point, double, bool>? TabDragHoverMoved;
    /// <summary>탭 드롭 — 커서가 반대 패널 위면 그 패널로 이동+삽입 위치 정렬하고 true(내부 재정렬 취소) 반환.</summary>
    public Func<WorkspacePaneView, TabItemBase, Point, bool>? TryCommitCrossDrop;
    /// <summary>탭 드래그 시작/종료 — 셸이 양쪽 패널의 + 버튼을 숨기고/복원한다.</summary>
    public Action<bool>? SetPanesTabDragActive;
    /// <summary>격리(분할 파트너) 중인 이 패널에서 탭(파일/세션)이 새로 열림 — 셸이 반대 패널에서 그 탭을 숨겨
    /// (HideTabInPane) 양쪽에 다 뜨는 것을 막는다. (파일탐색기·드롭·터미널 Ctrl+클릭·세션 추가 공통.)</summary>
    public event Action<WorkspacePaneView, TabItemBase>? IsolatedTabOpened;
    /// <summary>파일 에디터가 닫기를 요청함(탭 X·에디터 버튼·컨텍스트 메뉴 공통). 에디터는 공유 단일
    /// 인스턴스라 CloseRequested 는 '탭을 만든 패널'에 묶인다 — 그 탭이 다른 패널로 옮겨져 표시 중이면
    /// 생성 패널에서 닫아 봤자 표시 패널이 갱신되지 않는다(활성 이웃 미선택·타이틀 잔류). 셸이 실제
    /// 표시(활성) 패널로 라우팅해 그 패널에서 닫도록 이 이벤트로 위임한다.</summary>
    public event Action<FileTabItem>? FileTabCloseRequested;

    public TerminalHostView Terminal => _terminal;

    public WorkspacePaneView()
    {
        InitializeComponent();
        TerminalHostContainer.Content = _terminal;
        // 빈 패널로 시작(자동복원 off 가 기본)하면 UpdateEmptyState 가 안 불려 컨테이너가 XAML 기본값(풀사이즈)로
        // 방치된다. 그 상태에서 PrewarmWebView 가 WebView2(#0C0C0C)를 만들면 airspace HWND 가 풀사이즈로 떠
        // EmptyState 위를 덮어 시작 시 검정이 한 번 번쩍인다. 초기 상태를 0×0 주차로 맞춰 prewarm 이 안 보이게
        // 워밍되도록 한다(세션 열 때 UnparkTerminalHost 가 어두운 HWND 를 리사이즈 → 번쩍 없음).
        ParkTerminalHost();

        TabsHost.PreviewMouseMove += TabsHost_PreviewMouseMove;
        TabsHost.PreviewMouseLeftButtonUp += async (_, _) => await EndTabDragAsync();
        TabsHost.LostMouseCapture += async (_, _) => await EndTabDragAsync();

        _terminal.SessionStarted += id => { var s = FindSession(id); if (s != null) s.IsAlive = true; };
        _terminal.SessionExited += id => { var s = FindSession(id); if (s != null) { s.IsAlive = false; s.IsBusy = false; s.IsWaitingChoice = false; } HideSessionLoadingIf(id); };
        _terminal.TerminalReady += id => HideSessionLoadingIf(id);
        // 단독 ESC 취소 → busy 스피너 + 입력 대기 ❗ 즉시 해제(훅 신호보다 빠른 UI 반응; 훅도 곧 확정).
        _terminal.InterruptRequested += id => { var s = FindSession(id); if (s != null) { s.IsBusy = false; s.IsWaitingChoice = false; } };
        // 선택지 답변(Enter/숫자키) → 대기 ❗ 즉시 해제(busy 는 유지 — claude 가 답변 처리로 계속 진행).
        // 툴을 안 띄우는 메뉴(PostToolUse 미발화)에서도 ❗ 가 확실히 빠지게 하는 보조 신호.
        _terminal.MenuInputSubmitted += id => { var s = FindSession(id); if (s is { IsWaitingChoice: true }) s.IsWaitingChoice = false; };
        _terminal.SessionActionRequested += OnTerminalSessionAction;
        _terminal.UserInteracted += () => FocusRequested?.Invoke(this);
        // 세션 헤더 타이틀(마지막 메시지) 폰트를 터미널 폰트 크기와 동기화.
        _terminal.FontSizePxChanged += ApplyHeaderFontSize;
        // 터미널 → 파일 경로 Ctrl+클릭 → 에디터 탭으로 열기
        _terminal.FileOpenRequested += OnTerminalFileOpenRequested;
        // synced reveal 준비 완료 → 셸로 전달(셸이 좌우를 모아 동시에 fade)
        _terminal.RevealPrepared += () => RevealPrepared?.Invoke(this);
        // 콜드 세션: web 로딩 커버가 켜진 것(ACK)을 확인한 뒤에만 터미널 HWND 를 unpark 한다.
        _terminal.LoadingShown += OnLoadingShown;
        // 로딩 오버레이(파킹 중 터미널 영역 덮개) 배경을 '터미널 배경색'과 맞춘다 — 앱 배경(BgBrush)으로 두면
        // unpark 후 웹 커버/터미널(터미널 배경색)과 색이 달라 앱배경→터미널배경 점프가 검정 깜빡으로 보인다.
        ApplyTerminalBgToCovers();
        App.ThemeChanged += _ => ApplyTerminalBgToCovers();
        Loaded += (_, _) => ApplyHeaderFontSize(_terminal.EffectiveFontSizePx);
        // 터미널 폰트 크기 dock: 항상 노출(모델과 무관), Ctrl+휠/Ctrl+0 로 바뀌어도 콤보 선택값 동기화.
        FontSizeCombo.ItemsSource = FontSizeOptions;
        _terminal.FontSizePxChanged += SyncFontSizeCombo;
        Loaded += (_, _) => SyncFontSizeCombo(_terminal.EffectiveFontSizePx);
        // 로딩 중 레이아웃이 바뀌면(예: 시작 시 전체폭으로 세션 복원 → 곧바로 분할 적용) 웹 스피너의
        // px 앵커 좌표를 재전송해 카드가 항상 최종 중앙에 있게 한다.
        TerminalLoadingOverlay.SizeChanged += (_, e) =>
        {
            if (_loadingRoomId != null && TerminalLoadingOverlay.Visibility == Visibility.Visible)
                _terminal.SetLoading(true, e.NewSize.Width, e.NewSize.Height);
        };

        App.ThemeChanged += OnThemeChanged_UpdateSeam;
        Unloaded += (_, _) => App.ThemeChanged -= OnThemeChanged_UpdateSeam;

        ApplyProjectInfoHeaderVisibility();
        RefreshSplitIndicator(); // 시작 시 프로젝트 미선택 상태면 분할 버튼도 처음부터 숨김
    }

    /// <summary>설정(프로젝트 정보 헤더 숨기기) + 프로젝트 선택 여부에 따라 메타바(MetaBar) 표시 반영.
    /// 프로젝트가 선택되지 않은 빈 패널이면 설정과 무관하게 항상 숨긴다(#·경로 아이콘 제거).</summary>
    public void ApplyProjectInfoHeaderVisibility()
    {
        if (MetaBar != null)
            MetaBar.Visibility = (_activeProject != null && !SettingsService.LoadHideProjectInfoHeader())
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pane_PreviewInteract(object sender, MouseButtonEventArgs e) => FocusRequested?.Invoke(this);

    /// <summary>분할 시 이 패널이 우측(PaneB)인지. 좌/우 위치 스왑 추적에 쓰인다.</summary>
    public bool IsRightPane { get; set; }

    private bool _split;

    /// <summary>분할 상태 저장. 탭바 분할 토글 버튼(SplitDockBtn) 표시 여부 계산에 쓰인다.</summary>
    public void SetSplitActive(bool active) => _split = active;

    // ── 탭바 표시 필터(패널별 숨김) ──────────────────────────────────
    // Tabs 는 프로젝트 소유(공유) 컬렉션이라 분할된 두 패널이 같은 프로젝트를 보여줄 수 있다.
    // 탭을 드래그해 다른 패널로 "이동"시키면 세션/컬렉션은 그대로 두고, 이 패널의 탭바에서만
    // ListCollectionView 필터로 숨긴다(다른 패널 탭바엔 그대로 보임).
    private readonly HashSet<TabItemBase> _hiddenFromThisPane = new();
    private ListCollectionView? _tabsView;
    // "분할 보기/이동"으로 이 패널에 들어온 탭들의 화이트리스트(null=일반 모드=전체 표시).
    // 여러 번 이동해오면 계속 누적되어 쌓인다(마지막 하나만 남기지 않음).
    private HashSet<TabItemBase>? _isolatedTabs;

    private void ApplyTabsSource(ObservableCollection<TabItemBase>? tabs)
    {
        _hiddenFromThisPane.Clear();
        _isolatedTabs = null;
        if (tabs == null) { _tabsView = null; TabsHost.ItemsSource = null; return; }
        var view = new ListCollectionView(tabs) { Filter = FilterTab };
        _tabsView = view;
        TabsHost.ItemsSource = view;
    }

    private bool FilterTab(object o)
    {
        if (o is not TabItemBase t) return false;
        if (_isolatedTabs != null) return _isolatedTabs.Contains(t);
        return !_hiddenFromThisPane.Contains(t);
    }

    /// <summary>드래그로 다른 패널에 넘긴 탭을 이 패널의 탭바에서만 숨긴다(세션/ConPTY 는 유지).
    /// 이 패널이 격리(화이트리스트) 중이면 블랙리스트는 무의미하므로 화이트리스트에서 빼는 쪽으로
    /// 처리한다. 화이트리스트가 비어도 절대 null(=전체 목록 모드)로 되돌리지 않는다 — null 로
    /// 되돌리면 같은 프로젝트를 공유하는 반대쪽 패널의 탭 전체가 갑자기 이 패널에도 다 보이는
    /// 사고가 난다(격리 중이던 패널의 "마지막 탭"까지 이동했다면 그냥 빈 탭바가 맞다).
    /// 숨긴 탭이 이 패널에서 활성 중이었으면 다른 탭으로 교체(없으면 비움) — 안 그러면 이 패널의
    /// _activeTab 이 넘어간 탭을 계속 가리켜서, 그 탭을 다시 이 패널로 "이동"시키려 할 때
    /// target.ActiveTab==tab 으로 오판돼 조기 반환된다(파일 탭은 세션과 달리 소유권 라우팅이
    /// 없어 이 문제에 특히 취약함).</summary>
    public void HideTabInPane(TabItemBase tab)
    {
        bool changed = _isolatedTabs != null ? _isolatedTabs.Remove(tab) : _hiddenFromThisPane.Add(tab);
        if (!changed) return;
        _tabsView?.Refresh();
        RefreshHeaderSessionGate();

        if (!ReferenceEquals(_activeTab, tab)) return;
        int idx = _activeProject?.Tabs.IndexOf(tab) ?? -1;
        var next = PickNeighborTab(_activeProject, idx); // 왼쪽 우선 이웃 + Hidden 세션 제외(PickNeighborTab 과 동일 규칙)
        if (next is SessionItem ns) ActivateSession(ns);
        else if (next is FileTabItem nf) ActivateFileTab(nf);
        else ClearActiveSession();
    }

    /// <summary>분할 해제 준비 — 이 패널이 "분할 보기"로 숨긴 탭 목록 + 격리 중이었는지 스냅샷.
    /// 좌우 스왑 상태에서 분할을 닫을 땐 이 패널 자체가 사라지고 내용이 PaneA 로 옮겨가므로,
    /// 옮겨갈 새 홈에 ApplySplitCloseState 로 전달해 이어붙인다.</summary>
    public (List<TabItemBase> hidden, bool wasIsolated) CaptureSplitCloseState()
        => (_hiddenFromThisPane.ToList(), _isolatedTabs != null);

    /// <summary>분할 해제 후 이 패널(분할 종료 시 최종 홈)에서 호출 — 격리 모드를 풀고,
    /// 캡처된 숨김 탭들을 원래 위치가 아니라 탭바 맨 끝에 이어붙인다(순서 복원 아님, append).</summary>
    public void ApplySplitCloseState(List<TabItemBase> hiddenFromSource, bool wasIsolated)
    {
        bool changed = wasIsolated && _isolatedTabs != null;
        if (wasIsolated) _isolatedTabs = null;
        _hiddenFromThisPane.Clear();

        if (_activeProject != null)
        {
            foreach (var t in hiddenFromSource)
            {
                int idx = _activeProject.Tabs.IndexOf(t);
                if (idx < 0) continue;
                if (idx != _activeProject.Tabs.Count - 1) _activeProject.Tabs.Move(idx, _activeProject.Tabs.Count - 1);
                changed = true;
            }
        }

        if (changed)
        {
            _tabsView?.Refresh();
            WorkspaceStore.Save(Projects);
        }
    }

    /// <summary>"분할 보기/이동"으로 들어온 탭을 이 패널의 화이트리스트에 추가한다(누적 — 기존에
    /// 이미 보이던 탭들은 그대로 두고 새 탭만 더해져 쌓인다). 이 패널에서 화이트리스트 밖의 탭이
    /// 활성화되면(ActivateSession/ActivateFileTab) 자동 해제되어 전체 목록으로 돌아간다.</summary>
    public void IsolateTab(TabItemBase tab)
    {
        _isolatedTabs ??= new HashSet<TabItemBase>();
        _isolatedTabs.Add(tab);
        _tabsView?.Refresh();
    }

    /// <summary>분할 우측 패널이 그 프로젝트를 아직 안 띄운(빈/세션0) 상태에서 파일을 처음 열 때: 프로젝트를
    /// 활성화하되 '격리 모드(빈 화이트리스트)'로 시작한다. 이어지는 OpenFileAsTab 이 그 파일만 우측에 격리
    /// 표시해(전체 세션 노출·좌우 중복 방지) 준다. 세션은 활성화하지 않는다.</summary>
    public void ActivateProjectIsolated(ProjectItem proj)
    {
        SetActiveProject(proj);                     // _activeProject 세팅 + 탭소스(격리 null 로 리셋)
        _isolatedTabs = new HashSet<TabItemBase>(); // 빈 화이트리스트 = 아무 탭도 안 보임(곧 파일만 격리)
        _tabsView?.Refresh();
    }

    /// <summary>현재 화이트리스트 스냅샷(격리 중이 아니면 null). OpenSession/OpenFileTab 내부의
    /// ActivateSession→ClearIsolationIfMismatch 가 격리를 풀어버릴 수 있어, 호출 전에 미리
    /// 떠서 이동 후 IsolateTab 으로 다시 얹는 용도(누적 유지).</summary>
    public List<TabItemBase>? CurrentIsolatedTabs() => _isolatedTabs?.ToList();

    /// <summary>탭 참조 문자열("S:&lt;id&gt;"/"F:&lt;path&gt;"). 분할 상태 영속/복원용.</summary>
    public static string RefOf(TabItemBase? t) => t switch
    {
        SessionItem s => "S:" + s.Id,
        FileTabItem f => "F:" + f.FilePath,
        _ => "",
    };

    /// <summary>현재 우측 패널에 격리된 탭들의 참조 목록(같은 프로젝트 분할 시 우측 전체 탭). 격리 아님/빈 값 제외.</summary>
    public List<string> CurrentIsolatedRefs()
        => _isolatedTabs?.Select(RefOf).Where(r => r.Length > 0).ToList() ?? new List<string>();

    /// <summary>이 패널 탭바에 실제로 보이는(FilterTab 통과) 탭들의 참조. 화이트리스트/블랙리스트 모드 무관하게
    /// "이 패널이 지금 보여주는 탭 집합"을 준다 — 분할 상태 저장 시 우측 전용 집합 계산에 쓴다.</summary>
    public List<string> VisibleTabRefs()
        => _activeProject?.Tabs.Where(t => FilterTab(t) && !(t is SessionItem s && s.Hidden))
               .Select(RefOf).Where(r => r.Length > 0).ToList() ?? new List<string>();

    /// <summary>이 패널 활성 탭의 참조.</summary>
    public string? ActiveTabRef() => _activeTab == null ? null : RefOf(_activeTab);

    /// <summary>이 탭이 이 패널의 탭바에 실제로 보이는지(활성 프로젝트 소속 + 필터 통과). 사이드바 클릭 시
    /// 그 세션이 실제로 있는 패널을 골라 여는 데 쓴다 — 같은 프로젝트 분할에서 우측 격리 세션이 좌측에서
    /// 안 열리던 문제 방지.</summary>
    public bool ShowsTab(TabItemBase tab) => _activeProject != null && _activeProject.Tabs.Contains(tab) && FilterTab(tab);

    /// <summary>현재 이 패널이 파일 탭을 활성으로 보여주는지. 파일→세션 전환 시 세션 터미널 리플로우를 커버로 감추는 판정용.</summary>
    public bool ActiveIsFile => _activeTab is FileTabItem;

    /// <summary>활성 프로젝트의 Tabs 에서 참조("S:id"/"F:path")에 해당하는 탭을 찾는다. 세션은 숨김 제외.</summary>
    public TabItemBase? FindTabByRef(string? @ref)
    {
        if (_activeProject == null || string.IsNullOrEmpty(@ref) || @ref!.Length < 2 || @ref[1] != ':') return null;
        var key = @ref[2..];
        return @ref[0] switch
        {
            'S' => _activeProject.Tabs.OfType<SessionItem>().FirstOrDefault(s => !s.Hidden && s.Id == key),
            'F' => _activeProject.Tabs.OfType<FileTabItem>().FirstOrDefault(f => string.Equals(f.FilePath, key, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
    }

    /// <summary>참조가 가리키는 탭을 이 패널에서 활성화(세션/파일 라우팅). 못 찾으면 false.</summary>
    public bool ActivateByRef(string? @ref)
    {
        var t = FindTabByRef(@ref);
        if (t is SessionItem s) { ActivateSession(s); return true; }
        if (t is FileTabItem f) { ActivateFileTab(f); return true; }
        return false;
    }

    /// <summary>이 패널 탭바에서 이 탭이 확실히 보이게 한다 — 격리 중이면 화이트리스트에 추가,
    /// 전체 모드면 블랙리스트에서 제거. "이동"으로 탭이 들어왔는데, 과거에 이 패널이 그 탭을
    /// 블랙리스트에 넣어둔 상태(원래 이 패널에서 반대쪽으로 처음 보냈던 탭)면 활성화해도 필터에
    /// 걸려 안 보이는 "사라짐" 버그를 막는다.</summary>
    public void UnhideTabInPane(TabItemBase tab)
    {
        bool changed = _isolatedTabs != null ? _isolatedTabs.Add(tab) : _hiddenFromThisPane.Remove(tab);
        if (!changed) return;
        _tabsView?.Refresh();
        RefreshHeaderSessionGate();
    }

    /// <summary>이 패널이 현재 실제로 표시하는 탭들(FilterTab 통과), 탭 순서대로. 사이드바 카드 라이브 그룹용.</summary>
    public List<TabItemBase> VisibleTabsInOrder()
        => _activeProject?.Tabs.Where(FilterTab).ToList() ?? new List<TabItemBase>();

    private void ClearIsolationIfMismatch(TabItemBase active)
    {
        if (_isolatedTabs == null || _isolatedTabs.Contains(active)) return;
        _isolatedTabs = null;
        _tabsView?.Refresh();
    }

    /// <summary>이 패널의 우측 보더(우측 채널 세퍼레이터) 표시 여부. 우측에 아무 패널도
    /// 열려 있지 않은 최우측 패널은 우측 보더를 꺼서 떠 있는 세로선을 없앤다.</summary>
    public void SetRightChannelBorder(bool show)
    {
        CenterArea.BorderThickness = new Thickness(1, 0, show ? 1 : 0, 0);
        // 채널 보더를 끄면 CenterArea 우측 두께가 0이 되어 포커스 프레임(FocusFrame) 우측 보더와
        // WebView2 사이 여백이 1px 뿐이라, WebView2 HwndHost 가 DPI 반올림으로 1px 오버렌더하면
        // airspace 로 포커스 보더를 덮어 가린다. 이때 FocusFrame 우측 여백 1px 을 줘 항상 2px 인셋을
        // 유지한다(여백은 CenterArea 배경색이라 세로선은 안 보이고, 오버렌더는 그 여백에 떨어진다).
        FocusFrame.Margin = new Thickness(0, 1, show ? 0 : 1, 0);
    }

    /// <summary>분할 중 포커스 패널을 4면 테마색 보더로 표시. 포커스 시 PrimaryBrush, 아니면 투명.
    /// 색만 바뀌고 두께(레이아웃)는 고정이라 터미널 리사이즈가 없다. SetResourceReference 로 연결해
    /// 테마 변경 시 색이 자동으로 따라온다.</summary>
    public void SetFocusedVisual(bool focused)
    {
        if (FocusFrame == null) return;
        if (focused)
            FocusFrame.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
        else
            FocusFrame.BorderBrush = System.Windows.Media.Brushes.Transparent;
    }
    /// <summary>분할 접힘/펼침 애니메이션 중 줄바꿈을 꺼서 텍스트가 세로로 늘어나지 않게 한다.
    /// 완전히 보일 때만 Wrap 으로 복원한다.</summary>
    public void SetEmptyTextWrapping(bool wrap)
    {
        if (EmptyStateText == null) return;
        EmptyStateText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }

    /// <summary>세션 소유권 이전 — 이 패널이 해당 세션을 활성으로 들고 있으면 배선을 끊고 다음 세션으로(없으면 비움).</summary>
    public void ReleaseSessionIfActive(SessionItem s)
    {
        if (!ReferenceEquals(_activeSession, s)) return;
        try { _terminal.CloseTerminal(s.Id); } catch { /* ignore */ }
        var parent = ParentOf(s);
        var next = parent?.Tabs.OfType<SessionItem>().FirstOrDefault(x => !ReferenceEquals(x, s) && !x.Hidden);
        if (next != null) ActivateSession(next);
        else ClearActiveSession();
    }

    /// <summary>Collapsed 였다 되살아난 패널의 활성 세션을 자연스럽게 복원(cover→재동기→fade-in).
    /// preserve 로 이미 준비(ready)된 세션에만 적용한다 — 처음 여는(미준비) 세션은 기존 로딩 스피너 +
    /// WireSession 경로가 크기를 맞추므로 여기서 건드리면 이중 처리가 된다.</summary>
    public void ResyncActiveSessionOnReturn()
    {
        if (_activeSession != null && _terminal.IsReady(_activeSession.Id))
            _terminal.ResyncOnReturn(_activeSession.Id);
    }

    private bool _coverActive; // 전환 커버 중(파일 에디터를 숨기고 TerminalCurtain 으로 가리는 상태 판정용)

    /// <summary>분할 열림/닫힘·프로젝트 전환 직전 — 세션은 웹 레이어 커튼(#xfer-cover)으로, 파일 에디터는
    /// 단색 커튼(TerminalCurtain, md=WebView2 는 airspace 라 에디터를 숨기고 덮음)으로 가려 리플로우/조기표시를 막는다.</summary>
    public void CoverForTransition()
    {
        _coverActive = true;
        _terminal.CoverForTransition();
        UpdateEmptyState(); // 파일 탭이면 에디터 숨김 + 커튼 표시
    }

    /// <summary>synced reveal 준비 완료(양쪽 조율용). 셸이 좌우 준비를 모아 FadeRevealNow 로 동시에 걷는다.</summary>
    public event Action<WorkspacePaneView>? RevealPrepared;

    /// <summary>동시 reveal 준비 — 폭 안정·fit·재동기까지만 하고 커튼은 유지, 완료 시 RevealPrepared 발생.
    /// 활성 세션이 없으면(파일/빈 패널) 폭 조정이 필요 없다. 단 md 에디터가 콜드 로드 중이면 준비될 때까지
    /// 기다렸다 보고한다 — 즉시 보고하면 세션 쪽 준비만으로 커튼이 걷혀 md 영역이 빈(어두운) 채 보였다가
    /// 내용이 늦게 떠 "까매졌다 열리는" 것처럼 보인다(재시작 분할 복원의 콜드 로드가 대표 케이스).</summary>
    public void PrepareRevealSynced(bool kick)
    {
        if (_activeSession == null)
        {
            if (_activeTab is FileTabItem { Editor: MarkdownFileEditorView md } tab && !md.IsEditorShellReady)
            {
                WhenMdReady(md, async () =>
                {
                    if (!ReferenceEquals(_activeTab, tab)) return; // 그 사이 탭/전환이 바뀜 — 낡은 보고 폐기
                    await WaitForFramesAsync(2);                   // setMarkdown 반영이 페인트될 여유
                    RevealPrepared?.Invoke(this);
                });
                return;
            }
            RevealPrepared?.Invoke(this);
            return;
        }
        UpdateLayout();
        double target = TerminalHostContainer?.ActualWidth ?? 0;
        _terminal.PrepareRevealSynced(_activeSession.Id, kick, target);
    }

    /// <summary>md 에디터 준비(EditorShellReady) 시 콜백 — 안전 타임아웃 2.5s(크래시 등) 후에도 진행.</summary>
    private static void WhenMdReady(MarkdownFileEditorView md, Action then)
    {
        bool done = false;
        System.Windows.Threading.DispatcherTimer? timer = null;
        void Fire()
        {
            if (done) return;
            done = true;
            md.EditorShellReady -= Fire;
            timer?.Stop();
            then();
        }
        md.EditorShellReady += Fire;
        timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        timer.Tick += (_, _) => Fire();
        timer.Start();
    }

    /// <summary>synced reveal 의 최종 단계 — 세션·파일 커버를 즉시(동시에) 걷는다. 파일(md=WebView2)은
    /// airspace 로 즉시 나타나므로 세션 커버도 즉시 걷어야 좌우가 정확히 같은 순간에 뜬다.</summary>
    public void FadeRevealNow()
    {
        _terminal.FadeNow();
        EndCover(instant: true);
    }

    /// <summary>커버 해제 — 파일 커튼을 걷고(instant=false 면 fade) 파일 에디터를 다시 표시한다.</summary>
    private void EndCover(bool instant = false)
    {
        if (!_coverActive) return;
        _coverActive = false;
        UpdateEmptyState(); // 파일 에디터 다시 표시(md 는 WebView2 라 즉시, text 는 커튼 뒤에서 노출)
        if (TerminalCurtain.Visibility != Visibility.Visible) return;
        if (instant)
        {
            TerminalCurtain.BeginAnimation(UIElement.OpacityProperty, null);
            TerminalCurtain.Opacity = 1;
            TerminalCurtain.Visibility = Visibility.Collapsed;
        }
        else
        {
            var anim = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.14));
            anim.Completed += (_, _) => { TerminalCurtain.Visibility = Visibility.Collapsed; TerminalCurtain.Opacity = 1; };
            TerminalCurtain.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    /// <summary>전환 후 — 최종 폭으로 fit 재측정 후 커튼을 fade-out(활성 세션 기준). 세션이 없으면 커튼만 걷는다.
    /// kick=true 면 fit 후 resize-kick(cols-1→cols)으로 SIGWINCH 를 내 TUI 를 강제 리페인트한다 — 처음 표시되며
    /// 재배선된 세션(분할 보기 등)이 스크롤/뷰포트 정지 프레임으로 남는 것을 막는다(스플리터 nudge 자동화).</summary>
    public void RevealAfterTransition(bool kick = false)
    {
        // md 에디터가 콜드 로드 중이면 준비될 때까지 커튼을 유지(단일 패널 파일 전환의 "까매졌다 열림" 방지).
        if (_activeSession == null && _activeTab is FileTabItem { Editor: MarkdownFileEditorView md } tab && !md.IsEditorShellReady)
        {
            WhenMdReady(md, async () =>
            {
                if (!ReferenceEquals(_activeTab, tab)) return; // 그 사이 탭/전환이 바뀜 — 낡은 reveal 폐기
                await WaitForFramesAsync(2);
                DoRevealAfterTransition(kick);
            });
            return;
        }
        DoRevealAfterTransition(kick);
    }

    private void DoRevealAfterTransition(bool kick)
    {
        // 전환 컬럼 변경을 즉시 레이아웃에 반영해 '최종 목표 폭'을 읽는다. WPF 레이아웃은 동기라 여기서
        // ActualWidth 는 이미 최종(절반)이다 — WebView2 HWND 만 지연되므로, 이 목표를 JS 에 넘겨 clientWidth 가
        // 거기 근접할 때까지 기다리게 하면 중간 전체폭 plateau 를 확실히 건너뛴다.
        UpdateLayout();
        double target = TerminalHostContainer?.ActualWidth ?? 0;
        _terminal.RevealAfterTransition(_activeSession?.Id, kick, target);
        EndCover(); // 파일 커튼도 함께 걷는다(단일 패널 파일 전환)
    }

    /// <summary>분할 해제 시 — 이 패널의 프로젝트/세션 상태를 비운다(ConPTY·기록 보존).
    /// <paramref name="disposeTerminal"/>=true 면 보여주던 세션의 xterm 배선까지 끊는다(스왑 재부착처럼
    /// 같은 세션이 다른 패널로 옮겨가 더블 배선될 수 있는 경우 필수).
    /// false 면 xterm 인스턴스+준비(ready) 상태를 그대로 두고 상태만 비운다 — 비스왑 숨김(단순히 접혀
    /// 사라지는 우측 패널)에서 쓴다. 좌측 패널이 프로젝트를 오갈 때 그렇듯 terminal 을 보존해두면,
    /// 다시 분할로 돌아올 때 스피너·리로드·스크롤 튐 없이 즉시 재활성화된다(WebView2 는 Collapsed 로
    /// 숨겨질 뿐 파괴되지 않으므로 hidden 상태로도 출력을 계속 받아 버퍼가 최신으로 유지된다).</summary>
    public void ClearForHide(bool disposeTerminal = true)
    {
        // 커버 상태 정리 — 이 패널이 접히며 비워지므로 커튼/커버를 확실히 걷어 다음 사용 때 잔류하지 않게 한다.
        _coverActive = false;
        TerminalCurtain.BeginAnimation(UIElement.OpacityProperty, null);
        TerminalCurtain.Opacity = 1;
        TerminalCurtain.Visibility = Visibility.Collapsed;
        _terminal.FadeNow(); // 웹 레이어 #xfer-cover 도 해제(보존된 터미널에 잔류 방지)
        if (disposeTerminal && _activeSession != null)
            try { _terminal.CloseTerminal(_activeSession.Id); } catch { /* ignore */ }
        _activeProject = null;
        ClearActiveSession();
        ApplyTabsSource(null);
        RefreshSplitIndicator();
    }

    // ── 프로젝트 ─────────────────────────────────────────────────
    /// <summary>활성 프로젝트 전환 — 중앙 탭을 그 프로젝트의 탭들로 교체. 세션 활성화는 안 함.</summary>
    private void SetActiveProject(ProjectItem proj)
    {
        _activeProject = proj;
        ApplyTabsSource(proj.Tabs);
        if (ProjectPathText != null) { ProjectPathText.Text = proj.Path; ProjectPathText.ToolTip = proj.Path; }
        if (ProjectNameText != null) { ProjectNameText.Text = proj.Name; ProjectNameText.ToolTip = proj.Name; }
        UpdateProjectBranchBubble(proj);
        // 프리로드를 활성화 이후로 미룬다(Background). 지금 즉시 하면 곧 활성화될 세션까지 프리로드가
        // 기본(전체) 폭으로 ConPTY 를 먼저 만들어, 이후 절반 폭으로 리사이즈될 때 claude 히스토리가
        // 리플로우돼 깨진다(분할 좌측 세션 깨짐의 실제 원인). 활성화(show)가 최종 폭에서 세션을 만든 뒤,
        // 미뤄진 프리로드는 그 활성 세션을 제외하고 나머지만 배경 생성한다.
        if (SettingsService.LoadPreloadAllProjectSessions())
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_activeProject, proj))
                    PreloadProjectSessions(proj, except: _activeSession);
            }), System.Windows.Threading.DispatcherPriority.Background);
        RefreshSplitIndicator();
    }

    private void SplitDockBtn_Click(object sender, RoutedEventArgs e) => SplitToggleRequested?.Invoke(this);

    /// <summary>탭바 분할 토글 버튼 아이콘/툴팁/표시 여부를 실제 분할 상태(_split)에 맞춰 갱신.
    /// 아이콘/툴팁은 영속 플래그(SplitEnabled)가 아니라 현재 분할됐는지(_split)로 판단해야 한다
    /// — 컨텍스트 메뉴 "분할 보기"로 켜도 즉시 "닫기"로 바뀌고, 파트너 없이 재오픈된 빈 우측
    /// 패널에서도 닫기 버튼이 보인다.
    /// 가시성: 분할 중이면 우측 패널에만(빈 우측이어도 닫기용으로 보임), 비분할이면 프로젝트가
    /// 선택된 단일 패널에만(첫 실행 등 빈 패널은 숨김).</summary>
    public void RefreshSplitIndicator()
    {
        if (SplitDockBtn == null) return;
        bool visible = _split ? IsRightPane : (_activeProject != null);
        SplitDockBtn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (SplitDockIcon == null) return;
        SplitDockIcon.Data = (System.Windows.Media.Geometry)FindResource(_split ? "IconPanelLeftClose" : "IconPanelLeftOpen");
        SplitDockIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, _split ? "PrimaryBrush" : "TextMutedBrush");
        SplitDockBtn.ToolTip = _split ? "분할 닫기" : "분할 보기";
    }

    private System.Threading.CancellationTokenSource? _projectCts;

    private void UpdateProjectBranchBubble(ProjectItem? proj)
    {
        if (proj == null || string.IsNullOrEmpty(proj.Path) || BranchGroup == null || !PaneHasAnySessionTab())
        {
            if (BranchGroup != null) BranchGroup.Visibility = Visibility.Collapsed;
            return;
        }
        _projectCts?.Cancel();
        _projectCts = new System.Threading.CancellationTokenSource();
        _ = LoadBranchAsync(proj.Path, _projectCts.Token);
    }

    /// <summary>이 패널에 보이는(FilterTab 통과 + Hidden 아님) 세션 탭이 하나라도 있는지.
    /// 없으면(전부 다른 패널로 이동/닫힘, 파일 탭만 있음, 빈 패널 등) 브랜치·터미널 폰트 정보를 숨긴다.</summary>
    private bool PaneHasAnySessionTab()
        => _activeProject != null && _activeProject.Tabs.Any(t => t is SessionItem s && !s.Hidden && FilterTab(t));

    /// <summary>탭 이동/숨김/복원 등 "이 패널에 보이는 탭 집합"이 바뀌는 지점에서 호출 —
    /// 세션 탭이 하나도 없어지면 브랜치·터미널 폰트 정보를 같이 숨긴다.</summary>
    private void RefreshHeaderSessionGate()
    {
        if (FontSizeCombo != null)
            FontSizeCombo.Visibility = PaneHasAnySessionTab() ? Visibility.Visible : Visibility.Collapsed;
        UpdateProjectBranchBubble(_activeProject);
    }

    private async Task LoadBranchAsync(string repoDir, System.Threading.CancellationToken ct)
    {
        string? branch = null;
        try
        {
            if (await GitService.IsRepoAsync(repoDir))
            {
                var r = await GitService.RunAsync(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
                if (r.Ok)
                {
                    var name = r.Output.Trim();
                    if (!string.IsNullOrEmpty(name) && name != "HEAD") branch = name;
                }
            }
        }
        catch { /* git 미설치 등 */ }
        if (ct.IsCancellationRequested) return;
        await Dispatcher.InvokeAsync(() =>
        {
            BranchGroup.Visibility = branch != null ? Visibility.Visible : Visibility.Collapsed;
            if (branch != null) ProjectBranchText.Text = branch;
        });
    }

    /// <summary>프로젝트 선택 — 탭 교체 후 세션 하나 활성화.
    /// 우선순위: 이전 활성 세션 → 열려있는(실행 중) 세션 중 가장 위 → 첫 세션.
    /// (첫 세션이 안 열렸고 다른 세션만 열려있으면 그 열린 세션으로 연다.)</summary>
    public void SelectProject(ProjectItem proj)
    {
        SetActiveProject(proj);

        // 이전 활성 세션이 이 프로젝트 소속이면 그대로 유지.
        if (_activeSession != null && proj.Tabs.Contains(_activeSession) && !_activeSession.Hidden)
        {
            ActivateSession(_activeSession, unHide: false);
            return;
        }

        // 이 프로젝트에서 마지막으로 봤던 탭(세션/파일)을 복원 시도.
        if (TryActivateLastTab(proj)) return;

        // 폴백: 실행 중 세션 중 가장 위 → 첫 세션 → 없으면 비움.
        var sessions = proj.Tabs.OfType<SessionItem>().Where(s => !s.Hidden).ToList();
        var target = sessions.FirstOrDefault(s => s.IsAlive) ?? sessions.FirstOrDefault();
        if (target != null) ActivateSession(target, unHide: false);
        else ClearActiveSession();
        ActiveChanged?.Invoke(this);
    }

    /// <summary>proj.LastActiveTabRef("S:&lt;id&gt;"/"F:&lt;path&gt;")가 가리키는 탭을 찾아 활성화. 성공 시 true.
    /// 세션은 숨김 제외, 파일은 현재 열린 탭 중에서 찾는다(못 찾으면 false → 기본 폴백).</summary>
    private bool TryActivateLastTab(ProjectItem proj)
    {
        var rf = proj.LastActiveTabRef;
        if (string.IsNullOrEmpty(rf) || rf.Length < 2 || rf[1] != ':') return false;
        var key = rf[2..];
        switch (rf[0])
        {
            case 'S':
                var sess = proj.Tabs.OfType<SessionItem>().FirstOrDefault(s => !s.Hidden && s.Id == key);
                if (sess == null) return false;
                ActivateSession(sess, unHide: false);
                return true;
            case 'F':
                var file = proj.Tabs.OfType<FileTabItem>()
                    .FirstOrDefault(f => string.Equals(f.FilePath, key, StringComparison.OrdinalIgnoreCase));
                if (file == null) return false;
                ActivateFileTab(file);
                return true;
            default:
                return false;
        }
    }

    /// <summary>프로젝트의 "마지막 활성 탭" 참조를 갱신하고, 바뀐 경우에만 workspace.json 에 영속.
    /// 분할 중 우측 패널은 좌측과 별도 필드(SplitRightActiveRef)에 기록한다 — 안 그러면 우측 활성 탭이
    /// 좌측의 LastActiveTabRef 를 덮어써, 돌아왔을 때 좌측이 직전 탭이 아닌 엉뚱한(첫) 탭을 복원한다.</summary>
    private void RecordActiveTab(ProjectItem proj, string tabRef)
    {
        if (_split && IsRightPane)
        {
            if (proj.SplitRightActiveRef == tabRef) return;
            proj.SplitRightActiveRef = tabRef;
        }
        else
        {
            if (proj.LastActiveTabRef == tabRef) return;
            proj.LastActiveTabRef = tabRef;
        }
        WorkspaceStore.Save(Projects);
    }

    private void PreloadProjectSessions(ProjectItem proj, SessionItem? except)
    {
        foreach (var s in proj.Tabs.OfType<SessionItem>())
        {
            if (ReferenceEquals(s, except) || s.Hidden) continue;
            if (IsSessionActiveElsewhere?.Invoke(s) == true) continue; // 다른 패널이 표시 중 — 그 패널이 최종 폭으로 생성
            SettingsService.SaveClaudeCodeRoomDir(s.Id, proj.Path);
            _terminal.PreloadTerminal(s.Id);
        }
    }

    /// <summary>활성 프로젝트가 지정된 프로젝트면 비우고, next 가 있으면 그 프로젝트를 연다.</summary>
    public void OnProjectRemoved(ProjectItem proj, ProjectItem? next)
    {
        if (!ReferenceEquals(_activeProject, proj)) return;
        if (_activeSession != null) _activeSession.IsActive = false;
        _activeProject = null; _activeSession = null; _activeTab = null;
        if (next != null) SelectProject(next);
        else { ApplyTabsSource(null); ClearActiveSession(); ActiveChanged?.Invoke(this); }
    }

    // ── 세션 ─────────────────────────────────────────────────────
    private void OnTerminalSessionAction(string name, int index) => Dispatcher.BeginInvoke(() =>
    {
        switch (name)
        {
            case "newSession": if (_activeProject != null) AddSession(_activeProject); break;
            case "closeSession": if (_activeSession != null) StopTrackingSession(_activeSession); break;
            case "nextSession": CycleSession(+1); break;
            case "prevSession": CycleSession(-1); break;
            case "gotoSession": GotoSession(index); break;
        }
    });

    private void GotoSession(int index)
    {
        var sessionTabs = _activeProject?.Tabs.OfType<SessionItem>().Where(s => FilterTab(s) && !s.Hidden).ToList();
        if (sessionTabs == null || sessionTabs.Count == 0) return;
        int i = index < 0 ? sessionTabs.Count - 1 : index;
        if (i < 0 || i >= sessionTabs.Count) return;
        OpenSession(sessionTabs[i]);
    }

    private void CycleSession(int dir)
    {
        var sessionTabs = _activeProject?.Tabs.OfType<SessionItem>().Where(s => FilterTab(s) && !s.Hidden).ToList();
        if (_activeProject == null || _activeSession == null || sessionTabs == null || sessionTabs.Count < 2) return;
        int idx = sessionTabs.IndexOf(_activeSession);
        if (idx < 0) return;
        int n = sessionTabs.Count;
        OpenSession(sessionTabs[((idx + dir) % n + n) % n]);
    }

    /// <summary>전역 단축키(방향키)용 — 이 패널 안에서만 이전/다음 세션 탭으로 이동(래핑 없음).
    /// 경계(맨 끝)라 더 이동할 탭이 없으면 아무것도 바꾸지 않고 false 반환 — 호출자(MainWindow)가
    /// false 를 보면 반대편 패널로 포커스를 넘길지 판단한다.</summary>
    public bool CycleActiveSession(bool next)
    {
        var sessionTabs = _activeProject?.Tabs.OfType<SessionItem>().Where(s => FilterTab(s) && !s.Hidden).ToList();
        if (_activeProject == null || _activeSession == null || sessionTabs == null) return false;
        int idx = sessionTabs.IndexOf(_activeSession);
        if (idx < 0) return false;
        int ni = idx + (next ? 1 : -1);
        if (ni < 0 || ni >= sessionTabs.Count) return false; // 경계 — 더 이동 불가
        OpenSession(sessionTabs[ni]);
        return true;
    }

    /// <summary>전역 단축키 패널 간 이동용 — 이 패널의 첫/마지막 세션 탭을 선택.
    /// 반대편 패널 경계에서 넘어올 때 진입 지점을 정하는 데 쓴다.</summary>
    public bool SelectEdgeSession(bool first)
    {
        var sessionTabs = _activeProject?.Tabs.OfType<SessionItem>().Where(s => FilterTab(s) && !s.Hidden).ToList();
        if (sessionTabs == null || sessionTabs.Count == 0) return false;
        OpenSession(first ? sessionTabs[0] : sessionTabs[^1]);
        return true;
    }

    private static string NextSessionName(ProjectItem proj)
    {
        int max = 0;
        var rx = new System.Text.RegularExpressions.Regex(@"^세션\s+(\d+)$");
        foreach (var s in proj.Tabs.OfType<SessionItem>())
        {
            var m = rx.Match(s.Name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
        }
        return $"세션 {max + 1}";
    }

    public void AddSession(ProjectItem proj)
    {
        var available = AgentRegistry.GetEnabledAndInstalled();
        if (available.Count == 0)
        {
            ConfirmDialog.Alert("에이전트 없음",
                "사용 가능한 에이전트가 없습니다.\n설정 → 에이전트 에서 하나 이상 활성화해 주세요.");
            return;
        }
        string agentId;
        if (available.Count == 1) agentId = available[0].Id;
        else
        {
            var picked = AgentPickerDialog.Pick(Window.GetWindow(this), available, proj.Path);
            if (picked == null) return;
            agentId = picked;
        }

        var session = new SessionItem { Name = NextSessionName(proj), AgentId = agentId };
        proj.Tabs.Add(session);
        proj.IsExpanded = true;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, proj.Path);
        SettingsService.SaveAgentForRoom(session.Id, agentId);
        WorkspaceStore.Save(Projects);
        if (ReferenceEquals(_activeProject, proj))
        {
            // 이 패널이 격리(분할 파트너) 중이면 새 세션도 먼저 화이트리스트에 넣어 활성화가 격리를 풀지 않게
            // 하고(우측에 전체 세션 쏟아짐 방지), 반대 패널에선 숨겨 양쪽 중복을 막는다.
            bool isolated = _isolatedTabs != null;
            if (isolated) IsolateTab(session);
            OpenSession(session);
            if (isolated) IsolatedTabOpened?.Invoke(this, session);
        }
    }

    /// <summary>세션 포크 — 원본 대화를 복사한 새 세션을 같은 프로젝트에 만들어 연다.
    /// claude=<c>--fork-session</c>, opencode=<c>--fork</c> 네이티브 지원. gajae 는 네이티브 fork 가 없어
    /// 세션 jsonl 을 새 id 로 복사(<see cref="TerminalSessionManager.TryForkGajaeSession"/>). 그 외 에이전트는 미지원 안내.</summary>
    public void ForkSession(SessionItem source)
    {
        var proj = ParentOf(source);
        if (proj == null) return;

        var agentId = string.IsNullOrEmpty(source.AgentId)
            ? SettingsService.LoadAgentForRoom(source.Id) : source.AgentId;

        // 포크 지원 에이전트만. claude/opencode=CLI 네이티브, gajae=jsonl 복사. codex 등은 미지원.
        if (agentId != "claude" && agentId != "opencode" && agentId != "gajae")
        {
            ConfirmDialog.Alert("포크 미지원", "포크는 Claude · OpenCode · 가재코드 세션만 지원합니다.");
            return;
        }

        // 원본에 포크할 대화가 있는지 확인. claude/opencode=추적 세션 ID, gajae=파일 복사 시점에 판정.
        var srcSid = agentId == "claude"   ? SettingsService.LoadClaudeCodeRoomSession(source.Id)
                   : agentId == "opencode" ? SettingsService.LoadOpenCodeRoomSession(source.Id)
                   : null;
        if (agentId != "gajae" && string.IsNullOrWhiteSpace(srcSid))
        {
            ConfirmDialog.Alert("포크 불가",
                "아직 대화가 없어 포크할 수 없습니다.\n한 번 이상 대화한 세션만 포크할 수 있어요.");
            return;
        }

        var session = new SessionItem { Name = source.Name + " (fork)", AgentId = agentId };

        // gajae: 세션 jsonl 을 새 id 로 새 방 dir 에 복사(원본 대화 없으면 null → 포크 취소).
        if (agentId == "gajae")
        {
            var forkedId = TerminalSessionManager.TryForkGajaeSession(source.Id, session.Id);
            if (forkedId == null)
            {
                ConfirmDialog.Alert("포크 불가",
                    "아직 대화가 없어 포크할 수 없습니다.\n한 번 이상 대화한 세션만 포크할 수 있어요.");
                return;
            }
            SettingsService.SaveGajaeRoomSession(session.Id, forkedId); // 미리 확정 추적(마커 불필요)
        }

        proj.Tabs.Add(session);
        proj.IsExpanded = true;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, proj.Path); // RoomDir 은 에이전트 공통 저장소
        SettingsService.SaveAgentForRoom(session.Id, agentId);
        if (agentId != "gajae")
            SettingsService.SaveRoomForkSource(session.Id, srcSid!);  // claude/opencode: 첫 실행에 소비
        WorkspaceStore.Save(Projects);

        // 포크는 "새 세션이 열리게" 하는 게 목적 → 항상 연다(다른 프로젝트면 OpenSession 이 전환).
        bool isolatedSameProj = _isolatedTabs != null && ReferenceEquals(_activeProject, proj);
        if (isolatedSameProj) IsolateTab(session);
        OpenSession(session);
        if (isolatedSameProj) IsolatedTabOpened?.Invoke(this, session);
    }

    /// <summary>세션 클릭 — 필요하면 프로젝트 전환 후 해당 세션 활성화.</summary>
    public void OpenSession(SessionItem session)
    {
        var parent = ParentOf(session);
        if (parent == null) return;
        if (!ReferenceEquals(_activeProject, parent)) SetActiveProject(parent);
        ActivateSession(session);
    }

    /// <summary>다른 패널로 "분할 보기" 이동된 파일 탭을 이 패널에서 연다(필요 시 프로젝트 전환).</summary>
    public void OpenFileTab(FileTabItem tab)
    {
        var parent = ParentOfTab(tab);
        if (parent == null) return;
        if (!ReferenceEquals(_activeProject, parent)) SetActiveProject(parent);
        ActivateFileTab(tab);
    }

    // 이 패널에서 (패널 폭으로) 한 번이라도 표시한 세션들. 프리로드는 기본폭(80)으로 ConPTY 를 만들므로,
    // 이 패널에서 처음 표시되는 프리로드 세션은 패널 폭으로 리플로우되며 스크롤이 팍 튄다 → 그 첫 표시만 커버로 감춘다.
    private readonly HashSet<string> _shownSessions = new();

    // ReloadAllSessionsForTheme 가 종료→재시작 중인 방 id 들. 원래 활성 세션에만 안내 스피너를
    // 띄웠는데, 실제로는 이 목록의 모든 방이 같이 종료·재시작된다 — 재시작이 끝나기 전에 사용자가
    // 다른 탭을 눌러도 ActivateSession 의 콜드-게이트에서 같은 안내 문구를 보여주기 위한 추적셋.
    private readonly HashSet<string> _themeReloadRoomIds = new();
    // 재시작 중이라 연결을 미룬 탭 id 들. 정리가 끝나면(finally) 아직 그 탭을 보고 있는 경우에만 재연결.
    private readonly HashSet<string> _pendingReactivateAfterReload = new();
    private const string ThemeReloadLabel = "테마 적용 중\n세션을 다시 여는 중입니다.";

    private void ActivateSession(SessionItem session, bool unHide = true)
    {
        if (ReferenceEquals(_activeSession, session)) return;
        // 이 패널에서 처음 표시되는 프리로드(ready) 세션: 기본폭→패널폭 ConPTY 리플로우로 스크롤이 튄다.
        // 셸이 이미 커버 중이 아니면 여기서 잠깐 커버하고, 아래에서 최종 폭 재동기(kick) 후 걷는다.
        // (파일→세션·세션→세션, 사이드바 클릭·탭 클릭 모든 경로가 이 메서드로 온다.)
        bool coverReflow = !_coverActive && _terminal.IsReady(session.Id) && !_shownSessions.Contains(session.Id);
        if (coverReflow) CoverForTransition();
        ClearIsolationIfMismatch(session);
        DiagLog.Write($"ActivateSession begin: '{session.Name}' room={session.Id} isReady={_terminal.IsReady(session.Id)} alive={session.IsAlive}");
        using var _diag = DiagLog.Time($"ActivateSession '{session.Name}'");
        if (_activeSession != null) _activeSession.IsActive = false;
        var parent = ParentOf(session);
        if (parent == null) return;

        if (unHide && session.Hidden)
        {
            // 숨김 해제 시 원래 위치가 아니라 탭바 맨 오른쪽(끝)으로 옮긴다.
            int idx = parent.Tabs.IndexOf(session);
            if (idx >= 0 && idx != parent.Tabs.Count - 1) parent.Tabs.Move(idx, parent.Tabs.Count - 1);
            session.Hidden = false;
            WorkspaceStore.Save(Projects);
        }
        // 접혀 있던 프로젝트의 세션이 선택되면 자동으로 펼쳐서 보이게 한다.
        if (!parent.IsExpanded) parent.IsExpanded = true;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, parent.Path);

        if (_activeTab is FileTabItem prevFile) prevFile.IsActive = false; // 세션으로 전환 → 이전 활성 문서 해제
        _activeTab = session; // SelectedTab DP 갱신 → 이 패널 탭바만 이 탭을 선택 강조(패널별 독립)
        _activeSession = session;
        session.IsActive = true;
        RecordActiveTab(parent, "S:" + session.Id);

        // 테마 재시작이 이 방을 아직 정리 중이면 지금 TerminalSessionManager 가 들고 있는 세션은
        // 재진입 루프(cmd `goto __reenter`)가 exit 직후 스스로 되살린 "곧 죽을" 옛 세션이다 —
        // 여기 붙어 정상처럼 보여도 몇 초 뒤 배치 종료의 지연된 하드킬로 그대로 끊긴다.
        // 지금은 연결하지 않고 안내만 띄운 채, 정리가 실제로 끝난 뒤(finally 에서) 아직 이 탭을
        // 보고 있으면 그때 다시 진짜로 연결한다.
        if (_themeReloadRoomIds.Contains(session.Id))
        {
            _pendingReactivateAfterReload.Add(session.Id);
            UpdateEmptyState();
            ShowSessionLoading(session.Id, ThemeReloadLabel);
            EnsureSelectedTabVisible(session);
            RefreshModelEffortDock();
            ActiveChanged?.Invoke(this);
            // _shownSessions 는 여기서 추가하지 않는다 — 아직 실제로 터미널을 보여준 게 아니라
            // 스피너만 띄운 상태다. 잘못 마킹하면 나중에 진짜로 연결될 때 리플로우 감춤 커버가
            // "이미 이 폭으로 본 적 있음"으로 오판돼 스킵되고, 기본폭→패널폭 리플로우가 그대로
            // 노출돼 터미널이 화면 모서리에만 작게 뜨는 것처럼 보인다.
            if (coverReflow) RevealAfterTransition(kick: true);
            return;
        }

        session.IsAlive = true;
        var sessionAgentId = string.IsNullOrEmpty(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;
        if (sessionAgentId != "claude")
            AgentLastMsg?.TrackSession(parent.Path, sessionAgentId);
        _terminal.ShowTerminal(session.Id);
        _terminal.FocusTerminal();
        // 콜드(미준비) 세션: UpdateEmptyState 가 UnparkTerminalHost 로 webview 를 0×0→풀사이즈로 드러내는데,
        // 그 순간~아래 ShowSessionLoading(web 단색 커버) 사이 한 프레임 동안 빈/콜드 터미널이 노출돼
        // 프로젝트 선택 시 깜빡인다(준비된 프리로드 세션은 위 coverReflow 커튼이 가려 사각지대는 콜드뿐).
        // unpark '전에' 웹 로딩 커버를 먼저 켜 그 프레임을 없앤다(WPF 오버레이도 함께 켜 주차 구간부터
        // 단색 덮개가 끊기지 않게). 정확한 스피너 앵커는 UpdateEmptyState 로 최종 크기 확정 후 재전송한다.
        bool sessionReady = _terminal.IsReady(session.Id);
        // 게이트 조건 = 콜드(미준비) 이거나, 파일에서 오는 전환(airspace 스왑 은닉). 둘 다 unpark 을 web 커버
        // ACK 까지 미루고 md 파킹도 그때 함께 한다(RevealTerminalAfterGate) → 검정 갭 제거.
        // off-screen 파킹이라 unpark 은 리사이즈가 아니라 위치 이동뿐 → grow 없음. 따라서 파일에서 오는
        // 전환(fromFile)도 커버가 불필요(준비된 세션은 즉시 표시). 게이트는 콜드(부팅 스피너)만.
        bool needGate = !sessionReady;
        _gateUnpark = needGate;
        if (needGate)
        {
            TerminalLoadingOverlay.Visibility = Visibility.Visible;
            UpdateLayout();
            _terminal.SetLoading(true, TerminalLoadingOverlay.ActualWidth, TerminalLoadingOverlay.ActualHeight);
            ArmUnparkFallback(); // ACK 누락 대비 — 그때도 unpark 은 보장
        }
        else _unparkFallback?.Stop(); // 직전 게이트 취소(빠른 재전환)
        UpdateEmptyState();
        // 로딩 표시는 UpdateEmptyState '뒤' — 세션 헤더바 등 표시로 콘텐츠 그리드 크기가 확정된 다음
        // 기대 크기를 캡처해야 웹 스피너 게이트(뷰포트=목표 일치 대기)의 목표가 처음부터 정확하다.
        // 테마 재시작 중인 방이면 그 안내 문구를 그대로 — 종료/재시작 어느 시점에 눌러도 동일하게 보인다.
        if (needGate) ShowSessionLoading(session.Id, _themeReloadRoomIds.Contains(session.Id) ? ThemeReloadLabel : null); // 커버 유지 — 준비된 세션은 RevealTerminalAfterGate 가 걷음
        else HideSessionLoading();
        EnsureSelectedTabVisible(session);
        RefreshModelEffortDock();
        ActiveChanged?.Invoke(this);
        _shownSessions.Add(session.Id);                     // 이 패널에서 표시됨 — 다음부턴 리플로우 커버 불필요
        if (coverReflow) RevealAfterTransition(kick: true); // 최종 폭에서 세션 재동기 후 커버 걷기(리플로우 감춤)
    }

    /// <summary>작업 큐 → 활성 세션 터미널에 텍스트 입력 + Enter. 비활성/죽은 세션이면 false.</summary>
    public bool SendTextToActiveSession(string text)
    {
        var id = _activeSession?.Id;
        if (string.IsNullOrEmpty(id)) return false;
        var session = TerminalSessionManager.Instance.Get(id);
        if (session is not { IsAlive: true }) return false;
        session.Write(text);
        session.Write("\r");
        _terminal.ShowTerminal(id);
        _terminal.FocusTerminal();
        return true;
    }

    /// <summary>활성 세션 터미널에 텍스트만 삽입(엔터 없음). 외부 드래그로 경로 입력 시 사용 — 사용자가 확인 후 직접 Enter.</summary>
    private bool WriteToActiveSessionNoEnter(string text)
    {
        var id = _activeSession?.Id;
        if (string.IsNullOrEmpty(id)) return false;
        var session = TerminalSessionManager.Instance.Get(id);
        if (session is not { IsAlive: true }) return false;
        session.Write(text);
        _terminal.ShowTerminal(id);
        _terminal.FocusTerminal();
        return true;
    }

    // ── 외부 드래그 앤 드롭 (탐색기/이미지 등 → 활성 세션 터미널로 경로 입력) ──────────
    // 터미널 호스트(TerminalHostView)의 WebView2 외부 드롭은 비활성화되어 있어
    // WPF DragDrop 시스템이 TerminalHostContainer 의 Drop 이벤트로 라우팅된다.

    /// <summary>드롭 허용 확장자. 이미지(에이전트가 직접 읽을 수 있는 포맷) + 일반 텍스트(소스/문서/설정).
    /// 바이너리(.exe, .zip 등)는 토큰으로 의미가 없어 제외.</summary>
    private static readonly HashSet<string> DropExts = new(StringComparer.OrdinalIgnoreCase)
    {
        // 이미지
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".avif", ".tif", ".tiff",
        // 텍스트/소스/문서
        ".txt", ".md", ".markdown", ".rst", ".adoc",
        ".json", ".jsonc", ".yaml", ".yml", ".toml", ".ini", ".conf", ".config", ".env", ".editorconfig", ".props", ".targets",
        ".xml", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".vue", ".svelte", ".razor", ".cshtml",
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".java", ".kt", ".kts", ".swift", ".go", ".rs",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".py", ".rb", ".php", ".lua", ".dart", ".fs", ".fsi",
        ".vb", ".sql", ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".bat", ".cmd",
        ".gitignore", ".gitattributes", ".gitmodules", ".dockerignore",
    };

    private void TerminalHostContainer_DragOver(object sender, DragEventArgs e)
    {
        // 활성 세션이 살아있고, 파일 드롭 중이며, 그 중 허용 확장자가 하나라도 있을 때만 Copy 커서.
        if (_activeSession == null) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0 || !files.Any(IsDroppableFile))
        { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void TerminalHostContainer_Drop(object sender, DragEventArgs e)
    {
        if (_activeSession == null) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;

        // 허용 확장자만, 각 줄에 "@경로" 형식으로. 클로드/codex 등은 @경로 를 직접 읽어 첨부.
        var accepted = files.Where(IsDroppableFile)
                            .Select(p => "@" + p)
                            .ToArray();
        if (accepted.Length == 0) return;

        var text = string.Join("\r", accepted) + "\r";
        WriteToActiveSessionNoEnter(text);
        e.Handled = true;
    }

    private static bool IsDroppableFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            var name = Path.GetFileName(path);
            if (DropExts.Contains(name)) return true; // .gitignore, Dockerfile 등 확장자 없는 파일
            var ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && DropExts.Contains(ext);
        }
        catch { return false; }
    }

    private void ActivateFileTab(FileTabItem tab)
    {
        var parent = ParentOfTab(tab);
        if (parent == null) return;
        ClearIsolationIfMismatch(tab);

        if (_activeTab is FileTabItem prevFile) prevFile.IsActive = false; // 이전 활성 문서 하이라이트 해제
        _activeTab = tab; // SelectedTab DP 갱신 → 이 패널 탭바만 이 탭을 선택 강조(패널별 독립)
        tab.IsActive = true; // 사이드바 카드 문서 하이라이트(세션 IsActive 대응)
        if (_activeSession != null) _activeSession.IsActive = false;
        _activeSession = null;
        RecordActiveTab(parent, "F:" + tab.FilePath);

        HideSessionLoading();
        // 에디터(md/텍스트 공통)는 패널 간 공유 단일 UserControl 이다. 반대 패널로 옮겼다 돌아오면 이 패널의
        // Content 참조는 스테일(에디터를 가리키지만 실제 부모는 반대 패널)이라, Content 참조만 보는 가드는
        // 재부착을 건너뛰어 내용이 안 보인다. 실제 Parent 로 판단해 다른 컨테이너에 붙어 있으면 떼어낸 뒤
        // 이 패널에 부착한다(WPF: 한 요소는 부모가 하나뿐).
        var ctrl = tab.Editor.AsControl();
        if (!ReferenceEquals(ctrl.Parent, FileEditorHostContainer))
        {
            if (ctrl.Parent is ContentControl prevHost) prevHost.Content = null;                    // 반대 패널에서 떼기
            if (ReferenceEquals(FileEditorHostContainer.Content, ctrl)) FileEditorHostContainer.Content = null; // 스테일 참조 해제
            FileEditorHostContainer.Content = ctrl;
        }
        HookEditorInteract(tab); // 이 패널이 이 에디터를 표시하게 됐으니 포커스 통지 구독(가드로 표시 중일 때만 발화)
        UpdateEmptyState();
        EnsureSelectedTabVisible(tab);
        tab.Editor.Focus();
        RefreshModelEffortDock();
        ActiveChanged?.Invoke(this);
    }

    /// <summary>에디터 표면 클릭 → 포커스 패널 전환 구독. 에디터는 공유 단일 인스턴스라 여러 패널이
    /// 구독할 수 있으므로, 핸들러는 "이 패널이 지금 그 탭을 활성 표시 중일 때"만 FocusRequested 를 낸다
    /// (엉뚱한 패널이 포커스를 가져가는 것 방지). 패널당 에디터당 1회만 구독(중복 방지).</summary>
    private void HookEditorInteract(FileTabItem tab)
    {
        if (!_interactHooked.Add(tab.Editor)) return;
        tab.Editor.Interacted += (_, _) =>
        {
            if (ReferenceEquals(_activeTab, tab)) FocusRequested?.Invoke(this);
        };
    }
    private readonly HashSet<IFileTabEditor> _interactHooked = new();

    // ── 메타바 model/effort dock ─────────────────────────────────
    private static readonly ModelEffortOption[] ModelOptions =
    {
        new("Opus", "opus"), new("Sonnet", "sonnet"),
        new("Haiku", "haiku"), new("Fable", "fable"),
    };
    private static readonly ModelEffortOption[] EffortOptions =
    {
        new("low", "low"), new("medium", "medium"), new("high", "high"),
        new("xhigh", "xhigh"), new("max", "max"),
    };
    private const string DefaultModelValue = "opus";
    private const string DefaultEffortValue = "high";

    private bool _suppressModelEffort;
    private readonly Dictionary<string, string> _pendingModel = new();
    private readonly Dictionary<string, string> _pendingEffort = new();

    // ── 메타바 터미널 폰트 크기 dock(claude 여부와 무관, 항상 노출) ─────────
    private static readonly ModelEffortOption[] FontSizeOptions =
    {
        new("10pt", "10"), new("11pt", "11"), new("12pt", "12"), new("13pt", "13"),
        new("14pt", "14"), new("16pt", "16"), new("18pt", "18"), new("20pt", "20"),
        new("24pt", "24"), new("28pt", "28"),
    };
    private const double PtToPxRatio = 96.0 / 72.0;
    private bool _suppressFontSize;

    private void SyncFontSizeCombo(double px)
    {
        int pt = (int)Math.Round(px / PtToPxRatio);
        _suppressFontSize = true;
        try { FontSizeCombo.SelectedValue = pt.ToString(); }
        finally { _suppressFontSize = false; }
    }

    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFontSize) return;
        if (_activeSession == null) return;
        if (FontSizeCombo.SelectedValue is not string val || !int.TryParse(val, out var pt)) return;
        _terminal.SetRoomFontSizePt(_activeSession.Id, pt); // 지금 보고 있는 방에만 적용, 다른 방/새 방엔 영향 없음
    }

    private void RefreshModelEffortDock()
    {
        RefreshHeaderSessionGate(); // 세션 탭이 하나도 없으면 브랜치·터미널 폰트 정보도 같이 숨김
        if (ModelEffortDock == null) return;
        var s = _activeSession;

        // 폰트 크기는 에이전트 종류와 무관하게 항상 동기화(방별 값, 없으면 전역 기본값).
        if (s != null) SyncFontSizeCombo(_terminal.RoomEffectiveFontSizePx(s.Id));

        var agentId = s == null ? null : (string.IsNullOrEmpty(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId);
        bool isClaude = s != null && agentId == "claude";
        ModelEffortDock.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
        if (!isClaude) return;

        var (liveModelId, liveEffort) = ModelEffort?.Read(s!.Id) ?? (null, null);
        var model = ModelIdToValue(liveModelId) ?? SettingsService.LoadClaudeCodeRoomModel(s!.Id) ?? DefaultModelValue;
        var effort = (IsKnownEffort(liveEffort) ? liveEffort : null) ?? SettingsService.LoadClaudeCodeRoomEffort(s!.Id) ?? DefaultEffortValue;

        _suppressModelEffort = true;
        try
        {
            if (ModelCombo.ItemsSource == null) ModelCombo.ItemsSource = ModelOptions;
            if (EffortCombo.ItemsSource == null) EffortCombo.ItemsSource = EffortOptions;
            ModelCombo.SelectedValue = model;
            EffortCombo.SelectedValue = effort;
        }
        finally { _suppressModelEffort = false; }
    }

    private static string? ModelIdToValue(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var s = id.ToLowerInvariant();
        if (s.Contains("opus")) return "opus";
        if (s.Contains("sonnet")) return "sonnet";
        if (s.Contains("haiku")) return "haiku";
        if (s.Contains("fable") || s.Contains("mythos")) return "fable";
        return null;
    }

    private static bool IsKnownEffort(string? e)
        => e is "low" or "medium" or "high" or "xhigh" or "max";

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => OnModelEffortPicked(isModel: true);
    private void EffortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => OnModelEffortPicked(isModel: false);

    private void OnModelEffortPicked(bool isModel)
    {
        if (_suppressModelEffort) return;
        var s = _activeSession;
        if (s == null) return;
        var val = (isModel ? ModelCombo : EffortCombo).SelectedValue as string;
        if (string.IsNullOrEmpty(val)) return;

        if (isModel) SettingsService.SaveClaudeCodeRoomModel(s.Id, val);
        else SettingsService.SaveClaudeCodeRoomEffort(s.Id, val);

        if (s.IsBusy)
            (isModel ? _pendingModel : _pendingEffort)[s.Id] = val;
        else
            SendModelEffortSlash(s.Id, isModel, val);
    }

    /// <summary>응답 종료(busy→idle) 시 보류된 model/effort 변경을 라이브 주입. 셸이 호출.</summary>
    public void FlushPendingModelEffort(string roomId)
    {
        if (_pendingModel.Remove(roomId, out var m)) SendModelEffortSlash(roomId, isModel: true, m);
        if (_pendingEffort.Remove(roomId, out var ef)) SendModelEffortSlash(roomId, isModel: false, ef);
    }

    private void SendModelEffortSlash(string roomId, bool isModel, string value)
    {
        var sess = TerminalSessionManager.Instance.Get(roomId);
        if (sess is not { IsAlive: true }) return;
        _terminal.SuppressScroll(5);
        sess.Write((isModel ? "/model " : "/effort ") + value + "\r");
    }

    private void NewTabBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject == null)
        {
            ConfirmDialog.Alert("프로젝트 없음", "먼저 왼쪽 사이드바에서 프로젝트를 추가하세요.");
            return;
        }
        AddSession(_activeProject);
    }

    // ── 세션 로딩 스피너 ─────────────────────────────────────────
    private string? _loadingRoomId;
    private System.Windows.Threading.DispatcherTimer? _loadingTimeout;

    private void ShowSessionLoading(string roomId, string? label = null)
    {
        DiagLog.Write($"ShowSessionLoading room={roomId}");
        _loadingRoomId = roomId;
        TerminalLoadingOverlay.Visibility = Visibility.Visible;
        // 기대 크기(px 앵커) 전달 — 웹 스피너를 최종 레이아웃 중앙 px 에 고정해, HWND 리사이즈 지연으로
        // 뷰포트가 stale 인 동안 스피너가 옆/아래로 튀는 것을 막는다. TerminalLoadingOverlay 는 콘텐츠
        // 그리드를 가득 채우므로(주차된 터미널 컨테이너와 달리) 그 크기 = 터미널 최종 크기다.
        UpdateLayout();
        _terminal.SetLoading(true, TerminalLoadingOverlay.ActualWidth, TerminalLoadingOverlay.ActualHeight, label);

        _loadingTimeout?.Stop();
        _loadingTimeout ??= new System.Windows.Threading.DispatcherTimer();
        _loadingTimeout.Interval = TimeSpan.FromSeconds(20);
        _loadingTimeout.Tick -= LoadingTimeout_Tick;
        _loadingTimeout.Tick += LoadingTimeout_Tick;
        _loadingTimeout.Start();
    }

    private void LoadingTimeout_Tick(object? sender, EventArgs e) => HideSessionLoading();

    private void HideSessionLoading()
    {
        if (_loadingRoomId != null) DiagLog.Write($"HideSessionLoading room={_loadingRoomId}");
        _loadingTimeout?.Stop();
        _loadingRoomId = null;
        TerminalLoadingOverlay.Visibility = Visibility.Collapsed;
        _terminal.SetLoading(false);
    }

    private void HideSessionLoadingIf(string roomId)
    {
        if (_loadingRoomId == roomId) HideSessionLoading();
    }

    private void ClearActiveSession()
    {
        if (_activeSession != null) _activeSession.IsActive = false;
        if (_activeTab is FileTabItem prevFile) prevFile.IsActive = false;
        _activeSession = null;
        _activeTab = null; // SelectedTab DP=null → 이 패널 탭바 선택 강조 해제
        if (FileEditorHostContainer != null) FileEditorHostContainer.Content = null;
        HideSessionLoading();
        UpdateEmptyState();
        UpdateSelectedTabSeam();
        RefreshHeaderSessionGate();
        ActiveChanged?.Invoke(this);
    }

    public void RenameSession(SessionItem session)
    {
        var name = PromptDialog.Show("세션 이름 변경", "새 이름을 입력하세요.",
                                     defaultValue: session.Name, maxLength: 60);
        if (string.IsNullOrWhiteSpace(name) || name == session.Name) return;
        session.Name = name;
        WorkspaceStore.Save(Projects);
    }

    /// <summary>Discord 에서 스레드 삭제로 트리거 — 확인 대화상자 없이 즉시 세션 삭제(대화 기록도 제거).</summary>
    public void RemoveSessionSilent(SessionItem session) => RemoveSession(session, purge: true);

    /// <summary>Discord 에서 스레드 이름 변경으로 트리거 — 확인 없이 세션 이름만 동기화.</summary>
    public void RenameSessionSilent(SessionItem session, string newName)
    {
        var name = newName.Trim();
        if (string.IsNullOrWhiteSpace(name) || name == session.Name) return;
        if (name.Length > 60) name = name[..60];
        session.Name = name;
        WorkspaceStore.Save(Projects);
    }

    public void DeleteSession(SessionItem session)
    {
        if (!ConfirmDialog.Show("세션 삭제",
                $"'{session.Name}' 세션을 영구 삭제할까요?\n대화 기록(.jsonl)도 디스크에서 함께 삭제되며 복구할 수 없습니다.",
                okLabel: "삭제", danger: true))
            return;
        RemoveSession(session, purge: true);
    }

    /// <summary>사이드바 컨텍스트 메뉴 "세션 숨기기" — 탭 X 숨기기와 동일 동작.</summary>
    public void HideSession(SessionItem session)
    {
        session.Hidden = true;
        WorkspaceStore.Save(Projects);
        ActivateNeighborAfterHide(session);
    }

    public void StopTrackingSession(SessionItem session)
    {
        if (!ConfirmDialog.Show("세션 추적 중단",
                $"'{session.Name}' 세션을 목록에서 제거할까요?\n대화 기록은 디스크에 그대로 보존됩니다.",
                okLabel: "중단"))
            return;
        RemoveSession(session, purge: false);
    }

    private void RemoveSession(SessionItem session, bool purge)
    {
        var parent = ParentOf(session);
        bool wasActive = ReferenceEquals(_activeSession, session);
        int idx = parent?.Tabs.IndexOf(session) ?? -1;

        DisposeSessionProcess(session, purge);
        parent?.Tabs.Remove(session);
        WorkspaceStore.Save(Projects);

        if (wasActive)
        {
            var next = PickNeighborTab(parent, idx);
            if (next is SessionItem s) ActivateSession(s);
            else if (next is FileTabItem f) ActivateFileTab(f);
            else ClearActiveSession();
        }
    }

    /// <summary>닫힌 탭(원래 idx) 기준 왼쪽 우선, 없으면 오른쪽에서 표시 가능한 탭 선택.
    /// FilterTab 을 함께 봐 "이 패널에 실제로 보이는" 탭만 고른다 — 분할 시 반대쪽 패널로 넘긴(이 패널에선
    /// 숨겨진/격리 밖) 탭이 선택돼 엉뚱하게 딸려오는 것을 막는다. 비분할이면 필터가 항상 통과라 기존 동작 유지.</summary>
    private TabItemBase? PickNeighborTab(ProjectItem? parent, int removedIdx)
    {
        if (parent == null || removedIdx < 0) return null;
        bool Visible(TabItemBase t) => !(t is SessionItem s && s.Hidden) && FilterTab(t);
        for (int i = removedIdx - 1; i >= 0; i--)
            if (Visible(parent.Tabs[i])) return parent.Tabs[i];
        for (int i = removedIdx; i < parent.Tabs.Count; i++)
            if (Visible(parent.Tabs[i])) return parent.Tabs[i];
        return null;
    }

    /// <summary>세션을 숨긴 뒤(그 세션이 이 패널의 활성이었다면) 같은 패널에서 왼쪽 우선으로 이웃 탭을 활성화.
    /// PickNeighborTab 이 FilterTab 을 보므로 분할 시 반대쪽 패널 탭은 고르지 않는다. 이웃이 없으면 비운다.</summary>
    private void ActivateNeighborAfterHide(SessionItem s)
    {
        if (!ReferenceEquals(_activeSession, s)) return;
        var parent = ParentOf(s);
        int idx = parent?.Tabs.IndexOf(s) ?? -1;
        var next = PickNeighborTab(parent, idx);
        if (next is SessionItem ns) ActivateSession(ns);
        else if (next is FileTabItem nf) ActivateFileTab(nf);
        else ClearActiveSession();
    }

    /// <summary>사이드바 카드에서 문서 닫기 요청 — 파일 탭을 닫는다(내부 RemoveFileTab 위임).</summary>
    public void CloseFileTab(FileTabItem tab) => RemoveFileTab(tab);

    private void RemoveFileTab(FileTabItem tab)
    {
        var parent = ParentOfTab(tab);
        bool wasActive = ReferenceEquals(_activeTab, tab);
        int idx = parent?.Tabs.IndexOf(tab) ?? -1;
        if (FileEditorHostContainer.Content == tab.Editor.AsControl())
            FileEditorHostContainer.Content = null;
        _interactHooked.Remove(tab.Editor); // 포커스 구독 추적에서 제거(닫힌 에디터 참조 누수 방지)
        if (tab.Editor is IDisposable disposable) disposable.Dispose();
        parent?.Tabs.Remove(tab);
        PersistWorkspace(); // 닫은 파일 탭을 workspace.json 에서 제거(재시작 시 다시 안 열리도록)

        if (wasActive)
        {
            var next = PickNeighborTab(parent, idx);
            if (next is SessionItem s) ActivateSession(s);
            else if (next is FileTabItem f) ActivateFileTab(f);
            else ClearActiveSession();
        }
    }

    /// <summary>세션의 터미널 프로세스·매핑 정리(컬렉션은 건드리지 않음). 셸의 DeleteProject 도 호출.</summary>
    public void DisposeSessionProcess(SessionItem session, bool purge = true)
    {
        var workingDir = SettingsService.LoadClaudeCodeRoomDir(session.Id);
        try { _terminal.CloseTerminal(session.Id); } catch { /* ignore */ }
        try
        {
            if (purge) TerminalSessionManager.Instance.PurgeRoom(session.Id, workingDir);
            else TerminalSessionManager.Instance.DisposeRoom(session.Id, purgeTracking: false);
        }
        catch { /* ignore */ }
        if (purge) SettingsService.RemoveClaudeCodeRoomDir(session.Id);
    }

    /// <summary>활성 Claude 세션을 재시작 — MCP 매니저 저장 후 호출용. 비활성/비-Claude면 false.</summary>
    public bool TryRestartActiveClaudeSession()
    {
        if (_activeSession == null) return false;
        RestartAllClaudeSessions();
        return true;
    }

    private async void RestartAllClaudeSessions()
    {
        var allClaudeSessions = Projects
            .SelectMany(p => p.Tabs).OfType<SessionItem>()
            .Where(s =>
            {
                var aid = string.IsNullOrEmpty(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId;
                return aid == "claude";
            })
            .ToList();

        // graceful 종료는 훅 flush 대기 등으로 수 초 걸릴 수 있는데 그동안 스피너가 없으면
        // 화면이 멈춘 것처럼 보인다 — 종료 시작과 동시에 먼저 스피너를 띄운다.
        if (_activeSession != null && allClaudeSessions.Contains(_activeSession))
            ShowSessionLoading(_activeSession.Id, "세션을 안전하게 종료하는 중…");

        foreach (var s in allClaudeSessions)
        {
            try { _terminal.CloseTerminal(s.Id); } catch { /* ignore */ }
        }

        // 하드킬(DisposeRoom) 대신 graceful 종료 — Ctrl+C×2 + exit 로 claude 가 transcript 를
        // flush 하고 Stop/SessionEnd 훅을 기록할 틈을 준 뒤 정리한다.
        try { await TerminalSessionManager.Instance.GracefulDisposeRoomsAsync(allClaudeSessions.Select(s => s.Id)); }
        catch { /* best effort */ }

        foreach (var s in allClaudeSessions)
        {
            try
            {
                TerminalSessionManager.Instance.ClearDisposedRoom(s.Id);
                TerminalSessionManager.Instance.GetOrCreate(s.Id, 120, 30);
                // 곧바로 xterm 재생성+배선 — 배선 없이 세션만 만들면 시작 출력(alt-screen 신호)이
                // 버려져 그 방은 ready 를 못 찍고, 이후 진입할 때마다 로딩 스피너가 타임아웃까지 돈다.
                _terminal.PreloadTerminal(s.Id);
            }
            catch { /* ignore */ }
        }

        await Task.Delay(150);

        if (_activeSession != null && allClaudeSessions.Contains(_activeSession))
            ActivateSession(_activeSession);
    }

    /// <summary>테마 변경 적용 — 이 패널 세션의 ConPTY 를 종료한 뒤 활성 세션을 다시 불러온다.</summary>
    public async void ReloadAllSessionsForTheme()
    {
        var active = _activeSession;
        var proj = _activeProject;

        var allSessions = Projects.SelectMany(p => p.Tabs).OfType<SessionItem>().ToList();
        // 이 목록의 방들이 전부 같이 종료·재시작된다 — 재시작 끝나기 전에 다른 탭을 눌러도
        // ActivateSession 콜드-게이트가 같은 안내 문구를 보여줄 수 있게 추적해 둔다(finally 에서 정리).
        foreach (var s in allSessions) _themeReloadRoomIds.Add(s.Id);

        try
        {
            // graceful 종료는 훅 flush 대기 등으로 수 초 걸릴 수 있는데 그동안 스피너가 없으면
            // 화면이 멈춘 것처럼 보인다 — 종료 시작과 동시에 먼저 스피너를 띄운다.
            if (active != null) ShowSessionLoading(active.Id, ThemeReloadLabel);

            foreach (var s in allSessions)
            {
                try { _terminal.CloseTerminal(s.Id); } catch { /* ignore */ }
            }

            // 하드킬(DisposeRoom) 대신 graceful 종료 — Ctrl+C×2 + exit 로 각 에이전트가 transcript 를
            // flush 하고 종료 훅을 기록할 틈을 준 뒤 정리한다.
            try { await TerminalSessionManager.Instance.GracefulDisposeRoomsAsync(allSessions.Select(s => s.Id)); }
            catch { /* best effort */ }

            foreach (var s in allSessions)
            {
                try { TerminalSessionManager.Instance.ClearDisposedRoom(s.Id); }
                catch { /* ignore */ }
            }

            // GracefulDisposeRoomsAsync 가 이미 완전히 끝나 진짜 하드킬까지 된 뒤다 — 좀비 세션
            // 보호(연결 유예)가 더 이상 필요 없으니 지금 바로 해제한다. 이걸 finally 까지 미루면
            // 바로 아래의 활성 세션 재연결(ActivateSession(active))이 자기 자신을 아직 재시작
            // 중이라고 오판해 스피너만 띄우고 실제 연결을 건너뛴다(활성 세션이 바로 안 열리는 원인).
            foreach (var s in allSessions) _themeReloadRoomIds.Remove(s.Id);

            await Task.Delay(150);

            if (proj != null)
            {
                // 로드 중 사용자가 이미 다른 탭으로 옮겨갔으면(_activeSession 이 active 와 달라짐)
                // 원래 보던 탭으로 강제로 되돌리지 않는다.
                if (active != null && proj.Tabs.Contains(active) && ReferenceEquals(_activeSession, active))
                {
                    // ActivateSession 가드(_activeSession 동일 시 no-op) 우회 — 터미널이 dispose 됐으므로 강제 재활성화해 ConPTY 재생성 + 로딩 스피너 표시.
                    _activeSession.IsActive = false;
                    _activeSession = null;
                    _activeTab = null;
                    ActivateSession(active);
                    _pendingReactivateAfterReload.Remove(active.Id); // 방금 정상 재연결됨 — 아래서 중복 재연결 방지
                }
                if (SettingsService.LoadPreloadAllProjectSessions())
                    PreloadProjectSessions(proj, except: active);
            }

            // 정리 중(위에서 막 해제하기 전)에 사용자가 눌러서 연결을 미뤘던 탭들 — 이제 안전하니
            // 아직도 그 탭을 보고 있으면 지금 실제로 연결한다(다른 데로 또 옮겨갔으면 안 건드림).
            var toReactivate = _pendingReactivateAfterReload.Where(id => allSessions.Any(s => s.Id == id)).ToList();
            foreach (var id in toReactivate) _pendingReactivateAfterReload.Remove(id);
            foreach (var id in toReactivate)
            {
                var s = FindSession(id);
                if (s != null && ReferenceEquals(_activeSession, s))
                {
                    _activeSession.IsActive = false;
                    _activeSession = null;
                    _activeTab = null;
                    ActivateSession(s);
                }
            }
        }
        finally
        {
            // 안전망 — 예외로 위 본문이 중간에 끊겼어도 추적셋은 반드시 비운다.
            foreach (var s in allSessions) _themeReloadRoomIds.Remove(s.Id);
        }
    }

    private ProjectItem? ParentOf(SessionItem session)
        => AllProjects.FirstOrDefault(p => p.Tabs.Contains(session));

    private ProjectItem? ParentOfTab(TabItemBase tab)
        => AllProjects.FirstOrDefault(p => p.Tabs.Contains(tab));

    private SessionItem? FindSession(string id)
        => AllProjects.SelectMany(p => p.Tabs).OfType<SessionItem>().FirstOrDefault(s => s.Id == id);

    // ── 탭 이벤트 ─────────────────────────────────────────────────
    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (_tabDidDrag) { _tabDidDrag = false; return; }
        if (sender is FrameworkElement { DataContext: TabItemBase tab })
        {
            if (tab is SessionItem s) OpenSession(s);
            else if (tab is FileTabItem f) ActivateFileTab(f);
        }
    }

    private void Tab_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabItemBase tab }) return;
        e.Handled = true;

        var cm = new ContextMenu();

        if (tab is FileTabItem file)
        {
            var closeItem = new MenuItem { Header = "닫기", Icon = BuildMenuIcon("IconX") };
            closeItem.Click += (_, _) => file.Editor.RequestClose();
            cm.Items.Add(closeItem);

            var closeOthers = new MenuItem { Header = "다른 파일 모두 닫기", Icon = BuildMenuIcon("IconX") };
            closeOthers.Click += (_, _) =>
            {
                var parent = ParentOfTab(file);
                if (parent == null) return;
                foreach (var t in parent.Tabs.OfType<FileTabItem>().ToList())
                    if (t != file) t.Editor.RequestClose();
            };
            cm.Items.Add(closeOthers);

            cm.Items.Add(new Separator());
            cm.Items.Add(BuildSplitMoveItem(file));
        }
        else if (tab is SessionItem s)
        {
            // 이름변경·포크·내보내기 — 프로젝트 카드 세션 우클릭과 동일 기능.
            var renameItem = new MenuItem { Header = "이름 변경", Icon = BuildMenuIcon("IconPencil") };
            renameItem.Click += (_, _) => RenameSession(s);
            cm.Items.Add(renameItem);

            var forkItem = new MenuItem { Header = "포크", Icon = BuildMenuIcon("IconGitBranch") };
            forkItem.Click += (_, _) => ForkSession(s);
            cm.Items.Add(forkItem);

            var exportItem = new MenuItem { Header = "내보내기", Icon = BuildMenuIcon("IconFileText") };
            exportItem.Click += (_, _) => ExportSessionRequested?.Invoke(s);
            cm.Items.Add(exportItem);

            // Lock toggle
            var lockItem = new MenuItem
            {
                Header = s.IsLocked ? "잠금 해제" : "세션 잠금",
                Icon = BuildMenuIcon(s.IsLocked ? "IconLockOpen" : "IconLock"),
            };
            lockItem.Click += (_, _) => ToggleSessionLockRequested?.Invoke(s);
            cm.Items.Add(lockItem);

            cm.Items.Add(new Separator());
            cm.Items.Add(BuildSplitMoveItem(s));

            cm.Items.Add(new Separator());

            var hideItem = new MenuItem { Header = "숨기기", Icon = BuildMenuIcon("IconEyeOff") };
            hideItem.Click += (_, _) => HideSession(s);
            cm.Items.Add(hideItem);

            var hideOthers = new MenuItem { Header = "다른 세션 모두 숨기기", Icon = BuildMenuIcon("IconEyeOff") };
            hideOthers.Click += (_, _) =>
            {
                var parent = ParentOf(s);
                if (parent == null) return;
                foreach (var t in parent.Tabs.OfType<SessionItem>().ToList())
                {
                    if (t == s) continue;
                    t.Hidden = true;
                }
                WorkspaceStore.Save(Projects);
                if (ReferenceEquals(_activeSession, s))
                {
                    var next = parent.Tabs.OfType<SessionItem>().FirstOrDefault(x => !x.Hidden);
                    if (next != null) ActivateSession(next);
                    else ClearActiveSession();
                }
            };
            cm.Items.Add(hideOthers);

            if (!s.IsLocked)
            {
                var closeItem = new MenuItem { Header = "닫기", Icon = BuildMenuIcon("IconX") };
                closeItem.Click += (_, _) => StopTrackingSession(s);
                cm.Items.Add(closeItem);

                var deleteItem = new MenuItem { Header = "삭제", Icon = BuildMenuIcon("IconTrash2", danger: true) };
                deleteItem.SetResourceReference(MenuItem.ForegroundProperty, "DangerBrush");
                deleteItem.Click += (_, _) => DeleteSession(s);
                cm.Items.Add(deleteItem);
            }
        }

        cm.PlacementTarget = sender as UIElement;
        cm.IsOpen = true;
    }

    /// <summary>탭 우클릭 메뉴의 "분할 보기/이동" 항목. 비분할이면 "분할 보기"(새 분할 생성),
    /// 분할 중이면 이 패널이 좌/우 어느 쪽인지에 따라 "오른쪽/왼쪽으로 이동"으로 방향 표시.</summary>
    private MenuItem BuildSplitMoveItem(TabItemBase tab)
    {
        string header, iconKey;
        if (_split)
        {
            header = IsRightPane ? "왼쪽으로 이동" : "오른쪽으로 이동";
            iconKey = IsRightPane ? "IconChevronLeft" : "IconChevronRight";
        }
        else
        {
            header = "분할 보기";
            iconKey = "IconPanelLeftOpen"; // 패널 분할 버튼과 동일 아이콘
        }
        var item = new MenuItem { Header = header, Icon = BuildMenuIcon(iconKey) };
        item.Click += (_, _) => SplitViewRequested?.Invoke(this, tab);
        return item;
    }

    private System.Windows.Shapes.Path BuildMenuIcon(string iconKey, bool danger = false)
    {
        var path = new System.Windows.Shapes.Path
        {
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Style = (Style)FindResource("LucideIcon"),
            Data = (System.Windows.Media.Geometry)FindResource(iconKey),
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, danger ? "DangerBrush" : "TextMutedBrush");
        return path;
    }

    private void TabHide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabItemBase tab }) return;

        if (tab is SessionItem s)
        {
            s.Hidden = true;
            WorkspaceStore.Save(Projects);
            ActivateNeighborAfterHide(s); // 같은 패널의 왼쪽 이웃 우선(분할 시 반대쪽 패널 탭 제외)
        }
        else if (tab is FileTabItem f)
        {
            f.Editor.RequestClose();
        }
    }

    // ── 탭 드래그 순서변경 ─────────────────────────────────────────
    private Point _tabPressOrigin;
    private TabItemBase? _pendingTab;
    private ReorderDrag<TabItemBase>? _tabDrag;
    private bool _tabDidDrag;
    private bool _tabDragActive; // 드래그 중 + 버튼 숨김 상태(양쪽 패널 공통으로 셸이 토글).
    private double _dragGhostWidth;
    private readonly List<FrameworkElement> _hiddenTabFeet = new();
    private bool _tabDragHidSeam;

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabPressOrigin = e.GetPosition(TabsHost);
        _pendingTab = (sender as FrameworkElement)?.DataContext as TabItemBase;
        _tabDidDrag = false;
    }

    private void TabsHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDrag != null)
        {
            // 커서가 반대 패널이면 그쪽에 삽입 프리뷰(밀기) → 이 패널은 압축(빈자리 메움).
            var screen = TabsHost.PointToScreen(e.GetPosition(TabsHost));
            bool cross = TabDragHoverMoved?.Invoke(this, screen, _dragGhostWidth) == true;
            _tabDrag.SuppressDisplacement(cross); // 먼저 상태 전환
            _tabDrag.Update(e);                    // 그 다음 갱신 — 복귀 시 커서 기준으로 즉시 재계산(원래자리 빈 채 안 남음)
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed || _pendingTab == null) return;
        var diff = _tabPressOrigin - e.GetPosition(TabsHost);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        TryStartTabDrag(_pendingTab);
    }

    private async Task EndTabDragAsync()
    {
        var td = _tabDrag;
        _tabDrag = null;
        _pendingTab = null;
        var screen = TabsHost.PointToScreen(Mouse.GetPosition(TabsHost));
        if (Mouse.Captured == TabsHost) TabsHost.ReleaseMouseCapture();
        RestoreTabFeet();
        if (td != null)
        {
            // 커서가 반대 패널 위면 그쪽으로 이동(내부 재정렬 취소), 아니면 이 패널 내부 재정렬 커밋.
            bool crossed = TryCommitCrossDrop?.Invoke(this, td.Source, screen) == true;
            await td.FinishAsync(commit: !crossed);
        }
        SetPanesTabDragActive?.Invoke(false); // 드래그 종료 → 양쪽 + 버튼 복원
    }

    private void TryStartTabDrag(TabItemBase s)
    {
        var coll = _activeProject?.Tabs;
        if (coll == null) return;
        var rows = new List<(TabItemBase, FrameworkElement)>();
        FrameworkElement? sourceBorder = null;
        FrameworkElement? selectedRoot = null;
        foreach (var t in coll)
        {
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(t) is FrameworkElement fe
                && FindTabBorder(fe) is FrameworkElement border
                && VisualTreeHelper.GetParent(border) is FrameworkElement root)
            {
                rows.Add((t, root));
                if (ReferenceEquals(t, s)) sourceBorder = border;
                if (ReferenceEquals(t, _activeTab)) selectedRoot = root;
            }
        }
        if (sourceBorder == null) return; // 탭 1개(패널 마지막 탭)여도 크로스 패널 이동 위해 드래그 시작 허용.
        _dragGhostWidth = sourceBorder.ActualWidth;

        _tabDrag = ReorderDrag<TabItemBase>.TryStart(TabsHost, rows, s, sourceBorder,
            (tab, hostTarget, _) =>
            {
                var c = _activeProject?.Tabs;
                if (c != null)
                {
                    int from = c.IndexOf(tab);
                    if (from >= 0)
                    {
                        int to = Math.Clamp(hostTarget, 0, c.Count - 1);
                        if (to != from) { c.Move(from, to); WorkspaceStore.Save(Projects); }
                    }
                }
                return Task.CompletedTask;
            },
            exactFollow: true, horizontal: true, ghostSource: sourceBorder);
        if (_tabDrag != null)
        {
            _tabDidDrag = true;
            TabsHost.CaptureMouse();
            HideTabFeet(sourceBorder);
            SetupDragSeam(s, selectedRoot);
            SetPanesTabDragActive?.Invoke(true); // 드래그 중 양쪽 + 버튼 숨김
        }
        else
        {
            _pendingTab = null;
        }
    }

    /// <summary>탭 드래그 동안 이 패널의 + 버튼을 숨긴다(양쪽 패널에 셸이 적용). 종료 시 원복.</summary>
    public void SetTabDragActive(bool on)
    {
        _tabDragActive = on;
        if (NewTabBtn != null)
            NewTabBtn.Visibility = (!on && _activeProject != null) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 반대 패널 삽입 프리뷰(크로스 탭 드래그 중, 이 패널에 그림) ─────────────────────
    private readonly List<FrameworkElement> _previewShifted = new();

    /// <summary>커서 screen 위치의 삽입 지점부터 오른쪽 탭들을 gap 만큼 밀어 삽입 자리를 연다(반대 패널이 호출받음).</summary>
    public void ShowInsertPreview(Point screen, double gap)
    {
        int insert = InsertIndexAtScreenX(screen, null);
        _previewShifted.Clear();
        int i = 0;
        foreach (var t in VisibleTabsInOrder())
        {
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(t) is FrameworkElement fe
                && FindTabBorder(fe) is FrameworkElement border
                && VisualTreeHelper.GetParent(border) is FrameworkElement root)
            {
                AnimateTabX(root, i >= insert ? gap : 0);
                _previewShifted.Add(root);
            }
            i++;
        }
    }

    /// <summary>삽입 프리뷰 해제(모든 밀린 탭 원위치).</summary>
    public void ClearInsertPreview()
    {
        foreach (var r in _previewShifted) AnimateTabX(r, 0);
        _previewShifted.Clear();
    }

    private static void AnimateTabX(FrameworkElement el, double to)
    {
        TranslateTransform tt;
        if (el.RenderTransform is TranslateTransform t) tt = t;
        else if (el.RenderTransform is TransformGroup g && g.Children.OfType<TranslateTransform>().FirstOrDefault() is { } et) tt = et;
        else
        {
            tt = new TranslateTransform();
            if (el.RenderTransform != null && el.RenderTransform != Transform.Identity)
            { var grp = new TransformGroup(); grp.Children.Add(el.RenderTransform); grp.Children.Add(tt); el.RenderTransform = grp; }
            else el.RenderTransform = tt;
        }
        if (Math.Abs(tt.X - to) < 0.5) return;
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        tt.BeginAnimation(TranslateTransform.XProperty, anim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>커서 screen X 기준 이 패널 탭바의 삽입 인덱스(보이는 탭 중, exclude 제외, 중심이 커서 왼쪽인 개수).
    /// 삽입 프리뷰로 밀린(translate) 만큼은 빼서 '원래 레이아웃 중심'으로 판정(밀림 때문에 인덱스가 튀지 않게).</summary>
    public int InsertIndexAtScreenX(Point screen, TabItemBase? exclude)
    {
        int idx = 0;
        foreach (var t in VisibleTabsInOrder())
        {
            if (ReferenceEquals(t, exclude)) continue;
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(t) is FrameworkElement fe
                && FindTabBorder(fe) is FrameworkElement border
                && VisualTreeHelper.GetParent(border) is FrameworkElement root)
            {
                var mid = border.PointToScreen(new Point(border.ActualWidth / 2, border.ActualHeight / 2));
                if (mid.X - CurrentTranslateX(root) < screen.X) idx++;
                else break; // 좌→우 정렬 → 첫 중심이 커서 이상이면 여기 삽입.
            }
        }
        return idx;
    }

    /// <summary>요소에 걸린 TranslateTransform 의 X(프리뷰 밀림량). 없으면 0.</summary>
    private static double CurrentTranslateX(FrameworkElement el) => el.RenderTransform switch
    {
        TranslateTransform t => t.X,
        TransformGroup g when g.Children.OfType<TranslateTransform>().FirstOrDefault() is { } et => et.X,
        _ => 0,
    };

    /// <summary>이 패널 활성 프로젝트의 탭을, 보이는 탭 기준 visibleIndex 위치로 재정렬(탭바/카드 순서 = Tabs 순서).</summary>
    public void ReorderVisibleTab(TabItemBase tab, int visibleIndex)
    {
        var proj = _activeProject;
        if (proj == null || proj.Tabs.IndexOf(tab) < 0) return;
        var others = VisibleTabsInOrder().Where(t => !ReferenceEquals(t, tab)).ToList();
        if (others.Count == 0) return;
        visibleIndex = Math.Clamp(visibleIndex, 0, others.Count);
        int from = proj.Tabs.IndexOf(tab);
        if (visibleIndex >= others.Count)
        {
            int a = proj.Tabs.IndexOf(others[^1]);            // 마지막 뒤로
            int to = from < a ? a : a + 1;
            if (from != to) proj.Tabs.Move(from, to);
        }
        else
        {
            int a = proj.Tabs.IndexOf(others[visibleIndex]);  // 해당 탭 앞으로
            int to = from < a ? a - 1 : a;
            if (from != to) proj.Tabs.Move(from, to);
        }
    }

    private void HideTabFeet(FrameworkElement sourceBorder)
    {
        RestoreTabFeet();
        var root = VisualTreeHelper.GetParent(sourceBorder);
        if (root == null) return;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement
                { Name: "TabFootLeftFill" or "TabFootLeftLine"
                     or "TabFootRightFill" or "TabFootRightLine" } foot)
            {
                foot.Visibility = Visibility.Collapsed;
                _hiddenTabFeet.Add(foot);
            }
        }
    }

    private void RestoreTabFeet()
    {
        foreach (var foot in _hiddenTabFeet)
            foot.ClearValue(UIElement.VisibilityProperty);
        _hiddenTabFeet.Clear();
        _tabDragHidSeam = false;
        if (SelectedTabSeam != null) SelectedTabSeam.RenderTransform = null;
        Dispatcher.InvokeAsync(UpdateSelectedTabSeam, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SetupDragSeam(TabItemBase source, FrameworkElement? selectedRoot)
    {
        if (SelectedTabSeam == null) return;
        _tabDragHidSeam = true;
        bool draggingSelected = ReferenceEquals(source, _activeTab);
        if (draggingSelected || selectedRoot == null)
        {
            SelectedTabSeam.RenderTransform = null;
            SelectedTabSeam.Visibility = Visibility.Collapsed;
        }
        else
        {
            SelectedTabSeam.RenderTransform = GetOrCreateTranslate(selectedRoot);
        }
    }

    private static TranslateTransform GetOrCreateTranslate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        if (el.RenderTransform is TransformGroup g)
        {
            var ex = g.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (ex != null) return ex;
            var added = new TranslateTransform();
            g.Children.Add(added);
            return added;
        }
        var nt = new TranslateTransform();
        if (el.RenderTransform != null && el.RenderTransform != Transform.Identity)
        {
            var grp = new TransformGroup();
            grp.Children.Add(el.RenderTransform);
            grp.Children.Add(nt);
            el.RenderTransform = grp;
        }
        else el.RenderTransform = nt;
        return nt;
    }

    private static FrameworkElement? FindTabBorder(DependencyObject root)
    {
        if (root is FrameworkElement { Name: "TabBd" } fe) return fe;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            if (FindTabBorder(VisualTreeHelper.GetChild(root, i)) is FrameworkElement found)
                return found;
        }
        return null;
    }

    // ── 탭 오버플로우/seam/스크롤 ─────────────────────────────────
    private bool? _fadeLeft, _fadeRight;
    private double _fadeWidth = -1;
    private Action? _tabScrollAnimCancel;
    private const double TabScrollStep = 168;

    private void TabScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateTabOverflowButtons();
        UpdateSelectedTabSeam();
    }

    private void OnThemeChanged_UpdateSeam(string _key) => Dispatcher.BeginInvoke(new Action(() =>
    {
        UpdateSelectedTabSeam();
        // 테마 변경 → 에이전트 아이콘(opencode 흑/백 등) 재평가. 값 변경 없이 바인딩만 다시 돌린다.
        foreach (var proj in AllProjects)
            foreach (var s in proj.Tabs.OfType<SessionItem>())
                s.RefreshAgentIcon();
    }));

    /// <summary>외부(사이드바 카드 드래그로 탭 순서가 바뀐 뒤)에서 이 패널 탭 스트립의 선택 밑줄(seam)을
    /// 갱신 요청 — 탭 스트립이 재배치된 다음 프레임에 재계산해 밑줄이 새 위치로 따라가게 한다.</summary>
    public void RefreshSelectedTabSeam()
        => Dispatcher.InvokeAsync(UpdateSelectedTabSeam, System.Windows.Threading.DispatcherPriority.Loaded);

    private void UpdateSelectedTabSeam()
    {
        if (SelectedTabSeam == null || TabBar == null || TabsHost == null) return;
        if (_tabDragHidSeam) return;
        if (_activeTab == null ||
            TabsHost.ItemContainerGenerator.ContainerFromItem(_activeTab) is not FrameworkElement container)
        {
            SelectedTabSeam.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var pt = container.TransformToAncestor(TabBar).Transform(new Point(0, 0));
            const double seamExtra = 9.15;
            double bodyWidth = container.ActualWidth - 3;
            double seamWidth = bodyWidth + seamExtra * 2;
            double seamLeft = pt.X - seamExtra;

            double clipLeft = TabScroller.TransformToAncestor(TabBar).Transform(new Point(0, 0)).X;
            if (clipLeft < 0) clipLeft = 0;
            bool clampedLeft = false;
            if (seamLeft < clipLeft)
            {
                seamWidth -= clipLeft - seamLeft;
                seamLeft = clipLeft;
                clampedLeft = true;
            }
            if (seamWidth <= 0) { SelectedTabSeam.Visibility = Visibility.Collapsed; return; }

            SelectedTabSeam.Width = seamWidth;
            SelectedTabSeam.Margin = new Thickness(seamLeft, 0, 0, 0);

            var panelColor = (FindResource("PanelBrush") as SolidColorBrush)?.Color ?? Colors.Black;
            var clearColor = Color.FromArgb(0, panelColor.R, panelColor.G, panelColor.B);
            const double fadePx = 4;
            double f = seamWidth > 0 ? Math.Min(0.45, fadePx / seamWidth) : 0;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(clampedLeft ? panelColor : clearColor, 0));
            brush.GradientStops.Add(new GradientStop(panelColor, clampedLeft ? 0 : f));
            brush.GradientStops.Add(new GradientStop(panelColor, 1 - f));
            brush.GradientStops.Add(new GradientStop(clearColor, 1));
            brush.Freeze();
            SelectedTabSeam.Background = brush;
            SelectedTabSeam.Visibility = Visibility.Visible;
        }
        catch { SelectedTabSeam.Visibility = Visibility.Collapsed; }
    }

    private void UpdateTabOverflowButtons()
    {
        if (TabScroller == null || TabNavGroup == null) return;
        bool overflow = TabScroller.ScrollableWidth > 0.5;
        bool canLeft = TabScroller.HorizontalOffset > 0.5;
        bool canRight = TabScroller.HorizontalOffset < TabScroller.ScrollableWidth - 0.5;

        TabNavGroup.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (TabScrollLeftBtn != null) TabScrollLeftBtn.IsEnabled = canLeft;
        if (TabScrollRightBtn != null) TabScrollRightBtn.IsEnabled = canRight;

        ApplyTabEdgeFade(canLeft, canRight);
    }

    private void ApplyTabEdgeFade(bool fadeLeft, bool fadeRight)
    {
        double w = TabScroller.ActualWidth;
        if (_fadeLeft == fadeLeft && _fadeRight == fadeRight && Math.Abs(_fadeWidth - w) < 0.5) return;
        _fadeLeft = fadeLeft; _fadeRight = fadeRight; _fadeWidth = w;

        if (!fadeLeft && !fadeRight) { TabScroller.OpacityMask = null; return; }

        double f = Math.Min(0.10, 28 / Math.Max(1, w));
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var black = Colors.Black;
        var clear = Colors.Transparent;
        mask.GradientStops.Add(new GradientStop(fadeLeft ? clear : black, 0));
        mask.GradientStops.Add(new GradientStop(black, fadeLeft ? f : 0));
        mask.GradientStops.Add(new GradientStop(black, fadeRight ? 1 - f : 1));
        mask.GradientStops.Add(new GradientStop(fadeRight ? clear : black, 1));
        mask.Freeze();
        TabScroller.OpacityMask = mask;
    }

    private void TabScrollLeft_Click(object sender, RoutedEventArgs e)
        => AnimateTabScroll(TabScroller.HorizontalOffset - TabScrollStep);

    private void TabScrollRight_Click(object sender, RoutedEventArgs e)
        => AnimateTabScroll(TabScroller.HorizontalOffset + TabScrollStep);

    private void TabBar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroller.ScrollableWidth <= 0.5) return;
        _tabScrollAnimCancel?.Invoke(); _tabScrollAnimCancel = null;
        TabScroller.ScrollToHorizontalOffset(
            Math.Clamp(TabScroller.HorizontalOffset - e.Delta, 0, TabScroller.ScrollableWidth));
        e.Handled = true;
    }

    private void AnimateTabScroll(double to)
    {
        to = Math.Clamp(to, 0, TabScroller.ScrollableWidth);
        _tabScrollAnimCancel?.Invoke();
        var from = TabScroller.HorizontalOffset;
        if (Math.Abs(to - from) < 0.5) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int dur = 240;
        bool cancelled = false;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (cancelled) { CompositionTarget.Rendering -= handler!; return; }
            var t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)dur);
            TabScroller.ScrollToHorizontalOffset(from + (to - from) * EaseInOut(t));
            if (t >= 1.0) CompositionTarget.Rendering -= handler!;
        };
        CompositionTarget.Rendering += handler;
        _tabScrollAnimCancel = () => { if (cancelled) return; cancelled = true; CompositionTarget.Rendering -= handler; };
    }

    private void TabStrip_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var win = Window.GetWindow(this);
        if (win == null) return;
        if (e.ClickCount == 2)
        {
            win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { win.DragMove(); } catch { /* 이미 캡처 중 등 */ }
    }

    /// <summary>탭바가 넘쳐서 해당 탭이 스크롤 밖에 있으면 보이는 위치로 스크롤한다.
    /// "이동"으로 다른 패널에 새로 추가된 탭이 안 보일 수 있어(격리/재정렬 직후) 외부에서 명시 호출용.</summary>
    public void ScrollTabIntoView(TabItemBase tab) => EnsureSelectedTabVisible(tab);

    private void EnsureSelectedTabVisible(TabItemBase tab)
    {
        if (TabScroller == null) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            UpdateSelectedTabSeam();
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement fe) return;
            if (TabScroller.ScrollableWidth <= 0.5) return;
            var tl = fe.TransformToAncestor(TabScroller).Transform(new Point(0, 0));
            double left = tl.X + TabScroller.HorizontalOffset;
            double right = left + fe.ActualWidth;
            const double margin = 30;
            if (left < TabScroller.HorizontalOffset + margin)
                AnimateTabScroll(left - margin);
            else if (right > TabScroller.HorizontalOffset + TabScroller.ViewportWidth - margin)
                AnimateTabScroll(right - TabScroller.ViewportWidth + margin);
        }));
    }

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    // ── 상태/빈 화면 ─────────────────────────────────────────────
    // ── 터미널 HWND 0×0 주차 ─────────────────────────────────────────
    // 파일 탭/빈 패널일 때 터미널 컨테이너를 Collapsed 대신 0×0(Visible) 로 '주차'한다.
    // Collapsed 로 두면 WebView2 HWND 생성/컨트롤러 부착이 '첫 세션 표시 순간'으로 밀리고,
    // 그 순간 HwndHost 의 raw 자식 창이 컨트롤러가 붙기 전 잠깐 흰색으로 노출된다
    // (DefaultBackgroundColor 는 브라우저 렌더에만 적용 — raw 창 흰색은 못 막음 = md→세션 흰 번쩍).
    // 0×0 주차는 IsVisible=true 라 HWND·컨트롤러·페이지가 화면에 안 보인 채 미리 준비되고,
    // 첫 표시는 '이미 어두운 HWND 의 리사이즈'가 되어 흰 프레임이 없다.
    private bool _termParked;

    // 콜드 세션 unpark 게이트: web 로딩 커버(ACK=OnLoadingShown)가 켜진 뒤에만 터미널 HWND 를 unpark 해
    // unpark repaint 가 커버 위에서 일어나게 한다(부팅 노이즈 프레임 은닉). ACK 누락 대비 폴백 타이머.
    private bool _gateUnpark;
    private System.Windows.Threading.DispatcherTimer? _unparkFallback;

    /// <summary>로딩 오버레이 배경을 현재 터미널 테마 배경색으로 맞춘다(앱 배경과 달라 생기는 색 점프 제거).
    /// 파싱 실패 시 BgBrush 로 폴백. TerminalCurtain(파일 전환용)은 별개라 건드리지 않는다.</summary>
    private void ApplyTerminalBgToCovers()
    {
        if (TerminalLoadingOverlay == null) return;
        try
        {
            var hex = TerminalSessionManager.Instance.Config.Scheme.Background;
            if (!string.IsNullOrWhiteSpace(hex))
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                var brush = new System.Windows.Media.SolidColorBrush(c);
                brush.Freeze();
                TerminalLoadingOverlay.Background = brush;
                return;
            }
        }
        catch { }
        TerminalLoadingOverlay.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "BgBrush");
    }

    private void ArmUnparkFallback()
    {
        // ACK(web 커버 페인트 통지)가 정상 트리거. 폴백은 web 무응답 대비 '최후' 안전망이라 넉넉히 둔다.
        // 짧으면(구 200ms) 분할 우측(PaneB)처럼 webview 페이지가 콜드로 시작할 때 pageReady→커버 flush→ACK
        // 보다 폴백이 먼저 불려, 커버 확정 전에 unpark 돼 검정 프레임이 샌다. 대기 동안엔 파킹된 0×0 위로
        // WPF 로딩 오버레이(스피너)가 덮으므로 길게 둬도 검정 없이 스피너만 보인다.
        _unparkFallback ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2500),
        };
        _unparkFallback.Tick -= UnparkFallback_Tick;
        _unparkFallback.Tick += UnparkFallback_Tick;
        _unparkFallback.Stop();
        _unparkFallback.Start();
    }

    private void UnparkFallback_Tick(object? sender, EventArgs e)
    {
        _unparkFallback?.Stop();
        if (_gateUnpark) { _gateUnpark = false; RevealTerminalAfterGate(); }
    }

    /// <summary>web 로딩 커버가 페인트됨 — 게이트 중이면 이제 안전하게 unpark(커버 위에서 HWND repaint).</summary>
    private void OnLoadingShown()
    {
        if (!_gateUnpark) return;
        _gateUnpark = false;
        _unparkFallback?.Stop();
        RevealTerminalAfterGate();
    }

    /// <summary>게이트 해제 시점: 터미널을 unpark 하면서 '동시에' 파일 에디터를 파킹한다. 파일(md=WebView2)에서
    /// 세션으로 전환할 때 md 를 먼저 파킹하면(UpdateEmptyState) md 사라진 뒤 터미널 커버가 뜨기 전 airspace 갭에
    /// 창 배경(검정)이 새는데, md 를 이 순간까지 띄워두다 터미널 커버와 한 프레임에 맞바꿔 갭을 없앤다.</summary>
    private void RevealTerminalAfterGate()
    {
        if (_activeTab is not SessionItem s) return;
        // 터미널은 화면 밖에서 이미 전체폭으로 떠 있으므로 unpark 은 위치 이동만(리사이즈 없음) → grow strip 없음.
        // md 를 파킹해 걷으면 이미 페인트된 터미널이 그대로 드러난다.
        UnparkTerminalHost();
        ParkFileEditorHost();
        // 준비된 세션이면 커버를 즉시(페이드) 걷는다 — 콜드면 TerminalReady 가 HideSessionLoadingIf 로 걷는다.
        if (_terminal.IsReady(s.Id)) HideSessionLoading();
    }

    private void ParkTerminalHost()
    {
        if (_termParked) return;
        _termParked = true;
        // 0×0 대신 '전체폭 유지 + 화면 밖(Margin)'으로 주차. 이렇게 하면 unpark 이 리사이즈(0→full grow)가
        // 아니라 '위치 이동'만이라, 터미널 WebView2 가 grow 중 노출하던 raw HWND(검정 우측 strip/분할우측 전체)가
        // 사라진다. 컨테이너 Grid 는 ClipToBounds + 창 경계가 화면 밖 HWND 를 잘라 md/빈화면이 그대로 보인다.
        TerminalHostContainer.Width = double.NaN;
        TerminalHostContainer.Height = double.NaN;
        TerminalHostContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
        TerminalHostContainer.VerticalAlignment = VerticalAlignment.Stretch;
        TerminalHostContainer.Margin = new Thickness(-100000, 0, 100000, 0);
        TerminalHostContainer.Visibility = Visibility.Visible;
    }

    private void UnparkTerminalHost()
    {
        if (!_termParked) return;
        _termParked = false;
        // 화면 안으로 위치만 되돌린다(폭 동일 → 리사이즈 없음 → grow raw strip 없음).
        TerminalHostContainer.Width = double.NaN;
        TerminalHostContainer.Height = double.NaN;
        TerminalHostContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
        TerminalHostContainer.VerticalAlignment = VerticalAlignment.Stretch;
        TerminalHostContainer.Margin = new Thickness(0);
    }

    // 파일 에디터(md=WebView2) 컨테이너도 동일하게 0×0 주차 — Collapsed 로 감추면 HWND 생성/재표시가
    // '다시 보이는 순간'으로 밀려 컴포지터 첫 프레임(검정)이 번쩍인다(분할 복원 reveal 때 md 가
    // 까매졌다 뜨는 원인). 주차는 HWND·페이지를 안 보이게 살려 두어 표시가 리사이즈로 처리된다.
    private bool _fileParked;

    private void ParkFileEditorHost()
    {
        if (_fileParked) return;
        _fileParked = true;
        FileEditorHostContainer.Width = 0;
        FileEditorHostContainer.Height = 0;
        FileEditorHostContainer.HorizontalAlignment = HorizontalAlignment.Left;
        FileEditorHostContainer.VerticalAlignment = VerticalAlignment.Top;
        FileEditorHostContainer.Visibility = Visibility.Visible;
    }

    private void UnparkFileEditorHost()
    {
        if (_fileParked)
        {
            _fileParked = false;
            FileEditorHostContainer.Width = double.NaN;
            FileEditorHostContainer.Height = double.NaN;
            FileEditorHostContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            FileEditorHostContainer.VerticalAlignment = VerticalAlignment.Stretch;
        }
        FileEditorHostContainer.Visibility = Visibility.Visible; // 다이얼로그 suspend(Collapsed) 복귀 포함
    }

    private void UpdateEmptyState()
    {
        bool hasActive = _activeTab != null;

        if (_activeTab is SessionItem)
        {
            // 콜드 게이트 중이면 unpark 과 파일 에디터 파킹 둘 다 ACK(RevealTerminalAfterGate)까지 미룬다 —
            // 파일(md)에서 세션 전환 시 md 를 먼저 파킹하면 airspace 갭에 검정이 새므로, md 를 띄워둔 채 대기.
            if (!_gateUnpark) { UnparkTerminalHost(); ParkFileEditorHost(); }
            TerminalHostContainer.Visibility = Visibility.Visible;
        }
        else if (_activeTab is FileTabItem)
        {
            ParkTerminalHost();
            // 전환 커버 중이면 파일 에디터(md=WebView2 는 airspace 로 WPF 커튼에 안 가려짐)를 0×0 주차로
            // 감추고 TerminalCurtain(단색)으로 대신 가린다 → reveal 동기화 시 함께 나타나게(파일 조기표시 방지).
            // Collapsed 로 감추면 md HWND 생성/재표시가 reveal 순간으로 밀려 컴포지터 첫 프레임(검정)이
            // 번쩍인다 — 주차는 HWND 를 안 보이게 살려 두므로 reveal 이 '리사이즈'가 되어 검정 프레임이 없다.
            if (_coverActive) ParkFileEditorHost();
            else UnparkFileEditorHost();
        }
        else
        {
            ParkTerminalHost();
            ParkFileEditorHost();
        }

        // 파일 패널 커버: 커버 중 & 파일 탭일 때만 단색 커튼 노출(세션은 웹 레이어 #xfer-cover 가 담당).
        // 숨김은 EndCover 가 처리(fade). (커튼은 다이얼로그 suspend 와도 공유하지만 프로젝트 전환과 시점이 안 겹침.)
        if (_coverActive && _activeTab is FileTabItem)
        { TerminalCurtain.Opacity = 1; TerminalCurtain.Visibility = Visibility.Visible; }

        EmptyState.Visibility = hasActive ? Visibility.Collapsed : Visibility.Visible;

        // 프로젝트 미선택(빈 패널) 시 새 탭(+) 버튼과 메타바(#·경로) 숨김. 탭 드래그 중이면 항상 숨김.
        if (NewTabBtn != null)
            NewTabBtn.Visibility = (!_tabDragActive && _activeProject != null) ? Visibility.Visible : Visibility.Collapsed;
        ApplyProjectInfoHeaderVisibility();

        SessionHeaderBar.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        if (hasActive)
        {
            if (_activeTab is SessionItem sess)
            {
                var msg = sess.LastMessage;
                var hasMsg = !string.IsNullOrEmpty(msg);
                SessionHeaderTitleRun.Text = hasMsg ? msg : sess.Name;
                SessionHeaderTitle.ToolTip = hasMsg ? msg : null;
                LastMessageSep.Visibility = hasMsg ? Visibility.Visible : Visibility.Collapsed;
                FileHeaderIcon.Visibility = Visibility.Collapsed;
                FileHeaderPathRun.Text = string.Empty;
                FileDirtyDot.Visibility = Visibility.Collapsed;
                FileHeaderActions.Visibility = Visibility.Collapsed;
            }
            else if (_activeTab is FileTabItem file)
            {
                SessionHeaderTitleRun.Text = file.Title;
                SessionHeaderTitle.ToolTip = file.FilePath;
                LastMessageSep.Visibility = Visibility.Collapsed;
                FileHeaderIcon.Visibility = Visibility.Visible;
                FileHeaderPathRun.Text = "   " + file.FilePath;
                FileHeaderActions.Visibility = Visibility.Visible;
                RefreshFileHeaderState(file);
            }
        }
    }

    /// <summary>세션 헤더 타이틀(마지막 메시지)/구분자 폰트를 터미널 폰트 크기보다 2px 작게.</summary>
    private void ApplyHeaderFontSize(double px)
    {
        double size = Math.Max(1, px - 2);
        SessionHeaderTitle.FontSize = size;
        LastMessageSep.FontSize = size;
    }

    /// <summary>외부 훅이 세션 상태(lastmsg 등)를 갱신 → 이 패널의 활성 세션이면 헤더 즉시 갱신.</summary>
    public void NotifySessionStateChanged(SessionItem s)
    {
        if (ReferenceEquals(s, _activeSession)) UpdateEmptyState();
    }

    /// <summary>statusLine 훅이 model/effort 를 갱신 → 이 패널의 활성 세션이면 dock 갱신.</summary>
    public void NotifyModelEffortChanged(string roomId)
    {
        if (_activeSession != null && _activeSession.Id == roomId) RefreshModelEffortDock();
    }

    private void RefreshFileHeaderState(FileTabItem? file = null)
    {
        file ??= _activeTab as FileTabItem;
        if (file == null) return;
        bool dirty = file.Editor.IsDirty;
        FileDirtyDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        FileSaveBtn.IsEnabled = dirty;
        UpdateFileSize(file);
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024                     => $"{bytes} B",
            < 1024 * 1024             => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024     => $"{bytes / (1024.0 * 1024):F1} MB",
            _                         => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    private void UpdateFileSize(FileTabItem file)
    {
        try
        {
            var fi = new FileInfo(file.FilePath);
            if (fi.Exists)
                FileSizeText.Text = FormatFileSize(fi.Length);
            else
                FileSizeText.Text = "";
        }
        catch
        {
            FileSizeText.Text = "";
        }
    }
    private void FileSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is FileTabItem file && file.Editor.Save())
            RefreshFileHeaderState(file);
    }

    // ── 파일 탭 ──────────────────────────────────────────────────
    public FileTabItem? OpenFileAsTab(string path)
    {
        if (_activeProject == null) return null;
        var tab = CreateFileTab(_activeProject, path);
        if (tab == null) return null;
        // 이 패널이 격리(분할 파트너=화이트리스트) 중이면, 활성화가 ClearIsolationIfMismatch 로 격리를
        // 풀어버려 전체 탭이 쏟아진다(우측 패널에 세션 전부 뜸). 새 파일을 '먼저' 화이트리스트에 넣어
        // 활성화해도 격리가 유지되게 한다.
        bool isolated = _isolatedTabs != null;
        if (isolated) IsolateTab(tab);
        ActivateFileTab(tab);
        PersistWorkspace(); // 열린 파일 탭 목록을 workspace.json 에 영속(재시작 복원용)
        // 격리 중이었다면 반대 패널에서 이 파일을 숨겨(셸 처리) 좌우 양쪽에 다 뜨는 것을 막는다.
        if (isolated) IsolatedTabOpened?.Invoke(this, tab);
        return tab;
    }

    /// <summary>분할 파트너 복원용 — 지정 프로젝트를 이 패널에 띄우고 그 프로젝트의 파일 하나를 열어 활성화한 뒤 반환.
    /// 파일을 못 열면 null. (세션 파트너의 OpenSession 에 대응하는 파일 버전.)</summary>
    public FileTabItem? OpenFileTabForPartner(ProjectItem proj, string path)
    {
        if (!ReferenceEquals(_activeProject, proj)) SetActiveProject(proj);
        var tab = CreateFileTab(proj, path);
        if (tab == null) return null;
        ActivateFileTab(tab);
        return tab;
    }

    /// <summary>지정 프로젝트에 파일 편집기 탭을 만들어 Tabs 에 추가하고 반환(활성화는 호출부 담당).
    /// 같은 경로 탭이 이미 있으면 그것을 반환, 경로가 비었거나 로드 실패면 null.</summary>
    private FileTabItem? CreateFileTab(ProjectItem proj, string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var existing = proj.Tabs.OfType<FileTabItem>()
            .FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var tab = new FileTabItem { FilePath = path, Editor = CreateFileTabEditor(path) };
        if (!tab.Editor.LoadFile(path)) return null;
        // 닫기는 '생성 패널(this)'이 아니라 셸이 찾은 '실제 표시 패널'에서 처리하도록 위임한다.
        // (핸들러 미배선 등 예외 상황에선 생성 패널에서 직접 닫아 최소한 탭은 제거되게 폴백.)
        tab.Editor.CloseRequested += (_, _) =>
        {
            if (FileTabCloseRequested != null) FileTabCloseRequested(tab);
            else RemoveFileTab(tab);
        };
        tab.Editor.DirtyChanged += (_, _) => { if (ReferenceEquals(_activeTab, tab)) RefreshFileHeaderState(tab); };
        // Interacted(포커스 통지) 구독은 CreateFileTab(생성 패널)이 아니라 ActivateFileTab(표시 패널)에서
        // 가드와 함께 건다 — 에디터가 공유 단일 인스턴스라 다른 패널에서 표시될 때 생성 패널이 잘못 포커스되던 문제.
        proj.Tabs.Add(tab);
        return tab;
    }

    /// <summary>재시작 복원: 저장돼 있던 파일 경로들을 탭으로 다시 연다(활성화 안 함, 저장 순서 유지).
    /// 삭제됐거나 로드 실패한 파일은 건너뛴다.</summary>
    public void RestoreFileTabs(ProjectItem proj, IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                CreateFileTab(proj, path);
    }

    /// <summary>열린 파일 탭 목록 변화를 workspace.json 에 반영(파일 탭 추가/제거 시 호출).</summary>
    private void PersistWorkspace() => WorkspaceStore.Save(Projects);

    private static IFileTabEditor CreateFileTabEditor(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".md" or ".markdown" ? new MarkdownFileEditorView() : new FileEditorView();
    }

    // ── airspace 우회 (오버레이가 뜰 때 터미널 WebView2 정지) ──────────
    /// <summary>터미널 WebView2 를 스냅샷/커튼으로 대체하고 숨긴다. FileExplorer 는 셸이 처리.</summary>
    public async Task SuspendTerminalWithSnapshotAsync(bool blankCurtain = false)
    {
        // 파일 편집기(WebView2) 처리
        if (_activeTab is FileTabItem file)
    {
            if (blankCurtain)
        {
                // 설정창(modal ShowDialog)이 열린 경우 — 파일 에디터 WebView2를 visible로 유지.
                // 설정창은 별도 Window이므로 HWND airspace가 없고, 설정창 뒤로 새 테마가
                // 실시간 반영되어 보인다.
                return;
            }
            // 앱 종료 오버레이("세션 닫는 중") 등: WebView2를 숨기고 스냅샷으로 대체
            var fileSnap = await file.Editor.CaptureSnapshotAsync();
            if (fileSnap != null)
    {
                TerminalSnapshot.Source = fileSnap;
                TerminalSnapshot.Visibility = Visibility.Visible;
                // 스냅샷이 실제 프레임에 present 된 뒤 HWND(md 에디터)를 숨긴다 — 동시에 바꾸면 HWND 가
                // 먼저 사라져 빈 배경이 한 프레임 노출되며 종료 오버레이 직전 깜빡인다(SuspendTerminalOnlyAsync 와 동일 기법).
                await WaitForFramesAsync(2);
                FileEditorHostContainer.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (_activeSession == null) return;
        if (blankCurtain)
        {
            TerminalCurtain.Visibility = Visibility.Visible;
            await WaitForFramesAsync(2); // 커튼이 (HWND 뒤에서) 그려진 뒤 HWND 숨김 → 전환 프레임 빈 배경 방지
        }
        else
        {
            var snap = await _terminal.CaptureSnapshotAsync();
            if (snap != null)
            {
                TerminalSnapshot.Source = snap;
                TerminalSnapshot.Visibility = Visibility.Visible;
                await WaitForFramesAsync(2); // 스냅샷 present 후 HWND 숨김 → 빈 배경 한 프레임 노출 방지(종료 오버레이 깜빡임 제거)
            }
        }
        TerminalHostContainer.Visibility = Visibility.Collapsed;
    }

    public void ResumeTerminal()
    {
        if (_activeSession != null)
            TerminalHostContainer.Visibility = Visibility.Visible;
        if (_activeTab is FileTabItem)
            FileEditorHostContainer.Visibility = Visibility.Visible;
        TerminalSnapshot.Visibility = Visibility.Collapsed;
        TerminalSnapshot.Source = null;
        TerminalCurtain.Visibility = Visibility.Collapsed;
    }

    /// <summary>스냅샷만(커튼 없이) 정지 — 우측 오버레이 드로어용.
    /// anchorTopLeft=true 면 캡처 시점 크기로 좌상단 고정 → 패널이 리사이즈돼도 이미지가 같이 늘어나지 않고 잘려 보인다(분할 애니메이션용).
    /// stretchCover=true(webCover 전용)면 커버를 뷰포트에 맞춰 늘린다 — 전체화면 토글처럼 창 전체가
    /// 한 번에 크게 변하는 전환용(좌상단 고정은 커지는 쪽 영역이 배경색만 남아 '비어' 보인다).</summary>
    public async Task SuspendTerminalOnlyAsync(bool anchorTopLeft = false, bool webCover = false, bool stretchCover = false)
    {
        if (_activeSession == null) return;
        double cw = TerminalHostContainer.ActualWidth, ch = TerminalHostContainer.ActualHeight;

        if (webCover)
        {
            // 웹 레이어 커버 전용 경로 — HWND 를 Collapsed 하지 않는다.
            // 핵심: Collapsed→Visible 재표시 자체가 네이티브 HWND 재합성 플래시(빈/흰 프레임)를 내므로,
            // DOM 에 커버 이미지가 있어도 되살아나는 첫 프레임엔 그 플래시가 보인다. 따라서 애초에 숨기지 않고,
            // 사용자 제안대로 터미널 위(#xfer-cover, z-index 45)를 캡처 이미지로 덮은 채 애니메이션한다.
            // fit 은 억제되고(리플로우 없음) 커버가 정지 화면을 보여주므로 라이브가 비쳐 깜빡이지 않는다.
            // resume(RevealAfterTransition)이 최종 폭에서 fit 후 커버→라이브 크로스페이드. HWND 전환 0회 = 무플래시.
            var png = await _terminal.CapturePngAsync();
            if (png != null) _terminal.CoverForTransitionImage(png, cw, ch, stretchCover);
            else _terminal.CoverForTransition(); // 폴백: 단색 커버
            await WaitForFramesAsync(2); // 커버가 올라온 뒤 애니메이션 시작(가리기 전 리플로우 프레임 방지)
            return;
        }

        // 기존 스냅샷 + Collapsed 경로 (분할 슬라이드 애니메이션·좁은 창 오버레이 전용).
        var snapPng = await _terminal.CapturePngAsync();
        if (snapPng != null)
        {
            if (anchorTopLeft)
            {
                TerminalSnapshot.HorizontalAlignment = HorizontalAlignment.Left;
                TerminalSnapshot.VerticalAlignment = VerticalAlignment.Top;
                TerminalSnapshot.Width = cw;
                TerminalSnapshot.Height = ch;
            }
            TerminalSnapshot.Source = TerminalHostView.BitmapFromPng(snapPng);
            TerminalSnapshot.Visibility = Visibility.Visible;
            // 스냅샷이 실제 화면 프레임에 present 된 것을 확인한 뒤에 WebView2 HWND 를 숨긴다.
            // 같은 블록에서 동시에 바꾸면 네이티브 HWND(별도 렌더 파이프라인)가 스냅샷보다
            // 먼저 사라져, 그 아래 배경이 한 프레임 노출되며 "확 깜빡"인다. 두 렌더 프레임을
            // 기다리면 스냅샷이 확실히 올라온 뒤 HWND 가 사라져 빈 프레임이 없다.
            await WaitForFramesAsync(2);
        }
        // Collapsed 로 숨긴다 → WebView2(HwndHost)의 네이티브 HWND 가 실제로 가려진다.
        // (Hidden 은 레이아웃 슬롯을 남겨 HwndHost HWND 가 그대로 보이므로 금지 — 애니메이션 중 라이브 터미널이 비쳐 깜빡인다.)
        TerminalHostContainer.Visibility = Visibility.Collapsed;
    }

    // ── 종료 오버레이용 2단계 suspend ──────────────────────────────────
    // 기존(각 패널이 캡처→대기→hide 를 순차 수행)은 패널·에디터 HWND 가 서로 다른 프레임에 사라져
    // 팝이 여러 번 어긋나 보였다(= 종료 시 깜빡임). 셸이 ①모든 패널 스냅샷 present → ②같은 프레임에
    // 일괄 hide 하도록 준비/커밋을 분리한다.
    private enum ShutdownHide { None, Terminal, FileEditor }
    private ShutdownHide _shutdownHide;

    /// <summary>①스냅샷만 올린다(HWND 유지). 숨길 대상은 기억해 뒀다 CommitShutdownHide 가 처리.</summary>
    public async Task PrepareShutdownSnapshotAsync()
    {
        _shutdownHide = ShutdownHide.None;
        if (_activeTab is FileTabItem file)
        {
            var snap = await file.Editor.CaptureSnapshotAsync();
            if (snap == null) return; // 캡처 실패 — 에디터를 그대로 두면 최소한 내용은 보인다(기존 동작)
            TerminalSnapshot.Source = snap;
            TerminalSnapshot.Visibility = Visibility.Visible;
            _shutdownHide = ShutdownHide.FileEditor;
            return;
        }
        if (_activeSession == null) return;
        var png = await _terminal.CapturePngAsync();
        if (png != null)
        {
            TerminalSnapshot.Source = TerminalHostView.BitmapFromPng(png);
            TerminalSnapshot.Visibility = Visibility.Visible;
        }
        _shutdownHide = ShutdownHide.Terminal; // 캡처 실패해도 HWND 는 숨겨야 오버레이가 보인다
    }

    /// <summary>②스냅샷 present 확인(셸이 WaitForFramesAsync) 후 — HWND 를 숨긴다. 모든 패널이 같은 프레임에.</summary>
    public void CommitShutdownHide()
    {
        switch (_shutdownHide)
        {
            case ShutdownHide.FileEditor: FileEditorHostContainer.Visibility = Visibility.Collapsed; break;
            case ShutdownHide.Terminal: TerminalHostContainer.Visibility = Visibility.Collapsed; break;
        }
        _shutdownHide = ShutdownHide.None;
    }

    /// <summary>지정한 수만큼 컴포지션 렌더 프레임이 지나갈 때까지 대기. HwndHost 를 숨기기 전에
    /// WPF 스냅샷이 실제로 화면에 present 됐음을 보장해 airspace 전환 깜빡임을 없앤다.</summary>
    internal static async Task WaitForFramesAsync(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            var tcs = new TaskCompletionSource<bool>();
            EventHandler? h = null;
            h = (_, _) =>
            {
                System.Windows.Media.CompositionTarget.Rendering -= h;
                tcs.TrySetResult(true);
            };
            System.Windows.Media.CompositionTarget.Rendering += h;
            await tcs.Task;
        }
    }

    public void ResumeTerminalOnly(bool webCover = false)
    {
        // webCover: HWND 를 숨긴 적이 없으므로 되살릴 것도, WPF 스냅샷도 없다. 최종 폭을 확정(UpdateLayout)해
        // JS 에 넘겨, clientWidth 가 거기 근접하면 fit 억제 해제 + fit + ConPTY 재동기 후 커버 이미지를
        // 라이브 터미널로 크로스페이드한다 → resume 리플로우가 커버 아래서 일어나 안 보이고, HWND 전환 플래시도 없다.
        if (webCover)
        {
            if (_activeSession != null)
            {
                UpdateLayout();
                double target = TerminalHostContainer?.ActualWidth ?? 0;
                _terminal.RevealAfterTransition(_activeSession.Id, kick: true, expectWidth: target);
            }
            return;
        }

        if (_activeSession != null)
            TerminalHostContainer.Visibility = Visibility.Visible;
        TerminalSnapshot.Visibility = Visibility.Collapsed;
        TerminalSnapshot.Source = null;
        // 고정 크기/정렬 원복(다음 사용에서 기본 Fill 동작으로).
        TerminalSnapshot.Width = double.NaN;
        TerminalSnapshot.Height = double.NaN;
        TerminalSnapshot.HorizontalAlignment = HorizontalAlignment.Stretch;
        TerminalSnapshot.VerticalAlignment = VerticalAlignment.Stretch;
    }

    public void DisposeTerminal() => _terminal.Dispose();

    private void OnTerminalFileOpenRequested(string rawPath)
    {
        if (string.IsNullOrEmpty(rawPath)) return;

        // 절대경로(C:\... 또는 /...) → 그대로, 상대경로 → 프로젝트 루트와 조합
        string fullPath;
        if (System.IO.Path.IsPathRooted(rawPath))
        {
            fullPath = rawPath;
        }
        else
        {
            var proj = _activeProject;
            if (proj == null) return;
            fullPath = System.IO.Path.Combine(proj.Path, rawPath);
        }

        if (!System.IO.File.Exists(fullPath)) return;
        OpenFileAsTab(fullPath);
    }
}
