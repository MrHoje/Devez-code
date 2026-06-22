using System;
using System.IO;
using System.Text;

namespace DevezCode.Services.Terminal;

/// <summary>opencode 커스텀 테마 — DevezCode dark/soft/minimal 팔레트를 opencode TUI에 적용.
/// <c>~/.config/opencode/themes/devez-{dark,soft,minimal}.json</c> 으로 1회 설치(devez 앱이 만든 파일 있으면 보존·재사용).
/// Per-project 로 <c>tui.json</c> 에 <c>"theme": "devez-dark"</c> 형태로 주입해서 사용.</summary>
public static class OpenCodeCustomThemes
{
    private const string ThemesDirName = "themes";
    private const string DarkSlug     = "devez-dark";
    private const string SoftSlug     = "devez-soft";
    private const string MinimalSlug  = "devez-minimal";

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

    /// <summary>앱 시작 시 호출. 테마 파일이 없으면 생성. 기존 파일은 사용자 편집 보존 위해 덮어쓰지 않음.</summary>
    public static void EnsureInstalled()
    {
        try
        {
            var dir = ThemesDir;
            Directory.CreateDirectory(dir);

            WriteIfMissing(Path.Combine(dir, DarkSlug + ".json"),    DarkThemeJson);
            WriteIfMissing(Path.Combine(dir, SoftSlug + ".json"),    SoftThemeJson);
            WriteIfMissing(Path.Combine(dir, MinimalSlug + ".json"), MinimalThemeJson);
        }
        catch { /* best-effort — 실패해도 per-project 주입이 안 될 뿐 */ }
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>DevezCode 테마 → opencode theme 슬러그. per-project 주입에서 사용.</summary>
    public static string MapToOpenCodeTheme(string devezCodeTheme) => devezCodeTheme switch
    {
        "dark"    => DarkSlug,
        "soft"    => SoftSlug,
        "minimal" => MinimalSlug,
        _         => DarkSlug,
    };

    private const string DarkThemeJson = """
    {
      "$schema": "https://opencode.ai/theme.json",
      "name": "Devez Dark",
      "defs": {
        "bg":         "#1F1F1E",
        "panel":      "#272727",
        "element":    "#2F2F2F",
        "line":       "#404040",
        "text":       "#E8E8E8",
        "muted":      "#AAAAAA",
        "primary":    "#E8E8E8",
        "primaryLt":  "#FFFFFF",
        "accent":     "#E8E8E8",
        "error":      "#EF4444",
        "warning":    "#F59E0B",
        "success":    "#22C55E",
        "diffAddBg":  "#1A2E1A",
        "diffRemBg":  "#2E1A1A",
        "codeBg":     "#272727",
        "codeBorder": "#404040",
        "hlAdd":      "#22C55E",
        "hlRem":      "#EF4444"
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
        "diffAdded":         { "dark": "hlAdd",    "light": "hlAdd" },
        "diffRemoved":       { "dark": "hlRem",    "light": "hlRem" },
        "diffContext":       { "dark": "muted",    "light": "muted" },
        "diffHunkHeader":    { "dark": "muted",    "light": "muted" },
        "diffHighlightAdded":   { "dark": "hlAdd", "light": "hlAdd" },
        "diffHighlightRemoved": { "dark": "hlRem", "light": "hlRem" },
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
        "hlAdd":      "#5C8C4A",
        "hlRem":      "#D95F5F"
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
        "diffAdded":         { "dark": "hlAdd",    "light": "hlAdd" },
        "diffRemoved":       { "dark": "hlRem",    "light": "hlRem" },
        "diffContext":       { "dark": "muted",    "light": "muted" },
        "diffHunkHeader":    { "dark": "muted",    "light": "muted" },
        "diffHighlightAdded":   { "dark": "hlAdd", "light": "hlAdd" },
        "diffHighlightRemoved": { "dark": "hlRem", "light": "hlRem" },
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
        "diffAddBg":  "#DBEAFE",
        "diffRemBg":  "#FEE2E2",
        "codeBg":     "#FFFFFF",
        "codeBorder": "#E2E8F0",
        "hlAdd":      "#2563EB",
        "hlRem":      "#DC2626"
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
        "diffAdded":         { "dark": "hlAdd",    "light": "hlAdd" },
        "diffRemoved":       { "dark": "hlRem",    "light": "hlRem" },
        "diffContext":       { "dark": "muted",    "light": "muted" },
        "diffHunkHeader":    { "dark": "muted",    "light": "muted" },
        "diffHighlightAdded":   { "dark": "hlAdd", "light": "hlAdd" },
        "diffHighlightRemoved": { "dark": "hlRem", "light": "hlRem" },
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
