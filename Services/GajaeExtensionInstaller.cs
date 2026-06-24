using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace DevezCode.Services;

/// <summary>가재코드(gjc) room-tracker 확장 (Resources\Plugins\gajae-room-tracker.js) 을 사용자 머신에 설치.
/// %LOCALAPPDATA%\DevezCode\gajae\ext\gajae-room-tracker.js 로 복사하고, launch 배치가 `gjc -e <path>` 로 로드.
/// (gjc 전역 auto-discovery 디렉터리 대신 우리 전용 경로 → 사용자 자신의 gjc 세션은 오염되지 않음)
/// 매 시작 시 최신본으로 덮어써 자동 업데이트. busy\&lt;room&gt;.txt·todos\&lt;room&gt;.json 을 떨궈
/// 앱의 GajaeBusyService / TaskTrackingService 가 읽는다.</summary>
public static class GajaeExtensionInstaller
{
    public static string ExtensionInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevezCode", "gajae", "ext", "gajae-room-tracker.js");

    private static string ReadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var match = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("gajae-room-tracker.js", StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            using var s = asm.GetManifestResourceStream(match)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        var src = Path.Combine(AppContext.BaseDirectory, "Resources", "Plugins", "gajae-room-tracker.js");
        if (File.Exists(src)) return File.ReadAllText(src);
        return File.Exists(ExtensionInstallPath) ? File.ReadAllText(ExtensionInstallPath) : "";
    }

    /// <summary>확장을 ExtensionInstallPath 에 기록(항상 최신 유지). 실패해도 gjc 는 확장 없이 동작.</summary>
    public static void EnsureInstalled()
    {
        try
        {
            var dir = Path.GetDirectoryName(ExtensionInstallPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var content = ReadEmbeddedScript();
            if (string.IsNullOrEmpty(content)) return;
            if (!File.Exists(ExtensionInstallPath) || File.ReadAllText(ExtensionInstallPath) != content)
                File.WriteAllText(ExtensionInstallPath, content, new UTF8Encoding(false));
        }
        catch { /* 권한 부족 등 — 무시 */ }
    }
}
