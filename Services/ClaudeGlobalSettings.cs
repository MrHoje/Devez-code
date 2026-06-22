using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevezCode.Services;

/// <summary>Claude Code 전역설정(~/.claude/settings.json) 의 일부 키를 읽고/쓴다.
/// 나머지 키(statusLine, hooks 등)는 보존한다.</summary>
public static class ClaudeGlobalSettings
{
    private static string ClaudeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    private static string SettingsJsonPath => Path.Combine(ClaudeDir, "settings.json");

    /// <summary>Claude 기본값(미지정 시 30일 동안 세션 트랜스크립트 유지).</summary>
    public const int DefaultCleanupPeriodDays = 30;

    /// <summary>세션 유지기간(cleanupPeriodDays). 미지정/파싱 실패 시 기본값.</summary>
    public static int GetCleanupPeriodDays()
    {
        try
        {
            if (!File.Exists(SettingsJsonPath)) return DefaultCleanupPeriodDays;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsJsonPath));
            if (doc.RootElement.TryGetProperty("cleanupPeriodDays", out var v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var days) && days > 0)
                return days;
        }
        catch { /* 손상 파일 → 기본값 */ }
        return DefaultCleanupPeriodDays;
    }

    /// <summary>cleanupPeriodDays 머지 저장(나머지 설정 보존).</summary>
    public static void SetCleanupPeriodDays(int days)
    {
        if (days <= 0) days = DefaultCleanupPeriodDays;

        JsonNode? root;
        try
        {
            root = File.Exists(SettingsJsonPath)
                ? JsonNode.Parse(File.ReadAllText(SettingsJsonPath))
                : JsonNode.Parse("{}");
        }
        catch { root = JsonNode.Parse("{}"); }
        if (root is not JsonObject rootObj) rootObj = new JsonObject();

        rootObj["cleanupPeriodDays"] = days;

        Directory.CreateDirectory(ClaudeDir);
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(SettingsJsonPath, rootObj.ToJsonString(opts), new UTF8Encoding(false));
    }
}
