using System.IO;
using System.Text.Json;

namespace DevezCode.Services.Terminal;

/// <summary>임베디드 터미널이 사용할 구성 — Windows Terminal settings.json에서 추출.</summary>
public sealed class WtTerminalConfig
{
    public string CommandLine { get; init; } = "powershell.exe";
    public string? StartingDirectory { get; init; }
    public string FontFamily { get; init; } = "Cascadia Mono";
    /// <summary>xterm.js용 px 단위 (WT는 pt 단위 → ×96/72 변환).</summary>
    public double FontSizePx { get; init; } = 16;
    public WtColorScheme Scheme { get; init; } = WtColorScheme.Campbell;
}

/// <summary>WT 컬러 스킴 (xterm ITheme로 매핑됨). 색상은 "#RRGGBB" 문자열.</summary>
public sealed class WtColorScheme
{
    public string Name { get; init; } = "";
    public string Background { get; init; } = "#0C0C0C";
    public string Foreground { get; init; } = "#CCCCCC";
    public string CursorColor { get; init; } = "#FFFFFF";
    public string? SelectionBackground { get; init; }
    public string Black { get; init; } = "#0C0C0C";
    public string Red { get; init; } = "#C50F1F";
    public string Green { get; init; } = "#13A10E";
    public string Yellow { get; init; } = "#C19C00";
    public string Blue { get; init; } = "#0037DA";
    public string Purple { get; init; } = "#881798";
    public string Cyan { get; init; } = "#3A96DD";
    public string White { get; init; } = "#CCCCCC";
    public string BrightBlack { get; init; } = "#767676";
    public string BrightRed { get; init; } = "#E74856";
    public string BrightGreen { get; init; } = "#16C60C";
    public string BrightYellow { get; init; } = "#F9F1A5";
    public string BrightBlue { get; init; } = "#3B78FF";
    public string BrightPurple { get; init; } = "#B4009E";
    public string BrightCyan { get; init; } = "#61D6D6";
    public string BrightWhite { get; init; } = "#F2F2F2";

    public static readonly WtColorScheme Campbell = new() { Name = "Campbell" };

    /// <summary>WT 내장 스킴 — settings.json에는 없으므로 자체 테이블로 해석.</summary>
    public static readonly Dictionary<string, WtColorScheme> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Campbell"] = Campbell,
        ["Campbell Powershell"] = new()
        {
            Name = "Campbell Powershell", Background = "#012456",
        },
        ["One Half Dark"] = new()
        {
            Name = "One Half Dark", Background = "#282C34", Foreground = "#DCDFE4",
            Black = "#282C34", Red = "#E06C75", Green = "#98C379", Yellow = "#E5C07B",
            Blue = "#61AFEF", Purple = "#C678DD", Cyan = "#56B6C2", White = "#DCDFE4",
            BrightBlack = "#5A6374", BrightRed = "#E06C75", BrightGreen = "#98C379", BrightYellow = "#E5C07B",
            BrightBlue = "#61AFEF", BrightPurple = "#C678DD", BrightCyan = "#56B6C2", BrightWhite = "#DCDFE4",
        },
        ["One Half Light"] = new()
        {
            Name = "One Half Light", Background = "#FAFAFA", Foreground = "#383A42", CursorColor = "#4F525D",
            Black = "#383A42", Red = "#E45649", Green = "#50A14F", Yellow = "#C18301",
            Blue = "#0184BC", Purple = "#A626A4", Cyan = "#0997B3", White = "#FAFAFA",
            BrightBlack = "#4F525D", BrightRed = "#DF6C75", BrightGreen = "#98C379", BrightYellow = "#E4C07A",
            BrightBlue = "#61AFEF", BrightPurple = "#C577DD", BrightCyan = "#56B5C1", BrightWhite = "#FFFFFF",
        },
        ["Solarized Dark"] = new()
        {
            Name = "Solarized Dark", Background = "#002B36", Foreground = "#839496",
            Black = "#073642", Red = "#DC322F", Green = "#859900", Yellow = "#B58900",
            Blue = "#268BD2", Purple = "#D33682", Cyan = "#2AA198", White = "#EEE8D5",
            BrightBlack = "#002B36", BrightRed = "#CB4B16", BrightGreen = "#586E75", BrightYellow = "#657B83",
            BrightBlue = "#839496", BrightPurple = "#6C71C4", BrightCyan = "#93A1A1", BrightWhite = "#FDF6E3",
        },
        ["Solarized Light"] = new()
        {
            Name = "Solarized Light", Background = "#FDF6E3", Foreground = "#657B83", CursorColor = "#002B36",
            Black = "#073642", Red = "#DC322F", Green = "#859900", Yellow = "#B58900",
            Blue = "#268BD2", Purple = "#D33682", Cyan = "#2AA198", White = "#EEE8D5",
            BrightBlack = "#002B36", BrightRed = "#CB4B16", BrightGreen = "#586E75", BrightYellow = "#657B83",
            BrightBlue = "#839496", BrightPurple = "#6C71C4", BrightCyan = "#93A1A1", BrightWhite = "#FDF6E3",
        },
        ["Tango Dark"] = new()
        {
            Name = "Tango Dark", Background = "#000000", Foreground = "#D3D7CF",
            Black = "#000000", Red = "#CC0000", Green = "#4E9A06", Yellow = "#C4A000",
            Blue = "#3465A4", Purple = "#75507B", Cyan = "#06989A", White = "#D3D7CF",
            BrightBlack = "#555753", BrightRed = "#EF2929", BrightGreen = "#8AE234", BrightYellow = "#FCE94F",
            BrightBlue = "#729FCF", BrightPurple = "#AD7FA8", BrightCyan = "#34E2E2", BrightWhite = "#EEEEEC",
        },
        ["Tango Light"] = new()
        {
            Name = "Tango Light", Background = "#FFFFFF", Foreground = "#555753", CursorColor = "#000000",
            Black = "#000000", Red = "#CC0000", Green = "#4E9A06", Yellow = "#C4A000",
            Blue = "#3465A4", Purple = "#75507B", Cyan = "#06989A", White = "#D3D7CF",
            BrightBlack = "#555753", BrightRed = "#EF2929", BrightGreen = "#8AE234", BrightYellow = "#FCE94F",
            BrightBlue = "#729FCF", BrightPurple = "#AD7FA8", BrightCyan = "#34E2E2", BrightWhite = "#EEEEEC",
        },
        ["Vintage"] = new()
        {
            Name = "Vintage", Background = "#000000", Foreground = "#C0C0C0",
            Black = "#000000", Red = "#800000", Green = "#008000", Yellow = "#808000",
            Blue = "#000080", Purple = "#800080", Cyan = "#008080", White = "#C0C0C0",
            BrightBlack = "#808080", BrightRed = "#FF0000", BrightGreen = "#00FF00", BrightYellow = "#FFFF00",
            BrightBlue = "#0000FF", BrightPurple = "#FF00FF", BrightCyan = "#00FFFF", BrightWhite = "#FFFFFF",
        },
    };
}

/// <summary>
/// Windows Terminal settings.json 로더.
/// 기본 프로필의 셸 커맨드라인·시작 디렉터리·컬러 스킴·폰트를 읽는다.
/// WT 미설치/파싱 실패 시 Campbell + PowerShell 폴백.
/// </summary>
public static class WtSettingsLoader
{
    private const double PtToPx = 96.0 / 72.0;
    private const double DefaultFontPt = 12;

    /// <summary>알려진 프로필 → 셸 실행 파일 매핑 (commandline이 없는 내장/동적 프로필용).</summary>
    private static readonly Dictionary<string, string> KnownGuidShells = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{61c54bbd-c2c6-5271-96e7-009a87ff44bf}"] = "powershell.exe", // Windows PowerShell
        ["{0caa0dad-35be-5f56-a8ff-afceeeaa6101}"] = "cmd.exe",        // 명령 프롬프트
    };

    public static WtTerminalConfig Load()
    {
        try
        {
            var path = FindSettingsPath();
            if (path == null) return new WtTerminalConfig();
            return Parse(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return new WtTerminalConfig(); // 어떤 실패든 기본값 폴백
        }
    }

    /// <summary>Store판 → Preview → 비패키지판 순으로 탐색.</summary>
    public static string? FindSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(localAppData, @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json"),
            Path.Combine(localAppData, @"Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json"),
            Path.Combine(localAppData, @"Microsoft\Windows Terminal\settings.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>settings.json 본문 파싱 (JSONC 허용). 테스트 가능 진입점.</summary>
    public static WtTerminalConfig Parse(string json)
    {
        var docOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var doc = JsonDocument.Parse(json, docOptions);
        var root = doc.RootElement;

        // ── 프로필 선택: defaultProfile guid → list에서 검색, 없으면 첫 항목
        JsonElement profileList = default;
        JsonElement profileDefaults = default;
        bool hasList = false, hasDefaults = false;
        if (root.TryGetProperty("profiles", out var profiles))
        {
            if (profiles.ValueKind == JsonValueKind.Array) { profileList = profiles; hasList = true; }
            else if (profiles.ValueKind == JsonValueKind.Object)
            {
                hasList = profiles.TryGetProperty("list", out profileList);
                hasDefaults = profiles.TryGetProperty("defaults", out profileDefaults);
            }
        }

        JsonElement profile = default;
        bool hasProfile = false;
        string? defaultGuid = root.TryGetProperty("defaultProfile", out var dp) ? dp.GetString() : null;
        if (hasList && profileList.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in profileList.EnumerateArray())
            {
                if (defaultGuid != null
                    && p.TryGetProperty("guid", out var g)
                    && string.Equals(g.GetString(), defaultGuid, StringComparison.OrdinalIgnoreCase))
                {
                    profile = p; hasProfile = true; break;
                }
            }
            if (!hasProfile)
            {
                foreach (var p in profileList.EnumerateArray()) { profile = p; hasProfile = true; break; }
            }
        }

        // 프로필 값 ?? defaults 값 헬퍼
        string? Pick(string prop)
        {
            if (hasProfile && profile.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            if (hasDefaults && profileDefaults.TryGetProperty(prop, out var d) && d.ValueKind == JsonValueKind.String)
                return d.GetString();
            return null;
        }

        // ── 셸 커맨드라인
        string commandLine = Pick("commandline") ?? ResolveShellFallback(hasProfile ? profile : default, hasProfile);

        // ── 시작 디렉터리
        string? startingDirectory = Pick("startingDirectory");

        // ── 폰트: 신형 font:{face,size} → 구형 fontFace/fontSize → 기본
        (string family, double pt) = ReadFont(hasProfile ? profile : default, hasProfile,
                                              hasDefaults ? profileDefaults : default, hasDefaults);

        // ── 컬러 스킴: 사용자 정의 schemes → 내장 테이블 → Campbell
        string? schemeName = Pick("colorScheme");
        var scheme = ResolveScheme(root, schemeName);

        return new WtTerminalConfig
        {
            CommandLine = commandLine,
            StartingDirectory = startingDirectory,
            FontFamily = family,
            FontSizePx = Math.Round(pt * PtToPx, 1),
            Scheme = scheme,
        };
    }

    private static string ResolveShellFallback(JsonElement profile, bool hasProfile)
    {
        if (hasProfile)
        {
            if (profile.TryGetProperty("guid", out var g)
                && g.GetString() is string guid
                && KnownGuidShells.TryGetValue(guid, out var known))
                return known;

            if (profile.TryGetProperty("source", out var s) && s.GetString() is string source)
            {
                if (source.Contains("PowershellCore", StringComparison.OrdinalIgnoreCase)) return "pwsh.exe";
                if (source.Contains("Wsl", StringComparison.OrdinalIgnoreCase)) return "wsl.exe";
            }
        }
        return "powershell.exe";
    }

    private static (string family, double pt) ReadFont(JsonElement profile, bool hasProfile,
                                                       JsonElement defaults, bool hasDefaults)
    {
        string family = "Cascadia Mono";
        double pt = DefaultFontPt;

        // defaults 먼저 적용 후 프로필이 덮어씀
        foreach (var (el, has) in new[] { (defaults, hasDefaults), (profile, hasProfile) })
        {
            if (!has) continue;
            if (el.TryGetProperty("font", out var font) && font.ValueKind == JsonValueKind.Object)
            {
                if (font.TryGetProperty("face", out var face) && face.ValueKind == JsonValueKind.String)
                    family = face.GetString() ?? family;
                if (font.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number)
                    pt = size.GetDouble();
            }
            if (el.TryGetProperty("fontFace", out var legacyFace) && legacyFace.ValueKind == JsonValueKind.String)
                family = legacyFace.GetString() ?? family;
            if (el.TryGetProperty("fontSize", out var legacySize) && legacySize.ValueKind == JsonValueKind.Number)
                pt = legacySize.GetDouble();
        }
        return (family, pt);
    }

    private static WtColorScheme ResolveScheme(JsonElement root, string? schemeName)
    {
        if (string.IsNullOrEmpty(schemeName)) return WtColorScheme.Campbell;

        // 사용자 정의 스킴 우선
        if (root.TryGetProperty("schemes", out var schemes) && schemes.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in schemes.EnumerateArray())
            {
                if (!s.TryGetProperty("name", out var n)
                    || !string.Equals(n.GetString(), schemeName, StringComparison.OrdinalIgnoreCase)) continue;

                string Get(string prop, string fallback) =>
                    s.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? fallback : fallback;

                var c = WtColorScheme.Campbell;
                return new WtColorScheme
                {
                    Name = schemeName,
                    Background = Get("background", c.Background),
                    Foreground = Get("foreground", c.Foreground),
                    CursorColor = Get("cursorColor", c.CursorColor),
                    SelectionBackground = s.TryGetProperty("selectionBackground", out var sel)
                        && sel.ValueKind == JsonValueKind.String ? sel.GetString() : null,
                    Black = Get("black", c.Black), Red = Get("red", c.Red),
                    Green = Get("green", c.Green), Yellow = Get("yellow", c.Yellow),
                    Blue = Get("blue", c.Blue), Purple = Get("purple", c.Purple),
                    Cyan = Get("cyan", c.Cyan), White = Get("white", c.White),
                    BrightBlack = Get("brightBlack", c.BrightBlack), BrightRed = Get("brightRed", c.BrightRed),
                    BrightGreen = Get("brightGreen", c.BrightGreen), BrightYellow = Get("brightYellow", c.BrightYellow),
                    BrightBlue = Get("brightBlue", c.BrightBlue), BrightPurple = Get("brightPurple", c.BrightPurple),
                    BrightCyan = Get("brightCyan", c.BrightCyan), BrightWhite = Get("brightWhite", c.BrightWhite),
                };
            }
        }

        // 내장 스킴 테이블
        if (WtColorScheme.BuiltIns.TryGetValue(schemeName, out var builtin)) return builtin;
        return WtColorScheme.Campbell;
    }
}
