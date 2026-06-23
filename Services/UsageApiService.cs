using System.IO;
using System.Net.Http;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Anthropic OAuth usage API(<c>GET /api/oauth/usage</c>)를 주기적으로 폴링해 계정 사용량을
/// 알린다. statusLine 훅과 달리 claude 세션이 떠 있지 않아도 값을 갱신할 수 있다.
/// 토큰은 <c>~/.claude/.credentials.json</c> 에서 매 폴링마다 읽어(claude 가 갱신해도 따라감) 메모리에서만 사용한다.</summary>
public sealed class UsageApiService : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const int PollMs = 5 * 60 * 1000; // 5분 — rate limit 회피(스펙 §9: 5분 미만 폴링 금지)

    private static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private System.Threading.Timer? _poll;
    private RateLimitSnapshot? _last;

    public event Action<RateLimitSnapshot>? SnapshotUpdated;

    public void Start()
    {
        // 즉시 1회 + 이후 5분 주기. 폴링은 백그라운드 스레드에서 비동기로 돈다.
        _poll = new System.Threading.Timer(_ => _ = PollAsync(), null, 0, PollMs);
    }

    private async Task PollAsync()
    {
        try
        {
            var token = ReadToken();
            if (token == null) return; // 자격증명/토큰 없음 — 직전 값 유지

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return; // 401/5xx 등 — 직전 값 유지

            await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            var snap = new RateLimitSnapshot
            {
                FiveHourPercent  = ReadPct(root, "five_hour"),
                SevenDayPercent  = ReadPct(root, "seven_day"),
                FiveHourResetsAt = ReadReset(root, "five_hour"),
                SevenDayResetsAt = ReadReset(root, "seven_day"),
            };
            if (!snap.HasData) return;
            _last = snap;
            SnapshotUpdated?.Invoke(snap);
        }
        catch { /* 일시 오류 — 직전 값 유지 */ }
    }

    /// <summary>credentials.json 에서 OAuth 액세스 토큰 추출(느슨한 매칭). 실패/없음이면 null.</summary>
    private static string? ReadToken()
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return null;
            using var fs = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (root.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.TryGetProperty("accessToken", out var at)
                && at.ValueKind == JsonValueKind.String)
                return at.GetString();
            foreach (var key in new[] { "accessToken", "access_token", "token" })
                if (root.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>윈도우의 utilization(사용률 %)을 읽는다. 없으면 null.</summary>
    private static double? ReadPct(JsonElement root, string key)
        => root.TryGetProperty(key, out var w)
           && w.ValueKind == JsonValueKind.Object
           && w.TryGetProperty("utilization", out var u)
           && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble() : null;

    /// <summary>resets_at(ISO 8601 문자열)을 시각으로. 없거나 파싱 실패면 null.</summary>
    private static DateTimeOffset? ReadReset(JsonElement root, string key)
        => root.TryGetProperty(key, out var w)
           && w.ValueKind == JsonValueKind.Object
           && w.TryGetProperty("resets_at", out var r)
           && r.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(r.GetString(), out var dt)
            ? dt : null;

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _http.Dispose();
    }
}
