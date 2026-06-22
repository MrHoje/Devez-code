using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DevezCode.Services.Terminal;

/// <summary>claude code 커스텀 테마 — DevezCode 3개 테마 중 soft/minimal 의 톤에 맞춘 두 개.
/// <c>~/.claude/themes/devez-soft.json</c>, <c>devez-minimal.json</c> 으로 1회 설치(devez 앱이 만든 파일 있으면 보존·재사용).
/// Per-session 으로 <c>/config theme=custom:devez-soft</c> 같은 형태로 주입해서 사용.
/// (dark 는 claude 내장 "dark" 와 톤이 같으므로 별도 커스텀 불필요.)</summary>
public static class ClaudeCustomThemes
{
    private const string ThemesDirName = "themes";
    private const string SoftSlug     = "devez-soft";
    private const string MinimalSlug  = "devez-minimal";

    /// <summary>앱 시작 시 호출. 테마 파일이 없으면 생성. 기존 파일은 사용자 편집 보존 위해 덮어쓰지 않음.</summary>
    public static void EnsureInstalled()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ThemesDirName);
            Directory.CreateDirectory(dir);

            WriteIfMissing(Path.Combine(dir, SoftSlug + ".json"), SoftThemeJson);
            WriteIfMissing(Path.Combine(dir, MinimalSlug + ".json"), MinimalThemeJson);
        }
        catch { /* best-effort — 실패해도 per-session 주입이 안 될 뿐 */ }
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>DevezCode 테마 → claude theme 슬러그. per-session 주입에서 사용.</summary>
    public static string MapToClaudeTheme(string devezCodeTheme) => devezCodeTheme switch
    {
        "dark"    => "dark",
        "soft"    => "custom:" + SoftSlug,
        "minimal" => "custom:" + MinimalSlug,
        _         => "dark",
    };

    // ── 테마 정의 ──────────────────────────────────────────────
    // DevezCode Soft 팔레트를 claude TUI 토큰에 매핑. base=light (라이트 베이스에 톤만 덮어씀).
    // 미지정 토큰은 light 베이스 폴백. 토큰 이름/효과는 claude code 공식 문서 기준.
    private const string SoftThemeJson = """
    {
      "name": "Devez Soft",
      "base": "light",
      "overrides": {
        "claude": "#5C8C4A",
        "claudeShimmer": "#7BAA68",
        "text": "#2A2620",
        "inverseText": "#FAF7F2",
        "inactive": "#5A5448",
        "inactiveShimmer": "#7A7368",
        "subtle": "#D8D2C6",
        "suggestion": "#5C8C4A",
        "permission": "#5C8C4A",
        "permissionShimmer": "#7BAA68",
        "remember": "#5C8C4A",
        "success": "#4E7A3E",
        "warning": "#C97C1A",
        "error": "#D95F5F",
        "merged": "#5C8C4A",
        "promptBorder": "#5C8C4A",
        "promptBorderShimmer": "#7BAA68",
        "planMode": "#5C8C4A",
        "autoAccept": "#5C8C4A",
        "bashBorder": "#5C8C4A",
        "ide": "#5A5448",
        "fastMode": "#5C8C4A",
        "userMessageBackground": "#DEECD6",
        "userMessageBackgroundHover": "#C2D8B0",
        "messageActionsBackground": "#ECE7DE",
        "bashMessageBackgroundColor": "#ECE7DE",
        "memoryBackgroundColor": "#ECE7DE",
        "selectionBg": "#C2D8B0",
        "diffAdded": "#DEECD6",
        "diffAddedDimmed": "#F2EDE6",
        "diffAddedWord": "#5C8C4A",
        "diffRemoved": "#F2D6D6",
        "diffRemovedDimmed": "#F2EDE6",
        "diffRemovedWord": "#D95F5F",
        "rate_limit_fill": "#5C8C4A",
        "rate_limit_empty": "#D8D2C6",
        "briefLabelYou": "#5C8C4A",
        "briefLabelClaude": "#5A5448"
      }
    }
    """;

    // DevezCode Minimal 팔레트 (cool white + blue).
    private const string MinimalThemeJson = """
    {
      "name": "Devez Minimal",
      "base": "light",
      "overrides": {
        "claude": "#2563EB",
        "claudeShimmer": "#60A5FA",
        "text": "#0F172A",
        "inverseText": "#FFFFFF",
        "inactive": "#475569",
        "inactiveShimmer": "#64748B",
        "subtle": "#E2E8F0",
        "suggestion": "#2563EB",
        "permission": "#2563EB",
        "permissionShimmer": "#60A5FA",
        "remember": "#2563EB",
        "success": "#15803D",
        "warning": "#CA8A04",
        "error": "#EF4444",
        "merged": "#2563EB",
        "promptBorder": "#2563EB",
        "promptBorderShimmer": "#60A5FA",
        "planMode": "#2563EB",
        "autoAccept": "#2563EB",
        "bashBorder": "#2563EB",
        "ide": "#475569",
        "fastMode": "#2563EB",
        "userMessageBackground": "#DBEAFE",
        "userMessageBackgroundHover": "#C5D8F8",
        "messageActionsBackground": "#F1F5F9",
        "bashMessageBackgroundColor": "#F1F5F9",
        "memoryBackgroundColor": "#F1F5F9",
        "selectionBg": "#C5D8F8",
        "diffAdded": "#DBEAFE",
        "diffAddedDimmed": "#F8FAFC",
        "diffAddedWord": "#2563EB",
        "diffRemoved": "#FEE2E2",
        "diffRemovedDimmed": "#F8FAFC",
        "diffRemovedWord": "#DC2626",
        "rate_limit_fill": "#2563EB",
        "rate_limit_empty": "#E2E8F0",
        "briefLabelYou": "#2563EB",
        "briefLabelClaude": "#475569"
      }
    }
    """;
}
