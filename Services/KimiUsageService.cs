using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Kimi Code 정액제(구독) 사용량 폴링.
/// CLI /usage 와 동일: <c>GET {base}/usages</c> (managed:kimi-code OAuth 토큰).
/// 응답: {usage:{limit,used|remaining,reset_at}(주간), limits:[{detail, window:{duration,timeUnit}}](5h/7d 등), boosterWallet}.
/// Weekly=usage 요약, Primary=limits 중 최단 창(5h). 표시는 % (공식 CLI 도 절대수 없음).
/// 토큰 refresh 는 kimi CLI 가 백그라운드로 처리 — 여기선 디스크 토큰만 읽는다(KimiCredentialStore).</summary>
public sealed class KimiUsageService : IDisposable
{
    private const int PollMs = 3 * 60 * 1000;

    private static string UsagesUrl
    {
        get
        {
            var b = Environment.GetEnvironmentVariable("KIMI_CODE_BASE_URL");
            var baseUrl = string.IsNullOrWhiteSpace(b) ? "https://api.kimi.com/coding/v1" : b.TrimEnd('/');
            return baseUrl.TrimEnd('/') + "/usages";
        }
    }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private System.Threading.Timer? _poll;

    public event Action<ProviderUsage>? Updated;

    public void Start() => _poll = new System.Threading.Timer(_ => _ = PollAsync(), null, 0, PollMs);
    public void RefreshNow() => _ = PollAsync();
    public static bool IsConnected() => KimiCredentialStore.IsConnected();
    public void Disconnect() => KimiCredentialStore.Disconnect();

    private async Task PollAsync()
    {
        try
        {
            var token = KimiCredentialStore.ReadAccessToken();
            if (string.IsNullOrEmpty(token))
            {
                Updated?.Invoke(new ProviderUsage { Provider = "kimi", Error = "로그인 필요 — kimi login" });
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, UsagesUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "kimi-code");
            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                Updated?.Invoke(new ProviderUsage { Provider = "kimi", Error = "세션 만료 — kimi login" });
                return;
            }
            if (!res.IsSuccessStatusCode) return;

            await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var usage = Parse(doc.RootElement);
            if (usage != null) Updated?.Invoke(usage);
        }
        catch { }
    }

    private static ProviderUsage? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        UsageWindow? weekly = root.TryGetProperty("usage", out var u) ? ReadRow(u) : null;

        UsageWindow? primary = null;
        double primaryMinutes = double.MaxValue;
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var detail = item.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Object ? d : item;
                var row = ReadRow(detail);
                if (row == null) continue;
                double minutes = WindowMinutes(item, detail);
                if (minutes > 0 && minutes < primaryMinutes) { primaryMinutes = minutes; primary = row; }
            }
        }

        if (weekly == null && primary == null) return null;
        return new ProviderUsage { Provider = "kimi", Primary = primary, Weekly = weekly };
    }

    private static UsageWindow? ReadRow(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        double? limit = ReadNum(raw, "limit");
        double? used = ReadNum(raw, "used");
        if (used == null)
        {
            double? remaining = ReadNum(raw, "remaining");
            if (remaining != null && limit != null) used = limit.Value - remaining.Value;
        }
        if (used == null && limit == null) return null;
        double pct = (limit is double l && l > 0) ? Math.Clamp((used ?? 0) / l * 100.0, 0, 100) : 0;
        return new UsageWindow { UsedPercent = pct, ResetsAt = ReadReset(raw) };
    }

    private static double WindowMinutes(JsonElement item, JsonElement detail)
    {
        var window = item.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Object ? w : item;
        double? duration = ReadNum(window, "duration") ?? ReadNum(item, "duration") ?? ReadNum(detail, "duration");
        if (duration is not double dur || dur <= 0) return 0;
        var unitRaw = (GetStr(window, "timeUnit") ?? GetStr(item, "timeUnit") ?? GetStr(detail, "timeUnit") ?? "").ToUpperInvariant();
        if (unitRaw.Contains("HOUR")) return dur * 60;
        if (unitRaw.Contains("DAY")) return dur * 1440;
        if (unitRaw.Contains("MINUTE")) return dur;
        return dur; // 단위 불명 — duration 을 분으로 간주
    }

    private static DateTimeOffset? ReadReset(JsonElement raw)
    {
        foreach (var key in new[] { "reset_at", "resetAt", "reset_time", "resetTime" })
            if (raw.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(v.GetString(), out var dt)) return dt;
        foreach (var key in new[] { "reset_in", "resetIn", "ttl" })
            if (raw.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var secs))
                return DateTimeOffset.UtcNow.AddSeconds(secs);
        return null;
    }

    private static double? ReadNum(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
        return null;
    }

    private static string? GetStr(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _http.Dispose();
    }
}
