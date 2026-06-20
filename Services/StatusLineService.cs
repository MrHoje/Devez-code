using System.IO;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>claude statusLine 훅(statusline-hook.ps1)이 떨군 rate_limits JSON 을 감시해
/// 최신 계정 사용량을 알린다. rate_limits 는 계정 전역값이라 단일 파일(ratelimit.json)에
/// 마지막으로 떨어진 것이 곧 최신 — roomId 매칭이 필요 없다.</summary>
public sealed class StatusLineService : IDisposable
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude");
    private static string FilePath => Path.Combine(Dir, "ratelimit.json");

    private FileSystemWatcher? _watcher;

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
            Emit(); // 시작 시 기존 값 1회 반영
        }
        catch { /* 감시 실패해도 앱은 계속 — 푸터만 비어 있음 */ }
    }

    private void Emit()
    {
        var snap = TryRead();
        if (snap != null) SnapshotUpdated?.Invoke(snap);
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
    }
}
