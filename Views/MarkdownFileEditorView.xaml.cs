using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DevezCode.Views;

public partial class MarkdownFileEditorView : UserControl, IFileTabEditor, IDisposable
{
    public event EventHandler? CloseRequested;
    public event EventHandler? DirtyChanged;

    private string? _path;
    private string _currentMarkdown = "";
    private bool _dirty;
    private bool _loaded;
    private bool _initStarted;

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
            SetDirty(false);
            _loaded = true;
            MdHost.ApplyTheme(App.CurrentTheme);
            MdHost.SetMarkdown(_currentMarkdown, markClean: true);
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

    public void Dispose()
    {
        App.ThemeChanged -= OnThemeChanged;
        MdHost.Dispose();
    }
}
