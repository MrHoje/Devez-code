using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Anthropic OAuth usage API(<c>GET /api/oauth/usage</c>)를 주기적으로 폴링해 계정 사용량을
/// 알린다. statusLine 훅과 달리 claude 세션이 떠 있지 않아도 값을 갱신할 수 있다.
/// 토큰은 <c>~/.claude/.credentials.json</c> 에서 매 폴링마다 읽어(claude 가 갱신해도 따라감) 메모리에서만 사용한다.
///
/// 주의: 이 엔드포인트는 <c>User-Agent: claude-code/&lt;ver&gt;</c> 헤더가 없으면 공격적으로 rate limit(영구 429)
/// 되는 별도 버킷에 떨어진다. 헤더를 넣으면 180초 간격까지 안전(claude-code 이슈 #31021/#31637 참고).
/// 429 면 이번 주기를 건너뛰고 직전 값을 유지한다.</summary>
public sealed class UsageApiService : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    // claude CLI 로 위장한 User-Agent. 없으면 영구 429. claude 업데이트 시 갱신.
    private const string UserAgent = "claude-code/2.1.186";
    private const int PollMs = 3 * 60 * 1000; // 3분 — UA 포함 시 안전한 최소 간격

    // 토큰 후보 경로: claude CLI 파일(있으면 최신) → DevezCode 자체 로그인(ClaudeLoginWindow) 파일.
    private static IEnumerable<string> CredentialPaths()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        yield return ClaudeCredentialStore.StorePath;
    }

    // statusline.js 가 live rate_limits 가 없을 때 폴백으로 읽는 파일.
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude");
    private static string FallbackPath => Path.Combine(Dir, "api-usage.json");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private System.Threading.Timer? _poll;

    public event Action<RateLimitSnapshot>? SnapshotUpdated;

    public void Start()
    {
        // 즉시 1회 + 이후 3분 주기. 폴링은 백그라운드 스레드에서 비동기로 돈다.
        _poll = new System.Threading.Timer(_ => _ = PollAsync(), null, 0, PollMs);
    }

    /// <summary>지금 즉시 1회 폴링(로그인 직후 갱신용).</summary>
    public void RefreshNow() => _ = PollAsync();

    /// <summary>Claude OAuth 토큰이 있어 연결된 상태인지(claude CLI 또는 DevezCode 자체 로그인).</summary>
    public static bool IsConnected() => ReadToken() != null;

    // DevezCode 자체 로그인(ClaudeLoginWindow) 토큰의 refresh 용 — claude CLI 와 동일 값.
    private const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string OAuthTokenUrl = "https://console.anthropic.com/v1/oauth/token";

    /// <summary>DevezCode 자체 스토어 토큰이 만료 임박(5분)이면 refresh 로 갱신한다.
    /// claude CLI 파일은 claude 가 관리하므로 건드리지 않는다. 실패 시 기존 토큰 유지(다음에 재로그인 유도).</summary>
    private async Task EnsureFreshClaudeAsync()
    {
        if (ClaudeCredentialStore.Read() is not { } c) return;
        if (string.IsNullOrEmpty(c.refresh)) return;
        if (c.expiresMs - DateTimeOffset.Now.ToUnixTimeMilliseconds() > 5 * 60 * 1000) return; // 아직 충분
        try
        {
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = c.refresh!,
                ["client_id"] = OAuthClientId,
            });
            using var res = await _http.PostAsync(OAuthTokenUrl, body).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return; // revoked/실패 — 기존 유지
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
            var r = doc.RootElement;
            var access = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return;
            var refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : c.refresh; // 회전 시 교체
            var exp = r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetInt64() : 3600;
            ClaudeCredentialStore.Save(access!, refresh, DateTimeOffset.Now.ToUnixTimeMilliseconds() + exp * 1000);
        }
        catch { /* 일시 오류 — 기존 토큰 유지 */ }
    }

    private async Task PollAsync()
    {
        try
        {
            await EnsureFreshClaudeAsync().ConfigureAwait(false);
            var token = ReadToken();
            if (token == null) return; // 자격증명/토큰 없음 — 직전 값 유지

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            // 429/401/5xx 등 — 이번 주기 건너뛰고 직전 값 유지(다음 주기 3분 뒤 재시도).
            if (res.StatusCode == HttpStatusCode.TooManyRequests || !res.IsSuccessStatusCode) return;

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
            WriteFallback(snap); // statusline.js 폴백용
            SnapshotUpdated?.Invoke(snap);
        }
        catch { /* 일시 오류 — 직전 값 유지 */ }
    }

    /// <summary>statusline.js 가 live rate_limits 부재 시 읽을 폴백 파일을 쓴다.
    /// claude statusLine JSON 의 rate_limits 와 같은 모양(resets_at = unix 초).</summary>
    private static void WriteFallback(RateLimitSnapshot snap)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                WriteWindow(w, "five_hour", snap.FiveHourPercent, snap.FiveHourResetsAt);
                WriteWindow(w, "seven_day", snap.SevenDayPercent, snap.SevenDayResetsAt);
                w.WriteEndObject();
            }
            File.WriteAllBytes(FallbackPath, ms.ToArray());
        }
        catch { }
    }

    private static void WriteWindow(Utf8JsonWriter w, string key, double? pct, DateTimeOffset? reset)
    {
        if (pct is not double p) return;
        w.WriteStartObject(key);
        w.WriteNumber("used_percentage", p);
        if (reset is DateTimeOffset r) w.WriteNumber("resets_at", r.ToUnixTimeSeconds());
        w.WriteEndObject();
    }

    /// <summary>credentials.json 에서 OAuth 액세스 토큰 추출(느슨한 매칭). 후보 경로를 순서대로 시도.</summary>
    private static string? ReadToken()
    {
        foreach (var path in CredentialPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
        }
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
