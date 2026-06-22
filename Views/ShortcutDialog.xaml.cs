using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>프로젝트 "바로가기 추가/수정" 팝업 (devez ShellShortcutDialog 참고).
/// 이름 + 파일 경로(찾아보기) + 관리자 권한 실행 체크박스. 저장 시 결과를 반환한다.</summary>
public partial class ShortcutDialog : Window
{
    /// <summary>다이얼로그 결과 — 등록할 바로가기 정보.</summary>
    public sealed record Result(string Name, string Path, bool RunAsAdmin);

    private ShortcutDialog(string title, string? name, string? path, bool runAsAdmin)
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
        TitleText.Text = title;
        NameBox.Text = name ?? "";
        PathBox.Text = path ?? "";
        RunAsAdminCheck.IsChecked = runAsAdmin;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>새 바로가기 추가. 초기 경로(initialDir)는 파일 다이얼로그 시작 위치로 쓰인다.</summary>
    public static Result? ShowCreate(Window? owner, string? initialDir = null)
    {
        var dlg = new ShortcutDialog("바로가기 추가", null, null, false) { Owner = owner ?? Application.Current.MainWindow };
        dlg._initialDir = initialDir;
        return dlg.ShowDialog() == true ? dlg.BuildResult() : null;
    }

    private string? _initialDir;

    private Result BuildResult()
    {
        var path = PathBox.Text.Trim();
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path))
            name = Path.GetFileName(path);
        return new Result(name, path, RunAsAdminCheck.IsChecked == true);
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "파일 선택" };
        if (!string.IsNullOrEmpty(_initialDir) && Directory.Exists(_initialDir))
            dlg.InitialDirectory = _initialDir;
        if (dlg.ShowDialog(this) != true) return;
        PathBox.Text = dlg.FileName;
        // 이름이 비어 있으면 파일명으로 자동 채움(사용자 편의).
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ShakeWindow();
            PathFrame.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            PathBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void InputBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender == NameBox) NameFrame.BorderBrush = (Brush)FindResource("PrimaryBrush");
        else if (sender == PathBox) PathFrame.BorderBrush = (Brush)FindResource("PrimaryBrush");
    }

    private void InputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender == NameBox) NameFrame.BorderBrush = (Brush)FindResource("LineBrush");
        else if (sender == PathBox) PathFrame.BorderBrush = (Brush)FindResource("LineBrush");
    }

    private void ShakeWindow()
    {
        if (RenderTransform is not TranslateTransform tt) return;
        var shake = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(500) };
        double[] offsets = { 0, -8, 8, -6, 6, -3, 3, 0 };
        for (int i = 0; i < offsets.Length; i++)
            shake.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(
                offsets[i], System.Windows.Media.Animation.KeyTime.FromPercent((double)i / (offsets.Length - 1))));
        tt.BeginAnimation(TranslateTransform.XProperty, shake);
    }
}
