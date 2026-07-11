using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>좌측 사이드바 — 프로젝트(디렉터리) → 세션 트리. 동작은 이벤트로 MainWindow에 위임.</summary>
public partial class SidebarView : UserControl
{
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
        WorkspaceStore.ProjectFolders.CollectionChanged += ProjectFolders_CollectionChanged;
        PreviewMouseMove += Sidebar_PreviewMouseMove;
        PreviewMouseLeftButtonUp += Sidebar_PreviewMouseUp;
        LostMouseCapture += Sidebar_LostCapture;
    }

    public event Action? AddProjectRequested;
    public event Action<ProjectItem>? ProjectSelected;
    public event Action<ProjectItem>? AddSessionRequested;
    public event Action<ProjectItem>? ProjectDeleteRequested;
    /// <summary>프로젝트 메뉴 "이름 변경" 요청(MainWindow 위임).</summary>
    public event Action<ProjectItem>? ProjectRenameRequested;
    /// <summary>프로젝트 메뉴 "보관함 이동" — 활성에서 보관함으로(MainWindow 위임).</summary>
    public event Action<ProjectItem>? ProjectArchiveRequested;
    /// <summary>보관함 카드 "꺼내기" — 보관함에서 활성으로(MainWindow 위임).</summary>
    public event Action<ProjectItem>? ProjectUnarchiveRequested;
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
    /// <summary>카드의 열린 문서(파일 탭) 행 클릭 — 해당 파일 탭을 활성화(MainWindow 위임).</summary>
    public event Action<FileTabItem>? OpenDocSelected;
    /// <summary>카드 문서 우클릭 "문서 닫기" — 해당 파일 탭을 닫는다(MainWindow 위임).</summary>
    public event Action<FileTabItem>? OpenDocCloseRequested;
    /// <summary>카드의 웹 브라우저 탭 클릭/닫기 요청.</summary>
    public event Action<BrowserTabItem>? BrowserTabSelected;
    public event Action<BrowserTabItem>? BrowserTabCloseRequested;
    public event Action<BrowserTabItem>? BrowserTabRenameRequested;
    public event Action<SessionItem>? SessionDeleteRequested;
    public event Action<SessionItem>? SessionRenameRequested;
    public event Action<SessionItem>? SessionStopTrackingRequested;
    /// <summary>세션 메뉴 "세션 숨기기" — 탭 X 숨기기와 동일(MainWindow 위임).</summary>
    public event Action<SessionItem>? SessionHideRequested;
    /// <summary>세션 메뉴 "포크" 요청(MainWindow 위임) — 원본 대화를 복사한 새 세션 생성.</summary>
    public event Action<SessionItem>? SessionForkRequested;
    /// <summary>세션 메뉴 "내보내기" 요청(MainWindow 위임) — 대화를 .md 로 저장.</summary>
    public event Action<SessionItem>? SessionExportRequested;
    /// <summary>세션 메뉴 "잠금/잠금 해제" 요청(MainWindow 위임).</summary>
    public event Action<SessionItem>? SessionLockRequested;

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

        if ((e.PropertyName == nameof(ProjectItem.HasBusySession) ||
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
                !hasQuery || folderMatches || Matches(project)).ToList();
            SyncCollection(folder.Projects, desired);
            folder.UpdateSummary(allProjects, desired.Count, hasQuery);
            folder.IsSearchVisible = !hasQuery || folderMatches || desired.Count > 0;
            if (hasQuery && folder.IsSearchVisible) folder.IsExpanded = true;
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
            ProjectItem project => !hasQuery || Matches(project),
            _ => false,
        }).ToList());
        SyncCollection(_archivedRootItems, archivedRoots.Where(item => item switch
        {
            ProjectFolderItem folder => folder.IsSearchVisible,
            ProjectItem project => !hasQuery || Matches(project),
            _ => false,
        }).ToList());
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

    private void ArchiveToggleBtn_Click(object sender, RoutedEventArgs e) => OpenArchivePanel();
    private void ArchiveBack_Click(object sender, RoutedEventArgs e) => CloseArchivePanel();

    private void OpenArchivePanel()
    {
        if (_archiveOpen) return;
        _archiveOpen = true;
        UpdateArchiveEmptyState();
        UpdateExpandAllVisual();
        // 검색 초기화(보기 전환 시 필터 리셋)
        if (SidebarSearchBox.Text.Length > 0) SidebarSearchBox.Clear();

        double w = ActualWidth > 0 ? ActualWidth : 262;
        ArchivePanel.Visibility = Visibility.Visible;
        SlideTo(ArchivePanelTransform, w, 0);
        SlideTo(ActivePanelTransform, 0, -w, () => ActivePanel.Visibility = Visibility.Collapsed);

        HeaderTitle.Text = "보관함";
        BackBtn.Visibility = Visibility.Visible;
        ArchiveToggleBtn.Visibility = Visibility.Collapsed;
    }

    private void CloseArchivePanel()
    {
        if (!_archiveOpen) return;
        _archiveOpen = false;
        if (SidebarSearchBox.Text.Length > 0) SidebarSearchBox.Clear();

        double w = ActualWidth > 0 ? ActualWidth : 262;
        ActivePanel.Visibility = Visibility.Visible;
        SlideTo(ActivePanelTransform, -w, 0);
        SlideTo(ArchivePanelTransform, 0, w, () => ArchivePanel.Visibility = Visibility.Collapsed);

        HeaderTitle.Text = "프로젝트";
        BackBtn.Visibility = Visibility.Collapsed;
        ArchiveToggleBtn.Visibility = Visibility.Visible;
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
    private void UpdateButton_Click(object sender, RoutedEventArgs e) => UpdateClicked?.Invoke();

    /// <summary>좌측 패널 하단에 "업데이트 v{version}" 버튼 표시(devez 정합).</summary>
    public void ShowUpdateButton(string version)
    {
        UpdateButton.Tag = version;
        UpdateButton.Visibility = Visibility.Visible;
    }

    /// <summary>업데이트 버튼 숨김(설치 진행 중 등).</summary>
    public void HideUpdateButton() => UpdateButton.Visibility = Visibility.Collapsed;


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
    {
        _searchOpen = !_searchOpen;
        var anim = new DoubleAnimation
        {
            To = _searchOpen ? SearchRowHeight : 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        SearchRow.BeginAnimation(FrameworkElement.HeightProperty, anim);

        if (_searchOpen)
        {
            // 펼친 직후 포커스 + 기존 텍스트 전체 선택 (연속 재검색 편의)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SidebarSearchBox.Focus();
                SidebarSearchBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
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
        if (_sidebarSearchQuery.Length > 0)
            foreach (var project in CurrentProjects)
                if (!project.Name.Contains(_sidebarSearchQuery, StringComparison.OrdinalIgnoreCase) &&
                    project.Sessions.Any(session =>
                        session.Name.Contains(_sidebarSearchQuery, StringComparison.OrdinalIgnoreCase)))
                    project.IsExpanded = true;
        RefreshProjectGroups();
    }

    private void SidebarSearchClear_Click(object sender, RoutedEventArgs e)
    {
        SidebarSearchBox.Clear();
        SidebarSearchBox.Focus();
    }

    private void ProjectScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        // 패딩을 스크롤 상태로 토글하면 콘텐츠 높이가 바뀌어 오버플로 여부가 뒤집히고,
        // 그 결과 스크롤/페이드가 무한히 켜졌다 꺼져 맨 아래 카드 보더가 깜빡인다 → 패딩 고정.
        // 0.5px 여유로 서브픽셀 오프셋에 의한 페이드 깜빡임도 방지.
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
            ProjectSelected?.Invoke(p);
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

    private void Session_Click(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) { _didDrag = false; return; }
        if (sender is FrameworkElement { DataContext: SessionItem s })
        {
            // 비선택 프로젝트 세션 클릭도 OpenSession 이 프로젝트 전환까지 처리.
            // (ProjectSelected 를 따로 호출하면 첫 세션이 추가로 로드되므로 호출하지 않음)
            SessionSelected?.Invoke(s);
        }
    }

    private void SessionRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (FindProjectCardRoot(sender as DependencyObject) is { } projectCard)
            projectCard.Tag = true;
    }

    private void SessionRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (FindProjectCardRoot(sender as DependencyObject) is { } projectCard)
            projectCard.ClearValue(FrameworkElement.TagProperty);
    }

    private static Border? FindProjectCardRoot(DependencyObject? current)
    {
        for (; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is Border { Name: "ProjectCardRoot" } projectCard)
                return projectCard;
        return null;
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

    private void ProjectRename_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) ProjectRenameRequested?.Invoke(p);
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
    private void ProjectMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;

        var parent = cm.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "바로가기");
        if (parent is null) return;
        // 서브메뉴 헤더 자식의 Tag={Binding} 은 Opened 시점에 아직 평가 안 됐을 수 있어 null 가능 →
        // ContextMenu.DataContext(타겟에서 상속)로 프로젝트를 얻는다. 폴백으로 PlacementTarget·Tag.
        var p = parent.Tag as ProjectItem
                ?? cm.DataContext as ProjectItem
                ?? (cm.PlacementTarget as FrameworkElement)?.DataContext as ProjectItem;
        if (p is null) return;

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

    private void SessionDetach_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is not { ParentSessionId: not null } session) return;
        var project = CurrentProjects.FirstOrDefault(item => item.Sessions.Contains(session));
        if (project != null && project.DetachSessionAsRoot(session))
            SessionsReordered?.Invoke(project);
    }

    private void SessionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionDeleteRequested?.Invoke(s);
    }

    private void SessionRename_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionRenameRequested?.Invoke(s);
    }

    private void SessionFork_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionForkRequested?.Invoke(s);
    }

    private void SessionExport_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionExportRequested?.Invoke(s);
    }

    private void SessionStopTracking_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionStopTrackingRequested?.Invoke(s);
    }

    private void SessionHide_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionHideRequested?.Invoke(s);
    }

    private void SessionLock_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionLockRequested?.Invoke(s);
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
    private bool _didDrag;
    private ProjectItem? _draggedProject;
    private ProjectFolderItem? _projectFolderDropTarget;
    private Border? _projectFolderDropBorder;
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
            ? null
            : (sender as FrameworkElement)?.DataContext as TabItemBase;
        _pendingFolder = null;
        _pendingProject = null;
        _pendingFile = null;
        _didDrag = false;
    }

    private void ProjectFileRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pendingFile = (sender as FrameworkElement)?.DataContext as ProjectFile;
        _pendingFolder = null;
        _pendingProject = null;
        _pendingTab = null;
        _didDrag = false;
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

    private bool UpdateActiveDrag(MouseEventArgs e)
    {
        if (_rootDrag != null)
        {
            _rootDrag.Update(e);
            if (_draggedProject != null) UpdateProjectFolderDropPreview(e.GetPosition(this));
            return true;
        }
        if (_projectDrag != null)
        {
            _projectDrag.Update(e);
            UpdateProjectGridHeightPreview(_projectDrag.CurrentTargetColumn);
            UpdateProjectFolderDropPreview(e.GetPosition(this));
            return true;
        }
        if (_tabDrag != null) { _tabDrag.Update(e); return true; }
        if (_fileDrag != null) { _fileDrag.Update(e); return true; }
        return false;
    }

    private async void Sidebar_PreviewMouseUp(object sender, MouseButtonEventArgs e) => await EndDragAsync(commit: true);
    private async void Sidebar_LostCapture(object sender, MouseEventArgs e)
        => await EndDragAsync(commit: e.LeftButton != MouseButtonState.Pressed);

    private async Task EndDragAsync(bool commit)
    {
        if (_endingDrag) return;
        _endingDrag = true;
        try
        {
            var rootDrag = _rootDrag;
            var projectDrag = _projectDrag;
            var tabDrag = _tabDrag;
            var fileDrag = _fileDrag;
            var draggedProject = _draggedProject;
            var dropFolder = draggedProject == null
                ? null
                : FindProjectFolderDropTarget(Mouse.GetPosition(this), draggedProject).Folder;
            bool applyDeferredSearch = _sidebarSearchDeferredForDrag;
            _sidebarSearchDeferredForDrag = false;
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
        }
        finally
        {
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

    private void UpdateProjectFolderDropPreview(Point point)
    {
        ClearProjectFolderDropPreview();
        var draggedProject = _draggedProject;
        if (draggedProject == null) return;

        var target = FindProjectFolderDropTarget(point, draggedProject);
        if (target.Folder == null) return;
        _projectFolderDropTarget = target.Folder;
        if (draggedProject.FolderId == target.Folder.Id) return;

        _projectFolderDropBorder = target.PreviewBorder;
        _projectFolderDropBorder?.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
    }

    /// <summary>프로젝트 드롭 대상 폴더를 현재 포인터에서 직접 판정한다. 같은 폴더 내부 카드는
    /// 기존 재정렬을 유지하고, 다른 폴더는 헤더뿐 아니라 폴더 전체 영역에서 받아들인다.</summary>
    private (ProjectFolderItem? Folder, Border? PreviewBorder) FindProjectFolderDropTarget(
        Point point,
        ProjectItem draggedProject)
    {
        for (DependencyObject? current = InputHitTest(point) as DependencyObject;
             current != null && !ReferenceEquals(current, this);
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is not Border { DataContext: ProjectFolderItem folder } border)
                continue;

            if (border.Name == "FolderRoot" && draggedProject.FolderId != folder.Id)
                return (folder, border);
            if (border.Name != "FolderHeader") continue;

            var previewBorder = border;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(border);
                 parent != null && !ReferenceEquals(parent, this);
                 parent = VisualTreeHelper.GetParent(parent))
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
        if (_projectFolderDropBorder != null)
            _projectFolderDropBorder.ClearValue(Border.BorderBrushProperty);
        _projectFolderDropBorder = null;
        _projectFolderDropTarget = null;
    }

    private void TryStartFolderDrag(ProjectFolderItem folder)
        => TryStartRootDrag(folder, folder.IsArchived);

    private void TryStartRootDrag(object item, bool archived)
    {
        var host = archived ? ArchivedHost : ProjectsHost;
        var rows = new List<(object Item, FrameworkElement Element)>();
        foreach (var rootItem in host.Items.Cast<object>())
            if (host.ItemContainerGenerator.ContainerFromItem(rootItem) is FrameworkElement element)
                rows.Add((rootItem, element));

        var source = rows.FirstOrDefault(row => ReferenceEquals(row.Item, item));
        if (source.Element == null) return;

        double gridMidX = _projectColumns >= 2
            ? ComputeColumnsMidX(source.Element)
            : double.PositiveInfinity;
        var visibleItems = rows.Select(row => row.Item).ToList();
        var panel = FindVisualChildren<ProjectColumnsPanel>(host).FirstOrDefault();
        var visibleRoots = archived ? _archivedRootItems : _activeRootItems;
        int originalColumn = item is ProjectItem sourceProject ? sourceProject.Column : 0;
        List<object>? lastFolderPreviewOrder = null;
        double? folderHitTestX = null;
        if (_projectColumns >= 2 && item is ProjectFolderItem)
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

            ProjectItem? project = item as ProjectItem;
            int targetColumn = project == null || targetItem == null || double.IsPositiveInfinity(gridMidX)
                ? originalColumn
                : Mouse.GetPosition(this).X >= gridMidX ? 1 : 0;
            bool orderChanged = !visibleRoots.SequenceEqual(preview);
            bool columnChanged = project != null && project.Column != targetColumn;
            if (!orderChanged && !columnChanged) return;

            void ApplyPreviewLayout()
            {
                SyncCollection(visibleRoots, preview);
                if (project != null) project.Column = targetColumn;
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

        _rootDrag = ReorderDrag<object>.TryStart(
            this, rows, item, source.Element,
            (sourceItem, hostTarget, _) =>
            {
                bool columnChanged = false;
                if (sourceItem is ProjectItem project && _projectColumns >= 2 &&
                    !double.IsPositiveInfinity(gridMidX))
                {
                    int targetColumn = Mouse.GetPosition(this).X >= gridMidX ? 1 : 0;
                    columnChanged = project.Column != targetColumn;
                    project.Column = targetColumn;
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
                if (orderChanged || columnChanged)
                    ProjectsReordered?.Invoke();
                return Task.CompletedTask;
            },
            exactFollow: true,
            commitUnchanged: item is ProjectItem && _projectColumns >= 2,
            reorderPreviewChanged: PreviewRootMove,
            hitTestSlots: true,
            suppressDisplacement: _projectColumns >= 2,
            preserveRowOrder: true,
            hitTestXOverride: folderHitTestX);

        if (_rootDrag != null)
        {
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

        if (_projectColumns >= 2)
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
                includeElementMarginsInBounds: true);
        }
        else
        {
            _projectDrag = ReorderDrag<ProjectItem>.TryStart(this, rows, p, src.Element,
                (project, hostTarget, _) =>
                {
                    if (MoveProjectWithinVisible(coll, project, hostTarget, visibleItems))
                        ProjectsReordered?.Invoke();
                    return Task.CompletedTask;
                }, exactFollow: true);
        }

        if (_projectDrag != null)
        {
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

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        for (; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
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
            canDropInto: (source, target) => !siblingsOnly
                && source is SessionItem { ParentSessionId: null } child
                && target is SessionItem parent
                && project.CanSetSessionParent(child, parent),
            dropIntoPreviewChanged: SetSessionChildDropPreview,
            onDropInto: (source, target) =>
            {
                if (source is SessionItem child && target is SessionItem parent
                    && project.SetSessionParent(child, parent))
                    SessionsReordered?.Invoke(project);
                return Task.CompletedTask;
            },
            groupedElements: GroupElements);
        if (_tabDrag != null)
        {
            _didDrag = true;
            CaptureMouse();
        }
        _pendingTab = null;
    }
    private void SetSessionChildDropPreview(TabItemBase? target, FrameworkElement? container)
    {
        if (_sessionChildDropTarget != null)
            _sessionChildDropTarget.ClearValue(Border.BorderBrushProperty);
        _sessionChildDropTarget = null;

        if (target is not SessionItem || container == null) return;
        _sessionChildDropTarget = FindVisualChildren<Border>(container)
            .FirstOrDefault(border => border.Name == "SessionRow");
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
