using System.Windows;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>Discover 카드 클릭 시 뜨는 플러그인 상세 팝업.
/// 좌측: 이름·마켓·설명 + 설치 버튼, 우측: 설치 실행 결과 출력.</summary>
public partial class PluginDetailWindow : Window
{
    private readonly ClaudeAvailablePlugin _plugin;

    public PluginDetailWindow(ClaudeAvailablePlugin plugin)
    {
        InitializeComponent();
        _plugin = plugin;

        NameText.Text = plugin.Name;
        MarketText.Text = plugin.Marketplace;
        InstallCountText.Text = plugin.InstallCountText;
        DescText.Text = string.IsNullOrWhiteSpace(plugin.Description) ? "설명이 없습니다." : plugin.Description;

        // 이미 설치된 경우 설치 버튼 대신 '설치됨' 표시.
        if (plugin.IsInstalled)
        {
            InstallBtn.Visibility = Visibility.Collapsed;
            InstalledChip.Visibility = Visibility.Visible;
        }
    }

    private void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        ClaudePluginService.SpawnInstall(_plugin.Id);
        OutputPlaceholder.Visibility = Visibility.Collapsed;
        OutputText.Text = $"install · {_plugin.Name}\n\n'{_plugin.Id}' 설치를 별도 콘솔 창에서 진행합니다.\n" +
                          "완료되면 컨트롤러의 [새로고침] 으로 목록을 갱신하세요.";
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
