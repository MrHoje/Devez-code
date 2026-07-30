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

        // ── DevezCode 전용 스킴 (앱 UI 3개 테마와 1:1 매핑) ──────────────
        ["DevezCode Dark"] = new()
        {
            Name = "DevezCode Dark", Background = "#1F1F1E", Foreground = "#E8E8E8",
            CursorColor = "#FFFFFF", SelectionBackground = "#3F3F3F",
            Black = "#1F1F1E", Red = "#EF4444", Green = "#22C55E", Yellow = "#FBBF24",
            Blue = "#60A5FA", Purple = "#C084FC", Cyan = "#14B8A6", White = "#E8E8E8",
            BrightBlack = "#767676", BrightRed = "#F87171", BrightGreen = "#4ADE80", BrightYellow = "#FDE047",
            BrightBlue = "#93C5FD", BrightPurple = "#D8B4FE", BrightCyan = "#5EEAD4", BrightWhite = "#FFFFFF",
        },
        ["DevezCode Soft"] = new()
        {
            Name = "DevezCode Soft", Background = "#F2EDE6", Foreground = "#2A2620",
            CursorColor = "#5C8C4A", SelectionBackground = "#C2D8B0",
            // Blue/BrightBlue: claude 가 마크다운 굵은글씨(클래스명 등)에 raw ANSI blue 를 쓰는데
            // 이 색을 위한 claude 테마 override 토큰이 없어 여기(ANSI 팔레트)에서만 바꿀 수 있다.
            // soft 는 초록 계열이라 브랜드 그린(claude/suggestion 원래색 계열)으로 교체(eGhisDevWPF 동일 조치).
            Black = "#2A2620", Red = "#D95F5F", Green = "#5C8C4A", Yellow = "#C97C1A",
            Blue = "#5C8C4A", Purple = "#7C3AED", Cyan = "#0891B2", White = "#F2EDE6",
            BrightBlack = "#5A5448", BrightRed = "#C84A4A", BrightGreen = "#4E7A3E", BrightYellow = "#B86A15",
            BrightBlue = "#7BAA68", BrightPurple = "#8B5CF6", BrightCyan = "#0E7490", BrightWhite = "#FAF7F2",
        },
        ["DevezCode Minimal"] = new()
        {
            Name = "DevezCode Minimal", Background = "#F8FAFC", Foreground = "#0F172A",
            CursorColor = "#2563EB", SelectionBackground = "#DBEAFE",
            Black = "#0F172A", Red = "#DC2626", Green = "#15803D", Yellow = "#CA8A04",
            Blue = "#2563EB", Purple = "#7C3AED", Cyan = "#0891B2", White = "#F8FAFC",
            BrightBlack = "#475569", BrightRed = "#EF4444", BrightGreen = "#22C55E", BrightYellow = "#EAB308",
            BrightBlue = "#3B82F6", BrightPurple = "#8B5CF6", BrightCyan = "#06B6D4", BrightWhite = "#FFFFFF",
        },
        ["DevezCode Gray"] = new()
        {
            Name = "DevezCode Gray", Background = "#F3F4F6", Foreground = "#1F2937",
            CursorColor = "#4B5563", SelectionBackground = "#D9DDE3",
            Black = "#1F2937", Red = "#C2413E", Green = "#15803D", Yellow = "#A16207",
            Blue = "#326AA5", Purple = "#76558F", Cyan = "#0E7490", White = "#FFFFFF",
            BrightBlack = "#5F6774", BrightRed = "#DC5B57", BrightGreen = "#2F9A57", BrightYellow = "#C28A2C",
            BrightBlue = "#4D82B8", BrightPurple = "#926EAE", BrightCyan = "#268CA2", BrightWhite = "#FFFFFF",
        },
        ["DevezCode Soft Pink"] = new()
        {
            Name = "DevezCode Soft Pink", Background = "#FFF7FA", Foreground = "#3B2931",
            CursorColor = "#B54A6B", SelectionBackground = "#F2C9D7",
            Black = "#3B2931", Red = "#C2413E", Green = "#25723C", Yellow = "#9A650B",
            Blue = "#326A9F", Purple = "#84588F", Cyan = "#16758A", White = "#FFFCFD",
            BrightBlack = "#735763", BrightRed = "#DC5B57", BrightGreen = "#3D9254", BrightYellow = "#BB852B",
            BrightBlue = "#4A83B5", BrightPurple = "#A16DAE", BrightCyan = "#308CA0", BrightWhite = "#FFFFFF",
        },
        ["DevezCode Midnight Blue"] = new()
        {
            Name = "DevezCode Midnight Blue", Background = "#111827", Foreground = "#E5E7EB",
            CursorColor = "#60A5FA", SelectionBackground = "#1E3A5F",
            Black = "#111827", Red = "#F87171", Green = "#34D399", Yellow = "#FBBF24",
            Blue = "#60A5FA", Purple = "#A78BFA", Cyan = "#22D3EE", White = "#E5E7EB",
            BrightBlack = "#9CA3AF", BrightRed = "#FCA5A5", BrightGreen = "#6EE7B7", BrightYellow = "#FCD34D",
            BrightBlue = "#93C5FD", BrightPurple = "#C4B5FD", BrightCyan = "#67E8F9", BrightWhite = "#FFFFFF",
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
        // WT default profile 이 pwsh/wsl 등을 가리키지만 그 바이너리가 실제로 없으면(미설치/제거)
        // CreateProcessW 가 "셸 실행 실패"로 throw 돼 세션 생성 자체가 죽는다. 실행 가능 여부를 확인해
        // 불가하면 항상 존재하는 powershell.exe 로 폴백한다.
        commandLine = EnsureRunnableShell(commandLine);

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

    /// <summary>커맨드라인 선두 실행 파일이 실제로 실행 가능한지 확인하고, 아니면 안전한 셸로 폴백한다.
    /// 검증이 예외로 실패하면 원본을 그대로 둔다(오탐으로 정상 셸을 죽이지 않음).</summary>
    private static string EnsureRunnableShell(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "powershell.exe";
        try
        {
            var exe = ExtractLeadingExecutable(commandLine);
            if (exe == null || ExecutableResolvable(exe)) return commandLine;
            DevezCode.Services.DiagLog.Write(
                $"WT 셸 커맨드라인 실행 불가 → powershell.exe 폴백: [{commandLine}]");
            return "powershell.exe"; // System32 상주 — 항상 실행 가능
        }
        catch (Exception) { return commandLine; }
    }

    /// <summary>커맨드라인에서 선두 실행 파일 토큰만 뽑는다(인용/공백 처리). 환경변수는 확장.</summary>
    private static string? ExtractLeadingExecutable(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.Length == 0) return null;

        string token;
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            token = end > 0 ? s.Substring(1, end - 1) : s.Substring(1);
        }
        else
        {
            int sp = s.IndexOfAny(new[] { ' ', '\t' });
            token = sp > 0 ? s.Substring(0, sp) : s;
        }

        token = Environment.ExpandEnvironmentVariables(token);
        return token.Length == 0 ? null : token;
    }

    /// <summary>실행 파일이 절대/상대 경로면 File.Exists, bare 이름이면 PATH·WindowsApps 스캔으로 확인.</summary>
    private static bool ExecutableResolvable(string exe)
    {
        // 경로 구분자·드라이브 포함 → 경로로 취급
        if (exe.IndexOf('\\') >= 0 || exe.IndexOf('/') >= 0 || (exe.Length >= 2 && exe[1] == ':'))
            return File.Exists(exe);

        var withExt = Path.HasExtension(exe) ? exe : exe + ".exe";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in paths)
        {
            var candidate = Path.Combine(dir.Trim('"'), withExt);
            if (File.Exists(candidate)) return true;
        }

        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", withExt);
        return File.Exists(windowsApps);
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
