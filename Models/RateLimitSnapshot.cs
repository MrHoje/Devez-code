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

    /// <summary>두 스냅샷(예: statusLine 훅 + OAuth API, 또는 여러 세션)을 병합. next 가 null 이면 last 유지.</summary>
    public static RateLimitSnapshot? Merge(RateLimitSnapshot? last, RateLimitSnapshot? next)
    {
        if (next == null) return last;
        if (last == null) return next;
        var (f5p, f5r) = PickWindow(last.FiveHourPercent, last.FiveHourResetsAt,
                                    next.FiveHourPercent, next.FiveHourResetsAt);
        var (w7p, w7r) = PickWindow(last.SevenDayPercent, last.SevenDayResetsAt,
                                    next.SevenDayPercent, next.SevenDayResetsAt);
        var (fwp, fwr) = PickWindow(last.FableWeeklyPercent, last.FableWeeklyResetsAt,
                                    next.FableWeeklyPercent, next.FableWeeklyResetsAt);
        return new RateLimitSnapshot
        {
            FiveHourPercent = f5p, FiveHourResetsAt = f5r,
            SevenDayPercent = w7p, SevenDayResetsAt = w7r,
            FableWeeklyPercent = fwp, FableWeeklyResetsAt = fwr,
        };
    }

    /// <summary>두 소스(statusLine 훅 unix초 · OAuth API ISO)가 같은 윈도우를 살짝 다른 reset 시각으로
    /// 보고하므로 이 오차 이내는 동일 윈도우로 본다. 이 값이 없으면 3분마다 오는 API 폴이 statusLine 의
    /// 미세하게 늦은 reset 에 밀려 통째로 무시돼 Claude 값이 얼어붙었다.</summary>
    private static readonly TimeSpan WindowTolerance = TimeSpan.FromMinutes(5);

    /// <summary>한 윈도우 병합. reset 이 오차(WindowTolerance)를 넘어 앞서면 새 윈도우로 채택,
    /// 뒤처지면 낡은 스냅샷으로 무시. 오차 이내(같은 윈도우)면 방금 들어온(더 최신) 값을 채택하되
    /// reset 은 더 늦은 쪽을 유지해 진행바 꿈틀 방지. 계정 전역값이라 "마지막 갱신 = 최신".</summary>
    public static (double? pct, DateTimeOffset? reset) PickWindow(
        double? oldPct, DateTimeOffset? oldReset, double? newPct, DateTimeOffset? newReset)
    {
        // reset 시각이 이미 지난 값은 죽은 창의 값 — max 누적으로 새 창까지 넘어가 이전 창
        // 최고치에 얼어붙던 "주간 82% 고정" 방지. reset 없는 값은 판정 불가라 유지.
        var now = DateTimeOffset.Now;
        if (oldReset is DateTimeOffset ore && ore + WindowTolerance < now) { oldPct = null; oldReset = null; }
        if (newReset is DateTimeOffset nre && nre + WindowTolerance < now) { newPct = null; newReset = null; }

        if (newPct is not double n) return (oldPct, oldReset); // next 데이터 없음 → 이전 유지
        if (oldPct is not double) return (newPct, newReset);
        if (oldReset is DateTimeOffset orr && newReset is DateTimeOffset nrr)
        {
            var diff = nrr - orr;
            if (diff > WindowTolerance) return (newPct, newReset);   // 새 윈도우 → 채택
            if (diff < -WindowTolerance) return (oldPct, oldReset);  // 낡은 윈도우 → 무시
            // 같은 윈도우: 사용률은 리셋 전까지 단조 증가하므로 더 높은 값을 채택한다.
            // 낮은 값은 (a) 낡은 스냅샷이거나 (b) 두 소스의 반올림 차이(hook 의 정수
            // used_percentage vs OAuth 의 실수 utilization)일 뿐이다. 최신값을 무조건
            // 채택하면 두 소스가 번갈아 도착할 때 표시가 ±1 로 진동한다.
            // 단, 차이가 1%p 이하면 새 값이 낮거나 같을 때만 채택(올림차 진동 방지).
            // statusline(정수 used_percentage)이 API(실수 utilization)보다 같거나
            // 낮은 경향이 있어 자연히 Claude 세션 표시값이 푸터에 반영된다.
            if (Math.Abs(oldPct.Value - n) <= 1 && n <= oldPct.Value)
                return (n, nrr > orr ? nrr : orr);
            return (Math.Max(oldPct.Value, n), nrr > orr ? nrr : orr);
        }
        // 차이가 1%p 이하면 새 값이 낮거나 같을 때만 채택.
        if (Math.Abs(oldPct.Value - n) <= 1 && n <= oldPct.Value)
            return (n, newReset ?? oldReset);
        return (Math.Max(oldPct.Value, n), newReset ?? oldReset);
    }
}
