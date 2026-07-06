using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DevezCode.Services.Terminal;

/// <summary>claude code 커스텀 테마 — DevezCode 3개 테마 중 soft/minimal 의 톤에 맞춘 두 개.
/// <c>~/.claude/themes/devez-soft.json</c>, <c>devez-minimal.json</c> 으로 매 시작 시 번들 내용으로
/// 항상 재생성(statusline.js 와 동일한 관리 방식) — 팔레트를 코드에서 바꾸면 다음 실행에 바로 반영.
/// 이 두 파일은 우리 전용 슬러그라 사용자가 직접 편집해 커스터마이즈할 대상이 아니다.
/// Per-session 으로 <c>/config theme=custom:devez-soft</c> 같은 형태로 주입해서 사용.
/// (dark 는 claude 내장 "dark" 와 톤이 같으므로 별도 커스텀 불필요.)</summary>
public static class ClaudeCustomThemes
{
    private const string ThemesDirName = "themes";
    private const string SoftSlug     = "devez-soft";
    private const string MinimalSlug  = "devez-minimal";

    /// <summary>앱 시작 시 호출. 매번 번들 내용으로 덮어써 최신 팔레트를 강제 반영한다.</summary>
    public static void EnsureInstalled()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ThemesDirName);
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, SoftSlug + ".json"), SoftThemeJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, MinimalSlug + ".json"), MinimalThemeJson, new UTF8Encoding(false));
        }
        catch { /* best-effort — 실패해도 per-session 주입이 안 될 뿐 */ }
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
    // subtle: "마지막 프롬프트" 배너(inactive 배경) 위 텍스트로 쓰인다. 원래 값(#D8D2C6 / #E2E8F0)은
    // 라이트 페이지 위 아주 옅은 보더용이라 이 배경 위에서 dim 처리 후 거의 안 보였다.
    // text(본문 폰트색)와 같은 값으로 맞춤. 주의: 이 JSON은 그대로 파일로 쓰이므로
    // JSON 문자열 리터럴 내부에는 "//" 주석을 절대 넣지 말 것(파싱 실패 → 기본 테마 폴백).
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
        "subtle": "#2A2620",
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
        "diffAddedWord": "#7BAA68",
        "diffRemoved": "#F2D6D6",
        "diffRemovedDimmed": "#F2EDE6",
        "diffRemovedWord": "#E8A0A0",
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
        "subtle": "#0F172A",
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
        "diffAddedWord": "#60A5FA",
        "diffRemoved": "#FEE2E2",
        "diffRemovedDimmed": "#F8FAFC",
        "diffRemovedWord": "#F87171",
        "rate_limit_fill": "#2563EB",
        "rate_limit_empty": "#E2E8F0",
        "briefLabelYou": "#2563EB",
        "briefLabelClaude": "#475569"
      }
    }
    """;
}
