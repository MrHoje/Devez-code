using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            var json = File.ReadAllText(CodexHooksJsonPath);
            // 우리 스크립트 경로를 가리키는 command 가 하나라도 있으면 OK
            return json.Contains("DevezCode") && json.Contains("codex-hook.ps1");
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
            var scriptPath = ScriptInstallPath;
            var hookCommand = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";

            // 3개 이벤트에 등록
            foreach (var eventName in new[] { "UserPromptSubmit", "Stop", "SessionStart" })
            {
                // 이미 같은 command 가 등록돼 있으면 skip
                if (HasOurHook(hooksObj, eventName, hookCommand)) continue;

                // hooksObj[eventName] 이 배열인지 확인하고 우리 entry 추가
                JsonArray eventArray;
                if (hooksObj[eventName] is JsonArray arr)
                {
                    eventArray = arr;
                }
                else
                {
                    eventArray = new JsonArray();
                    hooksObj[eventName] = eventArray;
                }
                eventArray.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "command", ["command"] = hookCommand }
                    }
                });
            }

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(CodexHooksJsonPath, root.ToJsonString(opts), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
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

    /// <summary>codex 훅 사용 가능 여부 — 스크립트 설치 + hooks.json 등록 모두.</summary>
    public static bool HookAssetsHealthy()
        => File.Exists(ScriptInstallPath) && IsHooksJsonInstalled();
}
