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
    /// <summary>프로젝트 메뉴 "파일 추가" — 파일 다이얼로그로 등록할 파일을 고른다(MainWindow 위임).</summary>
    public event Action<ProjectItem>? AddProjectFileRequested;
    /// <summary>등록된 파일 클릭 — 편집 탭으로 연다(MainWindow 위임).</summary>
    public event Action<ProjectFile>? ProjectFileSelected;
    /// <summary>바로가기 행 제거 요청(MainWindow 위임 — Files 에서 제거 후 저장).</summary>
    public event Action<ProjectFile>? ProjectFileRemoveRequested;
    /// <summary>드래그로 프로젝트 순서가 바뀐 뒤 발생(영속 저장용).</summary>
    public event Action? ProjectsReordered;
    // 카드 접힘/펼침 변경 → 영속 저장 트리거 (검색 자동 펼침은 제외, 명시 토글만).
    public event Action? ProjectExpandChanged;
    /// <summary>드래그로 특정 프로젝트의 세션 순서가 바뀐 뒤 발생(탭 동기화 + 영속용).</summary>
    public event Action<ProjectItem>? SessionsReordered;
    public event Action<SessionItem>? SessionSelected;
    public event Action<SessionItem>? SessionDeleteRequested;
    public event Action<SessionItem>? SessionRenameRequested;
    public event Action<SessionItem>? SessionStopTrackingRequested;

    private ObservableCollection<ProjectItem>? _projects;
    public ObservableCollection<ProjectItem> Projects
    {
        get => _projects ??= new();
        set { _projects = value; ProjectsHost.ItemsSource = value; }
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
    private const double SearchRowHeight = 51; // 8(margin-top) + 35(pill) + 8(margin-bottom)

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
        foreach (var p in Projects) p.IsExpanded = target;
        UpdateExpandAllVisual();
        ProjectExpandChanged?.Invoke();
    }

    private bool AreAllExpanded() => Projects.Count > 0 && Projects.All(p => p.IsExpanded);

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
        if (q.Length == 0) { ProjectsHost.ItemsSource = Projects; return; }

        var filtered = Projects.Where(p =>
            p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            p.Sessions.Any(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        // 세션만 매칭된 프로젝트는 펼쳐서 해당 세션이 보이게 한다.
        foreach (var p in filtered)
            if (!p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                p.IsExpanded = true;
        ProjectsHost.ItemsSource = filtered;
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
            // 비선택 프로젝트 내부의 세션 클릭: 세션 선택 대신 프로젝트 선택으로 라우팅
            var project = Projects.FirstOrDefault(p => p.Sessions.Contains(s));
            if (project != null && !project.IsSelected)
            {
                ProjectSelected?.Invoke(project);
                return;
            }
            SessionSelected?.Invoke(s);
        }
    }

    private void AddSession_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) AddSessionRequested?.Invoke(p);
    }

    private void ProjectDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) ProjectDeleteRequested?.Invoke(p);
    }

    private void AddProjectFile_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectItem>(sender) is { } p) AddProjectFileRequested?.Invoke(p);
    }

    private void ProjectFileOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFile>(sender) is { } f) ProjectFileSelected?.Invoke(f);
    }

    /// <summary>카드 하단 바로가기 행 클릭 — 대상 실행.</summary>
    private void ProjectFileRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectFile f) ProjectFileSelected?.Invoke(f);
    }

    /// <summary>바로가기 행 우클릭 메뉴 — 제거.</summary>
    private void ProjectFileRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<ProjectFile>(sender) is { } f) ProjectFileRemoveRequested?.Invoke(f);
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

    private void SessionStopTracking_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf<SessionItem>(sender) is { } s) SessionStopTrackingRequested?.Invoke(s);
    }

    /// <summary>이벤트 소스에서 데이터 항목을 얻는다. 컨텍스트 메뉴 항목은 Tag, 행 요소는 DataContext.</summary>
    private static T? ItemOf<T>(object sender) where T : class
        => sender is FrameworkElement fe ? (fe.Tag ?? fe.DataContext) as T : null;

    // ── 드래그 순서변경 (devez ReorderDrag: 고스트 + 시프트 애니메이션) ──────────────
    private Point _pressOrigin;
    private ProjectItem? _pendingProject;
    private SessionItem? _pendingSession;
    private ReorderDrag<ProjectItem>? _projectDrag;
    private ReorderDrag<SessionItem>? _sessionDrag;
    private bool _didDrag;

    private void ProjectRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        // chevron 등 버튼 위에서 누른 경우 드래그를 무장하지 않는다(버튼 동작 보존).
        _pendingProject = IsWithinButton(e.OriginalSource as DependencyObject)
            ? null : (sender as FrameworkElement)?.DataContext as ProjectItem;
        _pendingSession = null;
        _didDrag = false;
    }

    private static bool IsWithinButton(DependencyObject? d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

    private void SessionRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pendingSession = (sender as FrameworkElement)?.DataContext as SessionItem;
        _pendingProject = null;
        _didDrag = false;
    }

    /// <summary>비선택 프로젝트 내부의 세션 우클릭 → 세션 메뉴를 막고 프로젝트 메뉴로 라우팅.
    /// (선택된 프로젝트면 세션 메뉴가 정상 노출되도록 그대로 통과.)</summary>
    private void SessionRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionItem s } fe) return;
        var project = Projects.FirstOrDefault(p => p.Sessions.Contains(s));
        if (project == null || project.IsSelected) return; // 선택된 프로젝트: 기본 동작(세션 메뉴) 유지

        // 비선택 프로젝트: 세션 메뉴를 막고 프로젝트 카드의 ContextMenu 를 마우스 위치에 띄움
        e.Handled = true;
        for (DependencyObject? d = fe; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Border { Name: "ProjectCardRoot" } card && card.ContextMenu != null)
            {
                card.ContextMenu.PlacementTarget = fe;
                card.ContextMenu.Placement = PlacementMode.MousePoint;
                card.ContextMenu.IsOpen = true;
                return;
            }
        }
    }

    private void Sidebar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_projectDrag != null) { _projectDrag.Update(e); return; }
        if (_sessionDrag != null) { _sessionDrag.Update(e); return; }
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = _pressOrigin - e.GetPosition(this);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (_pendingProject != null) TryStartProjectDrag(_pendingProject);
        else if (_pendingSession != null) TryStartSessionDrag(_pendingSession);
    }

    private async void Sidebar_PreviewMouseUp(object sender, MouseButtonEventArgs e) => await EndDragAsync(commit: true);
    private async void Sidebar_LostCapture(object sender, MouseEventArgs e) => await EndDragAsync(commit: true);

    private async Task EndDragAsync(bool commit)
    {
        var pd = _projectDrag; var sd = _sessionDrag;
        _projectDrag = null; _sessionDrag = null;
        _pendingProject = null; _pendingSession = null;
        if (Mouse.Captured == this) ReleaseMouseCapture();
        if (pd != null) await pd.FinishAsync(commit);
        if (sd != null) await sd.FinishAsync(commit);
    }

    private void TryStartProjectDrag(ProjectItem p)
    {
        var rows = GetProjectRows().ToList();
        var src = rows.FirstOrDefault(r => ReferenceEquals(r.Item, p));
        if (src.Element == null) return;

        _projectDrag = ReorderDrag<ProjectItem>.TryStart(this, rows, p, src.Element,
            (s, hostTarget, _) =>
            {
                int from = Projects.IndexOf(s);
                if (from >= 0)
                {
                    int to = Math.Clamp(hostTarget, 0, Projects.Count - 1);
                    if (to != from) { Projects.Move(from, to); ProjectsReordered?.Invoke(); }
                }
                return Task.CompletedTask;
            });
        if (_projectDrag != null) { _didDrag = true; CaptureMouse(); }
        _pendingProject = null;
    }

    private void TryStartSessionDrag(SessionItem s)
    {
        var project = Projects.FirstOrDefault(pr => pr.Sessions.Contains(s));
        if (project == null) return;
        var rows = GetSessionRows(project).ToList();
        var src = rows.FirstOrDefault(r => ReferenceEquals(r.Item, s));
        if (src.Element == null) return;

        _sessionDrag = ReorderDrag<SessionItem>.TryStart(this, rows, s, src.Element,
            (sess, hostTarget, _) =>
            {
                int from = project.Sessions.IndexOf(sess);
                if (from >= 0)
                {
                    int to = Math.Clamp(hostTarget, 0, project.Sessions.Count - 1);
                    if (to != from) { project.Sessions.Move(from, to); SessionsReordered?.Invoke(project); }
                }
                return Task.CompletedTask;
            });
        if (_sessionDrag != null) { _didDrag = true; CaptureMouse(); }
        _pendingSession = null;
    }

    private IEnumerable<(ProjectItem Item, FrameworkElement Element)> GetProjectRows()
    {
        foreach (var p in Projects)
            if (ProjectsHost.ItemContainerGenerator.ContainerFromItem(p) is FrameworkElement fe)
                yield return (p, fe);
    }

    private IEnumerable<(SessionItem Item, FrameworkElement Element)> GetSessionRows(ProjectItem project)
    {
        if (ProjectsHost.ItemContainerGenerator.ContainerFromItem(project) is not DependencyObject pc)
            yield break;
        var inner = FindVisualChildren<ItemsControl>(pc).FirstOrDefault();
        if (inner == null) yield break;
        foreach (var s in project.Sessions)
            if (inner.ItemContainerGenerator.ContainerFromItem(s) is FrameworkElement fe)
                yield return (s, fe);
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
