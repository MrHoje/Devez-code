using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DevezCode.Services.Terminal;

/// <summary>Grok Build CLI 테마 — DevezCode dark/soft/minimal 을 grok 내장 테마 이름으로 매핑.
/// Grok 은 커스텀 팔레트 JSON 을 지원하지 않으므로(claude/opencode/gjc 와 다름)
/// <c>~/.grok/config.toml</c> 의 <c>[ui] theme</c> 만 갱신한다.
/// dark → groknight, soft/minimal → grokday (라이트 계열 공유, codex 와 동일한 수준).
/// 터미널 배경은 xterm DevezCode 스킴(공통). 유저 프롬프트 박스 재색은 TerminalHostView
/// 바이트 치환(실측 truecolor, codex 패턴) — 1차 통합에선 스킴+config 매핑만.</summary>
public static class GrokCustomThemes
{
    private static string ConfigTomlPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "config.toml");

    /// <summary>DevezCode 테마 → grok 내장 theme 슬러그.</summary>
    public static string MapToGrokTheme(string devezCodeTheme) => devezCodeTheme switch
    {
        "dark" => "groknight",
        "soft" or "minimal" => "grokday",
        _ => "groknight",
    };

    /// <summary>앱 시작·테마 변경 시 호출. config.toml [ui] theme 을 매핑값으로 기록.
    /// 이미 떠 있는 grok 세션은 /theme 또는 세션 재시작 후 반영. 실패해도 무해.</summary>
    public static void Apply(string devezCodeTheme)
    {
        try
        {
            var slug = MapToGrokTheme(devezCodeTheme);
            var path = ConfigTomlPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            File.WriteAllText(path, UpsertUiTheme(text, slug), new UTF8Encoding(false));
        }
        catch { /* best-effort */ }
    }

    /// <summary>[ui] 테이블의 theme 키만 교체/추가. 나머지 설정 보존.
    /// 간단 라인 파서 — 주석·멀티라인 문자열은 일반 사용자 config 범위에서 충분.</summary>
    internal static string UpsertUiTheme(string text, string themeSlug)
    {
        text = text.Replace("\r\n", "\n");
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        bool inUi = false, themeSet = false, uiSeen = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            // 새 테이블 헤더
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inUi && !themeSet)
                {
                    sb.Append("theme = \"").Append(themeSlug).Append("\"\n");
                    themeSet = true;
                }
                inUi = string.Equals(trimmed, "[ui]", StringComparison.OrdinalIgnoreCase);
                if (inUi) uiSeen = true;
                sb.Append(raw).Append('\n');
                continue;
            }

            if (inUi && Regex.IsMatch(trimmed, @"^theme\s*=", RegexOptions.IgnoreCase))
            {
                sb.Append("theme = \"").Append(themeSlug).Append("\"\n");
                themeSet = true;
                continue;
            }

            sb.Append(raw).Append('\n');
        }

        if (inUi && !themeSet)
            sb.Append("theme = \"").Append(themeSlug).Append("\"\n");

        if (!uiSeen)
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            sb.Append("\n[ui]\ntheme = \"").Append(themeSlug).Append("\"\n");
        }

        return sb.ToString().TrimEnd('\n') + "\n";
    }
}
