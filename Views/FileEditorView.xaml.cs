using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DevezCode.Views;

/// <summary>파일 편집기 탭의 본체. 탭마다 1개의 인스턴스가 생성되어 콘텐츠 호스트에 붙는다.
/// "현재 다른 파일로 교체" 기능은 없음 — 파일을 바꾸려면 새 탭을 연다.</summary>
public partial class FileEditorView : UserControl, IFileTabEditor
{
    /// <summary>사용자가 닫기를 요청했을 때(저장 확인 포함) 발생. 호스트(=탭)가 탭을 제거한다.</summary>
    public event EventHandler? CloseRequested;
    public event EventHandler? DirtyChanged;

    private string? _path;
    private bool _loading;

    public FileEditorView() => InitializeComponent();

    /// <summary>인앱 편집기로 열 수 있는 텍스트 계열 파일인지 판정.</summary>
    public static bool IsEditable(string path)
    {
        // 확장자 없는 대표 텍스트 파일들
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

    /// <summary>현재 편집 중인 절대 경로. 미열림이면 null.</summary>
    public string? FilePath => _path;

    /// <summary>저장되지 않은 변경사항이 있는지.</summary>
    public bool IsDirty => _dirty;

    private bool _dirty;

    /// <summary>파일을 읽어 편집기에 표시. 성공 시 true. 호스트가 탭을 만들고 활성화한다.</summary>
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
            Editor.Text = File.ReadAllText(path);
            _loading = false;

            _path = path;
            SetDirty(false);

            Visibility = Visibility.Visible;
            Editor.Focus();
            Editor.CaretIndex = 0;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"파일을 열 수 없습니다.\n{ex.Message}", "DevezCode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
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

    /// <summary>변경분이 있으면 저장/취소를 묻고, 진행 가능하면 true.</summary>
    private bool ConfirmDiscardOrSave()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show("변경 사항을 저장하시겠습니까?", "DevezCode",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return Save();
        return true; // No → 변경 버림
    }

    /// <summary>편집기 닫기 요청 — 변경분 확인 후 호스트에 알린다.</summary>
    public void RequestClose()
    {
        if (string.IsNullOrEmpty(_path)) return;
        if (!ConfirmDiscardOrSave()) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── 키 입력: Ctrl+S 저장 / Esc 닫기 ─────────────────────────────
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
