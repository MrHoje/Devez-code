using System.IO;

namespace DevezCode.Services;

/// <summary>DevezCode 가 직접 Claude(claude.ai) OAuth 로그인으로 받은 토큰을 저장한다.
/// claude CLI 의 <c>~/.claude/.credentials.json</c> 과 같은 모양(<c>{ "claudeAiOauth": { accessToken, ... } }</c>)이라
/// <see cref="UsageApiService"/> 가 그대로 읽는다. claude CLI 파일은 건드리지 않아(덮어쓰기 위험 회피)
/// claude 미로그인 사용자(opencode 만 쓰는 등)도 DevezCode 안에서 연결할 수 있다.</summary>
public static class ClaudeCredentialStore
{
    public static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "claude-auth.json");

    /// <summary>저장된 토큰을 읽는다. 없거나 access 가 없으면 null. expiresMs=0 이면 만료 정보 없음.</summary>
    public static (string access, string? refresh, long expiresMs)? Read()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            using var fs = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = System.Text.Json.JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o)) return null;
            var a = o.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(a)) return null;
            var r = o.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null;
            var e = o.TryGetProperty("expiresAt", out var ex) && ex.ValueKind == System.Text.Json.JsonValueKind.Number
                ? ex.GetInt64() : 0;
            return (a, r, e);
        }
        catch { return null; }
    }

    /// <summary>OAuth 토큰을 저장. expiresMs 는 만료 시각(unix epoch 밀리초).</summary>
    public static void Save(string access, string? refresh, long expiresMs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            using var ms = new MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteStartObject("claudeAiOauth");
                w.WriteString("accessToken", access);
                if (!string.IsNullOrEmpty(refresh)) w.WriteString("refreshToken", refresh);
                w.WriteNumber("expiresAt", expiresMs);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            File.WriteAllBytes(StorePath, ms.ToArray());
        }
        catch { }
    }
}
