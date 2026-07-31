using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>우측 Git SCM 패널 — staged/unstaged 목록 + 커밋박스 + fetch/pull/push/sync.</summary>
public partial class GitScmView : UserControl
{
    private enum GitOperation { Stage, Unstage, Discard, Commit, Fetch, Pull, Push }

    /// <summary>파일 행 클릭 → 중앙에 diff 탭 열기 요청.(repo, relPath, staged)</summary>
    public event Action<string, string, bool>? DiffFileActivated;
    /// <summary>커밋/스테이지/pull/push 등 git 상태 변경(repo). 브랜치 버블 갱신용.</summary>
    public event Action<string>? GitStateChanged;

    private string? _repo;
    private bool _busy;
    private bool _refreshInProgress;
    private bool _refreshQueued;
    private FileSystemWatcher? _workTreeWatcher;
    private FileSystemWatcher? _gitMetadataWatcher;
    private string? _gitDirectory;
    private DateTime _ignoreGitMetadataUntilUtc;
    private readonly System.Windows.Threading.DispatcherTimer _repoRefreshTimer;
    private BranchState _branch = new();
    private readonly ObservableCollection<GitChange> _staged = new();
    private readonly ObservableCollection<GitChange> _unstaged = new();

    public GitScmView()
    {
        InitializeComponent();
        _repoRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _repoRefreshTimer.Tick += RepoRefreshTimer_Tick;
        ScmBodyGrid.SizeChanged += (_, _) => UpdateStagedCap();

        // Diff Git 사용 여부(전역)를 설정에서 초기화. 이후 설정 저장 시 GitUiState 가 갱신되면 반응.
        GitUiState.Instance.DiffGitEnabled = SettingsService.LoadDiffGitEnabled();

        // 커밋 입력창 높이 = 실제 폰트 라인높이 × 3줄(테마 폰트크기 연동).
        // 폰트(Pretendard) 메트릭이 확정된 Loaded 시점에 계산하고, 폰트 크기 변경 시 재계산.
        Loaded += (_, _) =>
        {
            ApplyCommitBoxHeight();
            // 팩 폰트 메트릭이 한 프레임 늦게 확정되는 콜드 스타트 대비 1회 재적용.
            Dispatcher.BeginInvoke(new Action(ApplyCommitBoxHeight), System.Windows.Threading.DispatcherPriority.Loaded);
            var dpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBox.FontSizeProperty, typeof(TextBox));
            dpd?.AddValueChanged(MsgBox, (_, _) => ApplyCommitBoxHeight());

            GitUiState.Instance.PropertyChanged += GitUiState_Changed;
            ApplyGitMode();
            SetupRepoWatchers(_repo);
        };
        Unloaded += (_, _) =>
        {
            GitUiState.Instance.PropertyChanged -= GitUiState_Changed;
            DisposeRepoWatchers();
            _repoRefreshTimer.Stop();
        };
    }

    private void GitUiState_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyGitMode();

    // Diff Git 미사용(false) 시: 하단 커밋/푸시/풀 컨트롤 숨김 + 스테이징 섹션 숨김(UpdateButtons 에서 가림).
    //  A/M/D/R/C/T/U 상태글자·되돌리기/스테이지 버튼은 XAML 이 GitUiState 에 바인딩되어 자동 숨김.
    private void ApplyGitMode()
    {
        GitControlBar.Visibility = GitUiState.Instance.DiffGitEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    // 커밋 입력창을 정확히 3줄 텍스트 높이로 고정(초과분은 내부 스크롤).
    private void ApplyCommitBoxHeight()
    {
        double fs = MsgBox.FontSize;
        if (double.IsNaN(fs) || fs <= 0) return;
        double line = MsgBox.FontFamily.LineSpacing * fs;   // 폰트 실제 라인 간격(em) × 폰트크기
        if (double.IsNaN(line) || line <= 0) line = fs * 1.4;   // 메트릭 미확정 시 근사 폴백
        MsgBox.Height = Math.Ceiling(line * 3);
    }

    // 스테이징·변경 둘 다 있을 때만 스테이징 높이를 전체의 절반으로 캡(초과분은 내부 스크롤).
    // 스테이징만 있으면 캡 해제해 남는 공간을 채운다.
    private void UpdateStagedCap()
    {
        bool both = _staged.Count > 0 && _unstaged.Count > 0;
        StagedTreeHost.MaxHeight = both && ScmBodyGrid.ActualHeight > 0
            ? ScmBodyGrid.ActualHeight * 0.5
            : double.PositiveInfinity;
    }

    public void SetRepo(string? path)
    {
        if (string.Equals(_repo, path, StringComparison.OrdinalIgnoreCase)) return;
        _repo = path;
        SetupRepoWatchers(path);
    }

    public async Task RefreshAsync()
    {
        if (_refreshInProgress)
        {
            _refreshQueued = true;
            return;
        }

        _refreshInProgress = true;
        try
        {
            do
            {
                _refreshQueued = false;
                await RefreshCoreAsync();
            }
            while (_refreshQueued);
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        _repoRefreshTimer.Stop();
        var repo = _repo;
        if (string.IsNullOrEmpty(repo) || !Directory.Exists(repo) || !await GitService.IsRepoAsync(repo))
        {
            if (!string.Equals(_repo, repo, StringComparison.OrdinalIgnoreCase)) return;
            _staged.Clear(); _unstaged.Clear();
            StagedTree.ItemsSource = null;
            UnstagedTree.ItemsSource = null;
            EmptyText.Visibility = Visibility.Visible;
            UpdateButtons();
            return;
        }

        // git status가 index stat 캐시를 갱신하며 watcher를 재발화할 수 있다.
        _ignoreGitMetadataUntilUtc = DateTime.UtcNow.AddSeconds(2);
        var st = await GitService.StatusAsync(repo);
        var branch = await GitService.BranchStateAsync(repo);
        _ignoreGitMetadataUntilUtc = DateTime.UtcNow.AddSeconds(1);
        if (!string.Equals(_repo, repo, StringComparison.OrdinalIgnoreCase)) return;

        _branch = branch;
        _staged.Clear(); foreach (var c in st.Staged) _staged.Add(c);
        _unstaged.Clear(); foreach (var c in st.Unstaged) _unstaged.Add(c);
        StagedTree.ItemsSource = BuildTree(st.Staged, repo, isStaged: true);
        UnstagedTree.ItemsSource = BuildTree(st.Unstaged, repo, isStaged: false);
        EmptyText.Visibility = st.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void SetupRepoWatchers(string? repo)
    {
        DisposeRepoWatchers();
        if (string.IsNullOrEmpty(repo) || !Directory.Exists(repo)) return;

        try
        {
            _gitDirectory = ResolveGitDirectory(repo);
            _workTreeWatcher = CreateRepoWatcher(repo);
            _workTreeWatcher.Changed += WorkTreeWatcher_Changed;
            _workTreeWatcher.Created += WorkTreeWatcher_Changed;
            _workTreeWatcher.Deleted += WorkTreeWatcher_Changed;
            _workTreeWatcher.Renamed += WorkTreeWatcher_Changed;
            _workTreeWatcher.Error += RepoWatcher_Error;
            _workTreeWatcher.EnableRaisingEvents = true;

            if (!string.IsNullOrEmpty(_gitDirectory) && Directory.Exists(_gitDirectory))
            {
                _gitMetadataWatcher = CreateRepoWatcher(_gitDirectory);
                _gitMetadataWatcher.Changed += GitMetadataWatcher_Changed;
                _gitMetadataWatcher.Created += GitMetadataWatcher_Changed;
                _gitMetadataWatcher.Deleted += GitMetadataWatcher_Changed;
                _gitMetadataWatcher.Renamed += GitMetadataWatcher_Changed;
                _gitMetadataWatcher.Error += RepoWatcher_Error;
                _gitMetadataWatcher.EnableRaisingEvents = true;
            }
        }
        catch
        {
            DisposeRepoWatchers();
        }
    }

    private static FileSystemWatcher CreateRepoWatcher(string path) => new(path)
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                       NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        InternalBufferSize = 64 * 1024,
    };

    private void DisposeRepoWatchers()
    {
        DisposeWatcher(ref _workTreeWatcher, WorkTreeWatcher_Changed, WorkTreeWatcher_Changed);
        DisposeWatcher(ref _gitMetadataWatcher, GitMetadataWatcher_Changed, GitMetadataWatcher_Changed);
        _gitDirectory = null;
    }

    private void DisposeWatcher(
        ref FileSystemWatcher? watcher,
        FileSystemEventHandler handler,
        RenamedEventHandler renamedHandler)
    {
        if (watcher == null) return;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= handler;
            watcher.Created -= handler;
            watcher.Deleted -= handler;
            watcher.Renamed -= renamedHandler;
            watcher.Error -= RepoWatcher_Error;
            watcher.Dispose();
        }
        catch { }
        watcher = null;
    }

    private void WorkTreeWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        // Git 내부는 별도 watcher가 필요한 파일만 선별한다.
        if (IsGitMetadataPath(e.FullPath)) return;
        ScheduleRepoRefresh();
    }

    private void GitMetadataWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        if (DateTime.UtcNow < _ignoreGitMetadataUntilUtc || !IsRelevantGitMetadata(e.FullPath)) return;
        ScheduleRepoRefresh();
    }

    private void RepoWatcher_Error(object sender, ErrorEventArgs e) => ScheduleRepoRefresh();

    private void ScheduleRepoRefresh()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            _repoRefreshTimer.Stop();
            _repoRefreshTimer.Start();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void RepoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _repoRefreshTimer.Stop();
        if (_busy || _refreshInProgress)
        {
            _repoRefreshTimer.Start();
            return;
        }

        var repo = _repo;
        await RefreshAsync();
        if (!string.IsNullOrEmpty(repo) && string.Equals(_repo, repo, StringComparison.OrdinalIgnoreCase))
            GitStateChanged?.Invoke(repo);
    }

    private bool IsGitMetadataPath(string path)
    {
        if (!string.IsNullOrEmpty(_gitDirectory) && IsWithin(path, _gitDirectory)) return true;
        if (string.IsNullOrEmpty(_repo)) return false;
        var dotGit = Path.Combine(_repo, ".git");
        return string.Equals(path, dotGit, StringComparison.OrdinalIgnoreCase)
            || IsWithin(path, dotGit);
    }

    private bool IsRelevantGitMetadata(string path)
    {
        if (string.IsNullOrEmpty(_gitDirectory)) return false;
        string relative;
        try { relative = Path.GetRelativePath(_gitDirectory, path).Replace('/', '\\'); }
        catch { return true; }

        if (relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("objects\\", StringComparison.OrdinalIgnoreCase))
            return false;

        return relative.Equals("index", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("config", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("packed-refs", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("FETCH_HEAD", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("ORIG_HEAD", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("MERGE_HEAD", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("refs\\", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("logs\\", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("rebase-", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("sequencer\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithin(string path, string directory)
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveGitDirectory(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
        {
            var dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit)) return Path.GetFullPath(dotGit);
            if (!File.Exists(dotGit)) continue;

            try
            {
                const string prefix = "gitdir:";
                var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
                if (line == null || !line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
                var gitDir = line[prefix.Length..].Trim();
                return Path.GetFullPath(Path.IsPathRooted(gitDir)
                    ? gitDir
                    : Path.Combine(dir.FullName, gitDir));
            }
            catch { return null; }
        }
        return null;
    }

    private void UpdateButtons()
    {
        CommitBtn.IsEnabled = !_busy && (_staged.Count > 0 || _unstaged.Count > 0) && !string.IsNullOrWhiteSpace(MsgBox.Text);
        bool onBranch = !_busy && _branch.Branch != null;
        FetchBtn.IsEnabled = onBranch;                                            // 원격 확인 — 항상 가능
        PullBtn.IsEnabled  = onBranch && _branch.Behind > 0;                      // 받을 게 0개면 비활성
        PushBtn.IsEnabled  = onBranch && (_branch.Ahead > 0 || !_branch.HasUpstream);  // 올릴 게 0개면 비활성(최초 푸시는 허용). 커밋 메시지와 무관.
        SyncBtn.IsEnabled = onBranch;                                             // upstream 없음: 최초 push, 있음: pull → push
        bool gitOn = GitUiState.Instance.DiffGitEnabled;   // Diff Git 미사용 시 스테이징 섹션 전체 숨김
        StagedHeader.Text = $"스테이징된 변경 사항 ({_staged.Count})";
        StagedHeaderRow.Visibility = gitOn && _staged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StagedTreeHost.Visibility = gitOn && _staged.Count > 0 && !_stagedCollapsed ? Visibility.Visible : Visibility.Collapsed;
        SectionSeparator.Visibility = gitOn && _staged.Count > 0 && _unstaged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStagedCap();
        ChangesHeader.Text = $"변경 내용 ({_unstaged.Count})";
        ChangesHeaderRow.Visibility = _unstaged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnstagedTreeHost.Visibility = _unstaged.Count > 0 && !_unstagedCollapsed ? Visibility.Visible : Visibility.Collapsed;

        var parts = new System.Collections.Generic.List<string>(2);
        if (_branch.Behind > 0) parts.Add($"↓{_branch.Behind}");
        if (_branch.Ahead > 0) parts.Add($"↑{_branch.Ahead}");
        BranchText.Text = _branch.Branch == null
            ? "(git 저장소 없음)"
            : _branch.Branch + (parts.Count > 0 ? "  " + string.Join(" ", parts) : "");
    }

    private void MsgBox_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            if (CommitBtn.IsEnabled) Commit_Click(CommitBtn, new RoutedEventArgs());
        }
    }

    private void MsgBox_TextChanged(object s, TextChangedEventArgs e) => UpdateButtons();

    private void ScmTree_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        double scrollableHeight = e.ExtentHeight - e.ViewportHeight;
        bool showTop = e.VerticalOffset > 0.5;
        bool showBottom = e.VerticalOffset < scrollableHeight - 0.5;
        if (ReferenceEquals(sender, StagedTree))
        {
            StagedFadeTop.Visibility = showTop ? Visibility.Visible : Visibility.Collapsed;
            StagedFadeBottom.Visibility = showBottom ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (ReferenceEquals(sender, UnstagedTree))
        {
            ChangesFadeTop.Visibility = showTop ? Visibility.Visible : Visibility.Collapsed;
            ChangesFadeBottom.Visibility = showBottom ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // 섹션 통째 접기/펼치기 — 셰브론 방향 갱신 후 UpdateButtons 가 트리 Visibility 반영.
    private bool _stagedCollapsed, _unstagedCollapsed;
    private void ToggleStaged(object s, MouseButtonEventArgs e)
    {
        _stagedCollapsed = !_stagedCollapsed;
        StagedChevron.RenderTransform = new System.Windows.Media.RotateTransform(_stagedCollapsed ? 0 : 90);
        AnimateSection(StagedTreeHost, !_stagedCollapsed);
    }
    private void ToggleUnstaged(object s, MouseButtonEventArgs e)
    {
        _unstagedCollapsed = !_unstagedCollapsed;
        ChangesChevron.RenderTransform = new System.Windows.Media.RotateTransform(_unstagedCollapsed ? 0 : 90);
        AnimateSection(UnstagedTreeHost, !_unstagedCollapsed);
    }

    // 섹션 접기/펼치기 — 폴더의 SlideExpand처럼 상단을 고정하고 Height를 애니메이션한다.
    // 변경내용은 별(*) 행이라 기본 Stretch 상태에서 Height를 줄이면 중앙 기준처럼 움직인다.
    // 애니메이션 중에만 Top 정렬해 아래로 펼치고, 아래쪽부터 위로 접히게 한다.
    private void AnimateSection(FrameworkElement host, bool expand)
    {
        var tree = ReferenceEquals(host, StagedTreeHost) ? StagedTree : UnstagedTree;
        // Height가 변하는 중에는 viewport가 잠깐 콘텐츠보다 작아져 Auto 스크롤바가 번쩍인다.
        // 애니메이션 동안만 숨기고 최종 높이가 확정된 뒤 정상 Auto 판정으로 돌린다.
        ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Hidden);
        host.BeginAnimation(FrameworkElement.HeightProperty, null);
        host.Opacity = 1;
        host.VerticalAlignment = VerticalAlignment.Top;
        if (expand)
        {
            host.Visibility = Visibility.Visible;
            host.ClearValue(FrameworkElement.HeightProperty);
            host.UpdateLayout();
            double target = host.ActualHeight;
            if (target <= 0) target = MeasureContentHeight(host, ScmBodyGrid.ActualHeight);
            host.Height = 0;
            var a = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0, To = target, Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            a.Completed += (_, _) =>
            {
                host.BeginAnimation(FrameworkElement.HeightProperty, null);
                host.ClearValue(FrameworkElement.HeightProperty);
                host.VerticalAlignment = VerticalAlignment.Stretch;
                ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
            };
            host.BeginAnimation(FrameworkElement.HeightProperty, a);
        }
        else
        {
            host.ClearValue(FrameworkElement.HeightProperty);
            host.UpdateLayout();
            double from = host.ActualHeight;
            if (from <= 0) from = MeasureContentHeight(host, ScmBodyGrid.ActualHeight);
            host.Height = from;
            var a = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = from, To = 0, Duration = TimeSpan.FromMilliseconds(130),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
            };
            a.Completed += (_, _) =>
            {
                host.BeginAnimation(FrameworkElement.HeightProperty, null);
                host.Visibility = Visibility.Collapsed;
                host.ClearValue(FrameworkElement.HeightProperty);
                host.VerticalAlignment = VerticalAlignment.Stretch;
                ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
            };
            host.BeginAnimation(FrameworkElement.HeightProperty, a);
        }
    }

    // host(트리 호스트)의 실제 콘텐츠 높이. 세로 제약을 avail 로 주면 가상화를 유지하면서
    // min(콘텐츠높이, 남은공간) 을 얻는다(빈 공간 애니메이션 방지).
    private static double MeasureContentHeight(FrameworkElement host, double avail)
    {
        double w = host.ActualWidth > 0 ? host.ActualWidth : double.PositiveInfinity;
        double h = avail > 0 ? avail : double.PositiveInfinity;
        host.Measure(new System.Windows.Size(w, h));
        return host.DesiredSize.Height;
    }

    // 화살표(삼각형) 단일 클릭 → 폴더 접기/펼치기. 텍스트 영역으로의 전파는 막는다.
    private void Tri_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ScmTreeNode node } || !node.IsFolder) return;
        if (e.ClickCount == 1) node.IsExpanded = !node.IsExpanded;
        e.Handled = true;
    }

    // 텍스트 영역: 더블클릭에서 동작(폴더 접기/펼치기 · 파일 열기).
    private void Node_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ScmTreeNode node }) return;
        if (e.ClickCount != 2) return;
        e.Handled = true;   // TreeViewItem 기본 더블클릭-확장과 상쇄(2번 토글=제자리) 방지
        if (node.IsFolder) { node.IsExpanded = !node.IsExpanded; return; }
        if (_repo != null && node.Change != null)
            DiffFileActivated?.Invoke(_repo, node.Change.Path, node.Change.IsStaged);
    }

    /// <summary>평면 변경 목록 → repo 루트(전체 경로) 아래 중첩 폴더 트리.
    /// 단일 폴더 체인은 VS식으로 한 행("A/B/C")으로 압축한다.</summary>
    private static List<ScmTreeNode> BuildTree(IEnumerable<GitChange> changes, string? repoPath, bool isStaged)
    {
        var roots = new List<ScmTreeNode>();
        var folders = new Dictionary<string, ScmTreeNode>();   // 누적경로 → 폴더노드
        foreach (var ch in changes)
        {
            var segs = ch.Path.Split('/');
            IList<ScmTreeNode> siblings = roots;
            var acc = "";
            for (int i = 0; i < segs.Length - 1; i++)
            {
                acc = acc.Length == 0 ? segs[i] : acc + "/" + segs[i];
                if (!folders.TryGetValue(acc, out var folder))
                {
                    // FolderPath=누적경로(repo 상대) → 폴더 단위 git add/restore/checkout 대상.
                    folder = new ScmTreeNode { Name = segs[i], IsFolder = true, FolderPath = acc, IsStaged = isStaged };
                    folders[acc] = folder;
                    siblings.Add(folder);
                }
                siblings = folder.Children;   // ObservableCollection<T> 는 IList<T> 구현
            }
            siblings.Add(new ScmTreeNode { Name = segs[^1], IsFolder = false, Change = ch, IsStaged = isStaged });
        }
        if (roots.Count == 0) return new List<ScmTreeNode>();
        Sort(roots);
        var root = new ScmTreeNode
        {
            Name = repoPath ?? "",
            IsFolder = true,
            IsRepositoryRoot = true,
            FolderPath = "",   // 빈 경로 = 저장소 전체(stage/unstage all)
            IsStaged = isStaged
        };
        foreach (var n in roots) root.Children.Add(Compress(n));
        return new List<ScmTreeNode> { root };
    }

    /// <summary>단일 하위 폴더만 있는 폴더 체인을 한 노드("부모\자식")로 병합(자식부터 재귀).</summary>
    private static ScmTreeNode Compress(ScmTreeNode n)
    {
        if (!n.IsFolder) return n;
        var kids = n.Children.Select(Compress).ToList();
        if (kids.Count == 1 && kids[0].IsFolder)
        {
            var c = kids[0];
            // 병합 노드의 FolderPath 는 가장 깊은(자식) 경로 — "부모/자식" 전체를 pathspec 로 처리.
            var merged = new ScmTreeNode { Name = n.Name + "\\" + c.Name, IsFolder = true, FolderPath = c.FolderPath, IsStaged = n.IsStaged };
            foreach (var g in c.Children) merged.Children.Add(g);
            return merged;
        }
        var res = new ScmTreeNode { Name = n.Name, IsFolder = true, FolderPath = n.FolderPath, IsStaged = n.IsStaged };
        foreach (var k in kids) res.Children.Add(k);
        return res;
    }

    private static void Sort(List<ScmTreeNode> nodes)
    {
        nodes.Sort((a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var f in nodes) if (f.IsFolder)
        {
            var tmp = f.Children.ToList();
            Sort(tmp);
            f.Children.Clear();
            foreach (var c in tmp) f.Children.Add(c);
        }
    }

    // 스테이지/해제 — 파일(Change), 폴더(FolderPath), 루트(FolderPath="") 공통.
    private async void Stage_Click(object s, RoutedEventArgs e)
        => await RunStage(s, stage: true);
    private async void Unstage_Click(object s, RoutedEventArgs e)
        => await RunStage(s, stage: false);

    private async Task RunStage(object sender, bool stage)
    {
        if (_repo == null || _busy || (sender as FrameworkElement)?.Tag is not ScmTreeNode node) return;
        // 파일이면 파일 경로, 폴더면 폴더 경로. 루트(폴더 경로="")는 전체 stage/unstage all.
        var path = node.Change?.Path ?? node.FolderPath ?? "";
        bool whole = node.Change == null && string.IsNullOrEmpty(path);
        Func<Task<GitService.GitResult>> op = (stage, whole) switch
        {
            (true, true)   => () => GitService.StageAllAsync(_repo!),
            (true, false)  => () => GitService.StageAsync(_repo!, path),
            (false, true)  => () => GitService.UnstageAllAsync(_repo!),
            (false, false) => () => GitService.UnstageAsync(_repo!, path),
        };
        await DoAll(op, stage ? GitOperation.Stage : GitOperation.Unstage);
    }

    private async void Discard_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null || _busy || (s as FrameworkElement)?.Tag is not ScmTreeNode node) return;

        // 파일 리프 — 단일 파일 취소.
        if (node.Change is { } c)
        {
            if (!ConfirmDialog.Show("변경 취소", $"'{c.Path}' 의 변경을 취소할까요? 되돌릴 수 없습니다.", "취소", danger: true)) return;
            await DoAll(() => GitService.DiscardAsync(_repo!, c.Path, c.IsUntracked), GitOperation.Discard);
            return;
        }

        // 폴더/루트 — 하위 작업트리 변경 전체 취소.
        var leaves = new List<GitChange>();
        CollectChanges(node, leaves);
        if (leaves.Count == 0) return;
        var msg = node.IsRepositoryRoot
            ? $"모든 변경 내용({leaves.Count}개)을 취소할까요? 되돌릴 수 없습니다."
            : $"'{node.FolderPath}' 폴더의 변경 {leaves.Count}개를 취소할까요? 되돌릴 수 없습니다.";
        if (!ConfirmDialog.Show("변경 취소", msg, "취소", danger: true)) return;
        await DoAll(() => GitService.DiscardFolderAsync(_repo!, node.FolderPath ?? "", leaves), GitOperation.Discard);
    }

    /// <summary>노드 하위(자기 자신 포함)의 파일 리프 변경을 모두 수집.</summary>
    private static void CollectChanges(ScmTreeNode node, List<GitChange> acc)
    {
        if (node.Change != null) acc.Add(node.Change);
        foreach (var child in node.Children) CollectChanges(child, acc);
    }

    private async Task DoAll(Func<Task<GitService.GitResult>> op, GitOperation operation)
    {
        if (_repo == null) return;
        _busy = true; UpdateButtons();
        var r = await op();
        _busy = false;
        if (!r.Ok) ShowGitFailure(operation, r);
        await RefreshAsync();
        GitStateChanged?.Invoke(_repo);
    }

    private async void Commit_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null) return;
        var msg = MsgBox.Text.Trim();
        if (msg.Length == 0 || (_staged.Count == 0 && _unstaged.Count == 0)) return;
        if (!await GitService.HasIdentityAsync(_repo))
        { ConfirmDialog.Alert("커밋 불가", "Git 사용자 정보가 없습니다.\n사용자 이름과 이메일을 설정한 뒤 다시 시도하세요."); return; }
        // staged 항목이 있으면 그대로 staged 커밋. 없으면 전체 변경을 자동 스테이징 후 커밋(확인 없이).
        _busy = true; UpdateButtons();
        if (_staged.Count == 0)
        {
            var sa = await GitService.StageAllAsync(_repo);
            if (!sa.Ok) { _busy = false; ShowGitFailure(GitOperation.Stage, sa); UpdateButtons(); return; }
        }
        var r = await GitService.CommitAsync(_repo, msg);
        _busy = false;
        if (!r.Ok) { ShowGitFailure(GitOperation.Commit, r); UpdateButtons(); return; }
        MsgBox.Clear();
        await RefreshAsync();
        GitStateChanged?.Invoke(_repo);
    }

    private async void Push_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null) return;
        // 원격에 로컬에 없는 커밋이 있으면(분기) 그냥 push 는 거부됨 → VS처럼 동기화 후 push 여부를 묻는다.
        if (_branch.Behind > 0)
        {
            await ConfirmSyncAndPushAsync();
            return;
        }

        // 마지막 fetch 이후 원격이 전진하면 _branch.Behind 는 아직 0이다. 첫 push 의 fetch-first 거절을
        // 그대로 오류로 끝내지 말고 fetch 로 실제 개수를 갱신한 뒤 위와 같은 sync+push 흐름으로 연결한다.
        var push = await RunRemote(() => GitService.PushAsync(_repo!), GitOperation.Push, showFailure: false);
        if (push.Ok) return;
        if (!IsRemoteAheadPushFailure(push))
        {
            ShowGitFailure(GitOperation.Push, push);
            return;
        }

        var fetch = await RunRemote(() => GitService.FetchAsync(_repo!), GitOperation.Fetch);
        if (!fetch.Ok) return;
        if (_branch.Behind > 0)
        {
            await ConfirmSyncAndPushAsync();
            return;
        }

        // fetch 는 성공했지만 분기 상태를 확인하지 못한 예외 상황에서는 최초 push 오류를 보존한다.
        ShowGitFailure(GitOperation.Push, push);
    }
    private async void Pull_Click(object s, RoutedEventArgs e)
        => await RunRemote(() => GitService.PullAsync(_repo!), GitOperation.Pull);
    private async void Fetch_Click(object s, RoutedEventArgs e)
        => await RunRemote(() => GitService.FetchAsync(_repo!), GitOperation.Fetch);
    private async void Sync_Click(object s, RoutedEventArgs e)
        => await RunSyncAsync();

    private async Task ConfirmSyncAndPushAsync()
    {
        if (_repo == null || _branch.Behind <= 0) return;
        if (!ConfirmDialog.Show("푸시",
                $"원격에 로컬에 없는 커밋이 {_branch.Behind}개 있습니다.\n원격 변경을 동기화한 뒤 푸시할까요?",
                "동기화 후 푸시"))
            return;
        await RunSyncAsync();
    }

    /// <summary>Visual Studio의 Sync와 동일하게 upstream이 있으면 pull 후 push. 최초 게시라면 push만 수행.</summary>
    private async Task RunSyncAsync()
    {
        var repo = _repo;
        if (repo == null) return;

        if (!_branch.HasUpstream)
        {
            await RunRemote(() => GitService.PushAsync(repo), GitOperation.Push);
            return;
        }

        _busy = true; SetSyncing(true); UpdateButtons();
        var operation = GitOperation.Pull;
        var r = await GitService.PullAsync(repo);
        if (r.Ok)
        {
            operation = GitOperation.Push;
            r = await GitService.PushAsync(repo);
        }
        _busy = false; SetSyncing(false);
        if (!r.Ok) ShowGitFailure(operation, r);
        await RefreshAsync();
        GitStateChanged?.Invoke(repo);
    }

    private static bool IsRemoteAheadPushFailure(GitService.GitResult result)
    {
        var text = result.Output + "\n" + result.Error;
        return text.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
            || text.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("[rejected]", StringComparison.OrdinalIgnoreCase)
                && text.Contains("remote contains work", StringComparison.OrdinalIgnoreCase));
    }

    private static void ShowGitFailure(GitOperation operation, GitService.GitResult result)
    {
        var raw = (result.Output + "\n" + result.Error).Trim();
        DiagLog.Write($"Git {operation} failed: {raw}");
        ConfirmDialog.Alert(GetGitFailureTitle(operation), FormatGitFailure(operation, raw));
    }

    private static string GetGitFailureTitle(GitOperation operation) => operation switch
    {
        GitOperation.Stage => "스테이징할 수 없음",
        GitOperation.Unstage => "스테이징을 해제할 수 없음",
        GitOperation.Discard => "변경을 취소할 수 없음",
        GitOperation.Commit => "커밋할 수 없음",
        GitOperation.Fetch => "원격 정보를 가져올 수 없음",
        GitOperation.Pull => "변경 내용을 동기화할 수 없음",
        _ => "푸시할 수 없음",
    };

    private static string FormatGitFailure(GitOperation operation, string text)
    {
        bool Has(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

        if (Has("현재 브랜치가 분리된 상태"))
            return "현재 브랜치가 분리된 상태라 푸시할 수 없습니다.\n\n푸시할 브랜치를 선택한 뒤 다시 시도하세요.";

        if (Has("git 프로세스를 시작하지 못했습니다"))
            return "Git 실행 파일을 시작할 수 없습니다.\n\nGit 설치 상태와 실행 경로를 확인하세요.";

        if (Has("cannot pull with rebase") && (Has("unstaged changes") || Has("commit or stash")))
            return "커밋되지 않은 변경 내용이 있어 원격 변경을 가져올 수 없습니다.\n\n변경 내용을 커밋하거나 스태시한 뒤 다시 시도하세요.";

        if (Has("rebase-merge") || Has("rebase in progress") || Has("rebase is already in progress"))
            return "이전에 시작한 동기화 작업이 아직 끝나지 않았습니다.\n\n진행 중인 작업을 완료하거나 취소한 뒤 다시 시도하세요.";

        if (Has("unmerged files") || Has("automatic merge failed") || Has("could not apply")
            || Has("resolve all conflicts manually") || Has("fix conflicts and then"))
            return "자동 병합 중 충돌이 발생했습니다.\n\n충돌 파일을 해결한 뒤 진행 중인 동기화 작업을 완료하거나 취소하세요.";

        if (Has("not concluded your merge") || Has("merge_head exists"))
            return "이전에 시작한 병합 작업이 아직 끝나지 않았습니다.\n\n진행 중인 병합을 완료하거나 취소한 뒤 다시 시도하세요.";

        if (Has("untracked working tree files would be overwritten"))
            return "추적되지 않은 로컬 파일이 덮어써질 수 있어 작업을 중단했습니다.\n\n해당 파일을 이동하거나 커밋한 뒤 다시 시도하세요.";

        if ((Has("local changes") && Has("would be overwritten")) || Has("would be overwritten by merge"))
            return "로컬 변경 내용이 덮어써질 수 있어 작업을 중단했습니다.\n\n변경 내용을 커밋하거나 스태시한 뒤 다시 시도하세요.";

        if (Has("index.lock") || Has("another git process"))
            return "다른 버전 관리 작업이 진행 중이거나 이전 작업의 잠금 정보가 남아 있습니다.\n\n다른 작업을 종료한 뒤 다시 시도하세요.";

        if (Has("not a git repository"))
            return "현재 폴더에서 Git 저장소를 찾을 수 없습니다.\n\n프로젝트 폴더와 저장소 상태를 확인하세요.";

        if (Has("repository not found"))
            return "원격 저장소를 찾을 수 없거나 접근 권한이 없습니다.\n\n원격 저장소 주소와 로그인 계정을 확인하세요.";

        if (Has("does not appear to be a git repository") || Has("no such remote") || Has("no configured push destination"))
            return "연결된 원격 저장소 정보를 확인할 수 없습니다.\n\n저장소의 원격 주소 설정을 확인하세요.";

        if (Has("authentication failed") || Has("could not read username") || Has("could not read password")
            || Has("terminal prompts disabled") || Has("permission denied (publickey)")
            || Has("http 401") || Has("http 403") || Has("error: 401") || Has("error: 403"))
            return "원격 저장소 인증에 실패했습니다.\n\n로그인 계정과 저장된 자격 증명을 확인하세요.";

        if (Has("could not resolve host"))
            return "원격 저장소의 네트워크 주소를 찾을 수 없습니다.\n\n인터넷 연결과 원격 저장소 주소를 확인하세요.";

        if (Has("failed to connect") || Has("couldn't connect") || Has("connection timed out")
            || Has("network is unreachable") || Has("connection was reset") || Has("connection reset by peer"))
            return "원격 저장소에 연결할 수 없습니다.\n\n인터넷 연결을 확인한 뒤 다시 시도하세요.";

        if (Has("ssl certificate problem") || Has("certificate verify failed"))
            return "원격 저장소의 보안 인증서를 확인할 수 없습니다.\n\n시스템 시간과 네트워크 보안 설정을 확인하세요.";

        if (Has("protected branch") || Has("pre-receive hook declined") || Has("hook declined")
            || Has("remote rejected") || Has("gh006"))
            return "원격 저장소 정책으로 푸시가 거부되었습니다.\n\n보호 브랜치와 서버 규칙을 확인하세요.";

        if (Has("fetch first") || Has("non-fast-forward") || Has("updates were rejected"))
            return "원격 저장소에 새로운 커밋이 있어 푸시할 수 없습니다.\n\n원격 변경을 동기화한 뒤 다시 시도하세요.";

        if (Has("author identity unknown") || Has("please tell me who you are") || Has("unable to auto-detect email address"))
            return "커밋 작성자 정보가 설정되어 있지 않습니다.\n\n사용자 이름과 이메일을 설정한 뒤 다시 시도하세요.";

        if (Has("nothing to commit") || Has("no changes added to commit"))
            return "커밋할 변경 내용이 없습니다.\n\n변경 파일과 스테이징 상태를 확인하세요.";

        if (Has("pathspec") && Has("did not match any file"))
            return "대상 파일을 찾을 수 없습니다.\n\n파일이 이동되었거나 삭제되었는지 확인하세요.";

        if (Has("src refspec") && Has("does not match any"))
            return "푸시할 로컬 커밋이나 브랜치를 찾을 수 없습니다.\n\n먼저 변경 내용을 커밋한 뒤 다시 시도하세요.";

        if (Has("no space left on device") || Has("disk full"))
            return "저장 공간이 부족해 작업을 완료할 수 없습니다.\n\n디스크 공간을 확보한 뒤 다시 시도하세요.";

        if (Has("permission denied") || Has("access is denied") || Has("unable to unlink")
            || Has("could not open") || Has("cannot open"))
            return "파일 또는 저장소에 접근할 권한이 없습니다.\n\n파일 사용 여부와 폴더 권한을 확인하세요.";

        return operation switch
        {
            GitOperation.Stage => "변경 내용을 스테이징하지 못했습니다.\n\n저장소 상태와 파일 권한을 확인한 뒤 다시 시도하세요.",
            GitOperation.Unstage => "스테이징을 해제하지 못했습니다.\n\n저장소 상태를 확인한 뒤 다시 시도하세요.",
            GitOperation.Discard => "변경 내용을 취소하지 못했습니다.\n\n파일 사용 여부와 권한을 확인한 뒤 다시 시도하세요.",
            GitOperation.Commit => "변경 내용을 커밋하지 못했습니다.\n\n저장소 상태를 확인한 뒤 다시 시도하세요.",
            GitOperation.Fetch => "원격 저장소 정보를 가져오지 못했습니다.\n\n네트워크 연결과 원격 저장소 설정을 확인하세요.",
            GitOperation.Pull => "원격 변경 내용을 동기화하지 못했습니다.\n\n저장소 상태를 확인한 뒤 다시 시도하세요.",
            _ => "로컬 커밋을 원격 저장소에 푸시하지 못했습니다.\n\n저장소 상태와 원격 저장소 권한을 확인하세요.",
        };
    }

    private async Task<GitService.GitResult> RunRemote(
        Func<Task<GitService.GitResult>> op,
        GitOperation operation,
        bool showFailure = true)
    {
        if (_repo == null) return new GitService.GitResult(false, "", "git 저장소가 선택되지 않았습니다.");
        _busy = true; SetSyncing(true); UpdateButtons();   // 스피너 ON + 원격 작업 버튼 비활성화(!_busy)
        var r = await op();
        _busy = false; SetSyncing(false);
        if (!r.Ok && showFailure) ShowGitFailure(operation, r);
        await RefreshAsync();
        GitStateChanged?.Invoke(_repo);
        return r;
    }

    // fetch/pull/push/sync 진행 중에만 브랜치 아이콘을 스피너로 교체(커밋/스테이징은 제외).
    private void SetSyncing(bool on)
    {
        BranchSpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        BranchIcon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
