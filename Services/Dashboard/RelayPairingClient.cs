using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DevezCode.Services.Dashboard;

/// <summary>
/// OAuth device-flow 유사 페어링. 앱이 릴레이에 페어링을 시작해 userCode/verificationUrl 을 받고,
/// 사용자가 브라우저에서 OAuth 로그인 후 코드를 승인하면 폴링으로 deviceId/deviceSecret 을 수령한다.
/// (스펙 4.2)
/// </summary>
public sealed class RelayPairingClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed class StartResult
    {
        [JsonPropertyName("userCode")] public string UserCode { get; set; } = "";
        [JsonPropertyName("verificationUrl")] public string VerificationUrl { get; set; } = "";
        [JsonPropertyName("expiresIn")] public int ExpiresInSeconds { get; set; } = 600;
        [JsonPropertyName("intervalSeconds")] public int IntervalSeconds { get; set; } = 3;
    }

    public sealed class PollResult
    {
        // pending | approved | denied | expired
        [JsonPropertyName("status")] public string Status { get; set; } = "pending";
        [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
        [JsonPropertyName("deviceSecret")] public string? DeviceSecret { get; set; }
    }

    public static async Task<StartResult> StartAsync(string httpsBaseUrl, string deviceName, CancellationToken ct)
    {
        var resp = await Http.PostAsJsonAsync($"{httpsBaseUrl.TrimEnd('/')}/api/pair/start", new { deviceName }, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<StartResult>(cancellationToken: ct)
               ?? throw new InvalidOperationException("페어링 응답이 비어 있습니다.");
    }

    public static async Task<PollResult> PollAsync(string httpsBaseUrl, string userCode, CancellationToken ct)
    {
        var resp = await Http.PostAsJsonAsync($"{httpsBaseUrl.TrimEnd('/')}/api/pair/poll", new { userCode }, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<PollResult>(cancellationToken: ct)
               ?? throw new InvalidOperationException("페어링 응답이 비어 있습니다.");
    }

    /// <summary>승인될 때까지 폴링. 성공 시 config 에 저장하고 true 반환.</summary>
    public static async Task<bool> PairAsync(string relayBaseUrl, string deviceName, Action<StartResult> onStarted, CancellationToken ct)
    {
        var cfg = new RemoteDashboardConfig { RelayBaseUrl = relayBaseUrl };
        var httpsBase = cfg.HttpsBaseUrl();
        var start = await StartAsync(httpsBase, deviceName, ct);
        onStarted(start);

        var deadline = DateTime.UtcNow.AddSeconds(start.ExpiresInSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(2, start.IntervalSeconds));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);
            var poll = await PollAsync(httpsBase, start.UserCode, ct);
            switch (poll.Status)
            {
                case "approved" when !string.IsNullOrWhiteSpace(poll.DeviceId) && !string.IsNullOrWhiteSpace(poll.DeviceSecret):
                    var saved = RemoteDashboardConfig.Current;
                    saved.RelayBaseUrl = relayBaseUrl;
                    saved.DeviceId = poll.DeviceId!;
                    saved.DeviceSecret = poll.DeviceSecret!;
                    saved.DeviceName = deviceName;
                    saved.Enabled = true;
                    saved.Save();
                    return true;
                case "denied":
                case "expired":
                    return false;
            }
        }
        return false;
    }
}
