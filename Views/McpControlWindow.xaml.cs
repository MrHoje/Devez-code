using System.Windows;
using System.Windows.Input;

namespace DevezCode.Views;

/// <summary>MCP 컨트롤러(라이브 대시보드)의 borderless 호스트. ESC 로 닫기 + 닫힘 통지.</summary>
public partial class McpControlWindow : Window
{
    public McpControlWindow()
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
