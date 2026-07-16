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

/// <summary>우측 Git SCM 패널 — staged/unstaged 목록 + 커밋박스 + pull/push/fetch.</summary>
public partial class GitScmView : UserControl
{
    /// <summary>파일 행 클릭 → 중앙에 diff 탭 열기 요청.(repo, relPath, staged)</summary>
    public event Action<string, string, bool>? DiffFileActivated;
    /// <summary>커밋/스테이지/pull/push 등 git 상태 변경(repo). 브랜치 버블 갱신용.</summary>
    public event Action<string>? GitStateChanged;

    private string? _repo;
    private bool _busy;
    private BranchState _branch = new();
    private readonly ObservableCollection<GitChange> _staged = new();
    private readonly ObservableCollection<GitChange> _unstaged = new();

    public GitScmView()
    {
        InitializeComponent();
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
        };
        Unloaded += (_, _) => GitUiState.Instance.PropertyChanged -= GitUiState_Changed;
    }

    private void GitUiState_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyGitMode();

    // Diff Git 미사용(false) 시: 하단 커밋/푸시/풀 컨트롤 숨김 + 스테이징 섹션 숨김(UpdateButtons 에서 가림).
    //  MADRU 상태글자·되돌리기/스테이지 버튼은 XAML 이 GitUiState 에 바인딩되어 자동 숨김.
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

    public void SetRepo(string? path) { if (_repo == path) return; _repo = path; }

    public async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_repo) || !Directory.Exists(_repo) || !await GitService.IsRepoAsync(_repo))
        {
            _staged.Clear(); _unstaged.Clear();
            StagedTree.ItemsSource = null;
            UnstagedTree.ItemsSource = null;
            EmptyText.Visibility = Visibility.Visible;
            UpdateButtons(); return;
        }
        var st = await GitService.StatusAsync(_repo);
        _branch = await GitService.BranchStateAsync(_repo);
        _staged.Clear(); foreach (var c in st.Staged) _staged.Add(c);
        _unstaged.Clear(); foreach (var c in st.Unstaged) _unstaged.Add(c);
        StagedTree.ItemsSource = BuildTree(st.Staged, _repo);
        UnstagedTree.ItemsSource = BuildTree(st.Unstaged, _repo);
        EmptyText.Visibility = st.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        CommitBtn.IsEnabled = !_busy && (_staged.Count > 0 || _unstaged.Count > 0) && !string.IsNullOrWhiteSpace(MsgBox.Text);
        bool onBranch = !_busy && _branch.Branch != null;
        FetchBtn.IsEnabled = onBranch;                                            // 원격 확인 — 항상 가능
        PullBtn.IsEnabled  = onBranch && _branch.Behind > 0;                      // 받을 게 0개면 비활성
        PushBtn.IsEnabled  = onBranch && (_branch.Ahead > 0 || !_branch.HasUpstream);  // 올릴 게 0개면 비활성(최초 푸시는 허용). 커밋 메시지와 무관.

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

    // 섹션 접기/펼치기 — Height 애니메이션으로 "위에서 아래로 펼치고 / 위로 접기".
    //  변경내용은 별(*) 행이라 ActualHeight 가 '남은 공간 전체'다. 그대로 쓰면 펼칠 때
    //  콘텐츠 뒤로 빈 공간이 자라고, 접을 때 빈 공간부터 줄어 콘텐츠가 마지막에 툭 사라진다.
    //  → 목표/시작 높이를 '실제 콘텐츠 높이'(콘텐츠가 더 크면 남은공간까지)로 잡아
    //    항상 콘텐츠가 위에서부터 펼쳐지고 위로 접히도록 한다.
    private void AnimateSection(FrameworkElement host, bool expand)
    {
        host.BeginAnimation(FrameworkElement.HeightProperty, null);
        host.Opacity = 1;
        if (expand)
        {
            host.Visibility = Visibility.Visible;
            host.ClearValue(FrameworkElement.HeightProperty);   // 남은 공간(별 행) 확보
            host.UpdateLayout();
            double avail = host.ActualHeight;
            double target = MeasureContentHeight(host, avail);
            if (target <= 0) target = avail;
            host.Height = 0;
            var a = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0, To = target, Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            a.Completed += (_, _) => { host.BeginAnimation(FrameworkElement.HeightProperty, null); host.ClearValue(FrameworkElement.HeightProperty); };
            host.BeginAnimation(FrameworkElement.HeightProperty, a);
        }
        else
        {
            double avail = host.ActualHeight;
            double from = MeasureContentHeight(host, avail);
            if (from <= 0) from = avail;
            host.Height = from;   // 콘텐츠 아래 빈 공간(별 행)을 즉시 제거 → 콘텐츠 높이에서 접기 시작
            var a = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = from, To = 0, Duration = TimeSpan.FromMilliseconds(130),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
            };
            a.Completed += (_, _) => { host.BeginAnimation(FrameworkElement.HeightProperty, null); host.Visibility = Visibility.Collapsed; host.ClearValue(FrameworkElement.HeightProperty); };
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
    private static List<ScmTreeNode> BuildTree(IEnumerable<GitChange> changes, string? repoPath)
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
                    folder = new ScmTreeNode { Name = segs[i], IsFolder = true };
                    folders[acc] = folder;
                    siblings.Add(folder);
                }
                siblings = folder.Children;   // ObservableCollection<T> 는 IList<T> 구현
            }
            siblings.Add(new ScmTreeNode { Name = segs[^1], IsFolder = false, Change = ch });
        }
        if (roots.Count == 0) return new List<ScmTreeNode>();
        Sort(roots);
        var root = new ScmTreeNode { Name = repoPath ?? "", IsFolder = true };
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
            var merged = new ScmTreeNode { Name = n.Name + "\\" + c.Name, IsFolder = true };
            foreach (var g in c.Children) merged.Children.Add(g);
            return merged;
        }
        var res = new ScmTreeNode { Name = n.Name, IsFolder = true };
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

    private async void Stage_Click(object s, RoutedEventArgs e) => await Do(c => GitService.StageAsync(_repo!, c.Path), s);
    private async void Unstage_Click(object s, RoutedEventArgs e) => await Do(c => GitService.UnstageAsync(_repo!, c.Path), s);

    private async void Discard_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null || (s as FrameworkElement)?.Tag is not GitChange c) return;
        if (!ConfirmDialog.Show("변경 취소", $"'{c.Path}' 의 변경을 취소할까요? 되돌릴 수 없습니다.", "취소", danger: true)) return;
        await Do(_ => GitService.DiscardAsync(_repo!, c.Path, c.IsUntracked), s, alreadyResolved: c);
    }

    private async Task Do(Func<GitChange, Task<GitService.GitResult>> op, object sender, GitChange? alreadyResolved = null)
    {
        var c = alreadyResolved ?? (sender as FrameworkElement)?.Tag as GitChange;
        if (_repo == null || c == null) return;
        _busy = true; UpdateButtons();
        var r = await op(c);
        _busy = false;
        if (!r.Ok) ConfirmDialog.Alert("실패", string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error);
        await RefreshAsync();
        GitStateChanged?.Invoke(_repo);
    }

    private async void Commit_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null) return;
        var msg = MsgBox.Text.Trim();
        if (msg.Length == 0 || (_staged.Count == 0 && _unstaged.Count == 0)) return;
        if (!await GitService.HasIdentityAsync(_repo))
        { ConfirmDialog.Alert("커밋 불가", "git 사용자 정보가 없습니다.\ngit config user.name / user.email 설정 후 다시 시도하세요."); return; }
        // staged 항목이 있으면 그대로 staged 커밋. 없으면 전체 변경을 자동 스테이징 후 커밋(확인 없이).
        _busy = true; UpdateButtons();
        if (_staged.Count == 0)
        {
            var sa = await GitService.StageAllAsync(_repo);
            if (!sa.Ok) { _busy = false; ConfirmDialog.Alert("스테이징 실패", string.IsNullOrWhiteSpace(sa.Error) ? sa.Output : sa.Error); UpdateButtons(); return; }
        }
        var r = await GitService.CommitAsync(_repo, msg);
        _busy = false;
        if (!r.Ok) { ConfirmDialog.Alert("커밋 실패", string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error); UpdateButtons(); return; }
        MsgBox.Clear();
        await RefreshAsync();
        GitStateChanged?.Invoke(_repo);
    }

    private async void Push_Click(object s, RoutedEventArgs e)
    {
        if (_repo == null) return;
        // 원격에 로컬에 없는 커밋이 있으면(분기) 그냥 push 는 거부됨 → VS 처럼 rebase 후 push 여부를 묻는다.
        if (_branch.Behind > 0)
        {
            await ConfirmRebaseAndPushAsync();
            return;
        }

        // 마지막 fetch 이후 원격이 전진하면 _branch.Behind 는 아직 0이다. 첫 push 의 fetch-first 거절을
        // 그대로 오류로 끝내지 말고 fetch 로 실제 개수를 갱신한 뒤 위와 같은 rebase+push 흐름으로 연결한다.
        var push = await RunRemote(() => GitService.PushAsync(_repo!), "푸시", showFailure: false);
        if (push.Ok) return;
        if (!IsRemoteAheadPushFailure(push))
        {
            ShowRemoteFailure("푸시", push);
            return;
        }

        var fetch = await RunRemote(() => GitService.FetchAsync(_repo!), "fetch");
        if (!fetch.Ok) return;
        if (_branch.Behind > 0)
        {
            await ConfirmRebaseAndPushAsync();
            return;
        }

        // fetch 는 성공했지만 분기 상태를 확인하지 못한 예외 상황에서는 최초 push 오류를 보존한다.
        ShowRemoteFailure("푸시", push);
    }
    private async void Pull_Click(object s, RoutedEventArgs e) => await RunRemote(() => GitService.PullAsync(_repo!), "pull");
    private async void Fetch_Click(object s, RoutedEventArgs e) => await RunRemote(() => GitService.FetchAsync(_repo!), "fetch");

    private async Task ConfirmRebaseAndPushAsync()
    {
        if (_repo == null || _branch.Behind <= 0) return;
        if (!ConfirmDialog.Show("푸시",
                $"원격에 로컬에 없는 커밋이 {_branch.Behind}개 있습니다.\n원격 변경을 rebase 한 뒤 푸시할까요?",
                "rebase 후 푸시"))
            return;
        await RunRemote(async () =>
        {
            var pr = await GitService.PullRebaseAsync(_repo!);
            return pr.Ok ? await GitService.PushAsync(_repo!) : pr;   // rebase 실패(충돌 등)면 그 결과를 그대로 알림
        }, "푸시");
    }

    private static bool IsRemoteAheadPushFailure(GitService.GitResult result)
    {
        var text = result.Output + "\n" + result.Error;
        return text.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
            || text.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("[rejected]", StringComparison.OrdinalIgnoreCase)
                && text.Contains("remote contains work", StringComparison.OrdinalIgnoreCase));
    }

    private static void ShowRemoteFailure(string label, GitService.GitResult result)
    {
        var text = result.Output + "\n" + result.Error;
        if (text.Contains("cannot pull with rebase", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("unstaged changes", StringComparison.OrdinalIgnoreCase)
                || text.Contains("commit or stash", StringComparison.OrdinalIgnoreCase)))
        {
            ConfirmDialog.Alert(
                "동기화할 수 없음",
                "커밋되지 않은 변경 내용이 있어 원격 변경을 가져올 수 없습니다.\n\n변경 내용을 커밋하거나 스태시한 뒤 다시 시도하세요.");
            return;
        }

        ConfirmDialog.Alert($"{label} 실패", string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    private async Task<GitService.GitResult> RunRemote(
        Func<Task<GitService.GitResult>> op,
        string label,
        bool showFailure = true)
    {
        if (_repo == null) return new GitService.GitResult(false, "", "git 저장소가 선택되지 않았습니다.");
        _busy = true; SetSyncing(true); UpdateButtons();   // 스피너 ON + 화살표 3개 비활성화(!_busy)
        var r = await op();
        _busy = false; SetSyncing(false);
        if (!r.Ok && showFailure) ShowRemoteFailure(label, r);
        await RefreshAsync();
        GitStateChanged?.Invoke(_repo);
        return r;
    }

    // 페치/풀/푸시 진행 중에만 브랜치 아이콘을 스피너로 교체(커밋/스테이징은 제외).
    private void SetSyncing(bool on)
    {
        BranchSpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        BranchIcon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
