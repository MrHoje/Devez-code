using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>codex/openai 사용량을 폴링한다. opencode 의 <c>auth.json</c> OAuth 토큰으로
/// <c>GET chatgpt.com/backend-api/wham/usage</c> 를 호출(@slkiser/opencode-quota 의 openai.js 와 동일 방식).
/// primary_window=5h, secondary_window=주간. 토큰 만료/오류 시 직전 값 유지.</summary>
public sealed class CodexUsageService : IDisposable
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const int PollMs = 3 * 60 * 1000;
    private static readonly string[] AuthKeys = { "openai", "codex", "chatgpt", "opencode" };

    // opencode auth.json 후보 경로(런타임 디렉터리).
    private static IEnumerable<string> AuthPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(home, ".local", "share", "opencode", "auth.json");
        yield return Path.Combine(appData, "opencode", "auth.json");
    }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private System.Threading.Timer? _poll;

    public event Action<ProviderUsage>? Updated;

    public void Start() => _poll = new System.Threading.Timer(_ => _ = PollAsync(), null, 0, PollMs);

    private async Task PollAsync()
    {
        try
        {
            var (token, accountId, expired) = ReadAuth();
            if (token == null) return;            // 미연결 — 직전 값 유지
            if (expired) { Updated?.Invoke(new ProviderUsage { Provider = "codex", Error = "토큰 만료 — 재로그인 필요" }); return; }

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("User-Agent", "OpenCode-Quota-Toast/1.0");
            if (accountId != null) req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);

            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return; // 401/429/5xx — 직전 값 유지

            await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            UsageWindow? primary = null, weekly = null;
            if (root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                primary = ReadWindow(rl, "primary_window");
                weekly  = ReadWindow(rl, "secondary_window");
            }
            if (primary == null && weekly == null) return;

            string? plan = root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String
                ? DerivePlanLabel(pt.GetString()) : null;
            Updated?.Invoke(new ProviderUsage { Provider = "codex", Primary = primary, Weekly = weekly, PlanLabel = plan });
        }
        catch { /* 일시 오류 — 직전 값 유지 */ }
    }

    /// <summary>window 객체에서 used_percent + reset_at(epoch초)/reset_after_seconds 를 읽어 UsageWindow 로.</summary>
    private static UsageWindow? ReadWindow(JsonElement rl, string key)
    {
        if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percent", out var up) || up.ValueKind != JsonValueKind.Number) return null;
        DateTimeOffset? reset = null;
        if (w.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number && ra.GetDouble() > 0)
            reset = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64());
        else if (w.TryGetProperty("reset_after_seconds", out var rs) && rs.ValueKind == JsonValueKind.Number && rs.GetDouble() > 0)
            reset = DateTimeOffset.Now.AddSeconds(rs.GetDouble());
        return new UsageWindow { UsedPercent = up.GetDouble(), ResetsAt = reset };
    }

    private static string DerivePlanLabel(string? planType)
    {
        var raw = (planType ?? "").ToLowerInvariant();
        if (raw.Contains("pro"))  return "OpenAI (Pro)";
        if (raw.Contains("plus")) return "OpenAI (Plus)";
        return string.IsNullOrEmpty(planType) ? "OpenAI" : $"OpenAI ({planType})";
    }

    /// <summary>auth.json 에서 (accessToken, accountId, 만료여부). 없으면 (null,null,false).</summary>
    private static (string? token, string? accountId, bool expired) ReadAuth()
    {
        foreach (var path in AuthPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;
                foreach (var key in AuthKeys)
                {
                    if (!root.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Object) continue;
                    if (!e.TryGetProperty("type", out var t) || t.GetString() != "oauth") continue;
                    if (!e.TryGetProperty("access", out var a) || a.ValueKind != JsonValueKind.String) continue;
                    var token = a.GetString()?.Trim();
                    if (string.IsNullOrEmpty(token)) continue;

                    bool expired = e.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.Number
                                   && ex.GetDouble() > 0 && ex.GetDouble() < DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    var accountId = AccountIdFromJwt(token)
                                    ?? (e.TryGetProperty("accountId", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() : null);
                    return (token, accountId, expired);
                }
            }
            catch { }
        }
        return (null, null, false);
    }

    /// <summary>JWT payload 의 "https://api.openai.com/auth".chatgpt_account_id 추출.</summary>
    private static string? AccountIdFromJwt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth)
                   && auth.TryGetProperty("chatgpt_account_id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _http.Dispose();
    }
}
