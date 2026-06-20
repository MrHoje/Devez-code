using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DevezCode.Views;

/// <summary>중앙 영역을 분할한 우측 인앱 텍스트/코드 편집기.
/// 파일 탐색기에서 편집 가능한 파일을 더블클릭하면 열린다. 저장(Ctrl+S)·닫기(Esc) 지원.
/// 패널 폭 애니메이션·표시/숨김은 호스트(MainWindow)가 컬럼 단위로 제어한다.</summary>
public partial class FileEditorView : UserControl
{
    /// <summary>사용자가 닫기를 요청(변경분 확인 통과)했을 때 발생. 호스트가 패널을 접는다.</summary>
    public event EventHandler? CloseRequested;

    private string? _path;
    private bool _loading;
    private bool _dirty;

    public FileEditorView() => InitializeComponent();

    /// <summary>현재 편집기가 화면에 떠 있는지.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

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

    private const long MaxBytes = 5 * 1024 * 1024; // 5MB 초과 파일은 거부(대용량 보호)

    /// <summary>파일을 읽어 편집기에 표시. 로드/표시에 성공하면 true(호스트가 패널을 펼친다).
    /// 이미 다른 파일을 보고 있던 경우엔 내용만 교체한다.</summary>
    public bool Open(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            if (fi.Length > MaxBytes)
            {
                MessageBox.Show($"파일이 너무 큽니다 (5MB 초과).\n{path}", "DevezCode",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            // 이미 다른 파일을 편집 중이고 변경분이 있으면 먼저 확인
            if (IsOpen && _dirty && !ConfirmDiscardOrSave()) return IsOpen;

            _loading = true;
            Editor.Text = File.ReadAllText(path);
            _loading = false;

            _path = path;
            FileNameText.Text = Path.GetFileName(path);
            FilePathText.Text = path;
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

    /// <summary>호스트가 패널을 접은 뒤 호출 — 내용을 비운다.</summary>
    public void Reset()
    {
        Visibility = Visibility.Collapsed;
        _path = null;
        _loading = true;
        Editor.Clear();
        _loading = false;
        SetDirty(false);
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SetDirty(true);
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        DirtyDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        // 저장·되돌리기는 수정사항이 있을 때만 활성화
        SaveBtn.IsEnabled = dirty;
        UndoBtn.IsEnabled = dirty;
    }

    private bool Save()
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

    private void UndoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.CanUndo) Editor.Undo();
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e) => Save();

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => RequestClose();

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

    /// <summary>편집기 닫기 요청 — 변경분 확인 후 호스트에 닫기 위임.</summary>
    public void RequestClose()
    {
        if (!IsOpen) return;
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
