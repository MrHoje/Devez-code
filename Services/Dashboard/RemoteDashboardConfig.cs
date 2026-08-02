using System.IO;
using System.Text.Json;

namespace DevezCode.Services.Dashboard;

/// <summary>원격 대시보드 릴레이 설정. deviceSecret은 이 PC에만 저장한다.</summary>
public sealed class RemoteDashboardConfig
{
    public const string DefaultRelayBaseUrl = "devezcode-relay.devez.workers.dev";

    public bool Enabled { get; set; }
    public string RelayBaseUrl { get; set; } = DefaultRelayBaseUrl;
    public string DeviceId { get; set; } = "";
    public string DeviceSecret { get; set; } = "";
    public string DeviceName { get; set; } = "";

    public bool IsPaired => !string.IsNullOrWhiteSpace(DeviceId)
        && !string.IsNullOrWhiteSpace(DeviceSecret)
        && !string.IsNullOrWhiteSpace(RelayBaseUrl);

    public Uri DeviceWebSocketUri()
    {
        var baseUrl = RelayBaseUrl.Trim().TrimEnd('/');
        if (!baseUrl.Contains("://")) baseUrl = "wss://" + baseUrl;
        else if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "wss://" + baseUrl["https://".Length..];
        else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "ws://" + baseUrl["http://".Length..];
        return new Uri($"{baseUrl}/device/{Uri.EscapeDataString(DeviceId)}");
    }

    public string HttpsBaseUrl()
    {
        var baseUrl = RelayBaseUrl.Trim().TrimEnd('/');
        if (!baseUrl.Contains("://")) return "https://" + baseUrl;
        if (baseUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return "https://" + baseUrl["wss://".Length..];
        if (baseUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return "http://" + baseUrl["ws://".Length..];
        return baseUrl;
    }

    private static readonly object Sync = new();
    private static RemoteDashboardConfig? _current;
    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "remote.json");

    public static RemoteDashboardConfig Current
    {
        get
        {
            lock (Sync)
            {
                if (_current != null) return _current;
                var text = AtomicFile.ReadValidated(ConfigPath, IsParseable, out _);
                if (text != null)
                {
                    try { _current = JsonSerializer.Deserialize<RemoteDashboardConfig>(text); }
                    catch { }
                }
                return _current ??= new RemoteDashboardConfig();
            }
        }
    }

    public void Save()
    {
        lock (Sync)
        {
            _current = this;
            AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static bool IsParseable(string text)
    {
        try { return JsonSerializer.Deserialize<RemoteDashboardConfig>(text) != null; }
        catch { return false; }
    }
}
