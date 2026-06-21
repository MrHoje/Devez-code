using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace DevezCode.Views;

/// <summary>3버튼 확인 다이얼로그 결과.</summary>
public enum ConfirmChoice { Primary, Secondary, Cancel }

public partial class ConfirmDialog : Window
{
    private string? _confirmText;
    private ConfirmChoice _choice = ConfirmChoice.Cancel;

    private ConfirmDialog(string title, string message, string okLabel, string iconKey, bool danger, string? confirmText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkText.Text = okLabel;
        _confirmText = confirmText;

        if (confirmText != null)
        {
            ConfirmInputPanel.Visibility = Visibility.Visible;
            ConfirmHintText.Text = $"계속하려면 '{confirmText}'을(를) 입력하세요.";
            OkBtn.IsEnabled = false;
            Loaded += (_, _) => ConfirmInputBox.Focus();
        }
        else
        {
            var lines = message.Count(c => c == '\n') + 1;
            var maxLineLength = message.Split('\n').DefaultIfEmpty("").Max(s => s.Length);
            Width = maxLineLength >= 36 || lines >= 4 ? 560 : 520;
            Height = lines <= 2 ? 250 : lines <= 4 ? 320 : 370;
        }

        KeyDown += OnKeyDown;
    }

    public static bool Show(
        string title,
        string message,
        string okLabel = "확인",
        string iconKey = "IconLogOut",
        bool danger = false,
        string? confirmText = null,
        bool topMost = false)
    {
        var dialog = new ConfirmDialog(title, message, okLabel, iconKey, danger, confirmText);

        if (Application.Current.MainWindow != null
            && Application.Current.MainWindow.IsLoaded
            && Application.Current.MainWindow != dialog)
        {
            dialog.Owner = Application.Current.MainWindow;
        }

        // Topmost 플로팅 창(스티커 메모 등) 위로 뜨도록
        if (topMost) dialog.Topmost = true;

        return dialog.ShowDialog() == true;
    }

    /// <summary>3버튼 확인 다이얼로그. Primary(주 동작)·Secondary(중간 동작)·Cancel 중 하나를 반환한다.</summary>
    public static ConfirmChoice ShowThreeWay(
        string title,
        string message,
        string primaryLabel,
        string secondaryLabel,
        string iconKey = "IconMessageSquare",
        bool danger = false,
        bool topMost = false,
        bool hideCancel = false)
    {
        var dialog = new ConfirmDialog(title, message, primaryLabel, iconKey, danger, null);
        dialog.MiddleText.Text = secondaryLabel;
        dialog.MiddleBtn.Visibility = Visibility.Visible;
        if (hideCancel) dialog.CancelBtn.Visibility = Visibility.Collapsed; // 취소 버튼 숨김(두 선택지만)

        if (Application.Current.MainWindow != null
            && Application.Current.MainWindow.IsLoaded
            && Application.Current.MainWindow != dialog)
        {
            dialog.Owner = Application.Current.MainWindow;
        }

        if (topMost) dialog.Topmost = true;

        dialog.ShowDialog();
        return dialog._choice;
    }

    private void ConfirmInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        OkBtn.IsEnabled = _confirmText != null && ConfirmInputBox.Text == _confirmText;
    }

    public static void Alert(
        string title,
        string message,
        string okLabel = "확인",
        string iconKey = "IconMessageSquare")
    {
        var dialog = new ConfirmDialog(title, message, okLabel, iconKey, false, null);
        dialog.CancelBtn.Visibility = Visibility.Collapsed;

        if (Application.Current.MainWindow != null
            && Application.Current.MainWindow.IsLoaded
            && Application.Current.MainWindow != dialog)
        {
            dialog.Owner = Application.Current.MainWindow;
        }

        dialog.ShowDialog();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_confirmText == null || ConfirmInputBox.Text == _confirmText)
            {
                _choice = ConfirmChoice.Primary;
                DialogResult = true;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e) { _choice = ConfirmChoice.Primary; DialogResult = true; }
    private void MiddleBtn_Click(object sender, RoutedEventArgs e) { _choice = ConfirmChoice.Secondary; DialogResult = true; }
    private void CancelBtn_Click(object sender, RoutedEventArgs e) { _choice = ConfirmChoice.Cancel; DialogResult = false; }
}
