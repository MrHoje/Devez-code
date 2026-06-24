using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DevezCode.Views;

public partial class MarkdownFileEditorView : UserControl, IFileTabEditor, IDisposable
{
    public event EventHandler? CloseRequested;
    public event EventHandler? DirtyChanged;

    private string? _path;
    private string _currentMarkdown = "";
    /// <summary>디스크에 마지막으로 기록한(또는 읽은) 내용 — 외부 변경 비교의 기준선.
    /// 이 값과 디스크가 다르면 다른 프로그램이 파일을 수정한 것으로 본다.</summary>
    private string _savedMarkdown = "";
    private bool _dirty;
    private bool _loaded;
    private bool _initStarted;

    private FileSystemWatcher? _fileWatcher;
    private DispatcherTimer? _watcherDebounce;
    /// <summary>재진입 가드 — 다이얼로그가 떠 있는 동안 추가 이벤트/포커스로 같은 검사가 다시 도는 것을 차단.</summary>
    private bool _checkInFlight;

    public MarkdownFileEditorView()
    {
        InitializeComponent();
        MdHost.MarkdownChanged += OnMarkdownChanged;
        MdHost.BaselineReady += OnBaselineReady;
        MdHost.SaveRequested += () => Save();
        MdHost.EditorReady += OnEditorReady;
        App.ThemeChanged += OnThemeChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initStarted) return;
        _initStarted = true;
        _ = MdHost.EnsureReadyAsync();
        // 윈도우 포커스 복귀 시 외부 변경 일괄 점검 — 다른 앱에서 메모를 편집하고 돌아온 케이스.
        if (Window.GetWindow(this) is { } win)
            win.Activated += OnWindowActivated;
    }

    public string? FilePath => _path;
    public bool IsDirty => _dirty;

    public bool LoadFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            const long maxBytes = 5 * 1024 * 1024;
            if (fi.Length > maxBytes)
            {
                MessageBox.Show($"파일이 너무 큽니다 (5MB 초과).\n{path}", "DevezCode",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            _path = path;
            _currentMarkdown = File.ReadAllText(path);
            _savedMarkdown = _currentMarkdown;
            SetDirty(false);
            _loaded = true;
            MdHost.ApplyTheme(App.CurrentTheme);
            MdHost.SetMarkdown(_currentMarkdown, markClean: true);
            SetupFileWatcher(path);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"파일을 열 수 없습니다.\n{ex.Message}", "DevezCode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void OnEditorReady()
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        MdHost.ApplyTheme(App.CurrentTheme);
        if (_loaded) MdHost.SetMarkdown(_currentMarkdown, markClean: true);
        MdHost.FocusEditor();
    }

    private void OnThemeChanged(string theme) => MdHost.ApplyTheme(theme);

    private void OnMarkdownChanged(string markdown, bool dirty)
    {
        if (_path == null) return;
        _currentMarkdown = markdown;
        SetDirty(dirty);
    }

    private void OnBaselineReady(string markdown)
    {
        if (_path == null) return;
        _currentMarkdown = markdown;
        SetDirty(false);
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Save()
    {
        if (_path == null) return false;
        try
        {
            File.WriteAllText(_path, _currentMarkdown);
            _savedMarkdown = _currentMarkdown; // 디스크 기준선 갱신
            MdHost.MarkClean();
            SetDirty(false);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"저장에 실패했습니다.\n{ex.Message}", "DevezCode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    public void RequestClose()
    {
        if (_path == null) return;
        if (_dirty)
        {
            var r = MessageBox.Show("변경 사항을 저장하시겠습니까?", "DevezCode",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes && !Save()) return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            Save();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            RequestClose();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /* ── 외부 변경 감지 (md-editor checkExternalChange 패턴) ────────── */

    private void SetupFileWatcher(string path)
    {
        DisposeFileWatcher();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            var name = Path.GetFileName(path);

            // Windows FS 이벤트는 저장 1회에도 Changed가 2~3회 연달아 옴(쓰기+메타) — 디바운스로 묶음.
            _watcherDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _watcherDebounce.Tick += (_, _) =>
            {
                _watcherDebounce!.Stop();
                CheckExternalChange();
            };

            _fileWatcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _fileWatcher.Changed += OnFileWatcherChanged;
            _fileWatcher.Created += OnFileWatcherChanged;
            // Renamed: 일부 에디터는 atomic-save로 rename→move를 쓰지만 같은 이름으로 돌아오지 않는 한
            // 우리 _path와 더는 매칭되지 않으므로 무시.
        }
        catch { DisposeFileWatcher(); }
    }

    private void OnFileWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (_path == null) return;
        if (!string.Equals(e.FullPath, _path, StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_watcherDebounce == null) return;
            _watcherDebounce.Stop();
            _watcherDebounce.Start();
        }, DispatcherPriority.Background);
    }

    private void DisposeFileWatcher()
    {
        if (_fileWatcher != null)
        {
            try { _fileWatcher.EnableRaisingEvents = false; } catch { }
            try
            {
                _fileWatcher.Changed -= OnFileWatcherChanged;
                _fileWatcher.Created -= OnFileWatcherChanged;
            } catch { }
            try { _fileWatcher.Dispose(); } catch { }
            _fileWatcher = null;
        }
        if (_watcherDebounce != null)
        {
            _watcherDebounce.Stop();
            _watcherDebounce = null;
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e) => CheckExternalChange();

    /// <summary>4-way 외부 변경 처리.
    /// 디스크==_savedMarkdown: 외부 변경 없음 → 무시.
    /// 디스크≠_savedMarkdown && !_dirty: 미저장 편집 없음 → 조용히 디스크 채택(토스트 알림).
    /// 디스크≠_savedMarkdown &&  _dirty: 미저장 편집 있음 → 사용자 선택 다이얼로그.
    ///   Primary  "디스크에서 불러오기": 콘텐츠 + _savedMarkdown + Toast UI baseline 모두 디스크로.
    ///   Secondary "현재 내용 유지"     : 콘텐츠 유지, _savedMarkdown과 Toast UI baseline만 디스크로 리베이스
    ///                                   (다음 사용자 편집/저장 시 새 디스크를 기준으로 dirty 판정).
    ///   Cancel  "취소"                : 아무것도 안 함 — 다음 이벤트/포커스 시 다시 확인.</summary>
    private void CheckExternalChange()
    {
        if (_checkInFlight) return;
        if (_path == null || !File.Exists(_path)) return;
        _checkInFlight = true;
        try
        {
            string diskContent;
            try { diskContent = File.ReadAllText(_path); }
            catch { return; } // 잠시 락 등 — 무시

            if (diskContent == _savedMarkdown) return;

            if (!_dirty)
            {
                // 미저장 편집 없음 → 조용히 디스크 채택
                ApplyDiskReload(diskContent, notify: true);
                return;
            }

            var name = Path.GetFileName(_path);
            var choice = ConfirmDialog.ShowThreeWay(
                "외부에서 파일이 변경됨",
                $"\"{name}\" 파일이 다른 프로그램에서 변경되었습니다.\n\n" +
                "디스크의 새 버전을 불러오면 편집 중인 내용이 사라집니다. 어떻게 할까요?",
                "디스크에서 불러오기",
                "현재 내용 유지");

            // 다이얼로그가 떠 있는 동안 또 바뀔 수 있으므로 최신 바이트로 한 번 더
            try
            {
                var latest = File.ReadAllText(_path);
                if (latest != diskContent) diskContent = latest;
            }
            catch { return; }

            switch (choice)
            {
                case ConfirmChoice.Primary:
                    ApplyDiskReload(diskContent, notify: false);
                    break;
                case ConfirmChoice.Secondary:
                    ApplyRebase(diskContent);
                    break;
                // Cancel: 다음 검사에서 다시
            }
        }
        finally
        {
            _checkInFlight = false;
        }
    }

    private void ApplyDiskReload(string content, bool notify)
    {
        _savedMarkdown = content;
        _currentMarkdown = content;
        SetDirty(false);
        MdHost.SetMarkdown(content, markClean: true);
        if (notify) MdHost.ShowToast("디스크에서 자동 갱신");
    }

    private void ApplyRebase(string content)
    {
        _savedMarkdown = content;
        // _currentMarkdown / _dirty 그대로
        MdHost.SetBaseline(content);
    }

    /// <summary>airspace 우회 — WebView2(MdHost) 화면 스냅샷. 앱 종료/오버레이 배경에 사용.</summary>
    public Task<System.Windows.Media.Imaging.BitmapSource?> CaptureSnapshotAsync() => MdHost.CaptureSnapshotAsync();

    public void Dispose()
    {
        DisposeFileWatcher();
        if (Window.GetWindow(this) is { } win)
            win.Activated -= OnWindowActivated;
        App.ThemeChanged -= OnThemeChanged;
        MdHost.Dispose();
    }
}
