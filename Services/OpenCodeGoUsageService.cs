using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>opencode-go(opencode Zen) 대시보드를 폴링해 사용량을 파싱한다(정식 API 없음 → HTML 스크래핑).
/// @slkiser/opencode-quota 의 opencode-go.js 와 동일하게 SolidJS SSR / data-slot 두 포맷을 시도.
/// rolling→Primary, weekly→Weekly, monthly→Monthly. 쿠키 만료(401/403)면 Error 로 재로그인 신호.</summary>
public sealed class OpenCodeGoUsageService : IDisposable
{
    private const string UrlPrefix = "https://opencode.ai/workspace/";
    private const string UrlSuffix = "/go";
    private const string FirefoxUA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Gecko/20100101 Firefox/148.0";
    private const int PollMs = 3 * 60 * 1000;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private System.Threading.Timer? _poll;

    public event Action<ProviderUsage>? Updated;

    public void Start() => _poll = new System.Threading.Timer(_ => _ = PollAsync(), null, 0, PollMs);

    /// <summary>지금 즉시 1회 폴링(로그인 직후 갱신용).</summary>
    public void RefreshNow() => _ = PollAsync();

    private async Task PollAsync()
    {
        try
        {
            if (OpenCodeGoCredentialStore.Resolve() is not { } creds) return; // 미연결 — 직전 값 유지

            var url = UrlPrefix + Uri.EscapeDataString(creds.WorkspaceId) + UrlSuffix;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", FirefoxUA);
            req.Headers.TryAddWithoutValidation("Accept", "text/html");
            req.Headers.TryAddWithoutValidation("Cookie", "auth=" + creds.AuthCookie);

            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Updated?.Invoke(new ProviderUsage { Provider = "opencode-go", Error = "세션 만료 — 클릭하여 재로그인" });
                return;
            }
            if (!res.IsSuccessStatusCode) return;

            var html = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            var now = DateTimeOffset.Now;

            var rolling = ParseSsr(html, "rollingUsage") ?? ParseDataSlot(html, "rolling");
            var weekly  = ParseSsr(html, "weeklyUsage")  ?? ParseDataSlot(html, "weekly");
            var monthly = ParseSsr(html, "monthlyUsage") ?? ParseDataSlot(html, "monthly");
            if (rolling == null && weekly == null && monthly == null) return;

            Updated?.Invoke(new ProviderUsage
            {
                Provider = "opencode-go",
                Primary  = ToWindow(rolling, now),
                Weekly   = ToWindow(weekly, now),
                Monthly  = ToWindow(monthly, now),
            });
        }
        catch { /* 일시 오류 — 직전 값 유지 */ }
    }

    private static UsageWindow? ToWindow((double pct, double resetSec)? w, DateTimeOffset now)
        => w is { } v ? new UsageWindow { UsedPercent = Math.Max(0, v.pct), ResetsAt = now.AddSeconds(Math.Max(0, v.resetSec)) } : null;

    // ── SolidJS SSR 포맷: rollingUsage:$R[n]={...usagePercent:..,resetInSec:..} (필드 순서 양쪽 시도) ──
    private static (double pct, double resetSec)? ParseSsr(string html, string key)
    {
        const string N = @"(-?\d+(?:\.\d+)?)";
        var pctFirst = Regex.Match(html, key + @":\$R\[\d+\]=\{[^}]*usagePercent:" + N + @"[^}]*resetInSec:" + N + @"[^}]*\}");
        if (pctFirst.Success && TryNum(pctFirst, 1, out var p1) && TryNum(pctFirst, 2, out var r1)) return (p1, r1);
        var resetFirst = Regex.Match(html, key + @":\$R\[\d+\]=\{[^}]*resetInSec:" + N + @"[^}]*usagePercent:" + N + @"[^}]*\}");
        if (resetFirst.Success && TryNum(resetFirst, 1, out var r2) && TryNum(resetFirst, 2, out var p2)) return (p2, r2);
        return null;
    }

    // ── data-slot 포맷: data-slot="usage-item" 블록별 라벨/값/리셋 추출 ──
    private static (double pct, double resetSec)? ParseDataSlot(string html, string windowKey)
    {
        var items = Regex.Split(html, "data-slot=\"usage-item\"");
        for (int i = 1; i < items.Length; i++)
        {
            var c = items[i];
            var labelM = Regex.Match(c, "data-slot=\"usage-label\">([^<]+)<");
            if (!labelM.Success) continue;
            var label = labelM.Groups[1].Value.Trim().ToLowerInvariant();
            string? key = label.Contains("rolling") ? "rolling"
                        : label.Contains("weekly") ? "weekly"
                        : label.Contains("monthly") ? "monthly" : null;
            if (key != windowKey) continue;

            var usageM = Regex.Match(c, @"data-slot=""usage-value"">[^0-9]*(\d+(?:\.\d+)?)");
            if (!usageM.Success || !TryNum(usageM, 1, out var pct)) continue;

            var resetM = Regex.Match(c, "data-slot=\"(reset-time|reset-now)\">([\\s\\S]*?)</span>");
            if (!resetM.Success) continue;
            double? resetSec = resetM.Groups[1].Value == "reset-now" ? 0
                : ParseHumanTime(Regex.Replace(resetM.Groups[2].Value, "<!--\\$-->|<!--/-->", "")
                                       .Replace("Resets in", "", StringComparison.OrdinalIgnoreCase).Trim());
            if (resetSec is { } rs) return (pct, rs);
        }
        return null;
    }

    /// <summary>"6 days 2 hours" / "26 분" 같은 사람용 시간 문자열 → 초. 못 읽으면 null.</summary>
    private static double? ParseHumanTime(string s)
    {
        s = s.ToLowerInvariant().Trim();
        if (s is "now" or "reset now" or "reset-now" or "resets now") return 0;
        double total = 0; bool any = false;
        foreach (var (rx, mult) in new (string, double)[] { (@"(\d+(?:\.\d+)?)\s*days?", 86400), (@"(\d+(?:\.\d+)?)\s*hours?", 3600), (@"(\d+(?:\.\d+)?)\s*minutes?", 60), (@"(\d+(?:\.\d+)?)\s*seconds?", 1) })
        {
            var m = Regex.Match(s, rx);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v)) { total += v * mult; any = true; }
        }
        return any ? total : null;
    }

    private static bool TryNum(Match m, int g, out double v)
        => double.TryParse(m.Groups[g].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v);

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _http.Dispose();
    }
}
