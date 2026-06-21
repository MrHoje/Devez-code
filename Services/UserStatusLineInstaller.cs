using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevezCode.Services;

/// <summary>claude code 의 커스텀 statusline(~/.claude/statusline.js) 을 사용자 머신에 보장.
/// 1) Resources\StatusLine\statusline.js 을 ~/.claude\statusline.js 로 복사(없을 때만 — 사용자 수정 보존).
/// 2) ~/.claude\settings.json 의 statusLine 키가 우리 스크립트를 가리키는지 확인,
///    아니면(또는 부재) 다른 설정은 보존한 채 statusLine 만 머지.
/// 다른 PC 에서 DevezCode 첫 실행 시 statusline.js + settings.json statusLine 이 자동 설치되어
/// branch/model/effort/ctx/5h/week/token 표시가 즉시 동작한다.</summary>
public static class UserStatusLineInstaller
{
    private static string ClaudeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    private static string StatusLineJsPath => Path.Combine(ClaudeDir, "statusline.js");
    private static string SettingsJsonPath => Path.Combine(ClaudeDir, "settings.json");

    private static string BundledScriptPath => Path.Combine(
        AppContext.BaseDirectory, "Resources", "StatusLine", "statusline.js");

    /// <summary>statusline.js 가 설치돼 있고 settings.json 의 statusLine 이 그 스크립트를 가리키는지.</summary>
    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(StatusLineJsPath)) return false;
            if (!File.Exists(SettingsJsonPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsJsonPath));
            if (!doc.RootElement.TryGetProperty("statusLine", out var sl)) return false;
            if (sl.ValueKind != JsonValueKind.Object) return false;
            if (!sl.TryGetProperty("command", out var cmd)) return false;
            if (cmd.ValueKind != JsonValueKind.String) return false;
            var cmdStr = cmd.GetString() ?? "";
            return cmdStr.Contains("statusline.js", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>statusline 스크립트와 settings.json 의 statusLine 등록을 보장. 없으면 설치.</summary>
    public static void EnsureInstalled()
    {
        if (IsInstalled()) return;
        try { InstallScript(); } catch { /* best effort */ }
        try { InstallSettingsEntry(); } catch { /* best effort */ }
    }

    /// <summary>번들 statusline.js 를 ~/.claude\statusline.js 로 복사. 이미 있으면 스킵(사용자 수정 보존).</summary>
    private static void InstallScript()
    {
        if (File.Exists(StatusLineJsPath)) return;
        if (!File.Exists(BundledScriptPath)) return;
        Directory.CreateDirectory(ClaudeDir);
        File.Copy(BundledScriptPath, StatusLineJsPath);
    }

    /// <summary>~/.claude\settings.json 에 statusLine 키를 머지(나머지 설정은 보존).
    /// 스크립트/command 가 우리 statusline.js 를 가리키도록 강제. node.exe 경로는 시스템에서 자동 탐지.</summary>
    private static void InstallSettingsEntry()
    {
        if (!File.Exists(StatusLineJsPath)) return;

        JsonNode? root;
        try
        {
            root = File.Exists(SettingsJsonPath)
                ? JsonNode.Parse(File.ReadAllText(SettingsJsonPath))
                : JsonNode.Parse("{}");
        }
        catch { root = JsonNode.Parse("{}"); }
        if (root is not JsonObject rootObj) rootObj = JsonNode.Parse("{}") as JsonObject ?? new JsonObject();

        // 이미 우리 statusline.js 를 가리키는 statusLine 이 있으면 그대로 둠
        if (rootObj["statusLine"] is JsonObject existing &&
            existing["command"]?.GetValue<string>()?.Contains("statusline.js", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var node = FindNodePath();
        if (node is null) return; // node 가 없으면 statusline 도 의미 없음 — 조용히 스킵

        // refreshInterval 은 의도적으로 넣지 않음 — event-driven 으로 충분,
        // 명시 주기는 idle 중 CPU 낭비.
        rootObj["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = $"\"{node}\" \"{StatusLineJsPath}\"",
        };

        Directory.CreateDirectory(ClaudeDir);
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(SettingsJsonPath, rootObj.ToJsonString(opts), new UTF8Encoding(false));
    }

    /// <summary>where.exe 로 node.exe 절대경로 탐색, 실패 시 일반 설치 경로 폴백.</summary>
    private static string? FindNodePath()
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", "node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var first = p?.StandardOutput.ReadToEnd().Split('\n').FirstOrDefault()?.Trim();
            p?.WaitForExit(2000);
            if (!string.IsNullOrEmpty(first) && File.Exists(first)) return first;
        }
        catch { }
        foreach (var c in new[] {
            @"C:\Program Files\nodejs\node.exe",
            @"C:\Program Files (x86)\nodejs\node.exe",
        })
            if (File.Exists(c)) return c;
        return null;
    }
}
