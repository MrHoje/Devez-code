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
    public event EventHandler? Interacted;

    private string? _path;
    private bool _loading;
    private readonly Action<string> _themeChangedHandler;
    public FileEditorView()
    {
        InitializeComponent();
        _themeChangedHandler = _ => Dispatcher.BeginInvoke(new Action(RefreshHighlighting));
        Loaded += (_, _) => App.ThemeChanged += _themeChangedHandler;
        Unloaded += (_, _) => App.ThemeChanged -= _themeChangedHandler;
        // WPF 네이티브 에디터라 클릭이 이미 CenterArea 로 버블링되지만, 인터페이스 일관성 + 명시적 포커스 통지.
        PreviewMouseDown += (_, _) => Interacted?.Invoke(this, EventArgs.Empty);
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
            ".json" or ".jsonc" => JsonDefinition(),
            ".go" => mgr.GetDefinition("C#"),
            ".rs" => mgr.GetDefinition("C#"),
            ".swift" => mgr.GetDefinition("C#"),
            ".kt" or ".kts" => mgr.GetDefinition("C#"),
            ".rb" => mgr.GetDefinition("C#"),
            ".fs" => mgr.GetDefinition("F#"),
            _ => null,
        };
    }

    // AvalonEdit 내장 JSON 정의 없음 → 키/문자열/숫자/bool 구분 가능한 전용 정의 임베드.
    private const string JsonXshd = @"<?xml version='1.0'?>
<SyntaxDefinition name='JSON' xmlns='http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008'>
  <Color name='Key'    foreground='#2563EB' />
  <Color name='String' foreground='#0A7C3E' />
  <Color name='Number' foreground='#B06000' />
  <Color name='Bool'   foreground='#9333EA' />
  <Color name='Punct'  foreground='#64748B' />
  <RuleSet>
    <Rule color='Key'>""[^""\\]*(?:\\.[^""\\]*)*""(?=\s*:)</Rule>
    <Span color='String' multiline='false'>
      <Begin>""</Begin>
      <End>""</End>
      <RuleSet><Span begin='\\' end='.' /></RuleSet>
    </Span>
    <Keywords color='Bool'>
      <Word>true</Word><Word>false</Word><Word>null</Word>
    </Keywords>
    <Rule color='Number'>\b[-+]?[0-9]+(\.[0-9]+)?([eE][-+]?[0-9]+)?\b</Rule>
    <Rule color='Punct'>[{}\[\]:,]</Rule>
  </RuleSet>
</SyntaxDefinition>";

    private static IHighlightingDefinition? _jsonDef;
    private static IHighlightingDefinition? JsonDefinition()
    {
        if (_jsonDef != null) return _jsonDef;
        using var sr = new System.IO.StringReader(JsonXshd);
        using var xr = System.Xml.XmlReader.Create(sr);
        _jsonDef = ICSharpCode.AvalonEdit.Highlighting.Xshd.HighlightingLoader.Load(xr, HighlightingManager.Instance);
        return _jsonDef;
    }

    // 하이라이팅 정의별 원본 토큰 색 보존(공유 싱글톤이라 덮어쓰기 누적 방지)
    private static readonly Dictionary<string, Dictionary<string, System.Windows.Media.Color?>> _origColors = new();

    /// <summary>현재 테마 배경이 어두우면 토큰색을 밝게 보정해 가독성 확보. 밝은 테마는 원본 유지.</summary>
    private static IHighlightingDefinition? ThemeHighlighting(IHighlightingDefinition? def)
    {
        if (def == null) return null;
        bool dark = App.CurrentTheme == "dark";

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
        // 흰색 쪽으로 lerp — 순색(순파랑 등)도 확실히 밝아짐. 색조는 cap으로 유지.
        double t = System.Math.Min(0.68, (0.6 - lum) / 0.6);
        byte M(byte v) => (byte)(v + (255 - v) * t);
        return System.Windows.Media.Color.FromRgb(M(c.R), M(c.G), M(c.B));
    }

    /// <summary>테마 변경 시 열려 있는 파일 편집기의 구문 하이라이팅을 새 테마색으로 다시 적용.</summary>
    private void RefreshHighlighting()
    {
        if (string.IsNullOrEmpty(_path)) return;
        var def = GetHighlighting(_path);
        Editor.SyntaxHighlighting = ThemeHighlighting(def);
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
