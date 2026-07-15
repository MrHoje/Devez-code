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
        CommitBtn.IsEnabled = !_busy && _staged.Count > 0 && !string.IsNullOrWhiteSpace(MsgBox.Text);
        PushBtn.IsEnabled = PullBtn.IsEnabled = FetchBtn.IsEnabled = !_busy && _branch.Branch != null;

        StagedHeader.Text = $"Staged Changes {_staged.Count}";
        StagedHeader.Visibility = _staged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesHeader.Text = $"Changes {_unstaged.Count}";
        ChangesHeader.Visibility = _unstaged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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

    private void Node_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ScmTreeNode node }) return;
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
        if (msg.Length == 0 || _staged.Count == 0) return;
        if (!await GitService.HasIdentityAsync(_repo))
        { ConfirmDialog.Alert("커밋 불가", "git 사용자 정보가 없습니다.\ngit config user.name / user.email 설정 후 다시 시도하세요."); return; }
        _busy = true; UpdateButtons();
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
