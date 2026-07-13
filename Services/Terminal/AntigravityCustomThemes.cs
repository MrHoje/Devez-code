using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevezCode.Services.Terminal;

/// <summary>안티그래비티(agy) 테마 — DevezCode dark/soft/minimal 을 agy 내장 컬러스킴으로 매핑.
/// agy 는 커스텀 팔레트 JSON 을 지원하지 않으므로(grok 과 동일한 수준)
/// <c>~/.gemini/antigravity-cli/settings.json</c> 의 <c>colorScheme</c> 만 갱신한다.
/// 다른 설정 키는 보존(merge). 이미 떠 있는 agy 세션은 세션 재시작 후 반영. 실패해도 무해.
/// 스킴 값은 대소문자 구분 소문자+공백 형식 (agy 1.1.1 실기 검증: "Dark" 는
/// "unrecognized value" 로 거부되어 TUI 시작 시 Settings Error 화면이 뜬다).
/// 밝은 앱 테마는 agy 의 "terminal" 스킴으로 매핑해 DevezCode 터미널 팔레트를 그대로 상속한다.</summary>
public static class AntigravityCustomThemes
{
    private static string SettingsJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini", "antigravity-cli", "settings.json");

    /// <summary>DevezCode 테마 → agy 내장 colorScheme 값.</summary>
    public static string MapToAntigravityScheme(string devezCodeTheme) => devezCodeTheme switch
    {
        "dark" => "dark",
        "soft" or "minimal" => "terminal",
        _ => "dark",
    };

    /// <summary>앱 시작·테마 변경·세션 기동 시 호출. settings.json 의 colorScheme 을 매핑값으로 기록.</summary>
    public static void Apply(string devezCodeTheme)
    {
        try
        {
            var scheme = MapToAntigravityScheme(devezCodeTheme);
            var path = SettingsJsonPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            JsonNode? root;
            try
            {
                root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : JsonNode.Parse("{}");
            }
            catch { root = JsonNode.Parse("{}"); }
            root ??= JsonNode.Parse("{}");

            string? current = null;
            if (root!["colorScheme"] is JsonValue v) v.TryGetValue(out current);
            if (current == scheme) return; // 변경 없음 — 불필요한 쓰기 방지

            root["colorScheme"] = scheme;
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, root.ToJsonString(opts), new UTF8Encoding(false));
        }
        catch { /* best-effort */ }
    }
}
