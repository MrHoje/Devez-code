using System.Windows;
using System.Windows.Input;

namespace DevezCode.Views;

/// <summary>MCP 서버 관리 오버레이의 borderless 호스트. ESC 로 닫기 + 닫힘 통지.</summary>
public partial class McpManagerWindow : Window
{
    public McpManagerWindow()
    {
        InitializeComponent();
        McpView.CloseRequested += (_, _) => Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            McpView.TryClose();
            e.Handled = true;
        }
    }
}
