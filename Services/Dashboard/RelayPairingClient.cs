using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DevezCode.Services.Dashboard;

/// <summary>Google OAuth 승인을 거치는 원격 PC 페어링 API 클라이언트.</summary>
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
        [JsonPropertyName("status")] public string Status { get; set; } = "pending";
        [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
        [JsonPropertyName("deviceSecret")] public string? DeviceSecret { get; set; }
    }

    public static async Task<StartResult> StartAsync(string httpsBaseUrl, string deviceName, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync(
            $"{httpsBaseUrl.TrimEnd('/')}/api/pair/start", new { deviceName }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StartResult>(cancellationToken: ct)
            ?? throw new InvalidOperationException("페어링 응답이 비어 있습니다.");
    }

    public static async Task<PollResult> PollAsync(string httpsBaseUrl, string userCode, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync(
            $"{httpsBaseUrl.TrimEnd('/')}/api/pair/poll", new { userCode }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PollResult>(cancellationToken: ct)
            ?? throw new InvalidOperationException("페어링 응답이 비어 있습니다.");
    }
}
