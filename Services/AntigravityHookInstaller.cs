using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DevezCode.Services;

/// <summary>Antigravity(agy) 상태 훅 설치/마이그레이션.
/// 최신 agy는 이름 있는 최상위 bundle + 이벤트별 flat/tool schema를 사용하고,
/// 1.1.1 이전 빌드는 예전 hooks bundle schema로 설치한다. 어느 경우든 타사 훅은 보존한다.</summary>
public static class AntigravityHookInstaller
{
    private const string BundleName = "devezcode-status";
    private const string LegacyBundleName = "hooks";
    private static readonly Version CurrentSchemaSince = new(1, 1, 1);

    private static readonly string[] CurrentEvents =
        { "PreInvocation", "PostInvocation", "PostToolUse", "Stop" };

    private static readonly string[] LegacyEvents =
        { "SessionStart", "PreInvocation", "PostInvocation", "PostToolUse", "Stop", "SessionEnd" };

    private static readonly Lazy<Version?> InstalledVersion = new(DetectInstalledVersion);

    public static string ScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "antigravity", "hook.cmd");

    private static string LegacyPs1Path => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "antigravity", "hook.ps1");

    public static string HooksJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config", "hooks.json");

    private static string LegacyHooksJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "hooks.json");

    private static bool UseCurrentSchema => InstalledVersion.Value is not { } version || version >= CurrentSchemaSince;

    public static string ReadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var match = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("antigravity-hook.cmd", StringComparison.OrdinalIgnoreCase));
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
            var content = ReadEmbeddedScript();
            // hook.cmd 는 agy 콘솔을 상속해 실행되므로 chcp 프롤로그를 넣지 않는다(그 콘솔의 코드페이지를
            // 바꾸면 렌더가 흔들린다). 내용은 ASCII 로만 유지해 인코딩 의존을 없앤다.
            if (!File.Exists(ScriptInstallPath) || File.ReadAllText(ScriptInstallPath) != content)
                AtomicFile.WriteAllText(ScriptInstallPath, content);
        }
        catch { /* best-effort */ }
    }

    /// <summary>공유 hooks.json을 최신본과 재병합해 설치한다. 기존 파일이 손상됐으면 덮어쓰지 않는다.</summary>
    public static bool InstallHooksJson()
    {
        var installed = AtomicFile.TryUpdateAllText(HooksJsonPath, raw =>
        {
            var root = ParseRoot(raw);
            RemoveManagedHooks(root);
            if (UseCurrentSchema) InstallCurrentSchema(root);
            else InstallLegacySchema(root);
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        });
        return installed && HookAssetsHealthy();
    }

    private static JsonObject ParseRoot(string? raw)
    {
        if (raw == null) return new JsonObject();
        return JsonNode.Parse(raw) as JsonObject
            ?? throw new JsonException("Antigravity hooks root must be an object.");
    }

    private static void InstallCurrentSchema(JsonObject root)
    {
        var bundle = GetOrCreateBundle(root, BundleName);
        foreach (var eventName in CurrentEvents)
        {
            var definitions = GetOrCreateDefinitions(bundle, eventName);
            var handler = BuildHandler(BuildHookCommand(eventName));
            if (eventName == "PostToolUse")
            {
                definitions.Add(new JsonObject
                {
                    ["matcher"] = "*",
                    ["hooks"] = new JsonArray(handler),
                });
            }
            else
            {
                definitions.Add(handler);
            }
        }
    }

    private static void InstallLegacySchema(JsonObject root)
    {
        var bundle = GetOrCreateBundle(root, LegacyBundleName);
        foreach (var eventName in LegacyEvents)
        {
            var definition = new JsonObject
            {
                ["hooks"] = new JsonArray(BuildHandler(BuildHookCommand(eventName))),
            };
            if (eventName == "PostToolUse") definition["matcher"] = "*";
            GetOrCreateDefinitions(bundle, eventName).Add(definition);
        }
    }

    private static JsonObject GetOrCreateBundle(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing) return existing;
        if (root.ContainsKey(name)) throw new JsonException($"Antigravity hook bundle '{name}' must be an object.");
        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    private static JsonArray GetOrCreateDefinitions(JsonObject bundle, string eventName)
    {
        if (bundle[eventName] is JsonArray existing) return existing;
        if (bundle.ContainsKey(eventName)) throw new JsonException($"Antigravity event '{eventName}' must be an array.");
        var created = new JsonArray();
        bundle[eventName] = created;
        return created;
    }

    private static JsonObject BuildHandler(string command) => new()
    {
        ["type"] = "command",
        ["command"] = command,
        ["timeout"] = 10,
    };

    private static string BuildHookCommand(string eventName)
        // agy command는 Windows에서 cmd /c로 실행된다. 따옴표가 보존되는 구버전을 위해
        // 공백 경로는 8.3 경로로 바꾸고 이벤트명은 안전한 고정 인자로 전달한다.
        => $"{ToArgSafePath(ScriptInstallPath)} {eventName}";

    private static string ToArgSafePath(string path)
    {
        if (!path.Contains(' ')) return path;
        try
        {
            var sb = new StringBuilder(260);
            if (GetShortPathName(path, sb, sb.Capacity) > 0 && sb.Length > 0) return sb.ToString();
        }
        catch { }
        return path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);

    /// <summary>모든 구·신 bundle에서 DevezCode 명령만 제거한다. direct handler와 hooks wrapper를 모두 처리한다.</summary>
    private static void RemoveManagedHooks(JsonObject root)
    {
        foreach (var topLevelName in root.Select(p => p.Key).ToList())
        {
            if (root[topLevelName] is not JsonObject bundle) continue;
            foreach (var eventName in bundle.Select(p => p.Key).ToList())
            {
                if (bundle[eventName] is not JsonArray definitions) continue;
                for (int i = definitions.Count - 1; i >= 0; i--)
                {
                    if (definitions[i] is not JsonObject definition) continue;
                    if (IsOurHookCommand(TryGetCommand(definition)))
                    {
                        definitions.RemoveAt(i);
                        continue;
                    }
                    if (definition["hooks"] is not JsonArray handlers) continue;
                    for (int h = handlers.Count - 1; h >= 0; h--)
                    {
                        if (handlers[h] is JsonObject handler && IsOurHookCommand(TryGetCommand(handler)))
                            handlers.RemoveAt(h);
                    }
                    if (handlers.Count == 0) definitions.RemoveAt(i);
                }
                if (definitions.Count == 0) bundle.Remove(eventName);
            }
            if (bundle.Count == 0 && (topLevelName == BundleName || topLevelName == LegacyBundleName))
                root.Remove(topLevelName);
        }
    }

    private static string? TryGetCommand(JsonObject node)
    {
        try { return node["command"]?.GetValue<string>(); }
        catch { return null; }
    }

    private static bool IsOurHookCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var normalized = command.Replace('/', '\\');
        foreach (var path in new[] { ScriptInstallPath, ToArgSafePath(ScriptInstallPath), LegacyPs1Path, ToArgSafePath(LegacyPs1Path) })
        {
            if (normalized.Contains(path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static void EnsureInstalled()
    {
        EnsureScriptInstalled();
        if (!InstallHooksJson() || !HookAssetsHealthy()) return;

        // 새 설치가 검증된 뒤에만 구경로를 정리한다. 실패한 설치가 정상 구버전 훅을 없애지 않는다.
        CleanupLegacyHooksJson();
        try { if (File.Exists(LegacyPs1Path)) File.Delete(LegacyPs1Path); } catch { }
    }

    private static void CleanupLegacyHooksJson()
    {
        if (!File.Exists(LegacyHooksJsonPath)) return;
        bool emptyAfterCleanup = false;
        var updated = AtomicFile.TryUpdateAllText(LegacyHooksJsonPath, raw =>
        {
            var root = ParseRoot(raw);
            var before = root.ToJsonString();
            RemoveManagedHooks(root);
            emptyAfterCleanup = root.Count == 0;
            return before == root.ToJsonString()
                ? raw
                : root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        });
        if (!updated || !emptyAfterCleanup) return;

        // 내용이 여전히 빈 객체일 때만 삭제한다. 타 프로세스가 새 훅을 썼다면 보존한다.
        try
        {
            var latest = JsonNode.Parse(File.ReadAllText(LegacyHooksJsonPath)) as JsonObject;
            if (latest?.Count == 0) File.Delete(LegacyHooksJsonPath);
        }
        catch { }
    }

    public static bool HookAssetsHealthy()
    {
        try
        {
            if (!File.Exists(ScriptInstallPath) || !File.Exists(HooksJsonPath)) return false;
            var root = JsonNode.Parse(File.ReadAllText(HooksJsonPath)) as JsonObject;
            if (root == null) return false;
            return UseCurrentSchema ? HasCurrentHooks(root) : HasLegacyHooks(root);
        }
        catch { return false; }
    }

    private static bool HasCurrentHooks(JsonObject root)
    {
        if (root[BundleName] is not JsonObject bundle) return false;
        foreach (var eventName in CurrentEvents)
        {
            if (bundle[eventName] is not JsonArray definitions) return false;
            var command = BuildHookCommand(eventName);
            var found = definitions.Any(node => node is JsonObject definition &&
                (TryGetCommand(definition) == command ||
                 definition["hooks"] is JsonArray handlers && handlers.Any(h => h is JsonObject handler && TryGetCommand(handler) == command)));
            if (!found) return false;
        }
        return true;
    }

    private static bool HasLegacyHooks(JsonObject root)
    {
        if (root[LegacyBundleName] is not JsonObject bundle) return false;
        foreach (var eventName in LegacyEvents)
        {
            if (bundle[eventName] is not JsonArray definitions) return false;
            var command = BuildHookCommand(eventName);
            if (!definitions.Any(node => node is JsonObject definition &&
                definition["hooks"] is JsonArray handlers &&
                handlers.Any(h => h is JsonObject handler && TryGetCommand(handler) == command))) return false;
        }
        return true;
    }

    private static Version? DetectInstalledVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /s /c \"agy --version\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (!output.Wait(300)) return null;
            var match = Regex.Match(output.Result, @"\d+(?:\.\d+){1,3}");
            return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
        }
        catch { return null; }
    }
}
