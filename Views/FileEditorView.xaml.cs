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
            Editor.SyntaxHighlighting = GetHighlighting(path);
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
