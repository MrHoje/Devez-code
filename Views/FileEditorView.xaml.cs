using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DevezCode.Views;

public partial class FileEditorView : UserControl, IFileTabEditor
{
    public event EventHandler? CloseRequested;
    public event EventHandler? DirtyChanged;
    public event EventHandler? Interacted;

    private string? _path;
    private bool _loading;
    public FileEditorView()
    {
        InitializeComponent();
        // WPF 네이티브 에디터라 클릭이 이미 CenterArea 로 버블링되지만, 인터페이스 일관성 + 명시적 포커스 통지.
        PreviewMouseDown += (_, _) => Interacted?.Invoke(this, EventArgs.Empty);
        Editor.PreviewMouseWheel += Editor_PreviewMouseWheel;
        // 테마 전환 시 현재 에디터 리렌더. 전역 정의 색은 App.SetTheme 이 먼저 갱신하므로 여기선 재할당만.
        Loaded += (_, _) => App.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => App.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(string theme)
    {
        var def = Editor.SyntaxHighlighting;
        if (def == null) return;
        Editor.SyntaxHighlighting = null;
        Editor.SyntaxHighlighting = def; // 재할당 → 갱신된 색으로 리렌더.
    }

    /// <summary>확장자 → AvalonEdit 내장 정의. 내장에 없는 XML 계열/별칭은 매핑, 없으면 null(순수 텍스트).</summary>
    private static IHighlightingDefinition? ResolveDefinition(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        ext = ext switch
        {
            ".xaml" or ".csproj" or ".vbproj" or ".fsproj" or ".props" or ".targets"
                or ".config" or ".resx" or ".nuspec" or ".vue" or ".svelte"
                or ".razor" or ".cshtml" or ".xhtml" or ".axml" => ".xml",
            ".jsonc" or ".json5" => ".json",
            ".htm" => ".html",
            ".bash" or ".zsh" => ".sh",
            ".psm1" => ".ps1",
            _ => ext
        };
        return HighlightingManager.Instance.GetDefinitionByExtension(ext);
    }

    // Ctrl + 휠: 폰트 크기 확대/축소 (노트패드/코드에디터 관례). [8, 40] 클램프.
    private void Editor_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        double size = Editor.FontSize + (e.Delta > 0 ? 1 : -1);
        Editor.FontSize = System.Math.Max(8, System.Math.Min(40, size));
        e.Handled = true;
    }

    public static bool IsEditable(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name is "dockerfile" or "makefile" or "license" or "readme" or ".gitignore"
                 or ".gitattributes" or ".editorconfig" or ".env")
            return true;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".txt" or ".md" or ".markdown" or ".json" or ".xml" or ".sql"
            or ".yml" or ".yaml" or ".toml" or ".ini" or ".conf" or ".config" or ".cfg"
            or ".properties" or ".csv" or ".log" or ".html" or ".htm" or ".css" or ".scss"
            or ".less" or ".js" or ".jsx" or ".ts" or ".tsx" or ".vue" or ".svelte"
            or ".cs" or ".xaml" or ".razor" or ".cshtml" or ".vb" or ".fs"
            or ".py" or ".java" or ".kt" or ".kts" or ".go" or ".rs" or ".rb" or ".php"
            or ".c" or ".cpp" or ".cc" or ".h" or ".hpp" or ".m" or ".mm" or ".swift"
            or ".dart" or ".lua" or ".r" or ".pl" or ".sh" or ".bash" or ".zsh"
            or ".ps1" or ".psm1" or ".bat" or ".cmd" or ".gradle" or ".groovy" or ".scala"
            or ".sln" or ".csproj" or ".props" or ".targets" or ".gitignore";
    }

    public string? FilePath => _path;
    public bool IsDirty => _dirty;

    private bool _dirty;

    public bool LoadFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            const long maxBytes = 5 * 1024 * 1024;
            if (fi.Length > maxBytes)
            {
                ConfirmDialog.Alert("파일 열기", $"파일이 너무 큽니다 (5MB 초과).\n{path}",
                    iconKey: "IconTriangleAlert");
                return false;
            }

            _loading = true;
            Editor.Text = File.ReadAllText(path);
            _loading = false;

            // 구문 강조: 정의가 있으면 하이라이팅 + 라인번호(VS 스타일), 없으면 순수 텍스트.
            var def = ResolveDefinition(path);
            Editor.SyntaxHighlighting = def;
            Editor.ShowLineNumbers = def != null;

            _path = path;
            SetDirty(false);

            Visibility = Visibility.Visible;
            Editor.Focus();
            Editor.CaretOffset = 0;
            return true;
        }
        catch (Exception ex)
        {
            ConfirmDialog.Alert("파일 열기", $"파일을 열 수 없습니다.\n{ex.Message}",
                iconKey: "IconTriangleAlert");
            return false;
        }
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        SetDirty(true);
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
            File.WriteAllText(_path, Editor.Text);
            SetDirty(false);
            return true;
        }
        catch (Exception ex)
        {
            ConfirmDialog.Alert("저장 실패", $"저장에 실패했습니다.\n{ex.Message}",
                iconKey: "IconTriangleAlert");
            return false;
        }
    }

    /// <summary>WPF 네이티브 에디터 — airspace 문제가 없어(오버레이가 그대로 덮음) 스냅샷이 불필요. null 반환.</summary>
    public Task<System.Windows.Media.Imaging.BitmapSource?> CaptureSnapshotAsync()
        => Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(null);

    private bool ConfirmDiscardOrSave()
    {
        if (!_dirty) return true;
        var r = ConfirmDialog.ShowThreeWay(
            "저장되지 않은 변경사항",
            "변경 사항을 저장하시겠습니까?",
            primaryLabel: "저장",
            secondaryLabel: "저장 안 함",
            iconKey: "IconMessageSquare");
        if (r == ConfirmChoice.Cancel) return false;
        if (r == ConfirmChoice.Primary) return Save();
        return true;
    }

    public void RequestClose()
    {
        if (string.IsNullOrEmpty(_path)) return;
        if (!ConfirmDiscardOrSave()) return;
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
}
