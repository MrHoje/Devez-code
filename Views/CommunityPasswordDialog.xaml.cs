using System.Windows;
using System.Windows.Input;

namespace DevezCode.Views;

public partial class CommunityPasswordDialog : Window
{
    private CommunityPasswordDialog(Window owner, string title, string message, string okLabel)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        HeaderTitleText.Text = title;
        MessageText.Text = message;
        OkText.Text = okLabel;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public static string? Ask(Window owner, string title, string message, string okLabel)
    {
        var dialog = new CommunityPasswordDialog(owner, title, message, okLabel);
        return dialog.ShowDialog() == true ? dialog.PasswordInput.Password : null;
    }

    private void TryAccept()
    {
        if (PasswordInput.Password.Length < 4)
        {
            HintText.Visibility = Visibility.Visible;
            PasswordInput.Focus();
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
