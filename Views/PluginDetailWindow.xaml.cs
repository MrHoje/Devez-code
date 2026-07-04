using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>Discover 카드 클릭 시 뜨는 플러그인 상세 팝업.
/// 좌측: 이름·마켓·설명 + 설치 버튼, 우측: 설치 실행 결과 출력.
/// 다른 팝업과 동일하게 라운드 클립 + Opacity 페이드로 열린다.</summary>
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

        Opacity = 0;
        SizeChanged += (_, _) => ApplyRoundedClip();
        Loaded += (_, _) => ApplyRoundedClip();
        ContentRendered += (_, _) => AnimateOpen();
    }

    // ClipToBounds 는 사각 경계로만 클립 → 자식 사각 모서리가 라운드 코너 위로 삐져나온다.
    // 콘텐츠 Border 를 둥근 RectangleGeometry 로 직접 클립.
    private void ApplyRoundedClip()
    {
        double w = ContentRoot.ActualWidth, h = ContentRoot.ActualHeight;
        if (w <= 0 || h <= 0) return;
        ContentRoot.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 14, 14);
    }

    private void AnimateOpen()
    {
        var dur = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
    }

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;
        ShowOutput($"install · {_plugin.Name}", "설치 중… (잠시 걸릴 수 있습니다)");
        try
        {
            var result = await ClaudePluginService.InstallAsync(_plugin.Id);
            ShowOutput($"install · {_plugin.Name}", string.IsNullOrWhiteSpace(result) ? "완료." : result);
        }
        catch (Exception ex)
        {
            ShowOutput($"install · {_plugin.Name}", "설치 실패: " + ex.Message);
        }
        finally
        {
            InstallBtn.IsEnabled = true;
        }
    }

    private void ShowOutput(string title, string body)
    {
        OutputText.Text = string.IsNullOrEmpty(title) ? body : $"{title}\n\n{body}";
        OutputPlaceholder.Visibility = Visibility.Collapsed;
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
