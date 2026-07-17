using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>Kimi Code(managed:kimi-code) OAuth 토큰 읽기.
/// kimi CLI 가 평소 실행 중 백그라운드로 토큰을 갱신하므로 DevezCode 는 refresh 를 시도하지 않고
/// 디스크의 현재 토큰만 읽는다(잘못된 refresh URL 추측 회피 — 만료 시 kimi 실행 후 자동 신선화).
/// 토큰 위치: ① $KIMI_CODE_HOME/credentials/kimi-code.json ② 레거시 ~/.kimi/credentials/kimi-code.json.
/// flat 스키마: {access_token, refresh_token, expires_at, scope, token_type, expires_in}.</summary>
public static class KimiCredentialStore
{
    public readonly record struct Creds(string AccessToken, DateTimeOffset? ExpiresAt, string SourcePath);

    private static string DisconnectedPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "kimi-auth.json.disconnected");

    private static IEnumerable<string> TokenPaths()
    {
        var configured = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        var home = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        yield return Path.Combine(home, "credentials", "kimi-code.json");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".kimi", "credentials", "kimi-code.json");
    }

    public static bool IsConnected() => Resolve() != null;
    public static string? ReadAccessToken() => Resolve()?.AccessToken;

    public static Creds? Resolve()
    {
        if (File.Exists(DisconnectedPath)) return null;
        foreach (var p in TokenPaths())
        {
            var c = ReadFile(p);
            if (c != null) return c;
        }
        return null;
    }

    private static Creds? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            DateTimeOffset? exp = null;
            if (root.TryGetProperty("expires_at", out var e))
            {
                if (e.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(e.GetString(), out var dt)) exp = dt;
                else if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var epoch))
                    exp = DateTimeOffset.FromUnixTimeSeconds(epoch > 9999999999 ? epoch / 1000 : epoch);
            }
            return new Creds(access!, exp, path);
        }
        catch { return null; }
    }

    public static void Disconnect()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DisconnectedPath)!);
            File.WriteAllText(DisconnectedPath, "");
        }
        catch { }
    }

    public static void Enable()
    {
        try { if (File.Exists(DisconnectedPath)) File.Delete(DisconnectedPath); } catch { }
    }
}
