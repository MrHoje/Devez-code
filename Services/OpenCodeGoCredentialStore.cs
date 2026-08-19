using System.IO;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>opencode-go(opencode Zen) 대시보드 접근용 자격증명(workspaceId + authCookie) 해석/저장.
/// 우선순위: ① DevezCode 자체 스토어(브라우저 로그인으로 캡처해 저장) → ② opencode-quota 플러그인 파일
/// → ③ 환경변수. 쿠키는 만료되므로 만료 시 브라우저 재로그인으로 ①을 갱신한다.</summary>
public static class OpenCodeGoCredentialStore
{
    public readonly record struct Creds(string WorkspaceId, string AuthCookie);

    // ① DevezCode 자체 스토어
    private static string OwnStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "opencode-go.json");

    private static string DisconnectedPath => OwnStorePath + ".disconnected";

    // ② opencode-quota 플러그인이 깔아둔 파일
    private static string PluginPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "opencode", "opencode-quota", "opencode-go.json");

    /// <summary>opencode-go 자격증명(쿠키/워크스페이스)이 있어 연결된 상태인지.</summary>
    public static bool IsConnected() => Resolve() != null;

    public static Creds? Resolve()
    {
        if (File.Exists(DisconnectedPath)) return null;

        // ① 자체 스토어 → ② 플러그인 파일 → ③ env. env 를 먼저 보면 예전 워크스페이스가 박힌
        // 환경변수가 남아 있을 때 브라우저로 새로 로그인해 저장한 값이 계속 밀려, 엉뚱한
        // 워크스페이스 대시보드를 조회하고 사용량이 비어 보인다.
        if (ReadFile(OwnStorePath) is { } own) return own;
        if (ReadFile(PluginPath) is { } plugin) return plugin;

        // ③ env
        var envWs = Environment.GetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID")?.Trim();
        var envCk = Environment.GetEnvironmentVariable("OPENCODE_GO_AUTH_COOKIE")?.Trim();
        return !string.IsNullOrEmpty(envWs) && !string.IsNullOrEmpty(envCk) ? new Creds(envWs, envCk) : null;
    }

    private static Creds? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            var ws = root.TryGetProperty("workspaceId", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString()?.Trim() : null;
            var ck = root.TryGetProperty("authCookie", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()?.Trim() : null;
            return !string.IsNullOrEmpty(ws) && !string.IsNullOrEmpty(ck) ? new Creds(ws, ck) : null;
        }
        catch { return null; }
    }

    /// <summary>브라우저 로그인으로 캡처한 자격증명을 자체 스토어에 저장.</summary>
    public static void Save(string workspaceId, string authCookie)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OwnStorePath)!);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("workspaceId", workspaceId);
                w.WriteString("authCookie", authCookie);
                w.WriteEndObject();
            }
            File.WriteAllBytes(OwnStorePath, ms.ToArray());
        }
        catch { }
    }

    public static void Enable()
    {
        try { if (File.Exists(DisconnectedPath)) File.Delete(DisconnectedPath); } catch { }
    }

    /// <summary>DevezCode 자체 자격증명을 지우고 플러그인/환경변수 자동 인식도 중지한다.</summary>
    public static void Disconnect()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DisconnectedPath)!);
            File.WriteAllText(DisconnectedPath, "");
            if (File.Exists(OwnStorePath)) File.Delete(OwnStorePath);
        }
        catch { }
    }
}
