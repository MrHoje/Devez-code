using System.IO;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>claude statusLine 훅(statusline-hook.ps1)이 떨군 rate_limits JSON 을 감시해
/// 최신 계정 사용량을 알린다. 여러 세션이 같은 파일을 덮어쓰므로 같은 reset window 에서
/// 더 낮은 사용률은 이전 세션의 낡은 스냅샷으로 보고 무시한다.</summary>
public sealed class StatusLineService : IDisposable
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude");
    private static string FilePath => Path.Combine(Dir, "ratelimit.json");

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _poll;
    private long _lastWriteTicks = -1; // 마지막으로 emit 한 파일 쓰기시각 — 중복 emit 방지
    private RateLimitSnapshot? _lastSnapshot;

    public event Action<RateLimitSnapshot>? SnapshotUpdated;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(Dir, "ratelimit.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Emit();
            _watcher.Created += (_, _) => Emit();
        }
        catch { /* 감시 실패해도 폴링이 커버 */ }

        // 폴링 폴백: 같은 폴더의 statusline-cache 파일이 수초마다 대량 갱신돼 watcher 내부 버퍼가
        // 넘치면 ratelimit.json 이벤트가 유실된다(콜드스타트 첫 스냅샷 누락 → 영영 미표시).
        // 2초 주기로 쓰기시각을 확인해 변했을 때만 emit — watcher 실패와 무관하게 항상 복원.
        _poll = new System.Threading.Timer(_ => Emit(), null, 0, 2000);
    }

    private void Emit()
    {
        try
        {
            // 쓰기시각이 그대로면 파싱 스킵(폴링이 매번 파일을 안 깨물도록).
            if (File.Exists(FilePath))
            {
                var ticks = File.GetLastWriteTimeUtc(FilePath).Ticks;
                if (ticks == _lastWriteTicks) return;
                _lastWriteTicks = ticks;
            }
        }
        catch { }
        var snap = MergeWithLastSnapshot(TryRead());
        if (snap != null) SnapshotUpdated?.Invoke(snap);
    }

    private RateLimitSnapshot? MergeWithLastSnapshot(RateLimitSnapshot? next)
    {
        if (next == null) return null;
        var last = _lastSnapshot;
        if (last == null) { _lastSnapshot = next; return next; }

        var merged = new RateLimitSnapshot
        {
            FiveHourPercent = KeepNonRegressing(last.FiveHourPercent, last.FiveHourResetsAt,
                                                next.FiveHourPercent, next.FiveHourResetsAt),
            SevenDayPercent = KeepNonRegressing(last.SevenDayPercent, last.SevenDayResetsAt,
                                               next.SevenDayPercent, next.SevenDayResetsAt),
            FiveHourResetsAt = next.FiveHourResetsAt,
            SevenDayResetsAt = next.SevenDayResetsAt,
        };
        _lastSnapshot = merged;
        return merged;
    }

    private static double? KeepNonRegressing(double? oldPct, DateTimeOffset? oldReset,
                                             double? newPct, DateTimeOffset? newReset)
    {
        if (newPct is not double n) return oldPct;
        if (oldPct is double o && oldReset == newReset && n < o) return o;
        return n;
    }

    /// <summary>파일을 읽어 스냅샷으로 파싱. 쓰기 경합 시 짧게 재시도. 실패/없음이면 null.</summary>
    private RateLimitSnapshot? TryRead()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(fs);
                if (!doc.RootElement.TryGetProperty("rate_limits", out var rl)) return null;
                return new RateLimitSnapshot
                {
                    FiveHourPercent  = ReadPct(rl, "five_hour"),
                    SevenDayPercent  = ReadPct(rl, "seven_day"),
                    FiveHourResetsAt = ReadReset(rl, "five_hour"),
                    SevenDayResetsAt = ReadReset(rl, "seven_day"),
                };
            }
            catch (IOException)   { System.Threading.Thread.Sleep(30); } // 훅이 쓰는 중 — 재시도
            catch (JsonException) { System.Threading.Thread.Sleep(30); } // 일부만 기록됨 — 재시도
            catch { return null; }
        }
        return null;
    }

    private static double? ReadPct(JsonElement rl, string key)
        => rl.TryGetProperty(key, out var o)
           && o.TryGetProperty("used_percentage", out var p)
           && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble() : null;

    /// <summary>resets_at (unix 초)을 시각으로. 없으면 null.</summary>
    private static DateTimeOffset? ReadReset(JsonElement rl, string key)
        => rl.TryGetProperty(key, out var o)
           && o.TryGetProperty("resets_at", out var r)
           && r.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()) : null;

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _poll?.Dispose();
        _poll = null;
    }
}
