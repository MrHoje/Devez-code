using System.Windows.Media;

namespace DevezCode.Models;

/// <summary>사용량 팝오버의 한 행(윈도우): 라벨(5시간/주간/월간) + 사용률 막대 + 초기화 안내.
/// 표시용으로 이미 가공된 값(막대 px·색·텍스트)을 담는다 — 뷰는 바인딩만 한다.</summary>
public sealed class UsageRowVM
{
    public required string Label { get; init; }
    public required string PercentText { get; init; }   // "62%" 또는 "--"
    public string ResetText { get; init; } = "";          // "↻ 1시간23분 후" / "↻ 6월 27일 09:00"
    public double BarWidth { get; init; }                  // 트랙(고정폭) 안 채움 너비 px
    public Brush? BarBrush { get; init; }
    public System.Windows.Visibility ResetVisibility
        => string.IsNullOrEmpty(ResetText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}

/// <summary>사용량 팝오버의 provider 카드(Claude/Codex/OpenCode Go).</summary>
public sealed class UsageCardVM
{
    public required string Name { get; init; }
    public string? Plan { get; init; }
    public required string IconPath { get; init; }        // pack:// 이미지 경로
    public IReadOnlyList<UsageRowVM> Rows { get; init; } = System.Array.Empty<UsageRowVM>();

    public System.Windows.Visibility PlanVisibility
        => string.IsNullOrEmpty(Plan) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}
