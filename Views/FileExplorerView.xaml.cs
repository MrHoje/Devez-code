using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;
using Microsoft.VisualBasic.FileIO;

namespace DevezCode.Views;

/// <summary>우측 파일 탐색기 — 선택된 프로젝트 디렉터리의 파일/폴더를 트리로 나열.</summary>
public partial class FileExplorerView : UserControl
{
    public FileExplorerView()
    {
        InitializeComponent();
        SwitchTab(0); // 초기: 디렉터리 탭 활성 표시
        Tree.ContextMenu = BuildEmptyAreaMenu(); // 빈 영역 우클릭 메뉴 (Tree 자체)
    }

    /// <summary>빈 영역 우클릭 메뉴: 새 파일/폴더 + 붙여넣기 + 탐색기에서 열기. Tree 의 ContextMenu 로 부착.</summary>
    private ContextMenu BuildEmptyAreaMenu()
    {
        var cm = new ContextMenu();
        var newFile = new MenuItem { Header = "새 파일" };
        newFile.Icon = new System.Windows.Shapes.Path { Style = (Style)FindResource("LucideMenuIcon"), Data = (System.Windows.Media.Geometry)FindResource("IconFilePlus") };
        newFile.Click += RootNewFile_Click;

        var newFolder = new MenuItem { Header = "새 폴더" };
        newFolder.Icon = new System.Windows.Shapes.Path { Style = (Style)FindResource("LucideMenuIcon"), Data = (System.Windows.Media.Geometry)FindResource("IconFolderPlus") };
        newFolder.Click += RootNewFolder_Click;

        var paste = new MenuItem { Header = "붙여넣기", InputGestureText = "Ctrl+V", Name = "RootPasteMenuItem" };
        paste.Icon = new System.Windows.Shapes.Path { Style = (Style)FindResource("LucideMenuIcon"), Data = (System.Windows.Media.Geometry)FindResource("IconClipboard") };
        paste.Click += RootPaste_Click;

        var reveal = new MenuItem { Header = "탐색기에서 열기" };
        reveal.Icon = new System.Windows.Shapes.Path { Style = (Style)FindResource("LucideMenuIcon"), Data = (System.Windows.Media.Geometry)FindResource("IconExternalLink") };
        reveal.Click += RootRevealInExplorer_Click;

        cm.Items.Add(newFile);
        cm.Items.Add(newFolder);
        cm.Items.Add(new Separator());
        cm.Items.Add(paste);
        cm.Items.Add(new Separator());
        cm.Items.Add(reveal);
        cm.Opened += EmptyAreaMenu_Opened;
        return cm;
    }

    /// <summary>Tree 우클릭 시: 항목 위면 Tree.ContextMenu 를 숨겨 항목 메뉴만 뜨게 한다.
    /// 빈 영역이면 Tree.ContextMenu 가 정상 표시된다.</summary>
    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 우클릭 위치의 원본 요소가 TreeViewItem (또는 그 자식) 이면 → 항목 메뉴만 표시, Tree.ContextMenu 는 막음
        if (e.OriginalSource is DependencyObject d)
        {
            for (DependencyObject? cur = d; cur != null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            {
                if (cur is System.Windows.Controls.TreeViewItem)
                {
                    Tree.ContextMenu = null; // 항목 메뉴만 표시
                    // 다음 빈 영역 우클릭을 위해 복원 (Dispatcher 로 지연)
                    Dispatcher.BeginInvoke(new System.Action(() => Tree.ContextMenu = BuildEmptyAreaMenu()));
                    return;
                }
                if (cur == Tree) break; // TreeViewItem 가 아닌 Tree 영역에 도달
            }
        }
    }

    /// <summary>편집 가능한 파일을 더블클릭했을 때 발생(인앱 편집기로 열도록 호스트에 위임).</summary>
    public event EventHandler<string>? FileOpenRequested;

    private string? _rootPath;

    /// <summary>우측 패널의 현재 뷰 모드.</summary>
    private enum ViewMode { Directory, Browser, Diff, Queue }
    private ViewMode _mode = ViewMode.Directory;
    private bool _browserMode => _mode == ViewMode.Browser;

    // ── 디렉터리 / 브라우저 / DIFF 뷰 전환 ──────────────────────────────
    // (단일 클릭 핸들러 — 텍스트 색상으로만 활성/비활성 구분)

    private void TabBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var idx))
            SwitchTab(idx);
    }

    private void SwitchTab(int idx)
    {
        _mode = (ViewMode)idx;
        Tree.Visibility    = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        Browser.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        DiffView.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        QueueView.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (idx == 1) Browser.EnsureStarted();       // 최초 진입 시 WebView2 초기화
        if (idx == 2) _ = DiffView.RefreshAsync();  // 진입할 때마다 최신 변경 내역 로드

        var (icon, title) = idx switch
        {
            1 => ("IconGlobe", "브라우저"),
            2 => ("IconGitCompare", "DIFF"),
            3 => ("IconListTodo", "작업 큐"),
            _ => ("IconFolderOpen", _rootPath ?? "파일 탐색기"),
        };
        HeaderIcon.Data = (System.Windows.Media.Geometry)FindResource(icon);
        PathText.Text = title;

        UpdateTabTextColors();
    }

    private void UpdateTabTextColors()
    {
        SetTabColor(DirViewIcon,     DirViewLabel,     _mode == ViewMode.Directory);
        SetTabColor(BrowserViewIcon, BrowserViewLabel, _mode == ViewMode.Browser);
        SetTabColor(DiffViewIcon,    DiffViewLabel,    _mode == ViewMode.Diff);
        SetTabColor(QueueViewIcon,   QueueViewLabel,   _mode == ViewMode.Queue);
    }

    /// <summary>탭 아이콘·라벨 색상: 활성=PrimaryBrush, 비활성=TextMutedBrush.
    /// 굵기는 항상 SemiBold로 고정 (활성/비활성 전환 시 글자 폭이 바뀌어 텍스트가 움직이는 현상 방지).</summary>
    private void SetTabColor(System.Windows.Shapes.Path icon, TextBlock label, bool active)
    {
        icon.Stroke = (System.Windows.Media.Brush)FindResource(active ? "PrimaryBrush" : "TextMutedBrush");
        label.Foreground = (System.Windows.Media.Brush)FindResource(active ? "PrimaryBrush" : "TextMutedBrush");
        label.FontWeight = FontWeights.SemiBold;
    }

    /// <summary>airspace: 오버레이가 뜰 때 브라우저 WebView2 를 스냅샷으로 숨김.</summary>
    public Task SuspendBrowserAsync()
        => _browserMode && Browser.IsStarted ? Browser.SuspendContentAsync() : Task.CompletedTask;
    public void ResumeBrowser() { if (Browser.IsStarted) Browser.ResumeContent(); }

    /// <summary>앱 종료 시 WebView2 해제.</summary>
    public void DisposeBrowser() => Browser.DisposeAll();

    /// <summary>탐색기를 지정한 디렉터리로 전환. null 이면 안내 문구.</summary>
    public void ShowDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            _rootPath = null;
            if (_mode == ViewMode.Directory) PathText.Text = "파일 탐색기";
            Tree.ItemsSource = null;
            DiffView.SetRepo(null);
            return;
        }
        if (_rootPath == path) return;
        _rootPath = path;
        if (_mode == ViewMode.Directory) PathText.Text = path;
        DiffView.SetRepo(path);
        // diff 탭이 현재 켜져 있으면 SetRepo 가 비워버리므로 즉시 새로 읽어 동기화.
        // (다른 탭이면 사용자가 diff 탭으로 진입할 때 SwitchTab 에서 RefreshAsync 가 호출됨.)
        if (_mode == ViewMode.Diff) _ = DiffView.RefreshAsync();

        var roots = new ObservableCollection<FileNode>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                if (!IsHidden(d)) roots.Add(FileNode.FromDirectory(d));
            foreach (var f in Directory.EnumerateFiles(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                if (!IsHidden(f)) roots.Add(new FileNode { Name = Path.GetFileName(f), FullPath = f, IsDirectory = false });
        }
        catch { /* 접근 거부 등 */ }
        Tree.ItemsSource = roots;
    }

    /// <summary>루트 목록을 디스크 상태로 다시 읽는다(루트 레벨 항목 변경 후).</summary>
    private void ReloadRoot()
    {
        var p = _rootPath;
        _rootPath = null;          // ShowDirectory 의 동일 경로 early-return 회피
        ShowDirectory(p);
    }

    private static bool IsHidden(string p)
    {
        try { var a = File.GetAttributes(p); return a.HasFlag(FileAttributes.Hidden) || a.HasFlag(FileAttributes.System); }
        catch { return false; }
    }

    /// <summary>파일 더블클릭 → OS 기본 앱으로 열기.</summary>
    private void Node_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is FrameworkElement { DataContext: FileNode node } && !node.IsDirectory)
        {
            // 텍스트 계열은 인앱 편집기로, 그 외(이미지·바이너리 등)는 OS 기본 앱으로.
            if (FileEditorView.IsEditable(node.FullPath))
                FileOpenRequested?.Invoke(this, node.FullPath);
            else
                OpenWithShell(node);
            e.Handled = true;
        }
    }

    /// <summary>파일 우클릭 → 선택 변경 (좌클릭과 동일하게 해당 파일 선택).
    /// 컨텍스트 메뉴는 별도로 항목의 ContextMenu 가 떠서 마우스 위치 기준 노드를 사용한다.</summary>
    private void Node_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileNode node })
        {
            // TreeView.SelectedItem 은 읽기 전용 — 해당 컨테이너의 IsSelected 를 직접 세팅.
            if (Tree.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem tvi)
                tvi.IsSelected = true;
        }
    }

    // ── 컨텍스트 메뉴 ───────────────────────────────────────────────
    private static FileNode? NodeOf(object sender) => (sender as MenuItem)?.DataContext as FileNode;

    /// <summary>OS 기본 핸들러로 실행될 때 사용자에게 한 번 더 확인받을 실행형 확장자.</summary>
    private static readonly HashSet<string> ExecutableExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".ps1", ".psm1", ".scr", ".lnk", ".msi",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".cpl", ".reg", ".pif", ".jar",
    };

    /// <summary>OS 기본 앱으로 열되, 실행형 파일은 한 번 더 확인(악성 리포의 위장 실행파일 방지).</summary>
    private static void OpenWithShell(FileNode node)
    {
        if (ExecutableExts.Contains(Path.GetExtension(node.FullPath))
            && !ConfirmDialog.Show("실행 파일 열기",
                   $"'{node.Name}'은(는) 실행 파일입니다. 정말 실행할까요?", "실행", danger: true))
            return;
        try { Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true }); }
        catch { /* 열 수 없는 파일 무시 */ }
    }

    /// <summary>새 항목·이름변경용 이름이 단일 파일명인지(경로 구분자·드라이브·예약문자·.. 거부 → 컨테이너 밖 탈출 차단).</summary>
    private static bool IsValidLeafName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name is not ("." or "..")
           && name == Path.GetFileName(name)
           && name.TrimEnd('.', ' ') == name   // 후행 점·공백 거부 — Windows 가 떼어내 다른 파일을 지칭/덮어쓰는 것 방지
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>심볼릭 링크·정션(reparse point) 여부. 복사 시 추종을 막아 외부 데이터 유출·순환 재귀를 차단.</summary>
    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    /// <summary>메뉴 열릴 때 클립보드에 파일이 있을 때만 '붙여넣기' 활성화.</summary>
    private void NodeMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        var hasFiles = Clipboard.ContainsFileDropList();
        foreach (var item in cm.Items)
            if (item is MenuItem { Name: "PasteMenuItem" } mi) mi.IsEnabled = hasFiles;
    }

    /// <summary>빈 영역(TreeView 자체) 메뉴 열릴 때 붙여넣기 활성/비활성.</summary>
    private void EmptyAreaMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        var hasFiles = Clipboard.ContainsFileDropList();
        foreach (var item in cm.Items)
            if (item is MenuItem { Name: "RootPasteMenuItem" } mi) mi.IsEnabled = hasFiles;
    }

    /// <summary>대상 노드가 들어있는 폴더 경로. 폴더면 자기 자신, 파일이면 부모 폴더.</summary>
    private static string ContainerDir(FileNode node)
        => node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath) ?? node.FullPath;

    /// <summary>대상 노드의 부모 폴더를 갱신(이름변경·삭제 후). 루트 레벨이면 루트 재로딩.</summary>
    private void RefreshParent(FileNode node)
    {
        if (node.Parent is { } p) p.Refresh();
        else ReloadRoot();
    }

    /// <summary>대상 노드를 컨테이너로 보고 그 안의 새 항목을 반영(새 파일·폴더·붙여넣기 후).</summary>
    private void RefreshContainer(FileNode node)
    {
        if (node.IsDirectory) { node.IsExpanded = true; node.Refresh(); }
        else RefreshParent(node);
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node) return;
        var name = PromptDialog.Show("새 파일", "파일 이름을 입력하세요.");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!IsValidLeafName(name)) { ConfirmDialog.Alert("새 파일", "사용할 수 없는 이름입니다."); return; }
        var dest = Path.Combine(ContainerDir(node), name);
        if (File.Exists(dest) || Directory.Exists(dest)) { ConfirmDialog.Alert("새 파일", "같은 이름이 이미 있습니다."); return; }
        try { File.Create(dest).Dispose(); }
        catch (Exception ex) { ConfirmDialog.Alert("새 파일 실패", ex.Message); return; }
        RefreshContainer(node);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node) return;
        var name = PromptDialog.Show("새 폴더", "폴더 이름을 입력하세요.");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!IsValidLeafName(name)) { ConfirmDialog.Alert("새 폴더", "사용할 수 없는 이름입니다."); return; }
        var dest = Path.Combine(ContainerDir(node), name);
        if (File.Exists(dest) || Directory.Exists(dest)) { ConfirmDialog.Alert("새 폴더", "같은 이름이 이미 있습니다."); return; }
        try { Directory.CreateDirectory(dest); }
        catch (Exception ex) { ConfirmDialog.Alert("새 폴더 실패", ex.Message); return; }
        RefreshContainer(node);
    }

    /// <summary>빈 영역 메뉴의 "새 파일" — 루트 디렉터리에 생성.</summary>
    private void RootNewFile_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_rootPath) || !Directory.Exists(_rootPath)) return;
        var name = PromptDialog.Show("새 파일", "파일 이름을 입력하세요.");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!IsValidLeafName(name)) { ConfirmDialog.Alert("새 파일", "사용할 수 없는 이름입니다."); return; }
        var dest = Path.Combine(_rootPath, name);
        if (File.Exists(dest) || Directory.Exists(dest)) { ConfirmDialog.Alert("새 파일", "같은 이름이 이미 있습니다."); return; }
        try { File.Create(dest).Dispose(); }
        catch (Exception ex) { ConfirmDialog.Alert("새 파일 실패", ex.Message); return; }
        ReloadRoot();
    }

    /// <summary>빈 영역 메뉴의 "새 폴더" — 루트 디렉터리에 생성.</summary>
    private void RootNewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_rootPath) || !Directory.Exists(_rootPath)) return;
        var name = PromptDialog.Show("새 폴더", "폴더 이름을 입력하세요.");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!IsValidLeafName(name)) { ConfirmDialog.Alert("새 폴더", "사용할 수 없는 이름입니다."); return; }
        var dest = Path.Combine(_rootPath, name);
        if (File.Exists(dest) || Directory.Exists(dest)) { ConfirmDialog.Alert("새 폴더", "같은 이름이 이미 있습니다."); return; }
        try { Directory.CreateDirectory(dest); }
        catch (Exception ex) { ConfirmDialog.Alert("새 폴더 실패", ex.Message); return; }
        ReloadRoot();
    }

    /// <summary>빈 영역 메뉴의 "붙여넣기" — 클립보드 파일을 루트에 복사/이동.</summary>
    private void RootPaste_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_rootPath) || !Directory.Exists(_rootPath)) return;
        var files = Clipboard.GetFileDropList();
        if (files.Count == 0) return;
        var move = ClipboardIsMove();
        try
        {
            foreach (string? src in files)
            {
                if (string.IsNullOrEmpty(src)) continue;
                if (!File.Exists(src) && !Directory.Exists(src)) continue;
                CopyInto(src, _rootPath, move);
            }
        }
        catch (Exception ex) { ConfirmDialog.Alert("붙여넣기 실패", ex.Message); }
        if (move) try { Clipboard.Clear(); } catch { }
        ReloadRoot();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node) return;
        var name = PromptDialog.Show("이름 바꾸기", "", node.Name);
        if (string.IsNullOrWhiteSpace(name) || name == node.Name) return;
        if (!IsValidLeafName(name)) { ConfirmDialog.Alert("이름 바꾸기", "사용할 수 없는 이름입니다."); return; }
        var dest = Path.Combine(Path.GetDirectoryName(node.FullPath) ?? "", name);
        if (File.Exists(dest) || Directory.Exists(dest)) { ConfirmDialog.Alert("이름 바꾸기", "같은 이름이 이미 있습니다."); return; }
        try
        {
            if (node.IsDirectory) Directory.Move(node.FullPath, dest);
            else File.Move(node.FullPath, dest);
        }
        catch (Exception ex) { ConfirmDialog.Alert("이름 바꾸기 실패", ex.Message); return; }
        RefreshParent(node);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node) return;
        if (!ConfirmDialog.Show("삭제", $"'{node.Name}'을(를) 휴지통으로 보낼까요?", "삭제", danger: true)) return;
        try
        {
            if (node.IsDirectory)
                FileSystem.DeleteDirectory(node.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                FileSystem.DeleteFile(node.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) { ConfirmDialog.Alert("삭제 실패", ex.Message); return; }
        RefreshParent(node);
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => SetClipboard(NodeOf(sender), move: false);
    private void Cut_Click(object sender, RoutedEventArgs e)  => SetClipboard(NodeOf(sender), move: true);

    private static void SetClipboard(FileNode? node, bool move)
    {
        if (node == null) return;
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { node.FullPath });
        var effect = move ? DragDropEffects.Move : DragDropEffects.Copy;
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)effect)));
        try { Clipboard.SetDataObject(data, true); } catch { /* 클립보드 점유 등 무시 */ }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node) return;
        var files = Clipboard.GetFileDropList();
        if (files.Count == 0) return;
        var destDir = ContainerDir(node);
        var move = ClipboardIsMove();
        try
        {
            foreach (string? src in files)
            {
                if (string.IsNullOrEmpty(src)) continue;
                if (!File.Exists(src) && !Directory.Exists(src)) continue;
                CopyInto(src, destDir, move);
            }
        }
        catch (Exception ex) { ConfirmDialog.Alert("붙여넣기 실패", ex.Message); }
        if (move) try { Clipboard.Clear(); } catch { }
        RefreshContainer(node);
    }

    private static bool ClipboardIsMove()
    {
        try
        {
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms)
            {
                var bytes = new byte[4];
                ms.Read(bytes, 0, 4);
                return ((DragDropEffects)BitConverter.ToInt32(bytes, 0)).HasFlag(DragDropEffects.Move);
            }
        }
        catch { }
        return false;
    }

    private static void CopyInto(string src, string destDir, bool move)
    {
        var name = Path.GetFileName(src.TrimEnd('\\', '/'));
        var dest = UniquePath(Path.Combine(destDir, name));
        var isDir = Directory.Exists(src);
        if (move)
        {
            // 이동은 링크 엔트리 자체를 옮기므로(추종 안 함) 안전.
            if (isDir) Directory.Move(src, dest); else File.Move(src, dest);
        }
        else
        {
            if (IsReparsePoint(src)) return; // 심링크/정션은 추종 복사 금지
            if (isDir) CopyDir(src, dest, 0); else File.Copy(src, dest);
        }
    }

    /// <summary>재귀 복사 최대 깊이. 순환 정션 등으로 인한 무한 재귀·디스크 고갈 방어.</summary>
    private const int MaxCopyDepth = 64;

    private static void CopyDir(string src, string dest, int depth)
    {
        if (depth > MaxCopyDepth) throw new IOException("폴더 깊이가 너무 깊어 복사를 중단했습니다.");
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
        {
            if (IsReparsePoint(f)) continue;
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)));
        }
        foreach (var d in Directory.GetDirectories(src))
        {
            if (IsReparsePoint(d)) continue; // 정션/심링크 추종 금지 → 외부 데이터 유출·순환 차단
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)), depth + 1);
        }
    }

    /// <summary>같은 폴더에 붙여넣을 때 이름 충돌을 피해 " (2)", " (3)" … 을 붙인다.</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var cand = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
            try { Clipboard.SetText(node.FullPath); } catch { }
    }

    /// <summary>파일 우클릭 메뉴 맨 위의 "실행" — OS 기본 앱으로 열기.
    /// 더블클릭과 동일하지만 명시적 액션 + 키보드 Enter 단축키 제공.</summary>
    private void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node) return;
        if (node.IsDirectory) return; // 폴더는 실행 불가
        if (FileEditorView.IsEditable(node.FullPath))
            FileOpenRequested?.Invoke(this, node.FullPath);
        else
            OpenWithShell(node);
    }

    private void RevealInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node) return;
        string full;
        try { full = Path.GetFullPath(node.FullPath); } catch { return; }   // 경로 정규화
        if (!File.Exists(full) && !Directory.Exists(full)) return;          // 실존 항목만
        // 경로엔 따옴표가 들어올 수 없으므로(Windows 파일명 제약) 인용 주입 불가.
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = false }); }
        catch { /* 실패 무시 */ }
    }

    /// <summary>빈 영역 메뉴의 "탐색기에서 열기" — 루트 폴더를 Windows Explorer 에서 연다.</summary>
    private void RootRevealInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_rootPath) || !Directory.Exists(_rootPath)) return;
        string full;
        try { full = Path.GetFullPath(_rootPath); } catch { return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = false }); }
        catch { /* 실패 무시 */ }
    }
}
