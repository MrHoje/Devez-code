using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevezCode.Services;

/// <summary>Grok 훅(grok-hook.ps1) 설치/유지.
/// 1) 스크립트를 %LOCALAPPDATA%\DevezCode\grok\hook.ps1 에 항상 최신본으로 기록.
/// 2) ~/.grok/hooks/devezcode-room-tracker.json 에 상태 추적 이벤트를 등록.</summary>
public static class GrokHookInstaller
{
    public static string ScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "grok", "hook.ps1");

    public static string FastStateScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "grok", "state-hook.cmd");

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

    private static string ReadFastStateScript()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Resources", "Hooks", "grok-state-hook.cmd");
        if (File.Exists(src)) return File.ReadAllText(src);
        return File.ReadAllText(FastStateScriptInstallPath);
    }

    public static void EnsureScriptInstalled()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptInstallPath)!);
            var content = ReadEmbeddedScript();
            if (!File.Exists(ScriptInstallPath) || File.ReadAllText(ScriptInstallPath) != content)
                AtomicFile.WriteAllText(ScriptInstallPath, content);
            var fastContent = ReadFastStateScript();
            if (!File.Exists(FastStateScriptInstallPath) || File.ReadAllText(FastStateScriptInstallPath) != fastContent)
                AtomicFile.WriteAllText(FastStateScriptInstallPath, fastContent);
        }
        catch { /* best-effort */ }
    }

    public static bool InstallHooksJson()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HooksJsonPath)!);
            EnsureScriptInstalled();

            // -WindowStyle Hidden 금지 — codex 훅과 동일 이유(콘솔 상속 스폰 시 부모 터미널 창 최소화).
            // 자세한 근거는 CodexHookInstaller.BuildHookCommand 참고.
            var powershellCommand =
                $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass " +
                $"-File \"{ScriptInstallPath}\"";
            var events = new[]
            {
                "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure",
                "Notification", "Stop", "SessionEnd", "StopFailure",
            };

            // 우리 전용 파일이라 매 설치 시 전체 덮어써 최신 이벤트 목록 유지(다른 훅 파일과 분리).
            var hooksObj = new JsonObject();
            foreach (var eventName in events)
            {
                var hookCommand = eventName is "PreToolUse" or "PostToolUse" or "PostToolUseFailure"
                    ? $"\"{FastStateScriptInstallPath}\" {eventName}"
                    : powershellCommand;
                var definition = new JsonObject
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
                };
                // Grok tool matchers are regular expressions. Bare '*' is invalid;
                // explicit '.*' keeps old/new Grok builds firing every tool event.
                if (eventName is "PreToolUse" or "PostToolUse" or "PostToolUseFailure")
                    definition["matcher"] = ".*";
                hooksObj[eventName] = new JsonArray
                {
                    definition
                };
            }

            var root = new JsonObject { ["hooks"] = hooksObj };
            var opts = new JsonSerializerOptions { WriteIndented = true };
            AtomicFile.WriteAllText(HooksJsonPath, root.ToJsonString(opts));
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
    {
        try
        {
            if (!File.Exists(ScriptInstallPath) || !File.Exists(FastStateScriptInstallPath) || !File.Exists(HooksJsonPath))
                return false;
            var root = JsonNode.Parse(File.ReadAllText(HooksJsonPath));
            if (root?["hooks"] is not JsonObject hooks) return false;
            foreach (var eventName in new[]
            {
                "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure",
                "Notification", "Stop", "SessionEnd", "StopFailure",
            })
            {
                var expected = eventName is "PreToolUse" or "PostToolUse" or "PostToolUseFailure"
                    ? $"\"{FastStateScriptInstallPath}\" {eventName}"
                    : $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptInstallPath}\"";
                if (hooks[eventName] is not JsonArray definitions ||
                    !definitions.Any(d => d is JsonObject definition &&
                        definition["hooks"] is JsonArray handlers &&
                        handlers.Any(h => h is JsonObject handler && handler["command"]?.GetValue<string>() == expected)))
                    return false;
            }
            return true;
        }
        catch { return false; }
    }
}
