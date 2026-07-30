using System.Windows;
using System.Windows.Input;

namespace DevezCode.Views;

/// <summary>설정창 — 화면을 꽉 채우는 창(Slack 스타일).
/// 테두리는 Window 가 담당하고, 실제 콘텐츠는 UserControl(SettingsDialog)에 위임한다.
/// UserControl 의 CloseRequested 가 오면 Window 를 닫는다.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SettingsView.CloseRequested += (_, _) => Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SettingsView.TryCloseWithConfirm();
        }
    }
}
