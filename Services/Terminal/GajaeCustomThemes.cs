using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DevezCode.Services.Terminal;

/// <summary>가재코드(gjc) 커스텀 테마 — DevezCode dark/soft/minimal 팔레트를 gjc TUI 에 적용.
/// gjc 는 활성 커스텀 테마 파일(<c>~/.gjc/agent/themes/&lt;name&gt;.json</c>)을 감시해 저장 시 라이브 리로드한다.
/// 그래서 고정 이름 <c>devez</c> 하나를 쓰고, 앱 테마가 바뀔 때마다 그 파일의 팔레트를 덮어써서 즉시 반영.
/// <c>~/.gjc/agent/config.yml</c> 에서 테마를 <c>devez</c> 로 고정하고,
/// Windows 에서 <c>gh.exe</c> 콘솔이 깜빡일 수 있는 GitHub star reminder 를 비활성화.
/// (gjc 의 settings/extension 과 달리 테마는 코어라 config.yml 로 안정적으로 적용됨.)</summary>
public static class GajaeCustomThemes
{
    private const string ThemeName = "devez";

    private static string AgentDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gjc", "agent");
    private static string ThemesDir => Path.Combine(AgentDir, "themes");
    private static string ThemeFilePath => Path.Combine(ThemesDir, ThemeName + ".json");
    private static string ConfigYmlPath => Path.Combine(AgentDir, "config.yml");

    /// <summary>DevezCode 설정을 gjc 에 적용 — devez.json 팔레트 덮어쓰기(라이브 리로드) +
    /// config.yml theme=devez, starReminder.enabled=false.
    /// 앱 시작 시 + 테마 변경 시 호출. 실패해도 무해(기본 테마 유지).</summary>
    public static void Apply(string devezCodeTheme)
    {
        try
        {
            Directory.CreateDirectory(ThemesDir);
            File.WriteAllText(ThemeFilePath, BuildThemeJson(devezCodeTheme), new UTF8Encoding(false));
            SetConfigYmlPreferences(ThemeName);
        }
        catch { /* best-effort */ }
    }

    /// <summary>config.yml 의 DevezCode 관리 값을 설정하고 나머지 키는 보존.</summary>
    private static void SetConfigYmlPreferences(string themeName)
    {
        string text = File.Exists(ConfigYmlPath) ? File.ReadAllText(ConfigYmlPath) : "";
        text = SetConfigYmlBlock(text, "theme", new Dictionary<string, string>
        {
            ["dark"] = themeName,
            ["light"] = themeName,
        });
        text = SetConfigYmlBlock(text, "starReminder", new Dictionary<string, string>
        {
            ["enabled"] = "false",
        });
        File.WriteAllText(ConfigYmlPath, text, new UTF8Encoding(false));
    }

    /// <summary>YAML 라이브러리 없이 단순 top-level 블록의 관리 키만 치환하고, 없으면 추가.</summary>
    private static string SetConfigYmlBlock(
        string text,
        string blockName,
        IReadOnlyDictionary<string, string> values)
    {
        var lines = text.Length == 0
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var setKeys = new HashSet<string>(StringComparer.Ordinal);
        bool inBlock = false, blockSeen = false;
        string blockPattern = $@"^{System.Text.RegularExpressions.Regex.Escape(blockName)}:\s*$";
        string valuePattern = $@"^(\s+)({string.Join("|", values.Keys)}):";

        void AppendMissingValues()
        {
            foreach (var pair in values)
            {
                if (!setKeys.Contains(pair.Key))
                    sb.Append($"  {pair.Key}: {pair.Value}\n");
            }
        }

        foreach (var raw in lines)
        {
            if (!inBlock && System.Text.RegularExpressions.Regex.IsMatch(raw, blockPattern))
            {
                inBlock = true;
                blockSeen = true;
                setKeys.Clear();
                sb.Append(blockName).Append(":\n");
                continue;
            }
            if (inBlock)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\s+\S"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(raw, valuePattern);
                    if (m.Success)
                    {
                        string key = m.Groups[2].Value;
                        setKeys.Add(key);
                        sb.Append(m.Groups[1].Value).Append(key).Append(": ").Append(values[key]).Append('\n');
                        continue;
                    }
                    sb.Append(raw).Append('\n');
                    continue;
                }
                AppendMissingValues();
                inBlock = false;
            }
            sb.Append(raw).Append('\n');
        }
        if (inBlock)
            AppendMissingValues();
        if (!blockSeen)
        {
            sb.Append(blockName).Append(":\n");
            foreach (var pair in values)
                sb.Append("  ").Append(pair.Key).Append(": ").Append(pair.Value).Append('\n');
        }

        return sb.ToString().TrimEnd('\n') + "\n";
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
        "gray" => new()
        {
            ["background"] = "#F3F4F6", ["currentLine"] = "#E5E7EB", ["selection"] = "#D9DDE3",
            ["backgroundDarker"] = "#E9EBEF", ["foreground"] = "#1F2937", ["comment"] = "#5F6774",
            ["primary"] = "#4B5563", ["secondary"] = "#326AA5", ["accentPurple"] = "#76558F",
            ["errorRed"] = "#C2413E", ["warningOrange"] = "#A16207", ["successGreen"] = "#15803D",
            ["infoCyan"] = "#0E7490", ["emphasizedYellow"] = "#A16207", ["border"] = "#D1D5DB",
            ["diffAdded"] = "#15803D", ["diffRemoved"] = "#C2413E", ["diffContext"] = "#5F6774",
            ["addedBg"] = "#E7F6EB", ["removedBg"] = "#FCE8E8",
        },
        "softpink" => new()
        {
            ["background"] = "#FFF7FA", ["currentLine"] = "#FCEFF4", ["selection"] = "#F2C9D7",
            ["backgroundDarker"] = "#FAE8EF", ["foreground"] = "#3B2931", ["comment"] = "#735763",
            ["primary"] = "#B54A6B", ["secondary"] = "#326A9F", ["accentPurple"] = "#84588F",
            ["errorRed"] = "#C2413E", ["warningOrange"] = "#9A650B", ["successGreen"] = "#25723C",
            ["infoCyan"] = "#16758A", ["emphasizedYellow"] = "#9A650B", ["border"] = "#EBCFD9",
            ["diffAdded"] = "#25723C", ["diffRemoved"] = "#C2413E", ["diffContext"] = "#735763",
            ["addedBg"] = "#E9F5EC", ["removedBg"] = "#FDE7E7",
        },
        "midnight" => new()
        {
            ["background"] = "#111827", ["currentLine"] = "#1F2937", ["selection"] = "#1E3A5F",
            ["backgroundDarker"] = "#0B1220", ["foreground"] = "#E5E7EB", ["comment"] = "#9CA3AF",
            ["primary"] = "#60A5FA", ["secondary"] = "#38BDF8", ["accentPurple"] = "#A78BFA",
            ["errorRed"] = "#F87171", ["warningOrange"] = "#FBBF24", ["successGreen"] = "#34D399",
            ["infoCyan"] = "#22D3EE", ["emphasizedYellow"] = "#FCD34D", ["border"] = "#374151",
            ["diffAdded"] = "#34D399", ["diffRemoved"] = "#F87171", ["diffContext"] = "#9CA3AF",
            ["addedBg"] = "#16362F", ["removedBg"] = "#3B1F2B",
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
