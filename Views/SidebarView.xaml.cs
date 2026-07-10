using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>좌측 사이드바 — 프로젝트(디렉터리) → 세션 트리. 동작은 이벤트로 MainWindow에 위임.</summary>
public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
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
    // 카드 접힘/펼침 변경 → 영속 저장 트리거 (검색 자동 펼침은 제외, 명시 토글만).
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

    private ObservableCollection<ProjectItem>? _projects;
    public ObservableCollection<ProjectItem> Projects
    {
        get => _projects ??= new();
        set { _projects = value; ProjectsHost.ItemsSource = value; }
    }

    private ObservableCollection<ProjectItem>? _archivedProjects;
    /// <summary>보관함 프로젝트 — 활성과 동일한 ProjectTemplate 로 보관함 패널에 표시.</summary>
    public ObservableCollection<ProjectItem> ArchivedProjects
    {
        get => _archivedProjects ??= new();
        set
        {
            if (_archivedProjects != null) _archivedProjects.CollectionChanged -= OnArchivedChanged;
            _archivedProjects = value;
            ArchivedHost.ItemsSource = value;
            value.CollectionChanged += OnArchivedChanged;
            UpdateArchiveEmptyState();
        }
    }

    private void OnArchivedChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateArchiveEmptyState();

    private void UpdateArchiveEmptyState()
    {
        if (ArchiveEmptyText != null)
            ArchiveEmptyText.Visibility = ArchivedProjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 보관함 슬라이드 전환 (devez 정합: 프로젝트↔보관함, 헤더 제목·뒤로가기 교체) ──────────
    private bool _archiveOpen;

    /// <summary>현재 보기의 대상 컬렉션(검색/일괄펼침/드래그 공용).</summary>
    private ObservableCollection<ProjectItem> CurrentProjects => _archiveOpen ? ArchivedProjects : Projects;
    private ItemsControl CurrentHost => _archiveOpen ? ArchivedHost : ProjectsHost;

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


    private void AddProject_Click(object sender, RoutedEventArgs e) => AddProjectRequested?.Invoke();

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
        foreach (var p in CurrentProjects) p.IsExpanded = target;
        UpdateExpandAllVisual();
        ProjectExpandChanged?.Invoke();
    }

    private bool AreAllExpanded() => CurrentProjects.Count > 0 && CurrentProjects.All(p => p.IsExpanded);

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
        var q = SidebarSearchBox.Text?.Trim() ?? "";
        if (q.Length == 0) { CurrentHost.ItemsSource = CurrentProjects; return; }

        var filtered = CurrentProjects.Where(p =>
            p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            p.Sessions.Any(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        // 세션만 매칭된 프로젝트는 펼쳐서 해당 세션이 보이게 한다.
        foreach (var p in filtered)
            if (!p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                p.IsExpanded = true;
        CurrentHost.ItemsSource = filtered;
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
    private ProjectItem? _pendingProject;
    private TabItemBase? _pendingTab;   // 카드 그룹의 세션/문서 행(드래그 재정렬 대상)
    private ProjectFile? _pendingFile;
    private ReorderDrag<ProjectItem>? _projectDrag;
    private ReorderDrag<TabItemBase>? _tabDrag;
    private ReorderDrag<ProjectFile>? _fileDrag;
    private Border? _sessionChildDropTarget;
    private bool _didDrag;

    private void ProjectRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        // chevron 등 버튼 위에서 누른 경우 드래그를 무장하지 않는다(버튼 동작 보존).
        _pendingProject = IsWithinButton(e.OriginalSource as DependencyObject)
            ? null : (sender as FrameworkElement)?.DataContext as ProjectItem;
        _pendingTab = null;
        _pendingFile = null;
        _didDrag = false;
    }

    private static bool IsWithinButton(DependencyObject? d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

    // 카드 그룹의 세션/문서 행 공용 — 드래그 재정렬 대상(TabItemBase).
    private void SessionRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pendingTab = (sender as FrameworkElement)?.DataContext as TabItemBase;
        _pendingProject = null;
        _pendingFile = null;
        _didDrag = false;
    }

    private void ProjectFileRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pendingFile = (sender as FrameworkElement)?.DataContext as ProjectFile;
        _pendingProject = null;
        _pendingTab = null;
        _didDrag = false;
    }

    private void Sidebar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_projectDrag != null) { _projectDrag.Update(e); return; }
        if (_tabDrag != null) { _tabDrag.Update(e); return; }
        if (_fileDrag != null) { _fileDrag.Update(e); return; }
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = _pressOrigin - e.GetPosition(this);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (_pendingProject != null) TryStartProjectDrag(_pendingProject);
        else if (_pendingTab != null) TryStartTabDrag(_pendingTab);
        else if (_pendingFile != null) TryStartFileDrag(_pendingFile);
    }

    private async void Sidebar_PreviewMouseUp(object sender, MouseButtonEventArgs e) => await EndDragAsync(commit: true);
    private async void Sidebar_LostCapture(object sender, MouseEventArgs e) => await EndDragAsync(commit: true);

    private async Task EndDragAsync(bool commit)
    {
        var pd = _projectDrag; var td = _tabDrag; var fd = _fileDrag;
        _projectDrag = null; _tabDrag = null; _fileDrag = null;
        _pendingProject = null; _pendingTab = null; _pendingFile = null;
        if (Mouse.Captured == this) ReleaseMouseCapture();
        if (pd != null) await pd.FinishAsync(commit);
        if (td != null) await td.FinishAsync(commit);
        if (fd != null) await fd.FinishAsync(commit);
    }

    private void TryStartProjectDrag(ProjectItem p)
    {
        var rows = GetProjectRows().ToList();
        var src = rows.FirstOrDefault(r => ReferenceEquals(r.Item, p));
        if (src.Element == null) return;

        var coll = CurrentProjects;

        if (_projectColumns >= 2)
        {
            // 2열: 목표 컬럼 + 컬럼 내 위치로 커밋. midX 는 패널 가운데(coordHost=this 기준).
            double midX = ComputeColumnsMidX();
            _projectDrag = ReorderDrag<ProjectItem>.TryStart(this, rows, p, src.Element,
                (s, targetCol, targetIdx) =>
                {
                    if (MoveProjectToColumn(coll, s, targetCol, targetIdx)) ProjectsReordered?.Invoke();
                    return Task.CompletedTask;
                }, exactFollow: true, columns: 2, gridMidX: midX);
        }
        else
        {
            _projectDrag = ReorderDrag<ProjectItem>.TryStart(this, rows, p, src.Element,
                (s, hostTarget, _) =>
                {
                    int from = coll.IndexOf(s);
                    if (from >= 0)
                    {
                        int to = Math.Clamp(hostTarget, 0, coll.Count - 1);
                        if (to != from) { coll.Move(from, to); ProjectsReordered?.Invoke(); }
                    }
                    return Task.CompletedTask;
                }, exactFollow: true);
        }
        if (_projectDrag != null) { _didDrag = true; CaptureMouse(); }
        _pendingProject = null;
    }

    /// <summary>현재 보기의 프로젝트 패널 가운데 X(좌/우 컬럼 경계). 좌표계는 this(SidebarView).</summary>
    private double ComputeColumnsMidX()
    {
        var panel = FindVisualChildren<ProjectColumnsPanel>(CurrentHost).FirstOrDefault();
        if (panel == null || panel.ActualWidth <= 0) return double.PositiveInfinity; // 폴백: 전부 좌 컬럼 취급
        try
        {
            var origin = panel.TransformToAncestor(this).Transform(new Point(0, 0));
            return origin.X + panel.ActualWidth / 2;
        }
        catch { return double.PositiveInfinity; }
    }

    /// <summary>2열: 드롭한 컬럼/위치로 프로젝트를 옮긴다. 같은 컬럼의 같은 위치면 no-op(false 반환).
    /// 컬럼 내 상대 순서가 유지되도록 마스터 컬렉션에서 제거 후 알맞은 마스터 인덱스에 재삽입한다.</summary>
    private static bool MoveProjectToColumn(ObservableCollection<ProjectItem> coll, ProjectItem s, int targetCol, int targetIdx)
    {
        targetCol = targetCol == 1 ? 1 : 0;
        int from = coll.IndexOf(s);
        if (from < 0) return false;

        // 드롭 전 같은-컬럼 내 현재 위치(no-op 판정용).
        int oldCol = s.Column;
        int oldWithin = 0;
        for (int i = 0; i < from; i++) if (coll[i].Column == oldCol) oldWithin++;

        // 목표 컬럼의 다른 카드들(마스터 순서) — s 제외.
        var colItems = coll.Where(x => !ReferenceEquals(x, s) && x.Column == targetCol).ToList();
        targetIdx = Math.Clamp(targetIdx, 0, colItems.Count);

        if (targetCol == oldCol && targetIdx == oldWithin) return false; // 변화 없음

        coll.RemoveAt(from);
        s.Column = targetCol;

        int insertAt;
        if (colItems.Count == 0)
            insertAt = Math.Min(from, coll.Count);                 // 빈 컬럼: 위치 무관(레이아웃은 컬럼만 따름)
        else if (targetIdx >= colItems.Count)
            insertAt = coll.IndexOf(colItems[^1]) + 1;             // 컬럼 맨 끝
        else
            insertAt = coll.IndexOf(colItems[targetIdx]);          // 해당 카드 앞
        coll.Insert(Math.Clamp(insertAt, 0, coll.Count), s);
        return true;
    }

    private void TryStartTabDrag(TabItemBase s)
    {
        var project = CurrentProjects.FirstOrDefault(pr => pr.Tabs.Contains(s));
        if (project == null) return;
        if (CurrentHost.ItemContainerGenerator.ContainerFromItem(project) is not DependencyObject pc) return;

        // s 가 실제로 렌더된 그룹 ItemsControl(좌/우)을 찾고, 그 그룹 항목(세션+문서)끼리만 재정렬한다(다른 그룹으로 못 드롭).
        ItemsControl? group = null;
        foreach (var ic in FindVisualChildren<ItemsControl>(pc))
            if (ic.ItemContainerGenerator.ContainerFromItem(s) is FrameworkElement) { group = ic; break; }
        if (group == null) return;

        var rows = new List<(TabItemBase Item, FrameworkElement Element)>();
        foreach (var item in group.Items)
            if (item is TabItemBase tb && group.ItemContainerGenerator.ContainerFromItem(tb) is FrameworkElement fe)
                rows.Add((tb, fe));
        var groupItems = rows.Select(r => r.Item).ToList();
        var src = rows.FirstOrDefault(r => ReferenceEquals(r.Item, s));
        // 자식이 분할 그룹에 단독으로 표시돼도 드래그만으로 부모에서 분리할 수 있어야 한다.
        // ReorderDrag는 1개 슬롯도 지원하며, commitUnchanged가 분리 커밋을 담당한다.
        if (src.Element == null) return;
        bool detachOnDrop = s is SessionItem { ParentSessionId: not null };

        _tabDrag = ReorderDrag<TabItemBase>.TryStart(this, rows, s, src.Element,
            (item, hostTarget, _) =>
            {
                // hostTarget = 그룹 내 인덱스 → 그 위치의 그룹 항목 자리로 Tabs 안에서 이동(세션/문서 공통).
                // 대상이 같은 그룹 항목이라 ref 파티션이 유지돼 반대 그룹은 영향 없다. Tabs.Move → Sessions 동기 +
                // 탭 스트립 반영, SessionsReordered → RefreshCardGroups 로 카드 순서 갱신.
                int to = Math.Clamp(hostTarget, 0, groupItems.Count - 1);
                int groupFrom = groupItems.IndexOf(item);
                if (item is SessionItem movedSession && groupItems[to] is SessionItem targetSession)
                {
                    if (project.MoveSessionAsRootRelative(movedSession, targetSession, after: groupFrom < to))
                        SessionsReordered?.Invoke(project);
                    return Task.CompletedTask;
                }
                bool treeChanged = item is SessionItem detachedSession && detachOnDrop
                    && project.DetachSessionAsRoot(detachedSession);
                int fromIdx = project.Tabs.IndexOf(item);
                int toIdx = project.Tabs.IndexOf(groupItems[to]);
                if (fromIdx >= 0 && toIdx >= 0 && fromIdx != toIdx)
                {
                    project.Tabs.Move(fromIdx, toIdx);
                    SessionsReordered?.Invoke(project);
                }
                else if (treeChanged)
                    SessionsReordered?.Invoke(project);
                return Task.CompletedTask;
            }, exactFollow: true,
            canDropInto: (source, target) => source is SessionItem child && target is SessionItem parent
                && project.CanSetSessionParent(child, parent),
            dropIntoPreviewChanged: SetSessionChildDropPreview,
            onDropInto: (source, target) =>
            {
                if (source is SessionItem child && target is SessionItem parent
                    && project.SetSessionParent(child, parent))
                    SessionsReordered?.Invoke(project);
                return Task.CompletedTask;
            }, commitUnchanged: detachOnDrop);
        if (_tabDrag != null)
        {
            if (detachOnDrop) _tabDrag.ShowDetachedSourcePreview();
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
        foreach (var p in CurrentProjects)
            if (CurrentHost.ItemContainerGenerator.ContainerFromItem(p) is FrameworkElement fe)
                yield return (p, fe);
    }

    private IEnumerable<(ProjectFile Item, FrameworkElement Element)> GetFileRows(ProjectItem project)
    {
        if (CurrentHost.ItemContainerGenerator.ContainerFromItem(project) is not DependencyObject pc)
            yield break;
        var lists = FindVisualChildren<ItemsControl>(pc).ToList();
        foreach (var f in project.Files)
            foreach (var inner in lists)
                if (inner.ItemContainerGenerator.ContainerFromItem(f) is FrameworkElement fe)
                { yield return (f, fe); break; }
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
