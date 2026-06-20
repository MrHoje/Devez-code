using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace DevezCode.Services;

/// <summary>opencode room-tracker 플러그인 (Resources\Plugins\opencode-room-tracker.js) 을
/// 사용자 머신에 설치. ~/.config\opencode\plugin\devezcode-room-tracker.js (XDG_CONFIG_HOME 우선).
/// 매 시작 시 항상 최신본으로 덮어써 자동 업데이트.</summary>
public static class OpenCodePluginInstaller
{
    public static string PluginInstallPath
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var configHome = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            // opencode 1.17.x 는 plugin/ (단수). 공식 docs 는 plugins/ (복수) 표기이지만 단수도 동작.
            return Path.Combine(configHome, "opencode", "plugin", "devezcode-room-tracker.js");
        }
    }

    public static string SessionTrackDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "opencode", "sessions");

    /// <summary>임베디드/소스에서 플러그인 스크립트 본문 읽기.</summary>
    public static string ReadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        var match = names.FirstOrDefault(n => n.EndsWith("opencode-room-tracker.js", StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            using var s = asm.GetManifestResourceStream(match)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        // 폴백: 소스/Content 경로
        var src = Path.Combine(AppContext.BaseDirectory, "Resources", "Plugins", "opencode-room-tracker.js");
        if (File.Exists(src)) return File.ReadAllText(src);
        return File.ReadAllText(PluginInstallPath);
    }

    /// <summary>플러그인을 PluginInstallPath 에 기록 (항상 최신 유지).</summary>
    public static void EnsureInstalled()
    {
        try
        {
            var dir = Path.GetDirectoryName(PluginInstallPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Directory.CreateDirectory(SessionTrackDir);

            var content = ReadEmbeddedScript();
            if (!File.Exists(PluginInstallPath) || File.ReadAllText(PluginInstallPath) != content)
                File.WriteAllText(PluginInstallPath, content, new UTF8Encoding(false));
        }
        catch { /* 권한 부족 등 — 무시 (opencode 는 플러그인 없이도 동작) */ }
    }

    /// <summary>특정 방의 최신 opencode session_id. 플러그인이 %APPDATA%\DevezCode\opencode\sessions\<room>.txt 에 기록한 값.</summary>
    public static string? LoadTrackedSessionId(string roomId)
    {
        try
        {
            var path = Path.Combine(SessionTrackDir, SafeRoomFileName(roomId) + ".txt");
            if (!File.Exists(path)) return null;
            var id = File.ReadAllText(path).Trim();
            return id.StartsWith("ses_", StringComparison.Ordinal) && id.Length > 4 ? id : null;
        }
        catch { return null; }
    }

    private static string SafeRoomFileName(string roomId)
        => System.Text.RegularExpressions.Regex.Replace(roomId, @"[^\w\-]", "");
}
