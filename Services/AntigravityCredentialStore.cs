using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>Google Antigravity(Gemini Code Assist) OAuth 자격증명 해석/저장/갱신.
/// 우선순위: ① DevezCode 자체 스토어(설정창 로그인 결과) → ② agy 키링(agy 자신이 로그인·갱신).
/// grok 패턴과 동일 — 자체 토큰이 있으면 그걸 쓰고 앱이 refresh 로 갱신, 없으면 agy 키링을 읽기 전용 사용.
/// <para>키링 블롭은 agy 실측(1.1.1)상 <c>{token:{access_token,refresh_token,expiry},auth_method}</c> 중첩 구조.
/// Windows 자격증명 관리자 <c>LegacyGeneric:target=gemini:antigravity</c>(타입 GENERIC).</para></summary>
public static class AntigravityCredentialStore
{
    private const string CredTarget = "gemini:antigravity";

    public readonly record struct Creds(
        string AccessToken,
        string? RefreshToken,
        long ExpiresMs,
        bool FromOwnStore);

    private static string OwnStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "antigravity-auth.json");

    private static string DisconnectedPath => OwnStorePath + ".disconnected";

    /// <summary>연결 상태 — 자체 토큰 또는 agy 키링 토큰이 있고, 연결 해제 상태가 아니면 true.</summary>
    public static bool IsConnected() => Resolve() != null;

    /// <summary>사용할 자격증명. 자체 스토어 우선, 없으면 키링. 연결 해제 상태면 null.</summary>
    public static Creds? Resolve()
    {
        if (File.Exists(DisconnectedPath)) return null;
        return ReadOwnStore() ?? ReadKeyring();
    }

    /// <summary>하위호환: 기존 호출부가 쓰던 튜플 형태. (access, refresh, expiresMs) 또는 null.</summary>
    public static (string access, string? refresh, long expiresMs)? Read()
    {
        var c = Resolve();
        return c == null ? null : (c.Value.AccessToken, c.Value.RefreshToken, c.Value.ExpiresMs);
    }

    /// <summary>설정창 로그인/refresh 결과를 자체 스토어에 저장.</summary>
    public static void Save(string access, string? refresh, long expiresMs)
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
                if (expiresMs > 0) w.WriteNumber("expiresMs", expiresMs);
                w.WriteEndObject();
            }
            File.WriteAllBytes(OwnStorePath, ms.ToArray());
        }
        catch { /* non-critical */ }
    }

    /// <summary>로그인 성공 시 연결 해제 sentinel 제거.</summary>
    public static void Enable()
    {
        try { if (File.Exists(DisconnectedPath)) File.Delete(DisconnectedPath); } catch { }
    }

    /// <summary>자체 토큰을 지우고 키링 자동 인식도 중지(연결 끊기). agy 키링 자체는 건드리지 않는다.</summary>
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

    /// <summary>자체 스토어가 refresh 원천일 때만 갱신 결과를 다시 기록.
    /// (키링 원천이면 agy 가 관리하므로 앱은 메모리 토큰만 갱신하고 파일은 안 건드림.)</summary>
    public static void SaveRefreshedIfOwn(Creds source, string access, string? refresh, long expiresMs)
    {
        if (source.FromOwnStore)
            Save(access, refresh ?? source.RefreshToken, expiresMs);
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
            long exp = root.TryGetProperty("expiresMs", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
            return new Creds(access!, refresh, exp, FromOwnStore: true);
        }
        catch { return null; }
    }

    private static Creds? ReadKeyring()
    {
        try
        {
            var blob = ReadCredentialBlob(CredTarget);
            if (blob == null || blob.Length == 0) return null;

            var txt = Encoding.UTF8.GetString(blob);
            if (txt.IndexOf('{') < 0) txt = Encoding.Unicode.GetString(blob);
            var brace = txt.IndexOf('{');
            if (brace < 0) return null;
            if (brace > 0) txt = txt.Substring(brace); // 선행 BOM/잡문자 제거

            using var doc = JsonDocument.Parse(txt);
            // agy 실측: 토큰 필드가 "token" 객체 안에 중첩. 평평한 구조면 root 그대로.
            var tok = doc.RootElement.TryGetProperty("token", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested : doc.RootElement;

            string? access = FirstString(tok, "access_token", "accessToken", "token", "id_token");
            if (string.IsNullOrEmpty(access)) return null;
            string? refresh = FirstString(tok, "refresh_token", "refreshToken");
            long exp = ReadExpiryMs(tok);
            return new Creds(access!, refresh, exp, FromOwnStore: false);
        }
        catch { return null; }
    }

    // 만료 시각(unix ms) 추출 — 후보 필드별 단위(ms/sec/ISO) 휴리스틱.
    private static long ReadExpiryMs(JsonElement root)
    {
        if (root.TryGetProperty("expiry_date", out var ed) && ed.ValueKind == JsonValueKind.Number)
            return ed.GetInt64();
        foreach (var k in new[] { "expires_at", "expiresAt", "expiry", "expires" })
        {
            if (!root.TryGetProperty(k, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number)
            {
                var n = v.GetInt64();
                return n > 4_000_000_000L ? n : n * 1000; // 10자리=초 → ms
            }
            if (v.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(v.GetString(), out var dt))
                return dt.ToUnixTimeMilliseconds();
        }
        return 0;
    }

    private static string? FirstString(JsonElement root, params string[] keys)
    {
        foreach (var k in keys)
            if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    // ── Windows Credential Manager (advapi32 CredRead, GENERIC) ──
    private static byte[]? ReadCredentialBlob(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var handle)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (c.CredentialBlobSize == 0 || c.CredentialBlob == IntPtr.Zero) return null;
            var b = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, b, 0, c.CredentialBlobSize);
            return b;
        }
        finally { CredFree(handle); }
    }

    private const int CRED_TYPE_GENERIC = 1;

    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32")]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    // ── OAuth 상수 (agy 1.1.1 바이너리 실측 — 설정창 로그인용) ──
    // 주의: client_id·secret·redirect·scope 조합은 실제 Google 로그인으로만 최종 확정된다.
    // 로그인 실패해도 위 키링 폴백으로 기존 동작은 유지된다(회귀 없음).
    public const string OAuthClientId = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
    public const string OAuthClientSecret = "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";
    public const string OAuthAuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string OAuthTokenUrl = "https://oauth2.googleapis.com/token";
    // cloud-platform 스코프면 loadCodeAssist/fetchAvailableModels 호출권이 포함된다(code-assist 표준).
    public const string OAuthScope = "https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile openid";
}
