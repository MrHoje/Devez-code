using System.Net.Http;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>DeepSeek API 잔액을 폴링한다.
/// <c>GET https://api.deepseek.com/user/balance</c> with Bearer token(API key).
/// 응답의 balance_infos(통화별 total/granted/topped_up 잔액)를 <see cref="ProviderUsage.Balances"/> 에 담는다.
/// percent 기반이 아니라 monetary-balance 이므로 Primary/Weekly/Monthly 는 사용하지 않는다.
/// 토큰 없음/401 → Error 신호. 네트워크 오류 시 직전 값 유지.</summary>
public sealed class DeepSeekUsageService : IDisposable
{
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";
    private const int PollMs = 3 * 60 * 1000;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private System.Threading.Timer? _poll;

    public event Action<ProviderUsage>? Updated;

    public void Start() => _poll = new System.Threading.Timer(_ => _ = PollAsync(), null, 0, PollMs);

    /// <summary>지금 즉시 1회 폴링(API 키 저장 직후 갱신용).</summary>
    public void RefreshNow() => _ = PollAsync();

    private async Task PollAsync()
    {
        try
        {
            var apiKey = DeepSeekCredentialStore.ReadApiKey();
            if (apiKey == null) { Updated?.Invoke(new ProviderUsage { Provider = "deepseek" }); return; } // 미연결 → 빈 스냅샷(푸터/패널 숨김)

            using var req = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var res = await _http.SendAsync(req).ConfigureAwait(false);

            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized || res.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Updated?.Invoke(new ProviderUsage { Provider = "deepseek", Error = "API 키가 유효하지 않습니다 — 설정에서 확인하세요" });
                return;
            }
            if (!res.IsSuccessStatusCode) return; // 429/5xx — 직전 값 유지

            await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() == 0)
                return;

            var balances = new List<BalanceInfo>(infos.GetArrayLength());
            foreach (var item in infos.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var currency = item.TryGetProperty("currency", out var c) ? c.GetString() : null;
                var total = item.TryGetProperty("total_balance", out var tb) ? tb.GetString() : null;
                var granted = item.TryGetProperty("granted_balance", out var gb) ? gb.GetString() : null;
                var toppedUp = item.TryGetProperty("topped_up_balance", out var tub) ? tub.GetString() : null;
                if (currency == null || total == null) continue;
                balances.Add(new BalanceInfo
                {
                    Currency = currency,
                    TotalBalance = total,
                    GrantedBalance = granted ?? "0.00",
                    ToppedUpBalance = toppedUp ?? "0.00",
                });
            }
            if (balances.Count == 0) return;

            Updated?.Invoke(new ProviderUsage
            {
                Provider = "deepseek",
                Balances = balances,
            });
        }
        catch (HttpRequestException)
        {
            // 네트워크 오류 — 직전 값 유지
        }
        catch (TaskCanceledException)
        {
            // 타임아웃 — 직전 값 유지
        }
        catch
        {
            // 기타 일시 오류 — 직전 값 유지
        }
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _http.Dispose();
    }
}
