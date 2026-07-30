using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>초기화권 소비 결과. Reset/AlreadyRedeemed = 실제 소비, 그 외는 미소비.</summary>
public enum ConsumeOutcome { Reset, NothingToReset, NoCredit, AlreadyRedeemed, Unknown }

/// <summary>codex/openai 사용량을 폴링한다. Codex CLI 또는 DevezCode/opencode 의 OAuth 토큰으로
/// <c>GET chatgpt.com/backend-api/wham/usage</c> 를 호출한다.
/// primary/secondary 슬롯은 고정 의미가 아니므로 limit_window_seconds 로 5h/주간을 구분한다.
/// 토큰 만료/오류 시 직전 값 유지.</summary>
public sealed class CodexUsageService : IDisposable
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string CreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private const string ConsumeUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume";
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

    // 마지막 성공 사용량 스냅샷 — 재시작 직후 드롭 가드 기준값 시드용(_pollGate 안에서만 접근).
    private static string SnapshotPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "codex-usage.json");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private System.Threading.Timer? _poll;
    // Timer 주기와 로그인/수동 새로고침이 겹치면 오래된 요청이 나중에 도착해 최신값을 덮을 수 있다.
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly HashSet<string> _rejectedTokens = new(StringComparer.Ordinal);
    private readonly UsageDropGuard _dropGuard = new();
    private bool _guardSeeded;
    private string? _guardAccountKey; // 가드 기준값을 만든 계정 키(토큰 회전에도 안정적인 account ID 지문)

    // 초기화권 조회는 사용량과 별개 엔드포인트라 간헐적으로 실패한다(타임아웃/429/5xx).
    // 실패를 "0개"로 게시하면 카드의 초기화권 영역이 폴링마다 사라졌다 나타난다.
    // 마지막 성공 목록을 들고 있다가 조회 실패 시 이어서 쓴다(_pollGate 안에서만 접근).
    private IReadOnlyList<ResetCredit>? _lastCredits;
    private DateTimeOffset _lastCreditsAt;
    private string? _lastCreditsAccountKey;
    private static readonly TimeSpan CreditsCarryOver = TimeSpan.FromMinutes(30);

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

            var fingerprint = Fingerprint(token); // 로그용 — 어떤 토큰이 쓰였는지
            // 가드/스냅샷 키는 토큰이 아니라 계정 기준이어야 한다. 토큰은 CLI refresh 로
            // 수시로 회전하므로 토큰 지문을 쓰면 같은 계정인데도 기준값이 무효화된다.
            var accountKey = Fingerprint(accountId ?? token);
            // 계정이 바뀌면 이전 계정 기준값으로 새 계정의 정상값을 보류하지 않도록 가드를 비운다.
            if (_guardAccountKey != null && !string.Equals(_guardAccountKey, accountKey, StringComparison.Ordinal))
            {
                _dropGuard.Reset();
                // 이전 계정의 초기화권 목록을 새 계정 화면에 이어 쓰지 않는다.
                _lastCredits = null;
                _lastCreditsAccountKey = null;
            }
            _guardAccountKey = accountKey;
            if (!_guardSeeded)
            {
                _guardSeeded = true;
                SeedDropGuard(accountKey);
            }

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

            ParsedWindow? primarySlot = null, secondarySlot = null;
            if (root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                primarySlot = ReadWindow(rl, "primary_window");
                secondarySlot = ReadWindow(rl, "secondary_window");
            }
            var (primary, weekly) = ClassifyWindows(primarySlot, secondarySlot);
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

            // 초기화권 정보는 별도 엔드포인트 — 실패해도 사용량은 정상 전달하고,
            // 목록은 마지막 성공값을 이어 써서 표시가 깜빡이지 않게 한다.
            var fetchedCredits = await FetchResetCreditsAsync(token, accountId, _http).ConfigureAwait(false);
            var credits = ResolveCredits(fetchedCredits, accountKey, DateTimeOffset.Now);
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
            if (!_dropGuard.ShouldPublish(
                    samples,
                    DateTimeOffset.Now,
                    out var dropReason,
                    out var shouldRetrySoon))
            {
                DiagLog.Write(
                    $"CodexUsage deferred suspicious drop: {dropReason} "
                    + $"({credentialSource}, credential={fingerprint})");
                if (shouldRetrySoon)
                {
                    // 사용량 급락과 reset_at 변경이 함께 관측되면 초기화권 사용 가능성이 높다.
                    // 서버의 단발성 가짜 프로필은 그대로 보류하되, 정규 3분 폴링 대신 한 번만
                    // 빠르게 재조회해 같은 새 윈도우가 유지되는지 확인한다.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                        await PollAsync(waitForTurn: true, resetRejected: false).ConfigureAwait(false);
                    });
                }
                return;
            }
            if (dropReason != null)
                DiagLog.Write(
                    $"CodexUsage accepted confirmed drop: {dropReason} "
                    + $"({credentialSource}, credential={fingerprint})");

            var usage = new ProviderUsage
            {
                Provider = "codex",
                Primary = primary,
                Weekly = weekly,
                PlanLabel = plan,
                ResetCredits = credits,
            };
            WriteSnapshot(primary, weekly, accountKey);
            DiagLog.Write(
                $"CodexUsage updated: 5h={primary?.UsedPercent:F0} reset5={primary?.ResetsAt:O}, "
                + $"weekly={weekly?.UsedPercent:F0} resetW={weekly?.ResetsAt:O} "
                + $"({credentialSource}, credential={fingerprint})");
            Updated?.Invoke(usage);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"CodexUsage poll failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally { _pollGate.Release(); }
    }

    private readonly record struct ParsedWindow(UsageWindow Usage, long? LimitWindowSeconds);

    /// <summary>API 슬롯을 실제 윈도우 길이로 분류한다. 5시간 한도가 임시 제거되면
    /// 7일 창이 primary_window 로 이동하므로 슬롯 위치만으로 의미를 판단하면 안 된다.</summary>
    private static (UsageWindow? Primary, UsageWindow? Weekly) ClassifyWindows(
        ParsedWindow? primarySlot, ParsedWindow? secondarySlot)
    {
        UsageWindow? primary = null, weekly = null;
        Assign(primarySlot, fallbackWeekly: false);
        Assign(secondarySlot, fallbackWeekly: true);
        return (primary, weekly);

        void Assign(ParsedWindow? parsed, bool fallbackWeekly)
        {
            if (parsed is not { } value) return;
            // 현재 Codex 윈도우는 5시간(18,000초) 또는 주간(604,800초)이다.
            // 메타데이터가 없는 구형 응답만 기존 슬롯 의미로 폴백한다.
            var isWeekly = value.LimitWindowSeconds is long seconds
                ? seconds >= (long)TimeSpan.FromDays(6).TotalSeconds
                : fallbackWeekly;
            if (isWeekly) weekly ??= value.Usage;
            else primary ??= value.Usage;
        }
    }

    /// <summary>window 객체에서 사용률·초기화 시각·윈도우 길이를 읽는다.</summary>
    private static ParsedWindow? ReadWindow(JsonElement rl, string key)
    {
        if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percent", out var up) || up.ValueKind != JsonValueKind.Number) return null;
        var rawPct = up.GetDouble();
        DateTimeOffset? reset = null;
        if (w.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number && ra.GetDouble() > 0)
            reset = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64());
        else if (w.TryGetProperty("reset_after_seconds", out var rs) && rs.ValueKind == JsonValueKind.Number && rs.GetDouble() > 0)
            reset = DateTimeOffset.Now.AddSeconds(rs.GetDouble());
        long? limitWindowSeconds = w.TryGetProperty("limit_window_seconds", out var lw)
            && lw.ValueKind == JsonValueKind.Number && lw.TryGetInt64(out var seconds)
            ? seconds
            : null;
        return new ParsedWindow(
            new UsageWindow { UsedPercent = rawPct, ResetsAt = reset },
            limitWindowSeconds);
    }

    private static string Fingerprint(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..12];

    /// <summary>직전 실행이 저장한 사용량 스냅샷을 드롭 가드 기준값으로 시드한다.
    /// 서버가 같은 계정에 두 사용량 레코드를 갖고 간헐적으로 낮은 쪽(가짜 급락)을 반환하는
    /// 문제가 재시작 직후 첫 응답에 걸리면 기준값이 없어 그대로 표시되므로, 계정 키가 일치하는
    /// 신선한 스냅샷으로 기준값을 복원해 실행 중과 동일하게 보류·확인하게 한다.</summary>
    private void SeedDropGuard(string accountKey)
    {
        try
        {
            if (!File.Exists(SnapshotPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SnapshotPath));
            var root = doc.RootElement;
            var stored = root.TryGetProperty("account_fingerprint", out var fp)
                && fp.ValueKind == JsonValueKind.String ? fp.GetString() : null;
            if (!string.Equals(stored, accountKey, StringComparison.Ordinal)) return;
            if (!root.TryGetProperty("fetched_at", out var fa) || fa.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(fa.GetString(), out var fetchedAt)
                || DateTimeOffset.Now - fetchedAt > TimeSpan.FromHours(48))
                return;

            var samples = new List<UsageDropGuard.WindowSample>(2);
            AddSnapshotSample(samples, root, "five_hour", "5h");
            AddSnapshotSample(samples, root, "weekly", "weekly");
            if (samples.Count == 0) return;
            _dropGuard.Seed(samples);
            DiagLog.Write(
                $"CodexUsage drop guard seeded from snapshot (fetched {fetchedAt:O}, account={accountKey})");
        }
        catch { /* 스냅샷 손상 — 무시하고 첫 응답을 기준값으로 사용 */ }
    }

    private static void AddSnapshotSample(
        List<UsageDropGuard.WindowSample> samples, JsonElement root, string key, string name)
    {
        if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return;
        if (!w.TryGetProperty("used_percent", out var up) || up.ValueKind != JsonValueKind.Number) return;
        DateTimeOffset? reset = null;
        if (w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
            reset = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64());
        samples.Add(new(name, up.GetDouble(), reset));
    }

    /// <summary>게시한 사용량을 스냅샷 파일로 남긴다(다음 실행의 가드 시드용).
    /// 토큰 원문은 기록하지 않고 계정 ID 기반 지문만 기록한다 — 토큰 회전에도 유지되도록.</summary>
    private static void WriteSnapshot(UsageWindow? primary, UsageWindow? weekly, string accountKey)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("fetched_at", DateTimeOffset.UtcNow.ToString("O"));
                w.WriteString("account_fingerprint", accountKey);
                WriteSnapshotWindow(w, "five_hour", primary);
                WriteSnapshotWindow(w, "weekly", weekly);
                w.WriteEndObject();
            }
            // 쓰다 만 파일을 다음 실행이 읽지 않도록 임시 파일 후 교체.
            var tmp = SnapshotPath + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, SnapshotPath, overwrite: true);
        }
        catch { }
    }

    private static void WriteSnapshotWindow(Utf8JsonWriter w, string key, UsageWindow? win)
    {
        if (win == null) return;
        w.WriteStartObject(key);
        w.WriteNumber("used_percent", win.UsedPercent);
        if (win.ResetsAt is { } reset) w.WriteNumber("resets_at", reset.ToUnixTimeSeconds());
        w.WriteEndObject();
    }

    private static string DerivePlanLabel(string? planType)
    {
        var raw = (planType ?? "").ToLowerInvariant();
        if (raw.Contains("pro"))  return "OpenAI (Pro)";
        if (raw.Contains("plus")) return "OpenAI (Plus)";
        return string.IsNullOrEmpty(planType) ? "OpenAI" : $"OpenAI ({planType})";
    }

    /// <summary>조회 결과를 표시용 목록으로 정한다. 성공(200)은 그대로 채택하고 마지막 성공값으로
    /// 기록한다. 조회 실패(null)는 "0개"가 아니라 정보 없음이므로, 같은 계정의 마지막 성공값이
    /// 아직 신선하면(30분) 이어 쓴다 — 아니면 빈 목록.</summary>
    private IReadOnlyList<ResetCredit> ResolveCredits(
        IReadOnlyList<ResetCredit>? fetched, string accountKey, DateTimeOffset now)
    {
        if (fetched != null)
        {
            _lastCredits = fetched;
            _lastCreditsAt = now;
            _lastCreditsAccountKey = accountKey;
            return fetched;
        }

        if (_lastCredits is { Count: > 0 } cached
            && string.Equals(_lastCreditsAccountKey, accountKey, StringComparison.Ordinal)
            && now - _lastCreditsAt <= CreditsCarryOver)
        {
            // 이어 쓰는 동안 만료 시각이 지난 항목은 서버 확인 없이도 확실히 사라진 것이다.
            var live = cached.Where(c => c.ExpiresAt == null || c.ExpiresAt > now).ToArray();
            DiagLog.Write(
                $"CodexUsage reset-credits 조회 실패 — 마지막 성공값 유지(count={live.Length}, "
                + $"{(int)(now - _lastCreditsAt).TotalSeconds}s 전)");
            return live;
        }

        DiagLog.Write("CodexUsage reset-credits 조회 실패 — 이어 쓸 최근 성공값 없음");
        return Array.Empty<ResetCredit>();
    }

    /// <summary>소비한 초기화권을 마지막 성공 목록에서 제거한다. 소비 직후 조회가 실패해도
    /// 이어 쓴 목록이 이미 쓴 초기화권을 되살리지 않게 한다(_pollGate 안에서 호출).</summary>
    private void DropConsumedCredit(string? creditId)
    {
        if (_lastCredits is not { Count: > 0 } cached) return;
        // id 로 못 찾으면(캐시가 낡음) 서버 auto-select 와 같은 기준인 만료 최빠름을 지운다.
        var target = (string.IsNullOrEmpty(creditId)
            ? null
            : cached.FirstOrDefault(c => string.Equals(c.Id, creditId, StringComparison.Ordinal)))
            ?? PickEarliestExpiring(cached);
        _lastCredits = cached.Where(c => !ReferenceEquals(c, target)).ToArray();
    }

    /// <summary>/wham/rate-limit-reset-credits 를 호출해 초기화권 목록을 읽는다.
    /// 조회 실패는 <c>null</c>(정보 없음) — 빈 배열(실제 0개)과 구분해야 표시가 깜빡이지 않는다.</summary>
    private static async Task<IReadOnlyList<ResetCredit>?> FetchResetCreditsAsync(string token, string? accountId, HttpClient http)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CreditsUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("User-Agent", "OpenCode-Quota-Toast/1.0");
            req.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");
            if (accountId != null) req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);

            using var res = await http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                DiagLog.Write($"CodexUsage reset-credits HTTP {(int)res.StatusCode}");
                return null;
            }

            await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("credits", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                // 200 인데 목록이 없으면 응답 형식이 바뀐 것 — "0개"로 단정하지 않는다.
                DiagLog.Write("CodexUsage reset-credits 응답에 credits 배열 없음");
                return null;
            }

            var list = new List<ResetCredit>(arr.GetArrayLength());
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "초기화권" : "초기화권";
                var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                DateTimeOffset? granted = TryParseTimestamp(item, "granted_at");
                DateTimeOffset? expires = TryParseTimestamp(item, "expires_at");
                list.Add(new ResetCredit { Id = id, Title = title, GrantedAt = granted, ExpiresAt = expires });
            }
            if (list.Count > 0 && list.All(c => c.Id == null))
                DiagLog.Write("CodexUsage: reset-credits 응답에 id 없음 — 소비 시 auto-select 폴백");
            return list;
        }
        catch (Exception ex)
        {
            DiagLog.Write("CodexUsage reset-credits 조회 예외: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>만료가 가장 빠른 초기화권을 고른다. ExpiresAt 오름차순, null(만료정보 없음)은 맨 뒤.
    /// 목록이 비면 null.</summary>
    public static ResetCredit? PickEarliestExpiring(IEnumerable<ResetCredit> credits)
        => credits
            .OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();

    /// <summary>초기화권 1개를 소비한다. 폴링과 직렬화되며 자동 재시도하지 않는다.
    /// 성공(Reset/AlreadyRedeemed) 시 dropGuard 에 급락을 예고(ExpectDrop)하고 쿨다운을
    /// 기록한 뒤, 즉시 재폴링 + 버스트 재폴링으로 서버 전파 지연을 추적한다.</summary>
    public async Task<ConsumeOutcome> ConsumeResetCreditAsync(string? creditId, string redeemRequestId)
    {
        await _pollGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureFreshAsync().ConfigureAwait(false);
            var (token, accountId, expired, _) = ReadAuth();
            if (token == null || expired)
            {
                DiagLog.Write("CodexUsage consume 중단: 토큰 없음/만료");
                return ConsumeOutcome.Unknown;
            }

            var payload = new Dictionary<string, string> { ["redeem_request_id"] = redeemRequestId };
            if (!string.IsNullOrEmpty(creditId)) payload["credit_id"] = creditId!;

            using var req = new HttpRequestMessage(HttpMethod.Post, ConsumeUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("User-Agent", "OpenCode-Quota-Toast/1.0");
            req.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");
            if (accountId != null) req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            var bodyText = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                DiagLog.Write($"CodexUsage consume HTTP {(int)res.StatusCode}");
                return ConsumeOutcome.Unknown;
            }

            var outcome = ParseConsumeOutcome(bodyText);
            DiagLog.Write($"CodexUsage consume outcome={outcome}");

            if (outcome is ConsumeOutcome.Reset or ConsumeOutcome.AlreadyRedeemed)
            {
                // 정당한 조기 초기화 — 단, 서버 반영이 수 분 지연될 수 있어 기준값을 비우는
                // 방식(Reset)으로는 안 된다: 즉시 재폴링이 아직 전파 안 된 옛 高사용률을 받아
                // 빈 가드의 새 기준값이 되고, 실제 급락은 다음 정규 폴링까지 보류된다(실측
                // 약 2분 20초 지연). 대신 기대 시간창 동안 첫 급락을 확인 없이 즉시 채택한다.
                // _guardSeeded 는 true 로 유지해야 한다. false 로 두면 다음 폴링이 소비 이전
                // 高사용률 스냅샷으로 가드를 재시드한다.
                _dropGuard.ExpectDrop(DateTimeOffset.Now + ExpectDropWindow);
                WriteResetCooldown(Fingerprint(accountId ?? token));
                // 소비한 초기화권은 이어 쓰기 캐시에서도 지운다. 직후 조회가 실패하면
                // 옛 목록이 그대로 이어 써져 이미 쓴 초기화권이 남아 보인다.
                DropConsumedCredit(creditId);
            }
            return outcome;
        }
        catch (Exception ex)
        {
            DiagLog.Write("CodexUsage consume 예외: " + ex.GetType().Name);
            return ConsumeOutcome.Unknown;
        }
        finally
        {
            _pollGate.Release();
            // 성공 여부와 무관하게 최신 상태를 다시 읽어온다(성공 시 초기화 반영, 실패 시 원복 확인).
            RefreshNow();
            // 소비 성공 시에만(기대 시간창이 열려 있을 때만) 서버 전파 지연을 짧은 간격으로 추적.
            StartPostConsumeBurstPoll();
        }
    }

    // 초기화권 소비 후 서버가 옛 사용량을 반환하는 전파 지연을 허용하는 기대 시간창.
    private static readonly TimeSpan ExpectDropWindow = TimeSpan.FromMinutes(5);

    /// <summary>소비 직후 정규 3분 폴링을 기다리지 않도록 짧은 간격으로 재조회한다.
    /// 가드가 초기화(급락 또는 윈도우 교체)를 채택하면 기대가 해제되어 조기 종료되고,
    /// 소비가 실패했으면 기대가 없어 즉시 종료된다.</summary>
    private void StartPostConsumeBurstPoll()
    {
        _ = Task.Run(async () =>
        {
            foreach (var seconds in new[] { 3, 5, 10, 15, 30, 60, 60, 60 })
            {
                if (!_dropGuard.IsExpectingDrop(DateTimeOffset.Now)) return;
                await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
                if (!_dropGuard.IsExpectingDrop(DateTimeOffset.Now)) return;
                await PollAsync(waitForTurn: true, resetRejected: false).ConfigureAwait(false);
            }
        });
    }

    private static ConsumeOutcome ParseConsumeOutcome(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
            return code switch
            {
                "reset" => ConsumeOutcome.Reset,
                "nothing_to_reset" => ConsumeOutcome.NothingToReset,
                "no_credit" => ConsumeOutcome.NoCredit,
                "already_redeemed" => ConsumeOutcome.AlreadyRedeemed,
                _ => ConsumeOutcome.Unknown,
            };
        }
        catch { return ConsumeOutcome.Unknown; }
    }

    // ── 초기화권 소비 후 1시간 쿨다운(중복 사용 방지 UX 가드, 재시작을 넘어 유지) ──
    public static readonly TimeSpan ResetCooldownDuration = TimeSpan.FromHours(1);

    private static string ResetCooldownPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "codex-reset-last-used.json");

    private void WriteResetCooldown(string accountFingerprint)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResetCooldownPath)!);
            var json = JsonSerializer.Serialize(new
            {
                used_at = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                fingerprint = accountFingerprint,
            });
            var tmp = ResetCooldownPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ResetCooldownPath, overwrite: true);
        }
        catch (Exception ex) { DiagLog.Write("CodexUsage 쿨다운 기록 실패: " + ex.GetType().Name); }
    }

    /// <summary>현재 codex 계정이 초기화권 쿨다운 중이면 남은 시간, 아니면 null.
    /// 지문이 현재 계정과 다르면(다른 계정) 무시한다.</summary>
    public static TimeSpan? ResetCooldownRemaining()
    {
        try
        {
            if (!File.Exists(ResetCooldownPath)) return null;
            var (token, accountId, _, _) = ReadAuth();
            if (token == null) return null;
            var current = Fingerprint(accountId ?? token);

            using var doc = JsonDocument.Parse(File.ReadAllText(ResetCooldownPath));
            var root = doc.RootElement;
            var fp = root.TryGetProperty("fingerprint", out var f) ? f.GetString() : null;
            if (!string.Equals(fp, current, StringComparison.Ordinal)) return null;
            if (!root.TryGetProperty("used_at", out var u) || u.ValueKind != JsonValueKind.Number) return null;

            var usedAt = DateTimeOffset.FromUnixTimeMilliseconds(u.GetInt64());
            var remaining = ResetCooldownDuration - (DateTimeOffset.Now - usedAt);
            return remaining > TimeSpan.Zero ? remaining : null;
        }
        catch { return null; }
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
