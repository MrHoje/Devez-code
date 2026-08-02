using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>백그라운드 프리로드를 포함해 해당 방의 TUI가 입력 가능한 상태가 됐을 때 알린다.</summary>
    public event Action<string>? SessionTerminalReady;
    /// <summary>세션이 표시되거나 사용자가 해당 터미널을 조작함. 셸의 유휴 시간 추적용.</summary>
    public event Action<string>? SessionActivity;

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
    /// <summary>세션 탭 우클릭 "외부 터미널로 열기" → 셸이 같은 세션 ID를 외부로 인계.</summary>
    public event Action<SessionItem>? ExternalSessionRequested;
    /// <summary>외부 실행 오버레이 "인앱으로 가져오기" → 외부 터미널을 종료시키고 세션을 인앱으로 복귀.</summary>
    public event Action<SessionItem>? ReturnExternalSessionRequested;
    /// <summary>세션 탭 우클릭 "잠금/잠금 해제" → MainWindow 가 토글.</summary>
    public event Action<SessionItem>? ToggleSessionLockRequested;
    /// <summary>세션 숨김/닫기/삭제는 부모-자식 서브트리 단위 처리를 위해 MainWindow에 위임.</summary>
    public event Action<SessionItem>? HideSessionRequested;
    public event Action<SessionItem>? StopTrackingSessionRequested;
    public event Action<SessionItem>? DeleteSessionRequested;
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
    /// <summary>브라우저 탭 닫기를 실제 활성/표시 패널로 라우팅하도록 셸에 위임한다.</summary>
    public event Action<BrowserTabItem>? BrowserTabCloseRequested;

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
        _terminal.TerminalReady += id =>
        {
            HideSessionLoadingIf(id);
            if (_activeSession?.Id == id) RefreshModelEffortDock();
            SessionTerminalReady?.Invoke(id);
        };
        // 에이전트별 종료 단축키 자동 재실행(claude·gjc=배치 루프 플래그, opencode=onExited 재배선) 동안
        // 배치 에코·부팅 출력이 보이지 않게 즉시 커버. 재실행된 TUI 의 준비 신호(alt-screen/인라인 마커)가
        // TerminalReady 로 커버를 걷는다(실패 시 폴백 6~8s·로딩 타임아웃 20s).
        _terminal.SessionRestarting += id => { if (_activeSession?.Id == id) ShowSessionLoading(id, "세션을 다시 시작하는 중…"); };
        // 단독 ESC 취소 → busy 스피너 + 입력 대기 ❗ 즉시 해제(훅 신호보다 빠른 UI 반응; 훅도 곧 확정).
        _terminal.InterruptRequested += id => { var s = FindSession(id); if (s != null) { s.IsBusy = false; s.IsWaitingChoice = false; } };
        // 선택지 답변(Enter/숫자키) → 대기 ❗ 즉시 해제(busy 는 유지 — claude 가 답변 처리로 계속 진행).
        // 툴을 안 띄우는 메뉴(PostToolUse 미발화)에서도 ❗ 가 확실히 빠지게 하는 보조 신호.
        _terminal.MenuInputSubmitted += id => { var s = FindSession(id); if (s is { IsWaitingChoice: true }) s.IsWaitingChoice = false; };
        _terminal.SessionActionRequested += OnTerminalSessionAction;
        _terminal.UserInteracted += () => FocusRequested?.Invoke(this);
        _terminal.SessionActivity += id => SessionActivity?.Invoke(id);
        // 세션 헤더 타이틀(마지막 메시지) 폰트를 터미널 폰트 크기와 동기화.
        _terminal.FontSizePxChanged += ApplyHeaderFontSize;
        // 터미널 → 경로 Ctrl+클릭 → 파일은 에디터 탭, 폴더는 Explorer.
        _terminal.FileOpenRequested += OnTerminalFileOpenRequested;
        _terminal.BrowserUrlOpenRequested += OnTerminalBrowserUrlOpenRequested;
        // Explorer 파일이 WebView2 터미널에 들어오면 HWND를 숨기고 패널 전체 드롭 선택 화면으로 전환.
        _terminal.ExternalFileDragEntered += ShowFileDropOverlay;
        _terminal.ExternalFileDropReceived += DismissFileDropOverlay;
        _fileDropOverlayCursorTimer.Tick += (_, _) => CheckFileDropOverlayCursor();
        Unloaded += (_, _) => _fileDropOverlayCursorTimer.Stop();
        // synced reveal 준비 완료 → 셸로 전달(셸이 좌우를 모아 동시에 fade)
        _terminal.RevealPrepared += () => RevealPrepared?.Invoke(this);
        // 콜드 세션: web 로딩 커버가 켜진 것(ACK)을 확인한 뒤에만 터미널 HWND 를 unpark 한다.
        _terminal.LoadingShown += OnLoadingShown;
        // 앱 재시작 복원 경로에서는 먼저 WPF 스냅샷을 보여 주고, 읽기 전용 xterm 생성 ACK 뒤
        // 라이브 외부 출력으로 교체한다.
        _terminal.ExternalPreviewReady += id =>
        {
            if (_activeSession is { IsExternal: true } session && session.Id == id)
                RevealExternalSessionPreview();
        };
        // 로딩 오버레이(파킹 중 터미널 영역 덮개) 배경을 '터미널 배경색'과 맞춘다 — 앱 배경(BgBrush)으로 두면
        // unpark 후 웹 커버/터미널(터미널 배경색)과 색이 달라 앱배경→터미널배경 점프가 검정 깜빡으로 보인다.
        ApplyTerminalBgToCovers();
        App.ThemeChanged += _ => ApplyTerminalBgToCovers();
        Loaded += (_, _) => ApplyHeaderFontSize(_terminal.EffectiveFontSizePx);
        // 터미널 폰트 크기 dock: 항상 노출(모델과 무관), Ctrl+휠/Ctrl+0 로 바뀌어도 콤보 선택값 동기화.
        FontSizeCombo.ItemsSource = FontSizeOptions;
        _terminal.FontSizePxChanged += SyncFontSizeCombo;
        Loaded += (_, _) => SyncFontSizeCombo(_terminal.EffectiveFontSizePx);
        _agentModelStateTimer.Tick += (_, _) => RefreshExternalAgentModelStateIfChanged();
        _agentModelStateTimer.Tick += (_, _) => RefreshUsageDock(); // 활성 세션 토큰 사용량 주기 갱신(busy 중에도)
        Loaded += (_, _) => _agentModelStateTimer.Start();
        Unloaded += (_, _) => _agentModelStateTimer.Stop();
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

    /// <summary>
    /// 외부 Explorer 파일을 프로젝트 정보 바·탭 바·세션 타이틀 바에 드롭하면
    /// 터미널 입력으로 넘기지 않고 현재 패널의 파일 탭으로 연다.
    /// </summary>
    private void FileOpenHeader_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool canDrop = _activeProject != null && GetDroppedFiles(e).Length > 0;
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (canDrop) ShowFileDropOverlay();
    }

    private void FileOpenHeader_PreviewDrop(object sender, DragEventArgs e)
    {
        var files = GetDroppedFiles(e);
        e.Handled = true;
        HideFileDropOverlay();
        if (_activeProject == null || files.Length == 0) return;

        FocusRequested?.Invoke(this);
        foreach (var path in files)
            OpenFileAsTab(path);
    }

    /// <summary>패널 콘텐츠 영역(빈 화면·파일 에디터·외부 세션 커버) 위 외부 파일 드래그 — 세션과 동일하게
    /// 패널 전체 드롭 선택 화면으로 전환한다. 터미널·브라우저·md 편집기는 WebView2(별도 HWND)라 여기로
    /// 오지 않는다(터미널/md 는 웹에서 fileDragEnter 통지, 브라우저는 웹 기본 동작 유지).
    /// 오버레이가 뜨면 콘텐츠 호스트들이 Collapsed 되어 이후 드래그는 오버레이 존들이 받는다.</summary>
    private void PaneContent_PreviewDragOver(object sender, DragEventArgs e)
    {
        var files = GetDroppedFiles(e);
        e.Effects = DragDropEffects.None; // 존(열기/첨부)에서만 드롭 — 세션과 동일
        e.Handled = true;
        // 프로젝트 미선택(완전히 빈 패널)이어도 드롭 경로가 등록된 프로젝트 하위면 그 프로젝트로 열 수 있다.
        if (files.Length > 0 && (_activeProject != null || ResolveProjectForPaths(files) != null))
            ShowFileDropOverlay();
    }

    private void PaneContent_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        HideFileDropOverlay();
    }

    /// <summary>드롭된 경로들을 담고 있는 등록 프로젝트(가장 깊게 일치하는 것). 없으면 null.
    /// 프로젝트를 아직 안 띄운 빈 패널에 파일을 떨어뜨렸을 때 어느 프로젝트로 열지 판단한다.</summary>
    private ProjectItem? ResolveProjectForPaths(IReadOnlyList<string> files)
    {
        ProjectItem? best = null;
        foreach (var proj in AllProjects)
        {
            if (string.IsNullOrWhiteSpace(proj.Path)) continue;
            var root = proj.Path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!files.Any(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase))) continue;
            if (best == null || proj.Path.Length > best.Path.Length) best = proj;
        }
        return best;
    }

    /// <summary>파일을 열 프로젝트를 확보한다 — 이미 활성 프로젝트가 있으면 그대로, 없으면 경로로 추론해 활성화.
    /// 어느 프로젝트에도 속하지 않으면 안내하고 false.</summary>
    private bool EnsureProjectForPaths(string[] files)
    {
        if (_activeProject != null) return true;
        var proj = ResolveProjectForPaths(files);
        if (proj == null)
        {
            ConfirmDialog.Alert("파일 열기",
                "이 파일이 속한 프로젝트가 없습니다.\n프로젝트를 먼저 추가한 뒤 다시 시도하세요.",
                iconKey: "IconTriangleAlert");
            return false;
        }
        SetActiveProject(proj);
        ApplyProjectInfoHeaderVisibility(); // 프로젝트가 생겼으니 메타바(#·경로) 노출 조건 재평가
        return true;
    }

    private static string[] GetDroppedFiles(DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
                e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
                return [];

            return paths
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private bool _fileDropOverlayActive;
    private Border? _highlightedFileDropZone;
    private readonly System.Windows.Threading.DispatcherTimer _fileDropOverlayCursorTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(60)
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    /// <summary>
    /// 현재 패널 전체를 파일 드롭 선택 화면으로 전환한다.
    /// HwndHost(WebView2) 위에는 WPF가 그려지지 않으므로 세 콘텐츠 호스트를 Collapsed 처리한다.
    /// </summary>
    public void ShowFileDropOverlay()
    {
        // 프로젝트 미선택(빈 패널)도 허용한다 — 드롭 시 경로로 프로젝트를 추론해 연다(EnsureProjectForPaths).
        if (_fileDropOverlayActive) return;

        _fileDropOverlayActive = true;
        // 파일 탭 활성 중엔 _activeSession 이 null 이라 터미널의 활성 방으로 판단한다(마지막 세션에 첨부).
        AddFileDropZone.IsEnabled = _activeSession is { IsExternal: false } session
            ? TerminalSessionManager.Instance.Get(session.Id) is { IsAlive: true }
            : _activeSession == null && _terminal.CanInsertFilePaths;
        FileDropOverlay.Visibility = Visibility.Visible;

        TerminalHostContainer.Visibility = Visibility.Collapsed;
        FileEditorHostContainer.Visibility = Visibility.Collapsed;
        BrowserHostContainer.Visibility = Visibility.Collapsed;
        _fileDropOverlayCursorTimer.Start();
        FocusRequested?.Invoke(this);
    }

    private void HideFileDropOverlay(bool restoreContent = true)
    {
        if (!_fileDropOverlayActive) return;
        _fileDropOverlayCursorTimer.Stop();
        SetHighlightedFileDropZone(null);
        _fileDropOverlayActive = false;
        FileDropOverlay.Visibility = Visibility.Collapsed;
        if (restoreContent) UpdateEmptyState();
    }

    public void DismissFileDropOverlay() => HideFileDropOverlay();

    private void CheckFileDropOverlayCursor()
    {
        if (!_fileDropOverlayActive)
        {
            _fileDropOverlayCursorTimer.Stop();
            return;
        }

        if (!IsLoaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
        {
            HideFileDropOverlay();
            return;
        }

        if (!GetCursorPos(out var cursor)) return;

        Point local;
        try
        {
            local = PointFromScreen(new Point(cursor.X, cursor.Y));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        // 패널 사이/좌우의 스플리터(4~6px) 위를 지날 때 오버레이가 닫혔다 다시 열리며 깜빡이지 않도록
        // 스플리터 폭보다 넉넉한 여유를 둔다(반대 패널로 완전히 넘어가면 여유를 벗어나 정상적으로 닫힘).
        const double tolerance = 12;
        if (local.X < -tolerance || local.Y < -tolerance ||
            local.X > ActualWidth + tolerance || local.Y > ActualHeight + tolerance)
        {
            HideFileDropOverlay();
            return;
        }

        Border? hoveredZone = IsCursorInside(OpenFileDropZone, cursor)
            ? OpenFileDropZone
            : IsCursorInside(AddFileDropZone, cursor) && AddFileDropZone.IsEnabled
                ? AddFileDropZone
                : null;
        SetHighlightedFileDropZone(hoveredZone);
    }

    private static bool IsCursorInside(FrameworkElement element, CursorPoint cursor)
    {
        try
        {
            var point = element.PointFromScreen(new Point(cursor.X, cursor.Y));
            return point.X >= 0 && point.Y >= 0 &&
                   point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SetHighlightedFileDropZone(Border? zone)
    {
        if (ReferenceEquals(_highlightedFileDropZone, zone)) return;

        if (_highlightedFileDropZone != null)
        {
            _highlightedFileDropZone.SetResourceReference(
                Border.BackgroundProperty, "PanelSoftBrush");
            _highlightedFileDropZone.SetResourceReference(
                Border.BorderBrushProperty, "LineBrush");
        }

        _highlightedFileDropZone = zone;
        if (zone == null) return;

        zone.SetResourceReference(Border.BackgroundProperty, "ProjectCardHoverBrush");
        zone.SetResourceReference(Border.BorderBrushProperty, "ProjectCardHoverBorderBrush");
    }

    private void FileDropOverlay_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        SetHighlightedFileDropZone(null);
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void FileDropOverlay_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        HideFileDropOverlay();
    }

    private void FileDropZone_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool canDrop = sender is FrameworkElement { IsEnabled: true } &&
                       GetDroppedFiles(e).Length > 0;
        SetHighlightedFileDropZone(canDrop ? sender as Border : null);
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FileDropZone_PreviewDrop(object sender, DragEventArgs e)
    {
        var files = GetDroppedFiles(e);
        var action = (sender as FrameworkElement)?.Tag as string;
        bool enabled = sender is FrameworkElement { IsEnabled: true };
        e.Handled = true;

        if (!enabled || files.Length == 0)
        {
            HideFileDropOverlay();
            return;
        }

        HideFileDropOverlay();
        if (string.Equals(action, "Add", StringComparison.Ordinal))
        {
            _terminal.InsertFilePaths(files);
            return;
        }

        if (!EnsureProjectForPaths(files)) return; // 빈 패널이면 경로로 프로젝트를 골라 활성화
        foreach (var path in files)
            OpenFileAsTab(path);
    }

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
        else if (next is BrowserTabItem nb) ActivateBrowserTab(nb);
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

    /// <summary>탭 참조 문자열("S:&lt;id&gt;"/"F:&lt;path&gt;"/"B:&lt;id&gt;"). 분할 상태 영속/복원용.</summary>
    public static string RefOf(TabItemBase? t) => t switch
    {
        SessionItem s => "S:" + s.Id,
        FileTabItem f => "F:" + f.FilePath,
        BrowserTabItem b => "B:" + b.Id,
        _ => "",
    };

    /// <summary>현재 우측 패널에 격리된 탭들의 참조 목록(같은 프로젝트 분할 시 우측 전체 탭). 격리 아님/빈 값 제외.</summary>
    public List<string> CurrentIsolatedRefs()
        => _isolatedTabs?.Select(RefOf).Where(r => r.Length > 0).ToList() ?? new List<string>();

    /// <summary>이 패널 탭바에 실제로 보이는(FilterTab 통과) 탭들의 참조. 화이트리스트/블랙리스트 모드 무관하게
    /// "이 패널이 지금 보여주는 탭 집합"을 준다 — 분할 상태 저장 시 우측 전용 집합 계산에 쓴다.</summary>
    public List<string> VisibleTabRefs()
        => _activeProject?.Tabs.Where(t => FilterTab(t) && !(t is SessionItem s && s.IsEffectivelyHidden))
               .Select(RefOf).Where(r => r.Length > 0).ToList() ?? new List<string>();

    /// <summary>이 패널 활성 탭의 참조.</summary>
    public string? ActiveTabRef() => _activeTab == null ? null : RefOf(_activeTab);

    /// <summary>이 탭이 이 패널의 탭바에 실제로 보이는지(활성 프로젝트 소속 + 필터 통과). 사이드바 클릭 시
    /// 그 세션이 실제로 있는 패널을 골라 여는 데 쓴다 — 같은 프로젝트 분할에서 우측 격리 세션이 좌측에서
    /// 안 열리던 문제 방지.</summary>
    public bool ShowsTab(TabItemBase tab) => _activeProject != null && _activeProject.Tabs.Contains(tab) && FilterTab(tab);

    /// <summary>현재 이 패널이 파일 탭을 활성으로 보여주는지. 파일→세션 전환 시 세션 터미널 리플로우를 커버로 감추는 판정용.</summary>
    public bool ActiveIsFile => _activeTab is FileTabItem;

    /// <summary>활성 프로젝트의 Tabs 에서 참조("S:id"/"F:path"/"B:id")에 해당하는 탭을 찾는다. 세션은 숨김 제외.</summary>
    public TabItemBase? FindTabByRef(string? @ref)
    {
        if (_activeProject == null || string.IsNullOrEmpty(@ref) || @ref!.Length < 2 || @ref[1] != ':') return null;
        var key = @ref[2..];
        return @ref[0] switch
        {
            'S' => _activeProject.Tabs.OfType<SessionItem>().FirstOrDefault(s => !s.IsEffectivelyHidden && s.Id == key),
            'F' => _activeProject.Tabs.OfType<FileTabItem>().FirstOrDefault(f => string.Equals(f.FilePath, key, StringComparison.OrdinalIgnoreCase)),
            'B' => _activeProject.Tabs.OfType<BrowserTabItem>().FirstOrDefault(b => b.Id == key),
            _ => null,
        };
    }

    /// <summary>참조가 가리키는 탭을 이 패널에서 활성화(세션/파일 라우팅). 못 찾으면 false.</summary>
    public bool ActivateByRef(string? @ref)
    {
        var t = FindTabByRef(@ref);
        if (t is SessionItem s) { ActivateSession(s); return true; }
        if (t is FileTabItem f) { ActivateFileTab(f); return true; }
        if (t is BrowserTabItem b) { ActivateBrowserTab(b); return true; }
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
        // 채널 보더가 없을 때도 WebView2 HwndHost 의 DPI 오버렌더가 포커스 보더를 덮지 않게
        // 우측 1px 여백을 확보한다.
        FocusFrame.Margin = new Thickness(0, 1, show ? 0 : 1, 0);
    }

    /// <summary>분할 중 포커스 패널을 고정 1px 프레임의 색상으로 표시한다.</summary>
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
        var next = parent?.Tabs.OfType<SessionItem>().FirstOrDefault(x => !ReferenceEquals(x, s) && !x.IsEffectivelyHidden);
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
    // 설정/MCP 창·종료 오버레이 등 스냅샷·커튼 suspend 중. 이 동안 에이전트 훅(lastmsg 갱신)이
    // NotifySessionStateChanged → UpdateEmptyState 를 태우면 Visible 복원으로 라이브 HWND 가
    // 스냅샷 위로 되살아난다(airspace) — suspend 중엔 표시 복원을 건너뛰게 하는 가드.
    private bool _overlaySuspended;

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
    public void RevealAfterTransition(bool kick = false, bool bounce = false)
    {
        // md 에디터가 콜드 로드 중이면 준비될 때까지 커튼을 유지(단일 패널 파일 전환의 "까매졌다 열림" 방지).
        if (_activeSession == null && _activeTab is FileTabItem { Editor: MarkdownFileEditorView md } tab && !md.IsEditorShellReady)
        {
            WhenMdReady(md, async () =>
            {
                if (!ReferenceEquals(_activeTab, tab)) return; // 그 사이 탭/전환이 바뀜 — 낡은 reveal 폐기
                await WaitForFramesAsync(2);
                DoRevealAfterTransition(kick, bounce);
            });
            return;
        }
        DoRevealAfterTransition(kick, bounce);
    }

    private void DoRevealAfterTransition(bool kick, bool bounce = false)
    {
        // 전환 컬럼 변경을 즉시 레이아웃에 반영해 '최종 목표 폭'을 읽는다. WPF 레이아웃은 동기라 여기서
        // ActualWidth 는 이미 최종(절반)이다 — WebView2 HWND 만 지연되므로, 이 목표를 JS 에 넘겨 clientWidth 가
        // 거기 근접할 때까지 기다리게 하면 중간 전체폭 plateau 를 확실히 건너뛴다.
        UpdateLayout();
        double target = TerminalHostContainer?.ActualWidth ?? 0;
        RememberActiveSessionSize(target);
        _terminal.RevealAfterTransition(_activeSession?.Id, kick, target, bounce);
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
        if (ProjectPathText != null) { ProjectPathText.Text = proj.Path; ProjectPathText.ToolTip = "디렉토리 열기"; }
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

    /// <summary>프로젝트 경로를 클릭하면 존재하는 프로젝트 폴더를 Windows 탐색기에서 연다.</summary>
    private void ProjectPathText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var path = _activeProject?.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch { /* 탐색기 실행 실패는 UI 상태에 영향 없음 */ }
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
        => _activeProject != null && _activeProject.Tabs.Any(t => t is SessionItem s && !s.IsEffectivelyHidden && FilterTab(t));

    /// <summary>탭 이동/숨김/복원 또는 활성 탭 전환 시 헤더 dock 상태를 갱신한다.
    /// 터미널 폰트는 활성 세션에서만 조절할 수 있고, 브랜치 정보는 보이는 세션 탭 기준으로 유지한다.</summary>
    private void RefreshHeaderSessionGate()
    {
        if (AttachFileBtn != null)
            AttachFileBtn.Visibility = _activeTab is SessionItem { IsExternal: false } ? Visibility.Visible : Visibility.Collapsed;
        if (FontSizeCombo != null)
            FontSizeCombo.Visibility = _activeTab is SessionItem { IsExternal: false } ? Visibility.Visible : Visibility.Collapsed;
        UpdateProjectBranchBubble(_activeProject);
    }

    private async Task LoadBranchAsync(string repoDir, System.Threading.CancellationToken ct)
    {
        string? branch = null;
        int ahead = 0;
        int behind = 0;
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

                if (branch != null)
                {
                    var sync = await GitService.RunAsync(repoDir, "rev-list", "--left-right", "--count", "HEAD...@{upstream}");
                    var counts = sync.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (sync.Ok && counts.Length == 2)
                    {
                        int.TryParse(counts[0], out ahead);
                        int.TryParse(counts[1], out behind);
                    }
                }
            }
        }
        catch { /* git 미설치 등 */ }
        if (ct.IsCancellationRequested) return;
        await Dispatcher.InvokeAsync(() =>
        {
            BranchGroup.Visibility = branch != null ? Visibility.Visible : Visibility.Collapsed;
            if (branch != null) ProjectBranchText.Text = branch;

            var syncParts = new List<string>(2);
            if (behind > 0) syncParts.Add($"↓{behind}");
            if (ahead > 0) syncParts.Add($"↑{ahead}");
            ProjectSyncCountText.Text = string.Join(" / ", syncParts);
            ProjectSyncCountText.Visibility = syncParts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ProjectSyncCountText.ToolTip = syncParts.Count > 0
                ? $"받을 커밋 {behind}개 · 보낼 커밋 {ahead}개"
                : null;
        });
    }

    /// <summary>프로젝트 선택 — 탭 교체 후 직전 탭을 복원하고, 없으면 세션/파일/브라우저 순으로 연다.</summary>
    public void SelectProject(ProjectItem proj)
    {
        SetActiveProject(proj);

        // 이전 활성 세션이 이 프로젝트 소속이면 그대로 유지.
        if (_activeSession != null && proj.Tabs.Contains(_activeSession) && !_activeSession.IsEffectivelyHidden)
        {
            ActivateSession(_activeSession, unHide: false);
            return;
        }

        // 이 프로젝트에서 마지막으로 봤던 탭(세션/파일/브라우저)을 복원 시도.
        if (TryActivateLastTab(proj)) return;

        // 폴백: 실행 중 세션 중 가장 위 → 첫 세션 → 첫 파일/브라우저 탭 → 없으면 비움.
        var sessions = proj.Tabs.OfType<SessionItem>().Where(s => !s.IsEffectivelyHidden).ToList();
        var target = sessions.FirstOrDefault(s => s.IsAlive) ?? sessions.FirstOrDefault();
        if (target != null) ActivateSession(target, unHide: false);
        else if (proj.Tabs.FirstOrDefault(t => t is not SessionItem s || !s.IsEffectivelyHidden) is FileTabItem file) ActivateFileTab(file);
        else if (proj.Tabs.FirstOrDefault(t => t is not SessionItem s || !s.IsEffectivelyHidden) is BrowserTabItem browser) ActivateBrowserTab(browser);
        else ClearActiveSession();
        ActiveChanged?.Invoke(this);
    }

    /// <summary>proj.LastActiveTabRef("S:&lt;id&gt;"/"F:&lt;path&gt;"/"B:&lt;id&gt;")가 가리키는 탭을 활성화한다.</summary>
    private bool TryActivateLastTab(ProjectItem proj)
    {
        var rf = proj.LastActiveTabRef;
        if (string.IsNullOrEmpty(rf) || rf.Length < 2 || rf[1] != ':') return false;
        var key = rf[2..];
        switch (rf[0])
        {
            case 'S':
                var sess = proj.Tabs.OfType<SessionItem>().FirstOrDefault(s => !s.IsEffectivelyHidden && s.Id == key);
                if (sess == null) return false;
                ActivateSession(sess, unHide: false);
                return true;
            case 'F':
                var file = proj.Tabs.OfType<FileTabItem>()
                    .FirstOrDefault(f => string.Equals(f.FilePath, key, StringComparison.OrdinalIgnoreCase));
                if (file == null) return false;
                ActivateFileTab(file);
                return true;
            case 'B':
                var browser = proj.Tabs.OfType<BrowserTabItem>().FirstOrDefault(b => b.Id == key);
                if (browser == null) return false;
                ActivateBrowserTab(browser);
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
        var sessions = proj.Tabs.OfType<SessionItem>()
            .Where(s => !ReferenceEquals(s, except) && !s.IsEffectivelyHidden && !s.IsExternal)
            .Where(s => !_themeReloadRoomIds.Contains(s.Id)) // 테마 종료 중 비활성 방을 백그라운드에서 되살리지 않음
            .Where(s => IsSessionActiveElsewhere?.Invoke(s) != true) // 다른 패널이 표시 중 — 그 패널이 최종 폭으로 생성
            .ToList();

        SettingsService.SaveClaudeCodeRoomDirs(sessions.Select(s => s.Id), proj.Path);
        foreach (var s in sessions)
            _terminal.PreloadTerminal(s.Id);
    }

    /// <summary>활성 프로젝트가 지정된 프로젝트면 비우고, next 가 있으면 그 프로젝트를 연다.</summary>
    public void OnProjectRemoved(ProjectItem proj, ProjectItem? next)
    {
        if (!ReferenceEquals(_activeProject, proj)) return;
        if (_activeSession != null) _activeSession.IsActive = false;
        if (_activeTab is BrowserTabItem browser) browser.IsActive = false;
        if (BrowserHostContainer != null) BrowserHostContainer.Content = null;
        _activeProject = null; _activeSession = null; _activeTab = null;
        if (next != null) SelectProject(next);
        else { ApplyTabsSource(null); ClearActiveSession(); ActiveChanged?.Invoke(this); }
    }

    // ── 세션 ─────────────────────────────────────────────────────
    private void OnTerminalSessionAction(string name, int index) => Dispatcher.BeginInvoke(() =>
    {
        // 이 액션들은 터미널 안에서 키로 들어온다 — 모달을 띄우거나 방을 바꾸기 전에 조합 상태를
        // 끊어, 모달 뒤/새 방에서 첫 한글이 중복 입력되는 것을 막는다(TerminalHostView.AbortIme 참고).
        _terminal.AbortIme();
        switch (name)
        {
            case "newSession": if (_activeProject != null) AddSession(_activeProject); break;
            case "closeSession": if (_activeSession != null) StopTrackingSession(_activeSession); break;
            case "hideSession": if (_activeSession != null) HideSession(_activeSession); break;
            case "deleteSession": if (_activeSession != null) DeleteSession(_activeSession); break;
            case "renameSession": if (_activeSession != null) RenameSession(_activeSession); break;
            case "nextSession": CycleSession(+1); break;
            case "prevSession": CycleSession(-1); break;
            case "gotoSession": GotoSession(index); break;
        }
    });

    private void GotoSession(int index)
    {
        var sessionTabs = _activeProject?.Tabs.OfType<SessionItem>().Where(s => FilterTab(s) && !s.IsEffectivelyHidden).ToList();
        if (sessionTabs == null || sessionTabs.Count == 0) return;
        int i = index < 0 ? sessionTabs.Count - 1 : index;
        if (i < 0 || i >= sessionTabs.Count) return;
        OpenSession(sessionTabs[i]);
    }

    private void CycleSession(int dir)
    {
        var sessionTabs = _activeProject?.Tabs.OfType<SessionItem>().Where(s => FilterTab(s) && !s.IsEffectivelyHidden).ToList();
        if (_activeProject == null || _activeSession == null || sessionTabs == null || sessionTabs.Count < 2) return;
        int idx = sessionTabs.IndexOf(_activeSession);
        if (idx < 0) return;
        int n = sessionTabs.Count;
        OpenSession(sessionTabs[((idx + dir) % n + n) % n]);
    }

    /// <summary>이 패널 탭바에 실제로 보이는 탭들(세션·파일·diff·브라우저 전부)을 표시 순서대로 반환.
    /// 단축키 이동이 세션뿐 아니라 열린 모든 탭을 동일하게 순회하도록 하는 근거 집합.</summary>
    private List<TabItemBase> VisibleTabs()
        => _activeProject?.Tabs.Where(t => FilterTab(t) && !(t is SessionItem s && s.IsEffectivelyHidden)).ToList()
           ?? new List<TabItemBase>();

    /// <summary>탭 종류(세션/파일·diff/브라우저)에 맞는 활성화 경로로 분기.</summary>
    private void ActivateTab(TabItemBase tab)
    {
        switch (tab)
        {
            case SessionItem s:    ActivateSession(s); break;
            case FileTabItem f:    ActivateFileTab(f); break;
            case BrowserTabItem b: ActivateBrowserTab(b); break;
        }
    }

    /// <summary>전역 단축키(방향키)용 — 이 패널 안에서만 이전/다음 탭으로 이동(래핑 없음).
    /// 세션·파일·diff·브라우저를 구분하지 않고 보이는 모든 탭을 동일하게 순회한다.
    /// 경계(맨 끝)라 더 이동할 탭이 없으면 아무것도 바꾸지 않고 false 반환 — 호출자(MainWindow)가
    /// false 를 보면 반대편 패널로 포커스를 넘길지 판단한다.</summary>
    public bool CycleActiveSession(bool next)
    {
        var tabs = VisibleTabs();
        if (_activeProject == null || _activeTab == null || tabs.Count == 0) return false;
        int idx = tabs.IndexOf(_activeTab);
        if (idx < 0) return false;
        int ni = idx + (next ? 1 : -1);
        if (ni < 0 || ni >= tabs.Count) return false; // 경계 — 더 이동 불가
        ActivateTab(tabs[ni]);
        return true;
    }

    /// <summary>전역 단축키 패널 간 이동용 — 이 패널의 첫/마지막 탭을 선택(종류 무관).
    /// 반대편 패널 경계에서 넘어올 때 진입 지점을 정하는 데 쓴다.</summary>
    public bool SelectEdgeSession(bool first)
    {
        var tabs = VisibleTabs();
        if (tabs.Count == 0) return false;
        ActivateTab(first ? tabs[0] : tabs[^1]);
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

    private static string NextBrowserName(ProjectItem proj)
    {
        int max = 0;
        var rx = new System.Text.RegularExpressions.Regex(@"^웹 브라우저(?:\s+(\d+))?$");
        foreach (var browser in proj.Tabs.OfType<BrowserTabItem>())
        {
            var match = rx.Match(browser.Name);
            if (!match.Success) continue;
            int n = match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : 1;
            if (n > max) max = n;
        }
        return max == 0 ? "웹 브라우저" : $"웹 브라우저 {max + 1}";
    }

    public SessionItem? AddSession(ProjectItem proj)
    {
        _terminal.AbortIme(); // 아래 모달(에이전트 선택·이름 입력) 전에 조합 상태 정리 — 중복 입력 방지
        var available = AgentRegistry.GetEnabledAndInstalled();
        if (available.Count == 0)
        {
            ConfirmDialog.Alert("에이전트 없음",
                "사용 가능한 에이전트가 없습니다.\n설정 → 에이전트 에서 하나 이상 활성화해 주세요.");
            return null;
        }
        string agentId;
        if (available.Count == 1) agentId = available[0].Id;
        else
        {
            var picked = AgentPickerDialog.Pick(Window.GetWindow(this), available, proj.Path);
            if (picked == null) return null;
            agentId = picked;
        }

        var sessionName = NextSessionName(proj);
        if (SettingsService.LoadPromptForNewSessionName())
        {
            var enteredName = PromptDialog.Show("새 세션 이름", "새 이름을 입력하세요.",
                                                defaultValue: sessionName, maxLength: 60);
            if (enteredName == null) return null;
            sessionName = enteredName;
        }

        var session = new SessionItem { Name = sessionName, AgentId = agentId };
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
        return session;
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

        // 포크 지원 에이전트만. claude/opencode/grok=CLI 네이티브, gajae/codex=jsonl 복사.
        // antigravity 는 미지원 — db 복사 실측 결과 "trajectory not found"(대화가 서버측 trajectory 에
        // 등록되어야 해 로컬 복사로 분기 불가, agy TUI 내 /fork 만 유효. 2026-07-13 실측).
        if (agentId != "claude" && agentId != "opencode" && agentId != "gajae" && agentId != "codex"
            && agentId != "grok" && agentId != "kimi" && agentId != "devezvibe")
        {
            ConfirmDialog.Alert("포크 미지원", agentId == "antigravity"
                ? "Antigravity 는 대화가 서버에 묶여 있어 앱에서 포크할 수 없습니다.\nagy 화면 안에서 /fork 명령을 사용하세요."
                : "포크는 Claude · OpenCode · 가재코드 · Codex · Grok · Kimi · Devez Vibe 세션만 지원합니다.");
            return;
        }

        // 원본에 포크할 대화가 있는지 확인. claude/opencode/codex/grok=추적 세션 ID, gajae=파일 복사 시점에 판정.
        var srcSid = agentId == "claude"   ? SettingsService.LoadClaudeCodeRoomSession(source.Id)
                   : agentId == "opencode" ? SettingsService.LoadOpenCodeRoomSession(source.Id)
                   : agentId == "codex"    ? SettingsService.LoadCodexRoomSession(source.Id)
                   : agentId == "grok"     ? SettingsService.LoadGrokRoomSession(source.Id)
                   : agentId == "kimi"     ? (SettingsService.LoadKimiRoomSession(source.Id) ?? KimiHookService.LoadTrackedSessionId(source.Id))
                   : agentId == "devezvibe" ? (SettingsService.LoadDevezVibeRoomSession(source.Id) ?? DevezVibeStateService.LoadTrackedSessionId(source.Id))
                   : null;
        if (agentId != "gajae" && string.IsNullOrWhiteSpace(srcSid))
        {
            ConfirmDialog.Alert("포크 불가",
                "아직 대화가 없어 포크할 수 없습니다.\n한 번 이상 대화한 세션만 포크할 수 있어요.");
            return;
        }

        var session = new SessionItem { Name = source.Name + " (fork)", AgentId = agentId };

        // gajae·claude: transcript(.jsonl)를 새 id 로 즉시 복사해 독립 세션을 만든다(원본 대화 없으면 취소).
        // claude 도 --fork-session(지연) 대신 eager 복사 — 포크 방 재실행 시 원본에서 재포크되던 문제 해소.
        // opencode/grok: 첫 실행에 CLI --fork / --fork-session 소비.
        string? forkedId = null;
        if (agentId == "gajae")
            forkedId = TerminalSessionManager.TryForkGajaeSession(source.Id, session.Id);
        else if (agentId == "claude")
            forkedId = TerminalSessionManager.TryForkClaudeSession(srcSid!, proj.Path);
        else if (agentId == "codex" || agentId == "devezvibe")
            // dvz 세션은 codex rollout 그 자체라 같은 복사기를 쓴다(새 id 로 복사 + 내부 id 치환).
            forkedId = TerminalSessionManager.TryForkCodexSession(srcSid!);
        else if (agentId == "kimi")
            forkedId = TerminalSessionManager.TryForkKimiSession(srcSid!);
        if (agentId != "opencode" && agentId != "grok" && forkedId == null)
        {
            ConfirmDialog.Alert("포크 불가",
                "아직 대화가 없어 포크할 수 없습니다.\n한 번 이상 대화한 세션만 포크할 수 있어요.");
            return;
        }

        proj.Tabs.Add(session);
        proj.IsExpanded = true;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, proj.Path); // RoomDir 은 에이전트 공통 저장소
        SettingsService.SaveAgentForRoom(session.Id, agentId);
        if (agentId == "gajae")
            SettingsService.SaveGajaeRoomSession(session.Id, forkedId!);   // 미리 확정 추적(마커 불필요)
        else if (agentId == "claude")
            SettingsService.SaveClaudeCodeRoomSession(session.Id, forkedId!); // 즉시 독립 세션 → 바로 resume
        else if (agentId == "codex")
            SettingsService.SaveCodexRoomSession(session.Id, forkedId!);   // 복사한 새 세션 id 로 바로 resume
        else if (agentId == "kimi")
            SettingsService.SaveKimiRoomSession(session.Id, forkedId!);    // 복사한 새 세션 id 로 바로 resume
        else if (agentId == "devezvibe")
            SettingsService.SaveDevezVibeRoomSession(session.Id, forkedId!); // 복사한 rollout id 로 바로 -r
        else
            SettingsService.SaveRoomForkSource(session.Id, srcSid!);        // opencode/grok: 첫 실행에 --fork 소비
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

    /// <summary>세션을 선택하거나 포커스를 옮기지 않고 터미널만 백그라운드에서 시작한다.</summary>
    public void PreloadSession(SessionItem session)
    {
        if (session.IsExternal) return;
        var parent = ParentOf(session);
        if (parent == null) return;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, parent.Path);
        _terminal.PreloadTerminal(session.Id);
    }

    public bool IsTerminalReady(string roomId) => _terminal.IsReady(roomId);

    /// <summary>다른 패널로 "분할 보기" 이동된 파일 탭을 이 패널에서 연다(필요 시 프로젝트 전환).</summary>
    public void OpenFileTab(FileTabItem tab)
    {
        var parent = ParentOfTab(tab);
        if (parent == null) return;
        if (!ReferenceEquals(_activeProject, parent)) SetActiveProject(parent);
        ActivateFileTab(tab);
    }

    /// <summary>브라우저 탭 클릭/분할 이동 — 필요하면 소속 프로젝트로 전환한 뒤 활성화.</summary>
    public void OpenBrowserTab(BrowserTabItem tab)
    {
        var parent = ParentOfTab(tab);
        if (parent == null) return;
        if (!ReferenceEquals(_activeProject, parent)) SetActiveProject(parent);
        ActivateBrowserTab(tab);
    }

    // 이 패널에서 세션별로 마지막 표시한 터미널 크기(폭·높이). 처음 표시되는 프리로드 세션뿐 아니라, 다른 탭을
    // 보는 동안 사이드패널(폭)이나 하단 터미널 패널(높이)이 열고 닫혀 크기가 달라진 세션도 다음 show 전에
    // 커버해야 한다. 단순 HashSet 으로 "한 번 봤음"만 기억하면 그 복귀가 일반 show 로 빠져
    // Codex 입력영역의 리사이즈 중간 프레임이 노출된다(하단 잘림 고착).
    private readonly Dictionary<string, (double W, double H)> _shownSessionSizes = new();

    private void RememberActiveSessionSize(double width)
    {
        double height = TerminalHostContainer?.ActualHeight ?? 0;
        if (_activeSession != null && width > 1)
            _shownSessionSizes[_activeSession.Id] = (width, height);
    }

    // 테마 적용으로 종료 중인 방 id 들. 완료 전 사용자가 다른 탭을 눌러도 죽어가는 프로세스에 붙지 않고
    // 같은 안내 문구를 보여준 뒤, 정리가 끝났을 때 실제로 보고 있는 방만 다시 열기 위한 추적셋.
    private readonly HashSet<string> _themeReloadRoomIds = new();
    // 재시작 중이라 연결을 미룬 탭 id 들. 정리가 끝나면(finally) 아직 그 탭을 보고 있는 경우에만 재연결.
    private readonly HashSet<string> _pendingReactivateAfterReload = new();
    private const string ThemeReloadLabel = "테마 적용 중\n세션을 다시 여는 중입니다.";

    // 숨김 후 실제 종료 대기(유예) CTS. 3초 안에 다시 열면 취소 → 프로세스 그대로 복귀.
    private readonly Dictionary<string, CancellationTokenSource> _pendingHideStopCts = new();
    // 유예 만료 후 graceful 종료 진행 중. 이 동안 다시 열면 옛 프로세스에 붙지 말고 완료 후 resume.
    private readonly HashSet<string> _gracefulStopRoomIds = new();
    private readonly HashSet<string> _pendingReactivateAfterHideStop = new();
    private const string HideStopLabel = "세션을 안전하게 종료하는 중…";
    private const int HideStopDelayMs = 3000; // 실수 숨김 복구 유예
    private string? _externalPreviewRoomId;
    private bool _externalReady = true; // 외부 오버레이 상태: true=실행 중(버튼), false=여는 중(스피너)
    private bool _returnRequested;      // "인앱으로 가져오기" 눌러 복귀 진행 중(버튼 재활성 방지)
    private SessionItem? _externalBusyWatch; // busy 변화 구독 중인 외부 세션

    private void ActivateSession(SessionItem session, bool unHide = true)
    {
        session.AcknowledgeCompletionPulse();
        if (session.IsExternal)
        {
            ActivateExternalSession(session, unHide);
            return;
        }
        SessionActivity?.Invoke(session.Id);
        if (ReferenceEquals(_activeSession, session)) return;
        // 처음 표시되는 프리로드 세션 또는 마지막 표시 이후 패널 폭이 달라진 ready 세션은 show 전에
        // 커버한다. show 가 먼저 fit 하면 Codex 인라인 TUI 의 지움/재그리기 중간 프레임이 노출되고,
        // 사후 opacity/refresh 로는 이미 어긋난 입력영역을 안정적으로 복구하지 못한다.
        // 커버를 먼저 올리면 show 의 fit 이 억제되고, RevealAfterTransition 이 최종 폭에서 fit한 뒤
        // Codex 출력이 quiet 해질 때까지 기다려 완성 프레임만 보여준다.
        // 인라인 TUI(codex/gjc) 한정 — alt-screen 에이전트(claude 등)는 SIGWINCH 한 번에 스스로
        // 완전한 프레임을 다시 그리므로 커버→fit 억제→reveal 개입이 오히려 재렌더와 간섭해
        // 하단 입력영역이 사라진 채 고착됐다(창모드 세션 로드 증상). devez 는 커버 없이 정상.
        double currentTerminalWidth = TerminalHostContainer?.ActualWidth ?? 0;
        double currentTerminalHeight = TerminalHostContainer?.ActualHeight ?? 0;
        bool hadPreviousSize = _shownSessionSizes.TryGetValue(session.Id, out var previousSize);
        bool sizeChanged = !hadPreviousSize
            || (currentTerminalWidth > 1 && Math.Abs(currentTerminalWidth - previousSize.W) > 2)
            || (currentTerminalHeight > 1 && Math.Abs(currentTerminalHeight - previousSize.H) > 2); // 하단 터미널 패널 = 높이 변화
        var reflowAgent = AgentRegistry.Find(string.IsNullOrWhiteSpace(session.AgentId)
            ? SettingsService.LoadAgentForRoom(session.Id) : session.AgentId);
        bool coverReflow = !_coverActive && _terminal.IsReady(session.Id) && sizeChanged
            && reflowAgent?.InlineTui == true;
        if (coverReflow)
        {
            DiagLog.Write($"ActivateSession reflow cover room={session.Id} size={(hadPreviousSize ? $"{previousSize.W:F1}x{previousSize.H:F1}" : "first")}->{currentTerminalWidth:F1}x{currentTerminalHeight:F1}");
            CoverForTransition();
        }
        ClearIsolationIfMismatch(session);
        DiagLog.Write($"ActivateSession begin: '{session.Name}' room={session.Id} isReady={_terminal.IsReady(session.Id)} alive={session.IsAlive}");
        using var _diag = DiagLog.Time($"ActivateSession '{session.Name}'");
        if (_activeSession != null) _activeSession.IsActive = false;
        var parent = ParentOf(session);
        if (parent == null) return;

        if (unHide && session.IsEffectivelyHidden)
        {
            // 직접 숨김 + 숨긴 조상 경로를 함께 해제. 새로 보이게 된 서브트리의 종료 예약도 취소.
            foreach (var visible in parent.UnhideSessionPath(session)) CancelPendingHideStop(visible.Id);
            WorkspaceStore.Save(Projects);
        }
        // 접혀 있던 프로젝트의 세션이 선택되면 자동으로 펼쳐서 보이게 한다.
        if (!parent.IsExpanded) parent.IsExpanded = true;
        SettingsService.SaveClaudeCodeRoomDir(session.Id, parent.Path);

        if (_activeTab is FileTabItem prevFile) prevFile.IsActive = false; // 세션으로 전환 → 이전 활성 문서 해제
        if (_activeTab is BrowserTabItem prevBrowser) DeactivateBrowserTab(prevBrowser);
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
            // _shownSessionSizes 는 여기서 추가하지 않는다 — 아직 실제로 터미널을 보여준 게 아니라
            // 스피너만 띄운 상태다. 잘못 마킹하면 나중에 진짜로 연결될 때 리플로우 감춤 커버가
            // "이미 이 폭으로 본 적 있음"으로 오판돼 스킵되고, 기본폭→패널폭 리플로우가 그대로
            // 노출돼 터미널이 화면 모서리에만 작게 뜨는 것처럼 보인다.
            if (coverReflow) RevealAfterTransition(kick: true);
            return;
        }

        // 숨김 graceful 종료 중이면 옛 프로세스에 붙지 않는다 — 종료 완료 후 재연결(OnHideStopFinished).
        // 종료는 owner 패널에서 돌므로(_gracefulStopRoomIds 는 그 패널 전용) 다른 패널의 재오픈은
        // 매니저의 전역 종료중 플래그로 판별한다 — 안 하면 죽어가는 세션에 재부착돼 종료 트랜스크립트가
        // 재생되고 "[세션 종료됨 — Enter로 재시작]" 죽은 방으로 굳는다(자동 resume 불가).
        if (_gracefulStopRoomIds.Contains(session.Id)
            || TerminalSessionManager.Instance.IsGracefulStopping(session.Id))
        {
            _pendingReactivateAfterHideStop.Add(session.Id);
            UpdateEmptyState();
            ShowSessionLoading(session.Id, HideStopLabel);
            EnsureSelectedTabVisible(session);
            RefreshModelEffortDock();
            ActiveChanged?.Invoke(this);
            if (coverReflow) RevealAfterTransition(kick: true);
            return;
        }

        session.IsAlive = true;
        // 콜드(미준비) 세션: UpdateEmptyState 가 UnparkTerminalHost 로 webview 를 0×0→풀사이즈로 드러내는데,
        // 그 전에 웹 로딩 커버부터 켜야 한다. ShowTerminal 을 먼저 보내면 빠른 codex 는 커서가 한 프레임
        // 노출된 뒤 loading 메시지를 받아 스피너가 뒤늦게 뜬다. 정확한 스피너 앵커는 UpdateEmptyState 로
        // 최종 크기 확정 후 재전송한다.
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
        // 콜드 세션은 loading ON 을 먼저 큐에 넣은 뒤 show 해야 커서/부팅 프레임이 커버 아래서 시작된다.
        // 터미널 호스트가 주차(브라우저/파일/빈 탭)돼 있다 복귀하는 경우엔 폭이 안 바뀌어 settle 바운스가
        // 스킵되지만, 주차 중 도착한 codex 출력이 스로틀돼 하단 입력영역이 어긋난 채 고착될 수 있다 →
        // reemit 로 재방출 1회를 강제해 자가치유(세션↔세션 전환은 주차가 없어 발동 안 함).
        // [실험] reemit 바운스가 codex 컴포저를 손상시킴이 로그로 확인됨 → 비활성화하고 baseline 확인.
        bool reemitOnShow = false;
        DiagLog.Write($"ActivateSession reemit-decide room={session.Id} termParked={_termParked} sessionReady={sessionReady} => reemit={reemitOnShow} (bounce disabled)");
        _terminal.ShowTerminal(session.Id, reemit: reemitOnShow);
        _terminal.FocusTerminal();
        UpdateEmptyState();
        // 로딩 표시는 UpdateEmptyState '뒤' — 세션 헤더바 등 표시로 콘텐츠 그리드 크기가 확정된 다음
        // 기대 크기를 캡처해야 웹 스피너 게이트(뷰포트=목표 일치 대기)의 목표가 처음부터 정확하다.
        // 테마 재시작 중인 방이면 그 안내 문구를 그대로 — 종료/재시작 어느 시점에 눌러도 동일하게 보인다.
        if (needGate) ShowSessionLoading(session.Id, _themeReloadRoomIds.Contains(session.Id) ? ThemeReloadLabel : null); // 커버 유지 — 준비된 세션은 RevealTerminalAfterGate 가 걷음
        else HideSessionLoading();
        EnsureSelectedTabVisible(session);
        RefreshModelEffortDock();
        ActiveChanged?.Invoke(this);
        RememberActiveSessionSize(TerminalHostContainer?.ActualWidth ?? 0);
        if (coverReflow) RevealAfterTransition(kick: true); // 최종 폭에서 세션 재동기 후 커버 걷기(리플로우 감춤)
    }

    private void ActivateExternalSession(SessionItem session, bool unHide)
    {
        if (ReferenceEquals(_activeSession, session))
        {
            UpdateEmptyState();
            EnsureSelectedTabVisible(session);
            RefreshModelEffortDock();
            ActiveChanged?.Invoke(this);
            return;
        }

        ClearIsolationIfMismatch(session);
        if (_activeSession != null) _activeSession.IsActive = false;
        var parent = ParentOf(session);
        if (parent == null) return;

        if (unHide && session.IsEffectivelyHidden)
        {
            foreach (var visible in parent.UnhideSessionPath(session)) CancelPendingHideStop(visible.Id);
            WorkspaceStore.Save(Projects);
        }
        if (!parent.IsExpanded) parent.IsExpanded = true;
        if (_activeTab is FileTabItem prevFile) prevFile.IsActive = false;
        if (_activeTab is BrowserTabItem prevBrowser) DeactivateBrowserTab(prevBrowser);
        _activeTab = session;
        _activeSession = session;
        session.IsActive = true;
        RecordActiveTab(parent, "S:" + session.Id);
        HideSessionLoading();
        UpdateEmptyState();
        EnsureSelectedTabVisible(session);
        RefreshModelEffortDock();
        ActiveChanged?.Invoke(this);
    }

    public async Task<byte[]?> CaptureSessionSnapshotPngAsync(SessionItem session)
    {
        if (!ReferenceEquals(_activeSession, session) || session.IsExternal) return null;
        var png = await _terminal.CapturePngAsync();
        return ReferenceEquals(_activeSession, session) ? png : null;
    }

    /// <summary>작업 큐 → 활성 세션 터미널에 텍스트 입력 + Enter. 비활성/죽은 세션이면 false.</summary>
    public bool SendTextToActiveSession(string text)
    {
        if (_activeSession is null or { IsExternal: true }) return false;
        var id = _activeSession.Id;
        var session = TerminalSessionManager.Instance.Get(id);
        if (session is not { IsAlive: true }) return false;
        session.Write(text);
        session.Write("\r");
        _terminal.ShowTerminal(id);
        _terminal.FocusTerminal();
        return true;
    }

    private void AttachFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is { IsExternal: true }) return;
        if (_activeSession == null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "첨부할 파일 선택",
            Multiselect = true,
            CheckFileExists = true,
        };
        var owner = Window.GetWindow(this);
        var selected = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (selected == true)
            _terminal.InsertFilePaths(dialog.FileNames);
    }

    private void ActivateFileTab(FileTabItem tab)
    {
        var parent = ParentOfTab(tab);
        if (parent == null) return;
        ClearIsolationIfMismatch(tab);

        if (_activeTab is FileTabItem prevFile) prevFile.IsActive = false; // 이전 활성 문서 하이라이트 해제
        if (_activeTab is BrowserTabItem prevBrowser) DeactivateBrowserTab(prevBrowser);
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

    /// <summary>브라우저 탭 비활성화. 세션 전용(자동화) 탭이면 화면 밖 주차장으로 되돌려
    /// 백그라운드에서도 페이지가 정상 크기로 렌더되게 한다(0×0 컨테이너에 남으면 스크립트 추출이 깨진다).</summary>
    private void DeactivateBrowserTab(BrowserTabItem tab)
    {
        tab.IsActive = false;
        if (tab.AutomationRoomId == null) return;
        MainWindow.Current?.ParkAutomationBrowser(tab.Browser);
    }

    private void ActivateBrowserTab(BrowserTabItem tab,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var parent = ParentOfTab(tab);
        if (parent == null) return;
        DiagLog.Write($"ActivateBrowserTab '{tab.Name}' id={tab.Id} caller={caller} termParkedBefore={_termParked}");
        ClearIsolationIfMismatch(tab);

        if (_activeTab is FileTabItem prevFile) prevFile.IsActive = false;
        // 같은 탭 재활성화면 재주차하지 않는다(불필요한 WebView2 재부모화 = 깜빡임).
        if (_activeTab is BrowserTabItem prevBrowser && !ReferenceEquals(prevBrowser, tab))
            DeactivateBrowserTab(prevBrowser);
        if (_activeSession != null) _activeSession.IsActive = false;

        _activeTab = tab;
        _activeSession = null;
        tab.IsActive = true;
        RecordActiveTab(parent, "B:" + tab.Id);

        HideSessionLoading();
        var browser = tab.Browser;
        browser.StateKey = tab.PersistenceKey;
        if (!ReferenceEquals(browser.Parent, BrowserHostContainer))
        {
            // 자동화 브라우저는 화면 밖 주차장(Panel)에 있을 수 있어 ContentControl 만 가정하면 안 된다.
            if (browser.Parent is ContentControl previousHost) previousHost.Content = null;
            else if (browser.Parent is Panel previousPark) previousPark.Children.Remove(browser);
            if (ReferenceEquals(BrowserHostContainer.Content, browser)) BrowserHostContainer.Content = null;
            BrowserHostContainer.Content = browser;
        }
        UpdateEmptyState();
        browser.EnsureStarted();
        browser.ResumeContent();
        EnsureSelectedTabVisible(tab);
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
        // md 편집기는 WebView2(별도 HWND)라 WPF DragOver 가 안 온다 — 웹에서 알려주는 드래그 진입으로
        // 세션과 동일한 드롭 선택 화면을 띄운다(에디터는 패널 간 공유 인스턴스라 표시 중인 패널만 반응).
        if (tab.Editor is MarkdownFileEditorView md)
        {
            md.ExternalFileDragEntered += () =>
            {
                if (ReferenceEquals(_activeTab, tab)) ShowFileDropOverlay();
            };
            md.ExternalFileDropReceived += () =>
            {
                if (ReferenceEquals(_activeTab, tab)) HideFileDropOverlay();
            };
        }
    }
    private readonly HashSet<IFileTabEditor> _interactHooked = new();

    // ── 메타바 model/effort dock ─────────────────────────────────
    private static readonly ModelEffortOption[] ClaudeModelOptions =
    {
        new("Opus", "opus"), new("Sonnet", "sonnet"),
        new("Haiku", "haiku"), new("Fable", "fable"),
    };
    private static readonly ModelEffortOption[] ClaudeEffortOptions =
    {
        new("low", "low"), new("medium", "medium"), new("high", "high"),
        new("xhigh", "xhigh"), new("max", "max"),
    };
    private const string DefaultModelValue = "opus";
    private const string DefaultEffortValue = "high";
    private static readonly object ClaudeDefaultsLock = new();
    private static DateTime _claudeDefaultsWriteUtc;
    private static (string? Model, string? Effort) _claudeDefaults;
    private static readonly object CodexSessionMetaLock = new();
    private static readonly Dictionary<string, (DateTime WriteUtc, long Length, string? Model, string? Effort)> CodexSessionMetaCache = new();

    private sealed record AgentModelOption(
        string Label, string Value, string? DefaultEffort, ModelEffortOption[] Efforts);

    private bool _suppressModelEffort;
    private readonly Dictionary<string, string> _pendingModel = new();
    private readonly Dictionary<string, string> _pendingEffort = new();
    private AgentModelOption[] _visibleAgentModels = Array.Empty<AgentModelOption>();
    private readonly System.Windows.Threading.DispatcherTimer _agentModelStateTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(750),
    };
    private string? _lastAgentModelStateSignature;

    // ── 메타바 터미널 폰트 크기 dock(claude 여부와 무관, 항상 노출) ─────────
    private static readonly ModelEffortOption[] FontSizeOptions =
    {
        new("10pt", "10"), new("11pt", "11"), new("12pt", "12"), new("13pt", "13"),
        new("14pt", "14"), new("16pt", "16"), new("18pt", "18"), new("20pt", "20"),
        new("24pt", "24"), new("28pt", "28"),
    };
    private const double PtToPxRatio = 96.0 / 72.0;
    private bool _suppressFontSize;

    private void DockCombo_DropDownClosed(object? sender, EventArgs e)
    {
        // ComboBox는 팝업이 닫혀도 키보드 포커스를 계속 가져 방향키로 값이 바뀐다.
        // 닫힘 처리가 끝난 다음 WPF 포커스를 비우고, 세션 탭이면 WebView2/xterm에 입력을 돌려준다.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Keyboard.ClearFocus();
            if (_activeSession != null) _terminal.FocusTerminal();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

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
        if (_activeSession is null or { IsExternal: true }) return;
        if (FontSizeCombo.SelectedValue is not string val || !int.TryParse(val, out var pt)) return;
        _terminal.SetRoomFontSizePt(_activeSession.Id, pt); // 지금 보고 있는 방에만 적용, 다른 방/새 방엔 영향 없음
    }

    private void RefreshExternalAgentModelStateIfChanged()
    {
        var session = _activeSession;
        if (session == null || session.IsBusy) return;
        var agentId = string.IsNullOrEmpty(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;
        if (agentId is not ("codex" or "grok")) return;
        var signature = BuildAgentModelStateSignature(session, agentId);
        if (signature == _lastAgentModelStateSignature) return;
        _lastAgentModelStateSignature = signature;
        RefreshModelEffortDock();
    }

    private string? _lastUsageKey;

    /// <summary>활성 세션(claude·codex)의 누적 토큰 사용량을 헤더 브랜치 오른쪽에 표시.
    /// 미지원 에이전트/데이터 없음이면 숨긴다. 파일 파싱은 백그라운드에서.</summary>
    private void RefreshUsageDock()
    {
        if (UsageGroup == null) return;
        var s = _activeSession;
        var agentId = s == null ? null : (string.IsNullOrEmpty(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId);
        if (s == null || agentId == null || !PaneHasAnySessionTab() || !SessionUsageService.IsSupported(agentId))
        {
            UsageGroup.Visibility = Visibility.Collapsed;
            _lastUsageKey = null;
            return;
        }
        _ = RefreshUsageAsync(s.Id, agentId, ParentOf(s)?.Path);
    }

    private async System.Threading.Tasks.Task RefreshUsageAsync(string roomId, string agentId, string? cwd)
    {
        var t = await System.Threading.Tasks.Task.Run(() => SessionUsageService.Read(roomId, agentId, cwd));
        if (_activeSession?.Id != roomId || UsageGroup == null) return; // 응답 사이 세션 전환됐으면 무시
        if (t is not { HasData: true } u)
        {
            UsageGroup.Visibility = Visibility.Collapsed;
            _lastUsageKey = null;
            return;
        }
        var inline = SessionUsageService.FormatInline(u);
        var key = roomId + "|" + inline;
        UsageGroup.Visibility = Visibility.Visible;
        if (key == _lastUsageKey) return; // 값 불변 → UI 재기록 생략
        _lastUsageKey = key;
        UsageText.Text = inline;
        UsageText.ToolTip = SessionUsageService.FormatTooltip(u);
    }

    private static string BuildAgentModelStateSignature(SessionItem session, string agentId)
    {
        var sessionId = agentId == "codex"
            ? SettingsService.LoadCodexRoomSession(session.Id)
            : SettingsService.LoadGrokRoomSession(session.Id);
        var transcript = agentId == "codex"
            ? TerminalSessionManager.FindCodexTranscriptPath(sessionId)
            : FindGrokSummaryPath(sessionId);
        var config = agentId == "codex"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "models_cache.json");
        return session.Id + "|" + sessionId + "|" + FileStamp(transcript) + "|" + FileStamp(config);
    }

    private static string FileStamp(string? path)
    {
        try
        {
            if (path == null) return "-";
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : "-";
        }
        catch { return "-"; }
    }

    private static string? FindGrokSummaryPath(string? sessionId)
    {
        var history = TerminalSessionManager.FindGrokChatHistoryPath(sessionId);
        if (history == null) return null;
        var path = Path.Combine(Path.GetDirectoryName(history)!, "summary.json");
        return File.Exists(path) ? path : null;
    }

    private void RefreshModelEffortDock()
    {
        RefreshHeaderSessionGate(); // 활성 세션이 아니면 터미널 폰트는 숨기고, 브랜치는 보이는 세션 탭 기준 갱신
        RefreshUsageDock();         // 활성 세션 토큰 사용량(입/출력/비용) 즉시 반영
        if (ModelEffortDock == null) return;
        var s = _activeSession;

        // 폰트 크기는 에이전트 종류와 무관하게 항상 동기화(방별 값, 없으면 전역 기본값).
        // 방마다 크기가 다를 수 있으므로 콤보·헤더 타이틀 모두 활성 방 크기에 맞춘다.
        if (s != null)
        {
            var roomPx = _terminal.RoomEffectiveFontSizePx(s.Id);
            SyncFontSizeCombo(roomPx);
            ApplyHeaderFontSize(roomPx);
        }

        var agentId = s == null ? null : (string.IsNullOrEmpty(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId);
        bool supportsSelection = s is { IsExternal: false } && agentId is ("claude" or "grok");
        ModelEffortDock.Visibility = supportsSelection ? Visibility.Visible : Visibility.Collapsed;
        bool isCodex = s is { IsExternal: false } && agentId == "codex";
        CodexModelEffortDock.Visibility = isCodex ? Visibility.Visible : Visibility.Collapsed;
        if (isCodex)
        {
            RefreshCodexModelEffortDisplay(s!);
            return;
        }
        if (!supportsSelection) return;

        _visibleAgentModels = LoadAgentModelOptions(agentId!);
        if (_visibleAgentModels.Length == 0)
        {
            ModelEffortDock.Visibility = Visibility.Collapsed;
            return;
        }

        string? liveModelId = null;
        string? liveEffort = null;
        if (agentId == "claude") (liveModelId, liveEffort) = ModelEffort?.Read(s!.Id) ?? (null, null);

        var savedModel = agentId == "claude"
            ? SettingsService.LoadClaudeCodeRoomModel(s!.Id)
            : SettingsService.LoadAgentRoomModel(s!.Id, agentId!);
        var savedEffort = agentId == "claude"
            ? SettingsService.LoadClaudeCodeRoomEffort(s!.Id)
            : SettingsService.LoadAgentRoomEffort(s!.Id, agentId!);
        var grokLive = agentId == "grok" ? LoadGrokSessionModelEffort(s!.Id) : (Model: (string?)null, Effort: (string?)null);
        if (agentId == "grok")
        {
            if (grokLive.Model != null && grokLive.Model != savedModel)
                SettingsService.SaveAgentRoomModel(s!.Id, "grok", grokLive.Model);
            if (grokLive.Effort != null && grokLive.Effort != savedEffort)
                SettingsService.SaveAgentRoomEffort(s!.Id, "grok", grokLive.Effort);
            else if (grokLive.Model != null && grokLive.Effort == null && savedEffort != null
                && _visibleAgentModels.FirstOrDefault(m => m.Value == grokLive.Model)?.Efforts.Length == 0)
                SettingsService.SaveAgentRoomEffort(s!.Id, "grok", null);
        }
        (string? Model, string? Effort) configuredClaude = agentId == "claude"
            ? LoadClaudeConfiguredDefaults()
            : (null, null);
        var model = agentId == "claude"
            ? ModelIdToValue(liveModelId) ?? savedModel ?? configuredClaude.Model
            : grokLive.Model ?? savedModel;
        model ??= agentId == "claude" ? DefaultModelValue : _visibleAgentModels[0].Value;
        var selectedModel = _visibleAgentModels.FirstOrDefault(m => m.Value == model) ?? _visibleAgentModels[0];
        var effort = (agentId == "claude" && IsKnownEffort(liveEffort) ? liveEffort : null)
            ?? grokLive.Effort
            ?? savedEffort
            ?? configuredClaude.Effort;
        if (effort != null && !selectedModel.Efforts.Any(e => e.Value == effort))
            effort = null;
        if (effort == null)
            effort = selectedModel.DefaultEffort ?? selectedModel.Efforts.FirstOrDefault()?.Value;

        _suppressModelEffort = true;
        try
        {
            ModelCombo.ItemsSource = _visibleAgentModels.Select(m => new ModelEffortOption(m.Label, m.Value)).ToArray();
            ModelCombo.SelectedValue = selectedModel.Value;
            EffortCombo.ItemsSource = selectedModel.Efforts;
            EffortCombo.Visibility = selectedModel.Efforts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Claude는 전체 버전 카탈로그 파일이 없으므로 settings.json의 현재 alias/effort만 읽는다.
    /// alias는 ModelIdToValue로 Opus/Sonnet 등 계열명으로만 표시한다. 파일이 그대로면 파싱도 생략.</summary>
    private static (string? Model, string? Effort) LoadClaudeConfiguredDefaults()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
            var writeUtc = File.GetLastWriteTimeUtc(path);
            lock (ClaudeDefaultsLock)
            {
                if (writeUtc == _claudeDefaultsWriteUtc) return _claudeDefaults;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var rawModel = root.TryGetProperty("model", out var modelNode) ? modelNode.GetString() : null;
                var effort = root.TryGetProperty("effortLevel", out var effortNode) ? effortNode.GetString() : null;
                _claudeDefaults = (ModelIdToValue(rawModel), IsKnownEffort(effort) ? effort : null);
                _claudeDefaultsWriteUtc = writeUtc;
                return _claudeDefaults;
            }
        }
        catch { return _claudeDefaults; }
    }

    private void RefreshCodexModelEffortDisplay(SessionItem session)
    {
        var configured = LoadCodexConfiguredDefaults(SettingsService.LoadClaudeCodeRoomDir(session.Id));
        var sessionMeta = LoadCodexSessionModelEffort(session.Id);
        var model = sessionMeta.Model ?? configured.Model;
        var effort = sessionMeta.Effort ?? configured.Effort;
        CodexModelText.Text = CodexModelDisplayName(model);
        CodexEffortText.Text = effort ?? "—";
        CodexEffortValueBorder.Visibility = string.IsNullOrWhiteSpace(effort)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>Codex rollout의 thread_settings_applied/turn_context에 실제 모델/effort가 함께 기록된다.
    /// 파일이 바뀌지 않았으면 캐시를 반환하고, 읽을 때는 활성 세션과 충돌하지 않게 공유 읽기한다.</summary>
    private static (string? Model, string? Effort) LoadCodexSessionModelEffort(string roomId)
    {
        try
        {
            var sessionId = SettingsService.LoadCodexRoomSession(roomId);
            var path = TerminalSessionManager.FindCodexTranscriptPath(sessionId);
            if (path == null) return (null, null);
            var info = new FileInfo(path);
            lock (CodexSessionMetaLock)
            {
                if (CodexSessionMetaCache.TryGetValue(path, out var cached)
                    && cached.WriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
                    return (cached.Model, cached.Effort);
            }

            var (model, effort) = ReadLatestCodexTurnContext(path);
            lock (CodexSessionMetaLock)
                CodexSessionMetaCache[path] = (info.LastWriteTimeUtc, info.Length, model, effort);
            return (model, effort);
        }
        catch { return (null, null); }
    }

    /// <summary>파일 끝 64KB부터 역방향으로 넓혀 최신 모델 상태 이벤트 하나만 찾는다.
    /// 큰 rollout 전체를 매 턴 파싱하지 않아 읽기 전용 표시 갱신이 UI를 막지 않는다.</summary>
    private static (string? Model, string? Effort) ReadLatestCodexTurnContext(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = fs.Length;
        if (length == 0) return (null, null);
        long window = Math.Min(length, 64 * 1024L);
        while (true)
        {
            var start = length - window;
            fs.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[checked((int)window)];
            int read = 0;
            while (read < bytes.Length)
            {
                var n = fs.Read(bytes, read, bytes.Length - read);
                if (n == 0) break;
                read += n;
            }
            var text = System.Text.Encoding.UTF8.GetString(bytes, 0, read);
            var lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                // 중간 바이트에서 시작한 첫 줄은 불완전할 수 있으므로 다음 확장 구간에서 처리한다.
                if (start > 0 && i == 0) continue;
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0 || (!line.Contains("\"turn_context\"", StringComparison.Ordinal)
                    && !line.Contains("\"thread_settings_applied\"", StringComparison.Ordinal))) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("payload", out var payload)) continue;
                    if (root.TryGetProperty("type", out var type) && type.GetString() == "turn_context")
                    {
                        var model = payload.TryGetProperty("model", out var modelNode) ? modelNode.GetString() : null;
                        var effort = payload.TryGetProperty("effort", out var effortNode) ? effortNode.GetString() : null;
                        return (model, effort);
                    }
                    if (payload.TryGetProperty("type", out var eventType)
                        && eventType.GetString() == "thread_settings_applied"
                        && payload.TryGetProperty("thread_settings", out var settings))
                    {
                        var model = settings.TryGetProperty("model", out var modelNode) ? modelNode.GetString() : null;
                        var effort = settings.TryGetProperty("reasoning_effort", out var effortNode) ? effortNode.GetString() : null;
                        return (model, effort);
                    }
                }
                catch { /* append 중인 마지막 불완전 라인은 건너뜀 */ }
            }
            if (start == 0) return (null, null);
            window = Math.Min(length, window * 4);
        }
    }

    private static (string? Model, string? Effort) LoadCodexConfiguredDefaults(string? projectDir)
    {
        string? model = null;
        string? effort = null;
        void Read(string path)
        {
            try
            {
                var text = File.ReadAllText(path);
                var modelMatch = System.Text.RegularExpressions.Regex.Match(text,
                    @"(?m)^\s*model\s*=\s*[\""']?([^\""'\s#]+)");
                var effortMatch = System.Text.RegularExpressions.Regex.Match(text,
                    @"(?m)^\s*model_reasoning_effort\s*=\s*[\""']?([^\""'\s#]+)");
                if (modelMatch.Success) model = modelMatch.Groups[1].Value;
                if (effortMatch.Success) effort = effortMatch.Groups[1].Value;
            }
            catch { /* 파일 없음/교체 중이면 상위 설정 유지 */ }
        }

        Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml"));
        if (!string.IsNullOrWhiteSpace(projectDir)) Read(Path.Combine(projectDir, ".codex", "config.toml"));
        return (model, effort);
    }

    private static string CodexModelDisplayName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "—";
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "models_cache.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var item in models.EnumerateArray())
                {
                    if (!item.TryGetProperty("slug", out var slug) || slug.GetString() != model) continue;
                    if (item.TryGetProperty("display_name", out var display) && display.GetString() is { Length: > 0 } label)
                        return label;
                }
            }
        }
        catch { /* 캐시가 없으면 raw model ID 표시 */ }
        return model;
    }

    private static AgentModelOption[] LoadAgentModelOptions(string agentId)
    {
        if (agentId == "claude")
            return ClaudeModelOptions
                .Select(m => new AgentModelOption(m.Label, m.Value, DefaultEffortValue, ClaudeEffortOptions))
                .ToArray();

        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "models_cache.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var loaded = ReadGrokModels(doc.RootElement);
            if (loaded.Length > 0) return loaded;
            throw new InvalidDataException("모델 캐시에 표시 가능한 모델이 없습니다.");
        }
        catch
        {
            // CLI를 아직 한 번도 실행하지 않아 캐시가 없거나, 업데이트 중 파일이 교체된 순간이면 안전한 기본 목록.
            return new[]
                {
                    new AgentModelOption("Grok 4.5", "grok-4.5", "high", ReasoningOptions("low", "medium", "high")),
                    new AgentModelOption("Composer 2.5", "grok-composer-2.5-fast", null, Array.Empty<ModelEffortOption>()),
                };
        }
    }

    private static (string? Model, string? Effort) LoadGrokSessionModelEffort(string roomId)
    {
        try
        {
            var sessionId = SettingsService.LoadGrokRoomSession(roomId);
            var path = FindGrokSummaryPath(sessionId);
            if (path == null) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var model = root.TryGetProperty("current_model_id", out var modelNode) ? modelNode.GetString() : null;
            var effort = root.TryGetProperty("reasoning_effort", out var effortNode) ? effortNode.GetString() : null;
            return (model, effort);
        }
        catch { return (null, null); }
    }

    private static AgentModelOption[] ReadGrokModels(JsonElement root)
    {
        var result = new List<AgentModelOption>();
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object) return result.ToArray();
        foreach (var property in models.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("info", out var info)) continue;
            if (info.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            var id = info.TryGetProperty("id", out var idNode) ? idNode.GetString() : property.Name;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var label = info.TryGetProperty("name", out var labelNode) ? labelNode.GetString() : null;
            var defaultEffort = info.TryGetProperty("reasoning_effort", out var defaultNode) ? defaultNode.GetString() : null;
            var efforts = new List<ModelEffortOption>();
            if (info.TryGetProperty("reasoning_efforts", out var levels) && levels.ValueKind == JsonValueKind.Array)
            {
                foreach (var level in levels.EnumerateArray())
                    if (level.TryGetProperty("value", out var effortNode) && effortNode.GetString() is { Length: > 0 } effort)
                        efforts.Add(new ModelEffortOption(effort, effort));
            }
            result.Add(new AgentModelOption(label ?? id!, id!, defaultEffort, efforts.ToArray()));
        }
        return result.ToArray();
    }

    private static ModelEffortOption[] ReasoningOptions(params string[] values)
        => values.Select(v => new ModelEffortOption(v, v)).ToArray();

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

        var agentId = string.IsNullOrEmpty(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId;
        if (agentId is not ("claude" or "grok")) return;

        if (isModel)
        {
            SaveRoomModel(s.Id, agentId, val);
            var selectedModel = _visibleAgentModels.FirstOrDefault(m => m.Value == val);
            if (selectedModel != null)
            {
                // 현재 콤보에는 Claude 훅의 라이브 effort까지 반영돼 있으므로 저장값보다 우선 보존한다.
                var oldEffort = EffortCombo.SelectedValue as string
                    ?? (agentId == "claude"
                        ? SettingsService.LoadClaudeCodeRoomEffort(s.Id)
                        : SettingsService.LoadAgentRoomEffort(s.Id, agentId));
                var effectiveEffort = selectedModel.Efforts.Any(x => x.Value == oldEffort)
                    ? oldEffort
                    : selectedModel.DefaultEffort ?? selectedModel.Efforts.FirstOrDefault()?.Value;
                SaveRoomEffort(s.Id, agentId, effectiveEffort);
                _suppressModelEffort = true;
                try
                {
                    EffortCombo.ItemsSource = selectedModel.Efforts;
                    EffortCombo.Visibility = selectedModel.Efforts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                    EffortCombo.SelectedValue = effectiveEffort;
                }
                finally { _suppressModelEffort = false; }
            }
        }
        else
        {
            // 저장값이 없어서 config/캐시 기본 모델을 표시 중이어도 effort 변경 시 그 모델을 함께 고정해야
            // 런처가 모델별 지원 여부를 검증하고 같은 조합으로 재개할 수 있다.
            if (agentId != "claude" && ModelCombo.SelectedValue is string visibleModel)
                SaveRoomModel(s.Id, agentId, visibleModel);
            SaveRoomEffort(s.Id, agentId, val);
        }

        if (s.IsBusy)
            (isModel ? _pendingModel : _pendingEffort)[s.Id] = val;
        else if (agentId == "grok")
        {
            SendGrokModelEffortSlash(s.Id, isModel, val);
        }
        else SendModelEffortSlash(s.Id, isModel, val);
    }

    /// <summary>응답 종료(busy→idle) 시 보류된 model/effort 변경을 라이브 주입. 셸이 호출.</summary>
    public void FlushPendingModelEffort(string roomId)
    {
        var session = FindSession(roomId);
        var agentId = session == null || string.IsNullOrEmpty(session.AgentId)
            ? AgentRegistry.DefaultAgentId
            : session.AgentId;
        bool grokModelIncludedEffort = false;
        if (_pendingModel.Remove(roomId, out var m))
        {
            if (agentId == "grok")
            {
                SendGrokModelEffortSlash(roomId, isModel: true, m);
                grokModelIncludedEffort = true;
            }
            else SendModelEffortSlash(roomId, isModel: true, m);
        }
        if (_pendingEffort.Remove(roomId, out var ef))
        {
            if (agentId == "grok")
            {
                if (!grokModelIncludedEffort) SendGrokModelEffortSlash(roomId, isModel: false, ef);
            }
            else SendModelEffortSlash(roomId, isModel: false, ef);
        }
    }

    /// <summary>세션 신규/재개 직전 로컬 모델 설정을 다시 반영한다. 다른 에이전트의 활성 패널은 건드리지 않는다.</summary>
    public void NotifyAgentModelCatalogRefreshRequested(string agentId)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var s = _activeSession;
            if (s == null) return;
            var activeAgent = string.IsNullOrEmpty(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId;
            if (activeAgent == agentId) RefreshModelEffortDock();
        });
    }

    private static void SaveRoomModel(string roomId, string agentId, string? value)
    {
        if (agentId == "claude") SettingsService.SaveClaudeCodeRoomModel(roomId, value);
        else SettingsService.SaveAgentRoomModel(roomId, agentId, value);
    }

    private static void SaveRoomEffort(string roomId, string agentId, string? value)
    {
        if (agentId == "claude") SettingsService.SaveClaudeCodeRoomEffort(roomId, value);
        else SettingsService.SaveAgentRoomEffort(roomId, agentId, value);
    }

    private void SendModelEffortSlash(string roomId, bool isModel, string value)
    {
        var sess = TerminalSessionManager.Instance.Get(roomId);
        if (sess is not { IsAlive: true }) return;
        _terminal.SuppressScroll(5);
        sess.Write((isModel ? "/model " : "/effort ") + value + "\r");
    }

    /// <summary>Grok은 현재 세션에서 모델과 effort를 직접 바꿀 수 있다. 모델 변경 시 저장된 effort를
    /// 두 번째 인자로 함께 보내 모델 선택 직후 별도 재실행이나 추가 명령이 필요 없게 한다.</summary>
    private void SendGrokModelEffortSlash(string roomId, bool isModel, string value)
    {
        var sess = TerminalSessionManager.Instance.Get(roomId);
        if (sess is not { IsAlive: true }) return;
        string command;
        if (isModel)
        {
            var effort = SettingsService.LoadAgentRoomEffort(roomId, "grok");
            command = "/model " + value + (string.IsNullOrEmpty(effort) ? "" : " " + effort);
        }
        else command = "/effort " + value;
        _terminal.SuppressScroll(5);
        sess.Write(command + "\r");
    }

    private void NewTabBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject == null)
        {
            ConfirmDialog.Alert("프로젝트 없음", "먼저 왼쪽 사이드바에서 프로젝트를 추가하세요.");
            return;
        }
        if (NewTabBtn.ContextMenu is not { } menu) return;
        menu.PlacementTarget = NewTabBtn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void NewSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject != null) AddSession(_activeProject);
    }

    private void NewBrowserMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject != null) AddBrowserTab(_activeProject);
    }

    private void OpenFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject == null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "텍스트, 이미지, PDF 열기",
            Filter = "지원 파일|*.pdf;*.txt;*.md;*.markdown;*.json;*.xml;*.yml;*.yaml;*.toml;*.csv;*.log;*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.ico;*.tif;*.tiff|PDF 파일|*.pdf|텍스트 파일|*.txt;*.md;*.markdown;*.json;*.xml;*.yml;*.yaml;*.toml;*.csv;*.log|이미지 파일|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.ico;*.tif;*.tiff",
            FilterIndex = 1,
        };
        if (Directory.Exists(_activeProject.Path))
            dialog.InitialDirectory = _activeProject.Path;
        if (dialog.ShowDialog() == true)
            OpenFileAsTab(dialog.FileName);
    }

    public BrowserTabItem? AddBrowserTab(ProjectItem proj, string? initialName = null, bool promptForName = true,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var name = initialName ?? NextBrowserName(proj);
        if (promptForName && SettingsService.LoadPromptForNewBrowserTabName())
        {
            var enteredName = PromptDialog.Show("새 브라우저 탭 이름", "새 이름을 입력하세요.",
                                                defaultValue: name, maxLength: 60);
            if (enteredName == null) return null;
            name = enteredName;
        }

        var tab = new BrowserTabItem { Name = name };
        DiagLog.Write($"AddBrowserTab id={tab.Id} caller={caller} project={proj.Name}");
        proj.Tabs.Add(tab);
        proj.IsExpanded = true;

        bool isolated = _isolatedTabs != null && ReferenceEquals(_activeProject, proj);
        if (isolated) IsolateTab(tab);
        if (!ReferenceEquals(_activeProject, proj)) SetActiveProject(proj);
        ActivateBrowserTab(tab);
        WorkspaceStore.Save(Projects);
        if (isolated) IsolatedTabOpened?.Invoke(this, tab);
        return tab;
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
        if (_activeTab is BrowserTabItem prevBrowser) DeactivateBrowserTab(prevBrowser);
        _activeSession = null;
        _activeTab = null; // SelectedTab DP=null → 이 패널 탭바 선택 강조 해제
        if (FileEditorHostContainer != null) FileEditorHostContainer.Content = null;
        HideSessionLoading();
        UpdateEmptyState();
        UpdateSelectedTabSeam();
        RefreshModelEffortDock(); // 활성 세션 없음 → 모델/effort 숨김 + 내부에서 폰트/브랜치 게이트도 재평가
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

    public void RenameBrowserTab(BrowserTabItem browser)
    {
        var name = PromptDialog.Show("탭 이름 변경", "새 이름을 입력하세요.",
                                     defaultValue: browser.Name, maxLength: 60);
        if (string.IsNullOrWhiteSpace(name) || name == browser.Name) return;
        browser.Name = name;
        WorkspaceStore.Save(Projects);
    }

    public void DeleteSession(SessionItem session)
        => DeleteSessionRequested?.Invoke(session);

    /// <summary>사이드바 컨텍스트 메뉴 "세션 숨기기" / 탭 X — 탭에서 숨기고,
    /// 이미 띄워 둔 ConPTY 는 <see cref="HideStopDelayMs"/> 유예 후 graceful 종료한다.
    /// 유예 안에 다시 열면 종료를 취소해 프로세스·대화 상태를 그대로 복구한다.
    /// 유예 후에는 transcript·훅 flush 후 프로세스만 내리고 추적 파일은 보존(다시 열면 resume).</summary>
    public void HideSession(SessionItem session)
        => HideSessionRequested?.Invoke(session);

    /// <summary>셸이 서브트리 숨김 상태를 적용한 뒤 각 패널의 활성 탭을 표시 가능한 이웃으로 교체.</summary>
    public void OnSessionsHidden(IReadOnlyCollection<SessionItem> hiddenSessions)
    {
        if (_activeSession != null && hiddenSessions.Contains(_activeSession))
            ActivateNeighborAfterHide(_activeSession);
    }

    /// <summary>셸이 선택한 실제 소유 패널에서 세션 하나의 지연 종료를 예약.</summary>
    public void ScheduleSessionHide(SessionItem session) => ScheduleGracefulStopAfterHide(session);

    /// <summary>조상 숨김 해제 등으로 다시 표시된 세션의 지연 종료 예약 취소.</summary>
    public void CancelSessionHide(string roomId) => CancelPendingHideStop(roomId);

    /// <summary>이 패널 WebView 에 남아 있는 방(xterm)만 정리한다 — ConPTY 프로세스는 건드리지 않는다.
    /// 숨김 graceful 종료가 owner 패널 방만 닫으므로, 반대 패널에 남을 죽은 방(재부착 시
    /// "[세션 종료됨 — Enter로 재시작]" 상태로 열려 자동 resume 불가) 정리용. 방이 없으면 no-op.</summary>
    public void CloseTerminalRoom(string roomId)
    {
        try { _terminal.CloseTerminal(roomId); } catch { /* ignore */ }
    }

    /// <summary>
    /// 외부 인계된 세션의 내부 배선을 끊되 현재 xterm 버퍼는 보존한다.
    /// 버퍼가 없는 재시작 복원 경로에서만 저장된 스냅샷을 사용한다.
    /// </summary>
    public async Task SetSessionExternalAsync(SessionItem session)
    {
        // 라이브 미러 보존 없이 항상 정지 스냅샷으로 전환한다. 내부 방은 외부 프록시가 소유하므로 정리.
        // 오버레이 첫 표시부터 '여는 중' 상태로 — 그래야 실행중(버튼) 패널이 한 프레임 깜빡이지 않는다.
        _externalReady = false;
        if (ReferenceEquals(_activeSession, session))
        {
            LoadExternalSessionPreview(session);
            await WaitForFramesAsync(2);
        }
        CloseTerminalRoom(session.Id);
        if (ReferenceEquals(_activeSession, session))
            UpdateEmptyState();
    }

    /// <summary>외부 lock 해제 후, 이 패널이 해당 세션을 보고 있었다면 보존 버퍼에 같은 방을 내부 resume 한다.</summary>
    public void OnExternalSessionEnded(SessionItem session)
    {
        bool active = ReferenceEquals(_activeSession, session);
        _terminal.EndExternalPreview(session.Id, reconnect: active);
        if (!active) return;
        HideExternalSessionPreview();
        session.IsActive = false;
        _activeSession = null;
        _activeTab = null;
        if (!session.IsEffectivelyHidden)
            ActivateSession(session, unHide: false);
        else
            ClearActiveSession();
    }

    /// <summary>메인 타이머가 호출한다. 이 패널에 외부 xterm이 있으면 독립 offset으로 새 출력을 반영한다.</summary>
    // 정지 스냅샷 모드에서는 라이브 출력 펌핑을 하지 않는다(내부는 블러 배경만 표시).
    public void PumpExternalSessionOutput(SessionItem session) { }

    // 미러 재생을 안 하므로 항상 "따라잡음" — 외부 종료 시 복귀가 막히지 않는다(복귀는 CLI 세션 resume).
    public bool IsExternalSessionOutputCaughtUp(SessionItem session) => true;

    /// <summary>숨김 graceful 종료가 완료됨(이 패널에서 종료가 실행됨) — 셸이 모든 패널로 중계한다.</summary>
    public event Action<SessionItem>? HideStopFinished;

    /// <summary>숨김 graceful 종료 완료 통지(어느 패널에서 돌았든). 종료 중 이 패널에 생긴 죽은/빈 방을
    /// 정리해 다음 표시가 항상 "방 없음 → 새로 생성(resume)" 경로를 타게 하고, 종료 중 이 세션을 열어
    /// 대기했다면(_pendingReactivateAfterHideStop) 이제 안전하게 새 세션으로 재연결한다.</summary>
    public void OnHideStopFinished(SessionItem session)
    {
        var roomId = session.Id;
        bool pending = _pendingReactivateAfterHideStop.Remove(roomId);
        if (TerminalSessionManager.Instance.Get(roomId) is not { IsAlive: true })
            CloseTerminalRoom(roomId);
        if (!pending || session.IsEffectivelyHidden || !ReferenceEquals(_activeSession, session)) return;
        _activeSession.IsActive = false;
        _activeSession = null;
        _activeTab = null;
        ActivateSession(session, unHide: false);
    }

    /// <summary>숨김 세션의 지연 종료 예약을 취소한다(다시 열기·삭제·프로젝트 제거 공통).</summary>
    private void CancelPendingHideStop(string roomId)
    {
        if (!_pendingHideStopCts.Remove(roomId, out var cts)) return;
        try { cts.Cancel(); } catch { /* ignore */ }
        try { cts.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>실행 중이면 3초 뒤 graceful 종료를 예약. 미기동은 no-op. 이미 예약이 있으면 재시작한다.</summary>
    private void ScheduleGracefulStopAfterHide(SessionItem session)
    {
        CancelPendingHideStop(session.Id);
        if (session.IsExternal) return;
        if (TerminalSessionManager.Instance.Get(session.Id) is not { IsAlive: true })
            return;

        var cts = new CancellationTokenSource();
        _pendingHideStopCts[session.Id] = cts;
        _ = GracefullyStopHiddenSessionAsync(session, cts);
    }

    /// <summary>유예 대기 후 숨긴 세션 프로세스를 graceful 종료. 유예 중 취소되면 프로세스 유지.
    /// CloseTerminal 로 핸들러를 먼저 떼 자동 재진입(codex/opencode)이 오발동하지 않게 한 뒤,
    /// Enter 없는 에이전트별 종료 제어키로 transcript 를 flush 하고 Dispose 한다. 추적 파일은 보존.</summary>
    private async Task GracefullyStopHiddenSessionAsync(SessionItem session, CancellationTokenSource cts)
    {
        var roomId = session.Id;
        bool notifiedStopFinished = false;
        try
        {
            try
            {
                await Task.Delay(HideStopDelayMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // 유예 안에 다시 열림 — 프로세스 그대로
            }

            // 예약이 교체됐으면(다시 숨김 등) 이 인스턴스는 폐기.
            if (!_pendingHideStopCts.TryGetValue(roomId, out var current) || !ReferenceEquals(current, cts))
                return;
            _pendingHideStopCts.Remove(roomId);
            try { cts.Dispose(); } catch { /* ignore */ }

            // 유예 동안 다시 열렸으면 죽이지 않는다.
            if (!session.IsEffectivelyHidden) return;
            if (TerminalSessionManager.Instance.Get(roomId) is not { IsAlive: true })
                return;

            // 실제 종료 시작 — UI 는 죽은 상태, 종료 중 재오픈은 완료 후 resume.
            session.IsAlive = false;
            session.IsBusy = false;
            session.IsWaitingChoice = false;
            _gracefulStopRoomIds.Add(roomId);
            try
            {
                try { _terminal.CloseTerminal(roomId); } catch { /* ignore */ }
                try
                {
                    await TerminalSessionManager.Instance.GracefulDisposeRoomsAsync(new[] { roomId });
                }
                catch { /* best effort */ }

                // dispose 플래그 해제 — 사이드바에서 다시 열 때 WireSession 이 막히지 않게.
                try { TerminalSessionManager.Instance.ClearDisposedRoom(roomId); } catch { /* ignore */ }

                if (session.IsEffectivelyHidden)
                {
                    session.IsAlive = false;
                    session.IsBusy = false;
                    session.IsWaitingChoice = false;
                }
            }
            finally
            {
                _gracefulStopRoomIds.Remove(roomId);
                NotifyStopFinished();
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write($"GracefullyStopHiddenSessionAsync room={roomId} err={ex.Message}");
            _gracefulStopRoomIds.Remove(roomId);
            if (_pendingHideStopCts.TryGetValue(roomId, out var cur) && ReferenceEquals(cur, cts))
            {
                _pendingHideStopCts.Remove(roomId);
                try { cts.Dispose(); } catch { /* ignore */ }
            }
            NotifyStopFinished();
        }

        // 종료 완료(성공/실패 공통, 1회) — 셸이 모든 패널로 중계해 죽은/빈 방을 정리하고,
        // 종료 중 이 세션을 열어 대기하던 패널(반대 패널 포함)을 resume 재연결한다(OnHideStopFinished).
        void NotifyStopFinished()
        {
            if (notifiedStopFinished) return;
            notifiedStopFinished = true;
            if (HideStopFinished != null) HideStopFinished(session);
            else OnHideStopFinished(session);
        }
    }

    public void StopTrackingSession(SessionItem session)
        => StopTrackingSessionRequested?.Invoke(session);

    /// <summary>닫힌 탭(원래 idx) 기준 왼쪽 우선, 없으면 오른쪽에서 표시 가능한 탭 선택.
    /// FilterTab 을 함께 봐 "이 패널에 실제로 보이는" 탭만 고른다 — 분할 시 반대쪽 패널로 넘긴(이 패널에선
    /// 숨겨진/격리 밖) 탭이 선택돼 엉뚱하게 딸려오는 것을 막는다. 비분할이면 필터가 항상 통과라 기존 동작 유지.</summary>
    private TabItemBase? PickNeighborTab(ProjectItem? parent, int removedIdx)
    {
        if (parent == null || removedIdx < 0) return null;
        bool Visible(TabItemBase t) => !(t is SessionItem s && s.IsEffectivelyHidden) && FilterTab(t);
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
        else if (next is BrowserTabItem nb) ActivateBrowserTab(nb);
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
            else if (next is BrowserTabItem b) ActivateBrowserTab(b);
            else ClearActiveSession();
        }
    }

    /// <summary>셸이 세션 서브트리를 컬렉션에서 제거한 뒤 활성 참조와 이웃 선택을 정리.</summary>
    public void OnSessionsRemoved(ProjectItem parent, IReadOnlyCollection<SessionItem> removed, int removedIndex)
    {
        if (_activeSession == null || !removed.Contains(_activeSession)) return;
        _activeSession.IsActive = false;
        _activeSession = null;
        _activeTab = null;
        var next = PickNeighborTab(parent, removedIndex);
        if (next is SessionItem s) ActivateSession(s, unHide: false);
        else if (next is FileTabItem f) ActivateFileTab(f);
        else if (next is BrowserTabItem b) ActivateBrowserTab(b);
        else ClearActiveSession();
    }

    public void CloseBrowserTab(BrowserTabItem tab) => RemoveBrowserTab(tab);

    private void RequestCloseBrowserTab(BrowserTabItem tab)
    {
        if (BrowserTabCloseRequested != null) BrowserTabCloseRequested(tab);
        else RemoveBrowserTab(tab);
    }

    private void RemoveBrowserTab(BrowserTabItem tab)
    {
        var parent = ParentOfTab(tab);
        bool wasActive = ReferenceEquals(_activeTab, tab);
        int idx = parent?.Tabs.IndexOf(tab) ?? -1;
        if (ReferenceEquals(BrowserHostContainer.Content, tab.Browser))
            BrowserHostContainer.Content = null;
        MainWindow.Current?.UnparkAutomationBrowser(tab.Browser);   // 주차장에 있던 자동화 탭 정리
        tab.Browser.DisposeAll();
        SettingsService.RemoveBrowserLastUrl(tab.PersistenceKey);
        parent?.Tabs.Remove(tab);
        PersistWorkspace();

        if (wasActive)
        {
            var next = PickNeighborTab(parent, idx);
            if (next is SessionItem s) ActivateSession(s);
            else if (next is FileTabItem f) ActivateFileTab(f);
            else if (next is BrowserTabItem b) ActivateBrowserTab(b);
            else ClearActiveSession();
        }
    }

    /// <summary>세션의 터미널 프로세스·매핑 정리(컬렉션은 건드리지 않음). 셸의 DeleteProject 도 호출.</summary>
    public void DisposeSessionProcess(SessionItem session, bool purge = true)
    {
        CancelPendingHideStop(session.Id); // 숨김 유예 종료 예약 취소(삭제/프로젝트 제거와 레이스 방지)
        var workingDir = SettingsService.LoadClaudeCodeRoomDir(session.Id);
        try { _terminal.CloseTerminal(session.Id); } catch { /* ignore */ }
        try
        {
            if (purge) TerminalSessionManager.Instance.PurgeRoom(session.Id, workingDir);
            else TerminalSessionManager.Instance.DisposeRoom(session.Id, purgeTracking: false);
        }
        catch { /* ignore */ }
        if (purge)
        {
            SettingsService.RemoveClaudeCodeRoomDir(session.Id);
            SessionUsageService.Remove(session.Id); // 세션 삭제 시 토큰 집계 캐시·영속 항목도 정리(고아 방지)
        }
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
            .Where(s => !s.IsExternal)
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

        // 하드킬(DisposeRoom) 대신 Enter 없는 종료 제어키로 claude 가 transcript 를
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

    /// <summary>테마 전역 재시작 1단계. 이 패널의 xterm/이벤트 배선만 모두 끊는다.
    /// 실제 ConPTY 종료는 MainWindow가 전역에서 한 번만 수행한다.</summary>
    public void BeginThemeReload(IReadOnlyList<SessionItem> allSessions)
    {
        // 이 목록의 방들이 전부 같이 종료된다 — 정리가 끝나기 전에 다른 탭을 눌러도 ActivateSession의
        // 콜드 게이트가 같은 안내 문구를 보여주고, 완료 후 현재 보이는 방만 열 수 있게 추적한다.
        foreach (var s in allSessions) _themeReloadRoomIds.Add(s.Id);

        // 숨겨진 PaneB도 예전 분할의 배선을 보존할 수 있어 전부 detach 한다. 다만 안내 스피너는
        // 실제 화면에 보이는 패널의 활성 세션에만 표시한다.
        try
        {
            if (Visibility == Visibility.Visible && _activeSession != null)
                ShowSessionLoading(_activeSession.Id, ThemeReloadLabel);
        }
        catch { /* 배선 해제는 계속 */ }

        foreach (var s in allSessions)
        {
            try { _terminal.CloseTerminal(s.Id); } catch { /* best effort */ }
        }
    }

    /// <summary>테마 전역 재시작 2단계. 실제로 보이는 이 패널의 현재 활성 세션만 다시 연다.
    /// 비활성 탭·다른 프로젝트·숨겨진 PaneB 세션은 dormant로 두고 사용자가 클릭할 때 resume 한다.</summary>
    public void CompleteThemeReload(IReadOnlyList<SessionItem> allSessions)
    {
        foreach (var s in allSessions)
        {
            _themeReloadRoomIds.Remove(s.Id);
            _pendingReactivateAfterReload.Remove(s.Id);
        }

        var active = _activeSession;
        var parent = active == null ? null : ParentOf(active);
        bool shouldRestart = Visibility == Visibility.Visible
            && active != null
            && parent != null
            && ReferenceEquals(_activeProject, parent)
            && ReferenceEquals(_activeTab, active)
            && parent.Tabs.Contains(active)
            && !active.IsEffectivelyHidden;

        if (!shouldRestart)
        {
            if (_loadingRoomId != null && allSessions.Any(s => s.Id == _loadingRoomId))
                HideSessionLoading();
            return;
        }

        // 동일 세션 가드 우회: 기존 ConPTY는 종료됐으므로 현재 화면의 방만 새 ConPTY로 생성해 resume 한다.
        var visibleActive = active!;
        visibleActive.IsActive = false;
        _activeSession = null;
        _activeTab = null;
        ActivateSession(visibleActive);
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
            // 이미 선택된 세션 탭도 다시 누르면 터미널에 실제 포커스를 재진입시킨다. 기존에는
            // ActivateSession의 same-session 조기 반환으로 아무 일도 없어 IME 이상 상태를 복구할 수 없었다.
            if (tab is SessionItem s)
            {
                if (ReferenceEquals(_activeSession, s)) _terminal.FocusTerminal();
                else OpenSession(s);
            }
            else if (tab is FileTabItem f) ActivateFileTab(f);
            else if (tab is BrowserTabItem b) ActivateBrowserTab(b);
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
        else if (tab is BrowserTabItem browser)
        {
            var openExternalItem = new MenuItem { Header = "기본 브라우저로 열기", Icon = BuildMenuIcon("IconExternalLink") };
            openExternalItem.Click += (_, _) => OpenBrowserInDefaultBrowser(browser);
            cm.Items.Add(openExternalItem);

            var copyUrlItem = new MenuItem { Header = "현재 URL 복사", Icon = BuildMenuIcon("IconCopy") };
            copyUrlItem.Click += (_, _) => CopyBrowserUrl(browser);
            cm.Items.Add(copyUrlItem);

            var renameItem = new MenuItem { Header = "이름 변경", Icon = BuildMenuIcon("IconPencil") };
            renameItem.Click += (_, _) => RenameBrowserTab(browser);
            cm.Items.Add(renameItem);
            cm.Items.Add(new Separator());

            var closeItem = new MenuItem { Header = "닫기", Icon = BuildMenuIcon("IconX") };
            closeItem.Click += (_, _) => RequestCloseBrowserTab(browser);
            cm.Items.Add(closeItem);

            var closeOthers = new MenuItem { Header = "다른 브라우저 모두 닫기", Icon = BuildMenuIcon("IconX") };
            closeOthers.Click += (_, _) =>
            {
                var parent = ParentOfTab(browser);
                if (parent == null) return;
                foreach (var other in parent.Tabs.OfType<BrowserTabItem>().ToList())
                    if (!ReferenceEquals(other, browser)) RequestCloseBrowserTab(other);
            };
            cm.Items.Add(closeOthers);

            cm.Items.Add(new Separator());
            cm.Items.Add(BuildSplitMoveItem(browser));
        }
        else if (tab is SessionItem s)
        {
            // 이름변경·포크·내보내기·잠금·외부 터미널 — 프로젝트 카드 세션 우클릭과 동일 기능.
            var renameItem = new MenuItem { Header = "이름 변경", Icon = BuildMenuIcon("IconPencil") };
            renameItem.Click += (_, _) => RenameSession(s);
            cm.Items.Add(renameItem);

            var forkItem = new MenuItem { Header = "포크", Icon = BuildMenuIcon("IconGitBranch") };
            forkItem.Click += (_, _) => ForkSession(s);
            cm.Items.Add(forkItem);

            var exportItem = new MenuItem { Header = "내보내기", Icon = BuildMenuIcon("IconFileText") };
            exportItem.Click += (_, _) => ExportSessionRequested?.Invoke(s);
            cm.Items.Add(exportItem);

            var externalItem = new MenuItem
            {
                Header = s.IsExternal ? "외부 터미널에서 실행 중" : "외부 터미널로 열기",
                Icon = BuildMenuIcon("IconExternalLink"),
                IsEnabled = !s.IsExternal && !s.IsBusy,
                ToolTip = s.IsExternal
                    ? "외부 터미널에서 실행 중입니다."
                    : s.IsBusy ? "응답이 완료된 후 외부 터미널로 열 수 있습니다." : null,
            };
            ToolTipService.SetShowOnDisabled(externalItem, true);
            externalItem.Click += (_, _) => ExternalSessionRequested?.Invoke(s);
            cm.Items.Add(externalItem);

            cm.Items.Add(new Separator());
            cm.Items.Add(BuildSplitMoveItem(s));

            cm.Items.Add(new Separator());

            // Lock toggle — 숨기기 바로 위.
            var lockItem = new MenuItem
            {
                Header = s.IsLocked ? "잠금 해제" : "세션 잠금",
                Icon = BuildMenuIcon(s.IsLocked ? "IconLockOpen" : "IconLock"),
            };
            lockItem.Click += (_, _) => ToggleSessionLockRequested?.Invoke(s);
            cm.Items.Add(lockItem);

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
                    HideSession(t); // graceful 종료 포함(미기동은 no-op)
                }
            };
            cm.Items.Add(hideOthers);

            if (!s.IsLocked && !s.IsExternal)
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

    private static void OpenBrowserInDefaultBrowser(BrowserTabItem browser)
    {
        if (browser.Browser.TryOpenInDefaultBrowser(browser.PersistenceKey)) return;
        ConfirmDialog.Alert("기본 브라우저로 열기", "열 수 있는 웹 주소가 없습니다.",
            iconKey: "IconTriangleAlert");
    }

    private static void CopyBrowserUrl(BrowserTabItem browser)
    {
        if (browser.Browser.TryCopyCurrentUrl(browser.PersistenceKey)) return;
        ConfirmDialog.Alert("현재 URL 복사", "URL을 클립보드에 복사하지 못했습니다.",
            iconKey: "IconTriangleAlert");
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
            HideSession(s); // 탭 숨김 + 실행 중이면 graceful 종료(내용 소실 방지)
        }
        else if (tab is FileTabItem f)
        {
            f.Editor.RequestClose();
        }
        else if (tab is BrowserTabItem b)
        {
            RequestCloseBrowserTab(b);
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
    private DocumentGroupItem? _tabGroupInsertPreview;
    private readonly List<Border> _tabGroupPreviewBorders = new();

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
            if (cross) ClearTabGroupInsertPreview();
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
            ClearTabGroupInsertPreview();
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
            exactFollow: true, horizontal: true, ghostSource: sourceBorder,
            reorderPreviewChanged: (target, _, after) =>
                SetTabGroupInsertPreview(FindTabGroupInsertPreview(s, target, after)));
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

    private DocumentGroupItem? FindTabGroupInsertPreview(
        TabItemBase source,
        TabItemBase? target,
        bool after)
    {
        if (_activeProject == null
            || source is not FileTabItem { IsDiff: false } sourceFile
            || target == null)
            return null;

        var remainingTabs = VisibleTabsInOrder()
            .Where(tab => !ReferenceEquals(tab, source))
            .ToList();
        int targetIndex = remainingTabs.IndexOf(target);
        if (targetIndex < 0) return null;

        int insertIndex = targetIndex + (after ? 1 : 0);
        foreach (var group in _activeProject.DocumentGroups)
        {
            var memberIndexes = group.Documents
                .Select(file => remainingTabs.IndexOf(file))
                .Where(index => index >= 0)
                .Order()
                .ToList();
            if (memberIndexes.Count == 0) continue;

            bool sourceAlreadyGrouped = group.Documents.Contains(sourceFile);
            bool staysInGroup = sourceAlreadyGrouped
                ? insertIndex >= memberIndexes[0] && insertIndex <= memberIndexes[^1] + 1
                : memberIndexes.Count >= 2
                    && insertIndex > memberIndexes[0]
                    && insertIndex <= memberIndexes[^1];
            if (staysInGroup) return group;
        }
        return null;
    }

    private void SetTabGroupInsertPreview(DocumentGroupItem? group)
    {
        if (ReferenceEquals(_tabGroupInsertPreview, group)) return;
        ClearTabGroupInsertPreview();
        if (group == null) return;

        _tabGroupInsertPreview = group;
        foreach (var file in group.Documents)
        {
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(file) is not FrameworkElement container
                || FindTabBorder(container) is not Border border)
                continue;

            border.SetResourceReference(Border.BackgroundProperty, "PrimarySoftBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
            _tabGroupPreviewBorders.Add(border);
        }
    }

    private void ClearTabGroupInsertPreview()
    {
        foreach (var border in _tabGroupPreviewBorders)
        {
            border.ClearValue(Border.BackgroundProperty);
            border.ClearValue(Border.BorderBrushProperty);
        }
        _tabGroupPreviewBorders.Clear();
        _tabGroupInsertPreview = null;
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
        // 테마 변경 → 에이전트 아이콘(codex/opencode 흑/백 등) 재평가. 값 변경 없이 바인딩만 다시 돌린다.
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
        ParkBrowserHost();
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
        //
        // 높이는 '현재 픽셀값으로 동결'한다. 브라우저/파일 탭 활성 시 SessionHeaderBar(Grid.Row2, 35px)가
        // collapse 되며 Row3(터미널 셀)이 커지는데, 파킹된 터미널이 Stretch 면 그 높이를 따라가 여전히
        // activeRoomId 인 백그라운드 방을 더 큰 행수(예: 42→44)로 refit 한다(window.resize→fit→ConPTY resize).
        // 그 방의 TUI(codex/claude)가 화면 밖에서 큰 크기로 재그린 뒤 show 때 다시 42 로 되돌려지며 하단
        // 입력영역/상태줄이 어긋난 채 남는다. 진입 시점 ActualHeight 는 아직 활성 레이아웃(헤더 포함) 값이므로
        // 그걸 고정하면 헤더 collapse 후에도 터미널이 안 커져 백그라운드 refit 자체가 사라진다.
        double h = TerminalHostContainer.ActualHeight;
        TerminalHostContainer.Width = double.NaN;
        TerminalHostContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (h > 1)
        {
            TerminalHostContainer.Height = h;
            TerminalHostContainer.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            TerminalHostContainer.Height = double.NaN;
            TerminalHostContainer.VerticalAlignment = VerticalAlignment.Stretch;
        }
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

    private bool _browserParked;

    private void ParkBrowserHost()
    {
        if (_browserParked) return;
        _browserParked = true;
        BrowserHostContainer.Width = 0;
        BrowserHostContainer.Height = 0;
        BrowserHostContainer.HorizontalAlignment = HorizontalAlignment.Left;
        BrowserHostContainer.VerticalAlignment = VerticalAlignment.Top;
        BrowserHostContainer.Visibility = Visibility.Visible;
    }

    private void UnparkBrowserHost()
    {
        if (_browserParked)
        {
            _browserParked = false;
            BrowserHostContainer.Width = double.NaN;
            BrowserHostContainer.Height = double.NaN;
            BrowserHostContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            BrowserHostContainer.VerticalAlignment = VerticalAlignment.Stretch;
        }
        BrowserHostContainer.Visibility = Visibility.Visible;
    }

    private void LoadExternalSessionPreview(SessionItem session)
    {
        // 스냅샷 없이 불투명 오버레이로 즉시 가린다(라이브 터미널 비침 방지). 터미널 HWND 는 park.
        _externalPreviewRoomId = session.Id;
        // 외부 에이전트 busy 변화(훅 상태파일→SessionBusyService→IsBusy)에 맞춰 복귀 버튼 활성/비활성.
        if (!ReferenceEquals(_externalBusyWatch, session))
        {
            if (_externalBusyWatch != null) _externalBusyWatch.PropertyChanged -= ExternalSession_PropertyChanged;
            _externalBusyWatch = session;
            session.PropertyChanged += ExternalSession_PropertyChanged;
        }
        ExternalSessionOverlay.Visibility = Visibility.Visible;
        ApplyExternalOverlayState();
    }

    // 여는 중(스피너) ↔ 실행 중(버튼) 오버레이 상태 반영.
    private void ApplyExternalOverlayState()
    {
        ExternalLaunchingPanel.Visibility = _externalReady ? Visibility.Collapsed : Visibility.Visible;
        ExternalReadyBorder.Visibility = _externalReady ? Visibility.Visible : Visibility.Collapsed;
        UpdateReturnButtonEnabled();
    }

    private void ExternalSession_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SessionItem.IsBusy) or nameof(SessionItem.IsExternal))
            UpdateReturnButtonEnabled();
    }

    // 응답 대기 중(IsBusy)이거나 이미 복귀 요청했으면 "인앱으로 가져오기" 비활성.
    private void UpdateReturnButtonEnabled()
    {
        bool busy = _activeSession?.IsBusy == true;
        ReturnInAppBtn.IsEnabled = !_returnRequested && !busy;
        ReturnInAppBtn.ToolTip = null;
        ReturnBtnLabel.Text = busy ? "응답이 진행중입니다." : "인앱으로 가져오기";
        ReturnBtnIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>외부 인계 시작 — 외부 터미널이 완전히 열릴 때까지 오버레이에 스피너를 표시한다.</summary>
    public void SetExternalLaunching(SessionItem session)
    {
        if (!ReferenceEquals(_activeSession, session)) return;
        _externalReady = false;
        if (ExternalSessionOverlay.Visibility == Visibility.Visible) ApplyExternalOverlayState();
    }

    /// <summary>외부 터미널이 열려 lock 을 잡음 — 스피너를 걷고 안내+복귀 버튼으로 전환.</summary>
    public void MarkExternalSessionReady(SessionItem session)
    {
        _externalReady = true;
        if (ReferenceEquals(_activeSession, session)
            && ExternalSessionOverlay.Visibility == Visibility.Visible)
            ApplyExternalOverlayState();
    }

    private void ShowExternalSessionPreview(SessionItem session)
    {
        _gateUnpark = false;
        _unparkFallback?.Stop();
        // 라이브 미러 대신 항상 정지 스냅샷(블러). WebView 터미널은 park 해 숨긴다.
        LoadExternalSessionPreview(session);
        ParkTerminalHost();
        ParkFileEditorHost();
        ParkBrowserHost();
        HideSessionLoading();
        TerminalCurtain.Visibility = Visibility.Collapsed;
    }

    private void RevealExternalSessionPreview()
    {
        HideExternalSessionPreview();
        UnparkTerminalHost();
        TerminalHostContainer.Visibility = Visibility.Visible;
        ParkFileEditorHost();
        ParkBrowserHost();
        HideSessionLoading();
        TerminalCurtain.Visibility = Visibility.Collapsed;
    }

    private void HideExternalSessionPreview()
    {
        ExternalSessionOverlay.Visibility = Visibility.Collapsed;
        // 다음에 다시 외부로 열 때 버튼이 활성/기본 문구로 보이게 복원.
        _returnRequested = false;
        ReturnInAppBtn.IsEnabled = true;
        ReturnInAppBtn.ToolTip = null;
        if (_externalBusyWatch != null)
        {
            _externalBusyWatch.PropertyChanged -= ExternalSession_PropertyChanged;
            _externalBusyWatch = null;
        }
        _externalPreviewRoomId = null;
    }

    /// <summary>오버레이 "인앱으로 가져오기" — 외부 터미널 종료를 요청한다. 실제 복귀는
    /// 외부 lock 해제를 감지한 셸(CheckExternalSessions)이 OnExternalSessionEnded 로 처리한다.</summary>
    private void ReturnInApp_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is not { IsExternal: true, IsBusy: false } session) return;
        _returnRequested = true; // 중복 클릭 방지 — 복귀 완료 시 오버레이가 사라지며 리셋
        UpdateReturnButtonEnabled();
        ReturnExternalSessionRequested?.Invoke(session);
    }

    private void UpdateEmptyState()
    {
        bool hasActive = _activeTab != null;

        if (_fileDropOverlayActive)
        {
            // 파일 드롭 선택 화면이 떠 있는 동안 훅·상태 갱신이 HWND를 다시 표시하지 못하게 막는다.
            TerminalHostContainer.Visibility = Visibility.Collapsed;
            FileEditorHostContainer.Visibility = Visibility.Collapsed;
            BrowserHostContainer.Visibility = Visibility.Collapsed;
        }
        else if (_activeTab is SessionItem { IsExternal: true } externalSession)
        {
            ShowExternalSessionPreview(externalSession);
        }
        else
        {
            HideExternalSessionPreview();
            if (_activeTab is SessionItem)
            {
                // 콜드 게이트 중이면 unpark 과 파일 에디터 파킹 둘 다 ACK(RevealTerminalAfterGate)까지 미룬다 —
                // 파일(md)에서 세션 전환 시 md 를 먼저 파킹하면 airspace 갭에 검정이 새므로, md 를 띄워둔 채 대기.
                if (!_gateUnpark) { UnparkTerminalHost(); ParkFileEditorHost(); ParkBrowserHost(); }
                // suspend(스냅샷+Collapsed) 중 훅발 갱신이 HWND 를 되살리면 airspace 로 스냅샷을 뚫고
                // 라이브 터미널이 보인다(설정창 열어둔 채 codex 응답 완료 등) — 복원은 ResumeTerminal 만.
                if (!_overlaySuspended) TerminalHostContainer.Visibility = Visibility.Visible;
            }
            else if (_activeTab is FileTabItem)
            {
                ParkTerminalHost();
                ParkBrowserHost();
                // 전환 커버 중이면 파일 에디터(md=WebView2 는 airspace 로 WPF 커튼에 안 가려짐)를 0×0 주차로
                // 감추고 TerminalCurtain(단색)으로 대신 가린다 → reveal 동기화 시 함께 나타나게(파일 조기표시 방지).
                // Collapsed 로 감추면 md HWND 생성/재표시가 reveal 순간으로 밀려 컴포지터 첫 프레임(검정)이
                // 번쩍인다 — 주차는 HWND 를 안 보이게 살려 두므로 reveal 이 '리사이즈'가 되어 검정 프레임이 없다.
                if (_coverActive) ParkFileEditorHost();
                else if (!_overlaySuspended) UnparkFileEditorHost(); // suspend 중 Visible 복원 금지(위 세션 분기와 동일)
            }
            else if (_activeTab is BrowserTabItem)
            {
                ParkTerminalHost();
                ParkFileEditorHost();
                if (_coverActive) ParkBrowserHost();
                else if (!_overlaySuspended) UnparkBrowserHost();
            }
            else
            {
                ParkTerminalHost();
                ParkFileEditorHost();
                ParkBrowserHost();
            }
        }

        // 파일 패널 커버: 커버 중 & 파일 탭일 때만 단색 커튼 노출(세션은 웹 레이어 #xfer-cover 가 담당).
        // 숨김은 EndCover 가 처리(fade). (커튼은 다이얼로그 suspend 와도 공유하지만 프로젝트 전환과 시점이 안 겹침.)
        if (_coverActive && _activeTab is FileTabItem or BrowserTabItem)
        { TerminalCurtain.Opacity = 1; TerminalCurtain.Visibility = Visibility.Visible; }

        EmptyState.Visibility = hasActive ? Visibility.Collapsed : Visibility.Visible;

        // 프로젝트 미선택(빈 패널) 시 새 탭(+) 버튼과 메타바(#·경로) 숨김. 탭 드래그 중이면 항상 숨김.
        if (NewTabBtn != null)
            NewTabBtn.Visibility = (!_tabDragActive && _activeProject != null) ? Visibility.Visible : Visibility.Collapsed;
        ApplyProjectInfoHeaderVisibility();

        SessionHeaderBar.Visibility = hasActive && _activeTab is not BrowserTabItem
            ? Visibility.Visible : Visibility.Collapsed;
        if (hasActive && _activeTab is not BrowserTabItem)
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
        if (!FileEditorView.IsEditable(path) && !ImageFileEditorView.IsImage(path) && !PdfFileEditorView.IsPdf(path))
        {
            ConfirmDialog.Alert("파일 열기", "텍스트, 이미지, PDF 파일만 앱에서 열 수 있습니다.",
                iconKey: "IconTriangleAlert");
            return null;
        }
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

    /// <summary>분할 복원용 — 저장된 ID의 브라우저 탭을 지정 프로젝트에서 열어 반환.</summary>
    public BrowserTabItem? OpenBrowserTabForPartner(ProjectItem proj, string id)
    {
        if (!ReferenceEquals(_activeProject, proj)) SetActiveProject(proj);
        var tab = proj.Tabs.OfType<BrowserTabItem>().FirstOrDefault(b => b.Id == id);
        if (tab == null) return null;
        ActivateBrowserTab(tab);
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

    /// <summary>diff 파일 탭 생성(같은 repo/경로/staged 이미 있으면 재사용). Editor 는 Monaco diff.</summary>
    private FileTabItem? CreateDiffTab(ProjectItem proj, string repo, string relPath, bool staged)
    {
        var abs = System.IO.Path.Combine(repo, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var existing = proj.Tabs.OfType<FileTabItem>()
            .FirstOrDefault(t => t.IsDiff && string.Equals(t.FilePath, abs, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var tab = new FileTabItem { FilePath = abs, IsDiff = true, Editor = new MonacoDiffHostView(repo, relPath, staged) };
        tab.Editor.CloseRequested += (_, _) =>
        {
            if (FileTabCloseRequested != null) FileTabCloseRequested(tab);
            else RemoveFileTab(tab);
        };
        proj.Tabs.Add(tab);
        return tab;
    }

    /// <summary>SCM 패널의 파일 클릭 → 이 패널에 diff 탭을 열고 활성화.</summary>
    public void OpenDiffTab(ProjectItem proj, string repo, string relPath, bool staged)
    {
        if (!ReferenceEquals(_activeProject, proj)) SetActiveProject(proj);
        // 이미지 파일은 diff(좌우 텍스트 비교)가 무의미 → 이미지 뷰어 탭으로 연다.
        var abs = System.IO.Path.Combine(repo, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (ImageFileEditorView.IsImage(relPath) && System.IO.File.Exists(abs))
        {
            var ftab = CreateFileTab(proj, abs);
            if (ftab != null) ActivateFileTab(ftab);
            return;
        }
        var tab = CreateDiffTab(proj, repo, relPath, staged);
        if (tab != null) ActivateFileTab(tab);
    }

    /// <summary>지정 repo 가 이 패널의 활성 프로젝트와 같으면 브랜치 버블을 다시 읽는다.</summary>
    public void RefreshBranchIfRepo(string repoDir)
    {
        if (_activeProject == null || string.IsNullOrEmpty(repoDir)) return;
        var a = _activeProject.Path?.TrimEnd('\\', '/');
        var b = repoDir.TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            UpdateProjectBranchBubble(_activeProject);
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
        if (ext is ".md" or ".markdown") return new MarkdownFileEditorView();
        if (ImageFileEditorView.IsImage(path)) return new ImageFileEditorView();
        if (PdfFileEditorView.IsPdf(path)) return new PdfFileEditorView();
        return new FileEditorView();
    }

    // ── airspace 우회 (오버레이가 뜰 때 터미널 WebView2 정지) ──────────
    /// <summary>터미널 WebView2 를 스냅샷/커튼으로 대체하고 숨긴다. FileExplorer 는 셸이 처리.</summary>
    public async Task SuspendTerminalWithSnapshotAsync(bool blankCurtain = false)
    {
        _overlaySuspended = true; // ResumeTerminal 이 해제 — 그 사이 훅발 UpdateEmptyState 의 표시 복원 차단
        if (_activeTab is BrowserTabItem browser)
        {
            await browser.Browser.SuspendContentAsync();
            return;
        }
        // 파일 편집기(WebView2) 처리
        if (_activeTab is FileTabItem file)
    {
            // 설정 화면·종료 오버레이 모두 같은 창 안의 WPF 오버레이다 — airspace 때문에 라이브
            // WebView2 HWND 를 가릴 수 없으므로, 스냅샷을 깔고 HWND 를 숨긴다(blankCurtain 여부 무관).
            var fileSnap = await file.Editor.CaptureSnapshotAsync();
            if (fileSnap != null)
    {
                TerminalSnapshot.Source = fileSnap;
                TerminalSnapshot.Visibility = Visibility.Visible;
                // 스냅샷이 실제 프레임에 present 된 뒤 HWND(md 에디터)를 숨긴다 — 동시에 바꾸면 HWND 가
                // 먼저 사라져 빈 배경이 한 프레임 노출되며 종료 오버레이 직전 깜빡인다(SuspendTerminalOnlyAsync 와 동일 기법).
                await WaitForFramesAsync(2);
            }
            // 캡처가 실패해도 HWND 는 반드시 숨긴다 — 남겨두면 오버레이를 뚫고 보인다.
            FileEditorHostContainer.Visibility = Visibility.Collapsed;
            return;
        }

        if (_activeSession is { IsExternal: true } externalSession
            && !_terminal.HasExternalPreview(externalSession.Id)) return;
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
        _overlaySuspended = false;
        if (_activeSession is { IsExternal: true } externalSession)
        {
            ShowExternalSessionPreview(externalSession);
            return;
        }
        if (_activeSession != null)
            TerminalHostContainer.Visibility = Visibility.Visible;
        if (_activeTab is FileTabItem)
            FileEditorHostContainer.Visibility = Visibility.Visible;
        if (_activeTab is BrowserTabItem browser)
        {
            UnparkBrowserHost();
            browser.Browser.ResumeContent();
        }
        TerminalSnapshot.Visibility = Visibility.Collapsed;
        TerminalSnapshot.Source = null;
        TerminalCurtain.Visibility = Visibility.Collapsed;
    }

    /// <summary>스냅샷만(커튼 없이) 정지 — 우측 오버레이 드로어용.
    /// anchorTopLeft=true 면 캡처 시점 크기로 좌상단 고정 → 패널이 리사이즈돼도 이미지가 같이 늘어나지 않고 잘려 보인다(분할 애니메이션용).
    /// stretchCover=true(webCover 전용)면 커버를 뷰포트에 맞춰 늘린다 — 전체화면 토글처럼 창 전체가
    /// 한 번에 크게 변하는 전환용(좌상단 고정은 커지는 쪽 영역이 배경색만 남아 '비어' 보인다).</summary>
    public async Task SuspendTerminalOnlyAsync(bool anchorTopLeft = false, bool webCover = false,
        bool stretchCover = false, bool captureSplitWide = false)
    {
        // 전체 오버레이(설정 화면 등)로 이미 정지·숨김 상태면 아무것도 하지 않는다.
        // 숨겨진(Collapsed) WebView2 는 CapturePngAsync 가 <b>완료되지 않으므로</b> 여기서 await 하면
        // 호출자(최대화 커버·패널 토글 커버·종료 준비)가 그대로 매달린다.
        if (_overlaySuspended) return;
        if (_activeTab is BrowserTabItem browser)
        {
            await browser.Browser.SuspendContentAsync();
            return;
        }
        if (_activeSession is { IsExternal: true } externalSession
            && !_terminal.HasExternalPreview(externalSession.Id)) return;
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
            if (png != null) _terminal.CoverForTransitionImage(png, cw, ch, stretchCover, captureSplitWide);
            else _terminal.CoverForTransition(captureSplitWide); // 폴백: 단색 커버
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
        // 설정 화면 등 오버레이로 이미 정지·숨김 상태면 준비할 게 없다. 그대로 캡처를 시도하면
        // 숨겨진 WebView2 의 CapturePngAsync 가 완료되지 않아 종료 준비가 타임아웃까지 지연된다.
        bool alreadySuspended = _overlaySuspended;
        _overlaySuspended = true; // 종료 오버레이 중 훅발 UpdateEmptyState 가 HWND 를 되살리지 않게(해제 불필요 — 앱 종료)
        _shutdownHide = ShutdownHide.None;
        if (alreadySuspended) return;
        if (_activeTab is FileTabItem file)
        {
            var snap = await file.Editor.CaptureSnapshotAsync();
            if (snap == null) return; // 캡처 실패 — 에디터를 그대로 두면 최소한 내용은 보인다(기존 동작)
            TerminalSnapshot.Source = snap;
            TerminalSnapshot.Visibility = Visibility.Visible;
            _shutdownHide = ShutdownHide.FileEditor;
            return;
        }
        if (_activeTab is BrowserTabItem browser)
        {
            await browser.Browser.SuspendContentAsync();
            return;
        }
        if (_activeSession is { IsExternal: true } externalSession
            && !_terminal.HasExternalPreview(externalSession.Id)) return;
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

    public void ResumeTerminalOnly(bool webCover = false, bool recoverWiden = false)
    {
        // 전체 오버레이가 떠 있는 동안은 되살리지 않는다(Suspend 를 스킵했으므로 되살릴 것도 없고,
        // 되살리면 라이브 HWND 가 오버레이를 뚫는다). 해제는 ResumeTerminal 이 담당.
        if (_overlaySuspended) return;
        if (_activeTab is BrowserTabItem browser)
        {
            UnparkBrowserHost();
            browser.Browser.ResumeContent();
            return;
        }
        if (_activeSession is { IsExternal: true } externalSession)
        {
            ShowExternalSessionPreview(externalSession);
            return;
        }
        // webCover: HWND 를 숨긴 적이 없으므로 되살릴 것도, WPF 스냅샷도 없다. 최종 폭을 확정(UpdateLayout)해
        // JS 에 넘겨, clientWidth 가 거기 근접하면 fit 억제 해제 + fit + ConPTY 재동기 후 커버 이미지를
        // 라이브 터미널로 크로스페이드한다 → resume 리플로우가 커버 아래서 일어나 안 보이고, HWND 전환 플래시도 없다.
        if (webCover)
        {
            if (_activeSession != null)
            {
                UpdateLayout();
                double target = TerminalHostContainer?.ActualWidth ?? 0;
                RememberActiveSessionSize(target);
                _terminal.RevealAfterTransition(_activeSession.Id, kick: true, expectWidth: target,
                    recoverWiden: recoverWiden);
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

    private void OnTerminalFileOpenRequested(string rawPath, int? line, int? column)
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

        if (System.IO.File.Exists(fullPath))
        {
            var tab = OpenFileAsTab(fullPath);
            if (line is > 0 && tab?.Editor is FileEditorView editor)
                editor.GoToLocation(line.Value, column);
            return;
        }

        if (!System.IO.Directory.Exists(fullPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath)
            {
                UseShellExecute = true,
            });
        }
        catch { /* Explorer로 열 수 없는 폴더는 무시 */ }
    }

    private void OnTerminalBrowserUrlOpenRequested(string url)
    {
        if (_activeProject == null)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* 기본 브라우저 실행 실패 무시 */ }
            return;
        }

        // 터미널 URL은 탭 이름도 주소 자체로 유지한다. 탭 UI의 CharacterEllipsis가 가용 폭까지만 표시한다.
        var tab = AddBrowserTab(_activeProject, initialName: url, promptForName: false);
        tab?.Browser.NavigateToUrl(url);
    }
}
