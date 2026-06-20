namespace DevezCode.Models;

/// <summary>Claude 계정 rate limit 사용량 (statusLine JSON 의 rate_limits 에서 추출).
/// 세션별이 아닌 계정 전역값이라 어느 세션이 떨구든 동일 — 마지막 갱신 = 최신.</summary>
public class RateLimitSnapshot
{
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    /// <summary>5시간 한도 사용률(%). 데이터 없으면 null.</summary>
    public double? FiveHourPercent  { get; init; }
    /// <summary>7일(주간) 한도 사용률(%). 데이터 없으면 null.</summary>
    public double? SevenDayPercent  { get; init; }

    /// <summary>5시간 한도 초기화 시각. 없으면 null.</summary>
    public DateTimeOffset? FiveHourResetsAt { get; init; }
    /// <summary>주간 한도 초기화 시각. 없으면 null.</summary>
    public DateTimeOffset? SevenDayResetsAt { get; init; }

    /// <summary>표시할 값이 하나라도 있는지.</summary>
    public bool HasData => FiveHourPercent.HasValue || SevenDayPercent.HasValue;
}
