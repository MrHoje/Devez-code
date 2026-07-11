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

    /// <summary>Fable 모델 주간 전용 한도 사용률(%). API 의 limits[weekly_scoped scope=Fable]. 없으면 null.</summary>
    public double? FableWeeklyPercent { get; init; }
    /// <summary>Fable 주간 한도 초기화 시각. 없으면 null.</summary>
    public DateTimeOffset? FableWeeklyResetsAt { get; init; }

    /// <summary>표시할 값이 하나라도 있는지.</summary>
    public bool HasData => FiveHourPercent.HasValue || SevenDayPercent.HasValue || FableWeeklyPercent.HasValue;

    /// <summary>두 스냅샷을 병합한다. next는 더 나중에 수집된 응답이므로 해당 응답에
    /// 포함된 윈도우를 그대로 채택한다. 공급자가 예정 시각 전에 한도를 초기화하면
    /// 사용률과 reset 시각이 모두 감소할 수 있으므로 단조 증가를 가정하지 않는다.</summary>
    public static RateLimitSnapshot? Merge(RateLimitSnapshot? last, RateLimitSnapshot? next)
    {
        if (next == null) return last;
        if (last == null) return next;
        return new RateLimitSnapshot
        {
            CapturedAt = next.CapturedAt,
            FiveHourPercent = next.FiveHourPercent ?? last.FiveHourPercent,
            FiveHourResetsAt = next.FiveHourPercent.HasValue ? next.FiveHourResetsAt : last.FiveHourResetsAt,
            SevenDayPercent = next.SevenDayPercent ?? last.SevenDayPercent,
            SevenDayResetsAt = next.SevenDayPercent.HasValue ? next.SevenDayResetsAt : last.SevenDayResetsAt,
            FableWeeklyPercent = next.FableWeeklyPercent ?? last.FableWeeklyPercent,
            FableWeeklyResetsAt = next.FableWeeklyPercent.HasValue ? next.FableWeeklyResetsAt : last.FableWeeklyResetsAt,
        };
    }

    /// <summary>호환용 윈도우 선택기. 호출자가 전달한 new 값이 최신 응답이므로
    /// reset 방향이나 사용률 증감을 추측하지 않고 그대로 채택한다.</summary>
    public static (double? pct, DateTimeOffset? reset) PickWindow(
        double? oldPct, DateTimeOffset? oldReset, double? newPct, DateTimeOffset? newReset)
        => newPct.HasValue ? (newPct, newReset) : (oldPct, oldReset);
}
