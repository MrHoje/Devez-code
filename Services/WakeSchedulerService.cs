using System.Globalization;
using System.Windows.Threading;
using System.Text.Json.Serialization;

namespace DevezCode.Services;

/// <summary>A persisted weekly wake request.</summary>
public sealed class WakeScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "claude";
    public List<DayOfWeek> Weekdays { get; set; } = new();
    public string Time { get; set; } = "09:00";
    public bool Enabled { get; set; } = true;
    public string LastOccurrenceKey { get; set; } = "";
    public string LastResult { get; set; } = "";
    [JsonIgnore]
    public string ProviderDisplayName => string.Equals(Provider, "codex", StringComparison.OrdinalIgnoreCase)
        ? "Codex" : "Claude";

    [JsonIgnore]
    public string ScheduleSummary
    {
        get
        {
            var days = Weekdays
                .Distinct()
                .OrderBy(day => day == DayOfWeek.Sunday ? 7 : (int)day)
                .Select(DayLabel);
            return $"{string.Join(" · ", days)}  {Time}";
        }
    }

    [JsonIgnore]
    public string StatusDisplay => string.IsNullOrWhiteSpace(LastResult) ? "아직 실행 기록이 없습니다" : LastResult;

    private static string DayLabel(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "월",
        DayOfWeek.Tuesday => "화",
        DayOfWeek.Wednesday => "수",
        DayOfWeek.Thursday => "목",
        DayOfWeek.Friday => "금",
        DayOfWeek.Saturday => "토",
        DayOfWeek.Sunday => "일",
        _ => "",
    };

    internal WakeScheduleEntry Clone() => new()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
        Provider = string.IsNullOrWhiteSpace(Provider) ? "claude" : Provider.Trim().ToLowerInvariant(),
        Weekdays = Weekdays?.Distinct().ToList() ?? new List<DayOfWeek>(),
        Time = Time,
        Enabled = Enabled,
        LastOccurrenceKey = LastOccurrenceKey ?? "",
        LastResult = LastResult ?? "",
    };
}

/// <summary>The result reported by the MainWindow dispatch adapter.</summary>
public sealed class WakeDispatchResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    public static WakeDispatchResult Succeeded() => new() { Success = true };
    public static WakeDispatchResult Failed(string? error = null) => new() { Success = false, Error = error };
}

/// <summary>
/// MainWindow-owned, UI-dispatcher weekly wake loop. It deliberately does not know how
/// to find or write to a terminal; that operation is supplied by the callback.
/// </summary>
public sealed class WakeSchedulerService : IDisposable
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);
    private readonly DispatcherTimer _timer;
    private readonly Func<WakeScheduleEntry, CancellationToken, Task<WakeDispatchResult>> _dispatch;
    private CancellationTokenSource _lifetime = new();
    private int _revision;
    private bool _checking;
    private bool _disposed;

    public WakeSchedulerService(Func<WakeScheduleEntry, CancellationToken, Task<WakeDispatchResult>> dispatch)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer.Start();
        _ = CheckAsync();
    }

    public void Stop()
    {
        if (_disposed) return;
        _timer.Stop();
        Interlocked.Increment(ref _revision);
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
    }

    /// <summary>Call after adding, removing, or editing a persisted schedule.</summary>
    public void NotifySchedulesChanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _revision);
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
    }

    private void OnTick(object? sender, EventArgs e) => _ = CheckAsync();

    private async Task CheckAsync()
    {
        if (_disposed || _checking) return;
        _checking = true;
        try
        {
            var token = _lifetime.Token;
            int revision = Volatile.Read(ref _revision);
            DateTime now = DateTime.Now;
            var enabledAgents = new HashSet<string>(
                AgentRegistry.GetEnabledAndInstalled().Select(a => a.Id),
                StringComparer.OrdinalIgnoreCase);

            foreach (var schedule in SettingsService.LoadWakeSchedules())
            {
                if (token.IsCancellationRequested || revision != Volatile.Read(ref _revision)) break;
                if (!schedule.Enabled || !enabledAgents.Contains(schedule.Provider)) continue;
                if (!TryGetDueOccurrence(schedule, now, out var occurrenceKey)) continue;
                if (!SettingsService.TryClaimWakeSchedule(schedule.Id, occurrenceKey)) continue;
                if (token.IsCancellationRequested || revision != Volatile.Read(ref _revision)) break;

                WakeDispatchResult result;
                try
                {
                    result = await _dispatch(schedule, token).ConfigureAwait(true)
                        ?? WakeDispatchResult.Failed("Dispatch returned no result.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { result = WakeDispatchResult.Failed(ex.Message); }

                // A schedule edit/stop wins over an in-flight callback's stale result.
                if (!token.IsCancellationRequested && revision == Volatile.Read(ref _revision))
                {
                    string status = result.Success ? "success" : "failed: " + (result.Error ?? "unknown error");
                    SettingsService.TrySaveWakeResult(schedule.Id, occurrenceKey, status);
                }
            }
        }
        finally { _checking = false; }
    }

    private static bool TryGetDueOccurrence(WakeScheduleEntry schedule, DateTime now, out string key)
    {
        key = "";
        if (schedule.Weekdays is null || !schedule.Weekdays.Contains(now.DayOfWeek)) return false;
        if (!TimeSpan.TryParseExact(schedule.Time, @"hh\:mm", CultureInfo.InvariantCulture, out var time)) return false;
        var occurrence = now.Date + time;
        if (now < occurrence || now >= occurrence + Grace) return false;
        key = occurrence.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
