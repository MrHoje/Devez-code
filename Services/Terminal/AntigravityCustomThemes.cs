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
/// 주의: 스킴 값은 agy 1.1.1 바이너리 실측 후보(Solarized/Dark 계열) 기반 — 정확한 enum 은
/// 런타임 검증 항목(설치 후 /settings 변경 → settings.json diff 로 확정). 알 수 없는 값이면
/// agy 가 기본 스킴으로 폴백하므로 기동 자체는 깨지지 않는다.</summary>
public static class AntigravityCustomThemes
{
    private static string SettingsJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini", "antigravity-cli", "settings.json");

    /// <summary>DevezCode 테마 → agy 내장 colorScheme 값.</summary>
    public static string MapToAntigravityScheme(string devezCodeTheme) => devezCodeTheme switch
    {
        "dark" => "Dark",
        "soft" or "minimal" => "Solarized Light",
        _ => "Dark",
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
