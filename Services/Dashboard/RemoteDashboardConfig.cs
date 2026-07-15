using System.IO;
using System.Text.Json;

namespace DevezCode.Services.Dashboard;

/// <summary>
/// 원격 대시보드(릴레이) 설정. %AppData%\DevezCode\remote.json 에 로컬 저장한다.
/// deviceSecret 은 이 PC 로컬에만 보관하며 릴레이엔 해시만 저장된다.
/// </summary>
public sealed class RemoteDashboardConfig
{
    // 고정 릴레이 호스트(사용자 입력 불필요). 자체 릴레이를 쓰려면 remote.json 에서 교체 가능.
    public const string DefaultRelayBaseUrl = "devezcode-relay.devez.workers.dev";

    public bool Enabled { get; set; }
    public string RelayBaseUrl { get; set; } = DefaultRelayBaseUrl;
    public string DeviceId { get; set; } = "";
    public string DeviceSecret { get; set; } = "";
    public string DeviceName { get; set; } = "";

    public bool IsPaired => !string.IsNullOrWhiteSpace(DeviceId) && !string.IsNullOrWhiteSpace(DeviceSecret)
        && !string.IsNullOrWhiteSpace(RelayBaseUrl);

    /// <summary>PC 아웃바운드 연결용 device WS 엔드포인트.</summary>
    public Uri DeviceWebSocketUri()
    {
        var baseUrl = RelayBaseUrl.Trim().TrimEnd('/');
        if (!baseUrl.Contains("://")) baseUrl = "wss://" + baseUrl;
        else if (baseUrl.StartsWith("https://")) baseUrl = "wss://" + baseUrl["https://".Length..];
        else if (baseUrl.StartsWith("http://")) baseUrl = "ws://" + baseUrl["http://".Length..];
        return new Uri($"{baseUrl}/device/{Uri.EscapeDataString(DeviceId)}");
    }

    /// <summary>페어링 REST 용 https 베이스.</summary>
    public string HttpsBaseUrl()
    {
        var baseUrl = RelayBaseUrl.Trim().TrimEnd('/');
        if (!baseUrl.Contains("://")) return "https://" + baseUrl;
        if (baseUrl.StartsWith("wss://")) return "https://" + baseUrl["wss://".Length..];
        if (baseUrl.StartsWith("ws://")) return "http://" + baseUrl["ws://".Length..];
        return baseUrl;
    }

    private static readonly object _lock = new();
    private static RemoteDashboardConfig? _current;

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "remote.json");

    public static RemoteDashboardConfig Current
    {
        get
        {
            lock (_lock)
            {
                if (_current != null) return _current;
                var text = AtomicFile.ReadValidated(ConfigPath, IsParseable, out _);
                if (text != null)
                {
                    try { _current = JsonSerializer.Deserialize<RemoteDashboardConfig>(text); }
                    catch { /* 손상 시 새로 시작 */ }
                }
                return _current ??= new RemoteDashboardConfig();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            _current = this;
            AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static bool IsParseable(string text)
    {
        try { return JsonSerializer.Deserialize<RemoteDashboardConfig>(text) != null; }
        catch { return false; }
    }
}
