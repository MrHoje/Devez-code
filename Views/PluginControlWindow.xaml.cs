using System.Windows;
using System.Windows.Input;

namespace DevezCode.Views;

/// <summary>플러그인 컨트롤러의 borderless 호스트. ESC 로 닫기(마켓플레이스 오버레이가 열려 있으면 그것부터).</summary>
public partial class PluginControlWindow : Window
{
    public PluginControlWindow()
    {
        InitializeComponent();
        PluginView.CloseRequested += (_, _) => Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            PluginView.TryClose();
            e.Handled = true;
        }
    }
}
