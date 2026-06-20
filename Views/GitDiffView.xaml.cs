using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>우측 패널 DIFF 뷰 — 선택된 프로젝트의 git 변경 파일 목록 + 선택 파일 diff.</summary>
public partial class GitDiffView : UserControl
{
    public GitDiffView() => InitializeComponent();

    private string? _repo;
    private readonly ObservableCollection<GitChange> _changes = new();
    private readonly ObservableCollection<DiffLine> _diff = new();

    /// <summary>대용량 파일/diff 보호용 상한.</summary>
    private const int MaxDiffLines = 8000;
    private const long MaxUntrackedBytes = 2 * 1024 * 1024;

    /// <summary>대상 저장소(프로젝트 루트) 설정. 경로가 바뀌면 다음 Refresh 때 새로 읽는다.</summary>
    public void SetRepo(string? path)
    {
        if (_repo == path) return;
        _repo = path;
        _changes.Clear();
        _diff.Clear();
    }

    /// <summary>git 상태를 다시 읽어 변경 파일 목록을 채운다.</summary>
    public async Task RefreshAsync()
    {
        ChangesHost.ItemsSource = _changes;
        DiffHost.ItemsSource = _diff;
        _changes.Clear();
        _diff.Clear();
        DiffHint.Visibility = Visibility.Visible;

        if (string.IsNullOrEmpty(_repo) || !Directory.Exists(_repo))
        {
            ShowEmpty("프로젝트를 선택하면 git 변경 내역이 표시됩니다.");
            return;
        }
        if (!await GitService.IsRepoAsync(_repo))
        {
            ShowEmpty("git 저장소가 아니거나 git 이 설치되어 있지 않습니다.");
            return;
        }

        var r = await GitService.RunAsync(_repo, "status", "--porcelain=v1", "-u");
        if (!r.Ok)
        {
            ShowEmpty(string.IsNullOrWhiteSpace(r.Error) ? "git 상태를 읽지 못했습니다." : r.Error.Trim());
            return;
        }

        foreach (var line in r.Output.Split('\n'))
        {
            var change = ParseStatusLine(line);
            if (change != null) _changes.Add(change);
        }

        if (_changes.Count == 0) ShowEmpty("변경된 파일이 없습니다.");
        else EmptyText.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
        DiffHint.Visibility = Visibility.Visible;
    }

    /// <summary>porcelain=v1 한 줄을 GitChange 로 파싱. 형식: "XY path" / "?? path" / "R  old -> new".</summary>
    private static GitChange? ParseStatusLine(string line)
    {
        if (line.Length < 4) return null;
        var x = line[0];
        var y = line[1];
        var rest = line[3..].Trim();
        if (rest.Length == 0) return null;

        // 이름 변경/복사: "old -> new" → new 경로를 표시.
        var arrow = rest.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow >= 0) rest = rest[(arrow + 4)..];

        var untracked = x == '?' && y == '?';
        // 스테이지(X)·워킹트리(Y) 중 의미 있는 상태 문자를 고른다.
        var code = untracked ? '?' : (x != ' ' && x != '?' ? x : y);
        var status = code switch
        {
            'A' => "A",
            'D' => "D",
            'M' => "M",
            'R' => "R",
            'C' => "R",
            '?' => "?",
            _   => "M",
        };
        return new GitChange { Status = status, Path = rest, IsUntracked = untracked };
    }

    private async void Change_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GitChange change }) return;
        await LoadDiffAsync(change);
    }

    private async Task LoadDiffAsync(GitChange change)
    {
        _diff.Clear();
        DiffHint.Visibility = Visibility.Collapsed;
        DiffScroller.ScrollToTop();
        DiffScroller.ScrollToLeftEnd();

        if (string.IsNullOrEmpty(_repo)) return;

        if (change.IsUntracked)
        {
            await LoadUntrackedAsync(change.Path);
            return;
        }

        var r = await GitService.RunAsync(_repo, "diff", "HEAD", "--", change.Path);
        var text = r.Output;
        if (string.IsNullOrWhiteSpace(text))
        {
            // HEAD 대비 차이가 없으면(예: 인덱스에만 추가) 워킹트리 diff 재시도.
            r = await GitService.RunAsync(_repo, "diff", "--", change.Path);
            text = r.Output;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            _diff.Add(new DiffLine { Text = "(표시할 텍스트 diff 가 없습니다 — 바이너리이거나 변경 없음)", Kind = DiffLineKind.Header });
            return;
        }
        AddDiffLines(text);
    }

    private async Task LoadUntrackedAsync(string relPath)
    {
        var full = Path.Combine(_repo!, relPath.Replace('/', Path.DirectorySeparatorChar));
        _diff.Add(new DiffLine { Text = $"신규 파일: {relPath}", Kind = DiffLineKind.Header });
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) { _diff.Add(new DiffLine { Text = "(파일을 찾을 수 없습니다)", Kind = DiffLineKind.Header }); return; }
            if (info.Length > MaxUntrackedBytes)
            {
                _diff.Add(new DiffLine { Text = "(파일이 너무 커서 미리보기를 생략합니다)", Kind = DiffLineKind.Header });
                return;
            }
            var lines = await File.ReadAllLinesAsync(full);
            var count = 0;
            foreach (var l in lines)
            {
                if (++count > MaxDiffLines) { _diff.Add(new DiffLine { Text = "… (이하 생략)", Kind = DiffLineKind.Header }); break; }
                _diff.Add(new DiffLine { Text = "+" + l, Kind = DiffLineKind.Add });
            }
        }
        catch
        {
            _diff.Add(new DiffLine { Text = "(텍스트로 읽을 수 없는 파일입니다)", Kind = DiffLineKind.Header });
        }
    }

    private void AddDiffLines(string text)
    {
        var count = 0;
        foreach (var raw in text.Split('\n'))
        {
            if (++count > MaxDiffLines) { _diff.Add(new DiffLine { Text = "… (diff 가 너무 길어 이하 생략)", Kind = DiffLineKind.Header }); break; }
            var line = raw.TrimEnd('\r');
            _diff.Add(new DiffLine { Text = line, Kind = ClassifyDiffLine(line) });
        }
    }

    private static DiffLineKind ClassifyDiffLine(string line)
    {
        if (line.StartsWith("@@", StringComparison.Ordinal)) return DiffLineKind.Hunk;
        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("new file", StringComparison.Ordinal) || line.StartsWith("deleted file", StringComparison.Ordinal)
            || line.StartsWith("rename ", StringComparison.Ordinal) || line.StartsWith("similarity ", StringComparison.Ordinal))
            return DiffLineKind.Header;
        if (line.StartsWith("+", StringComparison.Ordinal)) return DiffLineKind.Add;
        if (line.StartsWith("-", StringComparison.Ordinal)) return DiffLineKind.Del;
        return DiffLineKind.Context;
    }
}
