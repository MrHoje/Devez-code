using System.Windows.Media;

namespace DevezCode.Models;

/// <summary>사용량 팝오버의 한 행(윈도우): 라벨(5시간/주간/월간) + 사용률 막대 + 초기화 안내.
/// 표시용으로 이미 가공된 값(막대 px·색·텍스트)을 담는다 — 뷰는 바인딩만 한다.</summary>
public sealed class UsageRowVM
{
    public required string Label { get; init; }
    public required string PercentText { get; init; }   // "62%" 또는 "--"
    public string ResetText { get; init; } = "";          // "↻ 1시간23분 후" / "↻ 6월 27일 09:00"
    /// <summary>예상 한도 소진 시각 ("약 2시간 34분 후"). 설정 꺼졌거나 예측 불가면 빈 문자열.</summary>
    public string EstimateText { get; init; } = "";
    /// <summary>예상 소진 색상. 여유=SuccessBrush(초록), 부족=DangerBrush(빨강). null 이면 기본 표시 안 함.</summary>
    public Brush? EstimateBrush { get; init; }
    public double BarWidth { get; init; }                  // 트랙(고정폭) 안 채움 너비 px
    public Brush? BarBrush { get; init; }
    public bool ShowBar { get; init; } = true;             // false=막대 트랙 자체 숨김(DeepSeek 잔액 행)
    public System.Windows.Visibility BarVisibility
        => ShowBar ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Thickness PercentMargin { get; init; } = new(10, 0, 0, 0); // %/잔액 텍스트 좌측 여백
    public System.Windows.Visibility ResetVisibility
        => string.IsNullOrEmpty(ResetText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    /// <summary>예상 소진 표시 — 설정 on 이고 예측값이 있을 때만 보임.</summary>
    public System.Windows.Visibility EstimateVisibility
        => string.IsNullOrEmpty(EstimateText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
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
    /// <summary>카드 데이터의 실제 마지막 정상 수집 시각.</summary>
    public System.DateTimeOffset? CapturedAt { get; init; }
    /// <summary>최근 갱신 실패/장기 미갱신 상태.</summary>
    public bool IsStale { get; init; }
    public IReadOnlyList<UsageRowVM> Rows { get; init; } = System.Array.Empty<UsageRowVM>();

    /// <summary>초기화권 만료일/잔여기간 행. codex 만 채워짐.</summary>
    public IReadOnlyList<ResetCreditRowVM> ResetCredits { get; init; } = System.Array.Empty<ResetCreditRowVM>();

    public System.Windows.Visibility PlanVisibility
        => string.IsNullOrEmpty(Plan) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility ResetCreditsVisibility
        => ResetCredits.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    /// <summary>"초기화 3회 가능" 형태 헤더.</summary>
    public string ResetHeaderText => ResetCredits.Count > 0 ? $"초기화 {ResetCredits.Count}회 가능" : "";

    /// <summary>초기화권 [사용] 버튼 활성 여부(크레딧 있음 &amp; 쿨다운 아님). MainWindow가 주입.</summary>
    public bool CanConsumeResetCredit { get; init; }
    /// <summary>[사용] 버튼 호버 툴팁(쿨다운 안내). 쿨다운 아니면 null(툴팁 미표시).</summary>
    public string? ResetCreditTooltip { get; init; }
    /// <summary>[사용] 버튼 노출 — 초기화권이 있을 때만.</summary>
    public System.Windows.Visibility ResetUseButtonVisibility
        => ResetCredits.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}
