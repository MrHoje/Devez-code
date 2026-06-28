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

/// <summary>초기화권 1개 표시 정보(만료일 + 잔여기간).</summary>
public sealed class ResetCreditRowVM
{
    /// <summary>"7월 12일 03:34"</summary>
    public required string ExpiryText { get; init; }
    /// <summary>"14일 남음" / "오늘 만료" / "만료됨"</summary>
    public required string DaysLeftText { get; init; }
    /// <summary>3일 이내 만료 or 이미 만료 → 경고 강조.</summary>
    public bool IsExpiringSoon { get; init; }

    public System.Windows.Visibility SeparatorVisibility
        => string.IsNullOrEmpty(DaysLeftText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}

/// <summary>사용량 팝오버의 provider 카드(Claude/Codex/OpenCode Go).</summary>
public sealed class UsageCardVM
{
    public required string Name { get; init; }
    public string? Plan { get; init; }
    public required string IconPath { get; init; }        // pack:// 이미지 경로
    public IReadOnlyList<UsageRowVM> Rows { get; init; } = System.Array.Empty<UsageRowVM>();

    /// <summary>초기화권 만료일/잔여기간 행. codex 만 채워짐.</summary>
    public IReadOnlyList<ResetCreditRowVM> ResetCredits { get; init; } = System.Array.Empty<ResetCreditRowVM>();

    public System.Windows.Visibility PlanVisibility
        => string.IsNullOrEmpty(Plan) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility ResetCreditsVisibility
        => ResetCredits.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    /// <summary>"초기화 3회 가능" 형태 헤더.</summary>
    public string ResetHeaderText => ResetCredits.Count > 0 ? $"초기화 {ResetCredits.Count}회 가능" : "";
}
