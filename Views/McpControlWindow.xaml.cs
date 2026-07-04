using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DevezCode.Views;

/// <summary>MCP 컨트롤러(라이브 대시보드)의 borderless 호스트. ESC 로 닫기 + 닫힘 통지.</summary>
public partial class McpControlWindow : Window
{
    public McpControlWindow()
    {
        InitializeComponent();
        McpView.CloseRequested += (_, _) => Close();
        // 콘텐츠 UserControl 을 둥근 RectangleGeometry 로 클립 — ClipToBounds 만으로는 라운드 코너가 안 됨.
        McpView.SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += (_, _) => ApplyRoundedClip();
    }

    private void ApplyRoundedClip()
    {
        double w = McpView.ActualWidth, h = McpView.ActualHeight;
        if (w <= 0 || h <= 0) return;
        McpView.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 13, 13);
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
