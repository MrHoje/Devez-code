using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DevezCode.Services;

/// <summary>Google Antigravity(Gemini Code Assist) 의 OAuth 토큰을 읽는다.
/// Antigravity CLI(<c>agy</c>)/IDE 는 토큰을 디스크 평문이 아니라 Windows 자격증명 관리자
/// (go-keyring → <c>LegacyGeneric:target=gemini:antigravity</c>, 타입 GENERIC)에 저장한다.
/// 블롭은 UTF-8 JSON(필드명은 access_token/refresh_token/expiry 계열). 메모리에서만 사용한다.
/// 쓰기는 하지 않는다 — Antigravity 가 관리하는 자격증명을 덮어쓰지 않는다.</summary>
public static class AntigravityCredentialStore
{
    private const string CredTarget = "gemini:antigravity";

    /// <summary>저장된 토큰. 없거나 access 가 없으면 null. expiresMs=0 이면 만료 정보 없음.</summary>
    public static (string access, string? refresh, long expiresMs)? Read()
    {
        try
        {
            var blob = ReadCredentialBlob(CredTarget);
            if (blob == null || blob.Length == 0) return null;

            // go-keyring 은 UTF-8. BOM/UTF-16 대비 폴백.
            var txt = Encoding.UTF8.GetString(blob);
            if (txt.IndexOf('{') < 0) txt = Encoding.Unicode.GetString(blob);
            var brace = txt.IndexOf('{');
            if (brace < 0) return null;
            if (brace > 0) txt = txt.Substring(brace); // 선행 BOM/잡문자 제거

            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;

            string? access = FirstString(root, "access_token", "accessToken", "token", "id_token");
            if (string.IsNullOrEmpty(access)) return null;
            string? refresh = FirstString(root, "refresh_token", "refreshToken");
            long exp = ReadExpiryMs(root);
            return (access!, refresh, exp);
        }
        catch { return null; }
    }

    /// <summary>Antigravity OAuth 토큰이 있어 연결된 상태인지.</summary>
    public static bool IsConnected() => Read() != null;

    // 만료 시각(unix ms) 추출 — 후보 필드별 단위(ms/sec) 휴리스틱.
    private static long ReadExpiryMs(JsonElement root)
    {
        // expiry_date(ms), expires_at(sec 또는 ISO), expiry(ISO 문자열) 순으로 시도.
        if (root.TryGetProperty("expiry_date", out var ed) && ed.ValueKind == JsonValueKind.Number)
            return ed.GetInt64(); // gemini-cli 계열은 ms
        foreach (var k in new[] { "expires_at", "expiresAt", "expiry", "expires" })
        {
            if (!root.TryGetProperty(k, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number)
            {
                var n = v.GetInt64();
                return n > 4_000_000_000L ? n : n * 1000; // 10자리=초 → ms 변환
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
}
