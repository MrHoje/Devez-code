using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    // 실제 설치된 Claude CLI 버전을 사용한다. 조회 실패 시에도 claude-code 형식은 유지한다.
    private static readonly Lazy<string> UserAgent = new(DetectUserAgent);
    private const int PollMs = 3 * 60 * 1000; // 3분 — UA 포함 시 안전한 최소 간격

    // 토큰 후보 경로: claude CLI 파일(있으면 최신) → DevezCode 자체 로그인(ClaudeLoginWindow) 파일.
    private static IEnumerable<string> CredentialPaths()
    {
        yield return CliAccountStore.Instance.ClaudeCredentialsPath;
        if (CliAccountStore.Instance.HasActive("claude")) yield break;
        yield return ClaudeCredentialStore.StorePath;
    }

    // statusline.js 가 live rate_limits 가 없을 때 폴백으로 읽는 파일.
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude");
    private static string FallbackPath => Path.Combine(Dir, "api-usage.json");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private System.Threading.Timer? _poll;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private static string? _lastSuccessfulSubscriptionType;
    private readonly UsageDropGuard _dropGuard = new();
    // Claude 토큰은 opaque 라 계정 ID 를 얻을 수 없다(토큰 회전과 계정 전환을 구분 불가).
    // 그래서 가드 리셋은 두지 않고, 시드만 폴백 파일 지문이 현재 토큰과 일치할 때 수행한다.
    private bool _guardSeeded;

    // ── 이벤트 기반 즉시 갱신 · 실패 백오프 ──
    // UA 헤더가 있어도 이 엔드포인트는 짧은 간격에 민감하다(429 전용 버킷). 정규 3분 주기와
    // 이벤트 트리거가 겹쳐도 어떤 경로로든 요청 간격이 이 값 아래로 내려가지 않게 게이트한다.
    private static readonly TimeSpan MinRequestGap = TimeSpan.FromSeconds(90);
    private long _lastAttemptTicks; // 마지막 요청 시각(UTC ticks) — 0 이면 아직 요청 없음
    private readonly object _scheduleLock = new();
    private System.Threading.Timer? _deferred; // 스로틀로 미룬 갱신(최대 1건)
    private System.Threading.Timer? _retry;    // 실패 백오프 재시도(최대 1건)
    private int _retryAttempt;
    // 일시 실패(네트워크·타임아웃·5xx)와 429 의 재시도 간격. 429 는 별도 버킷을 더 보호한다.
    private static readonly int[] RetryDelaysSec = { 45, 90, 180 };
    private static readonly int[] RateLimitedDelaysSec = { 180, 360 };

    public event Action<RateLimitSnapshot>? SnapshotUpdated;

    public void Start()
    {
        // 즉시 1회 + 이후 3분 주기. 주기 폴링은 겹치면 생략한다.
        _poll = new System.Threading.Timer(
            _ => _ = PollAsync(waitForTurn: false), null, 0, PollMs);
    }

    /// <summary>지금 즉시 1회 폴링(로그인 직후 갱신용).</summary>
    public void RefreshNow() => _ = PollAsync(waitForTurn: true);

    public async Task ResetForAccountChangeAsync()
    {
        await _pollGate.WaitAsync();
        try
        {
            _dropGuard.Reset();
            _guardSeeded = false;
            _lastSuccessfulSubscriptionType = null;
            Interlocked.Exchange(ref _lastAttemptTicks, 0);
            if (File.Exists(FallbackPath)) File.Delete(FallbackPath);
        }
        finally { _pollGate.Release(); }
    }

    /// <summary>턴 완료처럼 사용량이 방금 변한 시점에 갱신을 요청한다. dvz(Devez Vibe)처럼
    /// statusLine 훅이 없는 에이전트는 이 호출이 없으면 다음 3분 주기까지 카드가 그대로다.
    /// 최소 요청 간격(<see cref="MinRequestGap"/>)을 지키기 위해 이르면 남은 시간 뒤로 1건만 예약한다
    /// (턴이 몰려도 요청은 늘지 않는다).</summary>
    public void RequestRefreshSoon()
    {
        var elapsed = TimeSpan.FromTicks(
            DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastAttemptTicks));
        var wait = MinRequestGap - elapsed;
        if (wait <= TimeSpan.Zero)
        {
            RefreshNow();
            return;
        }
        lock (_scheduleLock)
        {
            if (_deferred != null) return; // 이미 예약됨
            _deferred = new System.Threading.Timer(
                _ =>
                {
                    lock (_scheduleLock) { _deferred?.Dispose(); _deferred = null; }
                    RefreshNow();
                },
                null, (long)wait.TotalMilliseconds + 250, System.Threading.Timeout.Infinite);
        }
        DiagLog.Write($"ClaudeUsage refresh deferred {wait.TotalSeconds:F0}s (min request gap)");
    }

    /// <summary>유효한 Claude OAuth 토큰이 하나라도 있는지.</summary>
    public static bool IsConnected() => ReadCredentials().Count > 0;

    // DevezCode 자체 로그인(ClaudeLoginWindow) 토큰의 refresh 용 — claude CLI 와 동일 값.
    private const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string OAuthTokenUrl = "https://console.anthropic.com/v1/oauth/token";

    /// <summary>DevezCode 자체 스토어 토큰이 만료 임박(5분)이면 refresh 로 갱신한다.
    /// claude CLI 파일은 claude 가 관리하므로 건드리지 않는다. 실패 시 기존 토큰 유지(다음에 재로그인 유도).</summary>
    private async Task EnsureFreshClaudeAsync()
    {
        if (CliAccountStore.Instance.HasActive("claude")) return;
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
            if (!ClaudeCredentialStore.TrySaveIfCurrent(
                    c.access, c.refresh, access!, refresh,
                    DateTimeOffset.Now.ToUnixTimeMilliseconds() + exp * 1000))
                DiagLog.Write("ClaudeUsage refresh discarded: app credential changed");
        }
        catch { /* 일시 오류 — 기존 토큰 유지 */ }
    }

    private async Task PollAsync(bool waitForTurn)
    {
        // 수동 새로고침은 진행 중 요청 뒤에 반드시 실행하고, 타이머 중복만 생략한다.
        if (waitForTurn)
            await _pollGate.WaitAsync().ConfigureAwait(false);
        else if (!_pollGate.Wait(0))
            return;
        try
        {
            await EnsureFreshClaudeAsync().ConfigureAwait(false);
            var credentials = ReadCredentials();
            if (credentials.Count == 0)
            {
                DiagLog.Write("ClaudeUsage skipped: no valid OAuth credential");
                return;
            }
            // 실제로 요청을 보내는 주기만 최소 간격 계산의 기준으로 삼는다.
            Interlocked.Exchange(ref _lastAttemptTicks, DateTime.UtcNow.Ticks);
            var credentialGeneration = string.Join(",", credentials.Select(item => item.Fingerprint));
            InvalidateFallbackUnlessAnyAccountMatches(
                credentials.Select(item => item.Fingerprint));
            // 재시작 직후 첫 응답(가짜 급락 가능)이 기준값 없이 무조건 채택되지 않도록
            // 직전 실행의 폴백 파일로 드롭 가드 기준값을 복원한다.
            if (!_guardSeeded)
            {
                _guardSeeded = true;
                SeedDropGuardFromFallback(credentials.Select(item => item.Fingerprint));
            }

            foreach (var credential in credentials)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential.Token);
                req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);

                using var res = await _http.SendAsync(req).ConfigureAwait(false);
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    DiagLog.Write($"ClaudeUsage HTTP 401 ({credential.Source}); trying next credential");
                    InvalidateFallbackForAccount(credential.Fingerprint);
                    continue;
                }
                if (!res.IsSuccessStatusCode)
                {
                    DiagLog.Write($"ClaudeUsage HTTP {(int)res.StatusCode} ({credential.Source})");
                    // 다음 정규 주기(3분)까지 방치하지 않고 백오프로 재시도한다.
                    ScheduleRetry(rateLimited: res.StatusCode == HttpStatusCode.TooManyRequests);
                    return;
                }

                await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
                var root = doc.RootElement;

                double? fableWeeklyPct = null;
                DateTimeOffset? fableWeeklyReset = null;
                if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in limits.EnumerateArray())
                    {
                        if (!item.TryGetProperty("kind", out var kind) || kind.GetString() != "weekly_scoped") continue;
                        if (!item.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) continue;
                        if (!scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) continue;
                        if (!model.TryGetProperty("display_name", out var dn) || dn.GetString() != "Fable") continue;
                        if (item.TryGetProperty("percent", out var pct) && pct.ValueKind == JsonValueKind.Number)
                            fableWeeklyPct = pct.GetDouble();
                        if (item.TryGetProperty("resets_at", out var rs) && rs.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(rs.GetString(), out var dt))
                            fableWeeklyReset = dt;
                        break;
                    }
                }

                var snap = new RateLimitSnapshot
                {
                    CapturedAt = DateTime.Now,
                    FiveHourPercent = ReadPct(root, "five_hour"),
                    SevenDayPercent = ReadPct(root, "seven_day"),
                    FiveHourResetsAt = ReadReset(root, "five_hour"),
                    SevenDayResetsAt = ReadReset(root, "seven_day"),
                    FableWeeklyPercent = fableWeeklyPct,
                    FableWeeklyResetsAt = fableWeeklyReset,
                };
                if (!snap.HasData)
                {
                    DiagLog.Write($"ClaudeUsage response has no usage windows ({credential.Source})");
                    return;
                }

                // 요청 중 로그인/계정 구성이 바뀌었으면 이전 세대 응답을 게시하지 않는다.
                var currentGeneration = string.Join(
                    ",", ReadCredentials().Select(item => item.Fingerprint));
                if (!string.Equals(currentGeneration, credentialGeneration, StringComparison.Ordinal))
                {
                    DiagLog.Write("ClaudeUsage discarded: credentials changed during request");
                    return;
                }
                var samples = new List<UsageDropGuard.WindowSample>(3);
                if (snap.FiveHourPercent is double fiveHour)
                    samples.Add(new("5h", fiveHour, snap.FiveHourResetsAt));
                if (snap.SevenDayPercent is double sevenDay)
                    samples.Add(new("weekly", sevenDay, snap.SevenDayResetsAt));
                if (snap.FableWeeklyPercent is double fableWeekly)
                    samples.Add(new("fable", fableWeekly, snap.FableWeeklyResetsAt));
                if (!_dropGuard.ShouldPublish(samples, DateTimeOffset.Now, out var dropReason))
                {
                    DiagLog.Write(
                        $"ClaudeUsage deferred suspicious drop: {dropReason} "
                        + $"({credential.Source}, credential={credential.Fingerprint})");
                    return;
                }
                if (dropReason != null)
                    DiagLog.Write(
                        $"ClaudeUsage accepted confirmed drop: {dropReason} "
                        + $"({credential.Source}, credential={credential.Fingerprint})");

                CancelRetry();
                WriteFallback(snap, credential.Fingerprint);
                Volatile.Write(ref _lastSuccessfulSubscriptionType, credential.SubscriptionType);
                DiagLog.Write(
                    $"ClaudeUsage updated: 5h={snap.FiveHourPercent:F0} reset5={snap.FiveHourResetsAt:O}, "
                    + $"weekly={snap.SevenDayPercent:F0} resetW={snap.SevenDayResetsAt:O} "
                    + $"({credential.Source}, credential={credential.Fingerprint})");
                SnapshotUpdated?.Invoke(snap);
                return;
            }

            DiagLog.Write("ClaudeUsage failed: all OAuth credentials rejected");
        }
        catch (Exception ex)
        {
            DiagLog.Write($"ClaudeUsage poll failed: {ex.GetType().Name}: {ex.Message}");
            ScheduleRetry(rateLimited: false);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>일시 실패 후 백오프 재시도를 1건 예약한다. 횟수를 다 쓰면 다음 정규 주기를 기다린다.
    /// 성공하면 <see cref="CancelRetry"/> 로 카운터가 초기화된다.</summary>
    private void ScheduleRetry(bool rateLimited)
    {
        var delays = rateLimited ? RateLimitedDelaysSec : RetryDelaysSec;
        lock (_scheduleLock)
        {
            if (_retry != null) return; // 이미 예약됨
            if (_retryAttempt >= delays.Length)
            {
                DiagLog.Write("ClaudeUsage retry budget exhausted; waiting for regular poll");
                return;
            }
            var delay = delays[_retryAttempt++];
            _retry = new System.Threading.Timer(
                _ =>
                {
                    lock (_scheduleLock) { _retry?.Dispose(); _retry = null; }
                    _ = PollAsync(waitForTurn: true);
                },
                null, delay * 1000L, System.Threading.Timeout.Infinite);
            DiagLog.Write(
                $"ClaudeUsage retry scheduled in {delay}s (attempt {_retryAttempt}, rateLimited={rateLimited})");
        }
    }

    private void CancelRetry()
    {
        lock (_scheduleLock)
        {
            _retry?.Dispose();
            _retry = null;
            _retryAttempt = 0;
        }
    }

    /// <summary>statusline.js 가 live rate_limits 부재 시 읽을 폴백 파일을 쓴다.
    /// 수집 시각과 계정 지문을 함께 기록해 오래된/다른 계정 데이터를 진단할 수 있게 한다.</summary>
    private static void WriteFallback(RateLimitSnapshot snap, string accountFingerprint)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("fetched_at", snap.CapturedAt.ToUniversalTime().ToString("O"));
                w.WriteString("account_fingerprint", accountFingerprint);
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

    /// <summary>직전 실행이 저장한 폴백 파일을 드롭 가드 기준값으로 시드한다.
    /// 계정 지문이 현재 자격증명과 일치하고 48시간 이내일 때만 사용한다. reset 이
    /// 이미 지난 기준값은 가드가 자연히 무시하므로 추가 검사하지 않는다.</summary>
    private void SeedDropGuardFromFallback(IEnumerable<string> accountFingerprints)
    {
        try
        {
            if (!File.Exists(FallbackPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FallbackPath));
            var root = doc.RootElement;
            var stored = root.TryGetProperty("account_fingerprint", out var fp)
                && fp.ValueKind == JsonValueKind.String ? fp.GetString() : null;
            if (stored == null || !accountFingerprints.Contains(stored, StringComparer.Ordinal)) return;
            if (!root.TryGetProperty("fetched_at", out var fa) || fa.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(fa.GetString(), out var fetchedAt)
                || DateTimeOffset.Now - fetchedAt > TimeSpan.FromHours(48))
                return;

            var samples = new List<UsageDropGuard.WindowSample>(2);
            AddFallbackSample(samples, root, "five_hour", "5h");
            AddFallbackSample(samples, root, "seven_day", "weekly");
            if (samples.Count == 0) return;
            _dropGuard.Seed(samples);
            DiagLog.Write(
                $"ClaudeUsage drop guard seeded from fallback (fetched {fetchedAt:O}, credential={stored})");
        }
        catch { /* 폴백 손상 — 무시하고 첫 응답을 기준값으로 사용 */ }
    }

    private static void AddFallbackSample(
        List<UsageDropGuard.WindowSample> samples, JsonElement root, string key, string name)
    {
        if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return;
        if (!w.TryGetProperty("used_percentage", out var up) || up.ValueKind != JsonValueKind.Number) return;
        DateTimeOffset? reset = null;
        if (w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
            reset = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64());
        samples.Add(new(name, up.GetDouble(), reset));
    }

    private static void InvalidateFallbackUnlessAnyAccountMatches(
        IEnumerable<string> accountFingerprints)
    {
        try
        {
            if (!File.Exists(FallbackPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FallbackPath));
            var root = doc.RootElement;
            var stored = root.TryGetProperty("account_fingerprint", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var currentAccounts = accountFingerprints.ToHashSet(StringComparer.Ordinal);
            if (stored == null || !currentAccounts.Contains(stored))
                File.Delete(FallbackPath);
        }
        catch
        {
            try { File.Delete(FallbackPath); } catch { }
        }
    }

    private static void InvalidateFallbackForAccount(string accountFingerprint)
    {
        try
        {
            if (!File.Exists(FallbackPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FallbackPath));
            var root = doc.RootElement;
            var stored = root.TryGetProperty("account_fingerprint", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (string.Equals(stored, accountFingerprint, StringComparison.Ordinal))
                File.Delete(FallbackPath);
        }
        catch
        {
            try { File.Delete(FallbackPath); } catch { }
        }
    }
    private sealed record OAuthCredential(
        string Token,
        string Source,
        string Fingerprint,
        string? SubscriptionType);

    /// <summary>유효한 자격증명을 우선순위대로 읽는다. 만료된 CLI 토큰이 앱 로그인을
    /// 가리지 않도록 만료 시각을 검사하고 다음 후보로 넘어간다.</summary>
    private static IReadOnlyList<OAuthCredential> ReadCredentials()
    {
        var result = new List<OAuthCredential>();
        var nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        foreach (var path in CredentialPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;
                var oauth = root.TryGetProperty("claudeAiOauth", out var nested)
                    && nested.ValueKind == JsonValueKind.Object ? nested : root;
                string? token = null;
                foreach (var key in new[] { "accessToken", "access_token", "token" })
                {
                    if (oauth.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        token = value.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(token)) break;
                    }
                }
                if (string.IsNullOrEmpty(token)) continue;

                long expiresAt = oauth.TryGetProperty("expiresAt", out var expires)
                    && expires.ValueKind == JsonValueKind.Number ? expires.GetInt64() : 0;
                if (expiresAt > 0 && expiresAt <= nowMs)
                {
                    DiagLog.Write($"ClaudeUsage skipped expired credential: {Path.GetFileName(path)}");
                    continue;
                }

                string? subscription = oauth.TryGetProperty("subscriptionType", out var plan)
                    && plan.ValueKind == JsonValueKind.String ? plan.GetString() : null;
                result.Add(new OAuthCredential(
                    token,
                    path == ClaudeCredentialStore.StorePath ? "DevezCode" : "Claude CLI",
                    Fingerprint(token),
                    subscription));
            }
            catch (Exception ex)
            {
                DiagLog.Write($"ClaudeUsage credential read failed ({Path.GetFileName(path)}): {ex.GetType().Name}");
            }
        }
        return result;
    }

    public static string? ReadSubscriptionType()
        => Volatile.Read(ref _lastSuccessfulSubscriptionType)
            ?? ReadCredentials().FirstOrDefault()?.SubscriptionType;

    /// <summary>subscriptionType 문자열을 표시용 라벨로 변환("pro" → "Claude (Pro)").</summary>
    public static string FormatPlanLabel(string? subscriptionType)
    {
        if (string.IsNullOrEmpty(subscriptionType)) return "Claude";
        var plan = subscriptionType.ToLowerInvariant() switch
        {
            "pro" => "Pro",
            "max" => "Max",
            "team" => "Team",
            "enterprise" => "Enterprise",
            _ => subscriptionType,
        };
        return $"Claude ({plan})";
    }

    private static string Fingerprint(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];

    private static string DetectUserAgent()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            var output = process?.StandardOutput.ReadLine();
            if (process != null && process.WaitForExit(3000) && process.ExitCode == 0)
            {
                var version = output?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(version)) return "claude-code/" + version;
            }
        }
        catch { }
        return "claude-code/2";
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
        lock (_scheduleLock)
        {
            _deferred?.Dispose();
            _deferred = null;
            _retry?.Dispose();
            _retry = null;
        }
        _http.Dispose();
    }
}
