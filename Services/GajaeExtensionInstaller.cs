using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace DevezCode.Services;

/// <summary>가재코드(gjc) room-tracker 확장 (Resources\Plugins\gajae-room-tracker.js) 을 사용자 머신에 설치.
/// gjc auto-discovery 디렉터리(~/.gjc/agent/extensions/)에 복사 — gjc 가 인터랙티브 시작 시 자동 로드한다.
/// (`-e`/`--extension` 플래그는 인터랙티브에서 값이 MESSAGES 로 새어 경로가 자동 전송되는 문제가 있어 미사용.
///  auto-discovery 가 README 가 안내하는 표준 방식.)
/// 사용자 자신의 gjc 세션도 이 확장을 로드하지만, DEVEZCODE_ROOM_ID 가 없으면 즉시 no-op 이라 무해.
/// 매 시작 시 최신본으로 덮어써 자동 업데이트. busy\&lt;room&gt;.txt·lastmsg\&lt;room&gt;.txt·todos\&lt;room&gt;.json 을
/// 떨궈 앱의 GajaeBusyService / GajaeLastMessageService / TaskTrackingService 가 읽는다.</summary>
public static class GajaeExtensionInstaller
{
    /// <summary>gjc 글로벌 확장 auto-discovery 경로. (~/.gjc/agent/extensions/devezcode-room-tracker.js)</summary>
    public static string ExtensionInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gjc", "agent", "extensions", "devezcode-room-tracker.js");

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
