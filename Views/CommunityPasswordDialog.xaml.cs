using System.Windows;
using System.Windows.Input;

namespace DevezCode.Views;

public partial class CommunityPasswordDialog : Window
{
    private readonly bool _showPlainText;

    private CommunityPasswordDialog(Window owner, string title, string message, string okLabel, bool showPlainText)
    {
        InitializeComponent();
        _showPlainText = showPlainText;
        Owner = owner;
        Title = title;
        HeaderTitleText.Text = title;
        MessageText.Text = message;
        OkText.Text = okLabel;
        PasswordInput.Visibility = showPlainText ? Visibility.Collapsed : Visibility.Visible;
        VisibleTextInput.Visibility = showPlainText ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => ActiveInput.Focus();
    }

    public static string? Ask(Window owner, string title, string message, string okLabel)
    {
        var dialog = new CommunityPasswordDialog(owner, title, message, okLabel, showPlainText: false);
        return dialog.ShowDialog() == true ? dialog.InputValue : null;
    }

    public static string? AskVisibleText(Window owner, string title, string message, string okLabel)
    {
        var dialog = new CommunityPasswordDialog(owner, title, message, okLabel, showPlainText: true);
        return dialog.ShowDialog() == true ? dialog.InputValue : null;
    }

    private System.Windows.Controls.Control ActiveInput => _showPlainText ? VisibleTextInput : PasswordInput;
    private string InputValue => _showPlainText ? VisibleTextInput.Text : PasswordInput.Password;

    private void TryAccept()
    {
        if (InputValue.Length < 4)
        {
            HintText.Visibility = Visibility.Visible;
            ActiveInput.Focus();
            return;
        }
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryAccept();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else
        {
            HintText.Visibility = Visibility.Collapsed;
        }
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e) => TryAccept();
    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
