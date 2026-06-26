using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace DevezCode.Services;

/// <summary>opencode room-tracker 플러그인 (Resources\Plugins\opencode-room-tracker.js) 을
/// 사용자 머신에 설치. ~/.config\opencode\plugin\devezcode-room-tracker.js (XDG_CONFIG_HOME 우선).
/// 매 시작 시 항상 최신본으로 덮어써 자동 업데이트.
/// <para>플러그인 추적 외에 <c>opencode session list</c> + <c>opencode export &lt;id&gt;</c> 로
/// workingDir 매칭 세션 ID 를 찾는 백업 경로도 제공 — 플러그인 콜백이 어떤 이유로든
/// 호출되지 않는 경우(버전 비호환 등)에도 같은 디렉터리의 마지막 세션을 복원한다.</para></summary>
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

    /// <summary>백업 경로 — <c>opencode session list</c> + <c>opencode export &lt;id&gt;</c> 로
    /// workingDir 매칭 세션 ID 를 찾는다. 플러그인이 어떤 이유로 기록에 실패한 경우
    /// (콜백 시그니처 변경·환경변수 누락 등) 여기서 같은 디렉터리의 마지막 세션을 복원.
    /// opencode 미설치 / list 비었으면 null.
    /// <para><paramref name="exceptRoomId"/> 지정 시, 그 방을 제외한 다른 방들이 이미 점유 중인 세션 ID 는
    /// 후보에서 제외한다. 같은 폴더에 방을 여러 개 열어 둔 경우 한 세션을 두 방이 가로채(공유/덮어쓰기)는 것을
    /// 막기 위함. (cwd 만으로는 방을 구분 못 하므로, 다른 방 추적파일이 이미 쓰는 세션은 미사용분만 남긴다.)</para></summary>
    public static string? FindSessionIdByCwd(string? workingDir, string? exceptRoomId = null)
    {
        if (string.IsNullOrWhiteSpace(workingDir)) return null;
        try
        {
            var used = LoadOtherRoomSessionIds(exceptRoomId);
            var norm = Path.GetFullPath(workingDir).TrimEnd('\\', '/');
            var listOutput = RunOpenCode("session list");
            if (string.IsNullOrEmpty(listOutput)) return null;
            foreach (var line in listOutput.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("ses_")) continue;
                var idEnd = trimmed.IndexOfAny(new[] { ' ', '\t' });
                if (idEnd <= 0) continue;
                var sid = trimmed.Substring(0, idEnd);
                if (used.Contains(sid)) continue; // 다른 방이 이미 쓰는 세션 — 가로채지 않음
                var exported = RunOpenCode($"export {sid}");
                if (string.IsNullOrEmpty(exported)) continue;
                // JSON 내 "directory": "..." 매칭 — 백슬래시·따옴표 변형 모두 허용.
                if (exported.Contains($"\"directory\":\"{norm}\"", StringComparison.OrdinalIgnoreCase) ||
                    exported.Contains($"\"directory\": \"{norm}\"", StringComparison.OrdinalIgnoreCase) ||
                    exported.Contains($"\"directory\":\"{norm.Replace("\\", "\\\\")}\"", StringComparison.OrdinalIgnoreCase))
                    return sid;
            }
        }
        catch { /* 어떤 단계든 실패 시 null — 세션 복원만 스킵, 앱은 계속 */ }
        return null;
    }

    /// <summary>exceptRoomId 를 제외한 모든 방 추적파일(sessions\<room>.txt)이 가리키는 opencode 세션 ID 집합.
    /// FindSessionIdByCwd 가 다른 방 세션을 가로채지 않도록 후보 제외용으로 쓴다.</summary>
    private static HashSet<string> LoadOtherRoomSessionIds(string? exceptRoomId)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(SessionTrackDir)) return set;
            var exceptFile = exceptRoomId != null ? SafeRoomFileName(exceptRoomId) + ".txt" : null;
            foreach (var f in Directory.EnumerateFiles(SessionTrackDir, "*.txt"))
            {
                if (exceptFile != null && Path.GetFileName(f).Equals(exceptFile, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var id = File.ReadAllText(f).Trim();
                    if (id.StartsWith("ses_", StringComparison.Ordinal) && id.Length > 4) set.Add(id);
                }
                catch { }
            }
        }
        catch { }
        return set;
    }

    private static string? RunOpenCode(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "opencode",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            return p.StandardOutput.ReadToEnd();
        }
        catch { return null; }
    }

    private static string SafeRoomFileName(string roomId)
        => System.Text.RegularExpressions.Regex.Replace(roomId, @"[^\w\-]", "");
}
