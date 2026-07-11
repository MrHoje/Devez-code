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

    private static string DisconnectedPath => StorePath + ".disconnected";
    private static readonly object Sync = new();

    public static bool IsDisconnected() => File.Exists(DisconnectedPath);

    public static void Enable()
    {
        lock (Sync)
        {
            try { if (File.Exists(DisconnectedPath)) File.Delete(DisconnectedPath); } catch { }
        }
    }

    /// <summary>DevezCode 자체 토큰을 지우고 외부 opencode 토큰 자동 인식도 중지한다.</summary>
    public static void Disconnect()
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DisconnectedPath)!);
                File.WriteAllText(DisconnectedPath, "");
                if (File.Exists(StorePath)) File.Delete(StorePath);
            }
            catch { }
        }
    }

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

    /// <summary>refresh 시작 시의 자격증명이 유지되고 연결 해제되지 않았을 때만 갱신한다.</summary>
    public static bool TrySaveIfCurrent(
        string expectedAccess, string? expectedRefresh,
        string access, string? refresh, long expiresMs)
    {
        lock (Sync)
        {
            if (IsDisconnected()) return false;
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
            if (IsDisconnected() || !File.Exists(StorePath)) return null;
            using var fs = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("openai", out var o)) return null;
            var access = o.TryGetProperty("access", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            var refresh = o.TryGetProperty("refresh", out var rt) ? rt.GetString() : null;
            var expires = o.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.Number
                ? ex.GetInt64() : 0;
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
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("openai");
                writer.WriteString("type", "oauth");
                writer.WriteString("access", access);
                if (!string.IsNullOrEmpty(refresh)) writer.WriteString("refresh", refresh);
                writer.WriteNumber("expires", expiresMs);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            File.WriteAllBytes(StorePath, ms.ToArray());
        }
        catch { }
    }
}
