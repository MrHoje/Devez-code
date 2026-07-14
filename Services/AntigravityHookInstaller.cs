using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevezCode.Services;

/// <summary>안티그래비티(agy) 훅(antigravity-hook.cmd) 설치/유지.
/// 1) 배치를 %LOCALAPPDATA%\DevezCode\antigravity\hook.cmd 에 항상 최신본으로 기록.
/// 2) ~/.gemini/config/hooks.json 에 구·신버전 수명주기 이벤트를 함께 등록(merge).
/// PowerShell 대신 cmd 배치인 이유(실측 2026-07-13): agy 는 훅 프로세스를 기다리지 않고 조기
/// 취소할 수 있어(--print 의 SessionStart/Stop) 기동 ~수백ms 인 powershell 은 실행 전에 죽는다.
/// 이벤트명은 stdin JSON 에 없어 배치 인자(%1)로 전달한다.
/// 주의: agy 에는 UserPromptSubmit 훅이 없다 — 구버전은 Tool 이벤트, 신버전은 Invocation 이벤트도 활용.</summary>
public static class AntigravityHookInstaller
{
    public static string ScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "antigravity", "hook.cmd");

    /// <summary>구버전 PowerShell 훅 잔재 — 설치 시 정리.</summary>
    private static string LegacyPs1Path => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "antigravity", "hook.ps1");

    /// <summary>agy 글로벌 훅 파일. 실측(1.1.1): agy 는 <c>~/.gemini/antigravity-cli/hooks.json</c> 을 발견하면
    /// <c>~/.gemini/config/hooks.json</c> 으로 마이그레이션하고 이후 config 쪽만 읽는다(migrate.go 로그) —
    /// 처음부터 config 경로에 쓴다.</summary>
    public static string HooksJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config", "hooks.json");

    /// <summary>구버전(마이그레이션 전) 경로 — 여기 남은 우리 훅 파일은 agy 가 매번 재마이그레이션을
    /// 시도하므로 설치 시 정리한다.</summary>
    private static string LegacyHooksJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "hooks.json");

    private static readonly string[] HookEventNames =
        { "SessionStart", "PreInvocation", "PostInvocation", "PreToolUse", "PostToolUse", "Stop", "SessionEnd" };

    public static string ReadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        var match = names.FirstOrDefault(n => n.EndsWith("antigravity-hook.cmd", StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            using var s = asm.GetManifestResourceStream(match)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        var src = Path.Combine(AppContext.BaseDirectory, "Resources", "Hooks", "antigravity-hook.cmd");
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

            foreach (var eventName in HookEventNames)
                EnsureOurHook(hooksObj, eventName, BuildHookCommand(eventName));
            RemoveStaleManagedHooks(hooksObj);

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(HooksJsonPath, root.ToJsonString(opts), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    private static string BuildHookCommand(string eventName)
        // agy(1.1.1) 훅 command 제약 실측(json_hook_caller 로그):
        //  · 큰따옴표를 벗기지 않고 인자로 넘김 → -File "path" 가 "Illegal characters in path" 실패
        //  · cmd 계열 해석이라 & 가 명령 구분자 → -Command & 'path' 는 "must follow -Command" 실패
        // ⇒ 따옴표·특수문자 없는 평문 경로 + 배치 직접 실행. 공백 포함 경로는 8.3 단축경로로 변환.
        // 이벤트명은 stdin JSON 에 없어 인자로 전달.
        => $"{ToArgSafePath(ScriptInstallPath)} {eventName}";

    /// <summary>경로에 공백이 있으면 8.3 단축경로로 변환(따옴표 없이 인자로 쓸 수 있게). 실패 시 원본.</summary>
    private static string ToArgSafePath(string path)
    {
        if (!path.Contains(' ')) return path;
        try
        {
            var sb = new StringBuilder(260);
            if (GetShortPathName(path, sb, sb.Capacity) > 0 && sb.Length > 0)
                return sb.ToString();
        }
        catch { }
        return path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);

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
        firstHook["timeout"] = 10;

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
        var normalizedSafePath = ToArgSafePath(ScriptInstallPath).Replace('/', '\\');
        var normalizedLegacyPath = LegacyPs1Path.Replace('/', '\\');
        var normalizedLegacySafePath = ToArgSafePath(LegacyPs1Path).Replace('/', '\\');
        return normalizedCommand.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase)
            || normalizedCommand.Contains(normalizedSafePath, StringComparison.OrdinalIgnoreCase)
            || normalizedCommand.Contains(normalizedLegacyPath, StringComparison.OrdinalIgnoreCase)
            || normalizedCommand.Contains(normalizedLegacySafePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>예전 버전이 더 이상 관리하지 않는 이벤트에 남긴 우리 명령만 제거한다.
    /// 사용자/다른 플러그인의 훅과 현재 이벤트의 배열 위치는 보존한다.</summary>
    private static void RemoveStaleManagedHooks(JsonObject hooksObj)
    {
        var managedEvents = new HashSet<string>(HookEventNames, StringComparer.Ordinal);
        foreach (var eventName in hooksObj.Select(p => p.Key).ToList())
        {
            if (managedEvents.Contains(eventName) || hooksObj[eventName] is not JsonArray entries) continue;
            for (int entryIndex = entries.Count - 1; entryIndex >= 0; entryIndex--)
            {
                if (entries[entryIndex] is not JsonObject entry || entry["hooks"] is not JsonArray inner) continue;
                for (int hookIndex = inner.Count - 1; hookIndex >= 0; hookIndex--)
                {
                    if (inner[hookIndex] is JsonObject hook &&
                        IsOurHookCommand(hook["command"]?.GetValue<string>()))
                        inner.RemoveAt(hookIndex);
                }
                if (inner.Count == 0) entries.RemoveAt(entryIndex);
            }
            if (entries.Count == 0) hooksObj.Remove(eventName);
        }
    }

    public static void EnsureInstalled()
    {
        EnsureScriptInstalled();
        InstallHooksJson();
        CleanupLegacyHooksJson();
        try { if (File.Exists(LegacyPs1Path)) File.Delete(LegacyPs1Path); } catch { }
    }

    /// <summary>구경로에 남은 파일에서 우리 훅만 제거(다른 훅은 보존). 우리 훅만 있었다면 파일 삭제.</summary>
    private static void CleanupLegacyHooksJson()
    {
        try
        {
            if (!File.Exists(LegacyHooksJsonPath)) return;
            var root = JsonNode.Parse(File.ReadAllText(LegacyHooksJsonPath));
            if (root?["hooks"] is not JsonObject hooksObj) return;

            bool changed = false, anyOtherHook = false;
            foreach (var eventName in hooksObj.Select(p => p.Key).ToList())
            {
                if (hooksObj[eventName] is not JsonArray entries) continue;
                for (int entryIndex = entries.Count - 1; entryIndex >= 0; entryIndex--)
                {
                    if (entries[entryIndex] is not JsonObject entry ||
                        entry["hooks"] is not JsonArray inner) continue;
                    for (int hookIndex = inner.Count - 1; hookIndex >= 0; hookIndex--)
                    {
                        if (inner[hookIndex] is JsonObject hook &&
                            IsOurHookCommand(hook["command"]?.GetValue<string>()))
                        {
                            inner.RemoveAt(hookIndex);
                            changed = true;
                        }
                        else anyOtherHook = true;
                    }
                    if (inner.Count == 0) entries.RemoveAt(entryIndex);
                }
                if (entries.Count == 0) hooksObj.Remove(eventName);
            }

            if (!anyOtherHook)
            {
                File.Delete(LegacyHooksJsonPath);
            }
            else if (changed)
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(LegacyHooksJsonPath, root.ToJsonString(opts), new UTF8Encoding(false));
            }
        }
        catch { /* best-effort */ }
    }

    public static bool HookAssetsHealthy()
        => File.Exists(ScriptInstallPath) && File.Exists(HooksJsonPath);
}
