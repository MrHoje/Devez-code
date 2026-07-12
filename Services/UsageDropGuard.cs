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
    }

    public bool ShouldPublish(
        IEnumerable<WindowSample> samples,
        DateTimeOffset now,
        out string? reason)
    {
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
            Accept(candidate);
            reason = null;
            return true;
        }

        if (_pending != null && ConfirmsPendingDrop(_accepted, _pending, candidate, now))
        {
            Accept(candidate);
            reason = $"confirmed after consecutive responses: {FormatDrops(suspicious)}";
            return true;
        }

        _pending = candidate;
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
                drops.Add(new Drop(name, previous.UsedPercent, current.UsedPercent, current.ResetsAt));
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
        DateTimeOffset? ResetsAt);
}
