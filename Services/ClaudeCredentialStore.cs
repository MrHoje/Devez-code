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
    private static readonly object Sync = new();

    /// <summary>저장된 토큰을 읽는다. 없거나 access 가 없으면 null. expiresMs=0 이면 만료 정보 없음.</summary>
    public static (string access, string? refresh, long expiresMs)? Read()
    {
        lock (Sync) return ReadCore();
    }

    /// <summary>OAuth 토큰을 저장. expiresMs 는 만료 시각(unix epoch 밀리초).</summary>
    public static void Save(string access, string? refresh, long expiresMs)
    {
        lock (Sync) SaveCore(access, refresh, expiresMs);
    }

    /// <summary>refresh 요청을 시작할 때 읽은 자격증명이 아직 그대로일 때만 갱신한다.</summary>
    public static bool TrySaveIfCurrent(
        string expectedAccess, string? expectedRefresh,
        string access, string? refresh, long expiresMs)
    {
        lock (Sync)
        {
            var current = ReadCore();
            if (current is not { } value
                || !string.Equals(value.access, expectedAccess, StringComparison.Ordinal)
                || !string.Equals(value.refresh, expectedRefresh, StringComparison.Ordinal))
                return false;
            SaveCore(access, refresh, expiresMs);
            return true;
        }
    }

    private static (string access, string? refresh, long expiresMs)? ReadCore()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            using var fs = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = System.Text.Json.JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o)) return null;
            var access = o.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            var refresh = o.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null;
            var expires = o.TryGetProperty("expiresAt", out var ex)
                && ex.ValueKind == System.Text.Json.JsonValueKind.Number ? ex.GetInt64() : 0;
            return (access, refresh, expires);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCore(string access, string? refresh, long expiresMs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            using var ms = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("claudeAiOauth");
                writer.WriteString("accessToken", access);
                if (!string.IsNullOrEmpty(refresh)) writer.WriteString("refreshToken", refresh);
                writer.WriteNumber("expiresAt", expiresMs);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            File.WriteAllBytes(StorePath, ms.ToArray());
        }
        catch { }
    }
}
