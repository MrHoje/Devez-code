using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DevezCode.Services;

/// <summary>Claude Code 전역설정(~/.claude/settings.json) 의 일부 키를 읽고/쓴다.
/// 나머지 키(statusLine, hooks 등)는 보존한다.
/// 또한 전역 런타임 상태 파일(<c>~/.claude.json</c>)의 일부 UI 토글도 강제한다.</summary>
public static class ClaudeGlobalSettings
{
    private static string ClaudeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    private static string SettingsJsonPath => Path.Combine(ClaudeDir, "settings.json");
    /// <summary>Claude Code 전역 런타임 상태/설정(<c>~/.claude.json</c>).
    /// <c>~/.claude/settings.json</c> 과 별개 — /config 의 일부 UI 토글(leftArrowOpensAgents 등)이 여기 저장된다.</summary>
    private static string ClaudeJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    // "leftArrowOpensAgents": true|false  (공백 가변). 대규모 ~/.claude.json 을 전체 재직렬화하지 않고
    // 이 키만 수술적으로 패치하기 위한 패턴. 다른 키/순서를 보존한다.
    private static readonly Regex LeftArrowOpensAgentsRe = new(
        "\"leftArrowOpensAgents\"\\s*:\\s*(true|false)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Claude 기본값(미지정 시 30일 동안 세션 트랜스크립트 유지).</summary>
    public const int DefaultCleanupPeriodDays = 30;

    /// <summary>영구 보관(사실상 정리 안 함) 표현용 일수 — 약 273년.</summary>
    public const int PermanentDays = 99999;

    public const string DefaultPermissionMode = "acceptEdits";

    public static bool IsSupportedPermissionMode(string? value)
        => value is "acceptEdits" or "plan" or "auto" or "bypassPermissions";

    /// <summary>Claude Code 전역 <c>permissions.defaultMode</c>. GUI에서 지원하지 않는 값은 안전한 기본값으로 정규화한다.</summary>
    public static string GetDefaultPermissionMode()
    {
        try
        {
            if (!File.Exists(SettingsJsonPath)) return DefaultPermissionMode;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsJsonPath));
            if (doc.RootElement.TryGetProperty("permissions", out var permissions)
                && permissions.ValueKind == JsonValueKind.Object
                && permissions.TryGetProperty("defaultMode", out var mode)
                && mode.ValueKind == JsonValueKind.String
                && IsSupportedPermissionMode(mode.GetString()))
                return mode.GetString()!;
        }
        catch { /* 손상/경합 시 GUI 기본값 */ }
        return DefaultPermissionMode;
    }

    /// <summary>Claude Code 전역 <c>permissions.defaultMode</c>만 병합 저장하고 나머지 설정은 보존한다.</summary>
    public static bool SetDefaultPermissionMode(string? value)
    {
        if (!IsSupportedPermissionMode(value)) return false;
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return AtomicFile.TryUpdateAllText(SettingsJsonPath, original =>
        {
            JsonNode? root;
            try { root = JsonNode.Parse(string.IsNullOrWhiteSpace(original) ? "{}" : original); }
            catch { return null; }
            if (root is not JsonObject rootObj) return null;
            if (rootObj["permissions"] is not JsonObject permissions)
            {
                permissions = new JsonObject();
                rootObj["permissions"] = permissions;
            }
            permissions["defaultMode"] = value;
            return rootObj.ToJsonString(opts);
        });
    }

    /// <summary>세션 유지기간(cleanupPeriodDays). 미지정/파싱 실패 시 기본값.</summary>
    public static int GetCleanupPeriodDays()
    {
        try
        {
            if (!File.Exists(SettingsJsonPath)) return DefaultCleanupPeriodDays;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsJsonPath));
            if (doc.RootElement.TryGetProperty("cleanupPeriodDays", out var v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var days) && days > 0)
                // 프리셋에 없는 대형 값(과거 36500 등)은 영구 보관으로 매핑 → 콤보 빈칸 방지
                return days >= 999 ? PermanentDays : days;
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

    /// <summary>Claude 세션 기동 직전 호출. Claude Code /config 의 <c>← opens agents</c>
    /// (<c>leftArrowOpensAgents</c>) 를 항상 false 로 강제한다.
    /// 이 키는 room <c>--settings</c> 가 아니라 전역 <c>~/.claude.json</c> 에만 존재하므로
    /// 프로세스 시작 전에 패치한다. 이미 false 면 디스크 write 생략.
    /// 대규모 상태 파일이라 전체 JSON 재직렬화 없이 해당 키만 수술적으로 고친다.</summary>
    public static void EnsureLeftArrowOpensAgentsDisabled()
    {
        try
        {
            if (!File.Exists(ClaudeJsonPath)) return; // 아직 claude 미사용 — 건드릴 파일 없음

            var text = File.ReadAllText(ClaudeJsonPath);
            if (string.IsNullOrWhiteSpace(text)) return;

            var m = LeftArrowOpensAgentsRe.Match(text);
            string next;
            if (m.Success)
            {
                if (string.Equals(m.Groups[1].Value, "false", StringComparison.Ordinal)) return; // 이미 목표값
                // 매치 구간 전체를 "leftArrowOpensAgents": false 로 교체(공백 스타일 정규화)
                next = text.Substring(0, m.Index)
                     + "\"leftArrowOpensAgents\": false"
                     + text.Substring(m.Index + m.Length);
            }
            else
            {
                // 키 없음 — 루트 객체의 마지막 `}` 직전에 삽입.
                // 기본값이 true 이므로 키가 없으면 false 를 명시해야 한다.
                var close = text.LastIndexOf('}');
                if (close < 0) return;
                var before = text.AsSpan(0, close).TrimEnd();
                // 직전 비공백이 `{` 면 빈 객체, 아니면 콤마 필요
                var needsComma = before.Length > 0 && before[^1] != '{';
                var insert = (needsComma ? "," : "") + "\n  \"leftArrowOpensAgents\": false\n";
                next = string.Concat(text.AsSpan(0, close), insert, text.AsSpan(close));
            }

            // 패치 결과가 유효 JSON 인지 확인 — 실패 시 원본 보존
            try { using var _ = JsonDocument.Parse(next); }
            catch { return; }

            AtomicFile.WriteAllText(ClaudeJsonPath, next);
        }
        catch { /* best-effort — 실패해도 세션 기동은 막지 않음 */ }
    }
}
