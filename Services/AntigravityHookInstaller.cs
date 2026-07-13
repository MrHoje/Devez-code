using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevezCode.Services;

/// <summary>안티그래비티(agy) 훅(antigravity-hook.ps1) 설치/유지.
/// 1) 스크립트를 %LOCALAPPDATA%\DevezCode\antigravity\hook.ps1 에 항상 최신본으로 기록.
/// 2) ~/.gemini/antigravity-cli/hooks.json 에 SessionStart/PreToolUse/PostToolUse/Stop/SessionEnd 등록.
///    agy 의 hooks.json 은 사용자 공유 파일이므로 codex 처럼 merge — 다른 훅은 보존.
/// 주의: agy 에는 UserPromptSubmit 훅이 없다(바이너리 실측) — busy-ON 은 PreToolUse 가 담당.</summary>
public static class AntigravityHookInstaller
{
    public static string ScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "antigravity", "hook.ps1");

    /// <summary>agy 글로벌 훅 파일. (경로는 agy 1.1.1 바이너리 실측 기반 추정 — 런타임 검증 항목)</summary>
    public static string HooksJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "hooks.json");

    private static readonly string[] HookEventNames =
        { "SessionStart", "PreToolUse", "PostToolUse", "Stop", "SessionEnd" };

    public static string ReadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        var match = names.FirstOrDefault(n => n.EndsWith("antigravity-hook.ps1", StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            using var s = asm.GetManifestResourceStream(match)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        var src = Path.Combine(AppContext.BaseDirectory, "Resources", "Hooks", "antigravity-hook.ps1");
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

    /// <summary>hooks.json 에 우리 훅을 merge 등록. 다른 이벤트/훅은 보존. 성공 시 true.</summary>
    public static bool InstallHooksJson()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HooksJsonPath)!);

            JsonNode? root;
            try
            {
                root = File.Exists(HooksJsonPath)
                    ? JsonNode.Parse(File.ReadAllText(HooksJsonPath))
                    : JsonNode.Parse("{}");
            }
            catch { root = JsonNode.Parse("{}"); }
            root ??= JsonNode.Parse("{}");

            if (root!["hooks"] is not JsonObject hooksObj)
            {
                hooksObj = new JsonObject();
                root["hooks"] = hooksObj;
            }

            var hookCommand = BuildHookCommand();
            foreach (var eventName in HookEventNames)
                EnsureOurHook(hooksObj, eventName, hookCommand);

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(HooksJsonPath, root.ToJsonString(opts), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    private static string BuildHookCommand()
        // -WindowStyle Hidden 금지 — codex 훅과 동일 이유(콘솔 상속 스폰 시 부모 터미널 창 최소화).
        // 자세한 근거는 CodexHookInstaller.BuildHookCommand 참고.
        => $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptInstallPath}\"";

    /// <summary>이벤트 안의 DevezCode 훅을 최신 command 로 유지. 중복 제거, 다른 훅 보존
    /// (CodexHookInstaller.EnsureOurHook 과 동일 로직).</summary>
    private static void EnsureOurHook(JsonObject hooksObj, string eventName, string command)
    {
        JsonArray entries;
        if (hooksObj[eventName] is JsonArray existing)
        {
            entries = existing;
        }
        else
        {
            entries = new JsonArray();
            hooksObj[eventName] = entries;
        }

        var locations = new List<(int EntryIndex, int HookIndex)>();
        for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            if (entries[entryIndex] is not JsonObject entry ||
                entry["hooks"] is not JsonArray inner) continue;
            for (int hookIndex = 0; hookIndex < inner.Count; hookIndex++)
            {
                if (inner[hookIndex] is JsonObject hook &&
                    IsOurHookCommand(hook["command"]?.GetValue<string>()))
                    locations.Add((entryIndex, hookIndex));
            }
        }

        if (locations.Count == 0)
        {
            entries.Add(new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 10 }
                }
            });
            return;
        }

        var first = locations[0];
        var firstEntry = (JsonObject)entries[first.EntryIndex]!;
        var firstHooks = (JsonArray)firstEntry["hooks"]!;
        var firstHook = (JsonObject)firstHooks[first.HookIndex]!;
        firstHook["type"] = "command";
        firstHook["command"] = command;

        for (int i = locations.Count - 1; i >= 1; i--)
        {
            var duplicate = locations[i];
            if (entries[duplicate.EntryIndex] is not JsonObject entry ||
                entry["hooks"] is not JsonArray inner) continue;
            inner.RemoveAt(duplicate.HookIndex);
        }
        for (int entryIndex = entries.Count - 1; entryIndex >= 0; entryIndex--)
        {
            if (entries[entryIndex] is JsonObject entry &&
                entry["hooks"] is JsonArray inner && inner.Count == 0)
                entries.RemoveAt(entryIndex);
        }
    }

    private static bool IsOurHookCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var normalizedCommand = command.Replace('/', '\\');
        var normalizedPath = ScriptInstallPath.Replace('/', '\\');
        return normalizedCommand.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureInstalled()
    {
        EnsureScriptInstalled();
        InstallHooksJson();
    }

    public static bool HookAssetsHealthy()
        => File.Exists(ScriptInstallPath) && File.Exists(HooksJsonPath);
}
