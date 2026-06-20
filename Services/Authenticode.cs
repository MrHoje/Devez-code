using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace DevezCode.Services;

/// <summary>
/// 자동 업데이트로 받은 exe 의 Authenticode 서명을 검증한다.
/// 핵심: SHA256(version.json 기반)은 전송 오류는 잡아도 진위(공급망 변조)는 보장하지 못한다.
/// 코드서명은 MS 루트 CA + 게시자라는 독립 신뢰 앵커이므로, 서명된 빌드에서는 이게 마지막 방어선이다.
/// </summary>
public static class Authenticode
{
    /// <summary>
    /// 가능하면 서명을 강제한다.
    /// 현재 실행 파일이 서명돼 있으면(배포 빌드) → 새 exe 도 WinVerifyTrust 통과 + 동일 게시자여야 통과.
    /// 현재 실행 파일이 미서명이면(개발/미서명 빌드) → 비교 기준이 없으므로 검증을 생략(true)한다.
    /// 이렇게 하면 미서명 환경에서 자동 업데이트가 깨지지 않으면서, 서명 인프라를 갖추는 즉시 강제된다.
    /// </summary>
    public static bool VerifyMatchesCurrent(string newExe, out string reason)
    {
        reason = "";
        var current = Environment.ProcessPath;
        var currentThumb = current is null ? null : TryGetSignerThumbprint(current);
        if (currentThumb is null) { reason = "현재 빌드가 미서명이라 서명 검증을 생략합니다."; return true; }

        if (!IsTrusted(newExe)) { reason = "새 파일의 코드서명을 신뢰할 수 없습니다."; return false; }

        var newThumb = TryGetSignerThumbprint(newExe);
        if (!string.Equals(newThumb, currentThumb, StringComparison.OrdinalIgnoreCase))
        {
            reason = "새 파일의 게시자가 현재 빌드와 다릅니다.";
            return false;
        }
        return true;
    }

    /// <summary>서명자(leaf) 인증서 thumbprint. 서명이 없으면 null.</summary>
    private static string? TryGetSignerThumbprint(string path)
    {
        try
        {
            // CreateFromSignedFile 은 leaf 인증서만 반환(파일 변조 여부는 검증 안 함) → 진위는 IsTrust 가 담당.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return cert.Thumbprint;
        }
        catch { return null; }
    }

    // ── WinVerifyTrust (wintrust.dll) ───────────────────────────────
    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;

    /// <summary>파일이 유효한 Authenticode 서명을 가지고 신뢰 체인(루트 CA)·해시가 모두 검증되는가.</summary>
    private static bool IsTrusted(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(path),
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };
        var pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, pFile, false);

        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            pFile = pFile,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_SAFER_FLAG,
        };
        var pData = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_DATA>());
        Marshal.StructureToPtr(data, pData, false);
        try
        {
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, pData);

            // 상태 핸들 해제: VERIFY 가 *네이티브 버퍼*에 채운 hWVTStateData 를 읽어와야
            // CLOSE 가 그 핸들을 실제로 해제한다(관리 struct 의 0 을 덮어쓰면 누수).
            data = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, pData, true);
            WinVerifyTrust(IntPtr.Zero, ref action, pData);

            return result == 0; // 0 == 신뢰됨
        }
        catch (DllNotFoundException) { return false; }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(pFile);
            Marshal.FreeCoTaskMem(pData);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
