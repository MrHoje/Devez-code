using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>우측 패널 DIFF 뷰 — 선택된 프로젝트의 git 변경 파일 목록 + 선택 파일 diff.</summary>
public partial class GitDiffView : UserControl
{
    public GitDiffView()
    {
        InitializeComponent();
        _diffFontSize = BaseFontSizeFor(SettingsService.LoadFontScale());
        DiffList.FontSize = _diffFontSize;
        // 전역 글꼴 단계(작게/크게)에 연동. 사용자가 Ctrl+휠로 따로 조절하면 그 값을 유지한다.
        App.FontScaleChanged += OnFontScaleChanged;
        Unloaded += (_, _) => App.FontScaleChanged -= OnFontScaleChanged;
    }

    /// <summary>전역 글꼴 단계별 diff 기본 크기. 작게(0)=13, 크게(1)=15 (+2 스텝).</summary>
    private static double BaseFontSizeFor(int scale) => 13 + scale * 2;

    private bool _userAdjusted;

    private void OnFontScaleChanged(int scale)
    {
        if (_userAdjusted) return; // Ctrl+휠 수동 조절을 우선한다
        _diffFontSize = BaseFontSizeFor(scale);
        if (IsLoaded) DiffList.FontSize = _diffFontSize;
    }

    private string? _repo;
    private GitChange? _selected;
    private readonly ObservableCollection<GitChange> _changes = new();
    // diff 행은 한 번에 만들어 ItemsSource 로 통째 할당(행마다 알림 없음 → 큰 파일도 빠름).
    private List<DiffRow> _diff = new();

    /// <summary>diff 코드 글꼴 크기(Ctrl+휠로 조절). 라인 텍스트는 DiffList 에서 상속.</summary>
    private double _diffFontSize = 12;
    private const double MinFontSize = 8, MaxFontSize = 28;

    /// <summary>대용량 파일/diff 보호용 상한. 전체 코드를 보여주려 컨텍스트를 넓게 가져오므로 넉넉히.</summary>
    private const int MaxDiffLines = 20000;
    private const long MaxUntrackedBytes = 4 * 1024 * 1024;
    /// <summary>전체 코드를 표시하기 위한 diff 컨텍스트 줄 수(파일 전체를 덮을 만큼 크게).</summary>
    private const string FullContext = "-U100000";

    /// <summary>대상 저장소(프로젝트 루트) 설정. 경로가 바뀌면 다음 Refresh 때 새로 읽는다.</summary>
    public void SetRepo(string? path)
    {
        if (_repo == path) return;
        _repo = path;
        _changes.Clear();
        _diff = new List<DiffRow>();
        if (IsLoaded) DiffList.ItemsSource = null;
    }

    /// <summary>git 상태를 다시 읽어 변경 파일 목록을 채운다.</summary>
    public async Task RefreshAsync()
    {
        ChangesHost.ItemsSource = _changes;
        _changes.Clear();
        _diff = new List<DiffRow>();
        DiffList.ItemsSource = null;
        _selected = null;
        DiffHint.Visibility = Visibility.Visible;
        DiffHeader.Visibility = Visibility.Collapsed;
        MarkerStrip.Children.Clear();

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
        if (_selected != null) _selected.IsSelected = false;
        _selected = change;
        change.IsSelected = true;
        await LoadDiffAsync(change);
    }

    /// <summary>Ctrl+휠: diff 코드 글꼴 크기 조절(편집기 줌 관례).</summary>
    private void DiffScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        _userAdjusted = true;
        _diffFontSize = Math.Clamp(_diffFontSize + (e.Delta > 0 ? 1 : -1), MinFontSize, MaxFontSize);
        DiffList.FontSize = _diffFontSize;
        e.Handled = true; // 폰트 조절 중에는 스크롤하지 않음
    }

    private async Task LoadDiffAsync(GitChange change)
    {
        _diff = new List<DiffRow>();
        DiffList.ItemsSource = null;
        DiffHint.Visibility = Visibility.Collapsed;
        DiffHeader.Visibility = Visibility.Visible;

        try
        {
            if (string.IsNullOrEmpty(_repo)) return;

            if (change.IsUntracked)
            {
                await LoadUntrackedAsync(change.Path);
                return;
            }

            // FullContext: 파일 전체가 컨텍스트로 출력되어 변경 외 코드까지 모두 보인다.
            var r = await GitService.RunAsync(_repo, "diff", FullContext, "HEAD", "--", change.Path);
            var text = r.Output;
            if (string.IsNullOrWhiteSpace(text))
            {
                // HEAD 대비 차이가 없으면(예: 인덱스에만 추가) 워킹트리 diff 재시도.
                r = await GitService.RunAsync(_repo, "diff", FullContext, "--", change.Path);
                text = r.Output;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                AddHunkRow("(표시할 텍스트 diff 가 없습니다 — 바이너리이거나 변경 없음)");
                return;
            }
            BuildSideBySide(text);
        }
        finally
        {
            DiffList.ItemsSource = _diff; // 통째 할당 — 새 소스라 스크롤은 자동으로 맨 위
            RedrawMarkers();
        }
    }

    private async Task LoadUntrackedAsync(string relPath)
    {
        var full = Path.Combine(_repo!, relPath.Replace('/', Path.DirectorySeparatorChar));
        AddHunkRow($"신규 파일: {relPath}");
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) { AddHunkRow("(파일을 찾을 수 없습니다)"); return; }
            if (info.Length > MaxUntrackedBytes) { AddHunkRow("(파일이 너무 커서 미리보기를 생략합니다)"); return; }

            var lines = await File.ReadAllLinesAsync(full);
            var n = 0;
            foreach (var l in lines)
            {
                if (++n > MaxDiffLines) { AddHunkRow("… (이하 생략)"); break; }
                // 신규 파일: 우측(수정 후)에만 추가 라인, 좌측은 빈 셀.
                _diff.Add(new DiffRow
                {
                    LeftKind = DiffCellKind.Empty,
                    RightNum = n.ToString(), RightText = l, RightKind = DiffCellKind.Add,
                });
            }
        }
        catch { AddHunkRow("(텍스트로 읽을 수 없는 파일입니다)"); }
    }

    private void AddHunkRow(string text) => _diff.Add(new DiffRow { IsHunk = true, HunkText = text });

    /// <summary>unified diff 텍스트를 좌(수정 전)/우(수정 후) 행으로 정렬해 채운다.</summary>
    private void BuildSideBySide(string text)
    {
        int oldLn = 0, newLn = 0, total = 0;
        var dels = new List<(int num, string txt)>();
        var adds = new List<(int num, string txt)>();
        var truncated = false;

        // 삭제/추가 버퍼를 같은 행에 짝지어 flush. 남는 쪽은 빈 셀과 매칭.
        void Flush()
        {
            var n = Math.Max(dels.Count, adds.Count);
            for (var i = 0; i < n; i++)
            {
                (int num, string txt)? d = i < dels.Count ? dels[i] : null;
                (int num, string txt)? a = i < adds.Count ? adds[i] : null;
                _diff.Add(new DiffRow
                {
                    LeftNum  = d?.num.ToString() ?? "", LeftText  = d?.txt ?? "",
                    LeftKind  = d != null ? DiffCellKind.Del : DiffCellKind.Empty,
                    RightNum = a?.num.ToString() ?? "", RightText = a?.txt ?? "",
                    RightKind = a != null ? DiffCellKind.Add : DiffCellKind.Empty,
                });
            }
            dels.Clear();
            adds.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            if (total >= MaxDiffLines) { truncated = true; break; }
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                (oldLn, newLn) = ParseHunkHeader(line);
                AddHunkRow(line); total++;
                continue;
            }
            // 파일 머리글(diff/index/--- /+++ /new file …)은 side-by-side 본문에서 생략.
            if (line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("new file", StringComparison.Ordinal) || line.StartsWith("deleted file", StringComparison.Ordinal)
                || line.StartsWith("rename ", StringComparison.Ordinal) || line.StartsWith("similarity ", StringComparison.Ordinal)
                || line.StartsWith("\\ ", StringComparison.Ordinal))  // "\ No newline at end of file"
                continue;

            if (line.StartsWith("+", StringComparison.Ordinal)) { adds.Add((newLn++, line[1..])); total++; }
            else if (line.StartsWith("-", StringComparison.Ordinal)) { dels.Add((oldLn++, line[1..])); total++; }
            else
            {
                // context: 짝을 먼저 비우고 양쪽에 동일 라인.
                Flush();
                var t = line.Length > 0 ? line[1..] : "";
                _diff.Add(new DiffRow
                {
                    LeftNum = oldLn.ToString(),  LeftText = t,  LeftKind = DiffCellKind.Context,
                    RightNum = newLn.ToString(), RightText = t, RightKind = DiffCellKind.Context,
                });
                oldLn++; newLn++; total++;
            }
        }
        Flush();
        if (truncated) AddHunkRow("… (diff 가 너무 길어 이하 생략)");
    }

    /// <summary>"@@ -a,b +c,d @@" 에서 좌(a)·우(c) 시작 줄번호를 파싱.</summary>
    private static (int oldLn, int newLn) ParseHunkHeader(string line)
    {
        int old = 0, neu = 0;
        try
        {
            var minus = line.IndexOf('-');
            var plus = line.IndexOf('+');
            if (minus >= 0) old = ReadInt(line, minus + 1);
            if (plus >= 0) neu = ReadInt(line, plus + 1);
        }
        catch { /* 형식 이상 시 0 */ }
        return (old, neu);

        static int ReadInt(string s, int i)
        {
            var start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            return i > start ? int.Parse(s[start..i]) : 0;
        }
    }

    // ── 변경 위치 오버뷰 스트립 ─────────────────────────────────────
    private static readonly Brush MarkAdd = Frozen("#3FB950"); // 추가
    private static readonly Brush MarkDel = Frozen("#F85149"); // 삭제
    private static readonly Brush MarkMod = Frozen("#D29922"); // 수정(좌 삭제 + 우 추가)

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>행의 변경 종류 마커 색. context/hunk 는 null(표시 안 함).</summary>
    private static Brush? MarkerBrushOf(DiffRow row)
    {
        if (row.IsHunk) return null;
        var del = row.LeftKind == DiffCellKind.Del;
        var add = row.RightKind == DiffCellKind.Add;
        if (del && add) return MarkMod;
        if (del) return MarkDel;
        if (add) return MarkAdd;
        return null;
    }

    private void MarkerStrip_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawMarkers();

    /// <summary>전체 행 대비 변경 행 위치를 스트립에 색 막대로 그린다(연속 동색 구간은 병합).</summary>
    private void RedrawMarkers()
    {
        MarkerStrip.Children.Clear();
        var total = _diff.Count;
        var h = MarkerStrip.ActualHeight;
        var w = MarkerStrip.ActualWidth;
        if (total == 0 || h <= 0 || w <= 0) return;

        var i = 0;
        while (i < total)
        {
            var brush = MarkerBrushOf(_diff[i]);
            if (brush == null) { i++; continue; }
            var start = i;
            while (i < total && ReferenceEquals(MarkerBrushOf(_diff[i]), brush)) i++;
            var len = i - start;

            var y = (double)start / total * h;
            var rh = Math.Max(2.0, (double)len / total * h);
            var rect = new System.Windows.Shapes.Rectangle { Width = w, Height = rh, Fill = brush };
            Canvas.SetLeft(rect, 0);
            Canvas.SetTop(rect, y);
            MarkerStrip.Children.Add(rect);
        }
    }

    /// <summary>스트립 클릭 → 해당 비율 위치의 행으로 스크롤.</summary>
    private void MarkerStrip_Click(object sender, MouseButtonEventArgs e)
    {
        var h = MarkerStrip.ActualHeight;
        if (h <= 0 || _diff.Count == 0) return;
        var frac = Math.Clamp(e.GetPosition(MarkerStrip).Y / h, 0, 1);
        var idx = Math.Clamp((int)Math.Round(frac * (_diff.Count - 1)), 0, _diff.Count - 1);
        DiffList.ScrollIntoView(_diff[idx]);
    }
}
