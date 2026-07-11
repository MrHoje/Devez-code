using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>codex/openai 사용량을 폴링한다. Codex CLI 또는 DevezCode/opencode 의 OAuth 토큰으로
/// <c>GET chatgpt.com/backend-api/wham/usage</c> 를 호출한다.
/// primary_window=5h, secondary_window=주간. 토큰 만료/오류 시 직전 값 유지.</summary>
public sealed class CodexUsageService : IDisposable
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string CreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private const int PollMs = 3 * 60 * 1000;
    private static readonly string[] AuthKeys = { "openai", "codex", "chatgpt", "opencode" };

    // auth.json 후보 경로. CLI 표시값과 같은 계정/토큰을 쓰도록 Codex CLI 인증을 최우선으로 보고,
    // 없으면 DevezCode 자체 로그인과 opencode 인증을 사용한다.
    private static IEnumerable<string> AuthPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(home, ".codex", "auth.json");
        yield return CodexCredentialStore.StorePath;
        yield return Path.Combine(home, ".local", "share", "opencode", "auth.json");
        yield return Path.Combine(appData, "opencode", "auth.json");
    }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private System.Threading.Timer? _poll;
    // Timer 주기와 로그인/수동 새로고침이 겹치면 오래된 요청이 나중에 도착해 최신값을 덮을 수 있다.
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly HashSet<string> _rejectedTokens = new(StringComparer.Ordinal);
    private readonly UsageDropGuard _dropGuard = new();

    public event Action<ProviderUsage>? Updated;

    public void Start() => _poll = new System.Threading.Timer(
        _ => _ = PollAsync(waitForTurn: false, resetRejected: true), null, 0, PollMs);

    /// <summary>지금 즉시 1회 폴링(로그인 직후 갱신용).</summary>
    public void RefreshNow() => _ = PollAsync(waitForTurn: true, resetRejected: true);

    /// <summary>Codex OAuth 토큰이 있어 연결된 상태인지(DevezCode 자체 로그인 또는 opencode).</summary>
    public static bool IsConnected() => ReadAuth().token != null;

    // DevezCode 자체 로그인(CodexLoginWindow) 토큰의 refresh 용 — codex CLI 와 동일 값.
    private const string OAuthClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string OAuthTokenUrl = "https://auth.openai.com/oauth/token";

    /// <summary>DevezCode 자체 스토어 토큰이 만료 임박(5분)이면 refresh 로 갱신한다.
    /// opencode auth.json 은 opencode 가 관리하므로 건드리지 않는다. 실패 시 기존 토큰 유지(재로그인 유도).</summary>
    private async Task EnsureFreshAsync()
    {
        if (CodexCredentialStore.Read() is not { } c) return;
        if (string.IsNullOrEmpty(c.refresh)) return;
        if (c.expiresMs - DateTimeOffset.Now.ToUnixTimeMilliseconds() > 5 * 60 * 1000) return;
        try
        {
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = c.refresh!,
                ["client_id"] = OAuthClientId,
            });
            using var res = await _http.PostAsync(OAuthTokenUrl, body).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
            var r = doc.RootElement;
            var access = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return;
            var refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : c.refresh;
            var exp = r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetInt64() : 3600;
            if (!CodexCredentialStore.TrySaveIfCurrent(
                    c.access, c.refresh, access!, refresh,
                    DateTimeOffset.Now.ToUnixTimeMilliseconds() + exp * 1000))
                DiagLog.Write("CodexUsage refresh discarded: app credential changed or disconnected");
        }
        catch { /* 일시 오류 — 기존 토큰 유지 */ }
    }

    private async Task PollAsync(bool waitForTurn, bool resetRejected)
    {
        // 수동 새로고침은 진행 중 요청 뒤에 반드시 실행하고, 타이머 중복만 생략한다.
        if (waitForTurn)
            await _pollGate.WaitAsync().ConfigureAwait(false);
        else if (!_pollGate.Wait(0))
            return;
        if (resetRejected) _rejectedTokens.Clear();
        try
        {
            await EnsureFreshAsync().ConfigureAwait(false);
            var (token, accountId, expired, credentialSource) = ReadAuth(_rejectedTokens);
            if (token == null)
            {
                if (_rejectedTokens.Count > 0)
                    DiagLog.Write("CodexUsage failed: all OAuth credentials rejected");
                return;
            }
            if (expired) { Updated?.Invoke(new ProviderUsage { Provider = "codex", Error = "토큰 만료 — 재로그인 필요" }); return; }

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("User-Agent", "OpenCode-Quota-Toast/1.0");
            if (accountId != null) req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);

            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                DiagLog.Write($"CodexUsage usage HTTP {(int)res.StatusCode}");
                if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _rejectedTokens.Add(token);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                        await PollAsync(waitForTurn: true, resetRejected: false).ConfigureAwait(false);
                    });
                }
                return; // 직전 값 유지
            }

            await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            UsageWindow? primary = null, weekly = null;
            if (root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                primary = ReadWindow(rl, "primary_window");
                weekly  = ReadWindow(rl, "secondary_window");
            }
            if (primary == null && weekly == null)
            {
                DiagLog.Write("CodexUsage response has no rate-limit windows");
                return;
            }

            string? plan = root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String
                ? DerivePlanLabel(pt.GetString()) : null;

            var (currentToken, currentAccountId, _, _) = ReadAuth(_rejectedTokens);
            if (!string.Equals(token, currentToken, StringComparison.Ordinal)
                || !string.Equals(accountId, currentAccountId, StringComparison.Ordinal))
            {
                DiagLog.Write("CodexUsage response discarded: auth changed while request was in flight");
                return;
            }

            // 초기화권 정보는 별도 엔드포인트 — 실패해도 사용량은 정상 전달
            var credits = await FetchResetCreditsAsync(token, accountId, _http).ConfigureAwait(false);
            (currentToken, currentAccountId, _, _) = ReadAuth(_rejectedTokens);
            if (!string.Equals(token, currentToken, StringComparison.Ordinal)
                || !string.Equals(accountId, currentAccountId, StringComparison.Ordinal))
            {
                DiagLog.Write("CodexUsage response discarded: auth changed while fetching reset credits");
                return;
            }

            var samples = new List<UsageDropGuard.WindowSample>(2);
            if (primary != null)
                samples.Add(new("5h", primary.UsedPercent, primary.ResetsAt));
            if (weekly != null)
                samples.Add(new("weekly", weekly.UsedPercent, weekly.ResetsAt));
            if (!_dropGuard.ShouldPublish(samples, DateTimeOffset.Now, out var dropReason))
            {
                DiagLog.Write(
                    $"CodexUsage deferred suspicious drop: {dropReason} "
                    + $"({credentialSource}, credential={Fingerprint(token)})");
                return;
            }
            if (dropReason != null)
                DiagLog.Write(
                    $"CodexUsage accepted confirmed drop: {dropReason} "
                    + $"({credentialSource}, credential={Fingerprint(token)})");

            var usage = new ProviderUsage
            {
                Provider = "codex",
                Primary = primary,
                Weekly = weekly,
                PlanLabel = plan,
                ResetCredits = credits,
            };
            DiagLog.Write(
                $"CodexUsage updated: 5h={primary?.UsedPercent:F0} reset5={primary?.ResetsAt:O}, "
                + $"weekly={weekly?.UsedPercent:F0} resetW={weekly?.ResetsAt:O} "
                + $"({credentialSource}, credential={Fingerprint(token)})");
            Updated?.Invoke(usage);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"CodexUsage poll failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally { _pollGate.Release(); }
    }

    /// <summary>window 객체에서 used_percent + reset_at(epoch초)/reset_after_seconds 를 읽어 UsageWindow 로.</summary>
    private static UsageWindow? ReadWindow(JsonElement rl, string key)
    {
        if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percent", out var up) || up.ValueKind != JsonValueKind.Number) return null;
        var rawPct = up.GetDouble();
        DateTimeOffset? reset = null;
        if (w.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number && ra.GetDouble() > 0)
            reset = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64());
        else if (w.TryGetProperty("reset_after_seconds", out var rs) && rs.ValueKind == JsonValueKind.Number && rs.GetDouble() > 0)
            reset = DateTimeOffset.Now.AddSeconds(rs.GetDouble());
        return new UsageWindow { UsedPercent = rawPct, ResetsAt = reset };
    }

    private static string Fingerprint(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..12];

    private static string DerivePlanLabel(string? planType)
    {
        var raw = (planType ?? "").ToLowerInvariant();
        if (raw.Contains("pro"))  return "OpenAI (Pro)";
        if (raw.Contains("plus")) return "OpenAI (Plus)";
        return string.IsNullOrEmpty(planType) ? "OpenAI" : $"OpenAI ({planType})";
    }

    /// <summary>/wham/rate-limit-reset-credits 를 호출해 초기화권 목록을 읽는다. 실패 시 빈 배열.</summary>
    private static async Task<IReadOnlyList<ResetCredit>> FetchResetCreditsAsync(string token, string? accountId, HttpClient http)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CreditsUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("User-Agent", "OpenCode-Quota-Toast/1.0");
            req.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");
            if (accountId != null) req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);

            using var res = await http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return Array.Empty<ResetCredit>();

            await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("credits", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<ResetCredit>();

            var list = new List<ResetCredit>(arr.GetArrayLength());
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "초기화권" : "초기화권";
                DateTimeOffset? granted = TryParseTimestamp(item, "granted_at");
                DateTimeOffset? expires = TryParseTimestamp(item, "expires_at");
                list.Add(new ResetCredit { Title = title, GrantedAt = granted, ExpiresAt = expires });
            }
            return list;
        }
        catch
        {
            return Array.Empty<ResetCredit>();
        }
    }

    private static DateTimeOffset? TryParseTimestamp(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTimeOffset.TryParse(s.Replace("Z", "+00:00"), out var dt)) return dt;
        return null;
    }

    /// <summary>auth.json 에서 토큰, account ID, 만료 여부, 인증 출처를 읽는다.</summary>
    private static (string? token, string? accountId, bool expired, string? source) ReadAuth(
        ISet<string>? excludedTokens = null)
    {
        if (CodexCredentialStore.IsDisconnected()) return (null, null, false, null);

        foreach (var path in AuthPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;
                // Codex CLI 전용 스키마:
                // { "tokens": { "access_token": "..." }, ... }
                if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object
                    && tokens.TryGetProperty("access_token", out var cliAccess)
                    && cliAccess.ValueKind == JsonValueKind.String)
                {
                    var cliToken = cliAccess.GetString()?.Trim();
                    if (string.IsNullOrEmpty(cliToken)
                        || excludedTokens?.Contains(cliToken) == true)
                        continue;
                    var cliExpiry = ExpirationFromJwt(cliToken);
                    if (cliExpiry is { } expiry && expiry <= DateTimeOffset.UtcNow)
                        continue;
                    return (cliToken, AccountIdFromJwt(cliToken), false, AuthSource(path));
                }
                foreach (var key in AuthKeys)
                {
                    if (!root.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Object) continue;
                    if (!e.TryGetProperty("type", out var t) || t.GetString() != "oauth") continue;
                    if (!e.TryGetProperty("access", out var a) || a.ValueKind != JsonValueKind.String) continue;
                    var token = a.GetString()?.Trim();
                    if (string.IsNullOrEmpty(token)) continue;
                    if (excludedTokens?.Contains(token) == true) continue;

                    bool expired = e.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.Number
                                   && ex.GetDouble() > 0 && ex.GetDouble() < DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    if (expired) continue;
                    var accountId = AccountIdFromJwt(token)
                                    ?? (e.TryGetProperty("accountId", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() : null);
                    return (token, accountId, false, AuthSource(path));
                }
            }
            catch { }
        }
        return (null, null, false, null);
    }

    private static string AuthSource(string path)
    {
        if (string.Equals(path, CodexCredentialStore.StorePath, StringComparison.OrdinalIgnoreCase))
            return "DevezCode";
        return path.Contains($"{Path.DirectorySeparatorChar}.codex{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Codex CLI"
            : "opencode";
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

    private static DateTimeOffset? ExpirationFromJwt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty("exp", out var exp)
                && exp.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64())
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _http.Dispose();
    }
}
