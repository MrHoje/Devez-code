using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DevezCode.Services;

/// <summary>codex 훅(codex-hook.ps1) 을 사용자 머신에 설치/유지.
/// 1) 스크립트 본문을 %LOCALAPPDATA%\DevezCode\codex\ 에 항상 최신본으로 기록 (앱 시작 시).
/// 2) ~/.codex/hooks.json 의 세션·턴·권한·도구 이벤트에 우리 훅을 등록
///    (다른 훅은 보존 — merge).</summary>
public static class CodexHookInstaller
{
    public static string ScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "codex", "hook.ps1");

    public static string FastStateScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "codex", "state-hook.cmd");

    public static string CodexHooksJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "hooks.json");

    public static string CodexConfigTomlPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

    private static readonly string[] HookEventNames =
        { "SessionStart", "UserPromptSubmit", "PermissionRequest", "PreToolUse", "PostToolUse", "Stop" };

    private static readonly Regex HookStateHeaderRegex = new(
        @"^\s*\[hooks\.state\.'(?<key>[^']+)'\]\s*(?:#.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EnabledStateRegex = new(
        @"^(?<prefix>\s*enabled\s*=\s*)(?<value>true|false)(?<suffix>\s*(?:#.*)?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrustedHashRegex = new(
        @"^(?<prefix>\s*trusted_hash\s*=\s*)""(?<value>[^""]*)""(?<suffix>\s*(?:#.*)?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>임베디드 리소스(Resources\Hooks\codex-hook.ps1) 의 내용을 읽어 반환.</summary>
    public static string ReadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        // 리소스명: <RootNamespace>.<path-with-dots>
        // DevezCode 의 csproj 는 Resources\\**\\* 를 자동으로 임베드하지 않으므로 (csproj 보강 필요),
        // 우선 asm.GetManifestResourceStream 시도, 실패 시 소스 디렉터리에서 직접 읽기 폴백.
        var names = asm.GetManifestResourceNames();
        var match = names.FirstOrDefault(n => n.EndsWith("codex-hook.ps1", StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            using var s = asm.GetManifestResourceStream(match)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        // 폴백 — 소스 트리 (개발/디버그용)
        var src = Path.Combine(AppContext.BaseDirectory, "Resources", "Hooks", "codex-hook.ps1");
        if (File.Exists(src)) return File.ReadAllText(src);
        // 마지막 폴백 — csproj Content 가 출력 폴더에 복사한 경로
        return File.ReadAllText(ScriptInstallPath);
    }

    private static string ReadFastStateScript()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Resources", "Hooks", "codex-state-hook.cmd");
        if (File.Exists(src)) return File.ReadAllText(src);
        return File.ReadAllText(FastStateScriptInstallPath);
    }

    /// <summary>스크립트를 ScriptInstallPath 에 기록. 매 시작 시 호출해도 동일 내용이라 noop.</summary>
    public static void EnsureScriptInstalled()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptInstallPath)!);
            var content = ReadEmbeddedScript();
            if (!File.Exists(ScriptInstallPath) || File.ReadAllText(ScriptInstallPath) != content)
                File.WriteAllText(ScriptInstallPath, content, new UTF8Encoding(false));
            var fastContent = ReadFastStateScript();
            if (!File.Exists(FastStateScriptInstallPath) || File.ReadAllText(FastStateScriptInstallPath) != fastContent)
                File.WriteAllText(FastStateScriptInstallPath, fastContent, new UTF8Encoding(false));
        }
        catch { /* 권한 부족 등 — 무시 */ }
    }

    /// <summary>~/.codex/hooks.json 에 우리 훅이 등록되어 있는지 확인.</summary>
    public static bool IsHooksJsonInstalled()
    {
        try
        {
            if (!File.Exists(CodexHooksJsonPath)) return false;
            var root = JsonNode.Parse(File.ReadAllText(CodexHooksJsonPath));
            if (root?["hooks"] is not JsonObject hooksObj) return false;
            return HookEventNames.All(eventName => HasOurHook(hooksObj, eventName, BuildHookCommand(eventName)));
        }
        catch { return false; }
    }

    /// <summary>~/.codex/hooks.json 에 상태 추적 훅을 등록 (merge).
    /// 다른 이벤트/훅은 보존됨. 성공 시 true.</summary>
    public static bool InstallHooksJson()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CodexHooksJsonPath)!);

            // 기존 파일 파싱 (없으면 빈 객체로 시작)
            JsonNode? root;
            try
            {
                if (File.Exists(CodexHooksJsonPath))
                {
                    var raw = File.ReadAllText(CodexHooksJsonPath);
                    root = JsonNode.Parse(raw);
                }
                else
                {
                    root = JsonNode.Parse("{}");
                }
            }
            catch { root = JsonNode.Parse("{}"); }

            root ??= new JsonObject();

            // root.hooks 객체 보장
            if (root["hooks"] is not JsonObject hooksObj)
            {
                hooksObj = new JsonObject();
                root["hooks"] = hooksObj;
            }

            // 우리 커맨드 — 절대경로 (공백 있어도 안전)
            // 이벤트별 훅 등록. 구버전 command 가 있으면 같은 위치에서 최신 command 로 교체해
            // 기존 PC 에 옛 훅+새 훅이 중복 등록되지 않게 한다.
            foreach (var eventName in HookEventNames)
                EnsureOurHook(hooksObj, eventName, BuildHookCommand(eventName));

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(CodexHooksJsonPath, root.ToJsonString(opts), new UTF8Encoding(false));

            // Codex 는 훅별 활성/신뢰 상태를 config.toml 의 [hooks.state.'...'] 에 따로 저장한다.
            // command가 멀쩡해도 trusted_hash가 없거나 enabled=false면 실행되지 않으므로,
            // 앱 소유 훅만 찾아 매 시작/세션 실행 때 신뢰·활성 상태를 보장한다.
            return EnsureOurHookStatesTrusted(hooksObj);
        }
        catch { return false; }
    }

    private static string BuildHookCommand(string eventName)
        // -WindowStyle Hidden 금지: codex(Rust)는 훅 프로세스를 부모 콘솔을 상속해 스폰한다.
        // 상속된 powershell 에 -WindowStyle Hidden 을 주면 ShowWindow(GetConsoleWindow(), SW_HIDE) 가
        // '공유' 콘솔(외부 터미널 창)에 적용돼 세션 창이 최소화/숨김된다(프롬프트 전송·응답 완료 시점).
        // 콘솔을 상속하므로 새 창이 뜨지 않아 Hidden 없이도 flash 가 없고, DevezCode ConPTY(헤드리스)
        // 에서도 창이 없어 무해하다. (claude 는 windowsHide 로 스폰돼 Hidden 이 no-op 이라 영향 없었음.)
        => eventName switch
        {
            "PermissionRequest" => $"\"{FastStateScriptInstallPath}\" waiting",
            "PreToolUse" or "PostToolUse" => $"\"{FastStateScriptInstallPath}\" working",
            _ => $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptInstallPath}\"",
        };

    /// <summary>
    /// 이벤트 안의 DevezCode 훅을 최신 command 로 유지한다. command 옵션이 다른 구버전도
    /// 스크립트 절대경로로 식별해 제자리 교체하고, 과거 설치 중 생긴 중복은 제거한다.
    /// 다른 사용자/플러그인 훅은 보존한다.
    /// </summary>
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
                    new JsonObject { ["type"] = "command", ["command"] = command }
                }
            });
            return;
        }

        // 첫 훅은 배열 위치를 유지해 state key 변동을 최소화하고 command 만 갱신한다.
        var first = locations[0];
        var firstEntry = (JsonObject)entries[first.EntryIndex]!;
        var firstHooks = (JsonArray)firstEntry["hooks"]!;
        var firstHook = (JsonObject)firstHooks[first.HookIndex]!;
        firstHook["type"] = "command";
        firstHook["command"] = command;

        // 중복은 뒤에서부터 삭제. 비어 버린 matcher entry 만 함께 제거한다.
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
        var normalizedFastPath = FastStateScriptInstallPath.Replace('/', '\\');
        return normalizedCommand.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase)
            || normalizedCommand.Contains(normalizedFastPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOurHook(JsonObject hooksObj, string eventName, string command)
    {
        if (hooksObj[eventName] is not JsonArray arr) return false;
        foreach (var entry in arr)
        {
            if (entry is JsonObject entryObj &&
                entryObj["hooks"] is JsonArray innerArr)
            {
                foreach (var h in innerArr)
                {
                    if (h is JsonObject hookObj &&
                        hookObj["command"]?.GetValue<string>() == command)
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>현재 hooks.json 배열 위치를 기준으로 우리 훅의 config.toml state key 를 계산한다.</summary>
    private static Dictionary<string, string> FindOurHookStates(JsonObject hooksObj)
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var eventName in HookEventNames)
        {
            if (hooksObj[eventName] is not JsonArray entries) continue;
            for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                if (entries[entryIndex] is not JsonObject entry ||
                    entry["hooks"] is not JsonArray inner) continue;
                for (int hookIndex = 0; hookIndex < inner.Count; hookIndex++)
                {
                    if (inner[hookIndex] is not JsonObject hook ||
                        hook["command"]?.GetValue<string>() != BuildHookCommand(eventName)) continue;
                    var stateEvent = ToHookStateEventName(eventName);
                    var key = NormalizeHookStateKey(
                        $"{CodexHooksJsonPath}:{stateEvent}:{entryIndex}:{hookIndex}");
                    states[key] = ComputeTrustedHash(stateEvent, BuildHookCommand(eventName));
                }
            }
        }
        return states;
    }

    /// <summary>
    /// Codex 0.129+는 config.toml의 이벤트별 trusted_hash가 없으면 훅을 실행하지 않는다.
    /// 앱이 직접 설치한 DevezCode 훅만 Codex와 같은 정규화/해시 규칙으로 신뢰 등록하고,
    /// 사용자·다른 플러그인 훅의 상태와 해시는 건드리지 않는다.
    /// </summary>
    private static bool EnsureOurHookStatesTrusted(JsonObject hooksObj)
    {
        try
        {
            var ourStates = FindOurHookStates(hooksObj);
            if (ourStates.Count == 0) return false;

            var raw = File.Exists(CodexConfigTomlPath) ? File.ReadAllText(CodexConfigTomlPath) : "";
            var newline = raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
            if (lines.Count == 1 && lines[0].Length == 0) lines.Clear();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var insertions = new List<(int Index, List<string> Lines)>();
            bool changed = false;

            for (int i = 0; i < lines.Count; i++)
            {
                var header = HookStateHeaderRegex.Match(lines[i]);
                if (!header.Success) continue;
                var key = NormalizeHookStateKey(header.Groups["key"].Value);
                if (!ourStates.TryGetValue(key, out var expectedHash)) continue;
                found.Add(key);

                int end = i + 1;
                while (end < lines.Count && !lines[end].TrimStart().StartsWith("[", StringComparison.Ordinal)) end++;
                bool hasEnabled = false, hasHash = false;
                for (int j = i + 1; j < end; j++)
                {
                    var enabled = EnabledStateRegex.Match(lines[j]);
                    if (enabled.Success)
                    {
                        hasEnabled = true;
                        if (!enabled.Groups["value"].Value.Equals("true", StringComparison.OrdinalIgnoreCase))
                        {
                            lines[j] = enabled.Groups["prefix"].Value + "true" + enabled.Groups["suffix"].Value;
                            changed = true;
                        }
                        continue;
                    }
                    var trusted = TrustedHashRegex.Match(lines[j]);
                    if (!trusted.Success) continue;
                    hasHash = true;
                    if (!trusted.Groups["value"].Value.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[j] = trusted.Groups["prefix"].Value + expectedHash + trusted.Groups["suffix"].Value;
                        changed = true;
                    }
                }

                var additions = new List<string>();
                if (!hasEnabled) additions.Add("enabled = true");
                if (!hasHash) additions.Add($"trusted_hash = \"{expectedHash}\"");
                if (additions.Count > 0)
                {
                    int insertAt = end;
                    while (insertAt > i + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
                    insertions.Add((insertAt, additions));
                    changed = true;
                }
            }

            foreach (var insertion in insertions.OrderByDescending(x => x.Index))
                lines.InsertRange(insertion.Index, insertion.Lines);

            foreach (var pair in ourStates.Where(pair => !found.Contains(pair.Key)))
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
                lines.Add($"[hooks.state.'{pair.Key.Replace("'", "''", StringComparison.Ordinal)}']");
                lines.Add("enabled = true");
                lines.Add($"trusted_hash = \"{pair.Value}\"");
                lines.Add("");
                changed = true;
            }

            if (changed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CodexConfigTomlPath)!);
                File.WriteAllText(CodexConfigTomlPath, string.Join(newline, lines), new UTF8Encoding(false));
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Codex command_hook_hash: canonical JSON(identity) SHA-256.</summary>
    private static string ComputeTrustedHash(string eventLabel, string command)
    {
        // 키 삽입 순서는 사전순이다. JsonArray는 순서를 보존하므로 Codex canonical_json과 동일하다.
        var handler = new JsonObject
        {
            ["async"] = false,
            ["command"] = command,
            ["timeout"] = 600,
            ["type"] = "command",
        };
        var identity = new JsonObject
        {
            ["event_name"] = eventLabel,
            ["hooks"] = new JsonArray(handler),
        };
        var json = identity.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeHookStateKey(string key) => key.Replace('/', '\\');

    private static string ToHookStateEventName(string eventName)
    {
        var sb = new StringBuilder(eventName.Length + 4);
        for (int i = 0; i < eventName.Length; i++)
        {
            var c = eventName[i];
            if (i > 0 && char.IsUpper(c)) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>codex 훅 사용 가능 여부 — 스크립트 설치 + hooks.json 등록 모두.</summary>
    public static bool HookAssetsHealthy()
    {
        if (!File.Exists(ScriptInstallPath) || !File.Exists(FastStateScriptInstallPath) || !IsHooksJsonInstalled()) return false;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(CodexHooksJsonPath));
            if (root?["hooks"] is not JsonObject hooksObj) return false;
            var states = FindOurHookStates(hooksObj);
            if (!File.Exists(CodexConfigTomlPath)) return false;

            string? activeStateKey = null;
            var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(CodexConfigTomlPath))
            {
                var header = HookStateHeaderRegex.Match(line);
                if (header.Success)
                {
                    activeStateKey = NormalizeHookStateKey(header.Groups["key"].Value);
                    continue;
                }
                if (line.TrimStart().StartsWith("[", StringComparison.Ordinal)) activeStateKey = null;
                if (activeStateKey == null || !states.TryGetValue(activeStateKey, out var expectedHash)) continue;
                var enabled = EnabledStateRegex.Match(line);
                if (enabled.Success && !enabled.Groups["value"].Value.Equals("true", StringComparison.OrdinalIgnoreCase))
                    return false;
                var hash = TrustedHashRegex.Match(line);
                if (!hash.Success) continue;
                if (!hash.Groups["value"].Value.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
                trusted.Add(activeStateKey);
            }
            return states.Count == HookEventNames.Length && trusted.Count == states.Count;
        }
        catch { return false; }
    }
}
