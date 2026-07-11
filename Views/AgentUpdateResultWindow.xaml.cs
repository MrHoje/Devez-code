using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DevezCode.Services;

namespace DevezCode.Views;

/// <summary>에이전트 업데이트 최종 결과를 모아 보여주는 중앙 모달.
/// 진행 모달(<see cref="AgentUpdateWindow"/>)과 동일한 비주얼(그림자·라운드·bot 헤더).
/// 성공(버전 변경)은 체크, 실패는 X + '로그 열기' 링크로 표시한다.
/// Owner 가 있으면 <see cref="WindowCenter"/> 로 Owner 중앙에 정렬(없으면 화면 중앙).</summary>
public partial class AgentUpdateResultWindow : Window
{
    public AgentUpdateResultWindow(IReadOnlyList<AgentUpdateResult> results)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowCenter.CenterOverOwner(this);
        foreach (var r in results) AddRow(r);
    }

    /// <summary>결과 1행: [아이콘][본문]. 본문은 정규식 줄바꿈으로 의미 단위 정리(→ 앞에서 개행),
    /// 실패 행의 '로그 열기' 링크는 텍스트와 같은 줄에 두면 마지막 줄과 겹쳐 보여 아랫줄에 별도 표시.</summary>
    private void AddRow(AgentUpdateResult r)
    {
        bool ok = r.Status == AgentUpdateStatus.Updated;

        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Path
        {
            Style = (Style)FindResource("LucideIcon"),
            Data = (Geometry)FindResource(ok ? "IconCircleCheck" : "IconX"),
            Width = 14, Height = 14,
            Margin = new Thickness(0, 3, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Stroke = (Brush)FindResource(ok ? "PrimaryBrush" : "DangerBrush"),
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        // 본문(텍스트 + 실패 시 아랫줄 링크)을 세로로 쌓는다.
        var body = new StackPanel();
        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        // 전역 TextBlock 스타일(Ideal+ClearType)을 그대로 타도록 스타일 미지정 (텍스트렌더링규칙).
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = (double)FindResource("Fs13"),
            Foreground = (Brush)FindResource("TextBrush"),
            Text = BreakLines(ok
                ? $"{r.DisplayName}: {r.Before} → {r.After}"
                : $"{r.DisplayName}: 업데이트 실패 (종료 코드 {r.ExitCode})"),
        };
        body.Children.Add(text);

        if (!ok)
        {
            var link = new TextBlock
            {
                Text = "로그 열기",
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                FontSize = (double)FindResource("Fs12"),
                Foreground = (Brush)FindResource("PrimaryBrush"),
            };
            link.MouseEnter += (_, _) => link.Opacity = 0.75;
            link.MouseLeave += (_, _) => link.Opacity = 1.0;
            link.MouseLeftButtonUp += (_, _) => AgentUpdateService.OpenDetachedLog(r.Id);
            body.Children.Add(link);
        }

        ResultList.Children.Add(row);
    }

    /// <summary>긴 결과 한 줄이 임의 지점에서 감기지 않도록 정규식으로 의미 단위 줄바꿈.
    /// "이름: 구버전 → 신버전" 을 화살표 앞에서 개행해 구/신버전을 한 줄씩 보여준다.</summary>
    private static string BreakLines(string s)
        => System.Text.RegularExpressions.Regex.Replace(s, @"\s*→\s*", "\n→ ");

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
}
