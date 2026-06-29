using System.IO;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>DeepSeek API 키를 로컬에 저장/읽기.
/// codex-auth.json 과 달리 OAuth 토큰이 아니라 평문 API 키 하나만 저장한다.
/// %AppData%\DevezCode\deepseek-auth.json 에 {"apiKey":"sk-..."} 형태로 저장.</summary>
public static class DeepSeekCredentialStore
{
    private sealed class Store
    {
        public string? ApiKey { get; set; }
    }

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "deepseek-auth.json");

    private static Store? _cache;

    private static Store Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(StorePath))
            {
                var text = File.ReadAllText(StorePath);
                _cache = JsonSerializer.Deserialize<Store>(text) ?? new Store();
                return _cache;
            }
        }
        catch { /* 손상 시 새로 시작 */ }
        return _cache = new Store();
    }

    private static void SaveStore()
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_cache ?? new Store()));
        }
        catch { /* non-critical */ }
    }

    /// <summary>저장된 API 키. 없으면 null.</summary>
    public static string? ReadApiKey()
    {
        var s = Load();
        var key = s.ApiKey?.Trim();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>API 키 저장. null/빈 값이면 파일을 삭제한다.</summary>
    public static void SaveApiKey(string? apiKey)
    {
        var key = apiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            _cache = new Store();
            try { if (File.Exists(StorePath)) File.Delete(StorePath); } catch { }
            return;
        }
        _cache = new Store { ApiKey = key };
        SaveStore();
    }

    /// <summary>API 키가 저장되어 있는지 여부.</summary>
    public static bool IsConnected() => ReadApiKey() != null;
}
