using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DevezCode.Services;
using DevezCode.Services.Terminal;

namespace DevezCode;

public partial class App : Application
{
    private static string ThemeFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "theme.txt");

    /// <summary>현재 적용된 테마 ("minimal" | "soft" | "dark").</summary>
    public static string CurrentTheme { get; private set; } = "dark";
    /// <summary>테마 변경 시 발생.</summary>
    public static event Action<string>? ThemeChanged;
    public static event Action<int>? FontScaleChanged;

    /// <summary>자동 업데이트 파일 교체 실패로 임시 exe 가 재실행됐는지 여부(--update-failed).
    /// MainWindow 로드 후 수동 재설치 안내를 띄우는 데 사용.</summary>
    public static bool UpdateFailedRelaunch { get; private set; }

    private System.Threading.Mutex? _singleInstanceMutex;
    private const string SingleInstanceMutexName = @"Global\DevezCode.SingleInstance";

    protected override void OnStartup(StartupEventArgs e)
    {
        // 자동 업데이트 재실행 플래그(단일 인스턴스 분기보다 먼저 읽어 둔다).
        UpdateFailedRelaunch = e.Args.Contains("--update-failed");

        // 단일 인스턴스: 이미 떠 있으면 기존 창을 앞으로 가져오고 종료한다.
        // (여러 인스턴스가 동시에 떠 있으면 workspace.json 을 서로 덮어써 등록한 프로젝트/세션이 사라진다.)
        _singleInstanceMutex = new System.Threading.Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        // 원격 접속(RDP/터미널 세션, Chrome Remote Desktop)에서는 GPU 합성 화면이 원격 프로토콜로
        // 전달되지 않아 창이 검게/안 보인다. 이런 환경에서만 소프트웨어 렌더링으로 강제한다.
        // ProcessRenderMode 는 첫 비주얼 생성 전에만 의미가 있으므로 반드시 여기서 설정한다.
        if (IsRemoteSession() || IsCrdSessionActive())
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        base.OnStartup(e);
        SetFontScale(SettingsService.LoadFontScale());
        SetTheme(LoadSavedTheme());

        new MainWindow().Show();
    }

    /// <summary>이미 실행 중인 DevezCode 창을 복원·전경으로 가져온다.
    /// 전경 잠금(다른 앱이 포커스를 쥔 상태)을 우회하려고 잠깐 topmost 밴드로 끌어올렸다가 바로 푼다.</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                BringToFront(h);
                break;
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>대상 창을 다른 앱 위로 한 번 끌어올린다(항상 위 고정 아님). 전경 잠금 우회.</summary>
    public static void BringToFront(IntPtr h)
    {
        try
        {
            ShowWindow(h, 9 /* SW_RESTORE */);
            SetWindowPos(h, HWND_TOPMOST,   0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(h);
        }
        catch { /* best effort */ }
    }

    private static readonly IntPtr HWND_TOPMOST   = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_REMOTESESSION = 0x1000;

    /// <summary>클래식 RDP/터미널 세션 여부.</summary>
    private static bool IsRemoteSession()
    {
        try { return GetSystemMetrics(SM_REMOTESESSION) != 0; }
        catch { return false; }
    }

    /// <summary>Chrome Remote Desktop "실제 접속 중" 여부. 접속 중에만 remoting_desktop 프로세스가 존재한다
    /// (상시 실행되는 remoting_host 가 아니라 desktop 프로세스로 판별 — devez 실환경 검증).</summary>
    private static bool IsCrdSessionActive()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("remoting_desktop").Length > 0; }
        catch { return false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { TerminalSessionManager.Instance.DisposeAll(); } catch { /* 종료 정리 best-effort */ }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* 소유 안 한 경우 무시 */ }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static string LoadSavedTheme()
    {
        try { if (File.Exists(ThemeFile)) { var t = File.ReadAllText(ThemeFile).Trim(); if (t is "minimal" or "soft" or "dark") return t; } }
        catch { /* 무시 */ }
        return "dark"; // 기본값
    }

    // 글꼴 크기: 크게=모든 Fs* 리소스를 +2
    private static readonly int[] FontSizeBases = { 8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 26, 36 };

    public void SetFontScale(int scale)
    {
        var res = Application.Current.Resources;
        int d = scale switch { 1 => 2, _ => 0 };
        foreach (var b in FontSizeBases)
            res[$"Fs{b}"] = (double)(b + d);

        // 성능 칩(CPU/RAM) 폰트: devez의 Weather 칩과 동일 증가폭(+1, 상한 14/12)을 따른다.
        // 일반 Fs* 는 큰 배율에서 +2 라 칩만 더 커지던 문제를 막아 devez와 크기를 맞춘다.
        res["PerfChipValFs"] = scale == 0 ? 13.0 : 14.0;
        res["PerfChipLblFs"] = scale == 0 ? 11.0 : 12.0;

        res["HdrBtnSize"] = (double)(28 + d);
        res["RailWidth"]    = scale == 0 ? 44.0 : 46.0;
        res["RailBtnSize"]  = scale == 0 ? 32.0 : 34.0;
        res["RailIconSize"] = scale == 0 ? 16.0 : 18.0;
        FontScaleChanged?.Invoke(scale);
    }

    /// <summary>테마를 적용한다. <paramref name="persist"/> 가 false 면 디스크 저장 없이
    /// 화면에만 반영(설정창 라이브 미리보기용 — 저장/취소로 확정).</summary>
    public void SetTheme(string theme, bool persist = true)
    {
        var res = Application.Current.Resources;

        Color bg, panel, panelSoft, line, text, textMuted, primary, primaryHover, primaryPressed, primarySoft, bubble, bubbleBorder, danger;
        Color checkedBubble, checkedBorder, checkedText;
        Color star, sidebarSubText, today;

        if (theme == "dark")
        {
            bg             = Color.FromRgb(0x1f, 0x1f, 0x1e);
            panel          = Color.FromRgb(0x27, 0x27, 0x27);
            panelSoft      = Color.FromRgb(0x2f, 0x2f, 0x2f);
            line           = Color.FromRgb(0x40, 0x40, 0x40);
            text           = Color.FromRgb(0xe8, 0xe8, 0xe8);
            textMuted      = Color.FromRgb(0xaa, 0xaa, 0xaa);
            primary        = Color.FromRgb(0xc2, 0x62, 0x2a);
            primaryHover   = Color.FromRgb(0xb5, 0x57, 0x1f);
            primaryPressed = Color.FromRgb(0xa0, 0x4c, 0x18);
            bubble         = Color.FromRgb(0x36, 0x36, 0x36);
            primarySoft    = Color.FromRgb(0x43, 0x43, 0x43);
            bubbleBorder   = Color.FromRgb(0x42, 0x42, 0x42);
            danger         = Color.FromRgb(0xef, 0x44, 0x44);
            checkedBubble  = Color.FromRgb(0x21, 0x28, 0x22);
            checkedBorder  = Color.FromRgb(0x38, 0x60, 0x38);
            checkedText    = Color.FromRgb(0x86, 0xef, 0xac);
            star           = Color.FromRgb(0xfb, 0xbf, 0x24);
            sidebarSubText = Color.FromRgb(0xcc, 0xcc, 0xcc);
            today          = Color.FromRgb(0xf9, 0x73, 0x16);
        }
        else if (theme == "soft")
        {
            bg           = Color.FromRgb(0xf2, 0xed, 0xe6);
            panel        = Color.FromRgb(0xfa, 0xf7, 0xf2);
            panelSoft    = Color.FromRgb(0xec, 0xe7, 0xde);
            line         = Color.FromRgb(0xd8, 0xd2, 0xc6);
            text         = Color.FromRgb(0x2a, 0x26, 0x20);
            textMuted    = Color.FromRgb(0x5a, 0x54, 0x48);
            primary      = Color.FromRgb(0x5c, 0x8c, 0x4a);
            primaryHover = Color.FromRgb(0x4e, 0x7a, 0x3e);
            primaryPressed = Color.FromRgb(0x42, 0x68, 0x34);
            primarySoft  = Color.FromRgb(0xde, 0xec, 0xd6);
            bubble       = Color.FromRgb(0xe6, 0xf0, 0xde);
            bubbleBorder = Color.FromRgb(0xc2, 0xd8, 0xb0);
            danger       = Color.FromRgb(0xd9, 0x5f, 0x5f);
            checkedBubble = Color.FromRgb(0xd4, 0xed, 0xca);
            checkedBorder = Color.FromRgb(0x8f, 0xc4, 0x7e);
            checkedText   = Color.FromRgb(0x2a, 0x52, 0x20);
            star          = Color.FromRgb(0xc9, 0x7c, 0x1a);
            sidebarSubText = textMuted;
            today         = Color.FromRgb(0xea, 0x76, 0x2c);
        }
        else // minimal
        {
            bg           = Color.FromRgb(0xf8, 0xfa, 0xfc);
            panel        = Color.FromRgb(0xff, 0xff, 0xff);
            panelSoft    = Color.FromRgb(0xf1, 0xf5, 0xf9);
            line         = Color.FromRgb(0xe2, 0xe8, 0xf0);
            text         = Color.FromRgb(0x0f, 0x17, 0x2a);
            textMuted    = Color.FromRgb(0x47, 0x55, 0x69);
            primary      = Color.FromRgb(0x25, 0x63, 0xeb);
            primaryHover = Color.FromRgb(0x1d, 0x4e, 0xd8);
            primaryPressed = Color.FromRgb(0x1e, 0x40, 0xaf);
            primarySoft  = Color.FromRgb(0xdb, 0xea, 0xfe);
            bubble       = Color.FromRgb(0xee, 0xf4, 0xff);
            bubbleBorder = Color.FromRgb(0xc5, 0xd8, 0xf8);
            danger       = Color.FromRgb(0xef, 0x44, 0x44);
            checkedBubble = Color.FromRgb(0xf0, 0xfd, 0xf4);
            checkedBorder = Color.FromRgb(0x86, 0xef, 0xac);
            checkedText   = Color.FromRgb(0x16, 0x65, 0x34);
            star          = Color.FromRgb(0xf5, 0x9e, 0x0b);
            sidebarSubText = textMuted;
            today         = Color.FromRgb(0xf9, 0x73, 0x16);
        }

        res["BgColor"]             = bg;
        res["PanelColor"]          = panel;
        res["PanelSoftColor"]      = panelSoft;
        res["LineColor"]           = line;
        res["TextColor"]           = text;
        res["TextMutedColor"]      = textMuted;
        res["PrimaryColor"]        = primary;
        res["PrimaryHoverColor"]   = primaryHover;
        res["PrimaryPressedColor"] = primaryPressed;
        res["PrimarySoftColor"]    = primarySoft;
        res["BubbleColor"]         = bubble;
        res["BubbleBorderColor"]   = bubbleBorder;

        res["BgBrush"]                = new SolidColorBrush(bg);
        res["PanelBrush"]             = new SolidColorBrush(panel);
        res["PanelSoftBrush"]         = new SolidColorBrush(panelSoft);
        res["LineBrush"]              = new SolidColorBrush(line);
        res["TextBrush"]              = new SolidColorBrush(text);
        res["TextMutedBrush"]         = new SolidColorBrush(textMuted);
        res["PrimaryBrush"]           = new SolidColorBrush(primary);
        res["TabFocusRingBrush"]      = new SolidColorBrush(Color.FromArgb(0x8C, primary.R, primary.G, primary.B));
        res["PrimaryOverlay50Brush"]  = new SolidColorBrush(Color.FromArgb(0x1A, primary.R, primary.G, primary.B));
        res["PrimaryHoverBrush"]      = new SolidColorBrush(primaryHover);
        res["PrimaryPressedBrush"]    = new SolidColorBrush(primaryPressed);
        var tableHl = theme == "dark" ? primarySoft : primary;
        byte tableHlAlpha = theme == "dark" ? (byte)0x80 : (byte)0x20;
        res["TableHighlightBrush"]    = new SolidColorBrush(Color.FromArgb(tableHlAlpha, tableHl.R, tableHl.G, tableHl.B));
        res["TableHighlightLineBrush"] = theme == "dark"
            ? new SolidColorBrush(Color.FromRgb(0x5e, 0x5e, 0x5e))
            : new SolidColorBrush(Color.FromArgb(0x55, tableHl.R, tableHl.G, tableHl.B));
        res["PrimarySoftBrush"]       = new SolidColorBrush(primarySoft);
        res["PrimarySoftLighterBrush"] = new SolidColorBrush(Color.FromRgb(
            (byte)(primarySoft.R + (bg.R - primarySoft.R) * 0.5),
            (byte)(primarySoft.G + (bg.G - primarySoft.G) * 0.5),
            (byte)(primarySoft.B + (bg.B - primarySoft.B) * 0.5)));
        res["BubbleBrush"]            = new SolidColorBrush(bubble);
        res["BubbleBorderBrush"]      = new SolidColorBrush(bubbleBorder);
        res["DangerBrush"]            = new SolidColorBrush(danger);
        var successGreen = theme == "dark"
            ? Color.FromRgb(0x22, 0xc5, 0x5e)
            : Color.FromRgb(0x15, 0x80, 0x3d);
        res["SuccessColor"]           = successGreen;
        res["SuccessBrush"]           = new SolidColorBrush(successGreen);
        var saturday = theme == "dark"
            ? Color.FromRgb(0x60, 0xa5, 0xfa)
            : Color.FromRgb(0x25, 0x63, 0xeb);
        res["SaturdayColor"]          = saturday;
        res["SaturdayBrush"]          = new SolidColorBrush(saturday);
        res["SidebarSubTextColor"]    = sidebarSubText;
        res["SidebarSubTextBrush"]    = new SolidColorBrush(sidebarSubText);
        var railIcon = theme == "dark" ? Color.FromRgb(0xc4, 0xc4, 0xc4) : textMuted;
        res["RailIconColor"]          = railIcon;
        res["RailIconBrush"]          = new SolidColorBrush(railIcon);
        res["CheckedBubbleBrush"]     = new SolidColorBrush(checkedBubble);
        res["CheckedBubbleBorderBrush"] = new SolidColorBrush(checkedBorder);
        res["CheckedTextBrush"]       = new SolidColorBrush(checkedText);
        res["BubbleCheckedBgColor"]     = checkedBubble;
        res["BubbleCheckedBorderColor"] = checkedBorder;
        res["BubbleCheckedTextColor"]   = checkedText;
        res["CheckedBubbleOpacity"]     = theme == "dark" ? 0.3 : 0.4;
        res["ShadowColor"]              = Colors.Black;
        res["ShadowOpacity"]            = theme == "dark" ? 0.68 : 0.35;
        res["PopupShadowOpacity"]       = theme == "dark" ? 0.42 : 0.18;
        res["WindowShadowBlurRadius"]   = theme == "dark" ? 48.0 : 40.0;
        res["WindowShadowOuterMargin"]  = theme == "dark" ? new Thickness(22) : new Thickness(20);
        res["WindowShadowInnerMargin"]  = theme == "dark" ? new Thickness(3) : new Thickness(1);
        res["StarBrush"]              = new SolidColorBrush(star);
        res["TodayColor"]             = today;
        res["TodayBrush"]             = new SolidColorBrush(today);
        var badge = Color.FromRgb(
            (byte)(primary.R + (255 - primary.R) * 0.25),
            (byte)(primary.G + (255 - primary.G) * 0.25),
            (byte)(primary.B + (255 - primary.B) * 0.25));
        res["CountBadgeBrush"]        = new SolidColorBrush(badge);
        double badgeLum = (0.299 * badge.R + 0.587 * badge.G + 0.114 * badge.B) / 255.0;
        res["CountBadgeTextBrush"]    = new SolidColorBrush(
            badgeLum > 0.62 ? Color.FromRgb(0x1f, 0x29, 0x37) : Colors.White);

        // ── Code block tokens ──────────────────────────────────────────────
        Color codeBg, codePanel, codeBorder, codeText, codeMuted;
        Color codeLabelBg, codeLabelBorder, codeLabelText;
        Color codeActiveBg, codeActiveText, codeHover;
        if (theme == "dark")
        {
            codeBg          = bg;
            codePanel       = Color.FromRgb(0x27, 0x27, 0x27);
            codeBorder      = Color.FromRgb(0x40, 0x40, 0x40);
            codeText        = Color.FromRgb(0xe8, 0xe8, 0xe8);
            codeMuted       = Color.FromRgb(0xaa, 0xaa, 0xaa);
            codeLabelBg     = Color.FromRgb(0x36, 0x36, 0x36);
            codeLabelBorder = Color.FromRgb(0x42, 0x42, 0x42);
            codeLabelText   = Color.FromRgb(0xe8, 0xe8, 0xe8);
            codeActiveBg    = Color.FromArgb(0x14, 0xff, 0xff, 0xff);
            codeActiveText  = Color.FromRgb(0xe8, 0xe8, 0xe8);
            codeHover       = Color.FromArgb(0x10, 0xff, 0xff, 0xff);
        }
        else if (theme == "soft")
        {
            codeBg          = bg;
            codePanel       = Color.FromRgb(0xfa, 0xf7, 0xf2);
            codeBorder      = Color.FromRgb(0xd8, 0xd2, 0xc6);
            codeText        = Color.FromRgb(0x2a, 0x26, 0x20);
            codeMuted       = Color.FromRgb(0x5a, 0x54, 0x48);
            codeLabelBg     = Color.FromRgb(0xe6, 0xf0, 0xde);
            codeLabelBorder = Color.FromRgb(0xc2, 0xd8, 0xb0);
            codeLabelText   = Color.FromRgb(0x5c, 0x8c, 0x4a);
            codeActiveBg    = Color.FromRgb(0xde, 0xec, 0xd6);
            codeActiveText  = Color.FromRgb(0x5c, 0x8c, 0x4a);
            codeHover       = Color.FromArgb(0x1a, 0x5c, 0x8c, 0x4a);
        }
        else // minimal
        {
            codeBg          = bg;
            codePanel       = Color.FromRgb(0xff, 0xff, 0xff);
            codeBorder      = Color.FromRgb(0xe2, 0xe8, 0xf0);
            codeText        = Color.FromRgb(0x0f, 0x17, 0x2a);
            codeMuted       = Color.FromRgb(0x47, 0x55, 0x69);
            codeLabelBg     = Color.FromRgb(0xee, 0xf4, 0xff);
            codeLabelBorder = Color.FromRgb(0xc5, 0xd8, 0xf8);
            codeLabelText   = Color.FromRgb(0x25, 0x63, 0xeb);
            codeActiveBg    = Color.FromRgb(0xdb, 0xea, 0xfe);
            codeActiveText  = Color.FromRgb(0x25, 0x63, 0xeb);
            codeHover       = Color.FromArgb(0x14, 0x25, 0x63, 0xeb);
        }
        res["CodeBgBrush"]          = new SolidColorBrush(codeBg);
        res["CodePanelBrush"]       = new SolidColorBrush(codePanel);
        res["CodeBorderBrush"]      = new SolidColorBrush(codeBorder);
        res["CodeTextBrush"]        = new SolidColorBrush(codeText);
        res["CodeMutedBrush"]       = new SolidColorBrush(codeMuted);
        res["CodeLabelBgBrush"]     = new SolidColorBrush(codeLabelBg);
        res["CodeLabelBorderBrush"] = new SolidColorBrush(codeLabelBorder);
        res["CodeLabelTextBrush"]   = new SolidColorBrush(codeLabelText);
        res["CodeActiveBgBrush"]    = new SolidColorBrush(codeActiveBg);
        res["CodeActiveTextBrush"]  = new SolidColorBrush(codeActiveText);
        res["CodeHoverBrush"]       = new SolidColorBrush(codeHover);

        // ── 터미널 WebView 색 ───────────────────────────────────────────────
        // 선택 탭을 터미널 실제 배경색으로 칠해 strip 하단 라인을 덮고 본문(터미널)과
        // 매끄럽게 이어붙인다. 색은 WT settings 스킴 기준(테마와 독립). 로드 전이면 테마색 폴백.
        // 테마 변경 시 DevezCode 전용 스킴으로 자동 전환해 임베디드 터미널 색을 UI와 일치시킨다.
        Color terminalBg = bg, terminalFg = text;
        try
        {
            var schemeName = theme switch
            {
                "dark" => "DevezCode Dark",
                "soft" => "DevezCode Soft",
                _      => "DevezCode Minimal",
            };
            WtColorScheme? newScheme = null;
            if (Services.Terminal.WtColorScheme.BuiltIns.TryGetValue(schemeName, out var builtIn))
                newScheme = builtIn;
            if (newScheme != null)
                Services.Terminal.TerminalSessionManager.Instance.WithScheme(newScheme);
            var scheme = Services.Terminal.TerminalSessionManager.Instance.Config.Scheme;
            if (!string.IsNullOrWhiteSpace(scheme.Background))
                terminalBg = (Color)ColorConverter.ConvertFromString(scheme.Background);
            if (!string.IsNullOrWhiteSpace(scheme.Foreground))
                terminalFg = (Color)ColorConverter.ConvertFromString(scheme.Foreground);
        }
        catch { /* 설정 로드 전 — 테마 본문색으로 폴백 */ }
        res["TerminalBgColor"] = terminalBg;
        res["TerminalBgBrush"] = new SolidColorBrush(terminalBg);
        res["TerminalFgBrush"] = new SolidColorBrush(terminalFg);

        // Persist (미리보기 모드면 디스크 저장 생략)
        if (persist)
        {
            try
            {
                var dir = Path.GetDirectoryName(ThemeFile)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(ThemeFile, theme);
            }
            catch { /* non-critical */ }

            // claude code 의 theme 도 동기화 — claude 는 자체 theme (default=dark) 으로 TUI 를 그려서
            // DevezCode 가 light 라도 claude 입력 박스가 dark bar 로 떠 대비가 어색해진다.
            // dark → "dark", soft/minimal → "light". 새 claude 세션부터 적용.
            SyncClaudeTheme(theme);
        }

        CurrentTheme = theme;
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>~/.claude/settings.json 의 theme 키를 DevezCode 테마에 맞춰 갱신.
    /// 다른 설정(hooks, permissions 등)은 그대로 보존. 실패는 조용히 무시 (claude 설정은 비핵심).</summary>
    private static void SyncClaudeTheme(string devezCodeTheme)
    {
        try
        {
            var claudeSettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
            if (!File.Exists(claudeSettingsPath)) return;

            var claudeTheme = devezCodeTheme == "dark" ? "dark" : "light";
            var json = File.ReadAllText(claudeSettingsPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json,
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            var dict = new Dictionary<string, System.Text.Json.JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            dict["theme"] = System.Text.Json.JsonSerializer.SerializeToElement(claudeTheme);

            using var ms = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var kv in dict)
                {
                    writer.WritePropertyName(kv.Key);
                    kv.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            File.WriteAllText(claudeSettingsPath, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch { /* claude 설정 동기화는 best-effort */ }
    }
}
