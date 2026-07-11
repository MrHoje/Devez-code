namespace DevezCode.Services;

/// <summary>
/// 같은 초기화 구간에서 한 번만 관측된 큰 사용률 급락을 보류한다.
/// 공급자의 실제 조기 초기화도 가능하므로 연속된 두 응답이 급락을 확인하면 새 값을 채택한다.
/// </summary>
internal sealed class UsageDropGuard
{
    private const double SuspiciousDropPercent = 10;
    private static readonly TimeSpan ResetTolerance = TimeSpan.FromSeconds(5);

    private IReadOnlyDictionary<string, WindowSample>? _accepted;
    private IReadOnlyDictionary<string, WindowSample>? _pending;

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
            if (!accepted.TryGetValue(name, out var previous)
                || !SameActiveWindow(previous.ResetsAt, current.ResetsAt, now))
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
            if (!currentNames.Contains(drop.Name)
                || !candidate.TryGetValue(drop.Name, out var current)
                || !SameActiveWindow(drop.ResetsAt, current.ResetsAt, now)
                || drop.PreviousPercent - current.UsedPercent < SuspiciousDropPercent)
                return false;
        }
        return true;
    }

    private static bool SameActiveWindow(
        DateTimeOffset? previousReset,
        DateTimeOffset? currentReset,
        DateTimeOffset now)
        => previousReset is { } previous
           && currentReset is { } current
           && previous > now
           && current > now
           && (previous - current).Duration() <= ResetTolerance;

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
