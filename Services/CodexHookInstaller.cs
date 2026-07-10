using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DevezCode.Services;

/// <summary>codex 훅(codex-hook.ps1) 을 사용자 머신에 설치/유지.
/// 1) 스크립트 본문을 %LOCALAPPDATA%\DevezCode\codex\hook.ps1 에 항상 최신본으로 기록 (앱 시작 시).
/// 2) ~/.codex/hooks.json 의 UserPromptSubmit / Stop / SessionStart 이벤트에 우리 훅을 등록
///    (다른 훅은 보존 — merge).</summary>
public static class CodexHookInstaller
{
    public static string ScriptInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "codex", "hook.ps1");

    public static string CodexHooksJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "hooks.json");

    public static string CodexConfigTomlPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

    private static readonly string[] HookEventNames = { "UserPromptSubmit", "Stop", "SessionStart" };

    private static readonly Regex HookStateHeaderRegex = new(
        @"^\s*\[hooks\.state\.'(?<key>[^']+)'\]\s*(?:#.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DisabledStateRegex = new(
        @"^(?<prefix>\s*enabled\s*=\s*)false(?<suffix>\s*(?:#.*)?)$",
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

    /// <summary>스크립트를 ScriptInstallPath 에 기록. 매 시작 시 호출해도 동일 내용이라 noop.</summary>
    public static void EnsureScriptInstalled()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptInstallPath)!);
            var content = ReadEmbeddedScript();
            if (!File.Exists(ScriptInstallPath) || File.ReadAllText(ScriptInstallPath) != content)
                File.WriteAllText(ScriptInstallPath, content, new UTF8Encoding(false));
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
            var hookCommand = BuildHookCommand();
            return HookEventNames.All(eventName => HasOurHook(hooksObj, eventName, hookCommand));
        }
        catch { return false; }
    }

    /// <summary>~/.codex/hooks.json 에 UserPromptSubmit / Stop / SessionStart 훅을 등록 (merge).
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

            if (root == null) root = JsonNode.Parse("{}");

            // root.hooks 객체 보장
            if (root["hooks"] is not JsonObject hooksObj)
            {
                hooksObj = new JsonObject();
                root["hooks"] = hooksObj;
            }

            // 우리 커맨드 — 절대경로 (공백 있어도 안전)
            var hookCommand = BuildHookCommand();

            // 3개 이벤트에 등록. 구버전 command 가 있으면 같은 위치에서 최신 command 로 교체해
            // 기존 PC 에 옛 훅+새 훅이 중복 등록되지 않게 한다.
            foreach (var eventName in HookEventNames)
                EnsureOurHook(hooksObj, eventName, hookCommand);

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(CodexHooksJsonPath, root.ToJsonString(opts), new UTF8Encoding(false));

            // Codex 는 훅별 활성 상태를 config.toml 의 [hooks.state.'...'] 에 따로 저장한다.
            // hooks.json 에 command 가 멀쩡히 있어도 여기서 enabled=false 면 해당 이벤트만 조용히
            // 실행되지 않는다. 앱이 소유한 훅만 찾아 매 시작/세션 실행 때 재활성화한다.
            return EnsureOurHookStatesEnabled(hooksObj, hookCommand);
        }
        catch { return false; }
    }

    private static string BuildHookCommand()
        // -WindowStyle Hidden 금지: codex(Rust)는 훅 프로세스를 부모 콘솔을 상속해 스폰한다.
        // 상속된 powershell 에 -WindowStyle Hidden 을 주면 ShowWindow(GetConsoleWindow(), SW_HIDE) 가
        // '공유' 콘솔(외부 터미널 창)에 적용돼 세션 창이 최소화/숨김된다(프롬프트 전송·응답 완료 시점).
        // 콘솔을 상속하므로 새 창이 뜨지 않아 Hidden 없이도 flash 가 없고, DevezCode ConPTY(헤드리스)
        // 에서도 창이 없어 무해하다. (claude 는 windowsHide 로 스폰돼 Hidden 이 no-op 이라 영향 없었음.)
        => $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptInstallPath}\"";

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
        return normalizedCommand.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase);
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
    private static HashSet<string> FindOurHookStateKeys(JsonObject hooksObj, string command)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                        hook["command"]?.GetValue<string>() != command) continue;
                    var stateEvent = ToHookStateEventName(eventName);
                    keys.Add(NormalizeHookStateKey(
                        $"{CodexHooksJsonPath}:{stateEvent}:{entryIndex}:{hookIndex}"));
                }
            }
        }
        return keys;
    }

    /// <summary>
    /// Codex 가 config.toml 에 명시적으로 비활성화한 DevezCode 훅만 enabled=true 로 복구한다.
    /// 다른 사용자/플러그인 훅의 상태와 trusted_hash 는 건드리지 않는다.
    /// </summary>
    private static bool EnsureOurHookStatesEnabled(JsonObject hooksObj, string command)
    {
        try
        {
            if (!File.Exists(CodexConfigTomlPath)) return true;
            var ourStateKeys = FindOurHookStateKeys(hooksObj, command);
            if (ourStateKeys.Count == 0) return false;

            var raw = File.ReadAllText(CodexConfigTomlPath);
            var newline = raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            string? activeStateKey = null;
            bool changed = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var header = HookStateHeaderRegex.Match(lines[i]);
                if (header.Success)
                {
                    activeStateKey = NormalizeHookStateKey(header.Groups["key"].Value);
                    continue;
                }

                // 다른 TOML table 시작. 직전 hooks.state 범위 종료.
                if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    activeStateKey = null;
                    continue;
                }

                if (activeStateKey == null || !ourStateKeys.Contains(activeStateKey)) continue;
                var disabled = DisabledStateRegex.Match(lines[i]);
                if (!disabled.Success) continue;
                lines[i] = disabled.Groups["prefix"].Value + "true" + disabled.Groups["suffix"].Value;
                changed = true;
            }

            if (changed)
                File.WriteAllText(CodexConfigTomlPath, string.Join(newline, lines), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
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
        if (!File.Exists(ScriptInstallPath) || !IsHooksJsonInstalled()) return false;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(CodexHooksJsonPath));
            if (root?["hooks"] is not JsonObject hooksObj) return false;
            var command = BuildHookCommand();
            var stateKeys = FindOurHookStateKeys(hooksObj, command);
            if (!File.Exists(CodexConfigTomlPath)) return stateKeys.Count == HookEventNames.Length;

            string? activeStateKey = null;
            foreach (var line in File.ReadLines(CodexConfigTomlPath))
            {
                var header = HookStateHeaderRegex.Match(line);
                if (header.Success)
                {
                    activeStateKey = NormalizeHookStateKey(header.Groups["key"].Value);
                    continue;
                }
                if (line.TrimStart().StartsWith("[", StringComparison.Ordinal)) activeStateKey = null;
                if (activeStateKey != null && stateKeys.Contains(activeStateKey) && DisabledStateRegex.IsMatch(line))
                    return false;
            }
            return stateKeys.Count == HookEventNames.Length;
        }
        catch { return false; }
    }
}
