using System.IO;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>Grok Build(xAI CLI) OAuth 자격증명 해석/갱신.
/// 우선순위: ① DevezCode 자체 스토어 → ② <c>~/.grok/auth.json</c>(Grok CLI OIDC).
/// auth.json 키 형태: <c>https://auth.x.ai::&lt;client_id&gt;</c> → key/refresh_token/expires_at.</summary>
public static class GrokCredentialStore
{
    public const string OAuthClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    public const string OAuthTokenUrl = "https://auth.x.ai/oauth2/token";

    public readonly record struct Creds(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        string SourcePath,
        string? EntryKey);

    private static string OwnStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "grok-auth.json");

    private static string CliAuthPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".grok", "auth.json");

    public static bool IsConnected() => Resolve() != null;

    public static Creds? Resolve()
        => ReadOwnStore() ?? ReadCliAuth();

    public static string? ReadAccessToken() => Resolve()?.AccessToken;

    public static void Clear()
    {
        try { if (File.Exists(OwnStorePath)) File.Delete(OwnStorePath); } catch { }
    }

    /// <summary>DevezCode 자체 스토어에 토큰 저장(로그인/refresh 결과).</summary>
    public static void Save(string access, string? refresh, DateTimeOffset? expiresAt)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OwnStorePath)!);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("access", access);
                if (!string.IsNullOrEmpty(refresh)) w.WriteString("refresh", refresh);
                if (expiresAt is DateTimeOffset e)
                    w.WriteString("expiresAt", e.ToUniversalTime().ToString("o"));
                w.WriteEndObject();
            }
            File.WriteAllBytes(OwnStorePath, ms.ToArray());
        }
        catch { /* non-critical */ }
    }

    /// <summary>refresh 성공 시 소스 파일에 반영. CLI auth.json 이면 해당 엔트리 key/refresh/expires_at 갱신.</summary>
    public static void SaveRefreshed(Creds source, string access, string? refresh, DateTimeOffset expiresAt)
    {
        if (string.Equals(source.SourcePath, OwnStorePath, StringComparison.OrdinalIgnoreCase))
        {
            Save(access, refresh ?? source.RefreshToken, expiresAt);
            return;
        }
        try
        {
            if (!File.Exists(source.SourcePath) || string.IsNullOrEmpty(source.EntryKey)) return;
            var text = File.ReadAllText(source.SourcePath);
            using var doc = JsonDocument.Parse(text);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name == source.EntryKey)
                    {
                        w.WritePropertyName(prop.Name);
                        w.WriteStartObject();
                        foreach (var f in prop.Value.EnumerateObject())
                        {
                            if (f.Name is "key") w.WriteString("key", access);
                            else if (f.Name is "refresh_token")
                                w.WriteString("refresh_token", refresh ?? source.RefreshToken ?? f.Value.GetString());
                            else if (f.Name is "expires_at")
                                w.WriteString("expires_at", expiresAt.ToUniversalTime().ToString("o"));
                            else f.WriteTo(w);
                        }
                        // 필드 누락 시 보강
                        if (!prop.Value.TryGetProperty("key", out _)) w.WriteString("key", access);
                        if (!prop.Value.TryGetProperty("refresh_token", out _) && !string.IsNullOrEmpty(refresh))
                            w.WriteString("refresh_token", refresh);
                        if (!prop.Value.TryGetProperty("expires_at", out _))
                            w.WriteString("expires_at", expiresAt.ToUniversalTime().ToString("o"));
                        w.WriteEndObject();
                    }
                    else prop.WriteTo(w);
                }
                w.WriteEndObject();
            }
            File.WriteAllBytes(source.SourcePath, ms.ToArray());
        }
        catch { /* best-effort — 메모리 토큰은 이미 갱신됨 */ }
    }

    private static Creds? ReadOwnStore()
    {
        try
        {
            if (!File.Exists(OwnStorePath)) return null;
            using var fs = new FileStream(OwnStorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            var access = root.TryGetProperty("access", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            var refresh = root.TryGetProperty("refresh", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            DateTimeOffset? exp = null;
            if (root.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(e.GetString(), out var dt))
                exp = dt;
            return new Creds(access!, refresh, exp, OwnStorePath, null);
        }
        catch { return null; }
    }

    private static Creds? ReadCliAuth()
    {
        try
        {
            if (!File.Exists(CliAuthPath)) return null;
            using var fs = new FileStream(CliAuthPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var o = prop.Value;
                if (!o.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) continue;
                var access = keyEl.GetString()?.Trim();
                if (string.IsNullOrEmpty(access)) continue;
                var refresh = o.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                    ? rt.GetString() : null;
                DateTimeOffset? exp = null;
                if (o.TryGetProperty("expires_at", out var ea) && ea.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(ea.GetString(), out var dt))
                    exp = dt;
                return new Creds(access!, refresh, exp, CliAuthPath, prop.Name);
            }
        }
        catch { }
        return null;
    }
}
