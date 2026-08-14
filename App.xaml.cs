using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using DevezCode.Behaviors;
using DevezCode.Services;
using DevezCode.Services.Terminal;
using DevezCode.Services.ClaudeSdk;

namespace DevezCode;

public partial class App : Application
{
    private static string ThemeFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "theme.txt");

    /// <summary>현재 적용된 테마. 미리보기 중에는 preview 값으로 바뀐다.</summary>
    public static string CurrentTheme { get; private set; } = "dark";
    public static bool IsDarkTheme(string theme) => theme is "dark" or "midnight";

    /// <summary>문서(md) 뷰어에 적용할 실효 테마. 설정이 "auto"면 <paramref name="appTheme"/>(앱 테마)를 따라가고,
    /// 그 외에는 저장된 테마로 고정한다.</summary>
    public static string MarkdownEffectiveTheme(string appTheme)
    {
        var pref = Services.SettingsService.LoadMarkdownTheme();
        return pref == "auto" ? appTheme : pref;
    }

    /// <summary>문서 뷰어가 앱 테마와 다른 테마로 고정됐을 때 쓸 색(bg/panel/text/primary hex).
    /// SetTheme 의 테마별 색과 일치. 현재 로드된 앱 테마와 같은 테마면 라이브 리소스를 쓰므로 호출하지 않는다.</summary>
    public static (string Bg, string Panel, string Text, string Primary) MarkdownPalette(string theme) => theme switch
    {
        "dark"     => ("#1f1f1e", "#272727", "#e8e8e8", "#c2622a"),
        "midnight" => ("#111827", "#1f2937", "#e5e7eb", "#60a5fa"),
        "soft"     => ("#f2ede6", "#faf7f2", "#2a2620", "#5c8c4a"),
        "gray"     => ("#f3f4f6", "#ffffff", "#1f2937", "#4b5563"),
        "softpink" => ("#fff7fa", "#fffcfd", "#3b2931", "#b54a6b"),
        _          => ("#f8fafc", "#ffffff", "#0f172a", "#2563eb"), // minimal
    };

    /// <summary>디스크에 확정 저장된 테마(persist=true 로만 갱신). 미리보기(persist=false)는 반영 안 됨.
    /// 프로세스 내부에 고정되는 색(codex OSC 감지, COLORFGBG 폴백)은 미리보기가 새면 취소해도
    /// 안 돌아오므로, 라이브 <see cref="CurrentTheme"/> 대신 이 값을 참조한다.</summary>
    public static string CommittedTheme { get; private set; } = "dark";

    /// <summary>테마명 → DevezCode 터미널 스킴. 미확정(빌트인 없음)이면 Campbell 폴백.</summary>
    public static Services.Terminal.WtColorScheme SchemeForTheme(string theme)
    {
        var name = theme switch
        {
            "dark" => "DevezCode Dark",
            "soft" => "DevezCode Soft",
            "gray" => "DevezCode Gray",
            "softpink" => "DevezCode Soft Pink",
            "midnight" => "DevezCode Midnight Blue",
            _      => "DevezCode Minimal",
        };
        return Services.Terminal.WtColorScheme.BuiltIns.TryGetValue(name, out var s)
            ? s
            : Services.Terminal.WtColorScheme.Campbell;
    }

    /// <summary>Codex 아이콘 pack URI (파란색 고정, 테마 무관). Codex UI 공용 소스.</summary>
    public static string CodexIconUri =>
        "pack://application:,,,/Resources/Images/ShellPresets/codex.png";
    /// <summary>테마별 OpenCode 아이콘 pack URI (dark=흰색, light=검정). 푸터/사용량 패널 공용 소스.</summary>
    public static string OpenCodeIconUri =>
        $"pack://application:,,,/Resources/Images/ShellPresets/opencode_icon_{(IsDarkTheme(CurrentTheme) ? "white" : "black")}_50.png";
    /// <summary>테마별 Kimi 아이콘 pack URI (dark=흰색, light=검정). 푸터/사용량 패널 공용 소스.</summary>
    public static string KimiIconUri =>
        $"pack://application:,,,/Resources/Images/ShellPresets/kimi_icon_{(IsDarkTheme(CurrentTheme) ? "white" : "black")}_50.png";
    /// <summary>Devez Vibe 아이콘 pack URI. 채워진 컬러 로고라 테마 변형이 없다.</summary>
    public static string DevezVibeIconUri =>
        "pack://application:,,,/Resources/Images/ShellPresets/devezvibe_icon.png";
    /// <summary>테마별 Grok 아이콘 pack URI (dark=흰색, light=검정).</summary>
    public static string GrokIconUri =>
        $"pack://application:,,,/Resources/Images/ShellPresets/grok_icon_{(IsDarkTheme(CurrentTheme) ? "white" : "black")}_50.png";
    /// <summary>테마 변경 시 발생.</summary>
    public static event Action<string>? ThemeChanged;
    public static event Action<int>? FontScaleChanged;

    /// <summary>자동 업데이트 파일 교체 실패로 임시 exe 가 재실행됐는지 여부(--update-failed).
    /// MainWindow 로드 후 수동 재설치 안내를 띄우는 데 사용.</summary>
    public static bool UpdateFailedRelaunch { get; private set; }

#if !DEBUG
    private System.Threading.Mutex? _singleInstanceMutex;
    private const string SingleInstanceMutexName = @"Global\DevezCode.SingleInstance";
#endif

    /// <summary>'지금 재시작하고 업데이트'(RestartForAgentUpdate)로 재실행됐는지 — 시작 시 에이전트 업데이트를
    /// 데일리 게이트/자동업데이트 토글과 무관하게 강제 실행한다. --update-agents-now 인자로 전달.</summary>
    private static bool _forceAgentUpdateNow;
    private static string? _forceAgentUpdateAgentId;

    /// <summary>스타트업/런타임 크래시 진단 로그 경로(%AppData%\DevezCode\crash.log).
    /// 전역 예외가 아무 메시지 없이 앱을 죽일 때 원인을 남긴다.</summary>
    private static string CrashLogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "crash.log");

    /// <summary>컨텍스트 메뉴 항목은 좌클릭으로만 실행한다. 메뉴를 연 우클릭이
    /// 항목까지 전달돼 Click 으로 해석되는 것을 막는다.</summary>
    private static void BlockContextMenuItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not MenuItem item || !IsInContextMenu(item)) return;
        e.Handled = true;
    }

    private static bool IsInContextMenu(MenuItem item)
    {
        for (ItemsControl? parent = ItemsControl.ItemsControlFromItemContainer(item);
             parent != null;
             parent = parent is MenuItem parentItem
                 ? ItemsControl.ItemsControlFromItemContainer(parentItem)
                 : null)
        {
            if (parent is ContextMenu) return true;
        }

        return false;
    }

    /// <summary>처리되지 않은 예외를 crash.log 에 append 한다(best-effort). UI/백그라운드 공통.</summary>
    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var path = CrashLogFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            File.AppendAllText(path,
                $"[{stamp}] v{ver} {source}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}");
        }
        catch { /* best-effort — 로깅 실패가 크래시 처리를 막지 않도록 */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Windows Terminal 안에서 실행되는 독립 외부 세션 프록시는 UI·단일 인스턴스·환경 정리를
        // 모두 건너뛴다. 메인 앱이 종료돼도 이 프로세스와 그 ConPTY는 계속 살아 있어야 한다.
        if (ExternalSessionProxy.IsRequested(e.Args))
        {
            int exitCode;
            try
            {
                exitCode = ExternalSessionProxy.RunFromEnvironment();
            }
            catch (Exception ex)
            {
                WriteCrashLog("ExternalSessionProxy", ex);
                exitCode = 1;
            }
            Shutdown(exitCode);
            return;
        }

        // 모든 WPF 세로 스크롤에서 작은 트랙패드 델타만 누적하고 120단위 마우스 휠은 그대로 둔다.
        PrecisionWheelScroll.RegisterGlobally();

        // ContextMenu 는 우클릭으로 열되, 열린 MenuItem 은 좌클릭으로만 선택한다.
        EventManager.RegisterClassHandler(typeof(MenuItem), UIElement.PreviewMouseRightButtonDownEvent,
            new MouseButtonEventHandler(BlockContextMenuItemRightClick), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), UIElement.PreviewMouseRightButtonUpEvent,
            new MouseButtonEventHandler(BlockContextMenuItemRightClick), true);

        // 전역 예외 핸들러 — 스타트업 포함 어느 지점 크래시든 crash.log 에 기록.
        // (핸들러 부착 전 크래시는 못 잡으므로 OnStartup 최상단에서 건다.)
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            // 기록만 — 처리 표시 안 함(기존 크래시 동작 유지, 관찰만).
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            WriteCrashLog("UnobservedTaskException", args.Exception);

        // 상속된 DEVEZCODE_ROOM_ID 제거 — 방 안의 claude 세션이 앱을 재실행(빌드 후 Start-Process,
        // 외부 claude 에서 bin\DevezCode.exe 실행 등)하면, 그 claude 프로세스가 자기 방 ID 를 env 로
        // 물고 있어 새 DevezCode 가 그대로 상속한다. 그러면 방별 --settings roomArg 를 못 받는 훅 경로
        // (내부 서브에이전트 등)가 env 폴백으로 떨어져 남의 방 세션을 그 상속 방에 기록 → "중복세션·ID
        // 추적 불가". 시작 즉시 지워 오염 사슬을 끊는다(정식 방 launch 는 매번 재세팅하므로 무해).
        Environment.SetEnvironmentVariable("DEVEZCODE_ROOM_ID", null);

        // 자동 업데이트 재실행 플래그(단일 인스턴스 분기보다 먼저 읽어 둔다).
        UpdateFailedRelaunch = e.Args.Contains("--update-failed");

        // '지금 재시작하고 업데이트'로 재실행된 경우 — 시작 시 에이전트 업데이트를 강제 실행
        // (자동업데이트 토글 OFF·오늘 이미 업데이트했음과 무관하게). RestartForAgentUpdate 가 붙인 인자.
        _forceAgentUpdateNow = e.Args.Contains("--update-agents-now");
        _forceAgentUpdateAgentId = e.Args.FirstOrDefault(a => a.StartsWith("--update-agent=", StringComparison.OrdinalIgnoreCase))?
            ["--update-agent=".Length..];


        // 단일 인스턴스: 이미 떠 있으면 기존 창을 앞으로 가져오고 종료한다.
        // (여러 인스턴스가 동시에 떠 있으면 workspace.json 을 서로 덮어써 등록한 프로젝트/세션이 사라진다.)
        // Debug 빌드는 설치본과 나란히 띄워 개발하려고 이 게이트를 통째로 끈다(디버거 attach 불필요).
        // ⚠ %AppData%\DevezCode\ 는 여전히 공유하므로, 디버그 쪽에서 프로젝트 추가·세션 열기 등
        //    workspace 를 변경하는 작업을 하면 설치본 상태를 덮어쓴다.
#if !DEBUG
        if (!System.Diagnostics.Debugger.IsAttached)
        {
            _singleInstanceMutex = new System.Threading.Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirst);
            if (!isFirst)
            {
                ActivateExistingInstance();
                Shutdown();
                return;
            }
        }
#endif

        // 원격 접속(RDP/터미널 세션, Chrome Remote Desktop)에서는 GPU 합성 화면이 원격 프로토콜로
        // 전달되지 않아 창이 검게/안 보이거나 멈춰 보인다. 원격이거나 사용자가 GPU 를 끈 경우
        // 소프트웨어 렌더링으로 강제한다. ProcessRenderMode 는 첫 비주얼 생성 전에 가장 확실하고,
        // 이후에는 타이틀 로고 세 번 클릭(ApplyRenderMode)으로 창 단위 전환이 가능하다.
        if (IsRemoteControlSession() || !SettingsService.LoadUseGpuAcceleration())
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        base.OnStartup(e);

        // 세션 열기 지연 진단 — UI 스레드 정지 감지 + 구간 로그 (%AppData%\DevezCode\diag.log)
        DevezCode.Services.DiagLog.StartUiStallDetector();

        // 세션→내장 브라우저 조작 브리지. 파이프 이름/토큰을 프로세스 환경변수로 심어야 이후 생성되는
        // 모든 세션 자식 프로세스가 상속받으므로 세션 생성보다 먼저 시작한다.
        DevezCode.Services.BrowserBridgeServer.Instance.Start();
        DevezCode.Services.BrowserMcpInstaller.Sync();

        // ConPTY 자식(node 기반 claude/codex 등)이 색 지원을 간헐적으로 0(무색)으로 오판해 화면
        // 전체가 흰 글자로 렌더되던 문제 방지. node 의 supports-color 는 stdout.isTTY===false 면
        // WT_SESSION 이 있어도 색 레벨 0 을 반환하는데, ConPTY 초기화 타이밍에 따라 isTTY 가
        // 간헐적으로 false 로 잡힌다. FORCE_COLOR=3 / COLORTERM 으로 truecolor 를 강제해 항상 색을 켠다.
        // CreateProcessW 가 부모 환경을 상속하므로(lpEnvironment=Zero) 여기 한 번 설정하면 모든 세션이 상속.
        Environment.SetEnvironmentVariable("FORCE_COLOR", "3");
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");

        // Claude Code 세션 기록(트랜스크립트)을 자동 정리하지 않도록 매 실행 시 영구 보관으로 강제한다.
        // (~/.claude/settings.json 의 cleanupPeriodDays. 설정 UI 의 콤보는 제거했고 항상 이 값으로 고정.)
        ClaudeGlobalSettings.SetCleanupPeriodDays(ClaudeGlobalSettings.PermanentDays);

        SetFontScale(SettingsService.LoadFontScale());
        SetTheme(LoadSavedTheme());

        // claude code 커스텀 테마 (devezcode-soft / devezcode-minimal) 1회 설치.
        // per-session /config theme=custom:<slug> 주입의 기반 — 파일 없으면 claude 가 못 찾음.
        Services.Terminal.ClaudeCustomThemes.EnsureInstalled();

        // opencode 커스텀 테마 (devezcode-soft / devezcode-minimal) 1회 설치.
        // DevezCode 전용 OPENCODE_TUI_CONFIG 주입의 기반.
        Services.Terminal.OpenCodeCustomThemes.EnsureInstalled();

        // 가재코드(gjc) 테마 — 현재 테마 팔레트를 ~/.gjc/agent/themes/devez.json 에 쓰고 config.yml 을 devez 로.
        // SetTheme 안에서도 호출되지만, 시작 시 한 번 확실히 적용.
        Services.Terminal.GajaeCustomThemes.Apply(CurrentTheme);

        // Grok — config.toml theme 매핑.
        Services.Terminal.GrokCustomThemes.Apply(CurrentTheme);

        // 안티그래비티(agy) — settings.json colorScheme 매핑.
        Services.Terminal.AntigravityCustomThemes.Apply(CurrentTheme);

        // Kimi — tui.toml theme(dark/light) 매핑.
        Services.Terminal.KimiCustomThemes.Apply(CurrentTheme);

        StartupSequence();
    }

    /// <summary>메인 창 표시 순서. 설정에서 '실행 시 에이전트 자동 업데이트'가 켜져 있으면,
    /// 메인 창이 뜨기 전에 진행 모달을 먼저 띄워 업데이트 결과를 보여준 뒤 메인 창을 연다.</summary>
    private async void StartupSequence()
    {
#if DEBUG
        // Debug 실행은 로컬 개발 환경의 CLI/플러그인 버전을 변경하지 않는다.
        new MainWindow().Show();
        return;
#endif

        // devez-marketplace 갱신 + hoje-code 플러그인을 하루 1회, 창·모달·알림 없이 백그라운드로 조용히 최신화.
        // 아래 에이전트 자동업데이트 토글/게이트와 무관하게 항상 시도한다(사용자에게 진행을 노출하지 않음).
        TryUpdateDevezPluginsSilently();

        // '지금 재시작하고 업데이트'로 재실행된 경우 자동업데이트 토글/데일리 게이트를 모두 우회하고 강제 실행.
        bool forced = _forceAgentUpdateNow;

        if (!forced && !SettingsService.LoadAutoUpdateAgents())
        {
            new MainWindow().Show();
            return;
        }

        // 시작 시 자동 업데이트는 하루 1회만. 오늘 이미 실행했으면 모달 없이 바로 메인 창.
        // (설정의 '즉시 업데이트'/재시작 강제 실행은 이 게이트와 무관하게 언제나 동작한다.)
        // C(실패 시 재시도): 게이트는 여기서 선점 저장하되, 업데이트가 에이전트를 깨진(Failed) 채로 남기면
        // NotifyAgentUpdateResultsAsync 가 게이트를 비워 다음 실행에서 다시 시도한다(조용히 하루 방치 방지).
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!forced && SettingsService.LoadLastAgentAutoUpdateDate() == today)
        {
            new MainWindow().Show();
            return;
        }
        SettingsService.SaveLastAgentAutoUpdateDate(today);

        // 모달을 닫는 순간(메인 창 열기 전) 열린 창이 0개가 되어 앱이 종료되는 것을 막기 위해
        // 시퀀스 동안 ShutdownMode 를 명시적 종료로 바꾸고, 메인 창을 띄운 뒤 원복한다.
        var prevMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 창이 업데이트 실행·진행표시·완료/건너뛰기 타이밍을 모두 담당한다. 여기선 닫힐 때까지 대기만 한다.
        // (건너뛰기 시 업데이트는 백그라운드로 계속 진행 — npm/bun 설치 중 강제 종료는 손상 위험이 있어 하지 않음.)
        var win = new Views.AgentUpdateWindow { AutoCloseOnComplete = true, AgentId = _forceAgentUpdateAgentId };
        var done = new System.Threading.Tasks.TaskCompletionSource();
        win.Closed += (_, _) => done.TrySetResult();
        win.ProceedRequested += () => { try { win.Close(); } catch { } };
        win.Show();
        await done.Task;

        var mw = new MainWindow();
        this.MainWindow = mw;
        mw.Show();
        ShutdownMode = prevMode; // 원래대로(기본 OnLastWindowClose) 복원

        // 메인 창이 뜬 뒤 업데이트 최종 결과를 팝업으로 알린다.
        // '건너뛰고 시작'으로 모달을 닫았어도 Task 는 계속 돌므로, 끝나는 시점에 알림이 온다.
        _ = NotifyAgentUpdateResultsAsync(win.UpdateTask);
    }

    /// <summary>devez-marketplace/hoje-code 조용한 자동업데이트를 하루 1회만 fire-and-forget 으로 기동한다.
    /// 진행 UI·완료 알림 없이 백그라운드로만 돈다. 실패는 헬퍼 내부에서 삼킨다.</summary>
    private static void TryUpdateDevezPluginsSilently()
    {
        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (SettingsService.LoadLastDevezPluginUpdateDate() == today) return; // 오늘 이미 시도했으면 스킵
            SettingsService.SaveLastDevezPluginUpdateDate(today);                 // 게이트 선점(중복 기동 방지)
            _ = Services.ClaudePluginService.UpdateDevezSilentlyAsync();          // await 하지 않음 → 시작 지연 0
        }
        catch { /* 설정 접근 실패 등: 조용히 무시 */ }
    }

    /// <summary>실행 중인 세션을 안전 종료 경로로 닫고 앱을 재시작하면서 에이전트 업데이트를 강제 실행한다
    /// (설정 '즉시 업데이트'에서 세션이 떠 있을 때 사용 — 인플레이스 설치는 바이너리 잠금으로 실패/파손 위험).
    ///
    /// 단일 인스턴스 뮤텍스 때문에 새 인스턴스는 현재 프로세스가 완전히 종료된 뒤에 떠야 한다 → PowerShell 헬퍼가
    /// 현재 PID 종료를 기다렸다가 <c>--update-agents-now</c> 로 재실행한다(UpdateService 의 교체/재실행과 동일 원리).
    /// 재실행된 인스턴스는 <see cref="StartupSequence"/> 에서 게이트/토글과 무관하게 업데이트 모달을 띄운다.
    /// 종료 자체는 UpdateService 와 같이 메인 창 Close() 로 안전 경로(스냅샷·graceful 세션 종료)를 태운다.
    ///
    /// 반환: 재실행 헬퍼를 기동해 종료 절차를 시작했으면 true. 헬퍼 기동 실패 시 false(호출부가 안내) — 이때는
    /// 앱을 닫지 않는다(닫으면 재실행 없이 그냥 꺼져 버리므로).</summary>
    public static bool RestartForAgentUpdate(string? agentId = null)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            var pid = Environment.ProcessId;
            var script = Path.Combine(Path.GetTempPath(), "devezcode_agentupdate_restart.ps1");
            var exeLit = exe.Replace("'", "''");
            // 현재 프로세스가 완전히 종료(=뮤텍스 해제)된 뒤에 새 인스턴스를 띄운다 → 단일 인스턴스 충돌 방지.
            File.WriteAllText(script,
                $"try {{ Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue }} catch {{ }}\n" +
                $"Start-Process '{exeLit}' -ArgumentList '--update-agents-now{(string.IsNullOrWhiteSpace(agentId) ? "" : " --update-agent=" + agentId)}' -WorkingDirectory (Split-Path '{exeLit}')\n",
                // ScriptFile.Ps1(BOM) 필수 — 없으면 powershell 5.1 이 CP949 로 읽어 한글 사용자명
                // 경로(C:\Users\김이영\...)가 깨지고 재실행이 조용히 실패한다.
                Services.ScriptFile.Ps1);

            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            if (p == null) return false;
        }
        catch { return false; }

        // 헬퍼가 무장됐으면 안전 종료 시작. 모달(설정) 핸들러 안에서 호출될 수 있어 BeginInvoke 로 현재 스택을
        // 먼저 되감은 뒤 실행한다(재진입 방지). 메인 창 Close() 가 MainWindow.OnWindowClosing 의 안전 경로를 태운다.
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var mw = Application.Current.MainWindow;
            // 종료 오버레이에 '업데이트 후 자동으로 다시 실행됩니다' 안내를 띄우도록 표시.
            if (mw is MainWindow m) { m.RestartingForUpdate = true; m.ForceQuit = true; } // '닫기 시 최소화' 무시하고 실제 종료
            if (mw != null) { mw.Closed += (_, _) => Application.Current.Shutdown(); mw.Close(); }
            else Application.Current.Shutdown();
        }));
        return true;
    }

    /// <summary>시작 시 자동 업데이트의 최종 결과 — 성공(버전 변경)/실패를 모아 중앙 모달 1개로 보여준다.
    /// '이미 최신'은 무음(매일 뜨면 성가심). 타임아웃으로 백그라운드 계속(InProgress)인 항목은
    /// 서비스의 완주 감시가 끝나는 시점에 같은 모달로 따로 알린다.</summary>
    private static async System.Threading.Tasks.Task NotifyAgentUpdateResultsAsync(
        System.Threading.Tasks.Task<IReadOnlyList<AgentUpdateResult>>? updateTask)
    {
        if (updateTask == null) return;
        IReadOnlyList<AgentUpdateResult> results;
        try { results = await updateTask; } catch { return; }

        // (C 의 데일리 게이트 비우기는 시작·설정 두 경로가 공유하는 AgentUpdateService.UpdateEnabledAgentsAsync
        //  안에서 일원화 처리한다 — 여기서는 결과 모달 표시만 담당.)
        // 실제 교체가 있었으면(업데이트가 config/바이너리를 건드려 시작 시 적용한 테마를 날렸을 수 있음)
        // 파일 기반 에이전트 테마를 다시 확정 기록한다. no-op(UpToDate) 실행은 클로버가 없으므로 생략.
        // (타임아웃으로 InProgress 였던 항목은 WatchDetachedCompletionAsync 가 완주 시점에 동일 처리한다.)
        if (results.Any(r => r.Status == AgentUpdateStatus.Updated))
            ReapplyAgentFileThemes();

        var show = results
            .Where(r => r.Status is AgentUpdateStatus.Updated or AgentUpdateStatus.Failed)
            .ToList();
        if (show.Count > 0) ShowAgentUpdateResults(show);
    }

    /// <summary>업데이트 결과 모달(중앙)을 띄운다. UI 스레드가 아니어도 안전(디스패치).
    /// 메인 창이 있으면 Owner 로 걸어 그 중앙에, 없으면 화면 중앙에 표시.</summary>
    public static void ShowAgentUpdateResults(IReadOnlyList<AgentUpdateResult> results)
    {
        var app = Current;
        if (app == null || results.Count == 0) return;
        app.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var win = new Views.AgentUpdateResultWindow(results);
                var owner = app.MainWindow;
                if (owner != null && owner.IsVisible) win.Owner = owner;
                win.ShowDialog();
            }
            catch { /* best effort — 결과 표시 실패가 앱을 막지 않도록 */ }
        });
    }

    /// <summary>토스트 알림을 표시한다(설정 위치에 스택). UI 스레드가 아니어도 안전.</summary>
    public static void ShowNotification(string title, string? content, Action? onClick = null)
    {
        var app = Current;
        if (app == null) return;
        app.Dispatcher.InvokeAsync(() =>
        {
            try { new Views.NotificationPopup(title, content, onClick).Show(); }
            catch { /* best effort — 알림 실패가 앱을 막지 않도록 */ }
        });
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

    /// <summary>RDP/CRD 로 "지금 원격 제어 중"인지. 원격에는 쓸 GPU 가 없어 GPU 경로가 소프트 에뮬레이션
    /// (WebView2 는 SwiftShader)으로 떨어지므로, 원격일 때만 GPU 경로를 피하려는 판정에 쓴다.
    /// ⚠ WPF 렌더모드(<see cref="IsSoftwareRenderingActive"/>)와 혼용 금지 — WebView2 는 별 프로세스·별
    /// 렌더 경로라 WPF 가 SoftwareOnly 라는 사실만으로 WebView2 GPU 를 끌 이유가 없다(터미널 xterm
    /// WebGL 을 껐다가 세로 바·박스 테두리 이음새가 끊긴 회귀 — terminal.html 렌더러 주석 참고).</summary>
    public static bool IsRemoteControlSession() => IsRemoteSession() || IsCrdSessionActive();

    /// <summary>현재 프로세스/메인 창이 소프트웨어 렌더인지.</summary>
    public static bool IsSoftwareRenderingActive()
    {
        if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly) return true;
        try
        {
            var w = Current?.MainWindow;
            if (w != null && PresentationSource.FromVisual(w) is HwndSource src
                && src.CompositionTarget is HwndTarget ht)
                return ht.RenderMode == RenderMode.SoftwareOnly;
        }
        catch { /* best effort */ }
        return false;
    }

    /// <summary>GPU ↔ 소프트웨어 렌더 전환. 설정 영속 + 열린 창에 즉시 적용(재시작 없이 CRD 대응용).
    /// ProcessRenderMode 는 시작 전 설정이 가장 확실하지만, 창별 HwndTarget.RenderMode 는 런타임 변경 가능.</summary>
    public static void ApplyRenderMode(bool useGpu)
    {
        var mode = useGpu ? RenderMode.Default : RenderMode.SoftwareOnly;
        try { RenderOptions.ProcessRenderMode = mode; } catch { /* 이미 비주얼이 있어도 best-effort */ }
        try { SettingsService.SaveUseGpuAcceleration(useGpu); } catch { /* 영속 실패는 무시 */ }

        void applyAll()
        {
            var app = Current;
            if (app == null) return;
            foreach (Window w in app.Windows)
                ApplyWindowRenderMode(w, mode);
        }

        var cur = Current;
        if (cur == null) return;
        if (cur.Dispatcher.CheckAccess()) applyAll();
        else cur.Dispatcher.Invoke(applyAll);
    }

    /// <summary>GPU/소프트웨어 토글. 전환 후 모드를 반환(true=GPU).</summary>
    public static bool ToggleRenderMode()
    {
        bool useGpu = IsSoftwareRenderingActive(); // 소프트 → GPU, GPU → 소프트
        ApplyRenderMode(useGpu);
        return useGpu;
    }

    private static void ApplyWindowRenderMode(Window w, RenderMode mode)
    {
        try
        {
            if (PresentationSource.FromVisual(w) is HwndSource src
                && src.CompositionTarget is HwndTarget ht)
                ht.RenderMode = mode;
            w.InvalidateVisual();
        }
        catch { /* 창별 best-effort */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 에이전트는 종료 시 transcript(.jsonl)를 flush 하므로, 하드 kill 전에 에이전트별 제어키로 정상
        // 종료를 시도해 마지막 대화를 보존한다. 종료 경로는 CR/LF를 보내지 않아 작성 중 초안을 제출하지 않는다.
        // (예전엔 DisposeAll 로 즉시 kill → 대화가 디스크에 안 남아
        // 재실행 시 그 세션을 복원하지 못했다. opencode/gjc 는 실시간 추적이라 무관 → claude 만 증상이었다.)
        // UI 스레드 데드락을 피하려 Task.Run 으로 실행 후 상한 대기, 잔여는 DisposeAll 로 하드 정리.
        // 상한 = perGrace(2.5s) + postFlush cap(5s) + 정착(0.5s) + Dispose/스냅샷 여유.
        // MainWindow 종료 경로가 이미 처리했으면 여기선 사실상 무동작(세션 목록이 비어 있음).
        // 창을 거치지 않는 종료(업데이트 재시작 등)를 위한 폴백이라 둘을 병렬로 돌리고 상한만 둔다.
        // 상한 = 터미널 perGrace(2.5s)+postFlush(5s) 와 SDK 배수(8s) 중 느린 쪽 + 여유.
        try
        {
            System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.WhenAll(
                TerminalSessionManager.Instance.GracefulShutdownAllAsync(2500),
                ClaudeSdkSessionManager.Instance.ShutdownAllAsync())).Wait(12000);
        }
        catch { /* best-effort */ }
        try { DevezCode.Services.SessionUsageService.Save(); } catch { /* 토큰 집계 영속 best-effort */ }
        try { DevezCode.Services.BrowserBridgeServer.Instance.Stop(); } catch { /* 브리지 정리 best-effort */ }
        try { TerminalSessionManager.Instance.DisposeAll(); } catch { /* 종료 정리 best-effort */ }
#if !DEBUG
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* 소유 안 한 경우 무시 */ }
        _singleInstanceMutex?.Dispose();
#endif
        base.OnExit(e);
    }

    private static string LoadSavedTheme()
    {
        try { if (File.Exists(ThemeFile)) { var t = File.ReadAllText(ThemeFile).Trim(); if (t is "minimal" or "soft" or "dark" or "gray" or "softpink" or "midnight") return t; } }
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

        // 완료/대기 카드 아이콘 세로 보정: 작게(0)는 텍스트와 맞춰 위로 2px, 크게(1)는 0.
        res["HistoryIconY"] = scale == 0 ? -1.0 : 0.0;

        // 플러그인 헤더 액션 버튼: 글꼴 크게(1)에서만 텍스트가 아이콘보다 살짝 떠 보여 1px 내림.
        res["HeaderActionTextMargin"] = new Thickness(0, scale == 1 ? 1 : 0, 0, 0);

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
        var isDarkTheme = IsDarkTheme(theme);

        Color bg, panel, panelSoft, line, text, textMuted, primary, primaryHover, primaryPressed, primarySoft, bubble, bubbleBorder, danger;
        Color checkedBubble, checkedBorder, checkedText;
        Color star, sidebarSubText, today;
        Color markerRed, markerOrange, markerYellow, markerGreen, markerTeal, markerBlue, markerPurple, markerPink;

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
            markerRed      = Color.FromRgb(0xf8, 0x71, 0x71);
            markerOrange   = Color.FromRgb(0xfb, 0x92, 0x3c);
            markerYellow   = Color.FromRgb(0xfa, 0xcc, 0x15);
            markerGreen    = Color.FromRgb(0x4a, 0xde, 0x80);
            markerTeal     = Color.FromRgb(0x2d, 0xd4, 0xbf);
            markerBlue     = Color.FromRgb(0x60, 0xa5, 0xfa);
            markerPurple   = Color.FromRgb(0xa7, 0x8b, 0xfa);
            markerPink     = Color.FromRgb(0xf4, 0x72, 0xb6);
        }
        else if (theme == "midnight")
        {
            bg             = Color.FromRgb(0x11, 0x18, 0x27);
            panel          = Color.FromRgb(0x1f, 0x29, 0x37);
            panelSoft      = Color.FromRgb(0x29, 0x35, 0x48);
            line           = Color.FromRgb(0x37, 0x41, 0x51);
            text           = Color.FromRgb(0xe5, 0xe7, 0xeb);
            textMuted      = Color.FromRgb(0x9c, 0xa3, 0xaf);
            primary        = Color.FromRgb(0x60, 0xa5, 0xfa);
            primaryHover   = Color.FromRgb(0x3b, 0x82, 0xf6);
            primaryPressed = Color.FromRgb(0x25, 0x63, 0xeb);
            primarySoft    = Color.FromRgb(0x1e, 0x3a, 0x5f);
            bubble         = Color.FromRgb(0x17, 0x24, 0x3a);
            bubbleBorder   = Color.FromRgb(0x2d, 0x4a, 0x6b);
            danger         = Color.FromRgb(0xf8, 0x71, 0x71);
            checkedBubble  = Color.FromRgb(0x12, 0x35, 0x2d);
            checkedBorder  = Color.FromRgb(0x2a, 0x76, 0x61);
            checkedText    = Color.FromRgb(0x6e, 0xe7, 0xb7);
            star           = Color.FromRgb(0xfb, 0xbf, 0x24);
            sidebarSubText = Color.FromRgb(0xcb, 0xd5, 0xe1);
            today          = Color.FromRgb(0xfb, 0x92, 0x3c);
            markerRed      = Color.FromRgb(0xf8, 0x71, 0x71);
            markerOrange   = Color.FromRgb(0xfb, 0x92, 0x3c);
            markerYellow   = Color.FromRgb(0xfa, 0xcc, 0x15);
            markerGreen    = Color.FromRgb(0x34, 0xd3, 0x99);
            markerTeal     = Color.FromRgb(0x2d, 0xd4, 0xbf);
            markerBlue     = primary;
            markerPurple   = Color.FromRgb(0xa7, 0x8b, 0xfa);
            markerPink     = Color.FromRgb(0xf4, 0x72, 0xb6);
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
            markerRed      = Color.FromRgb(0xd9, 0x5f, 0x5f);
            markerOrange   = Color.FromRgb(0xd9, 0x77, 0x2a);
            markerYellow   = Color.FromRgb(0xb0, 0x8a, 0x2e);
            markerGreen    = Color.FromRgb(0x5c, 0x8c, 0x4a);
            markerTeal     = Color.FromRgb(0x4f, 0x8c, 0x82);
            markerBlue     = Color.FromRgb(0x4e, 0x70, 0xa3);
            markerPurple   = Color.FromRgb(0x80, 0x64, 0xa2);
            markerPink     = Color.FromRgb(0xb3, 0x5d, 0x79);
        }
        else if (theme == "gray")
        {
            bg           = Color.FromRgb(0xf3, 0xf4, 0xf6);
            panel        = Colors.White;
            panelSoft    = Color.FromRgb(0xe5, 0xe7, 0xeb);
            line         = Color.FromRgb(0xd1, 0xd5, 0xdb);
            text         = Color.FromRgb(0x1f, 0x29, 0x37);
            textMuted    = Color.FromRgb(0x5f, 0x67, 0x74);
            primary      = Color.FromRgb(0x4b, 0x55, 0x63);
            primaryHover = Color.FromRgb(0x37, 0x41, 0x51);
            primaryPressed = Color.FromRgb(0x1f, 0x29, 0x37);
            primarySoft  = Color.FromRgb(0xe2, 0xe5, 0xe9);
            bubble       = Color.FromRgb(0xec, 0xee, 0xf1);
            bubbleBorder = Color.FromRgb(0xc7, 0xcd, 0xd4);
            danger       = Color.FromRgb(0xc2, 0x41, 0x3e);
            checkedBubble = Color.FromRgb(0xf0, 0xfd, 0xf4);
            checkedBorder = Color.FromRgb(0x86, 0xef, 0xac);
            checkedText   = Color.FromRgb(0x16, 0x65, 0x34);
            star          = Color.FromRgb(0xb7, 0x79, 0x1f);
            sidebarSubText = textMuted;
            today         = Color.FromRgb(0xc2, 0x41, 0x0c);
            markerRed      = Color.FromRgb(0xc2, 0x41, 0x3e);
            markerOrange   = Color.FromRgb(0xb4, 0x53, 0x09);
            markerYellow   = Color.FromRgb(0x92, 0x61, 0x16);
            markerGreen    = Color.FromRgb(0x15, 0x80, 0x3d);
            markerTeal     = Color.FromRgb(0x0f, 0x76, 0x70);
            markerBlue     = Color.FromRgb(0x32, 0x6a, 0xa5);
            markerPurple   = Color.FromRgb(0x76, 0x55, 0x8f);
            markerPink     = Color.FromRgb(0xa8, 0x45, 0x68);
        }
        else if (theme == "softpink")
        {
            bg           = Color.FromRgb(0xff, 0xf7, 0xfa);
            panel        = Color.FromRgb(0xff, 0xfc, 0xfd);
            panelSoft    = Color.FromRgb(0xfc, 0xef, 0xf4);
            line         = Color.FromRgb(0xeb, 0xcf, 0xd9);
            text         = Color.FromRgb(0x3b, 0x29, 0x31);
            textMuted    = Color.FromRgb(0x73, 0x57, 0x63);
            primary      = Color.FromRgb(0xb5, 0x4a, 0x6b);
            primaryHover = Color.FromRgb(0xa4, 0x3d, 0x5f);
            primaryPressed = Color.FromRgb(0x8e, 0x34, 0x51);
            primarySoft  = Color.FromRgb(0xf8, 0xdc, 0xe6);
            bubble       = Color.FromRgb(0xfb, 0xe7, 0xee);
            bubbleBorder = Color.FromRgb(0xe8, 0xbf, 0xcf);
            danger       = Color.FromRgb(0xc2, 0x41, 0x3e);
            checkedBubble = Color.FromRgb(0xe9, 0xf5, 0xec);
            checkedBorder = Color.FromRgb(0x91, 0xc7, 0x9b);
            checkedText   = Color.FromRgb(0x25, 0x72, 0x3c);
            star          = Color.FromRgb(0x9a, 0x65, 0x0b);
            sidebarSubText = textMuted;
            today         = Color.FromRgb(0xc2, 0x41, 0x0c);
            markerRed      = Color.FromRgb(0xc2, 0x41, 0x3e);
            markerOrange   = Color.FromRgb(0xa1, 0x62, 0x07);
            markerYellow   = Color.FromRgb(0x92, 0x61, 0x16);
            markerGreen    = Color.FromRgb(0x25, 0x72, 0x3c);
            markerTeal     = Color.FromRgb(0x16, 0x75, 0x8a);
            markerBlue     = Color.FromRgb(0x32, 0x6a, 0x9f);
            markerPurple   = Color.FromRgb(0x84, 0x58, 0x8f);
            markerPink     = primary;
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
            markerRed      = Color.FromRgb(0xef, 0x44, 0x44);
            markerOrange   = Color.FromRgb(0xf9, 0x73, 0x16);
            markerYellow   = Color.FromRgb(0xca, 0x8a, 0x04);
            markerGreen    = Color.FromRgb(0x16, 0xa3, 0x4a);
            markerTeal     = Color.FromRgb(0x0d, 0x94, 0x88);
            markerBlue     = Color.FromRgb(0x25, 0x63, 0xeb);
            markerPurple   = Color.FromRgb(0x7c, 0x3a, 0xed);
            markerPink     = Color.FromRgb(0xdb, 0x27, 0x77);
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
        res["LightFileIconOpacity"] = isDarkTheme ? 0.0 : 1.0;
        res["DarkFileIconOpacity"]  = isDarkTheme ? 1.0 : 0.0;

        res["BgBrush"]                = new SolidColorBrush(bg);
        res["PanelBrush"]             = new SolidColorBrush(panel);
        res["PanelSoftBrush"]         = new SolidColorBrush(panelSoft);
        res["HoverBrush"] = isDarkTheme
            ? new SolidColorBrush(theme == "midnight" ? Color.FromRgb(0x26, 0x34, 0x49) : Color.FromRgb(0x42, 0x42, 0x42))
            : new SolidColorBrush(panelSoft);
        res["LineBrush"]              = new SolidColorBrush(line);
        res["TextBrush"]              = new SolidColorBrush(text);
        res["TextMutedBrush"]         = new SolidColorBrush(textMuted);
        res["PrimaryBrush"]           = new SolidColorBrush(primary);
        // 세션 생존 상태는 액션 강조색과 분리한다. Gray에서는 청회색/연회색으로 즉시 구분.
        var sessionAlive = theme == "gray" ? Color.FromRgb(0x32, 0x6a, 0xa5) : primary;
        var sessionInactive = theme == "gray" ? Color.FromRgb(0x9c, 0xa3, 0xaf) : Color.FromRgb(0x6b, 0x72, 0x80);
        res["SessionAliveBrush"]      = new SolidColorBrush(sessionAlive);
        res["SessionInactiveBrush"]   = new SolidColorBrush(sessionInactive);
        res["ProjectMarkerRedBrush"]    = new SolidColorBrush(markerRed);
        res["ProjectMarkerOrangeBrush"] = new SolidColorBrush(markerOrange);
        res["ProjectMarkerYellowBrush"] = new SolidColorBrush(markerYellow);
        res["ProjectMarkerGreenBrush"]  = new SolidColorBrush(markerGreen);
        res["ProjectMarkerTealBrush"]   = new SolidColorBrush(markerTeal);
        res["ProjectMarkerBlueBrush"]   = new SolidColorBrush(markerBlue);
        res["ProjectMarkerPurpleBrush"] = new SolidColorBrush(markerPurple);
        res["ProjectMarkerPinkBrush"]   = new SolidColorBrush(markerPink);
        var codeSyntaxString = theme switch
        {
            "soft" => Color.FromRgb(0x3f, 0x6f, 0x30),
            "minimal" => Color.FromRgb(0x15, 0x80, 0x3d),
            _ => markerGreen,
        };
        var codeSyntaxNumber = theme switch
        {
            "soft" => Color.FromRgb(0xb4, 0x53, 0x09),
            "minimal" => Color.FromRgb(0xc2, 0x41, 0x0c),
            _ => markerOrange,
        };
        var codeSyntaxType = theme switch
        {
            "soft" => Color.FromRgb(0x35, 0x6f, 0x67),
            "minimal" => Color.FromRgb(0x0f, 0x76, 0x6e),
            _ => markerTeal,
        };
        res["CodeSyntaxKeywordBrush"]  = new SolidColorBrush(markerPurple);
        res["CodeSyntaxStringBrush"]   = new SolidColorBrush(codeSyntaxString);
        res["CodeSyntaxNumberBrush"]   = new SolidColorBrush(codeSyntaxNumber);
        res["CodeSyntaxFunctionBrush"] = new SolidColorBrush(markerBlue);
        res["CodeSyntaxTypeBrush"]     = new SolidColorBrush(codeSyntaxType);
        res["TabFocusRingBrush"]      = new SolidColorBrush(Color.FromArgb(0x8C, primary.R, primary.G, primary.B));
        res["PrimaryOverlay50Brush"]  = new SolidColorBrush(Color.FromArgb(0x1A, primary.R, primary.G, primary.B));
        res["ProjectCardHoverBrush"] = isDarkTheme
            ? new SolidColorBrush(theme == "midnight" ? Color.FromRgb(0x26, 0x34, 0x49) : Color.FromRgb(0x42, 0x42, 0x42))
            : new SolidColorBrush(Color.FromArgb(0x1A, primary.R, primary.G, primary.B));
        res["ProjectCardHoverBorderBrush"] = isDarkTheme
            ? new SolidColorBrush(theme == "midnight" ? Color.FromRgb(0x4b, 0x63, 0x80) : Color.FromRgb(0x5a, 0x5a, 0x5a))
            : new SolidColorBrush(Color.FromArgb(0x80, primary.R, primary.G, primary.B));
        res["PrimaryHoverBrush"]      = new SolidColorBrush(primaryHover);
        res["PrimaryPressedBrush"]    = new SolidColorBrush(primaryPressed);
        var tableHl = isDarkTheme ? primarySoft : primary;
        byte tableHlAlpha = isDarkTheme ? (byte)0x80 : (byte)0x20;
        res["TableHighlightBrush"]    = new SolidColorBrush(Color.FromArgb(tableHlAlpha, tableHl.R, tableHl.G, tableHl.B));
        res["TableHighlightLineBrush"] = isDarkTheme
            ? new SolidColorBrush(theme == "midnight" ? Color.FromRgb(0x4b, 0x63, 0x80) : Color.FromRgb(0x5e, 0x5e, 0x5e))
            : new SolidColorBrush(Color.FromArgb(0x55, tableHl.R, tableHl.G, tableHl.B));
        res["PrimarySoftBrush"]       = new SolidColorBrush(primarySoft);
        double documentGroupMix = isDarkTheme ? 0.36 : 0.24;
        res["DocumentGroupAlternateBrush"] = new SolidColorBrush(Color.FromRgb(
            (byte)(panelSoft.R + (markerPurple.R - panelSoft.R) * documentGroupMix),
            (byte)(panelSoft.G + (markerPurple.G - panelSoft.G) * documentGroupMix),
            (byte)(panelSoft.B + (markerPurple.B - panelSoft.B) * documentGroupMix)));
        res["PrimarySoftLighterBrush"] = new SolidColorBrush(Color.FromRgb(
            (byte)(primarySoft.R + (bg.R - primarySoft.R) * 0.5),
            (byte)(primarySoft.G + (bg.G - primarySoft.G) * 0.5),
            (byte)(primarySoft.B + (bg.B - primarySoft.B) * 0.5)));
        res["BubbleBrush"]            = new SolidColorBrush(bubble);
        res["BubbleBorderBrush"]      = new SolidColorBrush(bubbleBorder);
        // 세션 row 호버/포커스 배경 (devez 정합: 테마별 색상 구조 상이)
        res["SessionHoverBrush"] = new SolidColorBrush(
            theme == "dark" ? panelSoft  // 다크: 호버=기본(변화 없음)
            : theme == "midnight" ? Color.FromRgb(0x26, 0x34, 0x49)
            : theme == "soft" ? Color.FromRgb(0xea, 0xe5, 0xdc)
            : theme == "gray" ? Color.FromRgb(0xe9, 0xeb, 0xef)
            : theme == "softpink" ? Color.FromRgb(0xfa, 0xe8, 0xef)
            : Color.FromRgb(0xef, 0xf3, 0xf7)); // minimal
        res["SessionFocusBrush"] = new SolidColorBrush(
            theme == "dark" ? Color.FromRgb(0x5a, 0x5a, 0x5a)
            : theme == "midnight" ? Color.FromRgb(0x1e, 0x3a, 0x5f)
            : theme == "soft" ? Color.FromRgb(0xdc, 0xea, 0xd4)
            : theme == "gray" ? Color.FromRgb(0xd9, 0xdd, 0xe3)
            : theme == "softpink" ? Color.FromRgb(0xf2, 0xc9, 0xd7)
            : Color.FromRgb(0xd9, 0xe8, 0xfc)); // minimal
        res["DangerBrush"]            = new SolidColorBrush(danger);
        var successGreen = isDarkTheme
            ? (theme == "midnight" ? Color.FromRgb(0x34, 0xd3, 0x99) : Color.FromRgb(0x22, 0xc5, 0x5e))
            : Color.FromRgb(0x15, 0x80, 0x3d);
        res["SuccessColor"]           = successGreen;
        res["SuccessBrush"]           = new SolidColorBrush(successGreen);
        var saturday = isDarkTheme
            ? Color.FromRgb(0x60, 0xa5, 0xfa)
            : Color.FromRgb(0x25, 0x63, 0xeb);
        res["SaturdayColor"]          = saturday;
        res["SaturdayBrush"]          = new SolidColorBrush(saturday);
        res["SidebarSubTextColor"]    = sidebarSubText;
        res["SidebarSubTextBrush"]    = new SolidColorBrush(sidebarSubText);
        var railIcon = isDarkTheme ? (theme == "midnight" ? Color.FromRgb(0xcb, 0xd5, 0xe1) : Color.FromRgb(0xc4, 0xc4, 0xc4)) : textMuted;
        res["RailIconColor"]          = railIcon;
        res["RailIconBrush"]          = new SolidColorBrush(railIcon);
        res["CheckedBubbleBrush"]     = new SolidColorBrush(checkedBubble);
        res["CheckedBubbleBorderBrush"] = new SolidColorBrush(checkedBorder);
        res["CheckedTextBrush"]       = new SolidColorBrush(checkedText);
        res["BubbleCheckedBgColor"]     = checkedBubble;
        res["BubbleCheckedBorderColor"] = checkedBorder;
        res["BubbleCheckedTextColor"]   = checkedText;
        res["CheckedBubbleOpacity"]     = isDarkTheme ? 0.3 : 0.4;
        res["ShadowColor"]              = Colors.Black;
        res["ShadowOpacity"]            = isDarkTheme ? 0.68 : 0.35;
        res["PopupShadowOpacity"]       = isDarkTheme ? 0.42 : 0.18;
        res["WindowShadowBlurRadius"]   = isDarkTheme ? 48.0 : 40.0;
        res["WindowShadowOuterMargin"]  = isDarkTheme ? new Thickness(22) : new Thickness(20);
        res["WindowShadowInnerMargin"]  = isDarkTheme ? new Thickness(3) : new Thickness(1);
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
        else if (theme == "midnight")
        {
            codeBg          = bg;
            codePanel       = Color.FromRgb(0x1f, 0x29, 0x37);
            codeBorder      = Color.FromRgb(0x37, 0x41, 0x51);
            codeText        = Color.FromRgb(0xe5, 0xe7, 0xeb);
            codeMuted       = Color.FromRgb(0x9c, 0xa3, 0xaf);
            codeLabelBg     = Color.FromRgb(0x17, 0x24, 0x3a);
            codeLabelBorder = Color.FromRgb(0x2d, 0x4a, 0x6b);
            codeLabelText   = Color.FromRgb(0x93, 0xc5, 0xfd);
            codeActiveBg    = Color.FromRgb(0x1e, 0x3a, 0x5f);
            codeActiveText  = Color.FromRgb(0x93, 0xc5, 0xfd);
            codeHover       = Color.FromArgb(0x24, 0x60, 0xa5, 0xfa);
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
        else if (theme == "gray")
        {
            codeBg = bg; codePanel = Colors.White; codeBorder = Color.FromRgb(0xd1, 0xd5, 0xdb);
            codeText = Color.FromRgb(0x1f, 0x29, 0x37); codeMuted = Color.FromRgb(0x5f, 0x67, 0x74);
            codeLabelBg = Color.FromRgb(0xec, 0xee, 0xf1); codeLabelBorder = Color.FromRgb(0xc7, 0xcd, 0xd4);
            codeLabelText = Color.FromRgb(0x4b, 0x55, 0x63); codeActiveBg = Color.FromRgb(0xe2, 0xe5, 0xe9);
            codeActiveText = Color.FromRgb(0x37, 0x41, 0x51); codeHover = Color.FromArgb(0x1a, 0x4b, 0x55, 0x63);
        }
        else if (theme == "softpink")
        {
            codeBg = bg; codePanel = Color.FromRgb(0xff, 0xfc, 0xfd); codeBorder = Color.FromRgb(0xeb, 0xcf, 0xd9);
            codeText = Color.FromRgb(0x3b, 0x29, 0x31); codeMuted = Color.FromRgb(0x73, 0x57, 0x63);
            codeLabelBg = Color.FromRgb(0xfb, 0xe7, 0xee); codeLabelBorder = Color.FromRgb(0xe8, 0xbf, 0xcf);
            codeLabelText = Color.FromRgb(0xb5, 0x4a, 0x6b); codeActiveBg = Color.FromRgb(0xf8, 0xdc, 0xe6);
            codeActiveText = Color.FromRgb(0xa4, 0x3d, 0x5f); codeHover = Color.FromArgb(0x1a, 0xb5, 0x4a, 0x6b);
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
            var newScheme = SchemeForTheme(theme);
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
            CommittedTheme = theme; // 확정 저장 시에만 갱신 — 프로세스 고정 색 참조용
        }

        CurrentTheme = theme;
        // AvalonEdit 파일 에디터 구문색 — 정의 싱글턴 색을 현재 테마로 덮어쓴다(ThemeChanged 핸들러보다 먼저).
        Services.SyntaxThemeService.Apply(theme);
        // 가재코드(gjc) — devez.json 팔레트 덮어쓰기. gjc의 테마는 전역 파일 하나(watch)라 외부 세션도 영향받지만,
        // 이 호출을 빼면 DevezCode 안 세션도 못 따라간다. (Claude/OpenCode는 per-project 설정이라 이런 문제 없음)
        Services.Terminal.GajaeCustomThemes.Apply(theme);
        // Grok — config.toml theme 갱신.
        Services.Terminal.GrokCustomThemes.Apply(theme);
        // 안티그래비티(agy) — settings.json colorScheme 갱신 (세션 재시작 시 반영).
        Services.Terminal.AntigravityCustomThemes.Apply(theme);
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>에이전트 자동업데이트는 각 CLI 의 config/설정 파일을 재작성·정규화하거나 바이너리를 교체하면서,
    /// 시작 시(<see cref="OnStartup"/>) 적용해 둔 테마를 날려버릴 수 있다(특히 grok <c>~/.grok/config.toml [ui] theme</c>).
    /// 시작 시 테마 적용은 업데이트보다 <b>먼저</b> 실행되고 그 뒤 재적용이 없었기 때문에, 실제 업데이트가 발생한 실행에서는
    /// 앱을 재실행하기 전까지 테마가 기본값으로 남았다. 업데이트가 완전히 끝난 직후(및 백그라운드 완주 시점)에 이 메서드로
    /// 파일 기반 에이전트 테마를 현재 테마로 다시 확정 기록해 재실행 없이도 유지되게 한다.
    /// best-effort — 실패해도 무해. (codex 는 파일이 아니라 런타임 OSC 색상질의 프록시라 여기 대상이 아니다.)</summary>
    public static void ReapplyAgentFileThemes()
    {
        try
        {
            var theme = CurrentTheme;
            Services.Terminal.GajaeCustomThemes.Apply(theme);
            Services.Terminal.GrokCustomThemes.Apply(theme);
            Services.Terminal.AntigravityCustomThemes.Apply(theme);
            Services.Terminal.KimiCustomThemes.Apply(theme);
        }
        catch { /* best-effort */ }
    }

}
