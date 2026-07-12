using System.Net.Http;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Grok Build 사용량 폴링.
/// CLI와 동일: <c>GET .../v1/billing?format=credits</c>
/// → creditUsagePercent(주간) + billingPeriodEnd.</summary>
public sealed class GrokUsageService : IDisposable
{
    // credits: 주간 크레딧 계정용(creditUsagePercent). usage: 월간 구독 계정용(used/monthlyLimit).
    // 통합/월간 계정은 credits 응답에 사용량 필드가 없으므로 usage 로 폴백한다.
    private const string BillingCreditsUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    private const string BillingUsageUrl = "https://cli-chat-proxy.grok.com/v1/billing";
    private const int PollMs = 3 * 60 * 1000;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private System.Threading.Timer? _poll;
    private string? _memoryAccess;
    private string? _memoryRefresh;
    private DateTimeOffset? _memoryExpires;

    public event Action<ProviderUsage>? Updated;

    public void Start() => _poll = new System.Threading.Timer(_ => _ = PollAsync(), null, 0, PollMs);

    public void RefreshNow() => _ = PollAsync();

    public static bool IsConnected() => GrokCredentialStore.IsConnected();

    public void Disconnect()
    {
        _memoryAccess = null;
        _memoryRefresh = null;
        _memoryExpires = null;
        GrokCredentialStore.Disconnect();
    }

    private async Task<string?> EnsureAccessTokenAsync()
    {
        if (!GrokCredentialStore.IsConnected()) return null;

        var c = GrokCredentialStore.Resolve();
        var access = _memoryAccess ?? c?.AccessToken;
        var refresh = _memoryRefresh ?? c?.RefreshToken;
        var exp = _memoryExpires ?? c?.ExpiresAt;

        if (string.IsNullOrEmpty(access) && string.IsNullOrEmpty(refresh))
            return null;

        bool needRefresh = string.IsNullOrEmpty(access)
            || exp is null
            || exp.Value - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(5);

        if (!needRefresh) return access;

        if (string.IsNullOrEmpty(refresh)) return access; // 만료됐지만 refresh 없음 — 그대로 시도

        try
        {
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh!,
                ["client_id"] = GrokCredentialStore.OAuthClientId,
            });
            using var res = await _http.PostAsync(GrokCredentialStore.OAuthTokenUrl, body).ConfigureAwait(false);
            var bodyText = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                // refresh 폐기(invalid_grant) — 만료 토큰으로 폴링해도 401만 반복
                if (bodyText.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                    || (int)res.StatusCode is 400 or 401 or 403)
                {
                    _memoryAccess = null;
                    _memoryRefresh = null;
                    _memoryExpires = null;
                    GrokCredentialStore.Clear();
                    return null;
                }
                return access;
            }

            using var doc = JsonDocument.Parse(bodyText);
            var r = doc.RootElement;
            var newAccess = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(newAccess)) return access;

            var newRefresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : refresh;
            var expIn = r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
                ? ei.GetInt64() : 3600;
            var newExp = DateTimeOffset.UtcNow.AddSeconds(expIn);

            _memoryAccess = newAccess;
            _memoryRefresh = newRefresh;
            _memoryExpires = newExp;

            GrokCredentialStore.Save(newAccess!, newRefresh, newExp);
            return newAccess;
        }
        catch
        {
            return access;
        }
    }

    private static HttpRequestMessage NewReq(string url, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("x-xai-token-auth", "xai-grok-cli");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "xai-grok-cli");
        req.Headers.TryAddWithoutValidation("x-grok-client-version", "0.2.93");
        return req;
    }

    private async Task PollAsync()
    {
        try
        {
            var token = await EnsureAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                // 토큰 없거나 refresh 폐기 — 사이드바에 안내 표시
                Updated?.Invoke(new ProviderUsage
                {
                    Provider = "grok",
                    Error = "로그인 필요 — 설정에서 Grok 로그인",
                });
                return;
            }

            var (authFailed, usage) = await FetchUsageAsync(token).ConfigureAwait(false);

            // 401/403 → 강제 refresh 1회 재시도
            if (authFailed)
            {
                _memoryExpires = DateTimeOffset.UtcNow.AddMinutes(-1); // 강제 만료
                var retry = await EnsureAccessTokenAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(retry) && retry != token)
                    (authFailed, usage) = await FetchUsageAsync(retry).ConfigureAwait(false);

                if (authFailed)
                {
                    Updated?.Invoke(new ProviderUsage
                    {
                        Provider = "grok",
                        Error = "세션 만료 — 설정에서 Grok 재로그인",
                    });
                    return;
                }
            }

            if (usage != null) Updated?.Invoke(usage);
        }
        catch { }
    }

    /// <summary>credits 조회 → 사용량 필드 없으면 usage 로 폴백.
    /// authFailed=true 는 401/403(재로그인 필요), usage=null 은 데이터 없음/일시 오류.</summary>
    private async Task<(bool authFailed, ProviderUsage? usage)> FetchUsageAsync(string token)
    {
        using var res = await _http.SendAsync(NewReq(BillingCreditsUrl, token)).ConfigureAwait(false);
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return (true, null);
        if (res.IsSuccessStatusCode)
        {
            var fromCredits = await ParseAsync(res).ConfigureAwait(false);
            if (fromCredits != null) return (false, fromCredits);
        }

        // credits 응답에 사용량 필드 없음(월간/통합 구독 계정) → 기본 billing 재조회
        using var res2 = await _http.SendAsync(NewReq(BillingUsageUrl, token)).ConfigureAwait(false);
        if (res2.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return (true, null);
        if (!res2.IsSuccessStatusCode) return (false, null);
        return (false, await ParseAsync(res2).ConfigureAwait(false));
    }

    private static async Task<ProviderUsage?> ParseAsync(HttpResponseMessage res)
    {
        await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
            return null;

        // 주간 창: creditUsagePercent 가 있으면 그 값, 없더라도 주간 currentPeriod 이 있으면 0%
        // (통합/월간 계정은 이 필드가 생략됨 = proto 기본값 0 → CLI usage 의 "Weekly limit: 0%" 와 동일).
        bool hasCredit = config.TryGetProperty("creditUsagePercent", out var cup) && cup.ValueKind == JsonValueKind.Number;
        if (hasCredit || HasWeeklyPeriod(config))
        {
            var pct = hasCredit ? Math.Clamp(cup.GetDouble(), 0, 100) : 0;
            return new ProviderUsage
            {
                Provider = "grok",
                Weekly = new UsageWindow { UsedPercent = pct, ResetsAt = ReadPeriodEnd(config) },
            };
        }

        double? used = ReadVal(config, "used");
        double? limit = ReadVal(config, "monthlyLimit");
        if (used is not double u || limit is not double lim || lim <= 0) return null;
        DateTimeOffset? pe = null;
        if (config.TryGetProperty("billingPeriodEnd", out var peEl) && peEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(peEl.GetString(), out var dt))
            pe = dt;
        return new ProviderUsage
        {
            Provider = "grok",
            Monthly = new UsageWindow { UsedPercent = Math.Clamp(u / lim * 100.0, 0, 100), ResetsAt = pe },
        };
    }

    /// <summary>currentPeriod 이 주간 창인지. type 이 없으면 currentPeriod 존재만으로 주간 취급.</summary>
    private static bool HasWeeklyPeriod(JsonElement config)
    {
        if (!config.TryGetProperty("currentPeriod", out var cp) || cp.ValueKind != JsonValueKind.Object)
            return false;
        if (cp.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString()?.Contains("WEEKLY", StringComparison.OrdinalIgnoreCase) ?? false;
        return true;
    }

    private static DateTimeOffset? ReadPeriodEnd(JsonElement config)
    {
        if (config.TryGetProperty("billingPeriodEnd", out var pe) && pe.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(pe.GetString(), out var dt))
            return dt;
        if (config.TryGetProperty("currentPeriod", out var cp) && cp.ValueKind == JsonValueKind.Object
            && cp.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(end.GetString(), out var dt2))
            return dt2;
        return null;
    }

    private static double? ReadVal(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("val", out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return null;
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _http.Dispose();
    }
}
