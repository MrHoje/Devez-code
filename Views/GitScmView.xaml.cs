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
        PushBtn.IsEnabled = PullBtn.IsEnabled = FetchBtn.IsEnabled = !_busy && _branch.Branch != null;

        StagedHeader.Text = $"스테이징된 변경 사항 ({_staged.Count})";
        StagedHeaderRow.Visibility = _staged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StagedTreeHost.Visibility = _staged.Count > 0 && !_stagedCollapsed ? Visibility.Visible : Visibility.Collapsed;
        SectionSeparator.Visibility = _staged.Count > 0 && _unstaged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStagedCap();
        ChangesHeader.Text = $"변경 내용 ({_unstaged.Count})";
        ChangesHeaderRow.Visibility = _unstaged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnstagedTreeHost.Visibility = _unstaged.Count > 0 && !_unstagedCollapsed ? Visibility.Visible : Visibility.Collapsed;

        var parts = new System.Collections.Generic.List<string>(2);
        if (_branch.Behind > 0) parts.Add($"↓{_branch.Behind}");
        if (_branch.Ahead > 0) parts.Add($"↑{_branch.Ahead}");
        BranchText.Text = _branch.Branch == null
            ? "(git 저장소 아님)"
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

    // 프로젝트 카드 세션 접기/펼치기와 동일한 Height 애니메이션(160ms, CubicEase EaseOut).
    private void AnimateSection(FrameworkElement host, bool expand)
    {
        host.BeginAnimation(FrameworkElement.HeightProperty, null);
        var dur = TimeSpan.FromMilliseconds(160);
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        if (expand)
        {
            host.Visibility = Visibility.Visible;
            host.Height = double.NaN;       // auto 로 실제 목표 높이(캡 반영) 측정
            host.UpdateLayout();
            double target = host.ActualHeight;
            host.Height = 0;
            var a = new System.Windows.Media.Animation.DoubleAnimation
            { From = 0, To = target, Duration = dur, EasingFunction = ease };
            a.Completed += (_, _) => { host.BeginAnimation(FrameworkElement.HeightProperty, null); host.ClearValue(FrameworkElement.HeightProperty); };
            host.BeginAnimation(FrameworkElement.HeightProperty, a);
        }
        else
        {
            double from = host.ActualHeight;
            var a = new System.Windows.Media.Animation.DoubleAnimation
            { From = from, To = 0, Duration = dur, EasingFunction = ease };
            a.Completed += (_, _) => { host.BeginAnimation(FrameworkElement.HeightProperty, null); host.Visibility = Visibility.Collapsed; host.ClearValue(FrameworkElement.HeightProperty); };
            host.BeginAnimation(FrameworkElement.HeightProperty, a);
        }
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
        var msg = _branch.HasUpstream ? $"커밋 {_branch.Ahead}개를 원격에 푸시할까요?" : "이 브랜치를 origin 에 처음 푸시할까요?";
        if (!ConfirmDialog.Show("푸시", msg, "푸시")) return;
        await RunRemote(() => GitService.PushAsync(_repo!), "푸시");
    }
    private async void Pull_Click(object s, RoutedEventArgs e) => await RunRemote(() => GitService.PullAsync(_repo!), "pull");
    private async void Fetch_Click(object s, RoutedEventArgs e) => await RunRemote(() => GitService.FetchAsync(_repo!), "fetch");

    private async Task RunRemote(Func<Task<GitService.GitResult>> op, string label)
    {
        if (_repo == null) return;
        _busy = true; UpdateButtons();
        var r = await op();
        _busy = false;
        if (!r.Ok) ConfirmDialog.Alert($"{label} 실패", string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error);
        await RefreshAsync();
        GitStateChanged?.Invoke(_repo);
    }
}
