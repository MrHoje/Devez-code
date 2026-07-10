using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevezCode.Services;

/// <summary>[개발 중단] Grok 통합 보류 — UI 비노출. 재개 시 사용.
/// grok 훅(grok-hook.ps1) 설치/유지.
/// 1) 스크립트를 %LOCALAPPDATA%\DevezCode\grok\hook.ps1 에 항상 최신본으로 기록.
/// 2) ~/.grok/hooks/devezcode-room-tracker.json 에 SessionStart/UserPromptSubmit/Stop/SessionEnd 등록.</summary>
public static class GrokHookInstaller
{
    public static string ScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "grok", "hook.ps1");

    public static string HooksJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "hooks", "devezcode-room-tracker.json");

    public static string ReadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        var match = names.FirstOrDefault(n => n.EndsWith("grok-hook.ps1", StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            using var s = asm.GetManifestResourceStream(match)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        var src = Path.Combine(AppContext.BaseDirectory, "Resources", "Hooks", "grok-hook.ps1");
        if (File.Exists(src)) return File.ReadAllText(src);
        return File.ReadAllText(ScriptInstallPath);
    }

    public static void EnsureScriptInstalled()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptInstallPath)!);
            var content = ReadEmbeddedScript();
            if (!File.Exists(ScriptInstallPath) || File.ReadAllText(ScriptInstallPath) != content)
                File.WriteAllText(ScriptInstallPath, content, new UTF8Encoding(false));
        }
        catch { /* best-effort */ }
    }

    public static bool InstallHooksJson()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HooksJsonPath)!);
            EnsureScriptInstalled();

            var hookCommand =
                $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden " +
                $"-File \"{ScriptInstallPath}\"";
            var events = new[] { "SessionStart", "UserPromptSubmit", "Stop", "SessionEnd", "StopFailure" };

            // 우리 전용 파일이라 매 설치 시 전체 덮어써 최신 이벤트 목록 유지(다른 훅 파일과 분리).
            var hooksObj = new JsonObject();
            foreach (var eventName in events)
            {
                hooksObj[eventName] = new JsonArray
                {
                    new JsonObject
                    {
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = hookCommand,
                                ["timeout"] = 10,
                            }
                        }
                    }
                };
            }

            var root = new JsonObject { ["hooks"] = hooksObj };
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(HooksJsonPath, root.ToJsonString(opts), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    public static void EnsureInstalled()
    {
        EnsureScriptInstalled();
        InstallHooksJson();
    }

    public static bool HookAssetsHealthy()
        => File.Exists(ScriptInstallPath) && File.Exists(HooksJsonPath);
}
