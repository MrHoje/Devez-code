using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>Claude Code 채팅방 생성 — 이름·작업 시작 디렉토리 입력.</summary>
public partial class ClaudeCodeRoomDialog : Window
{
    private ClaudeCodeRoomDialog()
    {
        InitializeComponent();
        DirBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>(이름, 작업 디렉토리) 또는 취소 시 null.</summary>
    public static new (string Title, string Dir)? Show()
    {
        var dialog = new ClaudeCodeRoomDialog();
        if (Application.Current.MainWindow is { IsLoaded: true } owner && owner != dialog)
            dialog.Owner = owner;

        return dialog.ShowDialog() == true
            ? (dialog.NameBox.Text.Trim(), dialog.DirBox.Text.Trim())
            : null;
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "작업 시작 디렉토리 선택" };
        if (Directory.Exists(DirBox.Text.Trim())) picker.InitialDirectory = DirBox.Text.Trim();
        if (picker.ShowDialog(this) == true) DirBox.Text = picker.FolderName;
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e) => TryAccept();

    private void TryAccept()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameFrame.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            ShakeWindow();
            NameBox.Focus();
            return;
        }
        if (!Directory.Exists(DirBox.Text.Trim()))
        {
            DirFrame.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            ShakeWindow();
            DirBox.Focus();
            return;
        }
        DialogResult = true;
    }

    /// <summary>검증 실패 시 창을 좌우로 흔드는 피드백(devez 정합).</summary>
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

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { TryAccept(); e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void Box_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TryFindResource("PrimaryBrush") is not Brush primary) return;
        if (ReferenceEquals(sender, NameBox)) NameFrame.BorderBrush = primary;
        else DirFrame.BorderBrush = primary;
    }

    private void Box_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryFindResource("LineBrush") is not Brush line) return;
        if (ReferenceEquals(sender, NameBox)) NameFrame.BorderBrush = line;
        else DirFrame.BorderBrush = line;
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
