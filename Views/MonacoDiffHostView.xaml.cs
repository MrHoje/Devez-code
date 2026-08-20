using System.IO;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>Monaco DiffEditor 를 호스팅하는 파일 탭 에디터(읽기 전용 diff).</summary>
public partial class MonacoDiffHostView : UserControl, IFileTabEditor, INativeInputSurface, IDisposable
{
    public event EventHandler? CloseRequested;
#pragma warning disable CS0067 // diff 는 dirty 없음 — IFileTabEditor 요구 이벤트라 선언만 하고 발화하지 않음
    public event EventHandler? DirtyChanged;
#pragma warning restore CS0067
    public event EventHandler? Interacted;
    public event EventHandler? NativeSurfaceFocused;

    private readonly string _repo;
    private readonly string _relPath;   // repo 기준 상대경로(/)
    private readonly bool _staged;
    private readonly MonacoHost _host = new();

    public MonacoDiffHostView(string repo, string relPath, bool staged)
    {
        InitializeComponent();
        _repo = repo; _relPath = relPath; _staged = staged;
        Root.Children.Add(_host);
        _host.PageReady += OnReady;
        App.ThemeChanged += OnThemeChanged;
        Loaded += async (_, _) => await _host.EnsureReadyAsync();
        Unloaded += (_, _) => App.ThemeChanged -= OnThemeChanged;
        _host.PreviewMouseDown += (_, _) => Interacted?.Invoke(this, EventArgs.Empty);
        _host.UserInteracted += () => NativeSurfaceFocused?.Invoke(this, EventArgs.Empty);
    }

    private void OnThemeChanged(string _) => _host.ApplyTheme();

    private async void OnReady()
    {
        var (orig, mod) = await LoadTextsAsync();
        // 추가된(원본 없는) 파일은 좌우 분할 대신 단일 뷰로.
        bool added = string.IsNullOrEmpty(orig) && !string.IsNullOrEmpty(mod);
        _host.SetDiff(orig, mod, LanguageOf(_relPath), sideBySide: !added);
    }

    private async Task<(string, string)> LoadTextsAsync()
    {
        // Unstaged: 인덱스 vs 작업트리 / Staged: HEAD vs 인덱스 / Untracked: 빈 vs 파일
        var abs = Path.Combine(_repo, _relPath.Replace('/', Path.DirectorySeparatorChar));
        if (_staged)
        {
            var head = await GitService.ShowFileAsync(_repo, "HEAD", _relPath);
            var index = await GitService.ShowFileAsync(_repo, ":", _relPath);
            return (head, index);
        }
        else
        {
            var index = await GitService.ShowFileAsync(_repo, ":", _relPath); // 추적 파일이면 내용, untracked 면 ""
            string work = "";
            try { if (File.Exists(abs)) work = await File.ReadAllTextAsync(abs); } catch { }
            return (index, work);
        }
    }

    private static string LanguageOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp", ".js" => "javascript", ".ts" => "typescript",
            ".json" => "json", ".xaml" or ".xml" => "xml", ".html" => "html",
            ".css" => "css", ".md" => "markdown", ".py" => "python",
            ".ps1" => "powershell", ".sh" => "shell", ".yml" or ".yaml" => "yaml",
            _ => "plaintext",
        };
    }

    // ── IFileTabEditor ──
    public string? FilePath => Path.Combine(_repo, _relPath.Replace('/', Path.DirectorySeparatorChar));
    public bool IsDirty => false;
    public bool LoadFile(string path) => true;   // diff 는 생성자에서 소스 확보
    public bool Save() => true;                   // no-op
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
    public new bool Focus() => _host.Focus();
    public Task<BitmapSource?> CaptureSnapshotAsync() => _host.CaptureSnapshotAsync();

    public void Dispose() => _host.Dispose();
}
