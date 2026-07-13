using System.Net.Http;
using System.Text;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>안티그래비티(agy/Antigravity) 사용량 폴링.
/// 커뮤니티 트래커(fuelcheck/antigravity-usage/CodexBar)와 동일 경로:
/// <c>POST cloudcode-pa.googleapis.com/v1internal:loadCodeAssist</c> → project/tier,
/// <c>POST .../v1internal:fetchAvailableModels</c> → 모델별 quotaInfo(remainingFraction·resetTime, 5시간 창).
/// 인증은 agy 가 Windows 자격증명 관리자에 저장한 OAuth 토큰(<see cref="AntigravityCredentialStore"/>)
/// 읽기 전용 — refresh 는 agy 자신이 수행하므로 만료면 안내만 표시.
/// 주의: v1internal 비공식 엔드포인트 — Google 이 예고 없이 바꾸면 이 푸터 표시만 조용히 실패한다.</summary>
public sealed class AntigravityUsageService : IDisposable
{
    private const string LoadCodeAssistUrl = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string FetchModelsUrl = "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels";
    private const int PollMs = 3 * 60 * 1000;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private System.Threading.Timer? _poll;
    private string? _projectId;
    private string? _planLabel;
    // 유료 플랜 월간 프롬프트 크레딧 (loadCodeAssist) — 무료 티어는 null.
    private double? _monthlyCredits;
    private double? _availableCredits;

    public event Action<ProviderUsage>? Updated;

    public void Start() => _poll = new System.Threading.Timer(_ => _ = PollAsync(), null, 0, PollMs);

    public void RefreshNow() => _ = PollAsync();

    public static bool IsConnected() => AntigravityCredentialStore.IsConnected();

    private async Task PollAsync()
    {
        try
        {
            var cred = AntigravityCredentialStore.Read();
            if (cred == null)
            {
                Updated?.Invoke(new ProviderUsage
                {
                    Provider = "antigravity",
                    Error = "로그인 필요 — agy 에서 Google 로그인",
                });
                return;
            }

            // 만료 토큰이면 agy 가 갱신할 때까지 안내 (읽기 전용 정책 — refresh 미수행).
            if (cred.Value.expiresMs > 0
                && DateTimeOffset.FromUnixTimeMilliseconds(cred.Value.expiresMs) <= DateTimeOffset.UtcNow)
            {
                Updated?.Invoke(new ProviderUsage
                {
                    Provider = "antigravity",
                    Error = "토큰 만료 — agy 를 한 번 실행해 갱신",
                });
                return;
            }

            var token = cred.Value.access;

            // 매 폴링마다 loadCodeAssist 재조회 — project id 외에 플랜 티어/월간 프롬프트
            // 크레딧(유료 플랜)이 여기서만 와서, 플랜 변경·크레딧 소진이 표시에 따라온다.
            await LoadCodeAssistAsync(token).ConfigureAwait(false);

            var usage = await FetchModelsUsageAsync(token).ConfigureAwait(false);
            if (usage != null) Updated?.Invoke(usage);
        }
        catch { /* 다음 폴링 */ }
    }

    private async Task LoadCodeAssistAsync(string token)
    {
        try
        {
            using var res = await SendAsync(token, LoadCodeAssistUrl,
                "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}")
                .ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return;
            await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.TryGetProperty("cloudaicompanionProject", out var proj))
            {
                _projectId = proj.ValueKind switch
                {
                    JsonValueKind.String => proj.GetString(),
                    JsonValueKind.Object when proj.TryGetProperty("id", out var pid) => pid.GetString(),
                    _ => null,
                };
            }
            if (root.TryGetProperty("currentTier", out var tier)
                && tier.ValueKind == JsonValueKind.Object
                && tier.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String)
                _planLabel = name.GetString();

            // 유료 플랜 월간 프롬프트 크레딧 — planInfo.monthlyPromptCredits(총량) +
            // availablePromptCredits(잔여). 무료 티어는 필드가 없어 null 유지.
            double? monthly = null, available = null;
            if (root.TryGetProperty("planInfo", out var plan) && plan.ValueKind == JsonValueKind.Object
                && plan.TryGetProperty("monthlyPromptCredits", out var mc) && mc.ValueKind == JsonValueKind.Number)
                monthly = mc.GetDouble();
            if (root.TryGetProperty("availablePromptCredits", out var ac) && ac.ValueKind == JsonValueKind.Number)
                available = ac.GetDouble();
            _monthlyCredits = monthly;
            _availableCredits = available;
        }
        catch { }
    }

    /// <summary>fetchAvailableModels → 모델별 quotaInfo 중 "가장 많이 쓴" 창을 대표로 표시.
    /// (agy 는 모델별 5시간 창 — 기본 에이전트 모델이 있으면 그 모델을 우선.)</summary>
    private async Task<ProviderUsage?> FetchModelsUsageAsync(string token)
    {
        var body = _projectId != null
            ? JsonSerializer.Serialize(new Dictionary<string, string> { ["project"] = _projectId })
            : "{}";
        using var res = await SendAsync(token, FetchModelsUrl, body).ConfigureAwait(false);
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return new ProviderUsage
            {
                Provider = "antigravity",
                Error = "인증 실패 — agy 를 한 번 실행해 갱신",
                PlanLabel = _planLabel,
            };
        }
        if (!res.IsSuccessStatusCode) return null;

        await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
            return null;

        string? defaultModelId = root.TryGetProperty("defaultAgentModelId", out var dm) && dm.ValueKind == JsonValueKind.String
            ? dm.GetString() : null;

        double? bestUsed = null;
        DateTimeOffset? bestReset = null;
        foreach (var m in models.EnumerateObject())
        {
            if (!m.Value.TryGetProperty("quotaInfo", out var q) || q.ValueKind != JsonValueKind.Object) continue;
            double used = 100;
            if (q.TryGetProperty("remainingFraction", out var rf) && rf.ValueKind == JsonValueKind.Number)
                used = Math.Clamp((1.0 - rf.GetDouble()) * 100.0, 0, 100);
            else if (!(q.TryGetProperty("isExhausted", out var ex) && ex.ValueKind == JsonValueKind.True))
                continue; // remainingFraction 도 isExhausted 도 없음 — 정보 없는 모델
            DateTimeOffset? reset = null;
            if (q.TryGetProperty("resetTime", out var rt) && rt.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(rt.GetString(), out var dt))
                reset = dt;

            // 기본 에이전트 모델이면 즉시 채택, 아니면 최대 사용률 추적.
            if (defaultModelId != null && m.Name == defaultModelId)
            {
                bestUsed = used;
                bestReset = reset;
                break;
            }
            if (bestUsed == null || used > bestUsed)
            {
                bestUsed = used;
                bestReset = reset;
            }
        }

        if (bestUsed == null) return null;

        // 창 종류는 플랜별로 다르다(유료=5시간, 무료 티어=주간 — 실측 리셋 7일 뒤).
        // API 가 창 종류를 안 주므로 리셋까지 남은 시간으로 판별: 6시간 이내면 5시간 창(Primary),
        // 그 외(또는 리셋 미상)는 주간 창(Weekly). 주간 창의 마지막 6시간 동안만 잠깐 5시간으로
        // 오분류될 수 있는 절충 — 5시간 플랜은 항상 정확.
        var window = new UsageWindow { UsedPercent = bestUsed.Value, ResetsAt = bestReset };
        bool shortWindow = bestReset != null && bestReset.Value - DateTimeOffset.UtcNow <= TimeSpan.FromHours(6);

        // 유료 플랜이면 월간 프롬프트 크레딧 사용률을 Monthly 로 병행 표시.
        UsageWindow? monthlyWindow = null;
        if (_monthlyCredits is double total and > 0 && _availableCredits is double avail)
            monthlyWindow = new UsageWindow
            {
                UsedPercent = Math.Clamp((total - avail) / total * 100.0, 0, 100),
            };

        return new ProviderUsage
        {
            Provider = "antigravity",
            Primary = shortWindow ? window : null,
            Weekly = shortWindow ? null : window,
            Monthly = monthlyWindow,
            PlanLabel = _planLabel,
        };
    }

    private async Task<HttpResponseMessage> SendAsync(string token, string url, string jsonBody)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("User-Agent", "antigravity");
        return await _http.SendAsync(req).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _http.Dispose();
    }
}
