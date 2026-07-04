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

    /// <summary>이 팝업에서 설치가 완료됐는지 — 닫힌 뒤 호출측이 목록에서 제거하는 데 사용.</summary>
    public bool Installed { get; private set; }

    public PluginDetailWindow(ClaudeAvailablePlugin plugin)
    {
        InitializeComponent();
        _plugin = plugin;

        NameText.Text = plugin.Name;
        MarketText.Text = plugin.Marketplace;
        VersionText.Text = plugin.VersionText;
        InstallCountText.Text = plugin.InstallCountText;
        DescText.Text = string.IsNullOrWhiteSpace(plugin.Description) ? "설명이 없습니다." : plugin.Description;
        RepoLink.Visibility = plugin.HasSourceUrl ? Visibility.Visible : Visibility.Collapsed;

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

    // 바깥 Border(CornerRadius=14)는 그대로 두고 자식(내용 Grid)만 라운드 클립 → 다른 팝업과 동일하게
    // 보더 모서리가 선명하다. (Border 자체를 클립하면 보더 스트로크가 재클립돼 모서리가 흐려짐)
    private void ApplyRoundedClip()
    {
        double w = ContentClip.ActualWidth, h = ContentClip.ActualHeight;
        if (w <= 0 || h <= 0) return;
        ContentClip.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 13, 13);
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
            // 설치 후에는 설치 버튼 비활성화 → '설치됨' 표시로 전환.
            Installed = true;
            InstallBtn.Visibility = Visibility.Collapsed;
            InstalledChip.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowOutput($"install · {_plugin.Name}", "설치 실패: " + ex.Message);
            InstallBtn.IsEnabled = true;   // 실패 시 재시도 가능하게 복구
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

    private void RepoLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_plugin.SourceUrl)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_plugin.SourceUrl) { UseShellExecute = true }); }
        catch { }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
