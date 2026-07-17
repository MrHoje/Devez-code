using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DevezCode.Services.Terminal;

/// <summary>Kimi Code CLI 테마 — DevezCode dark/soft/minimal 을 kimi 의 dark/light 로 매핑.
/// kimi 는 커스텀 팔레트를 지원하지 않아(grok/codex 수준) <c>~/.kimi-code/tui.toml</c> 의
/// 최상위 <c>theme</c> 키만 갱신한다(섹션 없는 top-level 키 — grok 의 [ui] theme 와 위치가 다름).
/// 이미 떠 있는 세션은 재시작/재진입 후 반영.</summary>
public static class KimiCustomThemes
{
    private static string TuiTomlPath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
            var home = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
            return Path.Combine(home, "tui.toml");
        }
    }

    /// <summary>DevezCode 테마 → kimi theme 값.</summary>
    public static string MapToKimiTheme(string devezCodeTheme)
        => string.Equals(devezCodeTheme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";

    public static void Apply(string devezCodeTheme)
    {
        try
        {
            var value = MapToKimiTheme(devezCodeTheme);
            var path = TuiTomlPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            File.WriteAllText(path, UpsertTopLevelTheme(text, value), new UTF8Encoding(false));
        }
        catch { /* best-effort */ }
    }

    /// <summary>최상위(첫 테이블 이전) theme 키만 교체/삽입. 나머지 설정 보존.</summary>
    internal static string UpsertTopLevelTheme(string text, string themeValue)
    {
        text = text.Replace("\r\n", "\n");
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        bool set = false, beforeAnyTable = true;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                // 첫 테이블 진입 전에 theme 이 없었으면 여기서 최상위에 삽입.
                if (beforeAnyTable && !set)
                {
                    sb.Append("theme = \"").Append(themeValue).Append("\"\n");
                    set = true;
                }
                beforeAnyTable = false;
                sb.Append(raw).Append('\n');
                continue;
            }
            if (beforeAnyTable && !set && Regex.IsMatch(trimmed, @"^theme\s*=", RegexOptions.IgnoreCase))
            {
                sb.Append("theme = \"").Append(themeValue).Append("\"\n");
                set = true;
                continue;
            }
            sb.Append(raw).Append('\n');
        }

        if (!set) sb.Insert(0, "theme = \"" + themeValue + "\"\n");
        return sb.ToString().TrimEnd('\n') + "\n";
    }
}
