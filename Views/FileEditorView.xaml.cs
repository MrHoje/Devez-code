using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DevezCode.Views;

public partial class FileEditorView : UserControl, IFileTabEditor
{
    public event EventHandler? CloseRequested;
    public event EventHandler? DirtyChanged;

    private string? _path;
    private bool _loading;

    public FileEditorView() => InitializeComponent();

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
                MessageBox.Show($"파일이 너무 큽니다 (5MB 초과).\n{path}", "DevezCode",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            _loading = true;
            Editor.SyntaxHighlighting = ThemeHighlighting(GetHighlighting(path));
            Editor.Text = File.ReadAllText(path);
            _loading = false;

            _path = path;
            SetDirty(false);

            Visibility = Visibility.Visible;
            Editor.Focus();
            Editor.CaretOffset = 0;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"파일을 열 수 없습니다.\n{ex.Message}", "DevezCode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private static IHighlightingDefinition? GetHighlighting(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mgr = HighlightingManager.Instance;
        return ext switch
        {
            ".cs" or ".csx" => mgr.GetDefinition("C#"),
            ".xaml" or ".xml" or ".csproj" or ".props" or ".targets" or ".config" or ".plist" or ".svg"
                => mgr.GetDefinition("XML"),
            ".html" or ".htm" or ".cshtml" or ".razor" => mgr.GetDefinition("HTML"),
            ".css" or ".scss" or ".less" => mgr.GetDefinition("CSS"),
            ".js" or ".jsx" => mgr.GetDefinition("JavaScript"),
            ".ts" or ".tsx" => mgr.GetDefinition("C#"),
            ".py" => mgr.GetDefinition("Python"),
            ".sql" => mgr.GetDefinition("SQL"),
            ".php" => mgr.GetDefinition("PHP"),
            ".java" => mgr.GetDefinition("Java"),
            ".c" or ".h" => mgr.GetDefinition("C"),
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".hxx" => mgr.GetDefinition("C++"),
            ".vb" => mgr.GetDefinition("VB"),
            ".ps1" or ".psm1" => mgr.GetDefinition("PowerShell"),
            ".bat" or ".cmd" => mgr.GetDefinition("BAT"),
            ".json" => mgr.GetDefinition("C#"),
            ".go" => mgr.GetDefinition("C#"),
            ".rs" => mgr.GetDefinition("C#"),
            ".swift" => mgr.GetDefinition("C#"),
            ".kt" or ".kts" => mgr.GetDefinition("C#"),
            ".rb" => mgr.GetDefinition("C#"),
            ".fs" => mgr.GetDefinition("F#"),
            _ => null,
        };
    }

    // 하이라이팅 정의별 원본 토큰 색 보존(공유 싱글톤이라 덮어쓰기 누적 방지)
    private static readonly Dictionary<string, Dictionary<string, System.Windows.Media.Color?>> _origColors = new();

    /// <summary>현재 테마 배경이 어두우면 토큰색을 밝게 보정해 가독성 확보. 밝은 테마는 원본 유지.</summary>
    private static IHighlightingDefinition? ThemeHighlighting(IHighlightingDefinition? def)
    {
        if (def == null) return null;
        bool dark = Application.Current?.TryFindResource("BgColor") is System.Windows.Media.Color bg
                    && Luminance(bg) < 0.5;

        if (!_origColors.TryGetValue(def.Name, out var orig))
        {
            orig = new();
            foreach (var c in def.NamedHighlightingColors)
                orig[c.Name] = c.Foreground?.GetColor(null);
            _origColors[def.Name] = orig;
        }

        foreach (var c in def.NamedHighlightingColors)
        {
            if (!orig.TryGetValue(c.Name, out var o) || o is not System.Windows.Media.Color src) continue;
            c.Foreground = new SimpleHighlightingBrush(dark ? Brighten(src) : src);
        }
        return def;
    }

    private static double Luminance(System.Windows.Media.Color c)
        => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static System.Windows.Media.Color Brighten(System.Windows.Media.Color c)
    {
        double lum = Luminance(c);
        if (lum >= 0.55) return c;                 // 이미 밝으면 그대로
        double f = 0.62 / System.Math.Max(lum, 0.04);
        byte Ch(double v) => (byte)System.Math.Min(255, v * f);
        return System.Windows.Media.Color.FromRgb(Ch(c.R), Ch(c.G), Ch(c.B));
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
            MessageBox.Show($"저장에 실패했습니다.\n{ex.Message}", "DevezCode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private bool ConfirmDiscardOrSave()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show("변경 사항을 저장하시겠습니까?", "DevezCode",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return Save();
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
