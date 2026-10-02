using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>좌측 사이드바 — 프로젝트(디렉터리) → 세션 트리. 동작은 이벤트로 MainWindow에 위임.</summary>
public partial class SidebarView : UserControl
{
    public static readonly DependencyProperty SearchHighlightQueryProperty =
        DependencyProperty.Register(
            nameof(SearchHighlightQuery),
            typeof(string),
            typeof(SidebarView),
            new PropertyMetadata(""));

    public string SearchHighlightQuery
    {
        get => (string)GetValue(SearchHighlightQueryProperty);
        private set => SetValue(SearchHighlightQueryProperty, value);
    }

    /// <summary>접힌 프로젝트 카드의 경로 줄 표시 여부. 설정 &gt; 프로젝트에서 전환.</summary>
    public static readonly DependencyProperty ShowCollapsedProjectPathProperty =
        DependencyProperty.Register(
            nameof(ShowCollapsedProjectPath),
            typeof(bool),
            typeof(SidebarView),
            new PropertyMetadata(true));

    public bool ShowCollapsedProjectPath
    {
        get => (bool)GetValue(ShowCollapsedProjectPathProperty);
        set => SetValue(ShowCollapsedProjectPathProperty, value);
    }

    public SidebarView()
    {
        InitializeComponent();
        _sidebarSearchDebounce.Tick += (_, _) =>
        {
            _sidebarSearchDebounce.Stop();
            if (HasActiveDrag)
            {
                _sidebarSearchDeferredForDrag = true;
                return;
            }
            ApplySidebarSearch();
        };
        ProjectsHost.ItemsSource = _activeRootItems;
        ArchivedHost.ItemsSource = _archivedRootItems;
        // 세션 클릭/포커스 시 WPF 자동 BringIntoView 가 프로젝트 패널을 스크롤하는 것 차단
        // (외부 ScrollViewer 의 ScrollContentPresenter 보다 아래에서 삼켜야 내부 숨김세션 스크롤은 그대로 동작).
        ProjectsHost.AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(ProjectHost_RequestBringIntoView));
        ArchivedHost.AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(ProjectHost_RequestBringIntoView));
        WorkspaceStore.ProjectFolders.CollectionChanged += ProjectFolders_CollectionChanged;
        PreviewMouseMove += Sidebar_PreviewMouseMove;
        PreviewMouseLeftButtonUp += Sidebar_PreviewMouseUp;
        LostMouseCapture += Sidebar_LostCapture;
        PreviewMouseLeftButtonDown += Sidebar_PreviewMouseLeftButtonDown;
        PreviewKeyDown += Sidebar_PreviewKeyDown;
    }

    private static void ProjectHost_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) => e.Handled = true;

    public event Action? AddProjectRequested;
    public event Action<ProjectItem>? ProjectSelected;
    public event Action<ProjectItem>? AddSessionRequested;
    public event Action<ProjectItem>? ProjectDeleteRequested;
    /// <summary>프로젝트 메뉴 "세션 관리자" — 세션 일괄 관리 팝업 요청(MainWindow 위임).</summary>
    public event Action<ProjectItem>? SessionManagerRequested;
    /// <summary>프로젝트 메뉴 "이름 변경" 요청(MainWindow 위임).</summary>
    public event Action<ProjectItem>? ProjectRenameRequested;
    /// <summary>프로젝트 메뉴 "보관함 이동" — 활성에서 보관함으로(MainWindow 위임).</summary>
    public event Action<ProjectItem>? ProjectArchiveRequested;
    /// <summary>보관함 카드 "꺼내기" — 보관함에서 활성으로(MainWindow 위임).</summary>
    public event Action<ProjectItem>? ProjectUnarchiveRequested;
    /// <summary>프로젝트 메뉴의 Git 원격 저장소 URL 열기 요청.</summary>
    public event Action<ProjectItem, string>? GitRemoteOpenRequested;
    /// <summary>프로젝트 메뉴 "파일 추가" — 파일 다이얼로그로 등록할 파일을 고른다(MainWindow 위임).</summary>
    public event Action<ProjectItem>? AddProjectFileRequested;
    /// <summary>등록된 파일 클릭 — 편집 탭으로 연다(MainWindow 위임).</summary>
    public event Action<ProjectFile>? ProjectFileSelected;
    /// <summary>바로가기 행 제거 요청(MainWindow 위임 — Files 에서 제거 후 저장).</summary>
    public event Action<ProjectFile>? ProjectFileRemoveRequested;
    /// <summary>바로가기 이름 변경 요청(MainWindow 위임).</summary>
    public event Action<ProjectFile>? ProjectFileRenameRequested;
    /// <summary>드래그로 프로젝트 순서가 바뀐 뒤 발생(영속 저장용).</summary>
    public event Action? ProjectsReordered;
    // 프로젝트/폴더/부모 세션 접힘·펼침 변경 → 영속 저장 트리거 (검색 자동 펼침은 제외).
    public event Action? ProjectExpandChanged;
    /// <summary>드래그로 특정 프로젝트의 세션 순서가 바뀐 뒤 발생(탭 동기화 + 영속용).</summary>
    public event Action<ProjectItem>? SessionsReordered;
    /// <summary>드래그로 특정 프로젝트의 바로가기 순서가 바뀐 뒤 발생(영속용).</summary>
    public event Action<ProjectItem>? FilesReordered;
    public event Action<SessionItem>? SessionSelected;
    /// <summary>사이드바 입력 UI가 닫혀 활성 세션으로 포커스를 돌려도 되는 시점을 알린다.</summary>
    public event Action? TerminalFocusRestoreRequested;
    /// <summary>카드의 열린 문서(파일 탭) 행 클릭 — 해당 파일 탭을 활성화(MainWindow 위임).</summary>
    public event Action<FileTabItem>? OpenDocSelected;
    /// <summary>카드 문서 우클릭 "문서 닫기" — 해당 파일 탭을 닫는다(MainWindow 위임).</summary>
    public event Action<FileTabItem>? OpenDocCloseRequested;
    /// <summary>카드 문서 우클릭 "다른 파일 모두 닫기" — 우클릭한 파일 외 같은 프로젝트 문서를 닫는다.</summary>
    public event Action<FileTabItem>? OpenDocCloseOthersRequested;
    public Func<TabItemBase, (string Header, string IconKey)>? SplitMovePresentationProvider { get; set; }
    public event Action<TabItemBase>? SplitMoveRequested;
    /// <summary>카드의 웹 브라우저 탭 클릭/닫기 요청.</summary>
    public event Action<BrowserTabItem>? BrowserTabSelected;
    public event Action<BrowserTabItem>? BrowserTabCloseRequested;
    public event Action<BrowserTabItem>? BrowserTabRenameRequested;
    public event Action<SessionItem>? SessionDeleteRequested;
    public event Action<SessionItem>? SessionRenameRequested;
    public event Action<SessionItem>? SessionStopTrackingRequested;
    /// <summary>세션 메뉴 "세션 숨기기" — 탭 X 숨기기와 동일(MainWindow 위임).</summary>
    public event Action<SessionItem>? SessionHideRequested;
    /// <summary>세션 메뉴 "세션 종료" — 목록은 그대로 두고 실행 중인 프로세스만 정상 종료(MainWindow 위임).</summary>
    public event Action<SessionItem>? SessionShutdownRequested;
    /// <summary>세션 메뉴 "포크" 요청(MainWindow 위임) — 원본 대화를 복사한 새 세션 생성.</summary>
    public event Action<SessionItem>? SessionForkRequested;
    public event Action<SessionItem>? SessionCopyIdRequested;
    /// <summary>세션 메뉴 "외부 터미널로 열기" 요청(MainWindow 위임).</summary>
    public event Action<SessionItem>? SessionExternalRequested;
    /// <summary>세션 메뉴 "내보내기" 요청(MainWindow 위임) — 대화를 .md 로 저장.</summary>
    public event Action<SessionItem>? SessionExportRequested;
    /// <summary>세션 메뉴 "잠금/잠금 해제" 요청(MainWindow 위임).</summary>
    public event Action<SessionItem>? SessionLockRequested;
    public event Action<IReadOnlyList<SessionItem>, bool>? SessionsLockRequested;
    public event Action<IReadOnlyList<SessionItem>>? SessionsHideRequested;
    public event Action<IReadOnlyList<SessionItem>>? SessionsStopTrackingRequested;
    public event Action<IReadOnlyList<SessionItem>>? SessionsDeleteRequested;

    // 프로젝트 목록 열 수(1/2). 2면 카드 2열 그리드 + 가로 드래그. 기본 1.
    /// <summary>숨김 세션 표시 토글 변경 → 영속 저장 트리거.</summary>
    public event Action? HiddenSessionVisibilityChanged;

    private int _projectColumns = 1;

    /// <summary>프로젝트/보관함 목록을 1열(세로) 또는 2열(좌/우 독립 컬럼)로 전환. MainWindow 가 설정값으로 호출.
    /// Tag → ProjectColumnsPanel.Columns 바인딩으로 레이아웃만 바뀌며, 카드의 Column 값은 그대로라 자동 재배치가 없다.</summary>
    public void ApplyProjectColumns(int cols)
    {
        _projectColumns = cols == 2 ? 2 : 1;
        ProjectsHost.Tag = _projectColumns;
        ArchivedHost.Tag = _projectColumns;
        foreach (var folder in WorkspaceStore.ProjectFolders)
            folder.ColumnToggleAvailable = _projectColumns == 2;
    }

    private readonly ObservableCollection<object> _activeRootItems = new();
    private readonly ObservableCollection<object> _archivedRootItems = new();
    private readonly ObservableCollection<ProjectFolderItem> _activeProjectFolders = new();
    private readonly ObservableCollection<ProjectFolderItem> _archivedProjectFolders = new();
    private ObservableCollection<ProjectItem>? _projects;
    public ObservableCollection<ProjectItem> Projects
    {
        get => _projects ??= new();
        set
        {
            if (_projects != null)
            {
                _projects.CollectionChanged -= Projects_CollectionChanged;
                foreach (var project in _projects)
                    project.PropertyChanged -= Project_PropertyChanged;
            }

            _projects = value;
            value.CollectionChanged += Projects_CollectionChanged;
            foreach (var project in value)
                project.PropertyChanged += Project_PropertyChanged;

            RefreshProjectGroups();
        }
    }

    private string _sidebarSearchQuery = "";
    private readonly DispatcherTimer _sidebarSearchDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(180)
    };
    private bool _sidebarSearchDeferredForDrag;
    private bool _projectFilterRefreshDeferredForDrag;
    private bool _showActiveProjectsOnly;

    private void Projects_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ProjectItem project in e.OldItems)
                project.PropertyChanged -= Project_PropertyChanged;
        if (e.NewItems != null)
            foreach (ProjectItem project in e.NewItems)
                project.PropertyChanged += Project_PropertyChanged;
        RefreshProjectGroups();
    }

    private void Project_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectItem.FolderId))
        {
            RefreshProjectGroups();
            return;
        }

        if (e.PropertyName == nameof(ProjectItem.HasAliveSession) && _showActiveProjectsOnly)
        {
            if (HasActiveDrag)
                _projectFilterRefreshDeferredForDrag = true;
            else
                RefreshProjectGroups();
            return;
        }

        if ((e.PropertyName == nameof(ProjectItem.HasBusySession) ||
             e.PropertyName == nameof(ProjectItem.UnseenSessionCount) ||
             e.PropertyName == nameof(ProjectItem.IsSelected)) &&
            sender is ProjectItem project)
            UpdateProjectFolderSummary(project);
    }

    private void UpdateProjectFolderSummary(ProjectItem project)
    {
        if (project.FolderId == null) return;
        var folder = WorkspaceStore.ProjectFolders.FirstOrDefault(item => item.Id == project.FolderId);
        if (folder == null) return;

        var source = folder.IsArchived ? ArchivedProjects : Projects;
        var allProjects = source.Where(item => item.FolderId == folder.Id).ToList();
        folder.UpdateSummary(allProjects, folder.Projects.Count, _sidebarSearchQuery.Length > 0);
    }

    private void ProjectFolders_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ProjectFolderItem folder in e.OldItems)
                folder.PropertyChanged -= ProjectFolder_PropertyChanged;
        if (e.NewItems != null)
            foreach (ProjectFolderItem folder in e.NewItems)
                folder.PropertyChanged += ProjectFolder_PropertyChanged;
        RefreshProjectGroups();
    }

    private void ProjectFolder_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectFolderItem.ArchivedAt) or nameof(ProjectFolderItem.Name))
            RefreshProjectGroups();
    }

    private bool _movingProjectFolder;
    private void RefreshProjectGroups()
    {
        if (_projects == null || _archivedProjects == null) return;

        var folders = WorkspaceStore.ProjectFolders;
        var foldersById = folders.ToDictionary(folder => folder.Id, StringComparer.Ordinal);
        if (!_movingProjectFolder)
        {
            foreach (var project in _projects)
                if (project.FolderId != null &&
                    (!foldersById.TryGetValue(project.FolderId, out var folder) || folder.IsArchived))
                    project.FolderId = null;
            foreach (var project in _archivedProjects)
                if (project.FolderId != null &&
                    (!foldersById.TryGetValue(project.FolderId, out var folder) || folder.IsActive))
                    project.FolderId = null;
        }

        bool hasQuery = _sidebarSearchQuery.Length > 0;
        bool Matches(ProjectItem project) =>
            project.Name.Contains(_sidebarSearchQuery, StringComparison.OrdinalIgnoreCase) ||
            project.Sessions.Any(session => session.Name.Contains(_sidebarSearchQuery, StringComparison.OrdinalIgnoreCase));
        bool MatchesActiveFilter(ProjectItem project) =>
            !_showActiveProjectsOnly || project.HasAliveSession;

        foreach (var project in _projects.Concat(_archivedProjects))
        {
            bool projectMatches = hasQuery &&
                project.Name.Contains(_sidebarSearchQuery, StringComparison.OrdinalIgnoreCase);
            bool folderMatches = hasQuery &&
                project.FolderId != null &&
                foldersById.TryGetValue(project.FolderId, out var folder) &&
                folder.Name.Contains(_sidebarSearchQuery, StringComparison.OrdinalIgnoreCase);
            project.ApplySidebarSearch(
                _sidebarSearchQuery,
                !hasQuery || projectMatches || folderMatches);
        }

        foreach (var folder in folders)
        {
            bool folderMatches = hasQuery &&
                folder.Name.Contains(_sidebarSearchQuery, StringComparison.OrdinalIgnoreCase);
            var source = folder.IsArchived ? _archivedProjects : _projects;
            var allProjects = source.Where(project => project.FolderId == folder.Id).ToList();
            var desired = allProjects.Where(project =>
                (folder.IsArchived || MatchesActiveFilter(project)) &&
                (!hasQuery || folderMatches || Matches(project))).ToList();
            SyncCollection(folder.Projects, desired);
            folder.ColumnToggleAvailable = _projectColumns == 2;
            folder.UpdateSummary(allProjects, desired.Count,
                hasQuery || (!folder.IsArchived && _showActiveProjectsOnly));
            folder.IsSearchVisible =
                (folder.IsArchived || !_showActiveProjectsOnly || desired.Count > 0) &&
                (!hasQuery || folderMatches || desired.Count > 0);
        }

        var activeFolders = folders.Where(folder => folder.IsActive).ToList();
        var archivedFolders = folders.Where(folder => folder.IsArchived).ToList();
        SyncCollection(_activeProjectFolders, activeFolders);
        SyncCollection(_archivedProjectFolders, archivedFolders);

        var activeRoots = BuildOrderedRootItems(
            activeFolders,
            _projects.Where(project => project.FolderId == null));
        var archivedRoots = BuildOrderedRootItems(
            archivedFolders,
            _archivedProjects.Where(project => project.FolderId == null));

        SyncCollection(_activeRootItems, activeRoots.Where(item => item switch
        {
            ProjectFolderItem folder => folder.IsSearchVisible,
            ProjectItem project => MatchesActiveFilter(project) && (!hasQuery || Matches(project)),
            _ => false,
        }).ToList());
        SyncCollection(_archivedRootItems, archivedRoots.Where(item => item switch
        {
            ProjectFolderItem folder => folder.IsSearchVisible,
            ProjectItem project => !hasQuery || Matches(project),
            _ => false,
        }).ToList());
        if (ActiveFilterEmptyText != null)
            ActiveFilterEmptyText.Visibility =
                _showActiveProjectsOnly && !hasQuery && _activeRootItems.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        UpdateArchiveEmptyState();
    }

    private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (!desired.Contains(target[i]))
                target.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            int current = target.IndexOf(desired[i]);
            if (current < 0) target.Insert(i, desired[i]);
            else if (current != i) target.Move(current, i);
        }
    }

    private static int RootOrderOf(object item) => item switch
    {
        ProjectFolderItem folder => folder.RootOrder,
        ProjectItem project => project.RootOrder,
        _ => int.MaxValue,
    };

    private static void SetRootOrder(object item, int order)
    {
        if (item is ProjectFolderItem folder) folder.RootOrder = order;
        else if (item is ProjectItem project) project.RootOrder = order;
    }

    private static List<object> BuildOrderedRootItems(
        IEnumerable<ProjectFolderItem> folders,
        IEnumerable<ProjectItem> projects)
    {
        var fallback = folders.Cast<object>().Concat(projects).ToList();
        var ordered = fallback
            .Select((item, index) => (Item: item, Index: index))
            .OrderBy(entry => RootOrderOf(entry.Item) == int.MaxValue ? 1 : 0)
            .ThenBy(entry => RootOrderOf(entry.Item))
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
            SetRootOrder(ordered[i], i);
        return ordered;
    }

    private List<object> GetAllRootItems(bool archived) =>
        BuildOrderedRootItems(
            WorkspaceStore.ProjectFolders.Where(folder => folder.IsArchived == archived),
            (archived ? ArchivedProjects : Projects).Where(project => project.FolderId == null));

    private static void ApplyRootOrder(IReadOnlyList<object> ordered)
    {
        for (int i = 0; i < ordered.Count; i++)
            SetRootOrder(ordered[i], i);
    }
    private ObservableCollection<ProjectItem>? _archivedProjects;
    /// <summary>보관함 프로젝트 — 활성과 동일한 ProjectTemplate 로 보관함 패널에 표시.</summary>
    public ObservableCollection<ProjectItem> ArchivedProjects
    {
        get => _archivedProjects ??= new();
        set
        {
            if (_archivedProjects != null)
            {
                _archivedProjects.CollectionChanged -= OnArchivedChanged;
                foreach (var project in _archivedProjects)
                    project.PropertyChanged -= Project_PropertyChanged;
            }
            _archivedProjects = value;
            value.CollectionChanged += OnArchivedChanged;
            foreach (var project in value)
                project.PropertyChanged += Project_PropertyChanged;
            RefreshProjectGroups();
        }
    }

    private void OnArchivedChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ProjectItem project in e.OldItems)
                project.PropertyChanged -= Project_PropertyChanged;
        if (e.NewItems != null)
            foreach (ProjectItem project in e.NewItems)
                project.PropertyChanged += Project_PropertyChanged;
        RefreshProjectGroups();
    }

    private void UpdateArchiveEmptyState()
    {
        if (ArchiveEmptyText != null)
            ArchiveEmptyText.Visibility =
                ArchivedProjects.Count == 0 && _archivedProjectFolders.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
    }

    // ── 보관함 슬라이드 전환 (devez 정합: 프로젝트↔보관함, 헤더 제목·뒤로가기 교체) ──────────
    private bool _archiveOpen;

    /// <summary>현재 보기의 대상 컬렉션(검색/일괄펼침/드래그 공용).</summary>
    private ObservableCollection<ProjectItem> CurrentProjects => _archiveOpen ? ArchivedProjects : Projects;

    // 보관함 버튼은 토글 — 열면 Tag="active"(Primary 배경), 다시 누르면 프로젝트 보기로 복귀.
    private void ArchiveToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveOpen) CloseArchivePanel();
        else OpenArchivePanel();
    }

    private void OpenArchivePanel()
    {
        if (_archiveOpen) return;
        ClearSessionMultiSelection();
        _archiveOpen = true;
        UpdateArchiveEmptyState();
        UpdateExpandAllVisual();
        // 검색 초기화(보기 전환 시 필터 리셋)
        if (SidebarSearchBox.Text.Length > 0) SidebarSearchBox.Clear();

        double w = ActualWidth > 0 ? ActualWidth : 262;
        ArchivePanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() => UpdateProjectScrollVisuals(ArchiveProjectScroll)), DispatcherPriority.Loaded);
        SlideTo(ArchivePanelTransform, w, 0);
        SlideTo(ActivePanelTransform, 0, -w, () => ActivePanel.Visibility = Visibility.Collapsed);

        HeaderTitle.Text = "보관함";
        ArchiveToggleBtn.Tag = "active";
        ActiveProjectFilterRow.Visibility = Visibility.Collapsed;
    }

    private void CloseArchivePanel()
    {
        if (!_archiveOpen) return;
        ClearSessionMultiSelection();
        _archiveOpen = false;
        if (SidebarSearchBox.Text.Length > 0) SidebarSearchBox.Clear();

        double w = ActualWidth > 0 ? ActualWidth : 262;
        ActivePanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() => UpdateProjectScrollVisuals(ActiveProjectScroll)), DispatcherPriority.Loaded);
        SlideTo(ActivePanelTransform, -w, 0);
        SlideTo(ArchivePanelTransform, 0, w, () => ArchivePanel.Visibility = Visibility.Collapsed);

        HeaderTitle.Text = "프로젝트";
        ArchiveToggleBtn.Tag = null;
        ActiveProjectFilterRow.Visibility = Visibility.Visible;
    }

    private static void SlideTo(System.Windows.Media.TranslateTransform t, double from, double to, Action? done = null)
    {
        var anim = new DoubleAnimation
        {
            From = from, To = to,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        if (done != null) anim.Completed += (_, _) => done();
        t.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }

    private void ArchiveProject_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) ProjectArchiveRequested?.Invoke(p);
    }

    private void UnarchiveProject_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) ProjectUnarchiveRequested?.Invoke(p);
    }

    /// <summary>하단 업데이트 버튼 클릭 — 설치 흐름은 MainWindow 에 위임.</summary>
    public event Action? UpdateClicked;
    /// <summary>하단 Devez Vibe 업데이트 버튼 클릭.</summary>
    public event Action? DevezVibeUpdateClicked;
    private bool _devezVibeUpdateButton;
    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_devezVibeUpdateButton) DevezVibeUpdateClicked?.Invoke();
        else UpdateClicked?.Invoke();
    }

    /// <summary>좌측 패널 하단에 "업데이트 v{version}" 버튼 표시(devez 정합).</summary>
    public void ShowUpdateButton(string version)
    {
        _devezVibeUpdateButton = false;
        UpdateButton.Tag = version;
        UpdateButton.Content = $"업데이트 v{version}";
        UpdateButton.Visibility = Visibility.Visible;
    }

    /// <summary>Devez Vibe 세션 종료·업데이트·복원 흐름을 시작하는 버튼 표시.</summary>
    public void ShowDevezVibeUpdateButton()
    {
        _devezVibeUpdateButton = true;
        UpdateButton.Content = "Devez Vibe 업데이트";
        UpdateButton.Visibility = Visibility.Visible;
    }

    /// <summary>Devez Vibe 업데이트 버튼을 표시하거나 숨긴다.</summary>
    public void ToggleDevezVibeUpdateButton()
    {
        if (_devezVibeUpdateButton && UpdateButton.Visibility == Visibility.Visible)
            HideUpdateButton();
        else
            ShowDevezVibeUpdateButton();
    }

    /// <summary>업데이트 버튼 숨김(설치 진행 중 등).</summary>
    public void HideUpdateButton()
    {
        _devezVibeUpdateButton = false;
        UpdateButton.Visibility = Visibility.Collapsed;
    }


    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner == null) return;

        var kind = ProjectAddDialog.Pick(owner);
        if (kind == ProjectAddKind.Project)
        {
            var existing = _archiveOpen ? Projects.ToHashSet() : null;
            AddProjectRequested?.Invoke();
            if (_archiveOpen && existing != null)
            {
                var added = Projects.FirstOrDefault(project => !existing.Contains(project));
                if (added != null) ProjectArchiveRequested?.Invoke(added);
            }
            return;
        }

        if (kind != ProjectAddKind.Folder) return;

        var folder = new ProjectFolderItem
        {
            Name = NextProjectFolderName(),
            ArchivedAt = _archiveOpen ? DateTime.UtcNow.ToString("o") : null,
        };
        WorkspaceStore.ProjectFolders.Add(folder);
        RefreshProjectGroups();
        WorkspaceStore.Save(Projects, ArchivedProjects);
    }
    private static string NextProjectFolderName()
    {
        const string baseName = "새 폴더";
        var names = WorkspaceStore.ProjectFolders
            .Select(folder => folder.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;

        for (int suffix = 1; ; suffix++)
        {
            var candidate = $"{baseName} ({suffix})";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    // ── 검색 행 토글 (돋보기 버튼) — Height 0↔43 애니메이션으로 프로젝트 카드를 아래로 밀어냄 ──
    private bool _searchOpen;
    private const double SearchRowHeight = 47; // 8(margin-top) + 35(pill) + 4(margin-bottom)

    private void SearchToggleBtn_Click(object sender, RoutedEventArgs e)
        => SetSidebarSearchOpen(!_searchOpen);

    private void SetSidebarSearchOpen(bool open)
    {
        _searchOpen = open;
        SearchToggleBtn.Tag = _searchOpen ? "active" : null;
        var anim = new DoubleAnimation
        {
            To = _searchOpen ? SearchRowHeight : 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        SearchRow.BeginAnimation(FrameworkElement.HeightProperty, anim);
        // 닫힌 검색창이 포커스를 쥔 채 입력을 받지 않도록 비활성화하고 필터도 해제한다.
        SidebarSearchBox.IsEnabled = _searchOpen;
        if (!_searchOpen && SidebarSearchBox.Text.Length > 0) SidebarSearchBox.Clear();

        if (_searchOpen)
        {
            // 펼친 직후 포커스 + 기존 텍스트 전체 선택 (연속 재검색 편의)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SidebarSearchBox.Focus();
                SidebarSearchBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
        else TerminalFocusRestoreRequested?.Invoke();
    }

    // ── 모두 펼치기 / 접기 (devez 정합) ──────────────────────────
    private void ToggleExpandAll_Click(object sender, RoutedEventArgs e)
    {
        bool target = !AreAllExpanded();
        foreach (var project in CurrentProjects) project.IsExpanded = target;
        foreach (var folder in CurrentProjectFolders) folder.IsExpanded = target;
        UpdateExpandAllVisual();
        ProjectExpandChanged?.Invoke();
    }

    private IEnumerable<ProjectFolderItem> CurrentProjectFolders =>
        _archiveOpen ? _archivedProjectFolders : _activeProjectFolders;

    private bool AreAllExpanded()
    {
        var folders = CurrentProjectFolders.ToList();
        if (CurrentProjects.Count == 0 && folders.Count == 0) return false;
        return CurrentProjects.All(project => project.IsExpanded) &&
               folders.All(folder => folder.IsExpanded);
    }

    /// <summary>일괄 버튼 아이콘/툴팁 갱신. 외부(프로젝트 로드 후)에서도 호출.</summary>
    public void UpdateExpandAllVisual()
    {
        if (ToggleExpandAllBtn == null || ToggleExpandAllIcon?.RenderTransform is not RotateTransform rt) return;
        bool all = AreAllExpanded();
        ToggleExpandAllBtn.ToolTip = all ? "모두 접기" : "모두 펼치기";
        rt.Angle = all ? -90 : 90;
    }

    // ── 검색 (프로젝트/세션 이름 필터) — devez 정합 ──────────────
    private void SidebarSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (HasActiveDrag)
        {
            _sidebarSearchDebounce.Stop();
            _sidebarSearchDeferredForDrag = true;
            return;
        }

        // devez PanelSearch와 동일: 지우기는 즉시 반영하고, 입력은 멈춘 뒤 180ms 후 검색한다.
        if (string.IsNullOrEmpty(SidebarSearchBox.Text))
        {
            _sidebarSearchDebounce.Stop();
            ApplySidebarSearch();
            return;
        }

        _sidebarSearchDebounce.Stop();
        _sidebarSearchDebounce.Start();
    }

    private void ApplySidebarSearch()
    {
        _sidebarSearchQuery = SidebarSearchBox.Text?.Trim() ?? "";
        SearchHighlightQuery = _sidebarSearchQuery;
        RefreshProjectGroups();
    }

    private void ActiveProjectFilterSwitch_Click(object sender, RoutedEventArgs e)
    {
        _showActiveProjectsOnly = !_showActiveProjectsOnly;
        ActiveProjectFilterRow.Tag = _showActiveProjectsOnly ? "active" : null;
        RefreshProjectGroups();
    }

    private void SidebarSearchClear_Click(object sender, RoutedEventArgs e)
    {
        SidebarSearchBox.Clear();
        SidebarSearchBox.Focus();
    }

    /// <summary>검색창 Esc: 검색어가 있으면 먼저 지우고, 비어 있으면 검색 행을 접는다.</summary>
    private void SidebarSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (!string.IsNullOrEmpty(SidebarSearchBox.Text))
        {
            SidebarSearchBox.Clear();
        }
        else
        {
            SetSidebarSearchOpen(false);
            if (Window.GetWindow(this) is { } window)
            {
                FocusManager.SetFocusedElement(window, window);
                Keyboard.Focus(window);
            }
        }

        e.Handled = true;
    }

    private void ProjectScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateProjectScrollVisuals((ScrollViewer)sender);
        SyncDragScrollOrigin();
    }

    /// <summary>드래그 중 목록이 스크롤된 만큼 캡처 좌표 기준을 옮긴다.
    /// 앵커(마지막 반영 오프셋) 비교라 여러 번 호출돼도 중복 적용되지 않는다.
    /// (ScrollChanged 는 버블링이라 카드 내부 스크롤도 올라오지만, 여기서는 목록 자신의
    /// VerticalOffset 만 보므로 무해하게 no-op 된다.)</summary>
    private void SyncDragScrollOrigin()
    {
        if (_rootDrag == null && _projectDrag == null) return;
        double offset = CurrentProjectScroll.VerticalOffset;
        double delta = offset - _dragScrollAnchor;
        if (Math.Abs(delta) < 0.01) return;
        _dragScrollAnchor = offset;
        _rootDrag?.ShiftCapturedOrigin(0, -delta);
        _projectDrag?.ShiftCapturedOrigin(0, -delta);
    }

    private void UpdateProjectScrollVisuals(ScrollViewer sv)
    {
        // 상하 여백은 ItemsControl Margin 이라 콘텐츠와 함께 스크롤된다.
        // 0.5px 여유로 서브픽셀 오프셋에 의한 페이드 깜빡임 방지.
        bool top = sv.VerticalOffset > 0.5;
        bool bottom = sv.VerticalOffset < sv.ScrollableHeight - 0.5;
        ProjectFadeTop.Visibility = top ? Visibility.Visible : Visibility.Collapsed;
        ProjectFadeBottom.Visibility = bottom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Project_Click(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) { _didDrag = false; return; } // 드래그 직후의 클릭은 무시
        // 행 클릭은 '선택'만 — 접고/펴기는 우측 chevron 버튼 전용
        if (sender is FrameworkElement { DataContext: ProjectItem p })
        {
            ClearSessionMultiSelection();
            ProjectSelected?.Invoke(p);
        }
    }

    private void ProjectChevron_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) p.IsExpanded = !p.IsExpanded;
        UpdateExpandAllVisual(); // 개별 토글도 일괄 버튼 상태에 반영
        ProjectExpandChanged?.Invoke();
        e.Handled = true; // 행 선택으로 버블링 방지
    }
    private void SessionChevron_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is not { HasSessionChildren: true } session) return;
        session.AreSessionChildrenExpanded = !session.AreSessionChildrenExpanded;
        ProjectExpandChanged?.Invoke();
        e.Handled = true;
    }
    private void ToggleProjectFolder(ProjectFolderItem folder)
    {
        folder.IsExpanded = !folder.IsExpanded;
        if (!folder.IsExpanded)
            foreach (var project in Projects.Concat(ArchivedProjects)
                                            .Where(project => project.FolderId == folder.Id))
                project.IsExpanded = false;

        UpdateExpandAllVisual();
        ProjectExpandChanged?.Invoke();
    }

    private void FolderHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) { _didDrag = false; return; }
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if ((sender as FrameworkElement)?.DataContext is not ProjectFolderItem folder) return;
        ToggleProjectFolder(folder);
    }

    private void FolderChevron_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFolderItem>(sender) is not { } folder) return;
        ToggleProjectFolder(folder);
        e.Handled = true;
    }

    // 폴더 내부 목록 1열↔2열 전환. 2열로 펼 때는 갯수 절반을 우측으로(홀수면 좌가 더 많게),
    // 1열로 접을 때는 우측 항목을 좌측 아래로 모은다. 전역 열 수가 2일 때만 동작.
    private void FolderColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf<ProjectFolderItem>(sender) is not { } folder || !folder.ColumnToggleAvailable) return;
        bool toTwo = !folder.TwoColumn;
        RedistributeFolderColumns(folder, toTwo);
        folder.TwoColumn = toTwo; // EffectiveColumns 재계산 → 내부 패널 재배치
        InvalidateRootColumns(folder.IsArchived); // 외곽 패널은 폴더 폭(전체/반) 변경을 관찰 못 하므로 수동 무효화
        WorkspaceStore.Save(Projects, ArchivedProjects);
    }

    // 루트 목록 패널 재측정 — 폴더의 EffectiveColumns 변화(전체폭↔반폭 카드)를 반영시킨다.
    private void InvalidateRootColumns(bool archived)
    {
        foreach (var panel in FindVisualChildren<ProjectColumnsPanel>(archived ? ArchivedHost : ProjectsHost))
        {
            panel.AnimateNextLayout();
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    private void RedistributeFolderColumns(ProjectFolderItem folder, bool toTwo)
    {
        var coll = folder.IsArchived ? ArchivedProjects : Projects;
        var members = coll.Where(project => project.FolderId == folder.Id).ToList();
        if (members.Count == 0) return;

        if (toTwo)
        {
            // 갯수 기준 앞 절반(ceil)=좌, 나머지=우. 컬렉션 순서 유지(앞=좌 위→아래, 뒤=우).
            int left = (members.Count + 1) / 2;
            for (int i = 0; i < members.Count; i++)
                members[i].Column = i < left ? 0 : 1;
        }
        else
        {
            // 좌 컬럼 항목 먼저, 그 아래로 우 컬럼 항목을 모아 단일 열로 재배치.
            var ordered = members.Where(project => project.Column != 1)
                                 .Concat(members.Where(project => project.Column == 1))
                                 .ToList();
            foreach (var project in ordered) project.Column = 0;

            var desired = coll.ToList();
            int idx = 0;
            for (int i = 0; i < desired.Count; i++)
                if (desired[i].FolderId == folder.Id)
                    desired[i] = ordered[idx++];
            SyncCollection(coll, desired);
        }
    }

    private void FolderRename_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFolderItem>(sender) is not { } folder) return;
        var name = PromptDialog.Show("폴더 이름 변경", "새 이름을 입력하세요.",
                                     defaultValue: folder.Name, maxLength: 60);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == folder.Name) return;
        folder.Name = name.Trim();
        WorkspaceStore.Save(Projects, ArchivedProjects);
    }
    private void FolderIconChange_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFolderItem>(sender) is not { } folder) return;
        var owner = Window.GetWindow(this);
        if (owner == null) return;

        var iconKey = FolderIconPickerDialog.Pick(owner, folder.IconKey, folder.Name);
        if (iconKey == null || iconKey == folder.IconKey) return;

        folder.IconKey = iconKey;
        WorkspaceStore.Save(Projects, ArchivedProjects);
    }

    private void FolderArchive_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFolderItem>(sender) is not { IsActive: true } folder) return;
        MoveProjectFolder(folder, archive: true);
    }

    private void FolderUnarchive_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFolderItem>(sender) is not { IsArchived: true } folder) return;
        MoveProjectFolder(folder, archive: false);
    }

    private void MoveProjectFolder(ProjectFolderItem folder, bool archive)
    {
        var source = archive ? Projects : ArchivedProjects;
        var projects = source.Where(project => project.FolderId == folder.Id).ToList();
        _movingProjectFolder = true;
        try
        {
            folder.ArchivedAt = archive ? DateTime.UtcNow.ToString("o") : null;
            foreach (var project in projects)
            {
                if (archive) ProjectArchiveRequested?.Invoke(project);
                else ProjectUnarchiveRequested?.Invoke(project);
            }
        }
        finally
        {
            _movingProjectFolder = false;
        }

        RefreshProjectGroups();
        WorkspaceStore.Save(Projects, ArchivedProjects);
    }

    private void FolderDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFolderItem>(sender) is not { } folder) return;
        if (!ConfirmDialog.Show("폴더 제거",
                $"'{folder.Name}' 폴더를 제거할까요?\n프로젝트는 폴더 밖의 프로젝트 영역으로 이동합니다.",
                okLabel: "제거", danger: true))
            return;

        foreach (var project in Projects.Concat(ArchivedProjects)
                                        .Where(project => project.FolderId == folder.Id))
            project.FolderId = null;
        WorkspaceStore.ProjectFolders.Remove(folder);
        RefreshProjectGroups();
        WorkspaceStore.Save(Projects, ArchivedProjects);
    }

    private void RemoveProjectFromFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is not { } project || project.FolderId == null) return;

        var folder = WorkspaceStore.ProjectFolders.FirstOrDefault(item => item.Id == project.FolderId);
        if (folder == null) return;

        project.FolderId = null;
        // 폴더 바로 아래로 꺼내려면 루트 컬럼도 폴더와 같은 열로 맞춘다(안 맞추면 옛 Column=좌측 맨위로 떨어짐).
        // 전체폭(2열) 폴더는 좌우 Y가 같아지므로 좌열(0)에 놓으면 바로 아래가 된다.
        project.Column = folder.EffectiveColumns >= 2 ? 0 : folder.Column;
        var roots = GetAllRootItems(folder.IsArchived);
        roots.Remove(project);
        int folderIndex = roots.IndexOf(folder);
        roots.Insert(folderIndex >= 0 ? folderIndex + 1 : roots.Count, project);
        ApplyRootOrder(roots);
        SyncUngroupedProjectCollection(folder.IsArchived ? ArchivedProjects : Projects, roots);
        RefreshProjectGroups();
        ProjectsReordered?.Invoke();
    }

    private static void SyncUngroupedProjectCollection(
        ObservableCollection<ProjectItem> collection,
        IReadOnlyList<object> roots)
    {
        var orderedProjects = roots.OfType<ProjectItem>().ToList();
        var desired = collection.ToList();
        int projectIndex = 0;
        for (int i = 0; i < desired.Count && projectIndex < orderedProjects.Count; i++)
            if (desired[i].FolderId == null)
                desired[i] = orderedProjects[projectIndex++];

        SyncCollection(collection, desired);
    }

    private void HiddenToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p)
        {
            p.ShowHiddenSessions = !p.ShowHiddenSessions;
            if (sender is Button btn)
                btn.ToolTip = p.ShowHiddenSessions ? "숨김 세션 숨기기" : "숨김 세션 표시";
            HiddenSessionVisibilityChanged?.Invoke();
    }
        e.Handled = true;
    }

    // ── 숨김 세션 그룹: 10개 초과 시 정확히 10행 높이로 제한 + 상하 페이드(완료기록 영역과 동일 방식) ──
    private void HiddenScroll_Loaded(object sender, RoutedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        UpdateHiddenScrollHeight(sv);
        UpdateHiddenScrollFade(sv);
    }

    private void HiddenScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            UpdateHiddenScrollHeight(sv);
        UpdateHiddenScrollFade(sv);

        // 행이 화면에서 밀린 시점 기록 — 클릭 직전 재배치였는지(누른 행 자체가 이미 어긋났는지) 대조용.
        if (e.VerticalChange != 0 || e.ExtentHeightChange != 0)
        {
            _hiddenGroupShiftTick = Environment.TickCount;
            DiagLog.Write(
                $"HIDDEN-GROUP-SHIFT: dOffset={e.VerticalChange:0.#} dExtent={e.ExtentHeightChange:0.#} "
                + $"offset={e.VerticalOffset:0.#} extent={e.ExtentHeight:0.#}");
        }
    }

    /// <summary>10개 이하는 제한 없음, 초과면 앞 10개 행의 실제 높이 합으로 MaxHeight 고정.</summary>
    private static void UpdateHiddenScrollHeight(ScrollViewer sv)
    {
        if (sv.Content is not ItemsControl ic) return;
        if (ic.Items.Count <= 10)
        {
            if (!double.IsPositiveInfinity(sv.MaxHeight)) sv.MaxHeight = double.PositiveInfinity;
            return;
        }
        double h = 0; int counted = 0;
        for (int i = 0; i < ic.Items.Count && counted < 10; i++)
        {
            if (ic.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe && fe.ActualHeight > 0)
            {
                h += fe.ActualHeight;
                counted++;
            }
        }
        if (counted == 10 && h > 0 && System.Math.Abs(sv.MaxHeight - h) > 0.5) sv.MaxHeight = h;
    }

    private static void UpdateHiddenScrollFade(ScrollViewer sv)
    {
        if (sv.Parent is not Grid g) return;
        const double tol = 1.0;
        bool scrollable = sv.ScrollableHeight > tol;
        bool top = scrollable && sv.VerticalOffset > tol;
        bool bottom = scrollable && sv.VerticalOffset < sv.ScrollableHeight - tol;
        foreach (var r in g.Children.OfType<Rectangle>())
        {
            if (r.VerticalAlignment == VerticalAlignment.Top)
                r.Visibility = top ? Visibility.Visible : Visibility.Collapsed;
            else if (r.VerticalAlignment == VerticalAlignment.Bottom)
                r.Visibility = bottom ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private const double ProjectSessionSearchRowHeight = 37;
    private ProjectItem? _openProjectSessionSearch;

    private void ProjectSessionSearchToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: ProjectItem project } button) return;

        var card = FindVisualAncestorByName<Border>(button, "ProjectCardRoot");
        bool open = !project.IsSessionSearchOpen;
        if (open && _openProjectSessionSearch != null &&
            !ReferenceEquals(_openProjectSessionSearch, project))
            SetProjectSessionSearchOpen(_openProjectSessionSearch, false);

        SetProjectSessionSearchOpen(project, open, card);
    }

    private void SetProjectSessionSearchOpen(ProjectItem project, bool open, Border? card = null)
    {
        card ??= FindVisualChildren<Border>(this)
            .FirstOrDefault(item => item.Name == "ProjectCardRoot" &&
                                    ReferenceEquals(item.DataContext, project));
        var row = FindVisualChildren<Border>(card)
            .FirstOrDefault(item => item.Name == "ProjectSessionSearchRow");
        var searchBox = FindVisualChildren<TextBox>(card)
            .FirstOrDefault(item => item.Name == "ProjectSessionSearchBox");
        double from = row?.ActualHeight ?? (project.IsSessionSearchOpen ? ProjectSessionSearchRowHeight : 0);

        project.IsSessionSearchOpen = open;
        if (open)
            _openProjectSessionSearch = project;
        else
        {
            project.SessionSearchQuery = "";
            if (ReferenceEquals(_openProjectSessionSearch, project))
                _openProjectSessionSearch = null;
        }

        if (open && !project.IsExpanded)
        {
            project.IsExpanded = true;
            ProjectExpandChanged?.Invoke();
        }

        if (row != null)
        {
            row.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation
            {
                From = from,
                To = open ? ProjectSessionSearchRowHeight : 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        if (open && searchBox != null)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!project.IsSessionSearchOpen) return;
                searchBox.Focus();
                searchBox.SelectAll();
            }), DispatcherPriority.Input);
        else TerminalFocusRestoreRequested?.Invoke();
    }

    private void ProjectSessionSearchClear_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: ProjectItem project } button) return;
        project.SessionSearchQuery = "";

        var card = FindVisualAncestorByName<Border>(button, "ProjectCardRoot");
        FindVisualChildren<TextBox>(card)
            .FirstOrDefault(item => item.Name == "ProjectSessionSearchBox")?.Focus();
    }

    /// <summary>세로형 ... 버튼 — 행에 정의된 우클릭 메뉴를 버튼 위치에 띄운다.</summary>
    private void ProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 행 선택으로 버블링 방지
        if (sender is not Button btn) return;
        // 조상 Border(트리 행)의 ContextMenu를 찾아 버튼 기준으로 표시
        for (DependencyObject? d = btn; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Border { ContextMenu: { } cm })
            {
                cm.PlacementTarget = btn;
                cm.Placement = PlacementMode.Bottom;
                cm.IsOpen = true;
                return;
            }
    }

    private SessionItem? _sessionSelectionAnchor;
    private ProjectItem? _sessionSelectionProject;

    private void Sidebar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsWithinSessionRow(e.OriginalSource as DependencyObject))
            ClearSessionMultiSelection();
    }

    private void Sidebar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _sessionSelectionProject == null) return;
        ClearSessionMultiSelection();
        e.Handled = true;
    }

    private static bool IsWithinSessionRow(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is Border { Name: "SessionRow" }) return true;
            current = current switch
            {
                FrameworkContentElement content => content.Parent,
                Visual _ => VisualTreeHelper.GetParent(current),
                System.Windows.Media.Media3D.Visual3D _ => VisualTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current),
            };
        }
        return false;
    }

    private ProjectItem? SessionProject(SessionItem session)
        => CurrentProjects.FirstOrDefault(project => project.Sessions.Contains(session));

    private void Session_Click(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) { _didDrag = false; _pressedSession = null; return; }
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (sender is not FrameworkElement { DataContext: SessionItem upSession }) return;

        // MouseUp 은 '놓는 순간 커서 아래 행'으로 라우팅된다(행에 마우스 캡처가 없다).
        // 누른 뒤 목록이 재배치되면(숨김 그룹 재동기화, 리스트 높이 변화, 스크롤 이동)
        // 누르지 않은 세션이 활성화되므로, 버튼처럼 '누른 행'을 기준으로 삼는다.
        var session = ResolvePressedSession(upSession);

        var modifiers = Keyboard.Modifiers;
        bool control = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        if (shift)
        {
            SelectSessionRange(session, additive: control);
            e.Handled = true;
            return;
        }
        if (control)
        {
            SetSessionMultiSelected(session, !session.IsMultiSelected);
            _sessionSelectionAnchor = session;
            e.Handled = true;
            return;
        }

        // 파일 탐색기처럼 보조키 없는 클릭은 기존 다중 선택을 해제하고 클릭한 항목을 기준점으로 삼는다.
        ClearSessionMultiSelection();
        _sessionSelectionAnchor = session;

        // 숨김 세션 활성화만 기록(빈도 낮음) — 엉뚱한 세션이 켜졌다는 제보와 대조.
        if (session.Hidden)
            DiagLog.Write($"HIDDEN-SESSION-ACTIVATE: '{session.Name}' "
                + $"sinceHiddenShift={unchecked(Environment.TickCount - _hiddenGroupShiftTick)}ms");

        // 비선택 프로젝트 세션 클릭도 OpenSession 이 프로젝트 전환까지 처리.
        // (ProjectSelected 를 따로 호출하면 첫 세션이 추가로 로드되므로 호출하지 않음)
        SessionSelected?.Invoke(session);
    }

    private void SessionMultiSelect_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is not { } session) return;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Shift) != 0)
            SelectSessionRange(session, additive: (modifiers & ModifierKeys.Control) != 0);
        else
        {
            _sessionSelectionAnchor = session;
            UpdateSessionMultiSelectMode();
        }
        e.Handled = true;
    }

    private void SetSessionMultiSelected(SessionItem session, bool selected)
    {
        var project = SessionProject(session);
        if (project == null) return;
        if (selected && _sessionSelectionProject != null
            && !ReferenceEquals(_sessionSelectionProject, project))
            ClearSessionMultiSelection();

        session.IsMultiSelected = selected;
        _sessionSelectionProject = selected
            ? project
            : project.Sessions.Any(item => item.IsMultiSelected) ? project : null;
        UpdateSessionMultiSelectMode();
    }

    private void SelectSessionRange(SessionItem target, bool additive)
    {
        var project = SessionProject(target);
        if (project == null) return;
        if (_sessionSelectionProject != null && !ReferenceEquals(_sessionSelectionProject, project))
            ClearSessionMultiSelection();

        var anchor = _sessionSelectionAnchor;
        if (anchor == null || !project.Sessions.Contains(anchor))
            anchor = target;
        var ordered = VisibleSessionOrder(project);
        int anchorIndex = ordered.IndexOf(anchor);
        int targetIndex = ordered.IndexOf(target);

        if (!additive)
            ClearSessionMultiSelection(clearAnchor: false);

        if (anchorIndex < 0 || targetIndex < 0)
        {
            target.IsMultiSelected = true;
        }
        else
        {
            int first = Math.Min(anchorIndex, targetIndex);
            int last = Math.Max(anchorIndex, targetIndex);
            for (int i = first; i <= last; i++)
                ordered[i].IsMultiSelected = true;
        }

        _sessionSelectionProject = project;
        _sessionSelectionAnchor ??= target;
        UpdateSessionMultiSelectMode();
    }

    private List<SessionItem> VisibleSessionOrder(ProjectItem project)
    {
        var root = _archiveOpen ? (DependencyObject)ArchivePanel : ActivePanel;
        var rows = new List<(SessionItem Session, Point Position)>();
        var seen = new HashSet<SessionItem>();
        foreach (var border in FindVisualChildren<Border>(root))
        {
            if (border.Name != "SessionRow" || !border.IsVisible
                || border.DataContext is not SessionItem session
                || !project.Sessions.Contains(session) || !seen.Add(session))
                continue;
            try
            {
                rows.Add((session, border.TransformToAncestor(this).Transform(new Point(0, 0))));
            }
            catch (InvalidOperationException)
            {
                // 레이아웃 갱신 중 트리에서 빠진 행은 현재 범위 선택 대상이 아니다.
            }
        }

        return rows.OrderBy(row => row.Position.Y)
            .ThenBy(row => row.Position.X)
            .Select(row => row.Session)
            .ToList();
    }

    private void UpdateSessionMultiSelectMode()
    {
        var sessions = CurrentProjects.SelectMany(project => project.Sessions).ToList();
        var selected = sessions.Where(item => item.IsMultiSelected).ToList();
        _sessionSelectionProject = selected.Count == 0 ? null : SessionProject(selected[0]);
        foreach (var item in sessions)
            item.IsMultiSelectMode = _sessionSelectionProject?.Sessions.Contains(item) == true;

        SelectionCountText.Text = $"{selected.Count}개 선택됨";
        SelectionCountText.Visibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderTitle.Visibility = selected.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private IReadOnlyList<SessionItem> SessionActionTargets(object sender)
    {
        if (ItemOf<SessionItem>(sender) is not { } target) return Array.Empty<SessionItem>();
        if (!target.IsMultiSelected) return new[] { target };
        var project = SessionProject(target);
        if (project == null) return Array.Empty<SessionItem>();
        return project.Sessions.Where(session => session.IsMultiSelected).ToList();
    }

    public bool HasSessionMultiSelection => _sessionSelectionProject != null;
    public void ClearSessionMultiSelection(bool clearAnchor = true)
    {
        foreach (var session in Projects.Concat(ArchivedProjects).SelectMany(project => project.Sessions))
        {
            session.IsMultiSelected = false;
            session.IsMultiSelectMode = false;
        }
        if (clearAnchor)
            _sessionSelectionAnchor = null;
        _sessionSelectionProject = null;
        SelectionCountText.Visibility = Visibility.Collapsed;
        HeaderTitle.Visibility = Visibility.Visible;
    }

    private void OpenDoc_Click(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) { _didDrag = false; return; }
        if (sender is FrameworkElement { DataContext: FileTabItem f })
            OpenDocSelected?.Invoke(f);
    }

    private void OpenDocClose_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<FileTabItem>(sender) is { } f) OpenDocCloseRequested?.Invoke(f);
    }

    private void OpenDocCloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<FileTabItem>(sender) is { } f) OpenDocCloseOthersRequested?.Invoke(f);
    }

    private ProjectItem? ProjectForDocument(FileTabItem file)
        => CurrentProjects.FirstOrDefault(project => project.Tabs.Contains(file));

    private ProjectItem? ProjectForDocumentGroup(DocumentGroupItem group)
        => CurrentProjects.FirstOrDefault(project => project.DocumentGroups.Contains(group));

    private void DocumentGroupCreate_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<FileTabItem>(sender) is not { } file
            || ProjectForDocument(file) is not { } project)
            return;

        var name = PromptDialog.Show("문서 그룹 만들기", "그룹 이름을 입력하세요.", "문서 그룹", maxLength: 60);
        if (name == null || project.CreateDocumentGroup(file, name) == null) return;
        SessionsReordered?.Invoke(project);
    }

    private sealed record DocumentGroupAddTarget(
        ProjectItem Project,
        DocumentGroupItem Group,
        FileTabItem File);

    private void DocumentGroupAddMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DocumentGroupAddTarget target }
            || !target.Project.AddDocumentToGroup(target.Group, target.File))
            return;
        e.Handled = true;
        SessionsReordered?.Invoke(target.Project);
    }

    private void DocumentGroupAdd_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: FileTabItem file } addItem)
            PopulateDocumentGroupItems(addItem, file);
    }

    private void DocumentGroupMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var group = menu.DataContext as DocumentGroupItem
                    ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as DocumentGroupItem;
        var project = group == null ? null : ProjectForDocumentGroup(group);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            bool? before = item.CommandParameter switch
            {
                "Before" => true,
                "After" => false,
                _ => null,
            };
            if (before.HasValue)
                item.IsEnabled = project != null
                                 && project.CountDocumentsOnGroupSide(group!, before.Value) > 0;
        }
    }

    private void DocumentGroupAddSide_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<DocumentGroupItem>(sender) is not { } group
            || ProjectForDocumentGroup(group) is not { } project)
            return;
        bool before = sender is MenuItem { CommandParameter: "Before" };
        if (project.AddDocumentsOnGroupSide(group, before) == 0) return;
        SessionsReordered?.Invoke(project);
    }

    private void DocumentGroupRemoveDocument_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<FileTabItem>(sender) is not { } file
            || ProjectForDocument(file) is not { } project
            || !project.RemoveDocumentFromGroup(file))
            return;
        SessionsReordered?.Invoke(project);
    }

    private void DocumentGroup_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FileTabItem }
            or FrameworkContentElement { DataContext: FileTabItem })
            return;
        if (_didDrag) { _didDrag = false; return; }
        if (sender is not FrameworkElement { DataContext: DocumentGroupItem group }
            || ProjectForDocumentGroup(group) is not { } project)
            return;
        group.IsExpanded = !group.IsExpanded;
        SessionsReordered?.Invoke(project);
    }

    private void DocumentGroupRename_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<DocumentGroupItem>(sender) is not { } group
            || ProjectForDocumentGroup(group) is not { } project)
            return;
        var name = PromptDialog.Show("문서 그룹 이름 변경", "새 이름을 입력하세요.",
            group.Name, maxLength: 60);
        if (name == null || StringComparer.Ordinal.Equals(name, group.Name)) return;
        group.Name = name;
        SessionsReordered?.Invoke(project);
    }

    private void DocumentGroupRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<DocumentGroupItem>(sender) is not { } group
            || ProjectForDocumentGroup(group) is not { } project
            || !project.RemoveDocumentGroup(group))
            return;
        SessionsReordered?.Invoke(project);
    }

    private void BrowserTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) { _didDrag = false; return; }
        if (sender is FrameworkElement { DataContext: BrowserTabItem browser })
            BrowserTabSelected?.Invoke(browser);
    }

    private void BrowserTabClose_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<BrowserTabItem>(sender) is { } browser)
            BrowserTabCloseRequested?.Invoke(browser);
    }

    private void BrowserTabOpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<BrowserTabItem>(sender) is not { } browser) return;
        if (browser.Browser.TryOpenInDefaultBrowser(browser.PersistenceKey)) return;
        ConfirmDialog.Alert("기본 브라우저로 열기", "열 수 있는 웹 주소가 없습니다.",
            iconKey: "IconTriangleAlert");
    }

    private void BrowserTabCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<BrowserTabItem>(sender) is not { } browser) return;
        if (browser.Browser.TryCopyCurrentUrl(browser.PersistenceKey)) return;
        ConfirmDialog.Alert("현재 URL 복사", "URL을 클립보드에 복사하지 못했습니다.",
            iconKey: "IconTriangleAlert");
    }

    private void BrowserTabRename_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<BrowserTabItem>(sender) is { } browser)
            BrowserTabRenameRequested?.Invoke(browser);
    }

    private void OpenDocCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<FileTabItem>(sender) is { } f)
            try { Clipboard.SetText(f.FilePath); } catch { }
    }


    private void AddSession_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) AddSessionRequested?.Invoke(p);
    }

    private void ProjectDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) ProjectDeleteRequested?.Invoke(p);
    }

    private void SessionManager_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) SessionManagerRequested?.Invoke(p);
    }

    private void ProjectRename_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) ProjectRenameRequested?.Invoke(p);
    }

    private void ProjectMarker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || ItemOf<ProjectItem>(item) is not { } project) return;

        var marker = item.CommandParameter as string;
        project.MarkerColor = string.Equals(marker, ProjectMarkerPalette.None, StringComparison.Ordinal)
            ? null
            : marker;

        if (ItemsControl.ItemsControlFromItemContainer(item) is MenuItem parent)
            UpdateProjectMarkerChecks(parent, project);

        WorkspaceStore.Save(Projects, ArchivedProjects);
    }

    private static void UpdateProjectMarkerChecks(MenuItem markerMenu, ProjectItem project)
    {
        foreach (var option in markerMenu.Items.OfType<MenuItem>())
        {
            var marker = option.CommandParameter as string;
            option.IsChecked = string.Equals(marker, ProjectMarkerPalette.None, StringComparison.Ordinal)
                ? !project.HasMarker
                : string.Equals(marker, project.MarkerColor, StringComparison.Ordinal);
        }
    }

    private void AddProjectFile_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) AddProjectFileRequested?.Invoke(p);
    }

    /// <summary>프로젝트 카드 우클릭 → 디렉토리 열기 — 탐색기에서 프로젝트 폴더를 연다.</summary>
    private void OpenProjectDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is not { } p) return;
        if (string.IsNullOrEmpty(p.Path) || !System.IO.Directory.Exists(p.Path))
        {
            ConfirmDialog.Alert("디렉토리 열기", "대상 디렉토리를 찾을 수 없습니다.");
            return;
        }
        try { System.Diagnostics.Process.Start("explorer.exe", p.Path); }
        catch { ConfirmDialog.Alert("디렉토리 열기", "탐색기를 열 수 없습니다."); }
    }

    private void CopyProjectPath_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is not { } p || string.IsNullOrEmpty(p.Path)) return;
        try { Clipboard.SetText(p.Path); } catch { }
    }

    private void OpenGitRemote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string url } item) return;
        var menu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu;
        var project = menu?.DataContext as ProjectItem
                      ?? (menu?.PlacementTarget as FrameworkElement)?.DataContext as ProjectItem;
        if (project != null) GitRemoteOpenRequested?.Invoke(project, url);
    }

    private void ProjectFileOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFile>(sender) is { } f) ProjectFileSelected?.Invoke(f);
    }

    /// <summary>카드 하단 바로가기 행 클릭 — 대상 실행.</summary>
    private void ProjectFileRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) { _didDrag = false; return; } // 드래그 재정렬은 실행 트리거 아님
        if ((sender as FrameworkElement)?.DataContext is ProjectFile f) ProjectFileSelected?.Invoke(f);
    }

    /// <summary>바로가기 행 우클릭 메뉴 — 제거.</summary>
    private void ProjectFileRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFile>(sender) is { } f) ProjectFileRemoveRequested?.Invoke(f);
    }

    /// <summary>바로가기 행 우클릭 메뉴 — 이름 변경.</summary>
    private void ProjectFileRename_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFile>(sender) is { } f) ProjectFileRenameRequested?.Invoke(f);
    }

    /// <summary>프로젝트 메뉴가 열릴 때 "바로가기" 서브메뉴를 등록 목록 + 맨 아래 "바로가기 추가" 로 선(先)채운다.
    /// 서브메뉴가 펼쳐지기 전(메뉴 오픈 시점)에 항목을 넣어, 열리는 도중 Clear 로 인한 팝업 미표시 버그를 피한다.</summary>
    private async void ProjectMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;

        var p = cm.DataContext as ProjectItem
                ?? (cm.PlacementTarget as FrameworkElement)?.DataContext as ProjectItem;
        if (p is null) return;

        var markerMenu = cm.Items.OfType<MenuItem>()
            .FirstOrDefault(item => Equals(item.CommandParameter, "ProjectMarker"));
        if (markerMenu != null)
            UpdateProjectMarkerChecks(markerMenu, p);

        var gitRemoteItem = cm.Items.OfType<MenuItem>()
            .FirstOrDefault(item => Equals(item.CommandParameter, "GitRemote"));
        if (gitRemoteItem != null)
        {
            gitRemoteItem.IsEnabled = false;
            gitRemoteItem.Tag = null;
            var webUrl = await GitService.GetOriginWebUrlAsync(p.Path);
            var currentProject = cm.DataContext as ProjectItem
                                 ?? (cm.PlacementTarget as FrameworkElement)?.DataContext as ProjectItem;
            if (!ReferenceEquals(currentProject, p)) return;
            gitRemoteItem.Tag = webUrl;
            gitRemoteItem.IsEnabled = webUrl != null;
        }

        var parent = cm.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "바로가기");
        if (parent is null) return;
        // 서브메뉴 헤더 자식의 Tag={Binding} 은 Opened 시점에 아직 평가 안 됐을 수 있어 null 가능 →
        // ContextMenu.DataContext(타겟에서 상속)로 프로젝트를 얻는다. 폴백으로 PlacementTarget·Tag.
        parent.Items.Clear();
        foreach (var f in p.Files)
        {
            var header = f.RunAsAdmin ? $"{f.DisplayName}  (관리자)" : f.DisplayName;
            var item = new MenuItem { Header = header, Tag = f, ToolTip = f.FilePath };
            var icon = FileIconHelper.GetSmallIcon(f.FilePath);
            if (icon != null)
                item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
            item.Click += ProjectFileOpen_Click;
            parent.Items.Add(item);
        }
        parent.Items.Add(new Separator());
        var add = new MenuItem { Header = "바로가기 추가", Tag = p };
        add.Click += AddProjectFile_Click;
        parent.Items.Add(add);
    }

    private void SessionMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        var target = cm.DataContext as SessionItem
                     ?? (cm.PlacementTarget as FrameworkElement)?.DataContext as SessionItem;
        if (target == null) return;

        var targets = target.IsMultiSelected
            ? SessionProject(target)?.Sessions.Where(session => session.IsMultiSelected).ToList() ?? new()
            : new List<SessionItem> { target };
        bool batch = targets.Count > 1;
        bool showCount = target.IsMultiSelected;
        int count = targets.Count;
        var separators = cm.Items.OfType<Separator>().ToList();
        bool anyLocked = targets.Any(session => session.IsLocked);
        bool anyExternal = targets.Any(session => session.IsExternal);
        bool anyUnlocked = targets.Any(session => !session.IsLocked);
        bool anyVisible = targets.Any(session => !session.Hidden);

        foreach (var item in cm.Items.OfType<MenuItem>())
        {
            string action = item.CommandParameter as string ?? "";
            bool destructive = action is "Close" or "Delete";
            item.IsEnabled = action == "External"
                ? !target.IsExternal && !target.IsBusy
                : !destructive || (!anyLocked && !anyExternal);
            item.ToolTip = action == "External"
                ? target.IsExternal
                    ? "외부 터미널에서 실행 중입니다."
                    : target.IsBusy
                        ? "응답이 완료된 후 외부 터미널로 열 수 있습니다."
                        : null
                : item.IsEnabled || !destructive ? null
                    : anyExternal
                        ? "외부 터미널에서 실행 중인 세션이 포함되어 있습니다."
                        : "잠긴 세션이 포함되어 있어 실행할 수 없습니다.";
            if (action == "Single")
            {
                item.Visibility = batch ? Visibility.Collapsed : Visibility.Visible;
                if (!batch && Equals(item.Header, "꺼내기"))
                    item.Visibility = target.HasSessionParent ? Visibility.Visible : Visibility.Collapsed;
                continue;
            }

            item.Visibility = action switch
            {
                "External" => batch ? Visibility.Collapsed : Visibility.Visible,
                "Lock" => (batch ? anyUnlocked : !target.IsLocked)
                    ? Visibility.Visible : Visibility.Collapsed,
                "Unlock" => (batch ? anyLocked : target.IsLocked)
                    ? Visibility.Visible : Visibility.Collapsed,
                "Hide" => (batch ? anyVisible : !target.Hidden)
                    ? Visibility.Visible : Visibility.Collapsed,
                "Shutdown" or "Close" or "Delete" => Visibility.Visible,
                _ => item.Visibility,
            };

            item.Header = action switch
            {
                "External" => target.IsExternal ? "외부 터미널에서 실행 중" : "외부 터미널로 열기",
                "Lock" => showCount ? $"세션 {count}개 잠금" : "세션 잠금",
                "Unlock" => showCount ? $"세션 {count}개 잠금 해제" : "잠금 해제",
                "Hide" => showCount ? $"세션 {count}개 숨기기" : "세션 숨기기",
                "Shutdown" => showCount ? $"세션 {count}개 종료" : "세션 종료",
                "Close" => showCount ? $"세션 {count}개 닫기" : "세션 닫기",
                "Delete" => showCount ? $"세션 {count}개 삭제" : "세션 삭제",
                _ => item.Header,
            };
        }

        if (separators.Count > 0)
            separators[0].Visibility = !batch && target.HasSessionParent
                ? Visibility.Visible : Visibility.Collapsed;
        if (separators.Count > 1)
            separators[1].Visibility = Visibility.Visible;

        UpdateSplitMoveMenu(cm, target, !batch);
    }

    private void TabMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        var target = cm.DataContext as TabItemBase
                     ?? (cm.PlacementTarget as FrameworkElement)?.DataContext as TabItemBase;
        if (target != null) UpdateSplitMoveMenu(cm, target, true);
        if (target is FileTabItem file) UpdateDocumentGroupMenu(cm, file);
    }

    private void UpdateDocumentGroupMenu(ContextMenu menu, FileTabItem file)
    {
        var project = ProjectForDocument(file);
        var currentGroup = project?.DocumentGroupOf(file);
        var createItem = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => Equals(item.CommandParameter, "DocumentGroupCreate"));
        var addItem = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => Equals(item.CommandParameter, "DocumentGroupAdd"));
        var removeItem = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => Equals(item.CommandParameter, "DocumentGroupRemove"));

        var groupVisibility = file.IsDiff ? Visibility.Collapsed : Visibility.Visible;
        if (createItem != null) createItem.Visibility = groupVisibility;
        if (removeItem != null)
            removeItem.Visibility = currentGroup != null && !file.IsDiff
                ? Visibility.Visible : Visibility.Collapsed;
        if (addItem == null) return;

        addItem.Visibility = groupVisibility;
        PopulateDocumentGroupItems(addItem, file);
    }

    private void PopulateDocumentGroupItems(MenuItem addItem, FileTabItem file)
    {
        var project = ProjectForDocument(file);
        var currentGroup = project?.DocumentGroupOf(file);
        addItem.Items.Clear();
        var groups = project?.DocumentGroups
                         .Where(group => !ReferenceEquals(group, currentGroup)
                                         && project.CanAddDocumentToGroup(group, file))
                         .ToList()
                     ?? new List<DocumentGroupItem>();
        foreach (var group in groups)
        {
            var groupItem = new MenuItem
            {
                Header = group.Name,
                Tag = new DocumentGroupAddTarget(project!, group, file),
            };
            groupItem.Click += DocumentGroupAddMenu_Click;
            addItem.Items.Add(groupItem);
        }
        if (groups.Count == 0)
            addItem.Items.Add(new MenuItem { Header = "추가할 그룹 없음", IsEnabled = false });
    }

    private void UpdateSplitMoveMenu(ContextMenu menu, TabItemBase target, bool visible)
    {
        var item = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(candidate => Equals(candidate.CommandParameter, "SplitMove"));
        if (item == null) return;

        item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var presentation = SplitMovePresentationProvider?.Invoke(target)
                           ?? ("분할 보기", "IconPanelLeftOpen");
        item.Header = presentation.Header;
        if (item.Icon is System.Windows.Shapes.Path icon)
            icon.Data = (Geometry)FindResource(presentation.IconKey);
    }

    private void TabSplitMove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<TabItemBase>(sender) is { } tab) SplitMoveRequested?.Invoke(tab);
    }

    private void SessionDetach_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is not { ParentSessionId: not null } session) return;
        var project = CurrentProjects.FirstOrDefault(item => item.Sessions.Contains(session));
        if (project != null && project.DetachSessionAsRoot(session))
            SessionsReordered?.Invoke(project);
    }

    private void SessionDelete_Click(object sender, RoutedEventArgs e)
    {
        var target = ItemOf<SessionItem>(sender);
        var targets = SessionActionTargets(sender);
        if (target?.IsMultiSelected == true)
            SessionsDeleteRequested?.Invoke(targets);
        else if (targets.Count == 1)
            SessionDeleteRequested?.Invoke(targets[0]);
    }

    private void SessionRename_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionRenameRequested?.Invoke(s);
    }

    private void SessionExternal_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { IsExternal: false, IsBusy: false } s)
            SessionExternalRequested?.Invoke(s);
    }

    private void SessionFork_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionForkRequested?.Invoke(s);
    }

    private void SessionCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionCopyIdRequested?.Invoke(s);
    }

    private void SessionExport_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionExportRequested?.Invoke(s);
    }

    private void SessionStopTracking_Click(object sender, RoutedEventArgs e)
    {
        var target = ItemOf<SessionItem>(sender);
        var targets = SessionActionTargets(sender);
        if (target?.IsMultiSelected == true)
            SessionsStopTrackingRequested?.Invoke(targets);
        else if (targets.Count == 1)
            SessionStopTrackingRequested?.Invoke(targets[0]);
    }

    /// <summary>다중 선택이면 선택한 세션을 하나씩 종료한다(잠금/외부 판단은 MainWindow).</summary>
    private void SessionShutdown_Click(object sender, RoutedEventArgs e)
    {
        foreach (var session in SessionActionTargets(sender))
            SessionShutdownRequested?.Invoke(session);
    }

    private void SessionHide_Click(object sender, RoutedEventArgs e)
    {
        var target = ItemOf<SessionItem>(sender);
        var targets = SessionActionTargets(sender).Where(session => !session.Hidden).ToList();
        if (target?.IsMultiSelected == true)
            SessionsHideRequested?.Invoke(targets);
        else if (targets.Count == 1)
            SessionHideRequested?.Invoke(targets[0]);
    }

    private void SessionLock_Click(object sender, RoutedEventArgs e)
    {
        var target = ItemOf<SessionItem>(sender);
        var targets = SessionActionTargets(sender);
        if (targets.Count == 0 || target == null) return;
        if (target.IsMultiSelected && targets.Count > 1)
        {
            bool locked = sender is MenuItem { CommandParameter: "Lock" };
            SessionsLockRequested?.Invoke(targets, locked);
        }
        else
        {
            SessionLockRequested?.Invoke(targets[0]);
        }
    }

    /// <summary>이벤트 소스에서 데이터 항목을 얻는다. 컨텍스트 메뉴 항목은 Tag, 행 요소는 DataContext.</summary>
    private static T? ItemOf<T>(object sender) where T : class
        => sender is FrameworkElement fe ? (fe.Tag ?? fe.DataContext) as T : null;

    // ── 드래그 순서변경 (devez ReorderDrag: 고스트 + 시프트 애니메이션) ──────────────
    private Point _pressOrigin;
    private ProjectFolderItem? _pendingFolder;
    private ProjectItem? _pendingProject;
    private TabItemBase? _pendingTab;   // 카드 그룹의 세션/문서 행(드래그 재정렬 대상)
    private ProjectFile? _pendingFile;
    private ReorderDrag<object>? _rootDrag;
    private ReorderDrag<ProjectItem>? _projectDrag;
    private ReorderDrag<TabItemBase>? _tabDrag;
    private ReorderDrag<ProjectFile>? _fileDrag;
    private Border? _sessionChildDropTarget;
    // 드래그 중 자식 편입 화살표를 띄운 세션들과, 현재 화살표 위에 커서가 있는 세션.
    private readonly List<SessionItem> _sessionChildDropHints = new();
    private SessionItem? _sessionChildDropHintActive;
    private bool _didDrag;
    private ProjectItem? _draggedProject;
    private ProjectFolderItem? _projectFolderHoverTarget;
    private ProjectFolderItem? _projectFolderDropTarget;
    private Border? _projectFolderDropBorder;
    private ProjectFolderEntrySide _projectFolderEntrySide;
    private Point? _lastProjectDragPoint;
    private ProjectColumnsPanel? _projectGridHeightPanel;
    private double _projectGridLeftHeight;
    private double _projectGridRightHeight;
    private double _projectGridSourceHeight;
    private double _projectGridHeightOffset;
    private ProjectItem? _projectGridSourceProject;
    private string? _projectGridSourceFolderId;
    private int _projectGridOriginColumn = -1;
    private int _projectGridPreviewColumn = -1;
    private bool _endingDrag;
    private bool HasActiveDrag =>
        _rootDrag != null || _projectDrag != null || _tabDrag != null || _fileDrag != null;

    private void ProjectRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        // chevron 등 버튼 위에서 누른 경우 드래그를 무장하지 않는다(버튼 동작 보존).
        _pendingProject = IsWithinButton(e.OriginalSource as DependencyObject)
            ? null : (sender as FrameworkElement)?.DataContext as ProjectItem;
        _pendingFolder = null;
        _pendingTab = null;
        _pendingFile = null;
        _didDrag = false;
        LatchPressedSession(null);
    }

    private void FolderHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pendingFolder = IsWithinButton(e.OriginalSource as DependencyObject)
            ? null : (sender as FrameworkElement)?.DataContext as ProjectFolderItem;
        _pendingProject = null;
        _pendingTab = null;
        _pendingFile = null;
        _didDrag = false;
        LatchPressedSession(null);
    }

    private static bool IsWithinButton(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is ButtonBase) return true;
            current = current switch
            {
                FrameworkContentElement content => content.Parent,
                Visual _ => VisualTreeHelper.GetParent(current),
                System.Windows.Media.Media3D.Visual3D _ => VisualTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current),
            };
        }
        return false;
    }

    // 카드 그룹의 세션/문서 행 공용 — 드래그 재정렬 대상(TabItemBase).
    private void SessionRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pendingTab = IsWithinButton(e.OriginalSource as DependencyObject)
                      || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0
            ? null
            : (sender as FrameworkElement)?.DataContext as TabItemBase;
        _pendingFolder = null;
        _pendingProject = null;
        _pendingFile = null;
        _didDrag = false;
        LatchPressedSession(IsWithinButton(e.OriginalSource as DependencyObject)
            ? null
            : (sender as FrameworkElement)?.DataContext as SessionItem);
    }

    // ── 클릭 대상 확정: MouseUp 라우팅이 아니라 '누른 행' 기준 ──
    private SessionItem? _pressedSession;
    private int _pressedSessionTick;
    private static int _hiddenGroupShiftTick;

    private void LatchPressedSession(SessionItem? session)
    {
        _pressedSession = session;
        _pressedSessionTick = Environment.TickCount;
    }

    /// <summary>누른 세션과 놓은 세션이 다르면(사이 재배치) 누른 세션을 반환하고 진단 로그를 남긴다.</summary>
    private SessionItem ResolvePressedSession(SessionItem upSession)
    {
        var pressed = _pressedSession;
        _pressedSession = null;
        bool fresh = unchecked(Environment.TickCount - _pressedSessionTick) < 2000;
        if (pressed == null || !fresh || ReferenceEquals(pressed, upSession)) return upSession;

        DiagLog.Write(
            $"SIDEBAR-CLICK-MISMATCH: pressed='{pressed.Name}'(hidden={pressed.Hidden}) "
            + $"up='{upSession.Name}'(hidden={upSession.Hidden}) "
            + $"pressAge={unchecked(Environment.TickCount - _pressedSessionTick)}ms "
            + $"sinceHiddenShift={unchecked(Environment.TickCount - _hiddenGroupShiftTick)}ms → 누른 세션으로 처리");
        return pressed;
    }

    private void SessionRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionItem session }) return;

        // 선택된 항목 우클릭은 기존 묶음을 유지한다. 선택되지 않은 항목 우클릭은
        // 기존 선택만 해제하고 일반 단일 세션 컨텍스트 메뉴를 연다.
        if (!session.IsMultiSelected)
            ClearSessionMultiSelection();
    }

    private void ProjectFileRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pendingFile = (sender as FrameworkElement)?.DataContext as ProjectFile;
        _pendingFolder = null;
        _pendingProject = null;
        _pendingTab = null;
        _didDrag = false;
        LatchPressedSession(null);
    }

    private void Sidebar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (UpdateActiveDrag(e)) return;

        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = _pressOrigin - e.GetPosition(this);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (_pendingFolder != null) TryStartFolderDrag(_pendingFolder);
        else if (_pendingProject != null) TryStartProjectDrag(_pendingProject);
        else if (_pendingTab != null) TryStartTabDrag(_pendingTab);
        else if (_pendingFile != null) TryStartFileDrag(_pendingFile);

        if (HasActiveDrag && _sidebarSearchDebounce.IsEnabled)
        {
            _sidebarSearchDebounce.Stop();
            _sidebarSearchDeferredForDrag = true;
        }

        // 임계값을 넘긴 현재 이벤트가 유일한 큰 이동일 수도 있으므로 시작 직후 바로 목표를 계산한다.
        UpdateActiveDrag(e);
    }

    private bool UpdateActiveDrag(MouseEventArgs e) => UpdateActiveDrag(e.GetPosition(this));

    private bool UpdateActiveDrag(Point pointer)
    {
        if (_rootDrag != null)
        {
            var folderDrop = _draggedProject == null
                ? ProjectFolderDropPreview.None
                : UpdateProjectFolderDropPreview(pointer);
            _rootDrag.SetExternalDropPreview(
                folderDrop != ProjectFolderDropPreview.None,
                preserveReorder: folderDrop == ProjectFolderDropPreview.IntoPreserveReorder);
            _rootDrag.Update(pointer);
            UpdateProjectDragAutoScroll(pointer);
            return true;
        }
        if (_projectDrag != null)
        {
            var folderDrop = UpdateProjectFolderDropPreview(pointer);
            _projectDrag.SetExternalDropPreview(folderDrop != ProjectFolderDropPreview.None);
            _projectDrag.Update(pointer);
            UpdateProjectGridHeightPreview(_projectDrag.CurrentTargetColumn);
            UpdateProjectDragAutoScroll(pointer);
            return true;
        }
        if (_tabDrag != null) { _tabDrag.Update(pointer); return true; }
        if (_fileDrag != null) { _fileDrag.Update(pointer); return true; }
        return false;
    }

    // ── 프로젝트 드래그 오토스크롤 ──────────────────────────────
    // 고스트는 패널 좌우로 못 나가지만 위/아래로는 나갈 수 있다. 포인터가 목록 위·아래
    // 가장자리 영역(AutoScrollZone)에 들어가면 그 깊이에 비례한 속도로 목록을 스크롤한다.
    private const double ProjectAutoScrollZone = 56;
    private const double ProjectAutoScrollMaxStep = 16;
    private DispatcherTimer? _projectAutoScrollTimer;
    private double _projectAutoScrollStep;
    private double _dragScrollAnchor; // 캡처 좌표 보정에 이미 반영한 스크롤 오프셋

    private ScrollViewer ProjectScrollFor(bool archived)
        => archived ? ArchiveProjectScroll : ActiveProjectScroll;

    // 고스트 좌우 제한은 카드가 실제로 놓이는 레인(ItemsControl) 기준.
    // ScrollViewer 를 쓰면 오른쪽 패딩·스크롤바 폭만큼 카드 보더 밖으로 나간다.
    private FrameworkElement ProjectLaneFor(bool archived)
        => archived ? ArchivedHost : ProjectsHost;

    // 왼쪽은 ScrollViewer 좌측 패딩(8px)까지 허용, 오른쪽은 반대로 8px 덜 가게 한다.
    private static readonly Thickness ProjectGhostClampInset = new(8, 0, -8, 0);

    private ScrollViewer CurrentProjectScroll => ProjectScrollFor(_archiveOpen);

    private void UpdateProjectDragAutoScroll(Point pointer)
    {
        var scroll = CurrentProjectScroll;
        if (scroll.ScrollableHeight <= 0.5) { StopProjectDragAutoScroll(); return; }

        double top, height;
        try
        {
            top = scroll.TransformToAncestor(this).Transform(new Point()).Y;
            height = scroll.ActualHeight;
        }
        catch { StopProjectDragAutoScroll(); return; }
        if (height <= ProjectAutoScrollZone * 2) { StopProjectDragAutoScroll(); return; }

        // 가장자리 영역 밖(위/아래로 완전히 벗어남 포함)에서는 최대 속도로 계속 스크롤한다.
        double aboveDepth = top + ProjectAutoScrollZone - pointer.Y;
        double belowDepth = pointer.Y - (top + height - ProjectAutoScrollZone);
        double step = aboveDepth > 0
            ? -Math.Min(1, aboveDepth / ProjectAutoScrollZone) * ProjectAutoScrollMaxStep
            : belowDepth > 0
                ? Math.Min(1, belowDepth / ProjectAutoScrollZone) * ProjectAutoScrollMaxStep
                : 0;

        if (step == 0) { StopProjectDragAutoScroll(); return; }
        _projectAutoScrollStep = step;
        if (_projectAutoScrollTimer != null) return;

        _projectAutoScrollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _projectAutoScrollTimer.Tick += ProjectAutoScrollTick;
        _projectAutoScrollTimer.Start();
    }

    private void ProjectAutoScrollTick(object? sender, EventArgs e)
    {
        // 캡처 유실 등으로 MouseUp 을 놓친 경우 타이머가 혼자 살아남지 않게 한다.
        if (!HasActiveDrag || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            StopProjectDragAutoScroll();
            return;
        }
        var scroll = CurrentProjectScroll;
        double offset = Math.Clamp(
            scroll.VerticalOffset + _projectAutoScrollStep, 0, scroll.ScrollableHeight);
        if (Math.Abs(offset - scroll.VerticalOffset) < 0.01) { StopProjectDragAutoScroll(); return; }

        scroll.ScrollToVerticalOffset(offset);
        scroll.UpdateLayout(); // 새 위치 기준으로 재정렬 프리뷰를 즉시 재계산
        SyncDragScrollOrigin();
        UpdateActiveDrag(Mouse.GetPosition(this));
    }

    private void StopProjectDragAutoScroll()
    {
        if (_projectAutoScrollTimer == null) return;
        _projectAutoScrollTimer.Stop();
        _projectAutoScrollTimer.Tick -= ProjectAutoScrollTick;
        _projectAutoScrollTimer = null;
        _projectAutoScrollStep = 0;
    }

    private async void Sidebar_PreviewMouseUp(object sender, MouseButtonEventArgs e) => await EndDragAsync(commit: true);
    private async void Sidebar_LostCapture(object sender, MouseEventArgs e)
        => await EndDragAsync(commit: e.LeftButton != MouseButtonState.Pressed);

    private async Task EndDragAsync(bool commit)
    {
        if (_endingDrag) return;
        _endingDrag = true;
        StopProjectDragAutoScroll();
        try
        {
            var rootDrag = _rootDrag;
            var projectDrag = _projectDrag;
            var tabDrag = _tabDrag;
            var fileDrag = _fileDrag;
            var draggedProject = _draggedProject;
            var dropFolder = draggedProject == null ? null : _projectFolderDropTarget;
            bool applyDeferredSearch = _sidebarSearchDeferredForDrag;
            bool applyDeferredProjectFilter = _projectFilterRefreshDeferredForDrag;
            _sidebarSearchDeferredForDrag = false;
            _projectFilterRefreshDeferredForDrag = false;
            _rootDrag = null;
            _projectDrag = null;
            _tabDrag = null;
            _fileDrag = null;
            _pendingFolder = null;
            _pendingProject = null;
            _pendingTab = null;
            _pendingFile = null;
            _draggedProject = null;
            ClearProjectFolderDropPreview();
            if (Mouse.Captured == this) ReleaseMouseCapture();

            bool droppedOnFolder = commit && draggedProject != null && dropFolder != null;
            bool moveIntoFolder = droppedOnFolder && draggedProject!.FolderId != dropFolder!.Id;
            if (rootDrag != null)
                await rootDrag.FinishAsync(commit && !droppedOnFolder);
            try
            {
                if (projectDrag != null)
                    await projectDrag.FinishAsync(commit && !droppedOnFolder);
                if (moveIntoFolder && MoveProjectIntoFolder(draggedProject!, dropFolder!))
                    ProjectsReordered?.Invoke();
                if (projectDrag != null && applyDeferredSearch)
                {
                    ApplySidebarSearch();
                    applyDeferredSearch = false;
                }
            }
            finally
            {
                FinishProjectGridHeightPreview();
            }
            if (tabDrag != null) await tabDrag.FinishAsync(commit);
            if (fileDrag != null) await fileDrag.FinishAsync(commit);
            if (applyDeferredSearch) ApplySidebarSearch();
            if (applyDeferredProjectFilter) RefreshProjectGroups();
        }
        finally
        {
            ClearSessionChildDropHints();
            _endingDrag = false;
        }
    }

    /// <summary>프로젝트를 대상 폴더의 마지막 프로젝트로 옮긴다.</summary>
    private bool MoveProjectIntoFolder(ProjectItem project, ProjectFolderItem folder)
    {
        var collection = folder.IsArchived ? ArchivedProjects : Projects;
        if (!WorkspaceStore.ProjectFolders.Contains(folder)
            || !collection.Contains(project)
            || project.IsArchived != folder.IsArchived
            || project.FolderId == folder.Id)
            return false;

        int oldIndex = collection.IndexOf(project);
        var lastDestinationProject = collection.LastOrDefault(item =>
            !ReferenceEquals(item, project) && item.FolderId == folder.Id);

        project.FolderId = folder.Id;

        int targetIndex;
        if (lastDestinationProject == null)
        {
            targetIndex = collection.Count - 1;
        }
        else
        {
            int lastIndex = collection.IndexOf(lastDestinationProject);
            targetIndex = oldIndex < lastIndex ? lastIndex : lastIndex + 1;
        }

        targetIndex = Math.Clamp(targetIndex, 0, collection.Count - 1);
        if (oldIndex != targetIndex)
            collection.Move(oldIndex, targetIndex);
        else
            RefreshProjectGroups();
        return true;
    }

    private enum ProjectFolderEntrySide { None, Top, Bottom }
    private enum ProjectFolderDropPreview { None, Into, IntoPreserveReorder }

    /// <summary>폴더에 처음 들어온 방향 쪽 75%는 내부 드롭, 반대쪽 25%는 폴더 앞/뒤
    /// 재정렬로 남긴다. 잠금 중 내부 75%는 밀려난 배치를 보존한 채 드롭 대상만 표시한다.</summary>
    private ProjectFolderDropPreview UpdateProjectFolderDropPreview(Point point)
    {
        ClearProjectFolderDropOutline();
        var draggedProject = _draggedProject;
        if (draggedProject == null)
        {
            ResetProjectFolderHover(point);
            return ProjectFolderDropPreview.None;
        }

        var target = FindProjectFolderDropTarget(point, draggedProject);
        if (target.Folder == null || target.PreviewBorder == null
            || draggedProject.FolderId == target.Folder.Id)
        {
            ResetProjectFolderHover(point);
            return ProjectFolderDropPreview.None;
        }

        double relativeY = ProjectFolderRelativeY(point, target.PreviewBorder);
        if (_rootDrag?.TryGetQuarterReorderLock(target.Folder, out bool reorderAfter) == true)
        {
            _projectFolderHoverTarget = target.Folder;
            _lastProjectDragPoint = point;
            bool crossedReorderQuarter = reorderAfter ? relativeY < 0.25 : relativeY > 0.75;
            if (crossedReorderQuarter) return ProjectFolderDropPreview.None;

            _projectFolderDropTarget = target.Folder;
            _projectFolderDropBorder = target.PreviewBorder;
            _projectFolderDropBorder.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
            return ProjectFolderDropPreview.IntoPreserveReorder;
        }

        if (!ReferenceEquals(_projectFolderHoverTarget, target.Folder))
        {
            _projectFolderHoverTarget = target.Folder;
            _projectFolderEntrySide = ResolveProjectFolderEntrySide(
                point, _lastProjectDragPoint, target.PreviewBorder);
        }
        _lastProjectDragPoint = point;

        bool oppositeQuarter = _projectFolderEntrySide switch
        {
            ProjectFolderEntrySide.Top => relativeY > 0.75,
            ProjectFolderEntrySide.Bottom => relativeY < 0.25,
            _ => false,
        };
        if (oppositeQuarter) return ProjectFolderDropPreview.None;

        _projectFolderDropTarget = target.Folder;
        _projectFolderDropBorder = target.PreviewBorder;
        _projectFolderDropBorder?.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
        return ProjectFolderDropPreview.Into;
    }

    private ProjectFolderEntrySide ResolveProjectFolderEntrySide(
        Point point,
        Point? previousPoint,
        Border border)
    {
        try
        {
            var origin = border.TransformToAncestor(this).Transform(new Point());
            double bottom = origin.Y + Math.Max(1, border.ActualHeight);
            if (previousPoint is Point previous)
            {
                if (previous.Y <= origin.Y) return ProjectFolderEntrySide.Top;
                if (previous.Y >= bottom) return ProjectFolderEntrySide.Bottom;
                if (point.Y > previous.Y + 0.25) return ProjectFolderEntrySide.Top;
                if (point.Y < previous.Y - 0.25) return ProjectFolderEntrySide.Bottom;
            }
        }
        catch { /* 레이아웃 전환 중이면 현재 위치의 절반으로 판정 */ }

        return ProjectFolderRelativeY(point, border) <= 0.5
            ? ProjectFolderEntrySide.Top
            : ProjectFolderEntrySide.Bottom;
    }

    private double ProjectFolderRelativeY(Point point, Border border)
    {
        try
        {
            var origin = border.TransformToAncestor(this).Transform(new Point());
            return Math.Clamp((point.Y - origin.Y) / Math.Max(1, border.ActualHeight), 0, 1);
        }
        catch { return 0.5; }
    }

    private void ResetProjectFolderHover(Point point)
    {
        _projectFolderHoverTarget = null;
        _projectFolderEntrySide = ProjectFolderEntrySide.None;
        _lastProjectDragPoint = point;
    }

    /// <summary>프로젝트 드롭 대상 폴더를 현재 포인터에서 직접 판정한다. 같은 폴더 내부 카드는
    /// 기존 재정렬을 유지하고, 다른 폴더는 헤더뿐 아니라 폴더 전체 영역에서 받아들인다.</summary>
    private (ProjectFolderItem? Folder, Border? PreviewBorder) FindProjectFolderDropTarget(
        Point point,
        ProjectItem draggedProject)
    {
        for (DependencyObject? current = InputHitTest(point) as DependencyObject;
             current != null && !ReferenceEquals(current, this);
             current = GetParentObject(current))
        {
            if (current is not Border { DataContext: ProjectFolderItem folder } border)
                continue;

            if (border.Name == "FolderRoot" && draggedProject.FolderId != folder.Id)
                return (folder, border);
            if (border.Name != "FolderHeader") continue;

            var previewBorder = border;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(border);
                 parent != null && !ReferenceEquals(parent, this);
                 parent = GetParentObject(parent))
            {
                if (parent is Border { Name: "FolderRoot" } root)
                {
                    previewBorder = root;
                    break;
                }
            }
            return (folder, previewBorder);
        }
        return (null, null);
    }

    private void ClearProjectFolderDropPreview()
    {
        ClearProjectFolderDropOutline();
        _projectFolderHoverTarget = null;
        _projectFolderEntrySide = ProjectFolderEntrySide.None;
        _lastProjectDragPoint = null;
    }

    private void ClearProjectFolderDropOutline()
    {
        if (_projectFolderDropBorder != null)
            _projectFolderDropBorder.ClearValue(Border.BorderBrushProperty);
        _projectFolderDropBorder = null;
        _projectFolderDropTarget = null;
    }

    private void TryStartFolderDrag(ProjectFolderItem folder)
    {
        if (IsReorderBlockedByActiveFilter(folder.IsArchived)) return;
        TryStartRootDrag(folder, folder.IsArchived);
    }

    // 활성 필터가 켜지면 활성 목록은 실제 순서의 부분집합만 보여주므로 재정렬을 막는다(보관 목록은 필터 대상 아님).
    private bool IsReorderBlockedByActiveFilter(bool isArchived)
    {
        if (!_showActiveProjectsOnly || isArchived) return false;
        // 드래그를 막았어도 마우스를 뗀 위치의 행이 클릭으로 실행되면 안 되므로 드래그로 표시해 클릭을 삼킨다.
        _didDrag = true;
        _pendingProject = null;
        _pendingFolder = null;
        _pressedSession = null;
        return true;
    }

    // 루트 카드의 컬럼(0/1) 읽기·쓰기 — 프로젝트와 1열(반폭) 폴더 공통.
    private static int ColumnOfItem(object item) => item switch
    {
        ProjectItem project => project.Column,
        ProjectFolderItem folder => folder.Column,
        _ => 0,
    };

    private static void SetColumnItem(object item, int column)
    {
        if (item is ProjectItem project) project.Column = column;
        else if (item is ProjectFolderItem folder) folder.Column = column;
    }

    private void TryStartRootDrag(object item, bool archived)
    {
        var host = archived ? ArchivedHost : ProjectsHost;
        var rows = new List<(object Item, FrameworkElement Element)>();
        foreach (var rootItem in host.Items.Cast<object>())
            if (host.ItemContainerGenerator.ContainerFromItem(rootItem) is FrameworkElement element)
                rows.Add((rootItem, element));

        var source = rows.FirstOrDefault(row => ReferenceEquals(row.Item, item));
        if (source.Element == null) return;

        var folderGhostSource = item is ProjectFolderItem
            ? source.Element is Border { Name: "FolderRoot" } folderRoot
                ? folderRoot
                : FindVisualChildren<Border>(source.Element)
                    .FirstOrDefault(border => border.Name == "FolderRoot")
            : null;

        double gridMidX = _projectColumns >= 2
            ? ComputeColumnsMidX(source.Element)
            : double.PositiveInfinity;
        var visibleItems = rows.Select(row => row.Item).ToList();
        var panel = FindVisualChildren<ProjectColumnsPanel>(host).FirstOrDefault();
        var visibleRoots = archived ? _archivedRootItems : _activeRootItems;
        // 열 배정 가능: 2열 설정 + (프로젝트 또는 반폭 1열 폴더). 전체폭 2열 폴더는 배정 없음.
        bool colAssignable = _projectColumns >= 2 &&
            (item is ProjectItem || item is ProjectFolderItem { EffectiveColumns: 1 });
        int originalColumn = ColumnOfItem(item);
        List<object>? lastFolderPreviewOrder = null;
        double? folderHitTestX = null;
        // 좌¼ 편향 히트테스트는 전체폭(2열) 폴더 전용. 반폭 폴더는 프로젝트처럼 포인터 위치로 판정.
        if (_projectColumns >= 2 && item is ProjectFolderItem { EffectiveColumns: >= 2 })
        {
            try
            {
                var sourceOrigin = source.Element.TransformToAncestor(this).Transform(new Point());
                folderHitTestX = sourceOrigin.X + source.Element.ActualWidth / 4;
            }
            catch { /* 연결이 끊긴 컨테이너면 기존 포인터 판정으로 폴백 */ }
        }

        void PreviewRootMove(object? targetItem, FrameworkElement? _, bool after)
        {
            if (_projectColumns < 2) return;

            var preview = BuildRootPreviewOrder(item, targetItem, after, visibleItems);
            if (item is ProjectFolderItem && _rootDrag != null)
                lastFolderPreviewOrder = targetItem == null ? null : preview;

            int targetColumn = !colAssignable || targetItem == null || double.IsPositiveInfinity(gridMidX)
                ? originalColumn
                : Mouse.GetPosition(this).X >= gridMidX ? 1 : 0;
            bool orderChanged = !visibleRoots.SequenceEqual(preview);
            bool columnChanged = colAssignable && ColumnOfItem(item) != targetColumn;
            if (!orderChanged && !columnChanged) return;

            void ApplyPreviewLayout()
            {
                SyncCollection(visibleRoots, preview);
                if (colAssignable) SetColumnItem(item, targetColumn);
                panel?.InvalidateMeasure();
                panel?.InvalidateArrange();
            }

            // EndDrag는 필드를 먼저 비운 뒤 프리뷰를 원복하므로 종료/취소 시에는 즉시 정리하고,
            // 실제 드래그 중 레이아웃 변경에만 2축 이동 애니메이션을 적용한다.
            if (_rootDrag != null)
                _rootDrag.AnimateLayoutChange(ApplyPreviewLayout);
            else
                ApplyPreviewLayout();
        }

        var folderGhostBackground = folderGhostSource == null
            ? null
            : FindVisualChildren<Border>(folderGhostSource)
                .FirstOrDefault(border => border.Name == "ProjectCardRoot")?.Background
                ?? (TryFindResource("PanelBrush") as Brush);
        _rootDrag = ReorderDrag<object>.TryStart(
            this, rows, item, source.Element,
            (sourceItem, hostTarget, _) =>
            {
                bool columnChanged = false;
                bool columnChangedFromOriginal = false;
                if (colAssignable && !double.IsPositiveInfinity(gridMidX))
                {
                    int targetColumn = Mouse.GetPosition(this).X >= gridMidX ? 1 : 0;
                    columnChanged = ColumnOfItem(sourceItem) != targetColumn;
                    SetColumnItem(sourceItem, targetColumn);
                    columnChangedFromOriginal = ColumnOfItem(sourceItem) != originalColumn;
                }

                bool orderChanged;
                if (sourceItem is ProjectFolderItem
                    && _projectColumns >= 2
                    && lastFolderPreviewOrder != null)
                {
                    orderChanged = ApplyRootOrderWithinVisible(
                        lastFolderPreviewOrder, visibleItems, archived);
                }
                else
                {
                    orderChanged = MoveRootItemWithinVisible(
                        sourceItem, hostTarget, visibleItems, archived);
                }
                if (columnChanged && !orderChanged)
                {
                    panel?.InvalidateMeasure();
                    panel?.InvalidateArrange();
                }
                if (orderChanged || columnChanged || columnChangedFromOriginal)
                    ProjectsReordered?.Invoke();
                return Task.CompletedTask;
            },
            exactFollow: true,
            commitUnchanged: colAssignable,
            reorderPreviewChanged: PreviewRootMove,
            hitTestSlots: true,
            suppressDisplacement: _projectColumns >= 2,
            preserveRowOrder: true,
            // 빈 공간 최근접 판정이 포인터가 속한 컬럼을 우선하도록 컬럼 경계를 전달(1열이면 무한대=비활성).
            gridMidX: gridMidX,
            hitTestXOverride: folderHitTestX,
            useLiveLayoutPlaceholder: _projectColumns >= 2 && item is ProjectFolderItem { EffectiveColumns: >= 2 },
            useFixedLayoutPlaceholder: _projectColumns < 2,
            useLogicalHitTestBounds: colAssignable,
            ghostSource: folderGhostSource,
            ghostBackgroundTarget: folderGhostSource,
            ghostBackground: folderGhostBackground,
            useQuarterReorderHysteresis: (sourceItem, targetItem) =>
                sourceItem is ProjectItem && targetItem is ProjectFolderItem,
            ghostClampHost: ProjectLaneFor(archived),
            ghostClampInset: ProjectGhostClampInset);

        if (_rootDrag != null)
        {
            _dragScrollAnchor = ProjectScrollFor(archived).VerticalOffset;
            _draggedProject = item as ProjectItem;
            _didDrag = true;
            CaptureMouse();
        }
        _pendingFolder = null;
        _pendingProject = null;
    }

    private static List<object> BuildRootPreviewOrder(
        object source,
        object? target,
        bool after,
        IReadOnlyList<object> visibleItems)
    {
        var preview = visibleItems.ToList();
        if (target == null || ReferenceEquals(target, source)) return preview;

        preview.Remove(source);
        int targetIndex = preview.IndexOf(target);
        if (targetIndex < 0) return visibleItems.ToList();
        if (after) targetIndex++;
        preview.Insert(targetIndex, source);
        return preview;
    }

    private bool ApplyRootOrderWithinVisible(
        IReadOnlyList<object> orderedVisible,
        IReadOnlyList<object> visibleItems,
        bool archived)
    {
        if (visibleItems.SequenceEqual(orderedVisible)) return false;

        var visibleSet = visibleItems.ToHashSet();
        var desired = GetAllRootItems(archived);
        int visibleIndex = 0;
        for (int i = 0; i < desired.Count; i++)
            if (visibleSet.Contains(desired[i]))
                desired[i] = orderedVisible[visibleIndex++];

        ApplyRootOrder(desired);
        SyncUngroupedProjectCollection(archived ? ArchivedProjects : Projects, desired);
        RefreshProjectGroups();
        return true;
    }

    private bool MoveRootItemWithinVisible(
        object source,
        int targetIndex,
        IReadOnlyList<object> visibleItems,
        bool archived)
    {
        var ordered = visibleItems.ToList();
        int oldIndex = ordered.IndexOf(source);
        if (oldIndex < 0) return false;

        targetIndex = Math.Clamp(targetIndex, 0, ordered.Count - 1);
        if (targetIndex == oldIndex) return false;

        ordered.RemoveAt(oldIndex);
        ordered.Insert(targetIndex, source);
        return ApplyRootOrderWithinVisible(ordered, visibleItems, archived);
    }

    private void TryStartProjectDrag(ProjectItem p)
    {
        if (IsReorderBlockedByActiveFilter(p.IsArchived)) return;

        if (p.FolderId == null)
        {
            TryStartRootDrag(p, p.IsArchived);
            return;
        }

        string? folderId = p.FolderId;
        var rows = GetProjectRows()
            .Where(row => row.Item.FolderId == folderId)
            .ToList();
        var src = rows.FirstOrDefault(row => ReferenceEquals(row.Item, p));
        if (src.Element == null) return;

        var coll = CurrentProjects;
        var visibleItems = rows.Select(row => row.Item).ToList();
        ProjectColumnsPanel? gridPanel = null;

        // 내부 목록 열 수는 전역이 아니라 폴더별 EffectiveColumns 를 따른다(폴더 1열 토글 시 전역 2열이어도 세로 재정렬).
        var folder = WorkspaceStore.ProjectFolders.FirstOrDefault(item => item.Id == folderId);
        int folderColumns = folder?.EffectiveColumns ?? _projectColumns;
        if (folderColumns >= 2)
        {
            double midX = ComputeColumnsMidX(src.Element);
            gridPanel = FindVisualAncestor<ProjectColumnsPanel>(src.Element);
            _projectDrag = ReorderDrag<ProjectItem>.TryStart(this, rows, p, src.Element,
                (project, targetColumn, targetIndex) =>
                {
                    if (MoveProjectToColumnWithinVisible(
                            coll, project, targetColumn, targetIndex, visibleItems))
                    {
                        // Column은 일반 모델 속성이므로 컬렉션 순서가 그대로인 열 이동도 패널을 직접 갱신한다.
                        gridPanel?.InvalidateMeasure();
                        gridPanel?.InvalidateArrange();
                        ProjectsReordered?.Invoke();
                    }
                    return Task.CompletedTask;
                }, exactFollow: true, columns: 2, gridMidX: midX,
                useGridPlaceholder: true,
                includeElementMarginsInBounds: true,
                ghostClampHost: ProjectLaneFor(p.IsArchived),
                ghostClampInset: ProjectGhostClampInset);
        }
        else
        {
            _projectDrag = ReorderDrag<ProjectItem>.TryStart(this, rows, p, src.Element,
                (project, hostTarget, _) =>
                {
                    if (MoveProjectWithinVisible(coll, project, hostTarget, visibleItems))
                        ProjectsReordered?.Invoke();
                    return Task.CompletedTask;
                }, exactFollow: true, useFixedLayoutPlaceholder: true,
                ghostClampHost: ProjectLaneFor(p.IsArchived),
                ghostClampInset: ProjectGhostClampInset);
        }

        if (_projectDrag != null)
        {
            _dragScrollAnchor = ProjectScrollFor(p.IsArchived).VerticalOffset;
            if (gridPanel != null)
                BeginProjectGridHeightPreview(gridPanel, rows, p);
            _draggedProject = p;
            _didDrag = true;
            CaptureMouse();
        }
        _pendingProject = null;
    }

    private void BeginProjectGridHeightPreview(
        ProjectColumnsPanel panel,
        IReadOnlyList<(ProjectItem Item, FrameworkElement Element)> rows,
        ProjectItem source)
    {
        panel.BeginAnimation(FrameworkElement.HeightProperty, null);
        panel.ClearValue(FrameworkElement.HeightProperty);
        panel.InvalidateMeasure();
        panel.InvalidateArrange();
        panel.UpdateLayout();
        _projectGridHeightPanel = panel;
        _projectGridLeftHeight = rows
            .Where(row => row.Item.Column != 1)
            .Sum(row => ProjectGridRowPitch(row.Element));
        _projectGridRightHeight = rows
            .Where(row => row.Item.Column == 1)
            .Sum(row => ProjectGridRowPitch(row.Element));
        _projectGridSourceHeight = ProjectGridRowPitch(rows
            .First(row => ReferenceEquals(row.Item, source)).Element);
        _projectGridHeightOffset = panel.ActualHeight
            - Math.Max(_projectGridLeftHeight, _projectGridRightHeight);
        _projectGridSourceProject = source;
        _projectGridSourceFolderId = source.FolderId;
        _projectGridOriginColumn = source.Column == 1 ? 1 : 0;
        _projectGridPreviewColumn = _projectGridOriginColumn;
    }

    private void UpdateProjectGridHeightPreview(int targetColumn)
    {
        var panel = _projectGridHeightPanel;
        targetColumn = targetColumn == 1 ? 1 : 0;
        if (panel == null || targetColumn == _projectGridPreviewColumn) return;
        _projectGridPreviewColumn = targetColumn;

        AnimateProjectGridPanelHeight(panel, ProjectGridHeightForColumn(targetColumn));
    }

    private double ProjectGridHeightForColumn(int? targetColumn)
    {
        double left = _projectGridLeftHeight;
        double right = _projectGridRightHeight;
        if (_projectGridOriginColumn == 0) left -= _projectGridSourceHeight;
        else right -= _projectGridSourceHeight;
        if (targetColumn == 1) right += _projectGridSourceHeight;
        else if (targetColumn == 0) left += _projectGridSourceHeight;
        return Math.Max(0, Math.Max(left, right) + _projectGridHeightOffset);
    }

    private static double ProjectGridRowPitch(FrameworkElement element) =>
        Math.Max(1, element.ActualHeight + element.Margin.Top + element.Margin.Bottom);

    private static void AnimateProjectGridPanelHeight(ProjectColumnsPanel panel, double targetHeight)
    {
        double currentHeight = panel.ActualHeight;
        if (Math.Abs(currentHeight - targetHeight) < 0.5) return;
        var animation = new DoubleAnimation
        {
            From = currentHeight,
            To = Math.Max(0, targetHeight),
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        panel.BeginAnimation(FrameworkElement.HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void FinishProjectGridHeightPreview()
    {
        var panel = _projectGridHeightPanel;
        var source = _projectGridSourceProject;
        int? finalColumn = source != null && source.FolderId == _projectGridSourceFolderId
            ? source.Column == 1 ? 1 : 0
            : null;
        double finalHeight = panel == null
            ? 0
            : ProjectGridHeightForColumn(finalColumn);
        _projectGridHeightPanel = null;
        _projectGridLeftHeight = 0;
        _projectGridRightHeight = 0;
        _projectGridSourceHeight = 0;
        _projectGridHeightOffset = 0;
        _projectGridSourceProject = null;
        _projectGridSourceFolderId = null;
        _projectGridOriginColumn = -1;
        _projectGridPreviewColumn = -1;
        if (panel == null) return;

        // 드롭 시 프리뷰 애니메이션을 먼저 제거하고 UpdateLayout 결과를 다시 목표로 삼으면,
        // 모델 열 이동이 정착되기 전의 원래 높이를 한 프레임 거쳐 접혔다가 다시 늘어날 수 있다.
        // 현재 애니메이션 값을 그대로 이어 받아 실제 확정된 Column의 계산 높이로 마무리한다.
        double from = panel.Height;
        if (double.IsNaN(from) || double.IsInfinity(from)) from = panel.ActualHeight;
        panel.BeginAnimation(FrameworkElement.HeightProperty, null);
        if (Math.Abs(from - finalHeight) < 0.5)
        {
            panel.ClearValue(FrameworkElement.HeightProperty);
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
            return;
        }

        panel.Height = from;
        var animation = new DoubleAnimation
        {
            From = from,
            To = finalHeight,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            panel.BeginAnimation(FrameworkElement.HeightProperty, null);
            panel.ClearValue(FrameworkElement.HeightProperty);
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        };
        panel.BeginAnimation(FrameworkElement.HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }


    /// <summary>드래그 중인 프로젝트가 배치된 패널의 가운데 X(좌/우 컬럼 경계).</summary>
    private double ComputeColumnsMidX(FrameworkElement source)
    {
        var panel = FindVisualAncestor<ProjectColumnsPanel>(source);

        if (panel == null || panel.ActualWidth <= 0) return double.PositiveInfinity;
        try
        {
            var origin = panel.TransformToAncestor(this).Transform(new Point(0, 0));
            return origin.X + panel.ActualWidth / 2;
        }
        catch { return double.PositiveInfinity; }
    }

    /// <summary>히트 테스트 결과에는 Run 같은 비-Visual 요소가 섞여 올 수 있으므로,
    /// 시각 트리와 논리 트리를 모두 다뤄 안전하게 부모를 얻는다.</summary>
    private static DependencyObject? GetParentObject(DependencyObject current) => current switch
    {
        FrameworkContentElement content => content.Parent,
        Visual _ => VisualTreeHelper.GetParent(current),
        System.Windows.Media.Media3D.Visual3D _ => VisualTreeHelper.GetParent(current),
        _ => LogicalTreeHelper.GetParent(current),
    };

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        for (; current != null; current = GetParentObject(current))
            if (current is T match) return match;
        return null;
    }

    private static T? FindVisualAncestorByName<T>(DependencyObject? current, string name)
        where T : FrameworkElement
    {
        for (; current != null; current = GetParentObject(current))
            if (current is T { Name: var elementName } match && elementName == name)
                return match;
        return null;
    }

    private static bool MoveProjectWithinVisible(
        ObservableCollection<ProjectItem> collection,
        ProjectItem source,
        int targetIndex,
        IReadOnlyList<ProjectItem> visibleItems)
    {
        var visibleSet = visibleItems.ToHashSet();
        var orderedVisible = collection.Where(visibleSet.Contains).ToList();
        int oldIndex = orderedVisible.IndexOf(source);
        if (oldIndex < 0) return false;

        targetIndex = Math.Clamp(targetIndex, 0, orderedVisible.Count - 1);
        if (targetIndex == oldIndex) return false;

        orderedVisible.RemoveAt(oldIndex);
        orderedVisible.Insert(targetIndex, source);

        var desired = collection.ToList();
        int visibleIndex = 0;
        for (int i = 0; i < desired.Count; i++)
            if (visibleSet.Contains(desired[i]))
                desired[i] = orderedVisible[visibleIndex++];
        SyncCollection(collection, desired);
        return true;
    }

    /// <summary>2열에서 검색으로 보이는 프로젝트만 재정렬하고, 숨은 프로젝트의 슬롯은 보존한다.</summary>
    private static bool MoveProjectToColumnWithinVisible(
        ObservableCollection<ProjectItem> collection,
        ProjectItem source,
        int targetColumn,
        int targetIndex,
        IReadOnlyList<ProjectItem> visibleItems)
    {
        targetColumn = targetColumn == 1 ? 1 : 0;
        var visibleSet = visibleItems.ToHashSet();
        var orderedVisible = collection.Where(visibleSet.Contains).ToList();
        int sourceIndex = orderedVisible.IndexOf(source);
        if (sourceIndex < 0) return false;

        int oldColumn = source.Column;
        int oldWithin = orderedVisible.Take(sourceIndex)
            .Count(project => project.Column == oldColumn);

        var columnItems = orderedVisible
            .Where(project => !ReferenceEquals(project, source) &&
                              project.Column == targetColumn)
            .ToList();
        targetIndex = Math.Clamp(targetIndex, 0, columnItems.Count);
        if (targetColumn == oldColumn && targetIndex == oldWithin) return false;

        orderedVisible.RemoveAt(sourceIndex);
        source.Column = targetColumn;

        int insertAt;
        if (columnItems.Count == 0)
            insertAt = Math.Min(sourceIndex, orderedVisible.Count);
        else if (targetIndex >= columnItems.Count)
            insertAt = orderedVisible.IndexOf(columnItems[^1]) + 1;
        else
            insertAt = orderedVisible.IndexOf(columnItems[targetIndex]);
        orderedVisible.Insert(Math.Clamp(insertAt, 0, orderedVisible.Count), source);

        var desired = collection.ToList();
        int visibleIndex = 0;
        for (int i = 0; i < desired.Count; i++)
            if (visibleSet.Contains(desired[i]))
                desired[i] = orderedVisible[visibleIndex++];
        SyncCollection(collection, desired);
        return true;
    }

    private void TryStartTabDrag(TabItemBase s)
    {
        if (s is DocumentGroupItem documentGroup)
        {
            TryStartDocumentGroupDrag(documentGroup);
            return;
        }

        var project = CurrentProjects.FirstOrDefault(pr => pr.Tabs.Contains(s));
        if (project == null) return;
        if (GetProjectContainer(project) is not DependencyObject pc) return;

        // 자식 세션은 같은 부모의 형제끼리만 재정렬한다. 최상위 행은 부모 세션의 자식들을 한 드래그 단위로 묶는다.
        ItemsControl? group = null;
        foreach (var ic in FindVisualChildren<ItemsControl>(pc))
            if (ic.ItemContainerGenerator.ContainerFromItem(s) is FrameworkElement) { group = ic; break; }
        if (group == null) return;

        string? siblingParentId = (s as SessionItem)?.ParentSessionId;
        bool siblingsOnly = !string.IsNullOrEmpty(siblingParentId);
        var allRows = new List<(TabItemBase Item, FrameworkElement Element)>();
        foreach (var item in group.Items)
        {
            if (item is not TabItemBase tab
                || group.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement element)
                continue;
            allRows.Add((tab, element));
        }

        // 검색, 부모 접힘, 숨김 토글로 높이가 0인 컨테이너는 드래그 인덱스에서 제외한다.
        // ItemsControl.Items에는 계속 남아 있으므로 별도 필터가 없으면 보이지 않는 행 기준으로 이동한다.
        var visibleRows = allRows
            .Where(row => row.Element.IsVisible
                          && row.Element.ActualWidth > 0.5
                          && row.Element.ActualHeight > 0.5)
            .ToList();

        var rows = siblingsOnly
            ? visibleRows.Where(row => row.Item is SessionItem sibling
                && StringComparer.Ordinal.Equals(sibling.ParentSessionId, siblingParentId)
                && sibling.Hidden == ((SessionItem)s).Hidden).ToList()
            : visibleRows.Where(row => row.Item is not SessionItem { ParentSessionId: not null }).ToList();
        if (siblingsOnly && rows.Count < 2) return;

        IReadOnlyList<FrameworkElement> GroupElements(TabItemBase item)
        {
            if (siblingsOnly || item is not SessionItem root)
                return rows.Where(row => ReferenceEquals(row.Item, item) && row.Element.IsVisible)
                    .Select(row => row.Element)
                    .ToList();

            var subtree = project.GetSessionSubtree(root).ToHashSet();
            return visibleRows
                .Where(row => row.Item is SessionItem session
                    && subtree.Contains(session))
                .Select(row => row.Element)
                .ToList();
        }

        var groupItems = rows.Select(row => row.Item).ToList();
        var visibleSiblings = siblingsOnly
            ? groupItems.OfType<SessionItem>().ToList()
            : null;
        var src = rows.FirstOrDefault(row => ReferenceEquals(row.Item, s));
        if (src.Element == null) return;

        bool CanDropIntoSession(TabItemBase source, TabItemBase target)
            => !siblingsOnly
               && source is SessionItem { ParentSessionId: null } child
               && target is SessionItem parent
               && project.CanSetSessionParent(child, parent);
        bool CanDropIntoTarget(TabItemBase source, TabItemBase target)
            => CanDropIntoSession(source, target)
               || (!siblingsOnly
                   && source is FileTabItem file
                   && target is DocumentGroupItem documentGroup
                   && project.CanAddDocumentToGroup(documentGroup, file));

        // 세션 자식 편입은 우측 화살표만, 문서 그룹 편입은 그룹 항목 전체를 드롭 영역으로 사용한다.
        var dropHandles = new Dictionary<TabItemBase, FrameworkElement?>();
        FrameworkElement? DropIntoHandle(TabItemBase item)
        {
            if (dropHandles.TryGetValue(item, out var cached)) return cached;
            var container = rows.FirstOrDefault(row => ReferenceEquals(row.Item, item)).Element;
            var handle = item is DocumentGroupItem
                ? container
                : container == null
                ? null
                : FindVisualChildren<FrameworkElement>(container)
                    .FirstOrDefault(element => element.Name == "SessionChildDropHandle");
            dropHandles[item] = handle;
            return handle;
        }

        // 편입 가능한 대상(자식 세션·자기 자신·순환 대상 제외)에만 화살표를 띄운다.
        ShowSessionChildDropHints(rows
            .Select(row => row.Item)
            .OfType<SessionItem>()
            .Where(item => CanDropIntoSession(s, item)));

        _tabDrag = ReorderDrag<TabItemBase>.TryStart(this, rows, s, src.Element,
            (item, hostTarget, _) =>
            {
                int to = Math.Clamp(hostTarget, 0, groupItems.Count - 1);
                int groupFrom = groupItems.IndexOf(item);
                if (item is SessionItem movedSession)
                {
                    bool changed;
                    if (siblingsOnly && groupItems[to] is SessionItem siblingTarget)
                        changed = project.MoveSessionWithinSiblings(
                            movedSession,
                            siblingTarget,
                            after: groupFrom < to,
                            visibleSiblings: visibleSiblings);
                    else
                        changed = project.MoveSessionSubtreeRelativeToTab(
                            movedSession,
                            groupItems[to],
                            after: groupFrom < to);
                    if (changed) SessionsReordered?.Invoke(project);
                    return Task.CompletedTask;
                }

                if (project.MoveStandaloneTabRelativeToTab(
                        item,
                        groupItems[to],
                        after: groupFrom < to))
                    SessionsReordered?.Invoke(project);
                return Task.CompletedTask;
            }, exactFollow: true,
            canDropInto: CanDropIntoTarget,
            dropIntoHitTest: (target, pointer) =>
                IsPointerOverChildDropHandle(DropIntoHandle(target), pointer),
            dropIntoPreviewChanged: SetSessionChildDropPreview,
            onDropInto: (source, target) =>
            {
                if (source is FileTabItem file && target is DocumentGroupItem documentGroup
                    && project.AddDocumentToGroup(documentGroup, file))
                {
                    SessionsReordered?.Invoke(project);
                    return Task.CompletedTask;
                }
                if (source is SessionItem child && target is SessionItem parent
                    && project.SetSessionParent(child, parent))
                    SessionsReordered?.Invoke(project);
                return Task.CompletedTask;
            },
            groupedElements: GroupElements,
            hitTestSlots: true);
        if (_tabDrag != null)
        {
            _didDrag = true;
            CaptureMouse();
        }
        else
        {
            ClearSessionChildDropHints();
        }
        _pendingTab = null;
    }

    private void TryStartDocumentGroupDrag(DocumentGroupItem source)
    {
        var project = CurrentProjects.FirstOrDefault(item => item.DocumentGroups.Contains(source));
        if (project == null || GetProjectContainer(project) is not DependencyObject projectContainer) return;

        ItemsControl? host = null;
        foreach (var itemsControl in FindVisualChildren<ItemsControl>(projectContainer))
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromItem(source) is FrameworkElement)
            {
                host = itemsControl;
                break;
            }
        }
        if (host == null) return;

        var rows = new List<(TabItemBase Item, FrameworkElement Element)>();
        foreach (var item in host.Items.OfType<TabItemBase>())
        {
            if (host.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement element
                || !element.IsVisible || element.ActualHeight <= 0.5)
                continue;
            rows.Add((item, element));
        }
        if (rows.Count < 2) return;

        var sourceRow = rows.FirstOrDefault(row => ReferenceEquals(row.Item, source));
        if (sourceRow.Element == null) return;
        var items = rows.Select(row => row.Item).ToList();
        _tabDrag = ReorderDrag<TabItemBase>.TryStart(this, rows, source, sourceRow.Element,
            (item, hostTarget, _) =>
            {
                if (item is not DocumentGroupItem group) return Task.CompletedTask;
                int targetIndex = Math.Clamp(hostTarget, 0, items.Count - 1);
                int sourceIndex = items.IndexOf(item);
                if (project.MoveDocumentGroupRelativeToItem(
                        group,
                        items[targetIndex],
                        after: sourceIndex < targetIndex))
                    SessionsReordered?.Invoke(project);
                return Task.CompletedTask;
            }, exactFollow: true, hitTestSlots: true);

        if (_tabDrag != null)
        {
            _didDrag = true;
            CaptureMouse();
        }
        _pendingTab = null;
    }

    /// <summary>세션 드래그 중 편입 가능한 대상 행 우측에 자식 편입 화살표를 표시한다.</summary>
    private void ShowSessionChildDropHints(IEnumerable<SessionItem> targets)
    {
        ClearSessionChildDropHints();
        foreach (var session in targets)
        {
            session.IsChildDropHintVisible = true;
            _sessionChildDropHints.Add(session);
        }
        // 화살표가 행 폭을 차지하므로, 드래그 슬롯 좌표를 잡기 전에 새 레이아웃을 확정한다.
        if (_sessionChildDropHints.Count > 0) UpdateLayout();
    }

    private void ClearSessionChildDropHints()
    {
        foreach (var session in _sessionChildDropHints)
        {
            session.IsChildDropHintVisible = false;
            session.IsChildDropHintActive = false;
        }
        _sessionChildDropHints.Clear();
        if (_sessionChildDropHintActive != null)
        {
            _sessionChildDropHintActive.IsChildDropHintActive = false;
            _sessionChildDropHintActive = null;
        }
    }

    /// <summary>포인터가 대상 세션 행의 자식 편입 화살표 위에 있는지. 화살표는 재정렬 애니메이션과
    /// 함께 움직이므로 드래그 시작 시 캡처한 좌표가 아니라 현재 화면 위치로 판정한다.</summary>
    private bool IsPointerOverChildDropHandle(FrameworkElement? handle, Point pointer)
    {
        const double padding = 3;
        if (handle == null || !handle.IsVisible
            || handle.ActualWidth <= 0.5 || handle.ActualHeight <= 0.5) return false;
        try
        {
            var origin = handle.TransformToAncestor(this).Transform(new Point());
            var bounds = new Rect(
                origin.X - padding,
                origin.Y - padding,
                handle.ActualWidth + padding * 2,
                handle.ActualHeight + padding * 2);
            return bounds.Contains(pointer);
        }
        catch { return false; }
    }

    private void SetSessionChildDropPreview(TabItemBase? target, FrameworkElement? container)
    {
        if (_sessionChildDropTarget != null)
            _sessionChildDropTarget.ClearValue(Border.BorderBrushProperty);
        _sessionChildDropTarget = null;
        if (_sessionChildDropHintActive != null)
        {
            _sessionChildDropHintActive.IsChildDropHintActive = false;
            _sessionChildDropHintActive = null;
        }

        if (container == null) return;
        if (target is SessionItem session)
        {
            session.IsChildDropHintActive = true;
            _sessionChildDropHintActive = session;
            _sessionChildDropTarget = FindVisualChildren<Border>(container)
                .FirstOrDefault(border => border.Name == "SessionRow");
        }
        else if (target is DocumentGroupItem)
        {
            _sessionChildDropTarget = FindVisualChildren<Border>(container)
                .FirstOrDefault(border => border.Name == "DocumentGroupRow");
        }
        _sessionChildDropTarget?.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
    }

    private void TryStartFileDrag(ProjectFile f)
    {
        var project = CurrentProjects.FirstOrDefault(pr => pr.Files.Contains(f));
        if (project == null) return;
        var rows = GetFileRows(project).ToList();
        var src = rows.FirstOrDefault(r => ReferenceEquals(r.Item, f));
        if (src.Element == null) return;

        _fileDrag = ReorderDrag<ProjectFile>.TryStart(this, rows, f, src.Element,
            (file, hostTarget, _) =>
            {
                int from = project.Files.IndexOf(file);
                if (from < 0) return Task.CompletedTask;
                int to = Math.Clamp(hostTarget, 0, project.Files.Count - 1);
                if (to != from) { project.Files.Move(from, to); FilesReordered?.Invoke(project); }
                return Task.CompletedTask;
            }, exactFollow: true);
        if (_fileDrag != null) { _didDrag = true; CaptureMouse(); }
        _pendingFile = null;
    }

    private IEnumerable<(ProjectItem Item, FrameworkElement Element)> GetProjectRows()
    {
        var root = _archiveOpen ? (DependencyObject)ArchivePanel : ActivePanel;
        foreach (var border in FindVisualChildren<Border>(root))
            if (border.Name == "ProjectCardRoot" && border.DataContext is ProjectItem project)
                yield return (project, border);
    }

    private FrameworkElement? GetProjectContainer(ProjectItem project)
        => GetProjectRows()
            .FirstOrDefault(row => ReferenceEquals(row.Item, project))
            .Element;

    private IEnumerable<(ProjectFile Item, FrameworkElement Element)> GetFileRows(ProjectItem project)
    {
        if (GetProjectContainer(project) is not DependencyObject container)
            yield break;
        var lists = FindVisualChildren<ItemsControl>(container).ToList();
        foreach (var file in project.Files)
            foreach (var inner in lists)
                if (inner.ItemContainerGenerator.ContainerFromItem(file) is FrameworkElement element)
                {
                    yield return (file, element);
                    break;
                }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) yield break;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in FindVisualChildren<T>(c)) yield return x;
        }
    }
}
