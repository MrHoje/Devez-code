using System;
using System.IO;
using System.Text;

namespace DevezCode.Services.Terminal;

/// <summary>opencode 커스텀 테마 — DevezCode 6개 팔레트를 opencode TUI에 적용.
/// <c>~/.config/opencode/themes/devez-*.json</c> 으로 매 시작 시 번들 내용으로
/// 항상 재생성(statusline.js/ClaudeCustomThemes 와 동일한 관리 방식) — 팔레트를 코드에서 바꾸면
/// 다음 실행에 바로 반영. 이 파일들은 우리 전용 슬러그라 사용자가 직접 편집할 대상이 아니다.
/// TUI 설정은 AppData에 두고 OPENCODE_TUI_CONFIG로 DevezCode 세션에만 주입한다.</summary>
public static class OpenCodeCustomThemes
{
    private const string ThemesDirName = "themes";
    private const string DarkSlug     = "devez-dark";
    private const string SoftSlug     = "devez-soft";
    private const string MinimalSlug  = "devez-minimal";
    private const string GraySlug     = "devez-gray";
    private const string SoftPinkSlug = "devez-softpink";
    private const string MidnightSlug = "devez-midnight";
    private const string NordSlug     = "devez-nord";
    private const string MonokaiSlug  = "devez-monokai";
    private const string MochaSlug    = "devez-mocha";

    private static string ThemesDir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var configHome = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            return Path.Combine(configHome, "opencode", ThemesDirName);
        }
    }

    public static string TuiConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "opencode", "tui.json");

    /// <summary>앱 시작 시 호출. 매번 번들 내용으로 덮어써 최신 팔레트를 강제 반영한다.</summary>
    public static void EnsureInstalled()
    {
        try
        {
            var dir = ThemesDir;
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, DarkSlug + ".json"),    DarkThemeJson,    new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, SoftSlug + ".json"),    SoftThemeJson,    new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, MinimalSlug + ".json"), MinimalThemeJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, GraySlug + ".json"), GrayThemeJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, SoftPinkSlug + ".json"), SoftPinkThemeJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, MidnightSlug + ".json"), MidnightThemeJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, NordSlug + ".json"), NordThemeJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, MonokaiSlug + ".json"), MonokaiThemeJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, MochaSlug + ".json"), MochaThemeJson, new UTF8Encoding(false));
        }
        catch { /* best-effort — 실패해도 per-project 주입이 안 될 뿐 */ }
    }

    /// <summary>DevezCode 테마 → opencode theme 슬러그. per-project 주입에서 사용.</summary>
    public static string MapToOpenCodeTheme(string devezCodeTheme) => devezCodeTheme switch
    {
        "dark"    => DarkSlug,
        "soft"    => SoftSlug,
        "minimal" => MinimalSlug,
        "gray"    => GraySlug,
        "softpink" => SoftPinkSlug,
        "midnight" => MidnightSlug,
        "nord"    => NordSlug,
        "monokai" => MonokaiSlug,
        "mocha"   => MochaSlug,
        _         => DarkSlug,
    };

    public static void Apply(string devezCodeTheme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TuiConfigPath)!);
            var root = new System.Text.Json.Nodes.JsonObject
            {
                ["theme"] = MapToOpenCodeTheme(devezCodeTheme),
            };
            File.WriteAllText(TuiConfigPath,
                root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* best-effort */ }
    }

    /// <summary>이전 버전이 프로젝트에 주입한 DevezCode 테마만 제거한다.</summary>
    public static void RemoveLegacyProjectTheme(string workingDir)
    {
        try
        {
            var path = Path.Combine(workingDir, "tui.json");
            if (!File.Exists(path)) return;

            var root = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as System.Text.Json.Nodes.JsonObject;
            var theme = root?["theme"]?.GetValue<string>();
            if (theme is not (DarkSlug or SoftSlug or MinimalSlug or GraySlug or SoftPinkSlug or MidnightSlug
                or NordSlug or MonokaiSlug or MochaSlug)) return;

            root!.Remove("theme");
            if (root.Count == 0)
                File.Delete(path);
            else
                File.WriteAllText(path,
                    root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
        }
        catch { /* 사용자 파일이면 그대로 둔다 */ }
    }

    private const string DarkThemeJson = """
    {
      "$schema": "https://opencode.ai/theme.json",
      "name": "Devez Dark",
      "defs": {
        "bg":         "#1F1F1E",
        "panel":      "#272727",
        "element":    "#2F2F2F",
        "line":       "#404040",
        "codeBg":     "#272727",
        "codeBorder": "#404040",
        "diffAddText":    "#A7C8A0",
        "diffRemText":    "#D8A0A2",
        "diffAddBg":      "#303A30",
        "diffRemBg":      "#3A3030",
        "diffLine":       "#AAAAAA",
        "diffAddLineBg":  "#293429",
        "diffRemLineBg":  "#342929",
        "darkStep1":  "#1F1F1E",
        "darkStep2":  "#272727",
        "darkStep3":  "#2F2F2F",
        "darkStep4":  "#282828",
        "darkStep5":  "#323232",
        "darkStep6":  "#404040",
        "darkStep7":  "#404040",
        "darkStep8":  "#606060",
        "darkStep9":  "#fab283",
        "darkStep10": "#ffc09f",
        "darkStep11": "#808080",
        "darkStep12": "#eeeeee",
        "darkSecondary": "#5c9cf5",
        "darkAccent":    "#9d7cd8",
        "darkRed":    "#e06c75",
        "darkOrange": "#f5a742",
        "darkGreen":  "#7fd88f",
        "darkCyan":   "#56b6c2",
        "darkYellow": "#e5c07b",
        "lightStep1":   "#ffffff",
        "lightStep2":   "#fafafa",
        "lightStep3":   "#f5f5f5",
        "lightStep4":   "#ebebeb",
        "lightStep5":   "#e1e1e1",
        "lightStep6":   "#d4d4d4",
        "lightStep7":   "#b8b8b8",
        "lightStep8":   "#a0a0a0",
        "lightStep9":   "#3b7dd8",
        "lightStep10":  "#2968c3",
        "lightStep11":  "#8a8a8a",
        "lightStep12":  "#1a1a1a",
        "lightSecondary": "#7b5bb6",
        "lightAccent":    "#d68c27",
        "lightRed":    "#d1383d",
        "lightOrange": "#d68c27",
        "lightGreen":  "#3d9a57",
        "lightCyan":   "#318795",
        "lightYellow": "#b0851f"
      },
      "theme": {
        "primary":           { "dark": "darkStep9",  "light": "lightStep9" },
        "secondary":         { "dark": "darkSecondary", "light": "lightSecondary" },
        "accent":            { "dark": "darkAccent", "light": "lightAccent" },
        "error":             { "dark": "darkRed",    "light": "lightRed" },
        "warning":           { "dark": "darkOrange", "light": "lightOrange" },
        "success":           { "dark": "darkGreen",  "light": "lightGreen" },
        "info":              { "dark": "darkCyan",   "light": "lightCyan" },
        "text":              { "dark": "darkStep12", "light": "lightStep12" },
        "textMuted":         { "dark": "darkStep11", "light": "lightStep11" },
        "background":        { "dark": "bg",         "light": "lightStep1" },
        "backgroundPanel":   { "dark": "panel",      "light": "lightStep2" },
        "backgroundElement": { "dark": "element",    "light": "lightStep3" },
        "border":            { "dark": "line",       "light": "lightStep7" },
        "borderActive":      { "dark": "darkStep8",  "light": "lightStep8" },
        "borderSubtle":      { "dark": "line",       "light": "lightStep6" },
        "diffAdded":         { "dark": "diffAddText", "light": "diffAddText" },
        "diffRemoved":       { "dark": "diffRemText", "light": "diffRemText" },
        "diffContext":       { "dark": "diffLine", "light": "diffLine" },
        "diffHunkHeader":    { "dark": "diffLine", "light": "diffLine" },
        "diffHighlightAdded":   { "dark": "diffAddText", "light": "diffAddText" },
        "diffHighlightRemoved": { "dark": "diffRemText", "light": "diffRemText" },
        "diffAddedBg":           { "dark": "diffAddBg", "light": "diffAddBg" },
        "diffRemovedBg":         { "dark": "diffRemBg", "light": "diffRemBg" },
        "diffContextBg":         { "dark": "panel",   "light": "lightStep2" },
        "diffLineNumber":        { "dark": "diffLine", "light": "diffLine" },
        "diffAddedLineNumberBg":   { "dark": "diffAddLineBg", "light": "diffAddLineBg" },
        "diffRemovedLineNumberBg": { "dark": "diffRemLineBg", "light": "diffRemLineBg" },
        "markdownText":         { "dark": "darkStep12", "light": "lightStep12" },
        "markdownHeading":      { "dark": "darkAccent", "light": "lightAccent" },
        "markdownLink":         { "dark": "darkStep9",  "light": "lightStep9" },
        "markdownLinkText":     { "dark": "darkCyan",   "light": "lightCyan" },
        "markdownCode":         { "dark": "darkGreen",  "light": "lightGreen" },
        "markdownBlockQuote":   { "dark": "darkYellow", "light": "lightYellow" },
        "markdownEmph":         { "dark": "darkYellow", "light": "lightYellow" },
        "markdownStrong":       { "dark": "darkOrange", "light": "lightOrange" },
        "markdownHorizontalRule": { "dark": "darkStep11", "light": "lightStep11" },
        "markdownListItem":     { "dark": "darkStep9",  "light": "lightStep9" },
        "markdownListEnumeration": { "dark": "darkCyan", "light": "lightCyan" },
        "markdownImage":        { "dark": "darkStep9",  "light": "lightStep9" },
        "markdownImageText":    { "dark": "darkCyan",   "light": "lightCyan" },
        "markdownCodeBlock":    { "dark": "darkStep12", "light": "lightStep12" },
        "syntaxComment":        { "dark": "darkStep11", "light": "lightStep11" },
        "syntaxKeyword":        { "dark": "darkAccent", "light": "lightAccent" },
        "syntaxFunction":       { "dark": "darkStep9",  "light": "lightStep9" },
        "syntaxVariable":       { "dark": "darkRed",    "light": "lightRed" },
        "syntaxString":         { "dark": "darkGreen",  "light": "lightGreen" },
        "syntaxNumber":         { "dark": "darkOrange", "light": "lightOrange" },
        "syntaxType":           { "dark": "darkYellow", "light": "lightYellow" },
        "syntaxOperator":       { "dark": "darkCyan",   "light": "lightCyan" },
        "syntaxPunctuation":    { "dark": "darkStep12", "light": "lightStep12" }
      }
    }
    """;

    private const string SoftThemeJson = """
    {
      "$schema": "https://opencode.ai/theme.json",
      "name": "Devez Soft",
      "defs": {
        "bg":         "#F2EDE6",
        "panel":      "#FAF7F2",
        "element":    "#ECE7DE",
        "line":       "#D8D2C6",
        "text":       "#2A2620",
        "muted":      "#5A5448",
        "primary":    "#5C8C4A",
        "primaryLt":  "#7BAA68",
        "accent":     "#5C8C4A",
        "error":      "#D95F5F",
        "warning":    "#C97C1A",
        "success":    "#4E7A3E",
        "diffAddBg":  "#DEECD6",
        "diffRemBg":  "#F2D6D6",
        "codeBg":     "#FAF7F2",
        "codeBorder": "#D8D2C6",
        "diffAddText": "#365F2D",
        "diffRemText": "#8C3F46"
      },
      "theme": {
        "primary":           { "dark": "primary",  "light": "primary" },
        "secondary":         { "dark": "primaryLt","light": "primaryLt" },
        "accent":            { "dark": "accent",   "light": "accent" },
        "error":             { "dark": "error",    "light": "error" },
        "warning":           { "dark": "warning",  "light": "warning" },
        "success":           { "dark": "success",  "light": "success" },
        "info":              { "dark": "primary",  "light": "primary" },
        "text":              { "dark": "text",     "light": "text" },
        "textMuted":         { "dark": "muted",    "light": "muted" },
        "background":        { "dark": "bg",       "light": "bg" },
        "backgroundPanel":   { "dark": "panel",    "light": "panel" },
        "backgroundElement": { "dark": "element",  "light": "element" },
        "border":            { "dark": "line",     "light": "line" },
        "borderActive":      { "dark": "primary",  "light": "primary" },
        "borderSubtle":      { "dark": "line",     "light": "line" },
        "diffAdded":         { "dark": "diffAddText", "light": "diffAddText" },
        "diffRemoved":       { "dark": "diffRemText", "light": "diffRemText" },
        "diffContext":       { "dark": "muted",    "light": "muted" },
        "diffHunkHeader":    { "dark": "muted",    "light": "muted" },
        "diffHighlightAdded":   { "dark": "diffAddText", "light": "diffAddText" },
        "diffHighlightRemoved": { "dark": "diffRemText", "light": "diffRemText" },
        "diffAddedBg":          { "dark": "diffAddBg", "light": "diffAddBg" },
        "diffRemovedBg":        { "dark": "diffRemBg", "light": "diffRemBg" },
        "diffContextBg":        { "dark": "panel", "light": "panel" },
        "diffLineNumber":       { "dark": "muted", "light": "muted" },
        "diffAddedLineNumberBg":   { "dark": "diffAddBg", "light": "diffAddBg" },
        "diffRemovedLineNumberBg": { "dark": "diffRemBg", "light": "diffRemBg" },
        "markdownText":         { "dark": "text",  "light": "text" },
        "markdownHeading":      { "dark": "primary", "light": "primary" },
        "markdownLink":         { "dark": "primary","light": "primary" },
        "markdownLinkText":     { "dark": "primaryLt","light": "primaryLt" },
        "markdownCode":         { "dark": "primary","light": "primary" },
        "markdownBlockQuote":   { "dark": "muted", "light": "muted" },
        "markdownEmph":         { "dark": "warning","light": "warning" },
        "markdownStrong":       { "dark": "text",  "light": "text" },
        "markdownHorizontalRule": { "dark": "line","light": "line" },
        "markdownListItem":     { "dark": "primary","light": "primary" },
        "markdownListEnumeration": { "dark": "primaryLt","light": "primaryLt" },
        "markdownImage":        { "dark": "primary","light": "primary" },
        "markdownImageText":    { "dark": "primaryLt","light": "primaryLt" },
        "markdownCodeBlock":    { "dark": "text",  "light": "text" },
        "syntaxComment":        { "dark": "muted", "light": "muted" },
        "syntaxKeyword":        { "dark": "primary","light": "primary" },
        "syntaxFunction":       { "dark": "primary","light": "primary" },
        "syntaxVariable":       { "dark": "accent", "light": "accent" },
        "syntaxString":         { "dark": "success","light": "success" },
        "syntaxNumber":         { "dark": "warning","light": "warning" },
        "syntaxType":           { "dark": "accent", "light": "accent" },
        "syntaxOperator":       { "dark": "primary","light": "primary" },
        "syntaxPunctuation":    { "dark": "text",  "light": "text" }
      }
    }
    """;

    // diff 전경은 primary/error 와 별도 토큰으로 치환해 테마 파생 시 초록/빨강 의미가 변하지 않게 한다.
    private static readonly string GrayThemeJson = SoftThemeJson
        .Replace("Devez Soft", "Devez Gray", StringComparison.Ordinal)
        .Replace("#F2EDE6", "#F3F4F6", StringComparison.Ordinal)
        .Replace("#FAF7F2", "#FFFFFF", StringComparison.Ordinal)
        .Replace("#ECE7DE", "#E5E7EB", StringComparison.Ordinal)
        .Replace("#D8D2C6", "#D1D5DB", StringComparison.Ordinal)
        .Replace("#2A2620", "#1F2937", StringComparison.Ordinal)
        .Replace("#5A5448", "#5F6774", StringComparison.Ordinal)
        .Replace("#5C8C4A", "#4B5563", StringComparison.Ordinal)
        .Replace("#7BAA68", "#326AA5", StringComparison.Ordinal)
        .Replace("#D95F5F", "#C2413E", StringComparison.Ordinal)
        .Replace("#C97C1A", "#A16207", StringComparison.Ordinal)
        .Replace("#4E7A3E", "#15803D", StringComparison.Ordinal)
        .Replace("#DEECD6", "#E7F6EB", StringComparison.Ordinal)
        .Replace("#F2D6D6", "#FCE8E8", StringComparison.Ordinal)
        .Replace("#365F2D", "#355E3B", StringComparison.Ordinal)
        .Replace("#8C3F46", "#8B3A3A", StringComparison.Ordinal);

    private static readonly string SoftPinkThemeJson = SoftThemeJson
        .Replace("Devez Soft", "Devez Soft Pink", StringComparison.Ordinal)
        .Replace("#F2EDE6", "#FFF7FA", StringComparison.Ordinal)
        .Replace("#FAF7F2", "#FFFCFD", StringComparison.Ordinal)
        .Replace("#ECE7DE", "#FCEFF4", StringComparison.Ordinal)
        .Replace("#D8D2C6", "#EBCFD9", StringComparison.Ordinal)
        .Replace("#2A2620", "#3B2931", StringComparison.Ordinal)
        .Replace("#5A5448", "#735763", StringComparison.Ordinal)
        .Replace("#5C8C4A", "#B54A6B", StringComparison.Ordinal)
        .Replace("#7BAA68", "#CE7892", StringComparison.Ordinal)
        .Replace("#D95F5F", "#C2413E", StringComparison.Ordinal)
        .Replace("#C97C1A", "#9A650B", StringComparison.Ordinal)
        .Replace("#4E7A3E", "#25723C", StringComparison.Ordinal)
        .Replace("#DEECD6", "#E9F5EC", StringComparison.Ordinal)
        .Replace("#F2D6D6", "#FDE7E7", StringComparison.Ordinal)
        .Replace("#365F2D", "#315E42", StringComparison.Ordinal)
        .Replace("#8C3F46", "#913A46", StringComparison.Ordinal);

    private static readonly string MidnightThemeJson = DarkThemeJson
        .Replace("Devez Dark", "Devez Midnight Blue", StringComparison.Ordinal)
        .Replace("#1F1F1E", "#111827", StringComparison.OrdinalIgnoreCase)
        .Replace("#272727", "#1F2937", StringComparison.OrdinalIgnoreCase)
        .Replace("#2F2F2F", "#293548", StringComparison.OrdinalIgnoreCase)
        .Replace("#404040", "#374151", StringComparison.OrdinalIgnoreCase)
        .Replace("#282828", "#172033", StringComparison.OrdinalIgnoreCase)
        .Replace("#323232", "#263449", StringComparison.OrdinalIgnoreCase)
        .Replace("#606060", "#4B6380", StringComparison.OrdinalIgnoreCase)
        .Replace("#fab283", "#60A5FA", StringComparison.OrdinalIgnoreCase)
        .Replace("#ffc09f", "#93C5FD", StringComparison.OrdinalIgnoreCase)
        .Replace("#808080", "#9CA3AF", StringComparison.OrdinalIgnoreCase)
        .Replace("#AAAAAA", "#9CA3AF", StringComparison.OrdinalIgnoreCase)
        .Replace("#eeeeee", "#E5E7EB", StringComparison.OrdinalIgnoreCase)
        .Replace("#5c9cf5", "#38BDF8", StringComparison.OrdinalIgnoreCase)
        .Replace("#9d7cd8", "#A78BFA", StringComparison.OrdinalIgnoreCase)
        .Replace("#e06c75", "#F87171", StringComparison.OrdinalIgnoreCase)
        .Replace("#f5a742", "#FBBF24", StringComparison.OrdinalIgnoreCase)
        .Replace("#7fd88f", "#34D399", StringComparison.OrdinalIgnoreCase)
        .Replace("#56b6c2", "#22D3EE", StringComparison.OrdinalIgnoreCase)
        .Replace("#e5c07b", "#FCD34D", StringComparison.OrdinalIgnoreCase)
        .Replace("#A7C8A0", "#9AC7B2", StringComparison.OrdinalIgnoreCase)
        .Replace("#D8A0A2", "#DEA0AB", StringComparison.OrdinalIgnoreCase)
        .Replace("#303A30", "#16362F", StringComparison.OrdinalIgnoreCase)
        .Replace("#3A3030", "#3B1F2B", StringComparison.OrdinalIgnoreCase)
        .Replace("#293429", "#102E29", StringComparison.OrdinalIgnoreCase)
        .Replace("#342929", "#321B25", StringComparison.OrdinalIgnoreCase);

    private static readonly string NordThemeJson = DarkThemeJson
        .Replace("Devez Dark", "Devez Nord", StringComparison.Ordinal)
        .Replace("#1F1F1E", "#2E3440", StringComparison.OrdinalIgnoreCase)
        .Replace("#272727", "#3B4252", StringComparison.OrdinalIgnoreCase)
        .Replace("#2F2F2F", "#434C5E", StringComparison.OrdinalIgnoreCase)
        .Replace("#404040", "#4C566A", StringComparison.OrdinalIgnoreCase)
        .Replace("#282828", "#333B49", StringComparison.OrdinalIgnoreCase)
        .Replace("#323232", "#3F4859", StringComparison.OrdinalIgnoreCase)
        .Replace("#606060", "#5D708A", StringComparison.OrdinalIgnoreCase)
        .Replace("#fab283", "#88C0D0", StringComparison.OrdinalIgnoreCase)
        .Replace("#ffc09f", "#9FCDD9", StringComparison.OrdinalIgnoreCase)
        .Replace("#808080", "#B6C0CF", StringComparison.OrdinalIgnoreCase)
        .Replace("#AAAAAA", "#ADB8C9", StringComparison.OrdinalIgnoreCase)
        .Replace("#eeeeee", "#ECEFF4", StringComparison.OrdinalIgnoreCase)
        .Replace("#5c9cf5", "#81A1C1", StringComparison.OrdinalIgnoreCase)
        .Replace("#9d7cd8", "#B48EAD", StringComparison.OrdinalIgnoreCase)
        .Replace("#e06c75", "#BF616A", StringComparison.OrdinalIgnoreCase)
        .Replace("#f5a742", "#D08770", StringComparison.OrdinalIgnoreCase)
        .Replace("#7fd88f", "#A3BE8C", StringComparison.OrdinalIgnoreCase)
        .Replace("#56b6c2", "#8FBCBB", StringComparison.OrdinalIgnoreCase)
        .Replace("#e5c07b", "#EBCB8B", StringComparison.OrdinalIgnoreCase)
        .Replace("#A7C8A0", "#A3BE8C", StringComparison.OrdinalIgnoreCase)
        .Replace("#D8A0A2", "#D79AA1", StringComparison.OrdinalIgnoreCase)
        .Replace("#303A30", "#2E4136", StringComparison.OrdinalIgnoreCase)
        .Replace("#3A3030", "#4A3440", StringComparison.OrdinalIgnoreCase)
        .Replace("#293429", "#2A3B31", StringComparison.OrdinalIgnoreCase)
        .Replace("#342929", "#43303A", StringComparison.OrdinalIgnoreCase);

    private static readonly string MonokaiThemeJson = DarkThemeJson
        .Replace("Devez Dark", "Devez Monokai Machine", StringComparison.Ordinal)
        .Replace("#1F1F1E", "#273136", StringComparison.OrdinalIgnoreCase)
        .Replace("#272727", "#2E3A40", StringComparison.OrdinalIgnoreCase)
        .Replace("#2F2F2F", "#3A4449", StringComparison.OrdinalIgnoreCase)
        .Replace("#404040", "#4A5559", StringComparison.OrdinalIgnoreCase)
        .Replace("#282828", "#303C42", StringComparison.OrdinalIgnoreCase)
        .Replace("#323232", "#354045", StringComparison.OrdinalIgnoreCase)
        .Replace("#606060", "#66757A", StringComparison.OrdinalIgnoreCase)
        .Replace("#fab283", "#FFD866", StringComparison.OrdinalIgnoreCase)
        .Replace("#ffc09f", "#FFE28A", StringComparison.OrdinalIgnoreCase)
        .Replace("#808080", "#AAB6B6", StringComparison.OrdinalIgnoreCase)
        .Replace("#AAAAAA", "#A3AFAF", StringComparison.OrdinalIgnoreCase)
        .Replace("#eeeeee", "#F2FFFC", StringComparison.OrdinalIgnoreCase)
        .Replace("#5c9cf5", "#7CD5F1", StringComparison.OrdinalIgnoreCase)
        .Replace("#9d7cd8", "#BAA0F8", StringComparison.OrdinalIgnoreCase)
        .Replace("#e06c75", "#FF6D7E", StringComparison.OrdinalIgnoreCase)
        .Replace("#f5a742", "#FC9867", StringComparison.OrdinalIgnoreCase)
        .Replace("#7fd88f", "#A2E57B", StringComparison.OrdinalIgnoreCase)
        .Replace("#56b6c2", "#7ED9CD", StringComparison.OrdinalIgnoreCase)
        .Replace("#e5c07b", "#FFD866", StringComparison.OrdinalIgnoreCase)
        .Replace("#A7C8A0", "#B4EB97", StringComparison.OrdinalIgnoreCase)
        .Replace("#D8A0A2", "#FB99A4", StringComparison.OrdinalIgnoreCase)
        .Replace("#303A30", "#2B4236", StringComparison.OrdinalIgnoreCase)
        .Replace("#3A3030", "#483336", StringComparison.OrdinalIgnoreCase)
        .Replace("#293429", "#27392F", StringComparison.OrdinalIgnoreCase)
        .Replace("#342929", "#422F33", StringComparison.OrdinalIgnoreCase);

    // 강조색(그린)이 success 와 같은 값이라, 헤딩·키워드에는 teal 을 써서 두 역할을 색으로 가른다.
    private static readonly string MochaThemeJson = DarkThemeJson
        .Replace("Devez Dark", "Devez Mocha Deep", StringComparison.Ordinal)
        .Replace("#1F1F1E", "#11111B", StringComparison.OrdinalIgnoreCase)
        .Replace("#272727", "#1E1E2E", StringComparison.OrdinalIgnoreCase)
        .Replace("#2F2F2F", "#262637", StringComparison.OrdinalIgnoreCase)
        .Replace("#404040", "#3D3F52", StringComparison.OrdinalIgnoreCase)
        .Replace("#282828", "#17211B", StringComparison.OrdinalIgnoreCase)
        .Replace("#323232", "#24243A", StringComparison.OrdinalIgnoreCase)
        .Replace("#606060", "#4A4D66", StringComparison.OrdinalIgnoreCase)
        .Replace("#fab283", "#A6E3A1", StringComparison.OrdinalIgnoreCase)
        .Replace("#ffc09f", "#B8EAB4", StringComparison.OrdinalIgnoreCase)
        .Replace("#808080", "#9399B2", StringComparison.OrdinalIgnoreCase)
        .Replace("#AAAAAA", "#9399B2", StringComparison.OrdinalIgnoreCase)
        .Replace("#eeeeee", "#CDD6F4", StringComparison.OrdinalIgnoreCase)
        .Replace("#5c9cf5", "#89B4FA", StringComparison.OrdinalIgnoreCase)
        .Replace("#9d7cd8", "#94E2D5", StringComparison.OrdinalIgnoreCase)
        .Replace("#e06c75", "#F38BA8", StringComparison.OrdinalIgnoreCase)
        .Replace("#f5a742", "#FAB387", StringComparison.OrdinalIgnoreCase)
        .Replace("#7fd88f", "#A6E3A1", StringComparison.OrdinalIgnoreCase)
        .Replace("#56b6c2", "#94E2D5", StringComparison.OrdinalIgnoreCase)
        .Replace("#e5c07b", "#F9E2AF", StringComparison.OrdinalIgnoreCase)
        .Replace("#A7C8A0", "#A6E3A1", StringComparison.OrdinalIgnoreCase)
        .Replace("#D8A0A2", "#F18EAB", StringComparison.OrdinalIgnoreCase)
        .Replace("#303A30", "#14211A", StringComparison.OrdinalIgnoreCase)
        .Replace("#3A3030", "#251722", StringComparison.OrdinalIgnoreCase)
        .Replace("#293429", "#121C16", StringComparison.OrdinalIgnoreCase)
        .Replace("#342929", "#20141E", StringComparison.OrdinalIgnoreCase);

    private const string MinimalThemeJson = """
    {
      "$schema": "https://opencode.ai/theme.json",
      "name": "Devez Minimal",
      "defs": {
        "bg":         "#F8FAFC",
        "panel":      "#FFFFFF",
        "element":    "#F1F5F9",
        "line":       "#E2E8F0",
        "text":       "#0F172A",
        "muted":      "#475569",
        "primary":    "#2563EB",
        "primaryLt":  "#60A5FA",
        "accent":     "#2563EB",
        "error":      "#EF4444",
        "warning":    "#CA8A04",
        "success":    "#15803D",
        "diffAddBg":  "#E7F6EB",
        "diffRemBg":  "#FCE8E8",
        "codeBg":     "#FFFFFF",
        "codeBorder": "#E2E8F0",
        "diffAddText": "#355E3B",
        "diffRemText": "#8B3A3A"
      },
      "theme": {
        "primary":           { "dark": "primary",  "light": "primary" },
        "secondary":         { "dark": "primaryLt","light": "primaryLt" },
        "accent":            { "dark": "accent",   "light": "accent" },
        "error":             { "dark": "error",    "light": "error" },
        "warning":           { "dark": "warning",  "light": "warning" },
        "success":           { "dark": "success",  "light": "success" },
        "info":              { "dark": "primary",  "light": "primary" },
        "text":              { "dark": "text",     "light": "text" },
        "textMuted":         { "dark": "muted",    "light": "muted" },
        "background":        { "dark": "bg",       "light": "bg" },
        "backgroundPanel":   { "dark": "panel",    "light": "panel" },
        "backgroundElement": { "dark": "element",  "light": "element" },
        "border":            { "dark": "line",     "light": "line" },
        "borderActive":      { "dark": "primary",  "light": "primary" },
        "borderSubtle":      { "dark": "line",     "light": "line" },
        "diffAdded":         { "dark": "diffAddText", "light": "diffAddText" },
        "diffRemoved":       { "dark": "diffRemText", "light": "diffRemText" },
        "diffContext":       { "dark": "muted",    "light": "muted" },
        "diffHunkHeader":    { "dark": "muted",    "light": "muted" },
        "diffHighlightAdded":   { "dark": "diffAddText", "light": "diffAddText" },
        "diffHighlightRemoved": { "dark": "diffRemText", "light": "diffRemText" },
        "diffAddedBg":          { "dark": "diffAddBg", "light": "diffAddBg" },
        "diffRemovedBg":        { "dark": "diffRemBg", "light": "diffRemBg" },
        "diffContextBg":        { "dark": "panel", "light": "panel" },
        "diffLineNumber":       { "dark": "muted", "light": "muted" },
        "diffAddedLineNumberBg":   { "dark": "diffAddBg", "light": "diffAddBg" },
        "diffRemovedLineNumberBg": { "dark": "diffRemBg", "light": "diffRemBg" },
        "markdownText":         { "dark": "text",  "light": "text" },
        "markdownHeading":      { "dark": "primary", "light": "primary" },
        "markdownLink":         { "dark": "primary","light": "primary" },
        "markdownLinkText":     { "dark": "primaryLt","light": "primaryLt" },
        "markdownCode":         { "dark": "primary","light": "primary" },
        "markdownBlockQuote":   { "dark": "muted", "light": "muted" },
        "markdownEmph":         { "dark": "warning","light": "warning" },
        "markdownStrong":       { "dark": "text",  "light": "text" },
        "markdownHorizontalRule": { "dark": "line","light": "line" },
        "markdownListItem":     { "dark": "primary","light": "primary" },
        "markdownListEnumeration": { "dark": "primaryLt","light": "primaryLt" },
        "markdownImage":        { "dark": "primary","light": "primary" },
        "markdownImageText":    { "dark": "primaryLt","light": "primaryLt" },
        "markdownCodeBlock":    { "dark": "text",  "light": "text" },
        "syntaxComment":        { "dark": "muted", "light": "muted" },
        "syntaxKeyword":        { "dark": "primary","light": "primary" },
        "syntaxFunction":       { "dark": "primary","light": "primary" },
        "syntaxVariable":       { "dark": "accent", "light": "accent" },
        "syntaxString":         { "dark": "success","light": "success" },
        "syntaxNumber":         { "dark": "warning","light": "warning" },
        "syntaxType":           { "dark": "accent", "light": "accent" },
        "syntaxOperator":       { "dark": "primary","light": "primary" },
        "syntaxPunctuation":    { "dark": "text",  "light": "text" }
      }
    }
    """;
}
