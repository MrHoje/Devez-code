using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>단일 텍스트 입력 다이얼로그(이름 변경 등). devez PromptDialog 디자인 정합.</summary>
public partial class PromptDialog : Window
{
    private readonly bool _requireInput;

    private PromptDialog(string title, string message, string defaultValue, int maxLength,
                         bool requireInput, string okLabel)
    {
        InitializeComponent();
        _requireInput = requireInput;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        OkText.Text = okLabel;

        InputBox.Text = defaultValue;
        if (maxLength > 0) InputBox.MaxLength = maxLength;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    /// <summary>입력값(트림)을 반환. 취소/닫기면 null.</summary>
    public static string? Show(string title, string message, string defaultValue = "",
                               int maxLength = 0, bool requireInput = true, string okLabel = "확인")
    {
        var dialog = new PromptDialog(title, message, defaultValue, maxLength, requireInput, okLabel);

        if (Application.Current.MainWindow is { IsLoaded: true } mw && mw != dialog)
            dialog.Owner = mw;

        return dialog.ShowDialog() == true ? dialog.InputBox.Text.Trim() : null;
    }

    private void TryAccept()
    {
        if (_requireInput && string.IsNullOrWhiteSpace(InputBox.Text))
        {
            HintText.Text = "값을 입력하세요.";
            HintText.Visibility = Visibility.Visible;
            if (TryFindResource("DangerBrush") is Brush danger) InputFrame.BorderBrush = danger;
            InputBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { TryAccept(); e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (HintText.Visibility == Visibility.Visible) HintText.Visibility = Visibility.Collapsed;
    }

    private void InputBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TryFindResource("PrimaryBrush") is Brush primary) InputFrame.BorderBrush = primary;
    }

    private void InputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryFindResource("LineBrush") is Brush line) InputFrame.BorderBrush = line;
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e) => TryAccept();
    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
