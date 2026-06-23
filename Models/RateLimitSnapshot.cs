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

    /// <summary>두 스냅샷(예: statusLine 훅 + OAuth API, 또는 여러 세션)을 병합. next 가 null 이면 last 유지.</summary>
    public static RateLimitSnapshot? Merge(RateLimitSnapshot? last, RateLimitSnapshot? next)
    {
        if (next == null) return last;
        if (last == null) return next;
        var (f5p, f5r) = PickWindow(last.FiveHourPercent, last.FiveHourResetsAt,
                                    next.FiveHourPercent, next.FiveHourResetsAt);
        var (w7p, w7r) = PickWindow(last.SevenDayPercent, last.SevenDayResetsAt,
                                    next.SevenDayPercent, next.SevenDayResetsAt);
        return new RateLimitSnapshot
        {
            FiveHourPercent = f5p, FiveHourResetsAt = f5r,
            SevenDayPercent = w7p, SevenDayResetsAt = w7r,
        };
    }

    /// <summary>한 윈도우 병합. resets_at 은 새 윈도우에서만 앞으로 이동하므로 next.reset 이 더 과거면
    /// 낡은 스냅샷으로 보고 통째로 무시(이전 pct+reset 유지) — reset 시각이 앞뒤로 튀어 진행바가
    /// 꿈틀대는 것 방지. 같은 윈도우면 사용률 비후퇴(높은 값 유지).</summary>
    public static (double? pct, DateTimeOffset? reset) PickWindow(
        double? oldPct, DateTimeOffset? oldReset, double? newPct, DateTimeOffset? newReset)
    {
        if (newPct is not double n) return (oldPct, oldReset); // next 데이터 없음 → 이전 유지
        if (oldPct is not double o) return (newPct, newReset);
        if (oldReset is DateTimeOffset orr && newReset is DateTimeOffset nrr)
        {
            if (nrr < orr) return (oldPct, oldReset);          // next 가 낡은 윈도우 → 무시
            if (nrr > orr) return (newPct, newReset);          // 새 윈도우 → 채택
            return (n < o ? o : n, newReset);                  // 같은 윈도우 → 사용률 비후퇴
        }
        return (n < o ? o : n, newReset ?? oldReset);          // reset 정보 부족 → 기존 비후퇴 동작
    }
}
