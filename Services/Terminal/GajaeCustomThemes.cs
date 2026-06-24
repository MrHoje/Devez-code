using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DevezCode.Services.Terminal;

/// <summary>가재코드(gjc) 커스텀 테마 — DevezCode dark/soft/minimal 팔레트를 gjc TUI 에 적용.
/// gjc 는 활성 커스텀 테마 파일(<c>~/.gjc/agent/themes/&lt;name&gt;.json</c>)을 감시해 저장 시 라이브 리로드한다.
/// 그래서 고정 이름 <c>devez</c> 하나를 쓰고, 앱 테마가 바뀔 때마다 그 파일의 팔레트를 덮어써서 즉시 반영.
/// 테마 선택은 <c>~/.gjc/agent/config.yml</c> 의 <c>theme.dark</c>/<c>theme.light</c> 를 <c>devez</c> 로 고정.
/// (gjc 의 settings/extension 과 달리 테마는 코어라 config.yml 로 안정적으로 적용됨.)</summary>
public static class GajaeCustomThemes
{
    private const string ThemeName = "devez";

    private static string AgentDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gjc", "agent");
    private static string ThemesDir => Path.Combine(AgentDir, "themes");
    private static string ThemeFilePath => Path.Combine(ThemesDir, ThemeName + ".json");
    private static string ConfigYmlPath => Path.Combine(AgentDir, "config.yml");

    /// <summary>DevezCode 테마를 gjc 에 적용 — devez.json 팔레트 덮어쓰기(라이브 리로드) + config.yml theme=devez.
    /// 앱 시작 시 + 테마 변경 시 호출. 실패해도 무해(기본 테마 유지).</summary>
    public static void Apply(string devezCodeTheme)
    {
        try
        {
            Directory.CreateDirectory(ThemesDir);
            File.WriteAllText(ThemeFilePath, BuildThemeJson(devezCodeTheme), new UTF8Encoding(false));
            SetConfigYmlTheme(ThemeName);
        }
        catch { /* best-effort */ }
    }

    /// <summary>config.yml 의 theme.dark/theme.light 값을 themeName 으로 설정(나머지 키 보존).
    /// YAML 라이브러리 없이 theme: 블록의 dark/light 라인만 치환, 없으면 블록 추가.</summary>
    private static void SetConfigYmlTheme(string themeName)
    {
        string text = File.Exists(ConfigYmlPath) ? File.ReadAllText(ConfigYmlPath) : "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        bool inTheme = false, setDark = false, setLight = false, themeSeen = false;

        foreach (var raw in lines)
        {
            // theme: 블록 진입(들여쓰기 없는 top-level 'theme:').
            if (!inTheme && System.Text.RegularExpressions.Regex.IsMatch(raw, @"^theme:\s*$"))
            {
                inTheme = true; themeSeen = true;
                sb.Append("theme:\n");
                continue;
            }
            if (inTheme)
            {
                // 블록 내부(들여쓰기 라인)면 dark/light 치환, 그 외 들여쓰기 키는 보존.
                if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\s+\S"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(raw, @"^(\s+)(dark|light):");
                    if (m.Success)
                    {
                        if (m.Groups[2].Value == "dark") setDark = true; else setLight = true;
                        sb.Append($"{m.Groups[1].Value}{m.Groups[2].Value}: {themeName}\n");
                        continue;
                    }
                    sb.Append(raw).Append('\n');
                    continue;
                }
                // 블록 끝 — 누락된 dark/light 보충 후 현재 라인 처리.
                if (!setDark) { sb.Append($"  dark: {themeName}\n"); setDark = true; }
                if (!setLight) { sb.Append($"  light: {themeName}\n"); setLight = true; }
                inTheme = false;
            }
            sb.Append(raw).Append('\n');
        }
        // 파일이 theme: 블록으로 끝난 경우 누락분 보충.
        if (inTheme)
        {
            if (!setDark) sb.Append($"  dark: {themeName}\n");
            if (!setLight) sb.Append($"  light: {themeName}\n");
        }
        // theme: 블록이 아예 없으면 추가.
        if (!themeSeen)
            sb.Append($"theme:\n  dark: {themeName}\n  light: {themeName}\n");

        // 끝의 중복 개행 정리.
        var outText = sb.ToString().TrimEnd('\n') + "\n";
        File.WriteAllText(ConfigYmlPath, outText, new UTF8Encoding(false));
    }

    /// <summary>DevezCode 테마 → gjc 테마 JSON. vars(팔레트)만 테마별로 바꾸고 colors 시맨틱 매핑은 공통.</summary>
    private static string BuildThemeJson(string devezCodeTheme)
    {
        var v = Vars(devezCodeTheme);
        var sb = new StringBuilder();
        sb.Append("{\n  \"name\": \"").Append(ThemeName).Append("\",\n  \"vars\": {\n");
        int i = 0;
        foreach (var kv in v)
            sb.Append("    \"").Append(kv.Key).Append("\": \"").Append(kv.Value).Append(++i < v.Count ? "\",\n" : "\"\n");
        sb.Append("  },\n  \"colors\": ").Append(ColorsJson).Append(",\n  \"symbols\": { \"preset\": \"unicode\" }\n}\n");
        return sb.ToString();
    }

    /// <summary>테마별 팔레트(gjc colors 매핑이 참조하는 var 이름들). DevezCode dark/soft/minimal 색상에서 가져옴.</summary>
    private static Dictionary<string, string> Vars(string theme) => theme switch
    {
        "soft" => new()
        {
            ["background"] = "#F2EDE6", ["currentLine"] = "#ECE7DE", ["selection"] = "#E0D9CC",
            ["backgroundDarker"] = "#E6DFD2", ["foreground"] = "#2A2620", ["comment"] = "#5A5448",
            ["primary"] = "#5C8C4A", ["secondary"] = "#7BAA68", ["accentPurple"] = "#5C8C4A",
            ["errorRed"] = "#D95F5F", ["warningOrange"] = "#C97C1A", ["successGreen"] = "#4E7A3E",
            ["infoCyan"] = "#4E7A3E", ["emphasizedYellow"] = "#C97C1A", ["border"] = "#D8D2C6",
            ["diffAdded"] = "#5C8C4A", ["diffRemoved"] = "#D95F5F", ["diffContext"] = "#5A5448",
            ["addedBg"] = "#DEECD6", ["removedBg"] = "#F2D6D6",
        },
        "minimal" => new()
        {
            ["background"] = "#F8FAFC", ["currentLine"] = "#F1F5F9", ["selection"] = "#E2E8F0",
            ["backgroundDarker"] = "#EDF1F5", ["foreground"] = "#0F172A", ["comment"] = "#475569",
            ["primary"] = "#2563EB", ["secondary"] = "#60A5FA", ["accentPurple"] = "#2563EB",
            ["errorRed"] = "#EF4444", ["warningOrange"] = "#CA8A04", ["successGreen"] = "#15803D",
            ["infoCyan"] = "#2563EB", ["emphasizedYellow"] = "#CA8A04", ["border"] = "#E2E8F0",
            ["diffAdded"] = "#2563EB", ["diffRemoved"] = "#DC2626", ["diffContext"] = "#475569",
            ["addedBg"] = "#DBEAFE", ["removedBg"] = "#FEE2E2",
        },
        _ => new() // dark
        {
            ["background"] = "#1F1F1E", ["currentLine"] = "#272727", ["selection"] = "#303030",
            ["backgroundDarker"] = "#121212", ["foreground"] = "#EEEEEE", ["comment"] = "#808080",
            ["primary"] = "#FAB283", ["secondary"] = "#5C9CF5", ["accentPurple"] = "#9D7CD8",
            ["errorRed"] = "#E06C75", ["warningOrange"] = "#F5A742", ["successGreen"] = "#7FD88F",
            ["infoCyan"] = "#56B6C2", ["emphasizedYellow"] = "#E5C07B", ["border"] = "#404040",
            ["diffAdded"] = "#478247", ["diffRemoved"] = "#7C4444", ["diffContext"] = "#A0A0A0",
            ["addedBg"] = "#303A30", ["removedBg"] = "#3A3030",
        },
    };

    /// <summary>gjc 필수 color 토큰 → var 매핑(빌트인 opencode 테마와 동일 구조). 모든 테마 공통.</summary>
    private const string ColorsJson = """
    {
        "accent": "primary", "border": "border", "borderAccent": "accentPurple", "borderMuted": "comment",
        "success": "successGreen", "error": "errorRed", "warning": "warningOrange", "muted": "comment",
        "dim": "comment", "text": "foreground", "thinkingText": "diffContext", "selectedBg": "selection",
        "userMessageBg": "currentLine", "userMessageText": "foreground", "customMessageBg": "currentLine",
        "customMessageText": "foreground", "customMessageLabel": "primary", "toolPendingBg": "currentLine",
        "toolSuccessBg": "addedBg", "toolErrorBg": "removedBg", "toolTitle": "foreground", "toolOutput": "comment",
        "mdHeading": "secondary", "mdLink": "primary", "mdLinkUrl": "infoCyan", "mdCode": "successGreen",
        "mdCodeBlock": "foreground", "mdCodeBlockBorder": "border", "mdQuote": "emphasizedYellow",
        "mdQuoteBorder": "emphasizedYellow", "mdHr": "comment", "mdListBullet": "primary",
        "toolDiffAdded": "diffAdded", "toolDiffRemoved": "diffRemoved", "toolDiffContext": "diffContext",
        "syntaxComment": "comment", "syntaxKeyword": "secondary", "syntaxFunction": "primary",
        "syntaxVariable": "errorRed", "syntaxString": "successGreen", "syntaxNumber": "accentPurple",
        "syntaxType": "emphasizedYellow", "syntaxOperator": "infoCyan", "syntaxPunctuation": "foreground",
        "thinkingOff": "comment", "thinkingMinimal": "diffContext", "thinkingLow": "infoCyan",
        "thinkingMedium": "secondary", "thinkingHigh": "accentPurple", "thinkingXhigh": "errorRed",
        "bashMode": "infoCyan", "pythonMode": "accentPurple", "statusLineBg": "backgroundDarker",
        "statusLineSep": "border", "statusLineModel": "primary", "statusLinePath": "secondary",
        "statusLineGitClean": "successGreen", "statusLineGitDirty": "warningOrange", "statusLineContext": "infoCyan",
        "statusLineSpend": "emphasizedYellow", "statusLineStaged": "diffAdded", "statusLineDirty": "warningOrange",
        "statusLineUntracked": "errorRed", "statusLineOutput": "foreground", "statusLineCost": "primary",
        "statusLineSubagents": "accentPurple"
    }
    """;
}
