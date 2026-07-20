namespace DevezCode.Services;

/// <summary>
/// 이전에 채택한 윈도우가 아직 끝나지 않았는데 관측된 큰 사용률 급락을 보류한다.
/// 같은 reset 구간의 단발 급락뿐 아니라 reset_at 이 바뀐(윈도우 교체 주장) 급락도,
/// 서버가 같은 계정에 두 사용량 레코드를 갖고 간헐적으로 다른 쪽을 반환하는 플립일 수
/// 있으므로 연속된 두 응답이 같은 급락을 확인할 때만 채택한다. 공급자의 실제 조기
/// 초기화는 최대 한 폴링 주기만 지연된다. 이전 윈도우의 reset 이 이미 경과했다면
/// 정상 윈도우 전환이므로 즉시 채택한다.
/// </summary>
internal sealed class UsageDropGuard
{
    private const double SuspiciousDropPercent = 10;
    private static readonly TimeSpan ResetTolerance = TimeSpan.FromSeconds(5);

    private IReadOnlyDictionary<string, WindowSample>? _accepted;
    private IReadOnlyDictionary<string, WindowSample>? _pending;
    // 초기화권 소비 등으로 정당한 급락이 예고된 마감 시각(UTC ticks, 0=없음).
    // 폴링 스레드와 버스트 스레드가 함께 읽으므로 Interlocked 로 다룬다.
    private long _expectDropUntilTicks;

    /// <summary>초기화권 소비처럼 클라이언트가 직접 초기화를 일으켜 급락이 확실히 예정된 경우,
    /// 마감 시각까지 관측되는 첫 급락을 연속 확인 없이 즉시 채택하게 한다. 서버 반영이
    /// 수 분 지연될 수 있어(옛 값이 먼저 도착) 단순 기준값 비우기로는 즉시 반영이 안 된다.</summary>
    public void ExpectDrop(DateTimeOffset until)
        => Interlocked.Exchange(ref _expectDropUntilTicks, until.UtcTicks);

    /// <summary>예고된 급락을 아직 기다리는 중인지 — 소비 직후 버스트 재폴링의 종료 조건.</summary>
    public bool IsExpectingDrop(DateTimeOffset now)
        => Interlocked.Read(ref _expectDropUntilTicks) >= now.UtcTicks;

    private void ClearExpectDrop()
        => Interlocked.Exchange(ref _expectDropUntilTicks, 0);

    /// <summary>재시작 직후 기준값이 없어 첫 응답(가짜 급락 포함)을 무조건 채택하는 구멍을
    /// 막기 위해, 직전 실행이 저장한 스냅샷을 기준값으로 놓는다. 이미 실제 응답을 채택한
    /// 뒤에는 무시한다. reset 이 경과한 기준값은 자연히 의심 판정에서 제외된다.</summary>
    public void Seed(IEnumerable<WindowSample> samples)
    {
        if (_accepted != null) return;
        var seeded = samples.ToDictionary(sample => sample.Name, StringComparer.Ordinal);
        if (seeded.Count == 0) return;
        _accepted = seeded;
    }

    /// <summary>인증(계정)이 바뀌면 이전 계정의 기준값으로 새 계정의 정상값을 보류하지
    /// 않도록 상태를 비운다.</summary>
    public void Reset()
    {
        _accepted = null;
        _pending = null;
        ClearExpectDrop();
    }

    public bool ShouldPublish(
        IEnumerable<WindowSample> samples,
        DateTimeOffset now,
        out string? reason)
        => ShouldPublish(samples, now, out reason, out _);

    /// <summary>급락과 함께 reset 시각도 바뀌면 실제 초기화 가능성이 높으므로 호출자가
    /// 정규 폴링을 기다리지 않고 한 번 빠르게 재확인할 수 있도록 알린다.</summary>
    public bool ShouldPublish(
        IEnumerable<WindowSample> samples,
        DateTimeOffset now,
        out string? reason,
        out bool shouldRetrySoon)
    {
        shouldRetrySoon = false;
        var candidate = samples.ToDictionary(sample => sample.Name, StringComparer.Ordinal);
        if (_accepted == null)
        {
            Accept(candidate);
            reason = null;
            return true;
        }

        var suspicious = FindSuspiciousDrops(_accepted, candidate, now);
        if (suspicious.Count == 0)
        {
            // 급락 없이 윈도우 교체(reset 변경)만 보여도 예고된 초기화가 반영된 것이므로
            // 기대를 해제해 버스트 재폴링이 조기 종료되게 한다(사용률이 원래 낮았던 경우).
            if (IsExpectingDrop(now) && AnyWindowReplaced(_accepted, candidate))
                ClearExpectDrop();
            Accept(candidate);
            reason = null;
            return true;
        }

        if (IsExpectingDrop(now))
        {
            // 클라이언트가 직접 일으킨 초기화(초기화권 소비)가 예고된 시간창 안의 급락은
            // 서버 플립이 아니라 예정된 결과이므로 연속 확인 없이 즉시 채택한다.
            ClearExpectDrop();
            Accept(candidate);
            reason = $"accepted expected drop (reset-credit consume): {FormatDrops(suspicious)}";
            return true;
        }

        if (_pending != null && ConfirmsPendingDrop(_accepted, _pending, candidate, now))
        {
            Accept(candidate);
            reason = $"confirmed after consecutive responses: {FormatDrops(suspicious)}";
            return true;
        }

        var hadPending = _pending != null;
        _pending = candidate;
        shouldRetrySoon = !hadPending && suspicious.Any(drop => drop.ResetChanged);
        reason = $"awaiting confirmation: {FormatDrops(suspicious)}";
        return false;
    }

    private void Accept(IReadOnlyDictionary<string, WindowSample> candidate)
    {
        var accepted = _accepted == null
            ? new Dictionary<string, WindowSample>(StringComparer.Ordinal)
            : new Dictionary<string, WindowSample>(_accepted, StringComparer.Ordinal);
        foreach (var (name, sample) in candidate)
            accepted[name] = sample;
        _accepted = accepted;
        _pending = null;
    }

    private static List<Drop> FindSuspiciousDrops(
        IReadOnlyDictionary<string, WindowSample> accepted,
        IReadOnlyDictionary<string, WindowSample> candidate,
        DateTimeOffset now)
    {
        var drops = new List<Drop>();
        foreach (var (name, current) in candidate)
        {
            // 이전 윈도우의 reset 이 없거나 이미 지났으면 정상 전환 — 의심하지 않는다.
            if (!accepted.TryGetValue(name, out var previous)
                || previous.ResetsAt is not { } previousReset
                || previousReset <= now)
                continue;

            var amount = previous.UsedPercent - current.UsedPercent;
            if (amount >= SuspiciousDropPercent)
                drops.Add(new Drop(
                    name,
                    previous.UsedPercent,
                    current.UsedPercent,
                    current.ResetsAt,
                    !SameWindowClaim(previous.ResetsAt, current.ResetsAt)));
        }
        return drops;
    }

    private static bool ConfirmsPendingDrop(
        IReadOnlyDictionary<string, WindowSample> accepted,
        IReadOnlyDictionary<string, WindowSample> pending,
        IReadOnlyDictionary<string, WindowSample> candidate,
        DateTimeOffset now)
    {
        var pendingDrops = FindSuspiciousDrops(accepted, pending, now);
        var currentDrops = FindSuspiciousDrops(accepted, candidate, now);
        if (pendingDrops.Count == 0 || currentDrops.Count != pendingDrops.Count)
            return false;

        var currentNames = currentDrops.Select(drop => drop.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var drop in pendingDrops)
        {
            // 두 응답이 같은 윈도우(같은 reset_at 주장)로 급락을 유지해야 실제 초기화로 본다.
            if (!currentNames.Contains(drop.Name)
                || !candidate.TryGetValue(drop.Name, out var current)
                || !SameWindowClaim(drop.ResetsAt, current.ResetsAt)
                || drop.PreviousPercent - current.UsedPercent < SuspiciousDropPercent)
                return false;
        }
        return true;
    }

    /// <summary>기준값과 이름이 같은 윈도우 중 reset 주장이 달라진 것이 있는지 —
    /// 예고된 초기화가 급락 없이(원래 저사용) 반영된 경우를 감지한다.</summary>
    private static bool AnyWindowReplaced(
        IReadOnlyDictionary<string, WindowSample> accepted,
        IReadOnlyDictionary<string, WindowSample> candidate)
    {
        foreach (var (name, current) in candidate)
            if (accepted.TryGetValue(name, out var previous)
                && !SameWindowClaim(previous.ResetsAt, current.ResetsAt))
                return true;
        return false;
    }

    private static bool SameWindowClaim(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return (a.Value - b.Value).Duration() <= ResetTolerance;
    }

    private static string FormatDrops(IEnumerable<Drop> drops)
        => string.Join(", ", drops.Select(drop =>
            $"{drop.Name}={drop.PreviousPercent:F0}->{drop.CurrentPercent:F0} reset={drop.ResetsAt:O}"));

    internal readonly record struct WindowSample(
        string Name,
        double UsedPercent,
        DateTimeOffset? ResetsAt);

    private readonly record struct Drop(
        string Name,
        double PreviousPercent,
        double CurrentPercent,
        DateTimeOffset? ResetsAt,
        bool ResetChanged);
}
