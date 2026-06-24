using System.IO;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>DevezCode 가 직접 ChatGPT(Codex) OAuth 로그인으로 받은 토큰을 저장한다.
/// opencode 의 auth.json 과 같은 모양(<c>{ "openai": { type:"oauth", access, refresh, expires } }</c>)이라
/// <see cref="CodexUsageService"/> 가 그대로 읽는다. opencode 미설치 사용자도 DevezCode 안에서 연결 가능.</summary>
public static class CodexCredentialStore
{
    public static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "codex-auth.json");

    /// <summary>저장된 토큰을 읽는다. 없거나 access 가 없으면 null. expiresMs=0 이면 만료 정보 없음.</summary>
    public static (string access, string? refresh, long expiresMs)? Read()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            using var fs = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("openai", out var o)) return null;
            var a = o.TryGetProperty("access", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(a)) return null;
            var r = o.TryGetProperty("refresh", out var rt) ? rt.GetString() : null;
            var e = o.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.Number
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
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteStartObject("openai");
                w.WriteString("type", "oauth");
                w.WriteString("access", access);
                if (!string.IsNullOrEmpty(refresh)) w.WriteString("refresh", refresh);
                w.WriteNumber("expires", expiresMs);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            File.WriteAllBytes(StorePath, ms.ToArray());
        }
        catch { }
    }
}
