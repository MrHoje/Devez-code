using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>스킬/에이전트 마크다운 파일 편집기. 파일을 읽어 편집 후 저장.
/// 다른 팝업과 동일한 라운드 클립 + Opacity 페이드 chrome. Ctrl+S 저장, Esc 닫기.</summary>
public partial class FileEditorWindow : Window
{
    private readonly string _path;

    /// <summary>이 편집기에서 저장이 이뤄졌는지 — 닫힌 뒤 호출측이 목록 갱신에 사용.</summary>
    public bool Saved { get; private set; }

    public FileEditorWindow(string title, string filePath)
    {
        InitializeComponent();
        _path = filePath;
        TitleText.Text = title;
        PathText.Text = filePath;

        Opacity = 0;
        SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += (_, _) => ApplyRoundedClip();
        ContentRendered += async (_, _) => { AnimateOpen(); await LoadAsync(); };
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        SaveBtn.IsEnabled = false;
        StatusText.Text = "불러오는 중…";
        var content = await ClaudeExtensionService.ReadAsync(_path);
        EditorBox.Text = content;
        StatusText.Text = File.Exists(_path) ? "" : "새 파일 — 저장 시 생성됩니다.";
        SaveBtn.IsEnabled = true;
        EditorBox.Focus();
        EditorBox.CaretIndex = 0;
    }

    private void ApplyRoundedClip()
    {
        double w = ContentClip.ActualWidth, h = ContentClip.ActualHeight;
        if (w <= 0 || h <= 0) return;
        ContentClip.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 13, 13);
    }

    private void AnimateOpen()
    {
        var dur = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async System.Threading.Tasks.Task SaveAsync()
    {
        SaveBtn.IsEnabled = false;
        StatusText.Text = "저장 중…";
        var ok = await ClaudeExtensionService.WriteAsync(_path, EditorBox.Text);
        if (ok)
        {
            Saved = true;
            StatusText.Text = "저장됨";
            Close();
        }
        else
        {
            StatusText.Text = "저장 실패 — 파일 권한을 확인하세요.";
            SaveBtn.IsEnabled = true;
        }
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        { e.Handled = true; await SaveAsync(); }
    }
}
