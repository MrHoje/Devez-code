using System.IO;
using DevezCode.Services;

namespace DevezCode.Services.Terminal;

/// <summary>
/// 채팅방(roomId)별 터미널 세션을 보관하는 전역 싱글톤.
/// 방 전환·탭 토글에도 세션은 유지되고, 앱 종료 시 DisposeAll로 일괄 정리.
/// </summary>
public sealed class TerminalSessionManager
{
    public static TerminalSessionManager Instance { get; } = new();

    /// <summary>신규/재개 세션을 실제 생성하기 직전 발생. UI가 에이전트 로컬 설정/모델 캐시를
    /// 다시 읽어 프로젝트 정보 패널의 model/effort 콤보를 갱신한다.</summary>
    public event Action<string>? AgentModelCatalogRefreshRequested;

    private readonly Dictionary<string, TerminalSession> _sessions = new();
    private readonly object _lock = new();
    private FileSystemWatcher? _claudeSessionWatcher;
    private WtTerminalConfig? _config;

    /// <summary>삭제된 방 ID. 삭제 직후 뒤늦게 도착한 생성 요청으로 claude 가 다시 떠 고아가 되는 것을 막는다.</summary>
    private readonly HashSet<string> _disposedRooms = new();

    /// <summary>앱 종료(graceful shutdown)가 시작됐는가 — 종료 중 세션 Exited 로 앱레벨
    /// 자동 재진입(codex/opencode)이 오발동하지 않게 TerminalHostView 가 확인한다.</summary>
    public volatile bool IsShuttingDown;

    /// <summary>방이 삭제되었는지(다시 세션을 만들면 안 됨).</summary>
    public bool IsRoomDisposed(string roomId)
    {
        lock (_lock) return _disposedRooms.Contains(roomId);
    }

    /// <summary>삭제 표시된 방을 다시 만들 수 있도록 해제. 재시작 시 사용 — 일반 삭제 경로에서는
    /// 호출하지 말 것(뒤늦은 WireSession 이 고아 claude 를 만들 수 있음).</summary>
    public void ClearDisposedRoom(string roomId)
    {
        lock (_lock) _disposedRooms.Remove(roomId);
    }

    /// <summary>유휴 자동 종료로 닫은 방. '모든 세션 미리 로드'가 켜져 있으면 프로젝트를 다시 열 때
    /// 프리로드가 이 방을 배경에서 새 ConPTY 로 되살려 자동 종료가 무효화되고(리소스 절약 실패)
    /// 사이드바 점도 다시 활성색으로 돌아간다. 사용자가 직접 세션을 열 때만 해제한다.</summary>
    private readonly HashSet<string> _idleStoppedRooms = new();

    /// <summary>유휴 자동 종료로 닫힌 방으로 표시 — 이후 프리로드가 건너뛴다.</summary>
    public void MarkIdleStopped(string roomId)
    {
        lock (_lock) _idleStoppedRooms.Add(roomId);
    }

    /// <summary>이 방이 유휴 자동 종료로 닫힌 상태인지(프리로드 제외 대상).</summary>
    public bool IsIdleStopped(string roomId)
    {
        lock (_lock) return _idleStoppedRooms.Contains(roomId);
    }

    /// <summary>사용자가 세션을 직접 열었을 때 해제 — 이후에는 정상적으로 resume·프리로드된다.</summary>
    public void ClearIdleStopped(string roomId)
    {
        lock (_lock) _idleStoppedRooms.Remove(roomId);
    }

    /// <summary>유휴 종료 설정을 끄거나 바꿀 때 전체 해제.</summary>
    public void ClearAllIdleStopped()
    {
        lock (_lock) _idleStoppedRooms.Clear();
    }

    /// <summary>graceful 종료(숨김 지연 종료·부분 재시작)가 진행 중인 방. 이 동안(최대 수 초) 새 배선/
    /// 재부착을 막는다 — 죽어가는 세션에 붙으면 종료 트랜스크립트가 재생되고 "[세션 종료됨]" 죽은 방으로
    /// 굳어 자동 resume 이 안 된다(다른 패널에서의 재오픈·프리로드 경로).</summary>
    private readonly HashSet<string> _gracefulStopping = new();

    /// <summary>이 방이 graceful 종료 진행 중인지 — WireSession/ActivateSession 재부착 가드용.</summary>
    public bool IsGracefulStopping(string roomId)
    {
        lock (_lock) return _gracefulStopping.Contains(roomId);
    }

    /// <summary>살아있는(프로세스 실행 중) 세션이 하나라도 있는지. 에이전트 인플레이스 업데이트 전에
    /// "세션이 바이너리를 잠그고 있는지"를 판단해, 있으면 안전 종료 후 재시작 업데이트로 우회하는 데 쓴다.</summary>
    public bool HasLiveSessions()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values)
                if (s.IsAlive) return true;
            return false;
        }
    }

    /// <summary>방별 "셸 준비 후 주입" 초기 커맨드. 직접 실행(cmd /k) 방·일반 방은 null.
    /// GetOrCreate 가 결정해 채우고 GetInitialCommand 가 1회 소비한다.</summary>
    private readonly Dictionary<string, string?> _pendingInitial = new();

    private TerminalSessionManager()
    {
        // 테마 변경 시 살아있는 모든 claude 세션에 /config theme=X 1회 전송 — 이미 떠 있는 TUI 도 즉시 갱신.
        // per-session 주입(InjectClaudeThemeAsync) 은 새 세션만 커버하므로, 기존 세션 갱신은 이 경로로.
        App.ThemeChanged += OnAppThemeChanged_Broadcast;
    }

    /// <summary>WT settings.json 기반 구성 (최초 1회 로드 후 캐시).</summary>
    public WtTerminalConfig Config
    {
        get
        {
            lock (_lock) return _config ??= WtSettingsLoader.Load();
        }
    }

    /// <summary>새 세션에 적용할 유효 기본 폰트 크기(pt). 설정값이 있으면 그 값, 없으면 WT 프로필 기본값.
    /// 설정 다이얼로그 표시·기존 세션 마이그레이션·EffectiveFontSizePx 가 모두 이 값을 기준으로 삼아 일관성을 맞춘다.</summary>
    public int DefaultFontSizePt
    {
        get
        {
            var saved = SettingsService.LoadTerminalFontSizePt();
            if (saved > 0) return saved;
            return (int)Math.Round(Config.FontSizePx / (96.0 / 72.0));
        }
    }

    /// <summary>현재 활성 스킴을 교체한다. <see cref="Config"/> 는 다른 필드는 그대로 두고 Scheme 만 바뀐 새 인스턴스를 반환.
    /// 기존 세션들의 xterm 테마는 호출자가 ThemeChanged → 브로드캐스트 처리.</summary>
    public WtTerminalConfig WithScheme(WtColorScheme scheme)
    {
        var current = Config;
        if (ReferenceEquals(current.Scheme, scheme)) return current;
        var next = new WtTerminalConfig
        {
            CommandLine = current.CommandLine,
            StartingDirectory = current.StartingDirectory,
            FontFamily = current.FontFamily,
            FontSizePx = current.FontSizePx,
            Scheme = scheme,
        };
        lock (_lock) _config = next;
        return next;
    }

    /// <summary>방 세션을 가져오거나 새로 만든다. 죽은 세션은 교체.</summary>
    public TerminalSession GetOrCreate(string roomId, int cols, int rows)
    {
        lock (_lock)
        {
            // 빠른 경로: 살아있는 세션 재사용. 프리페치(느릴 수 있음)를 타지 않게 먼저 확인.
            if (_sessions.TryGetValue(roomId, out var alive) && alive.IsAlive)
            {
                _pendingInitial[roomId] = null; // 이미 실행 중 — 재주입 금지
                return alive;
            }
        }

        // opencode cwd 폴백 조회는 외부 CLI 실행이라 느릴 수 있다 — _lock 밖에서 미리 끝내
        // 다른 방의 터미널 생성/조회가 함께 블록되지 않게 한다(대상 아니면 즉시 null).
        string? opencodeCwdSession;
        using (DiagLog.Time($"GetOrCreate.prefetch room={roomId}"))
            opencodeCwdSession = PrefetchOpenCodeSessionByCwd(roomId);

        using var _diagCreate = DiagLog.Time($"GetOrCreate.create room={roomId}");
        lock (_lock)
        {
            if (_sessions.TryGetValue(roomId, out var existing))
            {
                if (existing.IsAlive) { _pendingInitial[roomId] = null; return existing; } // 프리페치 사이 다른 호출이 생성 — 재주입 금지
                existing.Dispose();
                _sessions.Remove(roomId);
            }
            var cfg = _config ??= WtSettingsLoader.Load();
            var ccDir = SettingsService.LoadClaudeCodeRoomDir(roomId);

            // Claude Code 방: 셸을 거치지 않고 cmd /k 로 claude 를 직접 띄운다(셸 부팅·주입 대기 제거 → 빠른 진입).
            // .cmd/sudo-shim 해석과 resume 폴백을 위해 cmd 를 경유한다(순수 spawn 으로는 .cmd·shim 실행 불가).
            // 배치 작성 실패 시 옛 방식(셸 + 첫 출력 후 주입)으로 자동 폴백한다.
            string commandLine = cfg.CommandLine;
            string? startDir = cfg.StartingDirectory;
            string? inject = null;

            // 방별 에이전트 조회. Claude 만 풀 통합(훅/resume/세션ID 추적), 그 외는 단순 cmd /k <command> 실행.
            var agentId = SettingsService.LoadAgentForRoom(roomId);
            var agent = AgentRegistry.Find(agentId) ?? AgentRegistry.GetDefault();
            var isOpenCode = agent.Id == "opencode";
            if (agent.Id is "claude" or "codex" or "grok")
                AgentModelCatalogRefreshRequested?.Invoke(agent.Id);

            if (agent.Id == "shell")
            {
                // 하단 터미널 패널: 에이전트 미연결 빈 셸. 훅/resume/세션 추적 없음.
                // cmd 래핑 없이 pwsh 를 직접 스폰(순수 .exe 라 셸 경유 불필요). cwd = 사용자 홈.
                startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                commandLine = $"\"{ResolveShellExe()}\" -NoLogo -NoExit -Command \"{ShellPSReadLineColors}\"";
            }
            else if (ccDir != null && agent.Id == "codex")
            {
                // codex: 클로드와 동일한 "직접 실행" 패턴 (--session-id/--resume, 훅으로 lastmsg/busy/session_id 추적).
                // SupportsHooks=true 인 Claude 의 TryBuildDirectLaunch 와 별도 경로 — 커맨드/훅 스키마가 다름.
                startDir = ccDir;
                var direct = TryBuildCodexDirectLaunch(roomId, ccDir, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && agent.Id == "devezvibe")
            {
                // dvz: 자체 상태 기록(sessions/busy/waiting/lastmsg) + `dvz -r <threadId>` 복원.
                // 훅이 없어 설치기도 없다 — CLI 가 DEVEZCODE_ROOM_ID 를 직접 보고 기록한다.
                // 앱레벨 자동 재진입(codex/grok 패턴).
                startDir = ccDir;
                var direct = TryBuildDevezVibeDirectLaunch(roomId, ccDir, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && agent.Id == "grok")
            {
                // grok: 훅 + `grok -r <id>` 복원. 앱레벨 자동 재진입(codex 패턴).
                startDir = ccDir;
                var direct = TryBuildGrokDirectLaunch(roomId, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && isOpenCode)
            {
                // opencode: Devez 패턴 — 플러그인이 sessions\<room>.txt 에 기록한 session_id 로 --session <id> 로 정확히 복원.
                // 같은 폴더의 여러 방이 있어도 플러그인 $env:DEVEZCODE_ROOM_ID 로 분리됨.
                OpenCodeCustomThemes.Apply(DevezCode.App.CurrentTheme);
                OpenCodeCustomThemes.RemoveLegacyProjectTheme(ccDir);
                startDir = ccDir;
                var direct = TryBuildOpenCodeDirectLaunch(roomId, opencodeCwdSession, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && agent.Id == "gajae")
            {
                // 가재코드(gjc): 방별 격리 --session-dir + 그 폴더 최신 세션 ID 추출 → `gjc -r <id>` 로 복원.
                // gjc 는 --session-id 사전 발급이 없어 cwd 공유 시 -c 가 섞이므로, 방마다 별도 session-dir 로 분리.
                // 실행 직전에도 전역 설정을 보정해 star reminder 의 gh.exe 콘솔 깜빡임을 막는다.
                GajaeCustomThemes.Apply(DevezCode.App.CurrentTheme);
                startDir = ccDir;
                var direct = TryBuildGajaeDirectLaunch(roomId, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && agent.Id == "antigravity")
            {
                // agy: 훅(conversation_id) + `agy --conversation <id>` 복원. 사전 발급이 없어 훅 미기록 방은
                // agy 자체 cwd→conversation 매핑(last_conversations.json)으로 폴백 추종.
                // 앱레벨 자동 재진입(codex/grok 패턴). SupportsHooks=true 지만 훅 스키마가 달라 별도 경로.
                startDir = ccDir;
                var direct = TryBuildAntigravityDirectLaunch(roomId, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && agent.Id == "kimi")
            {
                // kimi(kimi-code): 훅(config.toml [[hooks]]) + `kimi -S <sessionId>` 복원(전역 session_index, cwd 무관).
                // 앱레벨 자동 재진입(codex/grok 패턴). --work-dir 이 없어 cwd 는 startDir(ConPTY)로 처리.
                startDir = ccDir;
                var direct = TryBuildKimiDirectLaunch(roomId, ccDir, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && agent.SupportsHooks)
            {
                startDir = ccDir;
                EnsureClaudeSessionWatcher();
                var direct = TryBuildDirectLaunch(roomId, cfg.CommandLine, out inject);
                if (direct != null) commandLine = direct; // 성공 시 inject == null
                else DiagLog.Write($"claude direct launch 실패 → 셸 폴백(첫 출력 후 주입) room={roomId}");
            }
            else if (ccDir != null)
            {
                // 비-Claude 에이전트: cmd /k "<에이전트 커맨드>" 만 작성. 에러 시 프롬프트가 남아 진단 가능.
                startDir = ccDir;
                var simple = TryBuildSimpleLaunch(roomId, agent);
                if (simple != null) commandLine = simple;
            }

            // claude 는 room-settings.json(--settings 커맨드라인, 프로세스 격리)에 이미 테마가 박혀
            // 시작되므로 프로젝트 local settings 를 따로 건드릴 필요가 없다(예전엔 건드렸으나, 그러면
            // 그 프로젝트 폴더에서 DevezCode 밖의 claude 를 켜도 테마가 새어나가는 부작용이 있었다).

            TerminalSession session;
            var previousOpenCodeTuiConfig = Environment.GetEnvironmentVariable("OPENCODE_TUI_CONFIG");
            var previousXdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            var previousRoomId = Environment.GetEnvironmentVariable("DEVEZCODE_ROOM_ID");
            var previousTrackingAgent = Environment.GetEnvironmentVariable(TrackingEnvironment.VariableName);
            try
            {
                // SessionStart 훅(room-hook.ps1)이 어느 방의 claude 세션인지 알 수 있게
                // 방 ID를 자식(cmd→claude→훅)에 상속시킨다. 생성 직후 해제해 다른 자식 프로세스로 새지 않게 한다.
                // (Claude 외 에이전트는 훅이 없으므로 무해.)
                Environment.SetEnvironmentVariable("DEVEZCODE_ROOM_ID", roomId);
                Environment.SetEnvironmentVariable(TrackingEnvironment.VariableName, agent.Id);
                if (isOpenCode)
                {
                    Environment.SetEnvironmentVariable("OPENCODE_TUI_CONFIG", OpenCodeCustomThemes.TuiConfigPath);
                    Environment.SetEnvironmentVariable("XDG_STATE_HOME", OpenCodeCustomThemes.XdgStateHomePath);
                }
                session = new TerminalSession(commandLine, startDir, cols, rows);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVEZCODE_ROOM_ID", previousRoomId);
                Environment.SetEnvironmentVariable(TrackingEnvironment.VariableName, previousTrackingAgent);
                if (isOpenCode)
                {
                    Environment.SetEnvironmentVariable("OPENCODE_TUI_CONFIG", previousOpenCodeTuiConfig);
                    Environment.SetEnvironmentVariable("XDG_STATE_HOME", previousXdgStateHome);
                }
            }
            // 직접 실행이면 inject==null → 주입 없음. 폴백 셸이면 첫 출력 후 WireSession 에서 inject 전송.
            _pendingInitial[roomId] = inject;
            _sessions[roomId] = session;

            return session;
        }
    }

    /// <summary>App.ThemeChanged → DevezCode 전용 opencode TUI 설정 갱신.
    /// opencode 는 TUI 설정을 시작 시에만 읽으므로 세션 재시작이 필요한데,
    /// 그 재시작은 JS 브리지를 가진 TerminalHostView 가 담당한다(rewire + "restarted" 통지로
    /// "Enter 로 재시작" 프롬프트 없이 매끄럽게 새 테마로 다시 띄움).
    /// claude 는 테마 변경 시 세션 자체가 재시작되고(SettingsDialog → ReloadAllSessionsForTheme),
    /// 재시작된 프로세스가 room-settings.json(--settings 커맨드라인)으로 새 테마를 받으므로
    /// 프로젝트 local settings 를 따로 건드릴 필요가 없다(예전엔 건드렸으나, 그 프로젝트 폴더에서
    /// DevezCode 밖의 claude 를 켜도 테마가 새어나가는 부작용만 있었다).</summary>
    private void OnAppThemeChanged_Broadcast(string theme)
    {
        OpenCodeCustomThemes.Apply(theme);
        KimiCustomThemes.Apply(theme);
    }

    /// <summary>하단 셸 진입 시 -Command 로 주입하는 PSReadLine 색 보정.
    /// PSReadLine 기본색은 다크 콘솔 전제라 입력 텍스트를 흰색(37/97)으로 칠하는데, 라이트 테마 스킴의
    /// white 는 배경과 같은 값이라(soft #F2EDE6, minimal #F8FAFC) 타이핑한 글자가 통째로 사라진다.
    /// hex 를 박지 않고 팔레트 인덱스(39 기본전경/33/32/36/90/31)로 지정해야 테마 전환 시 실행 중인
    /// 셸도 xterm 테마를 따라간다. -Colors 를 모르는 구버전 PSReadLine 은 catch 로 조용히 무시.</summary>
    private const string ShellPSReadLineColors =
        "$e=[char]27; try { Set-PSReadLineOption -Colors @{" +
        "Default=$e+'[39m';Type=$e+'[39m';Number=$e+'[39m';Member=$e+'[39m';" +
        "Command=$e+'[33m';Keyword=$e+'[32m';Variable=$e+'[32m';String=$e+'[36m';" +
        "Comment=$e+'[90m';Operator=$e+'[90m';Parameter=$e+'[90m';Error=$e+'[31m'} } catch {}";

    /// <summary>하단 터미널 패널용 셸 실행 파일. 이름 우선순위(pwsh → powershell)로 PATH 전체를 훑는다
    /// (AgentRegistry.ResolvePath 는 디렉터리 우선이라 System32 의 powershell 이 pwsh 를 이길 수 있음).</summary>
    private static string ResolveShellExe()
    {
        foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
        {
            foreach (var target in new[] { EnvironmentVariableTarget.Process,
                                           EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                string path;
                try { path = Environment.GetEnvironmentVariable("PATH", target) ?? ""; }
                catch { continue; }
                foreach (var dir in path.Split(Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        var full = Path.Combine(dir, name);
                        if (File.Exists(full)) return full;
                    }
                    catch { /* 잘못된 경로 무시 */ }
                }
            }
        }
        return "powershell.exe"; // 비현실적 폴백 — CreateProcess 가 PATH 해석
    }

    /// <summary>비-Claude 에이전트용 단순 cmd /k 커맨드라인. 세션 추적/훅 없음.
    /// 첫 실행 = plain 커맨드 (codex/opencode/gjc 가 새 세션 생성).
    /// 이후 실행 = ResumeFlag (--last / -c) 추가해서 가장 최근 세션 이어가기.</summary>
    private static string? TryBuildSimpleLaunch(string roomId, AgentDef agent)
    {
        if (string.IsNullOrWhiteSpace(agent.Command)) return null;
        // 첫 실행 감지: 저장소에 (roomId, agentId) 가 없으면 새 세션, 있으면 resume.
        bool firstLaunch = !SettingsService.IsAgentRoomLaunched(roomId, agent.Id);
        SettingsService.MarkAgentRoomLaunched(roomId, agent.Id);
        var body = (!firstLaunch && !string.IsNullOrEmpty(agent.ResumeFlag))
            ? $"{agent.Command} {agent.ResumeFlag}"
            : agent.Command;
        // 따옴표로 감싸 PATH/PATHEXT 해석은 cmd 에 맡긴다 (codex.cmd, gjc 등 변형 모두 호환).
        return $"cmd.exe /k \"{body}\"";
    }

    /// <summary>codex 방의 codex 를 cmd /k 배치로 직접 실행 (Claude 의 TryBuildDirectLaunch 와 동일 패턴).
    /// 첫 실행은 <c>codex --no-alt-screen</c> (codex 가 새 session_id 발급), 그 후엔
    /// <c>codex resume --no-alt-screen &lt;sessionId&gt;</c> 로
    /// 같은 대화 복원. SessionStart 훅이 실제 codex session_id 를 <see cref="CodexRoomSessions"/> 에
    /// 갱신 — /clear·수동 재실행으로 ID가 어긋나도 다음 실행 때 최신 ID 로 resume.
    /// (참고: codex CLI 는 <c>--session-id</c> 플래그가 없음 — <c>codex resume &lt;id&gt;</c> 만 가능.)</summary>
    private string? TryBuildCodexDirectLaunch(string roomId, string? workingDir, out string? injectFallback)
    {
        injectFallback = null;
        CodexHookInstaller.EnsureScriptInstalled();
        CodexHookInstaller.InstallHooksJson();

        var sessionId = SettingsService.LoadCodexRoomSession(roomId);
        if (sessionId != null
            && (!Guid.TryParse(sessionId, out _) || FindCodexTranscriptPath(sessionId) == null))
        {
            // 내부 Memory Writing Agent의 SessionStart가 부모 DEVEZCODE_ROOM_ID를 상속하면
            // resume 불가능한 내부 ID가 구버전 훅을 통해 저장될 수 있다. 같은 cwd에서 그 오염
            // 시점에 실제로 열려 있던 미할당 rollout을 찾아 기존 대화를 자동 복구한다.
            var staleSessionId = sessionId;
            sessionId = TryRecoverCodexSessionId(workingDir, staleSessionId);
            if (sessionId != null)
            {
                SettingsService.SaveCodexRoomSession(roomId, sessionId);
                SaveTrackedCodexSessionId(roomId, sessionId);
                DiagLog.Write($"launch[{roomId}]: codex 내부/유령 sid={staleSessionId} → 실제 sid={sessionId} 복구");
            }
            else
            {
                SettingsService.ClearCodexRoomSession(roomId);
                DeleteTrackedCodexSessionFile(roomId);
                DiagLog.Write($"launch[{roomId}]: codex sid={staleSessionId} transcript 없음, 복구 후보 없음 → 새 세션");
            }
        }
        if (sessionId != null)
            SaveTrackedCodexSessionId(roomId, sessionId);
        SettingsService.MarkAgentRoomLaunched(roomId, "codex"); // 추적용

        string options = "--no-alt-screen";
        var selectedModel = SettingsService.LoadAgentRoomModel(roomId, "codex");
        var selectedEffort = SettingsService.LoadAgentRoomEffort(roomId, "codex");
        if (IsSafeFlagValue(selectedModel)) options += $" --model {selectedModel}";
        if (IsSafeFlagValue(selectedEffort)) options += $" -c model_reasoning_effort=\"{selectedEffort}\"";

        // 배치 본문. 저장된 session_id(훅이 기록) 가 있으면 무조건 resume(실패 시 fresh 폴백).
        // launched 플래그에 의존하지 않는다 — 작업 중 강제 종료로 플래그가 유실돼도 session_id 가
        // 살아있으면 이어가야 하기 때문. codex 가 정상 시작하면 뒤 폴백 줄은 실행되지 않음.
        // codex 종료(/exit·Ctrl+D) 시 배치 끝의 exit 로 cmd 를 닫는다 → ConPTY 종료 → onExited 가
        // IsAutoReenterRoom(codex) 을 보고 앱레벨로 같은 세션을 resume 재시작(로딩커버로 스피너 표시).
        // ※ 인-배치 폴백(if errorlevel 1 codex)은 넣지 않는다: codex /exit 가 비정상 종료코드를 반환해
        //   폴백이 오발동하면 cmd 안에서 fresh codex 가 다시 떠 cmd 가 안 닫히고(→ onExited 미발생) 앱
        //   재진입이 막힌다. 재개 실패(세션 삭제 등)는 앱 재진입이 반복하다 AllowAutoRestart 3회 캡에서
        //   'Enter 로 재시작' 프롬프트로 폴백된다.
        string body;
        // DevezCode 는 Codex 를 인라인 TUI 로 렌더한다. 이 플래그를 생략하면 Codex 버전/환경에 따라
        // alternate-screen 으로 재개되어, rollout 문맥에는 남은 마지막 응답이 일반 스크롤백 화면에는
        // 재생되지 않는 경우가 있다. 신규/재개 모두 명시해 대화 표시와 실제 복원 문맥을 일치시킨다.
        if (string.IsNullOrEmpty(sessionId))
            body = $"codex {options}\r\nexit";                           // session_id 없음 — codex 가 새 세션 생성(훅이 저장)
        else
            body = $"codex resume {options} {sessionId}\r\nexit";         // 저장된 session_id 로 resume

        try
        {
            // codex-launch\<room>.cmd (Claude 의 launch 디렉터리와 별도 — codex 전용)
            var dir = CodexLaunchDir();
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            ScriptFile.WriteLaunchCmd(batchPath,
                "@echo off\r\n" +
                $"set \"DEVEZCODE_ROOM_ID={roomId}\"\r\n" +
                TrackingEnvironment.CmdSetLine("codex") +
                body + "\r\n");
            // /c: 배치가 끝나면(codex 반환 시) cmd 가 자동 종료 → ConPTY 종료 → onExited → 앱 재진입.
            // (/k 는 배치의 exit 에 의존하는데, codex 종료 경로에서 exit 에 도달 못 하고 프롬프트가 남는
            //  사례가 있어 /c 로 확실히 닫는다.)
            return $"cmd.exe /c \"{batchPath}\"";
        }
        catch
        {
            injectFallback = body + "\r";
            return null;
        }
    }

    /// <summary>kimi 방 직접 실행. 훅(config.toml [[hooks]]) 설치 후 세션 id 를 해석해
    /// <c>kimi -S &lt;id&gt;</c>(복원) 또는 <c>kimi</c>(신규) 로 실행. 배치 끝 exit + cmd /c →
    /// ConPTY 종료 → IsAutoReenterRoom(kimi) 앱레벨 재진입.
    /// 세션 id 는 3단 폴백: (1) 훅 추적값 (2) settings 저장값 (3) session_index.jsonl 의 workDir 매칭 최신값.
    /// (--work-dir 플래그가 없어 cwd 는 startDir=workingDir 로 ConPTY 가 설정.)</summary>
    private string? TryBuildKimiDirectLaunch(string roomId, string? workingDir, out string? injectFallback)
    {
        injectFallback = null;
        KimiHookInstaller.EnsureInstalled();
        KimiCustomThemes.Apply(DevezCode.App.CurrentTheme);

        var kimiAgent = AgentRegistry.Find("kimi");
        var kimiPath = kimiAgent == null ? null : AgentRegistry.ResolvePath(kimiAgent);
        var kimiCommand = string.IsNullOrWhiteSpace(kimiPath)
            ? "kimi"
            : $"\"{kimiPath.Replace("\"", "\"\"")}\"";

        var sessionId = ResolveKimiSessionId(roomId, workingDir);
        if (sessionId != null)
        {
            SettingsService.SaveKimiRoomSession(roomId, sessionId);
            KimiHookService.RestoreTrackedSessionId(roomId, sessionId);
        }
        SettingsService.MarkAgentRoomLaunched(roomId, "kimi");

        // call + exit: kimi 종료 후 제어가 배치로 돌아와 exit 로 cmd 를 확실히 닫는다 → onExited → 앱 재진입.
        string body = string.IsNullOrEmpty(sessionId)
            ? $"call {kimiCommand}\r\nexit"
            : $"call {kimiCommand} -S {sessionId}\r\nexit";

        try
        {
            var dir = KimiLaunchDir();
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            // set 으로 DEVEZCODE_ROOM_ID 명시(ConPTY env 상속 백업 — 훅이 방을 식별).
            var batch = "@echo off\r\nset \"DEVEZCODE_ROOM_ID=" + roomId + "\"\r\n" +
                        TrackingEnvironment.CmdSetLine("kimi") + body + "\r\n";
            ScriptFile.WriteLaunchCmd(batchPath, batch);
            return $"cmd.exe /c \"{batchPath}\"";
        }
        catch
        {
            injectFallback = body + "\r";
            return null;
        }
    }

    /// <summary>kimi 세션 저장 루트 (~/.kimi-code/sessions).</summary>
    private static string KimiSessionsRoot() => Path.Combine(KimiHookInstaller.KimiHome, "sessions");

    /// <summary>sessionId 의 세션 디렉터리(sessions/&lt;wdKey&gt;/&lt;sessionId&gt;/state.json)가 존재하면 경로 반환.</summary>
    private static string? FindKimiSessionDir(string? sessionId)
    {
        if (!KimiHookService.LooksLikeKimiSessionId(sessionId)) return null;
        try
        {
            var root = KimiSessionsRoot();
            if (!Directory.Exists(root)) return null;
            foreach (var wd in Directory.EnumerateDirectories(root))
            {
                var cand = Path.Combine(wd, sessionId!);
                if (File.Exists(Path.Combine(cand, "state.json"))) return cand;
            }
        }
        catch { }
        return null;
    }

    /// <summary>session_index.jsonl 에서 workDir 매칭 최신(append-only 마지막) 세션 id 를 찾는다(존재 검증 포함).</summary>
    private static string? FindLatestKimiSessionIdForWorkDir(string? workDir)
    {
        if (string.IsNullOrWhiteSpace(workDir)) return null;
        try
        {
            var indexPath = Path.Combine(KimiHookInstaller.KimiHome, "session_index.jsonl");
            if (!File.Exists(indexPath)) return null;
            var want = NormalizeKimiPath(workDir);
            string? latest = null;
            foreach (var line in File.ReadLines(indexPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(line);
                    var wd = node?["workDir"]?.GetValue<string>();
                    var sid = node?["sessionId"]?.GetValue<string>();
                    if (sid == null || wd == null) continue;
                    if (!NormalizeKimiPath(wd).Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
                    if (KimiHookService.LooksLikeKimiSessionId(sid)) latest = sid; // 마지막 매칭이 최신
                }
                catch { }
            }
            return FindKimiSessionDir(latest) != null ? latest : null;
        }
        catch { return null; }
    }

    private static string NormalizeKimiPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

    /// <summary>kimi 세션의 메인 에이전트 wire.jsonl 경로(내보내기용). 없으면 null.</summary>
    public static string? FindKimiWirePath(string? sessionId)
    {
        var dir = FindKimiSessionDir(sessionId);
        if (dir == null) return null;
        var p = Path.Combine(dir, "agents", "main", "wire.jsonl");
        return File.Exists(p) ? p : null;
    }

    /// <summary>방의 kimi 세션 id 를 해석: 훅 추적값 → settings 저장값 → session_index workDir 매칭. 없으면 null(신규).</summary>
    private static string? ResolveKimiSessionId(string roomId, string? workingDir)
    {
        var tracked = KimiHookService.LoadTrackedSessionId(roomId);
        if (FindKimiSessionDir(tracked) != null) return tracked;
        var saved = SettingsService.LoadKimiRoomSession(roomId);
        if (FindKimiSessionDir(saved) != null) return saved;
        return FindLatestKimiSessionIdForWorkDir(workingDir);
    }

    /// <summary>opencode 방에서 추적파일·settings 모두 비었을 때만 cwd 매칭 세션을 조회한다.
    /// 외부 CLI(session list/export) 실행이라 느릴 수 있어 GetOrCreate 가 _lock 밖에서 호출한다.
    /// 대상이 아니거나(비-opencode 방, 이미 추적값 있음) 실패하면 null — 호출부는 새 세션으로 진행.</summary>
    private static string? PrefetchOpenCodeSessionByCwd(string roomId)
    {
        try
        {
            var ccDir = SettingsService.LoadClaudeCodeRoomDir(roomId);
            if (ccDir == null) return null;
            // GetOrCreate 의 에이전트 판별과 동일 규칙(Find 실패 시 기본 에이전트).
            var agent = AgentRegistry.Find(SettingsService.LoadAgentForRoom(roomId)) ?? AgentRegistry.GetDefault();
            if (agent.Id != "opencode") return null;
            if (OpenCodePluginInstaller.LoadTrackedSessionId(roomId) != null) return null;
            if (SettingsService.LoadOpenCodeRoomSession(roomId) != null) return null;
            return OpenCodePluginInstaller.FindSessionIdByCwd(ccDir, roomId);
        }
        catch { return null; }
    }

    /// <summary>opencode 방의 opencode 를 cmd /k 배치로 직접 실행 (Devez 패턴 이식).
    /// 첫 실행은 <c>opencode</c> (시작 디렉터리에서 새 세션), 재진입은 <c>opencode --session &lt;id&gt;</c> 로
    /// 같은 대화 복원. 플러그인(opencode-room-tracker.js) 이 <c>session.created</c>/<c>session.updated</c>
    /// 이벤트에서 session_id 를 %APPDATA%\DevezCode\opencode\sessions\&lt;room&gt;.txt 에 기록.
    /// $env:DEVEZCODE_ROOM_ID 로 어느 방의 opencode 인지 식별 → 같은 폴더의 여러 방이 있어도 완전 분리.
    /// (배치 시작에 <c>set</c> 으로 env 를 명시 — ConPTY 의 env 상속에 의존하지 않음)
    /// <para>세션 ID 는 3단 폴백으로 결정: (1) 플러그인이 기록한 최신 ID (2) settings 의 저장값
    /// (3) <c>opencode session list</c> 에서 workingDir 매칭 ID. 플러그인 콜백이 어떤 이유로
    /// 호출되지 않는 환경에서도 (3) 이 마지막 대화 를 복원한다.</para></summary>
    private string? TryBuildOpenCodeDirectLaunch(string roomId, string? cwdFallbackSessionId, out string? injectFallback)
    {
        injectFallback = null;
        OpenCodePluginInstaller.EnsureInstalled();
        // 이 메서드는 DevezCode가 새 최상위 OpenCode 프로세스를 만들 때만 호출된다.
        // 이전 프로세스 크래시가 남긴 PID 소유권을 비워 새 루트가 원자적으로 다시 claim하게 한다.
        try
        {
            File.Delete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "opencode", "owners", SafeRoomFileName(roomId) + ".txt"));
        }
        catch { }

        var tracked = OpenCodePluginInstaller.LoadTrackedSessionId(roomId);
        var sessionId = SettingsService.LoadOpenCodeRoomSession(roomId);
        if (tracked != null && tracked != sessionId)
        {
            sessionId = tracked;
            SettingsService.SaveOpenCodeRoomSession(roomId, tracked);
        }

        // ── 포크(opencode): 이 방이 다른 세션에서 분기 요청됐고 아직 분기 전이면 --fork 로 원본을 분기.
        //    cwd 폴백(아래)보다 먼저 처리 — 안 그러면 같은 폴더의 다른 세션을 잡아버린다.
        //    claude 와 동일하게, 마커는 첫 실행에 소비하지 않고 "추적값이 원본과 달라질 때(분기 완료)"까지 유지.
        var forkSrc = SettingsService.LoadRoomForkSource(roomId);
        if (forkSrc != null)
        {
            bool diverged = sessionId != null
                && !string.Equals(sessionId, forkSrc, StringComparison.OrdinalIgnoreCase);
            if (diverged)
            {
                SettingsService.RemoveRoomForkSource(roomId); // 분기 완료 → 아래 일반 경로(새 세션 resume)
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(forkSrc, @"^[A-Za-z0-9_.\-]+$"))
            {
                string forkBody = $"@echo off\r\n" +
                                  $"set \"DEVEZCODE_ROOM_ID={roomId}\"\r\n" +
                                  TrackingEnvironment.CmdSetLine("opencode") +
                                  $"set \"OPENCODE_TUI_CONFIG={OpenCodeCustomThemes.TuiConfigPath}\"\r\n" +
                                  $"set \"XDG_STATE_HOME={OpenCodeCustomThemes.XdgStateHomePath}\"\r\n" +
                                  $"call opencode --session {forkSrc} --fork || call opencode\r\n";
                try
                {
                    var fdir = OpenCodeLaunchDir();
                    Directory.CreateDirectory(fdir);
                    var fbatch = Path.Combine(fdir, SafeRoomFileName(roomId) + ".cmd");
                    // 위와 동일 — opencode 는 exit 로 cmd 를 닫고 앱이 새 세션으로 재시작(0xc0000142 회피).
                    ScriptFile.WriteLaunchCmd(fbatch, forkBody + "exit\r\n");
                    return $"cmd.exe /k \"{fbatch}\"";
                }
                catch
                {
                    injectFallback = $"opencode --session {forkSrc} --fork\r";
                    return null;
                }
            }
            else
            {
                SettingsService.RemoveRoomForkSource(roomId); // 비정상 ID → 정리
            }
        }

        // (3) 플러그인도 settings 도 비어있으면 cwd 매칭 ID(프리페치 — GetOrCreate 가 _lock 밖에서
        // 미리 조회해 전달; CLI 실행이라 lock 안에서 돌리면 모든 방이 함께 멈춘다). 플러그인 콜백
        // 미작동·환경변수 누락 등 어떤 이유로든 (1)(2) 가 비어도 같은 폴더의 마지막 대화를 복원.
        if (sessionId == null && cwdFallbackSessionId != null)
        {
            sessionId = cwdFallbackSessionId;
            SettingsService.SaveOpenCodeRoomSession(roomId, cwdFallbackSessionId);
        }

        // body: opencode 실행 라인. 실패 시 fresh 폴백.
        // opencode 는 opencode.cmd(배치) — call 로 불러야 종료 후 제어가 배치(재진입 루프)로 돌아온다.
        string opencodeCmd = sessionId != null
            ? $"call opencode --session {sessionId} || call opencode"
            : "call opencode";

        // 배치: env 명시 set → opencode 실행. cmd 의 env 상속이 불안정해도 set 으로 확실히 전달.
        // roomId 에 공백/특수문자 가능 — set "VAR=value" 형식으로 안전하게.
        string body = $"@echo off\r\n" +
                      $"set \"DEVEZCODE_ROOM_ID={roomId}\"\r\n" +
                      TrackingEnvironment.CmdSetLine("opencode") +
                      $"set \"OPENCODE_TUI_CONFIG={OpenCodeCustomThemes.TuiConfigPath}\"\r\n" +
                      $"set \"XDG_STATE_HOME={OpenCodeCustomThemes.XdgStateHomePath}\"\r\n" +
                      $"{opencodeCmd}\r\n";

        try
        {
            var dir = OpenCodeLaunchDir();
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            // opencode 는 opencode.exe(bun TUI) 를 종료한 ConPTY 에서 in-place 재기동하면 0xc0000142
            // (DLL init 실패)가 난다. 그래서 배치 루프(claude/gjc 방식)를 쓰지 않고, 세션이 끝나면 exit 로
            // cmd 를 닫아 앱(TerminalHostView.onExited)이 "새 ConPTY 세션"으로 같은 세션을 resume 재시작한다.
            ScriptFile.WriteLaunchCmd(batchPath, body + "exit\r\n");
            return $"cmd.exe /k \"{batchPath}\"";
        }
        catch
        {
            injectFallback = (sessionId != null ? $"opencode --session {sessionId}" : "opencode") + "\r";
            return null;
        }
    }

    /// <summary>가재코드(gjc) 방별 세션 디렉터리. gjc 가 여기 안에 &lt;timestamp&gt;_&lt;sessionId&gt;.jsonl 로 세션을 저장.
    /// 방마다 분리해 같은 폴더의 여러 방이 서로의 대화를 침범하지 않게 한다.</summary>
    private static string GajaeSessionDir(string roomId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "gajae", "sessions", SafeRoomFileName(roomId));

    private static string CodexLaunchDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "codex", "launch");

    private static string GrokLaunchDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "grok", "launch");

    private static string KimiLaunchDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "kimi", "launch");

    /// <summary>Grok 방 직접 실행. 훅 설치 후 저장된 session_id 가 있으면 <c>grok -r &lt;id&gt;</c>,
    /// 없으면 <c>grok</c> 신규. 포크 마커가 있으면 <c>-r src --fork-session</c>.
    /// 배치 끝 exit + cmd /c → ConPTY 종료 → IsAutoReenterRoom 앱레벨 재진입.</summary>
    private string? TryBuildGrokDirectLaunch(string roomId, out string? injectFallback)
    {
        injectFallback = null;
        GrokHookInstaller.EnsureInstalled();
        GrokCustomThemes.Apply(DevezCode.App.CurrentTheme);

        var grokAgent = AgentRegistry.Find("grok");
        var grokPath = grokAgent == null ? null : AgentRegistry.ResolvePath(grokAgent);
        var grokCommand = string.IsNullOrWhiteSpace(grokPath)
            ? "grok"
            : $"\"{grokPath.Replace("\"", "\"\"")}\"";

        // 훅 root-session fence가 지킨 현재 ID, settings, 이전 ID 순으로 실제 이 작업폴더에
        // transcript가 있는 후보만 채택. 잘못된 내부/자식 ID는 settings에 넣지 않는다.
        var tracked = GrokHookService.LoadTrackedSessionId(roomId);
        var saved = SettingsService.LoadGrokRoomSession(roomId);
        var previous = GrokHookService.LoadPreviousTrackedSessionId(roomId);
        var workingDir = SettingsService.LoadClaudeCodeRoomDir(roomId);
        var fencedTracked = GrokHookService.IsRootTrackedSession(roomId, tracked) ? tracked : null;
        string? sessionId = null;
        // 구버전 훅이 남긴 un-fenced tracked는 settings보다 뒤에서만 고려한다.
        foreach (var candidate in new[] { fencedTracked, saved, previous, tracked })
        {
            if (FindGrokChatHistoryPathForWorkingDirectory(candidate, workingDir) == null) continue;
            sessionId = candidate;
            break;
        }
        if (sessionId != null)
        {
            if (!string.Equals(saved, sessionId, StringComparison.OrdinalIgnoreCase))
                SettingsService.SaveGrokRoomSession(roomId, sessionId);
            if (!string.Equals(tracked, sessionId, StringComparison.OrdinalIgnoreCase)
                || !GrokHookService.IsRootTrackedSession(roomId, sessionId))
                GrokHookService.RestoreTrackedSessionId(roomId, sessionId);
        }
        else
        {
            SettingsService.RemoveGrokRoomSession(roomId);
            GrokHookService.ResetTrackedSessionIds(roomId);
        }
        SettingsService.MarkAgentRoomLaunched(roomId, "grok");

        var selectedModel = SettingsService.LoadAgentRoomModel(roomId, "grok");
        var selectedEffort = SettingsService.LoadAgentRoomEffort(roomId, "grok");
        if (!IsCachedGrokSelection(selectedModel, selectedEffort))
        {
            SettingsService.SaveAgentRoomModel(roomId, "grok", null);
            SettingsService.SaveAgentRoomEffort(roomId, "grok", null);
            selectedModel = selectedEffort = null;
        }
        string options = "";
        if (IsSafeFlagValue(selectedModel)) options += $" --model {selectedModel}";
        if (IsSafeFlagValue(selectedEffort)) options += $" --reasoning-effort {selectedEffort}";

        // 포크: 첫 실행에 원본 resume + --fork-session. 추적 ID 가 원본과 달라지면 마커 소비.
        var forkSrc = SettingsService.LoadRoomForkSource(roomId);
        string body;
        if (forkSrc != null)
        {
            bool diverged = sessionId != null
                && !string.Equals(sessionId, forkSrc, StringComparison.OrdinalIgnoreCase);
            if (diverged)
            {
                SettingsService.RemoveRoomForkSource(roomId);
                body = $"{grokCommand}{options} -r {sessionId}\r\nexit";
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(forkSrc, @"^[A-Za-z0-9_\-]+$"))
            {
                body = $"{grokCommand}{options} -r {forkSrc} --fork-session\r\nexit";
            }
            else
            {
                body = string.IsNullOrEmpty(sessionId)
                    ? $"{grokCommand}{options}\r\nexit"
                    : $"{grokCommand}{options} -r {sessionId}\r\nexit";
            }
        }
        else if (string.IsNullOrEmpty(sessionId))
            body = $"{grokCommand}{options}\r\nexit";
        else
            body = $"{grokCommand}{options} -r {sessionId}\r\nexit";

        try
        {
            var dir = GrokLaunchDir();
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            // DEVEZCODE_ROOM_ID 를 배치에서도 set — ConPTY env 상속 실패 대비 (훅 room 식별).
            ScriptFile.WriteLaunchCmd(batchPath,
                "@echo off\r\n" +
                $"set \"DEVEZCODE_ROOM_ID={roomId}\"\r\n" +
                TrackingEnvironment.CmdSetLine("grok") +
                body + "\r\n");
            return $"cmd.exe /c \"{batchPath}\"";
        }
        catch
        {
            injectFallback = body + "\r";
            return null;
        }
    }

    private static string DevezVibeLaunchDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "devezvibe", "launch");

    /// <summary>Devez Vibe(dvz) 방 직접 실행. dvz 가 기록한 thread ID 가 있으면 <c>dvz -r &lt;id&gt;</c>,
    /// 없으면 신규. 배치 끝 exit + cmd /c → ConPTY 종료 → IsAutoReenterRoom(devezvibe) 앱레벨 재진입.
    /// <para>세션 실체는 백엔드마다 다른 파일이라 유효성은 <see cref="DevezVibeSessionExists"/> 가
    /// ID 접두사로 판정한다(codex rollout / claude transcript / opencode).
    /// 포크는 rollout 복사본의 새 ID 가 <see cref="SettingsService.SaveDevezVibeRoomSession"/> 에
    /// 들어오는 방식이라 여기서 따로 분기하지 않는다 — 그냥 -r 로 열린다.</para></summary>
    private string? TryBuildDevezVibeDirectLaunch(string roomId, string? workingDir, out string? injectFallback)
    {
        injectFallback = null;
        // 이 메서드는 DevezCode가 새 최상위 dvz 프로세스를 만들 때만 호출된다.
        // 이전 프로세스가 비정상 종료하며 남긴 소유권을 지운 뒤 새 루트가 create_new로 claim한다.
        try
        {
            File.Delete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "devezvibe", "owners", SafeRoomFileName(roomId) + ".txt"));
        }
        catch { }
        // 이전 프로세스의 idle 파일이 남아 있으면 터미널 준비 게이트가 새 프로세스의 복원 상태로
        // 오인해 로딩 커버를 먼저 걷을 수 있다. 새 dvz가 loading/idle을 다시 기록할 때까지 미확정으로 둔다.
        DevezVibeStateService.ClearSessionActivityState(roomId);

        var agent = AgentRegistry.Find("devezvibe");
        var exePath = agent == null ? null : AgentRegistry.ResolvePath(agent);
        var command = string.IsNullOrWhiteSpace(exePath)
            ? "dvz"
            : $"\"{exePath.Replace("\"", "\"\"")}\"";

        // dvz 가 방금 떨군 값 우선, 없으면 settings. 대화 기록이 사라졌으면(세션 삭제) 폐기하고 새 대화.
        var tracked = DevezVibeStateService.LoadTrackedSessionId(roomId);
        var saved = SettingsService.LoadDevezVibeRoomSession(roomId);
        string? sessionId = null;
        foreach (var candidate in new[] { tracked, saved })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            // 추적 ID 가 시작 시 백엔드 이름을 그대로 달고 있는데 대화는 전환된 백엔드에 있는 경우
            // (dvz 가 방 ID 를 안 갈아 준 옛 방) dvz 라우트로 실제 세션을 되찾는다.
            var resolved = ResolveDevezVibeResumeId(candidate, workingDir);
            if (resolved == null) continue;
            sessionId = resolved;
            break;
        }
        if (sessionId != null)
        {
            if (!string.Equals(saved, sessionId, StringComparison.OrdinalIgnoreCase))
                SettingsService.SaveDevezVibeRoomSession(roomId, sessionId);
        }
        else if (!string.IsNullOrWhiteSpace(saved))
        {
            SettingsService.ClearDevezVibeRoomSession(roomId);
            DiagLog.Write($"launch[{roomId}]: dvz thread={saved} rollout 없음 → 새 세션");
        }
        SettingsService.MarkAgentRoomLaunched(roomId, "devezvibe");

        // dvz 는 %APPDATA%\DevezVibe\theme.txt 를 DevezCode\theme.txt 보다 먼저 읽는다. CLI 안에서
        // /theme 을 한 번이라도 쓰면 그 값이 고착되므로, 앱 테마를 인자로 못박아 우선순위를 덮는다.
        var theme = DevezCode.App.CurrentTheme switch
        {
            // dvz 0.1.27부터 DevezCode 6종 테마 키를 그대로 지원한다.
            "minimal" or "soft" or "dark" or "gray" or "softpink" or "midnight"
                => DevezCode.App.CurrentTheme,
            _ => "dark",
        };

        // ConPTY 콘솔의 기본 출력 코드페이지는 949(시스템 ANSI)다. claude/codex 는 node 가 콘솔에
        // WriteConsoleW(유니코드)로 쓰거나 스스로 UTF-8 로 올려서 무관하지만, dvz 의 OpenTUI 백엔드는
        // 렌더 프레임을 원시 UTF-8 바이트로 콘솔 핸들에 직접 쓴다 → 949 로 해석돼 박스문자·기호는 '?',
        // 한글은 바이트 짝이 어긋나 ESC 까지 삼키며 화면 전체가 깨졌다(외부 터미널은 65001 이라 정상).
        // 실측: 같은 프레임이 949 에서 '?' 1083바이트, 65001 에서 45바이트.
        // 이 배치는 내부 ConPTY 전용이다. Process 전역 환경을 잠깐 바꾸면 같은 순간 열리는 외부
        // Windows Terminal이 프로필을 잘못 상속할 수 있으므로, dvz를 실행하는 이 cmd 안에서만 설정한다.
        const string widthProfile = "set \"DEVEZCODE_TERM_WIDTH_PROFILE=xterm6-unicode6-paw2\"\r\n";
        // dvz가 이미 연 세션은 그 세션이 마지막에 쓴 model/effort로 재개한다. 방 콤보 값을 넘기면 명시
        // 인자가 우선해 세션 안 /model 변경이 방을 만들 때 고른 값으로 되돌아간다. 새 세션과, dvz가
        // 아직 모르는 포크 사본에만 콤보(포크 원본의) 값을 넘긴다.
        string selection = "";
        if (string.IsNullOrEmpty(sessionId) || !IsKnownDevezVibeSession(sessionId))
        {
            var selectedModel = SettingsService.LoadAgentRoomModel(roomId, "devezvibe");
            var selectedEffort = SettingsService.LoadAgentRoomEffort(roomId, "devezvibe");
            if (IsSafeFlagValue(selectedModel)) selection += $" --model {selectedModel}";
            if (IsSafeFlagValue(selectedEffort)) selection += $" --effort {selectedEffort}";
        }
        string body = widthProfile + (string.IsNullOrEmpty(sessionId)
            ? $"chcp 65001 >nul\r\ncall {command} --theme {theme}{selection}\r\nexit"
            : $"chcp 65001 >nul\r\ncall {command} --theme {theme}{selection} -r {sessionId}\r\nexit");

        try
        {
            var dir = DevezVibeLaunchDir();
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            // set 으로 DEVEZCODE_ROOM_ID 명시(ConPTY env 상속 백업) — dvz 는 이 값이 있을 때만 상태를 기록한다.
            ScriptFile.WriteLaunchCmd(batchPath,
                "@echo off\r\n" +
                $"set \"DEVEZCODE_ROOM_ID={roomId}\"\r\n" +
                TrackingEnvironment.CmdSetLine("devezvibe") +
                body + "\r\n");
            return $"cmd.exe /c \"{batchPath}\"";
        }
        catch
        {
            injectFallback = body + "\r";
            return null;
        }
    }

    private static string AntigravityLaunchDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "antigravity", "launch");

    /// <summary>agy CLI 데이터 루트. 대화는 conversations\&lt;id&gt;.db(SQLite), cwd→conversation 매핑은
    /// cache\last_conversations.json 에 저장된다.</summary>
    internal static string AntigravityCliDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini", "antigravity-cli");

    /// <summary>안티그래비티(agy) 방 직접 실행. 훅이 기록한 conversation_id 가 있으면
    /// <c>agy --conversation &lt;id&gt;</c>, 없으면 agy 자체 cwd→conversation 매핑
    /// (~/.gemini/antigravity-cli/cache/last_conversations.json)으로 폴백 추종.
    /// conversation db(.db)가 실제로 있을 때만 resume — 없으면(빈/유실 ID) 폐기 후 새 대화
    /// (claude 의 빈 세션 탈출과 동일, 잘못된 ID 고착으로 매번 빈 화면이 되는 것 방지).
    /// resume 실패(외부 삭제 등) 시 errorlevel 폴백으로 fresh 기동.
    /// 배치 끝 exit + cmd /c → ConPTY 종료 → IsAutoReenterRoom 앱레벨 재진입.
    /// <para>주의: agy 자체 매핑은 작업폴더당 conversation 1개라 같은 폴더 다중 방이 섞일 수 있는데,
    /// 훅(DEVEZCODE_ROOM_ID 기반 sessions\&lt;room&gt;.txt)이 방별로 추적을 분리해 준다.</para></summary>
    private string? TryBuildAntigravityDirectLaunch(string roomId, out string? injectFallback)
    {
        injectFallback = null;
        AntigravityHookInstaller.EnsureInstalled();
        AntigravityCustomThemes.Apply(DevezCode.App.CurrentTheme);

        var agyAgent = AgentRegistry.Find("antigravity");
        var agyPath = agyAgent == null ? null : AgentRegistry.ResolvePath(agyAgent);
        var agyCommand = string.IsNullOrWhiteSpace(agyPath)
            ? "agy"
            : $"\"{agyPath.Replace("\"", "\"\"")}\"";

        const string flags = "--dangerously-skip-permissions";

        // 훅 root-session fence가 지킨 현재 ID를 우선하되, 실제 db가 없는 후보는 settings에
        // 반영하지 않는다. current가 손상됐으면 settings/previous에서 정상 대화를 복구한다.
        var tracked = AntigravityHookService.LoadTrackedSessionId(roomId);
        var saved = SettingsService.LoadAntigravityRoomSession(roomId);
        var previous = AntigravityHookService.LoadPreviousTrackedSessionId(roomId);
        var fencedTracked = AntigravityHookService.IsRootTrackedSession(roomId, tracked) ? tracked : null;
        string? sessionId = null;
        foreach (var candidate in new[] { fencedTracked, saved, previous, tracked })
        {
            if (!AntigravityConversationExists(candidate)) continue;
            sessionId = candidate;
            break;
        }
        if (sessionId != null)
        {
            if (!string.Equals(saved, sessionId, StringComparison.OrdinalIgnoreCase))
                SettingsService.SaveAntigravityRoomSession(roomId, sessionId);
            if (!string.Equals(tracked, sessionId, StringComparison.OrdinalIgnoreCase)
                || !AntigravityHookService.IsRootTrackedSession(roomId, sessionId))
                AntigravityHookService.RestoreTrackedSessionId(roomId, sessionId);
        }

        // 훅 미기록 방(첫 도입/훅 실패)은 agy 자체 cwd→conversation 매핑으로 폴백 추종.
        var ccDir = SettingsService.LoadClaudeCodeRoomDir(roomId);
        if (sessionId == null)
        {
            var byCwd = FindAntigravityConversationByCwd(ccDir);
            if (byCwd != null)
            {
                sessionId = byCwd;
                SettingsService.SaveAntigravityRoomSession(roomId, byCwd);
                AntigravityHookService.RestoreTrackedSessionId(roomId, byCwd);
            }
        }

        // 검증 가능한 후보가 하나도 없으면 stale 추적값을 모두 비우고 새 대화.
        if (sessionId == null)
        {
            SettingsService.RemoveAntigravityRoomSession(roomId);
            AntigravityHookService.ResetTrackedSessionIds(roomId);
        }

        SettingsService.MarkAgentRoomLaunched(roomId, "antigravity");

        string body = sessionId != null
            ? $"{agyCommand} --conversation {sessionId} {flags}\r\nif errorlevel 1 {agyCommand} {flags}\r\nexit"
            : $"{agyCommand} {flags}\r\nexit";

        try
        {
            var dir = AntigravityLaunchDir();
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            // DEVEZCODE_ROOM_ID 를 배치에서도 set — ConPTY env 상속 실패 대비 (훅 room 식별).
            ScriptFile.WriteLaunchCmd(batchPath,
                "@echo off\r\n" +
                $"set \"DEVEZCODE_ROOM_ID={roomId}\"\r\n" +
                TrackingEnvironment.CmdSetLine("antigravity") +
                body + "\r\n");
            return $"cmd.exe /c \"{batchPath}\"";
        }
        catch
        {
            injectFallback = (sessionId != null
                ? $"agy --conversation {sessionId} {flags}"
                : $"agy {flags}") + "\r";
            return null;
        }
    }

    /// <summary>agy 의 cwd→conversation 매핑(last_conversations.json)에서 작업폴더에 해당하는
    /// conversation ID. 없거나 GUID 가 아니면 null.</summary>
    private static string? FindAntigravityConversationByCwd(string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(workingDir)) return null;
        try
        {
            var path = Path.Combine(AntigravityCliDir(), "cache", "last_conversations.json");
            if (!File.Exists(path)) return null;
            var norm = Path.GetFullPath(workingDir).TrimEnd('\\', '/');
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                try
                {
                    if (!string.Equals(Path.GetFullPath(prop.Name).TrimEnd('\\', '/'), norm, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var id = prop.Value.GetString();
                    return id != null && Guid.TryParse(id, out _) ? id : null;
                }
                catch (Exception) { /* 비정상 키 무시하고 다음 후보 */ }
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>agy conversation transcript(conversations\&lt;id&gt;.db)가 실제 디스크에 있는지.</summary>
    internal static bool AntigravityConversationExists(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return false;
        try { return File.Exists(Path.Combine(AntigravityCliDir(), "conversations", conversationId + ".db")); }
        catch (Exception) { return false; }
    }

    private static string OpenCodeLaunchDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "opencode", "launch");

    private static string GajaeLaunchDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "gajae", "launch");

    private static string GajaeQuitFlagPath(string roomId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "gajae", "quitting", SafeRoomFileName(roomId) + ".txt");

    private static void MarkGajaeQuitting(string roomId)
    {
        try
        {
            var path = GajaeQuitFlagPath(roomId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "1");
        }
        catch { /* best effort — 실패해도 timeout 뒤 Job 하드 정리가 종료를 보장 */ }
    }

    private static void ClearGajaeQuitFlag(string roomId)
    {
        try { File.Delete(GajaeQuitFlagPath(roomId)); } catch { }
    }

    /// <summary>방의 session-dir 에서 가장 최근 세션 .jsonl 의 ID(파일명 끝 UUID)를 추출. 없으면 null.
    /// gjc 파일명: <c>2026-06-24T06-43-03-266Z_019ef85e-31e2-7000-9b2a-e205d434126f.jsonl</c>
    /// → 마지막 '_' 뒤가 세션 ID. /clear·새 대화로 ID 가 바뀌어도 항상 최신을 집어 추종한다.</summary>
    private static string? FindLatestGajaeSessionId(string sessionDir)
    {
        try
        {
            if (!Directory.Exists(sessionDir)) return null;
            var newest = new DirectoryInfo(sessionDir)
                .GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return null;
            var name = Path.GetFileNameWithoutExtension(newest.Name);
            var us = name.LastIndexOf('_');
            if (us < 0 || us + 1 >= name.Length) return null;
            var id = name.Substring(us + 1);
            return Guid.TryParse(id, out _) ? id : null;
        }
        catch { return null; }
    }

    /// <summary>가재코드(gjc) 방의 최신 세션 jsonl 전체 경로(없으면 null). 세션 내보내기용.</summary>
    public static string? FindLatestGajaeTranscriptPath(string roomId)
    {
        try
        {
            var dir = GajaeSessionDir(roomId);
            if (!Directory.Exists(dir)) return null;
            return new DirectoryInfo(dir).GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>codex 세션 rollout jsonl 을 session_id 로 찾는다. 파일명에 id 가 들어가 있어(rollout-&lt;ts&gt;-&lt;id&gt;.jsonl)
    /// ~/.codex/sessions 하위를 재귀 검색. 없으면 null.</summary>
    public static string? FindCodexTranscriptPath(string? sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            if (!Directory.Exists(root)) return null;
            return new DirectoryInfo(root).GetFiles("*" + sessionId + "*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>dvz 자신의 라우트 저장소. 방의 대화가 시작 시 이름 붙은 백엔드를 떠나면
    /// (예: Claude 로 열렸다가 Codex 로 전환) 화면·추적 ID 는 <c>claude:UUID</c> 로 남고 실제 대화는
    /// codex rollout 에 쌓인다. 그 대응은 이 파일에만 있다.</summary>
    private static string DevezVibeRouteStorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezVibe", "session-routes.json");

    /// <summary>dvz 추적 ID 를 실제 대화가 있는 세션 ID 로 바로잡는다. 라우트에 적힌 활성 백엔드
    /// 세션을 먼저 고르고, 라우트가 없을 때만 추적 ID 자체를 쓴다.
    /// <para>추적 ID 를 먼저 채택하면 대화 도중 provider 를 바꾼 방이 방을 만든 백엔드의 옛 세션으로
    /// 되돌아간다 — 그 세션 파일도 디스크에 그대로 남아 있어 유효 판정을 통과하기 때문이다.</para>
    /// 못 찾으면 null — 그때만 새 대화로 연다.</summary>
    public static string? ResolveDevezVibeResumeId(string? sessionId, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        foreach (var candidate in DevezVibeRoutedSessionIds(sessionId!))
        {
            if (DevezVibeSessionExists(candidate, workingDir)) return candidate;
        }
        if (DevezVibeSessionExists(sessionId, workingDir)) return sessionId;
        return null;
    }

    /// <summary>dvz 라우트 저장소에서 이 방이 거친 백엔드 세션 ID 들을 활성 백엔드 우선으로 나열한다.
    /// 추적 ID 는 방의 visible ID 일 수도(방 이름), 전환 뒤 기록된 백엔드 세션 ID 일 수도 있어
    /// 키 조회가 빗나가면 백엔드 ID 로 역조회한다. 파일이 없거나 항목이 없으면 빈 목록.</summary>
    private static IEnumerable<string> DevezVibeRoutedSessionIds(string trackedId)
    {
        var ids = new List<string>();
        try
        {
            var path = DevezVibeRouteStorePath();
            if (!File.Exists(path)) return ids;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty(trackedId, out var route)
                && !TryFindDevezVibeRouteByBackingId(doc.RootElement, trackedId, out route))
                return ids;

            string? Backing(string name)
                => route.TryGetProperty(name, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? value.GetString() : null;

            var claude = Backing("claude_id") is { Length: > 0 } c ? "claude:" + c : null;
            var codex = Backing("codex_id");
            var openCode = Backing("open_code_id");
            var active = route.TryGetProperty("active", out var kind) ? kind.GetString() : null;
            // 활성 백엔드부터 — 마지막으로 대화한 쪽이 이어갈 세션이다.
            foreach (var id in active switch
            {
                "Codex" => new[] { codex, claude, openCode },
                "OpenCode" => new[] { openCode, claude, codex },
                _ => new[] { claude, codex, openCode },
            })
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!);
        }
        catch { }
        return ids;
    }

    /// <summary>dvz 라우트 저장소에 이 세션이 있으면 dvz가 마지막 model/effort를 기억하고 있다.</summary>
    private static bool IsKnownDevezVibeSession(string sessionId)
    {
        try
        {
            var path = DevezVibeRouteStorePath();
            if (!File.Exists(path)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(sessionId, out _)
                || TryFindDevezVibeRouteByBackingId(doc.RootElement, sessionId, out _);
        }
        catch { return false; }
    }

    /// <summary>DevezCode 포크가 dvz 원본의 현재 제공자 모델·effort를 새 방에 전달할 때 사용한다.</summary>
    public static (string? Model, string? Effort) ReadDevezVibeSessionModelEffort(
        string? trackedId, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(trackedId)) return (null, null);
        string? routedModel = null;
        string? routedEffort = null;
        try
        {
            var path = DevezVibeRouteStorePath();
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty(trackedId!, out var route)
                    || TryFindDevezVibeRouteByBackingId(doc.RootElement, trackedId!, out route))
                {
                    routedModel = route.TryGetProperty("claude_model", out var modelNode)
                        ? modelNode.GetString() : null;
                    routedEffort = route.TryGetProperty("claude_effort", out var effortNode)
                        ? effortNode.GetString() : null;
                }
            }
        }
        catch { }

        var activeId = ResolveDevezVibeResumeId(trackedId, workingDir) ?? trackedId;
        if (activeId.StartsWith("claude:", StringComparison.Ordinal))
        {
            var rawId = DevezVibeStateService.StripBackendPrefix(activeId);
            var transcript = ClaudeTranscriptSnapshotParser.ParseFile(
                FindClaudeTranscriptPath(workingDir, rawId));
            return (ModelEffortService.ToModelValue(routedModel ?? transcript.Model),
                routedEffort ?? transcript.Effort);
        }

        var codexPath = FindCodexTranscriptPath(activeId);
        return codexPath == null ? (null, null) : CodexTranscriptMetadata.ReadLatest(codexPath);
    }

    /// <summary>백엔드 세션 ID 로 그 세션을 품은 라우트를 찾는다. provider 를 바꾼 뒤 기록된 추적 ID 는
    /// 방 이름이 아니라 그때 활성이던 백엔드의 세션 ID 라서, 키 조회만으로는 라우트에 닿지 못한다.</summary>
    private static bool TryFindDevezVibeRouteByBackingId(
        System.Text.Json.JsonElement root, string trackedId, out System.Text.Json.JsonElement found)
    {
        var raw = DevezVibeStateService.StripBackendPrefix(trackedId);
        var matched = false;
        System.Text.Json.JsonElement candidate = default;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            foreach (var name in new[] { "claude_id", "codex_id", "open_code_id" })
            {
                if (entry.Value.TryGetProperty(name, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String
                    && string.Equals(value.GetString(), raw, StringComparison.OrdinalIgnoreCase))
                {
                    if (matched)
                    {
                        // The same native id belongs to more than one visible
                        // room. Choosing the first JSON object would resume an
                        // arbitrary conversation, so let the dvz id itself win.
                        found = default;
                        return false;
                    }
                    candidate = entry.Value;
                    matched = true;
                    break;
                }
            }
        }
        found = candidate;
        return matched;
    }

    /// <summary>dvz 세션의 실체가 디스크에 있는지 확인한다. dvz 는 한 방 안에서 Codex thread(UUID)·
    /// Claude 세션(<c>claude:UUID</c>)·OpenCode 세션(<c>ses_…</c>)을 오가므로 ID 접두사로 어느
    /// 저장소를 볼지 고른다. codex rollout 만 보면 Claude 로 대화한 방은 항상 폐기된다.</summary>
    public static bool DevezVibeSessionExists(string? sessionId, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (sessionId!.StartsWith("claude:", StringComparison.Ordinal))
            return FindClaudeTranscriptPath(
                workingDir, DevezVibeStateService.StripBackendPrefix(sessionId)) != null;
        // OpenCode 세션 저장소는 CLI 내부라 여기서 볼 수 없다 — 유효성 판정은 dvz 에 맡긴다.
        if (sessionId.StartsWith("ses_", StringComparison.Ordinal)) return true;
        return FindCodexTranscriptPath(sessionId) != null;
    }

    private sealed record CodexTranscriptCandidate(
        string SessionId, string WorkingDir, DateTimeOffset? CreatedAt, DateTime LastWriteTimeUtc);

    /// <summary>resume 불가능한 내부/유령 ID로 오염된 방에서 실제 부모 Codex rollout을 복구한다.
    /// 같은 cwd의 미할당 transcript 중 오염 UUIDv7 생성 시점에 실제로 열려 있던 후보만 사용한다.
    /// 시점 판정이 불가능하면 후보가 정확히 하나일 때만 복구해 다른 방/외부 세션 혼입을 막는다.</summary>
    private static string? TryRecoverCodexSessionId(string? workingDir, string? staleSessionId)
    {
        if (string.IsNullOrWhiteSpace(workingDir) || string.IsNullOrWhiteSpace(staleSessionId)) return null;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            if (!Directory.Exists(root)) return null;
            var normalizedWorkingDir = Path.GetFullPath(workingDir).TrimEnd('\\', '/');
            var claimed = SettingsService.LoadManagedSessionSnapshot().Codex
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            claimed.Remove(staleSessionId);

            var candidates = new List<CodexTranscriptCandidate>();
            foreach (var file in new DirectoryInfo(root).GetFiles("*.jsonl", SearchOption.AllDirectories))
            {
                var candidate = TryReadCodexTranscriptCandidate(file);
                if (candidate == null || claimed.Contains(candidate.SessionId)) continue;
                string candidateDir;
                try { candidateDir = Path.GetFullPath(candidate.WorkingDir).TrimEnd('\\', '/'); }
                catch { continue; }
                if (string.Equals(candidateDir, normalizedWorkingDir, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(candidate);
            }
            if (candidates.Count == 0) return null;

            var staleCreatedAt = TryGetUuidV7Timestamp(staleSessionId);
            if (staleCreatedAt != null)
            {
                var margin = TimeSpan.FromMinutes(1);
                var activeAtCorruption = candidates
                    .Where(c => c.CreatedAt == null || c.CreatedAt <= staleCreatedAt.Value + margin)
                    .Where(c => c.LastWriteTimeUtc >= staleCreatedAt.Value.UtcDateTime - margin)
                    .OrderByDescending(c => c.CreatedAt ?? DateTimeOffset.MinValue)
                    .ToList();
                if (activeAtCorruption.Count > 0) return activeAtCorruption[0].SessionId;
            }

            return candidates.Count == 1 ? candidates[0].SessionId : null;
        }
        catch { return null; }
    }

    private static CodexTranscriptCandidate? TryReadCodexTranscriptCandidate(FileInfo file)
    {
        try
        {
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var firstLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(firstLine)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(firstLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "session_meta"
                || !root.TryGetProperty("payload", out var payload)) return null;
            string? id = null;
            if (payload.TryGetProperty("id", out var idElement)) id = idElement.GetString();
            if (id == null && payload.TryGetProperty("session_id", out var sidElement)) id = sidElement.GetString();
            if (id == null || !Guid.TryParse(id, out _)
                || !payload.TryGetProperty("cwd", out var cwdElement)
                || string.IsNullOrWhiteSpace(cwdElement.GetString())) return null;
            DateTimeOffset? createdAt = null;
            if (payload.TryGetProperty("timestamp", out var timestampElement)
                && DateTimeOffset.TryParse(timestampElement.GetString(), out var parsedTimestamp))
                createdAt = parsedTimestamp;
            return new CodexTranscriptCandidate(id, cwdElement.GetString()!, createdAt, file.LastWriteTimeUtc);
        }
        catch { return null; }
    }

    private static DateTimeOffset? TryGetUuidV7Timestamp(string sessionId)
    {
        try
        {
            if (sessionId.Length != 36 || sessionId[14] != '7') return null;
            var hex = sessionId[..8] + sessionId.Substring(9, 4);
            var unixMs = Convert.ToInt64(hex, 16);
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }
        catch { return null; }
    }

    private static string CodexTrackedSessionPath(string roomId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "codex", "sessions", SafeRoomFileName(roomId) + ".txt");

    private static void SaveTrackedCodexSessionId(string roomId, string sessionId)
    {
        CodexHookService.RestoreTrackedSessionId(roomId, sessionId);
    }

    private static void DeleteTrackedCodexSessionFile(string roomId)
    {
        try { File.Delete(CodexTrackedSessionPath(roomId)); } catch { }
        try
        {
            File.Delete(Path.Combine(
                Path.GetDirectoryName(CodexTrackedSessionPath(roomId))!,
                SafeRoomFileName(roomId) + ".root.txt"));
        }
        catch { }
    }

    /// <summary>grok 세션 chat_history.jsonl.
    /// 경로: ~/.grok/sessions/&lt;url-encoded-cwd&gt;/&lt;sessionId&gt;/chat_history.jsonl.
    /// cwd 인코딩이 달라도 sessionId 폴더명으로 재귀 검색.</summary>
    public static string? FindGrokChatHistoryPath(string? sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId) || !Guid.TryParse(sessionId, out _)) return null;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "sessions");
            if (!Directory.Exists(root)) return null;
            // 직접 경로 우선: */<sessionId>/chat_history.jsonl
            foreach (var cwdDir in Directory.EnumerateDirectories(root))
            {
                var p = Path.Combine(cwdDir, sessionId, "chat_history.jsonl");
                if (File.Exists(p)) return p;
            }
            // 폴백 재귀
            return new DirectoryInfo(root).GetFiles("chat_history.jsonl", SearchOption.AllDirectories)
                .Where(f => string.Equals(f.Directory?.Name, sessionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>Grok 세션이 이 방의 작업폴더에 실제로 속하는지까지 확인한다.
    /// 내부/별도 Grok 프로세스가 DEVEZCODE_ROOM_ID를 상속해 다른 cwd의 ID를 기록하는 경우 차단.</summary>
    public static string? FindGrokChatHistoryPathForWorkingDirectory(string? sessionId, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(workingDir)
            || string.IsNullOrWhiteSpace(sessionId)
            || !Guid.TryParse(sessionId, out _)) return null;
        try
        {
            var expected = Path.GetFullPath(workingDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "sessions");
            if (!Directory.Exists(root)) return null;

            // UUID가 다른 cwd 아래 중복될 수 있으므로 첫 전역 검색 결과를 믿지 않고 모든 후보의
            // summary.info.cwd를 비교한다.
            foreach (var cwdDir in Directory.EnumerateDirectories(root))
            {
                var historyPath = Path.Combine(cwdDir, sessionId, "chat_history.jsonl");
                if (File.Exists(historyPath) && GrokHistoryMatchesWorkingDirectory(historyPath, expected))
                    return historyPath;
            }
            foreach (var file in new DirectoryInfo(root).GetFiles("chat_history.jsonl", SearchOption.AllDirectories)
                .Where(f => string.Equals(f.Directory?.Name, sessionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                if (GrokHistoryMatchesWorkingDirectory(file.FullName, expected)) return file.FullName;
            }
        }
        catch { return null; }
        return null;
    }

    private static bool GrokHistoryMatchesWorkingDirectory(string historyPath, string expectedWorkingDir)
    {
        try
        {
            var summaryPath = Path.Combine(Path.GetDirectoryName(historyPath)!, "summary.json");
            if (!File.Exists(summaryPath)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(summaryPath));
            if (!doc.RootElement.TryGetProperty("info", out var info)
                || !info.TryGetProperty("cwd", out var cwdElement)) return false;
            var sessionCwd = cwdElement.GetString();
            if (string.IsNullOrWhiteSpace(sessionCwd)) return false;
            var actual = Path.GetFullPath(sessionCwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(expectedWorkingDir, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>현재 방을 종료한 뒤 동일 대화로 다시 들어갈 근거가 디스크에 있는지 확인한다.
    /// 자동 유휴 종료는 이 검사를 통과한 방에만 적용한다. Antigravity는 권한 대기 상태를
    /// 별도로 관측할 수 없어(false-idle 위험) 사용자가 직접 숨길 때만 종료한다.</summary>
    public static bool CanSafelyResumeRoom(string roomId)
    {
        try
        {
            TrySnapshotRoomSession(roomId);
            var agent = AgentRegistry.Find(SettingsService.LoadAgentForRoom(roomId)) ?? AgentRegistry.GetDefault();
            return agent.Id switch
            {
                "claude" => ClaudeTranscriptExists(
                    SettingsService.LoadClaudeCodeRoomDir(roomId),
                    SettingsService.LoadClaudeCodeRoomSession(roomId)),
                "codex" => FindCodexTranscriptPath(SettingsService.LoadCodexRoomSession(roomId)) != null,
                "opencode" => OpenCodePluginInstaller.LoadTrackedSessionId(roomId) is { Length: > 0 } openCodeId
                    && string.Equals(openCodeId, SettingsService.LoadOpenCodeRoomSession(roomId), StringComparison.Ordinal),
                "gajae" => FindLatestGajaeTranscriptPath(roomId) != null
                    && !string.IsNullOrWhiteSpace(SettingsService.LoadGajaeRoomSession(roomId)),
                "grok" => FindGrokChatHistoryPathForWorkingDirectory(
                    SettingsService.LoadGrokRoomSession(roomId),
                    SettingsService.LoadClaudeCodeRoomDir(roomId)) != null,
                "kimi" => ResolveKimiSessionId(roomId, SettingsService.LoadClaudeCodeRoomDir(roomId)) != null,
                "devezvibe" => ResolveDevezVibeResumeId(
                    SettingsService.LoadDevezVibeRoomSession(roomId),
                    SettingsService.LoadClaudeCodeRoomDir(roomId)) != null,
                _ => false,
            };
        }
        catch { return false; }
    }

    /// <summary>가재코드(gjc) 세션 포크 — 네이티브 fork 가 없어, 원본 방의 최신 세션 jsonl 을 새 GUID 로
    /// (내부 id 참조 전역 치환) 복사해 새 방의 session-dir 에 심는다. 새 세션 id 반환(원본에 대화 없으면 null).
    /// gjc 는 --session-dir 로 격리되므로 새 방은 이 복사본만 resume → 원본과 완전 독립.</summary>
    public static string? TryForkGajaeSession(string sourceRoomId, string newRoomId)
    {
        try
        {
            var srcDir = GajaeSessionDir(sourceRoomId);
            if (!Directory.Exists(srcDir)) return null;
            var srcFile = new DirectoryInfo(srcDir)
                .GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (srcFile == null) return null;

            // 원본 파일명(<ts>_<id>.jsonl)에서 옛 세션 id 추출.
            var srcName = Path.GetFileNameWithoutExtension(srcFile.Name);
            var us = srcName.LastIndexOf('_');
            if (us < 0 || us + 1 >= srcName.Length) return null;
            var oldId = srcName.Substring(us + 1);
            if (!Guid.TryParse(oldId, out _)) return null;

            // 새 id 로 복사 + 내부 id 참조(세션 헤더 "id"/메시지 sessionID 등) 전역 치환.
            // FileShare.ReadWrite 로 읽는다 — 대화 중인 세션이면 gjc 가 이 jsonl 을 열어두고 있어
            // File.ReadAllText(제한 공유)는 "다른 프로세스가 사용 중" 예외로 실패한다(=활성 세션 포크 불가).
            var newId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            string content;
            using (var fs = new FileStream(srcFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                content = sr.ReadToEnd();
            content = content.Replace(oldId, newId);

            var newDir = GajaeSessionDir(newRoomId);
            Directory.CreateDirectory(newDir);
            var ts = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'");
            File.WriteAllText(Path.Combine(newDir, ts + "_" + newId + ".jsonl"), content);
            return newId;
        }
        catch { return null; }
    }

    /// <summary>codex 세션 포크 — 네이티브 fork 가 없어, 원본 세션 rollout jsonl 을 새 GUID 로(내부 id 참조
    /// 전역 치환) 복사해 codex sessions 폴더에 심는다. 새 방은 이 새 id 로 resume → 원본과 독립.
    /// 새 세션 id 반환(원본 파일 없거나 실패면 null → 호출부가 포크 취소). best-effort: codex 가 v4 GUID
    /// resume 을 거부해도 배치의 fresh 폴백으로 무해.</summary>
    public static string? TryForkCodexSession(string sourceSessionId)
    {
        try
        {
            var srcPath = FindCodexTranscriptPath(sourceSessionId);
            if (srcPath == null || !File.Exists(srcPath)) return null;
            var newId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            string content;
            using (var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                content = sr.ReadToEnd();
            content = content.Replace(sourceSessionId, newId); // 헤더 payload.id 등 전역 치환

            // codex 규약 파일명: rollout-<ISO(대시)>-<id>.jsonl, 날짜별 폴더.
            var now = DateTime.Now;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "sessions", now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
            Directory.CreateDirectory(dir);
            var ts = now.ToString("yyyy-MM-dd'T'HH-mm-ss");
            File.WriteAllText(Path.Combine(dir, $"rollout-{ts}-{newId}.jsonl"), content, new System.Text.UTF8Encoding(false));
            return newId;
        }
        catch { return null; }
    }

    /// <summary>kimi 세션 포크 — 네이티브 launch 포크가 없어(/fork 는 TUI 전용), 원본 세션 디렉터리
    /// (state.json + agents/*/wire.jsonl + tasks/cron 등)를 새 session_&lt;uuid&gt; 로 통째 복사하고
    /// .json/.jsonl 내부의 old id 참조를 new id 로 치환 + state.json 에 forkedFrom 기록 +
    /// session_index.jsonl 에 새 항목 append. 새 방은 `kimi -S <newId>` 로 복원 → 원본과 독립.
    /// 새 세션 id 반환(원본 없거나 실패면 null → 호출부가 포크 취소).</summary>
    public static string? TryForkKimiSession(string sourceSessionId)
    {
        try
        {
            var srcDir = FindKimiSessionDir(sourceSessionId);
            if (srcDir == null) return null;
            var wdDir = Path.GetDirectoryName(srcDir)!;                 // sessions/<wdKey>
            var newId = "session_" + Guid.NewGuid().ToString("D").ToLowerInvariant();
            var dstDir = Path.Combine(wdDir, newId);
            CopyKimiSessionDir(srcDir, dstDir, sourceSessionId, newId);

            // state.json: forkedFrom 기록 (homedir 등 old id 경로는 CopyKimiSessionDir 텍스트 치환으로 이미 반영).
            var statePath = Path.Combine(dstDir, "state.json");
            string? workDir = null;
            if (File.Exists(statePath))
            {
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath)) is System.Text.Json.Nodes.JsonObject node)
                    {
                        node["forkedFrom"] = sourceSessionId;
                        workDir = node["workDir"]?.GetValue<string>();
                        File.WriteAllText(statePath,
                            node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                            new System.Text.UTF8Encoding(false));
                    }
                }
                catch { }
            }

            // session_index.jsonl 에 새 항목 append (kimi -S 가 전역 조회하는 인덱스).
            try
            {
                var indexPath = Path.Combine(KimiHookInstaller.KimiHome, "session_index.jsonl");
                var entry = new System.Text.Json.Nodes.JsonObject
                {
                    ["sessionId"] = newId,
                    ["sessionDir"] = dstDir.Replace('\\', '/'),
                    ["workDir"] = (workDir ?? "").Replace('\\', '/'),
                };
                File.AppendAllText(indexPath, entry.ToJsonString() + "\n");
            }
            catch { }

            return newId;
        }
        catch { return null; }
    }

    /// <summary>kimi 세션 디렉터리 재귀 복사. .json/.jsonl 은 old→new 세션 id 치환, 그 외(blob)는 그대로 복사.
    /// 활성 세션도 포크 가능하도록 FileShare.ReadWrite 로 읽는다.</summary>
    private static void CopyKimiSessionDir(string srcDir, string dstDir, string oldId, string newId)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, file);
            var dst = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".json" || ext == ".jsonl")
            {
                string content;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                    content = sr.ReadToEnd();
                content = content.Replace(oldId, newId, StringComparison.Ordinal);
                File.WriteAllText(dst, content, new System.Text.UTF8Encoding(false));
            }
            else
            {
                using var srcFs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dstFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
                srcFs.CopyTo(dstFs);
            }
        }
    }

    // antigravity(agy) 포크는 미지원 — db 복사 실측(2026-07-13) 결과 `--conversation <복사본>` 이
    // "trajectory not found" 로 거부됨. 대화 db 내부(trajectory_meta.cascade_id + protobuf 블롭)에
    // 원본 id 가 박혀 있고 서버측 trajectory 등록도 필요해 로컬 조작만으로는 분기 불가.
    // agy TUI 내 /fork 명령만 유효 (WorkspacePaneView.ForkSession 이 안내).

    /// <summary>claude 세션 포크 — CLI 의 --fork-session(지연 분기) 대신 원본 transcript(.jsonl)를
    /// 새 GUID 로 즉시 복사(내부 sessionId 참조 치환)해 독립 세션을 만든다. 새 세션 id 반환(없으면 null).
    /// (--fork-session 은 메시지를 보내기 전까지 원본 id 를 추적 → 포크 방을 재실행하면 원본에서 매번
    ///  다시 포크되어 포크가 자기 대화로 고정되지 않았다. 같은 프로젝트 폴더에 심어 cwd 인코딩을 맞춘다.)</summary>
    public static string? TryForkClaudeSession(string sourceSessionId, string? workingDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceSessionId) || !Guid.TryParse(sourceSessionId, out _)) return null;
            var srcPath = FindClaudeTranscriptPath(workingDir, sourceSessionId);
            if (srcPath == null) return null;

            var oldId = Path.GetFileNameWithoutExtension(srcPath); // 파일명 실제 케이스로 치환해야 정확히 맞음
            var newId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            // FileShare.ReadWrite — 대화 중인 세션도 claude 가 jsonl 을 열어둔 채라 제한 공유면 읽기 실패한다.
            string content;
            using (var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                content = sr.ReadToEnd();
            // frame-link는 현재 열린 아티팩트 패널의 UI 상태다. 포크 transcript에 남기면
            // 부모에서 X로 닫았어도 새 세션 복원 시 패널이 다시 열린다.
            var forked = ClaudeTranscriptSnapshotParser.CloneForFork(content, oldId, newId);

            // 복사본은 반드시 "fork 방이 실행될 cwd(workingDir)의 인코딩 폴더"에 심어야 한다 — claude --resume 은
            // 그 폴더에서만 <id>.jsonl 을 찾는다. srcPath 는 전역 폴백 스캔으로 다른 프로젝트 폴더에서 왔을 수
            // 있어(그 경우 소스 폴더에 두면 resume 이 못 찾아 빈 세션이 됨) srcPath 폴더를 그대로 쓰면 안 된다.
            var destDir = ClaudeProjectDir(workingDir) ?? Path.GetDirectoryName(srcPath)!;
            Directory.CreateDirectory(destDir);
            var newPath = Path.Combine(destDir, newId + ".jsonl");
            File.WriteAllText(newPath, forked);
            return newId;
        }
        catch { return null; }
    }

    /// <summary>가재코드(gjc)를 cmd /k 배치로 직접 실행. 방별 --session-dir 로 세션을 격리하고,
    /// 그 폴더의 최신 세션 ID 를 영속(SettingsService)한 뒤 `gjc -r &lt;id&gt;` 로 같은 대화를 복원한다.
    /// 첫 실행(저장·추출 ID 모두 없음)은 plain `gjc` 로 새 세션 생성. resume 실패(외부 삭제 등) 시 fresh 폴백.
    /// gjc 는 claude 의 --session-id 같은 사전 발급이 없어, 만들어진 ID 를 파일명에서
    /// 캡처하는 방식을 쓴다.</summary>
    private string? TryBuildGajaeDirectLaunch(string roomId, out string? injectFallback)
    {
        injectFallback = null;
        ClearGajaeQuitFlag(roomId);
        var sessionDir = GajaeSessionDir(roomId);
        try { Directory.CreateDirectory(sessionDir); } catch { }

        // 폴더 최신 세션 ID 가 저장값과 다르면 그쪽이 최신 대화 → 교체(새 대화·/clear 추종).
        // gjc 는 새 세션 시 .jsonl 은 lazy 라도 세션 디렉터리(<ts>_<id>/)는 즉시 만들므로,
        // transcript 지연 flush 상황에서도 orphan 디렉터리가 최신 세션의 증거가 된다.
        var sessionId = SettingsService.LoadGajaeRoomSession(roomId);
        var latest = FindLatestGajaeSessionId(sessionDir);
        if (latest != null && latest != sessionId)
        {
            sessionId = latest;
            SettingsService.SaveGajaeRoomSession(roomId, latest);
        }

        // --session-dir 경로는 따옴표로 감싸 공백 안전. -r <id> 는 GUID 만(파일명 검증) → 주입 차단.
        // GJC 의 --hook/--extension 플래그는 0.11.1 에서도 미파싱(경로가 초기 프롬프트가 됨)이고
        // 파일시스템 확장 로딩 자체가 격리(quarantine)라 훅/확장 주입은 불가 — 상태는 GJC 가 직접 쓰는
        // 런타임 사이드카(<workingDir>\.gjc\_session-<id>\runtime\runtime-state.json) + 세션 JSONL 폴링으로 추적한다.
        string sd = GajaeMcpBackend.AppendLaunchArgument($"--session-dir \"{sessionDir}\"");
        // call: gjc 가 gjc.cmd(npm) 인 환경에서도 종료 후 제어가 배치(재진입 루프)로 돌아오게 한다(.exe 엔 무해).
        string cmd = sessionId != null
            ? $"call gjc {sd} -r {sessionId}\r\nif errorlevel 1 call gjc {sd}"
            : $"call gjc {sd}";

        try
        {
            var dir = GajaeLaunchDir();
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            // gjc 일반(비멀티플렉서) 모드로 실행 — 멀티플렉서 모드(STY)는 입력창 하단에 빈 줄을
            // 더 그려서 제외했다. 일반 모드가 풀 재페인트마다 보내는 스크롤백 클리어(\x1b[3J)는
            // terminal.html 파서에서 gjc 방 한정으로 삼켜 스크롤백/휠 스크롤을 보존한다.
            ScriptFile.WriteLaunchCmd(batchPath,
                "@echo off\r\n" +
                $"set \"DEVEZCODE_ROOM_ID={roomId}\"\r\n" +
                TrackingEnvironment.CmdSetLine("gajae") +
                cmd + "\r\n" +
                GajaeReentryLoop(sd, GajaeQuitFlagPath(roomId), RegisterReenterFlag(roomId)));
            return $"cmd.exe /k \"{batchPath}\"";
        }
        catch
        {
            injectFallback = (sessionId != null ? $"gjc {sd} -r {sessionId}" : $"gjc {sd}") + "\r";
            return null;
        }
    }

    /// <summary>"셸 준비 후 주입" 커맨드. 직접 실행(cmd /k) 방·일반 방은 null.
    /// GetOrCreate 가 결정해 둔 값을 1회 소비한다.</summary>
    public string? GetInitialCommand(string roomId)
    {
        lock (_lock)
        {
            if (_pendingInitial.Remove(roomId, out var cmd)) return cmd;
        }
        return null;
    }

    public TerminalSession? Get(string roomId)
    {
        lock (_lock) return _sessions.TryGetValue(roomId, out var s) ? s : null;
    }

    /// <summary>
    /// Claude Code 방의 claude 를 cmd /k 배치로 직접 실행하는 ConPTY 커맨드라인을 만든다.
    /// 방 생성 시 발급한 세션 ID로 첫 실행은 --session-id, 재진입은 --resume(실패 시 --session-id 폴백)으로
    /// 같은 대화를 복원한다. --settings 로 SessionStart 훅(room-hook.ps1)을 주입해 방의 "실제 현재
    /// 세션 ID"를 파일로 기록 — /clear·방 안 /resume·수동 재실행으로 ID가 어긋나도 다음 실행 때 최신 ID로 resume.
    ///
    /// cmd 를 경유하는 이유: PATH/PATHEXT 해석으로 claude.cmd(npm)·sudo-shim(.cmd, 관리자 승격)을
    /// 그대로 띄우고 errorlevel 폴백을 쓸 수 있기 때문(순수 CreateProcess 로는 .cmd·shim 실행 불가).
    /// sudo shim: %USERPROFILE%\.local\sudo-shims\claude.cmd 가 PATH 맨 앞에서 claude 를 가로채 승격한다.
    /// cmd /k 라 claude 종료 후에도 프롬프트가 남아 에러 진단·재실행이 가능하다.
    ///
    /// 배치 작성 실패 시 null 을 반환하고 <paramref name="injectFallback"/> 에 옛 방식(셸 첫 출력 후
    /// 주입할) 커맨드를 채워 호출부가 셸+주입으로 폴백하게 한다. 세션 마킹은 어느 경로든 1회만 일어난다.
    /// </summary>
    private string? TryBuildDirectLaunch(string roomId, string shellCommandLine, out string? injectFallback)
    {
        injectFallback = null;
        // 이전 종료(GracefulExitPlan → MarkClaudeQuitting)가 남긴 종료중 플래그를 새 실행 전에 지운다 —
        // 남아있으면 ClaudeReentryLoop 가 이번 실행의 재진입도 "종료중"으로 오판해 resume 을 못 한다.
        ClearClaudeQuitFlag(roomId);
        // claude 프로세스 기동 직전: /config "← opens agents"(leftArrowOpensAgents) 를 false 로 강제.
        // 전역 ~/.claude.json 키라 room --settings 로는 못 박음. SessionStart 훅이 아니라
        // 세션 생성 이벤트(TryBuildDirectLaunch)에서 처리 — claude 가 설정을 읽기 전에 맞춤.
        // 이미 false 면 no-op 이라 다중 세션 기동 비용 무시 가능.
        ClaudeGlobalSettings.EnsureLeftArrowOpensAgentsDisabled();
        var roomSettings = BuildRoomSettings(roomId); // 방별 settings 생성(roomId 인자 박힌 hook command 포함)
        string flags = "--dangerously-skip-permissions";
        if (File.Exists(roomSettings)) flags += $" --settings \"{roomSettings}\"";

        // 방별 model/effort 를 런치 플래그로 적용. 값은 콤보 화이트리스트지만 변조 대비 영숫자/하이픈만 허용.
        // 우선순위: (1) statusLine 이 영속한 라이브값 = 세션이 마지막에 쓰던 model/effort(TUI 안 /model 변경 포함)
        //          (2) 콤보로 명시 저장한 값. 둘 다 없으면 플래그 생략 → claude 자체 기본값(sonnet 등) 유지.
        var (liveModelId, liveEffort) = ModelEffortService.ReadPersisted(roomId);
        var model = ModelEffortService.ToModelValue(liveModelId)
                    ?? SettingsService.LoadClaudeCodeRoomModel(roomId);
        var effort = liveEffort ?? SettingsService.LoadClaudeCodeRoomEffort(roomId);
        if (IsSafeFlagValue(model)) flags += $" --model {model}";
        if (IsSafeFlagValue(effort)) flags += $" --effort {effort}";

        var sessionId = SettingsService.LoadClaudeCodeRoomSession(roomId);
        // 불변식: 세션 ID는 항상 GUID 여야 한다. 비정상 값(설정 파일 변조 등)은 무시 →
        // 배치에 그대로 보간되어 cmd 명령이 주입되는 것을 원천 차단(새 세션처럼 시작).
        if (sessionId != null && !Guid.TryParse(sessionId, out _)) sessionId = null;

        // 훅이 기록한 마지막 세션 ID가 저장값과 다르면 그쪽이 최신 대화 — 교체 후 resume
        sessionId = SyncTrackedClaudeSessionId(roomId) ?? sessionId;

        // 포크(claude)는 이제 방 생성 시 transcript 를 새 GUID 로 즉시 복사(TryForkClaudeSession)해
        // 독립 세션으로 추적하므로, 여기선 특별 처리 없이 아래 일반 resume 경로로 그 세션을 복원한다.
        // (예전 --fork-session 지연 분기 방식은 포크 방 재실행마다 원본에서 다시 포크되어 폐기했다.)
        //
        // 레거시 마이그레이션: 구버전 --fork-session 으로 만든 포크 방(RoomForkSource 마커 잔존)이
        // 아직 독립 세션으로 안 갈라진 경우(추적==원본 or 미기록) 지금 eager 복사로 독립 세션을 만든다.
        // 이 처리가 없으면 위에서 sessionId==원본으로 잡혀 원본 대화를 그대로 resume → 원본 방과 세션이
        // 섞인다(양쪽이 같은 jsonl 에 쓴다). 이미 갈라졌으면(추적!=원본) 마커만 정리한다.
        var legacyForkSrc = SettingsService.LoadRoomForkSource(roomId);
        if (legacyForkSrc != null)
        {
            bool undiverged = sessionId == null
                || string.Equals(sessionId, legacyForkSrc, StringComparison.OrdinalIgnoreCase);
            bool migrated = false;
            if (undiverged)
            {
                var forked = TryForkClaudeSession(legacyForkSrc, SettingsService.LoadClaudeCodeRoomDir(roomId));
                if (forked != null)
                {
                    // 추적 파일을 '먼저' 갱신하고 성공한 경우에만 마커를 소비한다 — 순서가 반대면
                    // 추적 쓰기가 IO 로 실패했는데 마커가 지워져, 다음 실행의 SyncTrackedClaudeSessionId 가
                    // 옛 원본 id 로 settings 를 되돌리고(원본 방과 세션 공유) 재마이그레이션 기회도 사라진다.
                    try
                    {
                        var tf = Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".txt");
                        Directory.CreateDirectory(Path.GetDirectoryName(tf)!);
                        File.WriteAllText(tf, forked);
                        SettingsService.SaveClaudeCodeRoomSession(roomId, forked);
                        sessionId = forked;
                        migrated = true;
                    }
                    catch { /* 추적 쓰기 실패 → 마커 유지, 다음 실행에 재시도 */ }
                }
                else
                {
                    // 원본 transcript 가 없어 포크 불가 — 마커만 정리(계속 두면 매번 헛시도).
                    migrated = true;
                }
            }
            if (migrated || !undiverged) SettingsService.RemoveRoomForkSource(roomId);
        }

        // resume 은 추적된 세션의 대화 transcript 가 실제로 디스크에 있을 때만 한다.
        // (빈 세션 등 conversation 이 저장 안 된 경우 --resume 하면 "No conversation found" 에러가
        //  화면에 뜨므로, 없으면 --session-id 로 새로 시작해 에러를 원천 차단한다.)
        var ccDir = SettingsService.LoadClaudeCodeRoomDir(roomId);
        // sessionId(훅 기록 파일에서 동기화) + transcript 가 둘 다 있으면 명백한 이전 대화 → 무조건 resume.
        // launched 플래그(settings.json)에 의존하지 않는다 — 작업 중 강제 종료로 플래그가 유실돼도
        // transcript 가 살아있으면 이어가야 하기 때문(예전엔 launched 유실 시 새 세션이 열렸다).
        bool resume = sessionId != null && ClaudeTranscriptExists(ccDir, sessionId);

        // transcript 를 못 찾으면 이번엔 새 세션으로 시작하되, 추적/settings 는 절대 삭제하지 않는다.
        // (예전엔 여기서 삭제(자폭)했는데 — 스캔 일시 실패·파일잠금·외부 동기화 지연 등으로 잠깐만 못 찾아도
        //  멀쩡한 세션을 영구히 잃었다. 삭제하지 않으면: ① 사용자가 새 세션에서 대화하면 busy-hook 이
        //  새 sid 로 추적을 덮어 자연히 정상화되고, ② transcript 가 다시 보이면 그대로 resume 된다.
        //  빈 세션 ID 고착(영원히 빈 화면)도 안 생긴다 — 이번 실행이 이미 새 세션(sessionId=null)으로 시작하기 때문.)
        if (sessionId != null && !resume)
        {
            // 폴백: resume 은 새 sid 로 즉시 포크하고 SessionStart 훅이 그 sid 를 추적에 기록하지만,
            // 새 .jsonl 은 첫 활동 전까지 생성되지 않는다(lazy). 그 사이 앱이 재시작되면 추적이
            // transcript 없는 유령 sid 를 가리켜 방이 "빈 세션"으로 열리고, 이전 대화는 디스크에
            // 멀쩡히 있는데 포인터만 잃는다. 훅이 보존한 직전 sid(.prev)의 transcript 가 있으면
            // 그쪽으로 복원하고 추적 파일도 치유한다(재진입 배치 루프 %SID% 도 같은 결론을 보도록).
            var prevSid = LoadPrevTrackedSessionId(roomId);
            if (prevSid != null && !string.Equals(prevSid, sessionId, StringComparison.OrdinalIgnoreCase)
                && ClaudeTranscriptExists(ccDir, prevSid))
            {
                DiagLog.Write($"launch[{roomId}]: 추적 sid={sessionId} transcript 없음(유령) → prev sid={prevSid} 로 복원");
                sessionId = prevSid;
                resume = true;
                try
                {
                    File.WriteAllText(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".txt"), prevSid);
                    SettingsService.SaveClaudeCodeRoomSession(roomId, prevSid);
                }
                catch { /* 치유 실패해도 이번 실행은 prevSid 로 resume — 다음 실행이 재시도 */ }
            }
            else
            {
                // prev 도 유령 — 방별 known-good 링(실활동 sid 이력, 최신순)에서 살아있는 transcript 를 가진
                // 첫 sid 로 복원한다. 연속 무활동 재시작으로 tracked·prev 가 모두 유령이 돼도 실제 대화를 되찾는다.
                // 방 단위 파일이라 cwd 공유 프로젝트에서 다른 방 대화를 끌어오지 않는다.
                string? recovered = null;
                foreach (var g in Enumerable.Reverse(LoadGoodTrackedSessionIds(roomId)))
                {
                    if (!string.Equals(g, sessionId, StringComparison.OrdinalIgnoreCase)
                        && ClaudeTranscriptExists(ccDir, g)) { recovered = g; break; }
                }
                if (recovered != null)
                {
                    DiagLog.Write($"launch[{roomId}]: 추적 sid={sessionId}·prev 유령 → good 링 sid={recovered} 로 복원");
                    sessionId = recovered;
                    resume = true;
                    try
                    {
                        File.WriteAllText(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".txt"), recovered);
                        SettingsService.SaveClaudeCodeRoomSession(roomId, recovered);
                    }
                    catch { /* 치유 실패해도 이번 실행은 recovered 로 resume — 다음 실행이 재시도 */ }
                }
                else
                {
                    DiagLog.Write($"launch[{roomId}]: 추적 sid={sessionId} transcript 없음, prev·good 폴백 불가 → 새 세션으로 시작");
                    sessionId = null;
                }
            }
        }

        // 배치 본문: 세션 없으면 단발(새 세션), 있으면(=transcript 확인됨) resume → 실패(외부 삭제 등) 시
        // --session-id 폴백(추적 ID 보존) → 그마저 실패(손상/타 인스턴스 점유로 "already in use" 등) 시
        // plain 새 세션. 3단이 없으면 이중 실패 때 cmd 프롬프트만 남아 방이 죽는다.
        // claude 가 정상 시작하면 인터랙티브로 유지되어 뒤 폴백 줄은 실행되지 않는다.
        // call: claude 가 claude.cmd(npm) 인 환경에서도 종료 후 제어가 배치(재진입 루프)로 돌아오게 한다(.exe 엔 무해).
        string body;
        if (sessionId == null)
            body = $"call claude {flags}";
        else
            body = $"call claude --resume {sessionId} {flags}\r\n"
                 + $"if errorlevel 1 call claude --session-id {sessionId} {flags}\r\n"
                 + $"if errorlevel 1 call claude {flags}";

        try
        {
            Directory.CreateDirectory(LaunchDir);
            var trackFile = Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".txt");
            ScriptFile.WriteLaunchCmd(LaunchBatchPath(roomId),
                "@echo off\r\n" +
                $"set \"DEVEZCODE_ROOM_ID={roomId}\"\r\n" +
                TrackingEnvironment.CmdSetLine("claude") +
                body + "\r\n" +
                ClaudeReentryLoop(flags, trackFile, ClaudeQuitFlagPath(roomId), RegisterReenterFlag(roomId)));
            // 경로에 공백이 있어도 cmd /k "<단일 토큰>" 규칙으로 안전(따옴표 보존/제거 모두 정상 실행).
            return $"cmd.exe /k \"{LaunchBatchPath(roomId)}\"";
        }
        catch (Exception)
        {
            injectFallback = BuildInjectCommand(shellCommandLine, sessionId, resume, flags);
            return null;
        }
    }

    /// <summary>폴백용 — 셸 첫 출력 후 주입할 claude 커맨드(끝에 CR). 셸 종류에 맞춘 resume 폴백 포함.</summary>
    private static string BuildInjectCommand(string shellCommandLine, string? sessionId, bool resume, string flags)
    {
        if (sessionId == null) return $"claude {flags}\r";
        if (!resume) return $"claude --session-id {sessionId} {flags}\r";
        var r = $"claude --resume {sessionId} {flags}";
        var f = $"claude --session-id {sessionId} {flags}";
        var n = $"claude {flags}"; // 3단: --session-id 도 실패("already in use" 등) 시 plain 새 세션
        // cmd.exe 는 `a || b`, PowerShell 은 `a; if ($LASTEXITCODE -ne 0) { b }`.
        bool isCmd = shellCommandLine.Contains("cmd", StringComparison.OrdinalIgnoreCase)
                     && !shellCommandLine.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                     && !shellCommandLine.Contains("pwsh", StringComparison.OrdinalIgnoreCase);
        return isCmd
            ? $"{r} || {f} || {n}\r"
            : $"{r}; if ($LASTEXITCODE -ne 0) {{ {f} }}; if ($LASTEXITCODE -ne 0) {{ {n} }}\r";
    }

    // ── 종료 시 같은 세션으로 자동 재진입 (cmd /k 배치 루프) ──────────────
    // 에이전트별 종료 단축키로 CLI 를 닫아도 셸로 빠지지 않고, 방이 살아있는 동안은 같은 세션으로 다시 띄운다.
    // "진짜 종료"는 앱에서 방(탭)을 닫는 것으로만 한다. 각 에이전트의 첫 실행(fork/resume/신규 판정)
    // 배치 본문 뒤에 이 루프를 붙인다 — 루프는 매 반복 추적파일/세션-dir 에서 현재 세션을 다시 잡는다.
    // 안전장치: 연속 실행 실패(=CLI 미설치 등)가 5회 쌓이면 무한 스핀 대신 프롬프트를 남겨 진단 가능.

    // ── 재진입 플래그 (배치 루프 → 앱 신호) ─────────────────────────
    // 배치 재진입 루프가 에이전트를 다시 띄우기 "직전" 방별 플래그 파일을 touch 한다.
    // FileSystemWatcher 가 이를 감지해 RoomReentering 을 발화 — UI(TerminalHostView)가 즉시
    // 로딩 커버를 띄워 배치 에코·resume 커맨드 입력 과정을 가린다. 출력 스트림 휴리스틱
    // (alt-screen 이탈 감지)과 달리 오발동이 없고 ConPTY 의 시퀀스 필터링에도 안전하다.

    /// <summary>배치 재진입 직전 신호. roomId 전달 — FSW 백그라운드 스레드에서 발화되므로 구독자가 마샬링.</summary>
    public static event Action<string>? RoomReentering;

    private static string ReenterFlagDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "reenter");
    private static FileSystemWatcher? _reenterWatcher;
    private static readonly Dictionary<string, string> _reenterRoomByFile = new(); // "<safe>.flag" → roomId
    private static readonly object _reenterLock = new();

    /// <summary>방의 재진입 플래그 파일 경로를 등록(파일명→roomId 역매핑 + FSW 1회 기동)하고 반환.
    /// 배치 작성 시점에 호출 — 실패해도 배치 실행엔 지장 없게 예외를 삼키고 경로만 돌려준다.</summary>
    private static string RegisterReenterFlag(string roomId)
    {
        var file = SafeRoomFileName(roomId) + ".flag";
        var path = Path.Combine(ReenterFlagDir, file);
        try
        {
            Directory.CreateDirectory(ReenterFlagDir);
            lock (_reenterLock)
            {
                _reenterRoomByFile[file] = roomId;
                if (_reenterWatcher == null)
                {
                    _reenterWatcher = new FileSystemWatcher(ReenterFlagDir, "*.flag")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    };
                    FileSystemEventHandler h = (_, e) =>
                    {
                        string? room;
                        lock (_reenterLock) _reenterRoomByFile.TryGetValue(e.Name ?? string.Empty, out room);
                        if (room != null) RoomReentering?.Invoke(room);
                    };
                    _reenterWatcher.Created += h;
                    _reenterWatcher.Changed += h;
                    _reenterWatcher.EnableRaisingEvents = true;
                }
            }
        }
        catch { /* 감시 실패 = 스피너 커버만 없음 — 재진입 자체는 배치가 수행 */ }
        return path;
    }

    /// <summary>재진입 루프 공통 꼬리: 실패 카운트·스로틀(≈1s)·재진입 goto·5회 실패 시 giveup.</summary>
    private static string ReentryTail() =>
        "if errorlevel 1 (set /a FAILS+=1) else (set \"FAILS=0\")\r\n" +
        "if %FAILS% geq 5 goto __giveup\r\n" +
        "ping -n 2 127.0.0.1 >nul\r\n" +      // ≈1s 스로틀 — 정상 TUI 종료엔 무해, 크래시 루프 스핀 방지
        "goto __reenter\r\n" +
        ":__giveup\r\n" +
        "echo.\r\n" +
        "echo [DevezCode] Session restart failed repeatedly. Run a command or close this room.\r\n";

    /// <summary>claude 재진입 루프 — 추적파일(sessions\&lt;room&gt;.txt, 훅이 라이브 갱신)의 sid 를 매 반복
    /// 다시 읽어 --resume. resume 실패(transcript 삭제 등) 시 즉시 신규 세션으로 폴백(에러 잔류 없음).
    /// quitFlagPath: 앱 종료(GracefulExitPlan → MarkClaudeQuitting)가 남긴 "종료중" 플래그. escFirst
    /// 로 claude 가 예상보다 빨리 정상 종료하면 Dispose(ConPTY 닫기) 전 이 틈에 재진입해 새 claude 를
    /// 띄우는 레이스가 있어, 재진입 직전에 이 플래그를 먼저 확인해 있으면 루프를 끝낸다.</summary>
    private static string ClaudeReentryLoop(string flags, string trackFile, string quitFlagPath, string reenterFlagPath)
    {
        // 유령 SID(resume 포크 직후 .jsonl 미생성 상태에서 크래시/수동 /exit) 대비: 훅이 보존한
        // 직전 sid(.prev)로 한 번 더 resume 을 시도한 뒤에야 새 세션으로 폴백한다 — 대화 유실 방지.
        var prevFile = trackFile.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? trackFile[..^4] + ".prev.txt"
            : trackFile + ".prev";
        return
        "set FAILS=0\r\n" +
        ":__reenter\r\n" +
        $"if exist \"{quitFlagPath}\" goto __quitflag\r\n" +
        // 재실행 직전 앱에 신호(touch) — FSW 가 감지해 로딩 커버를 띄운다. 종료(quitflag) 경로는 안 지나므로
        // 앱 종료 중 스피너 오발동 없음. 첫 실행은 루프 밖(body)이라 콜드스타트에도 안 걸린다.
        $"type nul >\"{reenterFlagPath}\"\r\n" +
        "set \"SID=\"\r\n" +
        $"if exist \"{trackFile}\" for /f \"usebackq delims=\" %%i in (\"{trackFile}\") do set \"SID=%%i\"\r\n" +
        "set \"PSID=\"\r\n" +
        $"if exist \"{prevFile}\" for /f \"usebackq delims=\" %%i in (\"{prevFile}\") do set \"PSID=%%i\"\r\n" +
        "if defined SID (\r\n" +
        $"  call claude --resume %SID% {flags}\r\n" +   // call: claude 가 claude.cmd(npm) 인 환경에서도 종료 후 제어가 루프로 복귀
        // prev 폴백 전 추적 파일을 prev 로 치유 — 안 하면 새 claude 의 SessionStart 훅이 추적 파일의
        // 유령 sid 를 $prev 로 읽어 .prev 를 유령으로 덮는다(체인 오염). C# 실행 경로의 치유와 동일 원리.
        $"  if errorlevel 1 if defined PSID (\r\n" +
        $"    copy /y \"{prevFile}\" \"{trackFile}\" >nul\r\n" +
        $"    call claude --resume %PSID% {flags}\r\n" +
        $"  )\r\n" +
        $"  if errorlevel 1 call claude {flags}\r\n" +
        ") else (\r\n" +
        $"  call claude {flags}\r\n" +
        ")\r\n" +
        ReentryTail() +
        "goto :eof\r\n" + // giveup은 진단용 cmd 프롬프트를 유지; quitflag만 아래 exit로 셸까지 닫는다.
        ":__quitflag\r\n" +
        "exit\r\n";
    }

    /// <summary>가재(gjc) 재진입 루프 — 방별 --session-dir 로 격리돼 있어 -c(최신 이어가기)가 곧 이 방의
    /// 마지막 대화. 앱의 session-dir 폴링(GajaeLastMessageService)이 재진입 세션도 그대로 추적한다.
    /// 앱이 Ctrl+D 종료 전에 quitFlagPath를 남기면 새 gjc를 띄우지 않고 배치를 끝낸다.</summary>
    private static string GajaeReentryLoop(string sd, string quitFlagPath, string reenterFlagPath) =>
        "set FAILS=0\r\n" +
        ":__reenter\r\n" +
        $"if exist \"{quitFlagPath}\" goto __quitflag\r\n" +
        $"type nul >\"{reenterFlagPath}\"\r\n" + // 재실행 직전 앱 신호(touch) — 로딩 커버용. 첫 실행은 루프 밖
        $"call gjc {sd} -c\r\n" +   // call: gjc 가 gjc.cmd(npm) 인 환경에서도 종료 후 제어가 루프로 복귀
        $"if errorlevel 1 call gjc {sd}\r\n" +
        ReentryTail() +
        "goto :eof\r\n" + // giveup은 진단용 cmd 프롬프트를 유지; quitflag만 아래 exit로 셸까지 닫는다.
        ":__quitflag\r\n" +
        "exit\r\n";

    // ── Claude 세션 ID 추적 (%APPDATA%\DevezCode\claude\) ──────────────
    // SessionStart 훅이 방별 현재 세션 ID를 sessions\<roomId>.txt 에 기록한다.

    private static string ClaudeTrackDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude");
    private static readonly Lazy<Version?> InstalledClaudeVersion = new(DetectInstalledClaudeVersion);
    private static string HookScriptPath => Path.Combine(ClaudeTrackDir, "room-hook.ps1");
    // 방별 claude --settings 파일. hook/statusLine command 에 roomId 인자가 박혀 있다(BuildRoomSettings).
    private static string RoomSettingsPath(string roomId) => Path.Combine(ClaudeTrackDir, "room-settings", SafeRoomFileName(roomId) + ".json");
    // statusLine 훅: stdin 으로 받은 statusLine JSON 을 그대로 파일에 떨군다(계정 rate_limits 추출용).
    private static string StatusLineScriptPath => Path.Combine(ClaudeTrackDir, "statusline-hook.ps1");
    // statusLine 렌더용 node 스크립트(powershell 체인 대체, ~150ms). 캡처(ratelimit/modeleffort) +
    // 사용자 statusline.js 스폰 렌더를 한 번의 node 프로세스로 처리한다. node 있을 때만 사용.
    private static string RoomStatusLineJsPath => Path.Combine(ClaudeTrackDir, "room-statusline.js");
    // busy 훅: UserPromptSubmit(running)/Stop(idle) 시 방별 상태 파일을 써 좌측 트리 스피너를 켜고 끈다.
    private static string BusyHookScriptPath => Path.Combine(ClaudeTrackDir, "busy-hook.ps1");

    // 방별 claude 직접 실행 배치(cmd /k 로 띄움). 매 실행 시 최신 커맨드로 덮어쓴다.
    private static string LaunchDir => Path.Combine(ClaudeTrackDir, "launch");
    private static string LaunchBatchPath(string roomId) => Path.Combine(LaunchDir, SafeRoomFileName(roomId) + ".cmd");

    /// <summary>런치 플래그 값 안전성 — 모델 ID에 쓰이는 영숫자/점/밑줄/하이픈만 허용
    /// (공백·따옴표·세미콜론 등 명령 주입 문자는 차단). 빈 값은 false.</summary>
    private static bool IsSafeFlagValue(string? v)
        => !string.IsNullOrEmpty(v) && System.Text.RegularExpressions.Regex.IsMatch(v, @"^[A-Za-z0-9._\-]+$");

    /// <summary>Grok 캐시가 있으면 저장된 모델/effort가 현재도 지원되는지 확인한다.
    /// 캐시가 없거나 업데이트 도중 읽기 실패면 UI의 폴백 목록을 허용하고, 명백히 제거된 값만 차단한다.</summary>
    private static bool IsCachedGrokSelection(string? model, string? effort)
    {
        if (model == null) return effort == null; // 기본 모델에서는 모델별 지원 effort를 확정할 수 없다.
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "models_cache.json");
            if (!File.Exists(path)) return true;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("models", out var grokModels)
                || !grokModels.TryGetProperty(model, out var grokModel)
                || !grokModel.TryGetProperty("info", out var info)) return false;
            if (info.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == System.Text.Json.JsonValueKind.True)
                return false;
            if (effort == null) return true;
            if (!info.TryGetProperty("reasoning_efforts", out var grokLevels)) return false;
            return grokLevels.EnumerateArray().Any(level =>
                level.TryGetProperty("value", out var value) && value.GetString() == effort);
        }
        catch { return true; }
    }

    /// <summary>roomId를 파일명으로 안전하게 (훅 ps1의 -replace 와 동일 규칙).</summary>
    private static string SafeRoomFileName(string roomId)
        => System.Text.RegularExpressions.Regex.Replace(roomId, @"[^\w\-]", "");

    /// <summary>훅 자산(스크립트·설정)이 모두 있고 busy 연동을 포함하는 최신본인지.
    /// 시작 시 배너 표시 판단용 — 하나라도 누락/구버전이면 false.</summary>
    public static bool HookAssetsHealthy()
    {
        try
        {
            if (!File.Exists(BusyHookScriptPath)) return false;
            if (!File.Exists(HookScriptPath)) return false;
            if (!File.Exists(StatusLineScriptPath)) return false;
            // settings 는 방별로 세션마다 생성되므로 공통 파일 대신 스크립트가 최신본인지 확인한다.
            var busy = File.ReadAllText(BusyHookScriptPath);
            return busy.Contains("roomArg") && busy.Contains("last_assistant_message") &&
                   busy.Contains("Touch-LiveSubruns") && busy.Contains("'permission'") &&
                   busy.Contains("agent_needs_input") && // ❗ Notification type 분기 + 서브 unwait 무시 버전
                   busy.Contains(@"claude\done") && // 턴종료 마커(완료카드 게이트) 버전
                   busy.Contains("DEVEZCODE_TRACKING_AGENT"); // 교차 에이전트 상속 차단 버전
        }
        catch { return false; }
    }

    /// <summary>훅 자산을 (재)생성한다.</summary>
    public static void EnsureHookAssets() => EnsureSessionHookAssets();

    /// <summary>Claude SessionStart 훅이 남긴 room별 id 파일을 감시해 실행 중 /resume 등도 즉시 영속화한다.</summary>
    private void EnsureClaudeSessionWatcher()
    {
        lock (_lock)
        {
            if (_claudeSessionWatcher != null) return;
            try
            {
                var dir = Path.Combine(ClaudeTrackDir, "sessions");
                Directory.CreateDirectory(dir);
                var watcher = new FileSystemWatcher(dir, "*.txt")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                // 핸들러를 먼저 붙인 뒤 활성화해야 그 사이 발생 이벤트를 놓치지 않는다.
                watcher.Created += OnClaudeSessionFileChanged;
                watcher.Changed += OnClaudeSessionFileChanged;
                watcher.Renamed += OnClaudeSessionFileRenamed;
                watcher.EnableRaisingEvents = true;
                _claudeSessionWatcher = watcher;
            }
            catch (Exception) { /* 추적 실패해도 claude 실행은 계속 */ }
        }
    }

    private static void OnClaudeSessionFileChanged(object sender, FileSystemEventArgs e)
        => SyncTrackedClaudeSessionFile(e.FullPath);

    private static void OnClaudeSessionFileRenamed(object sender, RenamedEventArgs e)
        => SyncTrackedClaudeSessionFile(e.FullPath);

    private static void SyncTrackedClaudeSessionFile(string path)
    {
        try
        {
            var roomId = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(roomId)) return;
            // 삭제된 방의 남은 파일이 settings 에 다시 들어오지 않도록 현재 등록된 room 만 반영한다.
            if (SettingsService.LoadClaudeCodeRoomDir(roomId) == null) return;
            SyncTrackedClaudeSessionId(roomId);
        }
        catch (Exception) { }
    }

    private static string? SyncTrackedClaudeSessionId(string roomId)
    {
        var tracked = LoadTrackedSessionId(roomId);
        if (tracked == null) return null;
        if (SettingsService.LoadClaudeCodeRoomSession(roomId) != tracked)
            SettingsService.SaveClaudeCodeRoomSession(roomId, tracked);
        SettingsService.MarkClaudeCodeRoomLaunched(roomId); // 기록이 있다 = 이미 실행된 적 있음
        return tracked;
    }

    /// <summary>SessionStart 훅 스크립트·설정 파일 생성 (항상 덮어써 최신 유지).</summary>
    private static void EnsureSessionHookAssets()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(ClaudeTrackDir, "sessions"));

            const string script = """
                # DevezCode claude room session tracker (SessionStart hook)
                # 세션 ID 추적은 다음 시점에 기록한다:
                #  - SessionStart 에서 source 가 resume(명시적 세션 전환), clear(/clear), compact(내부 재구성) → 즉시 기록
                #  - UserPromptSubmit(busy 'running') 에서 사용자가 메시지를 보낸 세션 → 추가 기록
                # startup(앱 시작 시 --session-id)은 빈 세션 ID가 기존 대화를 덮는 것을 방지하기 위해 제외.
                # roomId 는 settings command 인자(우선) 또는 env 로 받는다(claude 가 env 를 자식에 못 넘기는
                # 환경 대비 — 인자가 1차, env 는 폴백).
                param([string]$roomArg = '')
                try {
                  if ($env:DEVEZCODE_TRACKING_AGENT -ne 'claude') {
                    [Console]::In.ReadToEnd() | Out-Null
                    exit 0
                  }
                  # tmp 파일에 먼저 쓰고 교체(원자적) — 강제종료가 쓰기 도중 끼어들어도 파일이 잘린 채로
                  # 남지 않는다(직접 Set-Content 는 중간에 죽으면 손상/빈 파일이 남아 session_id 를 통째로 잃는다).
                  function Write-State($path, $value, $encoding = 'Ascii') {
                    try {
                      $tmp = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
                      Set-Content -LiteralPath $tmp -Value $value -Encoding $encoding -Force
                      Move-Item -LiteralPath $tmp -Destination $path -Force
                    } catch { try { Set-Content -LiteralPath $path -Value $value -Encoding $encoding -Force } catch { } }
                  }
                  $j = [Console]::In.ReadToEnd() | ConvertFrom-Json
                  $room = if ($roomArg) { $roomArg } else { $env:DEVEZCODE_ROOM_ID }
                  # session_id 가 비면(claude stdin 포맷/필드명 변경 대비) transcript_path 파일명(<sid>.jsonl)에서 복구한다.
                  $sid = '' + $j.session_id
                  if (-not $sid) { try { $sid = [System.IO.Path]::GetFileNameWithoutExtension('' + $j.transcript_path) } catch { } }
                  # /clear·/resume·compact: 세션 전환 발생. 새 session_id 를 추적에 박아 다음 실행 시
                  # 해당 세션으로 바로 복원되게 한다.
                  if ($room -and $sid -and ($j.source -eq 'clear' -or $j.source -eq 'resume' -or $j.source -eq 'compact')) {
                    $room = $room -replace '[^\w\-]', ''
                    $dir = Join-Path $env:APPDATA 'DevezCode\claude\sessions'
                    New-Item -ItemType Directory -Force -Path $dir | Out-Null
                    # lastmsg(헤더 타이틀의 마지막 프롬프트)는 "다른 대화로 바뀔 때만" 비운다.
                    # 판정 = 새 sid 가 기존 추적 sid 와 다른가. /clear(새 sid)·다른 세션 /resume 전환 → 비움,
                    # 앱 재실행의 --resume(같은 sid)·compact(같은 sid) → 유지.
                    # (예전엔 source 만 보고 무조건 비워서, 재실행할 때마다 타이틀이 세션명으로 돌아갔다.)
                    $tfile = Join-Path $dir ($room + '.txt')
                    $prev = ''
                    try { if (Test-Path -LiteralPath $tfile) { $prev = (Get-Content -LiteralPath $tfile -Raw -ErrorAction SilentlyContinue).Trim() } } catch { }
                    # sid 가 바뀌면 이전 sid 를 .prev 로 보존 — resume 은 새 sid 로 포크하지만 새 .jsonl 은
                    # 첫 활동 전까지 생성되지 않아(lazy), 그 사이 재시작하면 추적이 유령 sid 를 가리켜
                    # 빈 세션으로 열린다. 실행 시(C# TryBuildDirectLaunch) transcript 없으면 .prev 로 복원.
                    # 단 /clear(의도적 초기화)는 prev 를 지운다 — 남겨두면 "clear 후 입력 없이 재시작" 때
                    # 폴백이 사용자가 지운 대화를 되살린다(clear 의도 위반).
                    $pfile = Join-Path $dir ($room + '.prev.txt')
                    if ($j.source -eq 'clear') {
                      try { Remove-Item -LiteralPath $pfile -Force -ErrorAction SilentlyContinue } catch { }
                    } elseif ($prev -and $sid -ne $prev) {
                      Write-State $pfile $prev
                    }
                    Write-State $tfile $sid
                    if ($sid -ne $prev) {
                      $mdir = Join-Path $env:APPDATA 'DevezCode\claude\lastmsg'
                      New-Item -ItemType Directory -Force -Path $mdir | Out-Null
                      Set-Content -LiteralPath (Join-Path $mdir ($room + '.txt')) -Value '' -Encoding UTF8 -Force
                    }
                  }
                } catch { }
                exit 0
                """;
            ScriptFile.WritePs1(HookScriptPath, script);

            // statusLine 훅: 받은 JSON 을 ratelimit.json 에 저장(앱 푸터용)한 뒤, 사용자의 원래
            // statusLine(~/.claude/settings.json)에 같은 JSON 을 넘겨 그 출력을 그대로 통과시킨다.
            // → CLI 에 원래 뜨던 statusLine 이 그대로 유지되면서 앱도 계정 rate_limits 를 캡처한다.
            // 여러 세션이 같은 파일에 써도 rate_limits 는 계정 전역값이라 마지막으로 쓴 것이 곧 최신.
            const string statusScript = """
                # DevezCode statusLine: capture account rate_limits, then pass through to the
                # user's own statusLine so the original CLI status line keeps rendering.
                # roomId 는 settings command 인자(우선) 또는 env(폴백)로 받는다.
                param([string]$roomArg = '')
                if ($env:DEVEZCODE_TRACKING_AGENT -ne 'claude') {
                  [Console]::In.ReadToEnd() | Out-Null
                  exit 0
                }
                $rid = if ($roomArg) { $roomArg } else { $env:DEVEZCODE_ROOM_ID }
                [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
                $raw = [Console]::In.ReadToEnd()
                $o = $null
                try { $o = $raw | ConvertFrom-Json } catch { }
                try {
                  $dir = Join-Path $env:APPDATA 'DevezCode\claude'
                  New-Item -ItemType Directory -Force -Path $dir | Out-Null
                  Set-Content -LiteralPath (Join-Path $dir 'ratelimit.json') -Value $raw -Encoding utf8 -Force
                } catch { }
                # 방별 실제 model/effort/context/session ID 를 떨군다. GUI 전환 시 같은 상태를 복원한다.
                $sig = ''
                try {
                  if ($o) { $sig = (('' + $o.model.id) + '-' + ('' + $o.effort.level)) -replace '[^\w\-]', '' }
                  $room = $rid
                  if ($room -and $o) {
                    $room = $room -replace '[^\w\-]', ''
                    $md = Join-Path $dir 'modeleffort'
                    New-Item -ItemType Directory -Force -Path $md | Out-Null
                    Set-Content -LiteralPath (Join-Path $md ($room + '.txt')) -Value ("{0}`n{1}`n{2}`n{3}`n{4}" -f $o.model.id, $o.effort.level, $o.context_window.total_input_tokens, $o.context_window.context_window_size, $o.session_id) -Encoding utf8 -Force
                  }
                } catch { }
                try {
                  $sp = Join-Path $env:USERPROFILE '.claude\settings.json'
                  if (Test-Path $sp) {
                    $cmd = (Get-Content -Raw -LiteralPath $sp | ConvertFrom-Json).statusLine.command
                    if ($cmd) {
                      # 사용자 statusLine 패스스루는 매 호출마다 cmd 프로세스를 새로 띄워 비싸다.
                      # 3초 캐시: 직전 출력을 파일로 두고 만료 전이면 재실행 없이 그대로 통과시킨다.
                      # 캐시 키 = 방 + model/effort 시그니처. 방별 분리(세션 간 값 안 섞임) + model/effort 가
                      # 바뀌면 키가 달라져 즉시 캐시 미스 → 새 모델/강도가 statusline 에 바로 반영(interval 대기 X).
                      $cacheRoom = if ($rid) { $rid -replace '[^\w\-]', '' } else { 'global' }
                      $cache = Join-Path $dir ('statusline-cache-' + $cacheRoom + '-' + $sig + '.txt')
                      $fresh = (Test-Path $cache) -and (((Get-Date) - (Get-Item $cache).LastWriteTime).TotalSeconds -lt 3)
                      if ($fresh) {
                        [Console]::Out.Write((Get-Content -Raw -LiteralPath $cache))
                      } else {
                        $out = $raw | & $env:ComSpec /c $cmd 2>$null
                        if ($out) {
                          $text = ($out -join "`n")
                          Set-Content -LiteralPath $cache -Value $text -Encoding utf8 -Force
                          [Console]::Out.Write($text)
                        }
                      }
                    }
                  }
                } catch { }
                exit 0
                """;
            ScriptFile.WritePs1(StatusLineScriptPath, statusScript);

            // statusLine 렌더 node 스크립트: powershell→cmd→node 체인(1.4~2초) 대신 node 한 번(~150ms)으로
            // ① rate_limits 캡처(ratelimit.json) ② 방별 model/effort/context 기록 ③ 사용자 statusline.js 스폰 렌더.
            // DevezCode 내부 세션이 일반 터미널과 동일하게 빠르게 statusLine 을 그리게 한다(resume 빈 줄 해소).
            const string roomStatusJs = """
                // DEVEZCODE-ROOM-STATUSLINE v3 (GUI 전환용 context/session ID 기록 추가)
                const fs = require("fs"), path = require("path"), os = require("os"), cp = require("child_process");
                const roomArg = (process.argv[2] || process.env.DEVEZCODE_ROOM_ID || "").replace(/[^\w\-]/g, "");
                let raw = "";
                process.stdin.on("data", c => raw += c);
                process.stdin.on("end", () => {
                  if (process.env.DEVEZCODE_TRACKING_AGENT !== "claude") return;
                  let o = null;
                  try { o = JSON.parse(raw); } catch (e) {}
                  const appData = process.env.APPDATA;
                  // 1) 계정 rate_limits 캡처(앱 푸터용). 마지막으로 쓴 값이 최신.
                  try {
                    if (appData) {
                      const dir = path.join(appData, "DevezCode", "claude");
                      fs.mkdirSync(dir, { recursive: true });
                      fs.writeFileSync(path.join(dir, "ratelimit.json"), raw);
                    }
                  } catch (e) {}
                  // 2) 방별 실제 model/effort/context/session ID 기록(GUI 전환 복원).
                  try {
                    if (appData && roomArg && o) {
                      const md = path.join(appData, "DevezCode", "claude", "modeleffort");
                      fs.mkdirSync(md, { recursive: true });
                      const mid = (o.model && o.model.id) || "";
                      const eff = (o.effort && o.effort.level) || "";
                      const context = o.context_window || {};
                      const used = Number.isFinite(context.total_input_tokens) ? context.total_input_tokens : "";
                      const window = Number.isFinite(context.context_window_size) ? context.context_window_size : "";
                      const sid = typeof o.session_id === "string" ? o.session_id : "";
                      fs.writeFileSync(path.join(md, roomArg + ".txt"), [mid, eff, used, window, sid].join("\n"));
                    }
                  } catch (e) {}
                  // 3) 사용자 statusline.js 로 렌더 위임(사용자 커스터마이즈 보존). 같은 node 재사용.
                  //    렌더 결과를 방별 캐시(statusline-cache-<room>-<sig>.txt)에도 떨군다 —
                  //    앱 사이드바 푸터가 이 파일을 읽는다(sig=model-effort 라 모델/강도 변경 즉시 반영).
                  try {
                    const js = path.join(os.homedir(), ".claude", "statusline.js");
                    if (fs.existsSync(js)) {
                      const r = cp.spawnSync(process.execPath, [js], {
                        input: raw, encoding: "utf8", timeout: 4000, windowsHide: true
                      });
                      if (r && r.stdout) {
                        process.stdout.write(r.stdout);
                        try {
                          if (appData && o) {
                            const dir = path.join(appData, "DevezCode", "claude");
                            const mid = (o.model && o.model.id) || "";
                            const eff = (o.effort && o.effort.level) || "";
                            const sig = (mid + "-" + eff).replace(/[^\w\-]/g, "");
                            const room = roomArg || "global";
                            fs.writeFileSync(path.join(dir, "statusline-cache-" + room + "-" + sig + ".txt"), r.stdout);
                          }
                        } catch (e) {}
                      }
                    }
                  } catch (e) {}
                  process.exit(0);
                });
                """;
            File.WriteAllText(RoomStatusLineJsPath, roomStatusJs, new System.Text.UTF8Encoding(false));

            // busy 훅(clude-blinker 방식): claude 가 한 턴을 처리하는 동안만 좌측 스피너를 켠다.
            // 방 ID는 SessionStart 훅과 동일하게 인자(roomArg)/$env:DEVEZCODE_ROOM_ID 로 구분.
            //
            // 스피너 = (메인 턴 진행중) OR (살아있는 서브에이전트 >=1). 둘 다 room 키(session_id 아님)로
            // 판정하므로 resume 로 세션ID 가 바뀌어도 추적이 끊기지 않는다. (구버전은 Stop 시점에
            // agent-*.meta.json 개수 델타 - <task-notification> 완료수 로 pending 을 '추론'했는데,
            // baseline 이 session_id 키라 resume 시 미스매치→pending=0→스피너 조기소멸했다.)
            //   • 메인 턴: UserPromptSubmit=running(main 플래그 set) / Stop·SessionEnd=idle(main 플래그 clear).
            //   • 서브에이전트: SubagentStart=substart(run 파일 생성) / SubagentStop=substop(run 파일 삭제).
            //     busy 는 substop·Stop 시점에 (main 플래그 존재 OR run 파일 개수>0)로 재평가한다.
            // ghost 방어: SubagentStop 누락(크래시/kill) 대비 1시간 초과 run 파일은 카운트 전 prune.
            // 아주 짧은 서브에서 Stop 훅 프로세스가 Start보다 먼저 끝나는 역전은 agent_id.done fence로 차단.
            // SessionEnd 시 방 run 디렉터리 전량 제거, 앱 시작 시 subruns/main 플래그 wipe(C# SessionBusyService).
            // 완료카드 게이트: Stop/SessionEnd 만 done\<room>.txt 턴종료 마커를 남긴다. main 플래그가 유실된 방에서
            // substop 드레인이 만드는 순간 idle(서브 간 공백 flap)은 마커가 없어 카드/알림이 발행되지 않는다.
            const string busyScript = """
                # DevezCode busy-state hook. Arg1 = running|idle|notify|unwait|pulse|substart|substop. Per-room sidebar spinner state.
                # 스피너 = (메인 턴 진행중) OR (살아있는 서브에이전트 >=1). 둘 다 room 키 → resume 로 session_id 바뀌어도 안 깨짐.
                param([string]$status = 'idle', [string]$roomArg = '')
                try {
                  if ($env:DEVEZCODE_TRACKING_AGENT -ne 'claude') {
                    [Console]::In.ReadToEnd() | Out-Null
                    exit 0
                  }
                  $room = if ($roomArg) { $roomArg } else { $env:DEVEZCODE_ROOM_ID }
                  if (-not $room) { exit 0 }
                  $room = $room -replace '[^\w\-]', ''
                  $dir = Join-Path $env:APPDATA 'DevezCode\claude\busy'
                  New-Item -ItemType Directory -Force -Path $dir | Out-Null
                  $busyFile = Join-Path $dir ($room + '.txt')
                  $wdir = Join-Path $env:APPDATA 'DevezCode\claude\waiting'
                  New-Item -ItemType Directory -Force -Path $wdir | Out-Null
                  $waitFile = Join-Path $wdir ($room + '.txt')
                  $runDir = Join-Path (Join-Path $env:APPDATA 'DevezCode\claude\subruns') $room
                  $sdir = Join-Path $dir '_state'
                  $mainFile = Join-Path $sdir ('main_' + $room + '.flag')
                  # 턴 종료 마커: Stop/SessionEnd(진짜 메인 턴 종료)에서만 생성 → C#(MainWindow) 가 완료카드 발행 근거로 소비.
                  # main 플래그 유실 시 substop 드레인이 만드는 순간 idle(flap)로는 카드가 안 찍히게 하는 게이트.
                  $doneFile = Join-Path (Join-Path $env:APPDATA 'DevezCode\claude\done') ($room + '.txt')
                  # 같은 agent_id가 resume될 수 있으므로 done 존재만으로 Start를 막으면 안 된다.
                  # 훅 프로세스 생성 시각을 세대로 사용: Stop보다 먼저 생성된 늦은 Start만 폐기한다.
                  $hookStartTicks = [DateTime]::UtcNow.Ticks
                  try { $hookStartTicks = (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks } catch { }

                  function Write-State($path, $value, $encoding = 'Ascii') {
                    try {
                      $tmp = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
                      Set-Content -LiteralPath $tmp -Value $value -Encoding $encoding -Force
                      Move-Item -LiteralPath $tmp -Destination $path -Force
                    } catch { try { Set-Content -LiteralPath $path -Value $value -Encoding $encoding -Force } catch { } }
                  }

                  # 세션 추적 기록. sid 가 바뀔 때 이전 sid 를 .prev 로 보존 — 추적 sid 의 transcript 가
                  # 아직 없을 때(resume 포크 직후 활동 전 재시작 = 유령 sid) 실행부가 .prev 로 복원한다.
                  function Write-Tracked($room, $sid) {
                    $tdir = Join-Path $env:APPDATA 'DevezCode\claude\sessions'
                    New-Item -ItemType Directory -Force -Path $tdir | Out-Null
                    $tfile = Join-Path $tdir ($room + '.txt')
                    $prevSid = ''
                    try { if (Test-Path -LiteralPath $tfile) { $prevSid = (Get-Content -LiteralPath $tfile -Raw -ErrorAction SilentlyContinue).Trim() } } catch { }
                    if ($prevSid -and $sid -ne $prevSid) { Write-State (Join-Path $tdir ($room + '.prev.txt')) $prevSid }
                    Write-State $tfile $sid
                    # known-good 링: 이 함수는 running/idle(실활동) 에서만 호출되므로 $sid 는 transcript 가
                    # 보장된 '살아있는' 세션이다. 방별 최근 good sid 8개를 유지 — tracked·prev 가 동시에 유령이 되는
                    # (연속 무활동 재시작) 최악 경우에도 실행부가 이 링에서 실제 대화를 복원한다. prev(1단) 보다 깊다.
                    try {
                      $gfile = Join-Path $tdir ($room + '.good.txt')
                      $good = @()
                      try { if (Test-Path -LiteralPath $gfile) { $good = @(Get-Content -LiteralPath $gfile -ErrorAction SilentlyContinue | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } } catch { }
                      $good = @($good | Where-Object { $_ -ne $sid }) + $sid
                      if ($good.Count -gt 8) { $good = $good[($good.Count-8)..($good.Count-1)] }
                      Write-State $gfile ($good -join "`n")
                    } catch { }
                  }

                  function Touch-LiveSubruns($rd) {
                    try {
                      if (-not (Test-Path -LiteralPath $rd)) { return }
                      $cut = (Get-Date).AddHours(-1)
                      foreach ($f in @(Get-ChildItem -LiteralPath $rd -Filter '*.run' -ErrorAction SilentlyContinue)) {
                        if ($f.LastWriteTime -lt $cut) { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue }
                        # .run content is the immutable Start generation. Refresh only mtime.
                        else { [IO.File]::SetLastWriteTimeUtc($f.FullName, [DateTime]::UtcNow) }
                      }
                    } catch { }
                  }

                  # 살아있는 서브에이전트 수. 1시간 초과 stale run 파일(SubagentStop 누락분)은 prune 후 제외.
                  function Get-LiveSubCount($rd) {
                    try {
                      if (-not (Test-Path -LiteralPath $rd)) { return 0 }
                      $cut = (Get-Date).AddHours(-1)
                      $live = 0
                      foreach ($f in @(Get-ChildItem -LiteralPath $rd -Filter '*.run' -ErrorAction SilentlyContinue)) {
                        if ($f.LastWriteTime -lt $cut) { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue }
                        else { $live++ }
                      }
                      return $live
                    } catch { return 0 }
                  }

                  function Test-AgentCompleted($donePath, [long]$startTicks) {
                    try {
                      if (-not (Test-Path -LiteralPath $donePath)) { return $false }
                      $doneTicks = [long]0
                      $rawDone = (Get-Content -LiteralPath $donePath -Raw -ErrorAction SilentlyContinue).Trim()
                      if (-not [long]::TryParse($rawDone, [ref]$doneTicks)) {
                        Remove-Item -LiteralPath $donePath -Force -ErrorAction SilentlyContinue
                        return $false
                      }
                      if ($doneTicks -ge $startTicks) { return $true }
                      # Stop 뒤 새로 생성된 같은 agent_id의 resume Start: 이전 세대 fence 해제.
                      Remove-Item -LiteralPath $donePath -Force -ErrorAction SilentlyContinue
                    } catch { }
                    return $false
                  }

                  # Start/Stop for the same agent_id are separate hook processes and can finish in
                  # either order. Serialize their run/done transition; process start ticks then tell
                  # an old delayed Stop from a legitimate Stop for the current resumed generation.
                  $agentMutex = $null
                  $agentMutexHeld = $false
                  function Enter-AgentLock([string]$agentId) {
                    try {
                      $script:agentMutex = New-Object System.Threading.Mutex($false,
                        ('Local\DevezCode.ClaudeSub.' + $room + '.' + $agentId))
                      try {
                        $script:agentMutexHeld = $script:agentMutex.WaitOne(10000)
                      } catch [System.Threading.AbandonedMutexException] {
                        $script:agentMutexHeld = $true
                      }
                      return $script:agentMutexHeld
                    } catch {
                      $script:agentMutexHeld = $false
                      return $false
                    }
                  }

                  function Exit-AgentLock {
                    try {
                      if ($script:agentMutexHeld -and $script:agentMutex) { $script:agentMutex.ReleaseMutex() }
                    } catch { }
                    try { if ($script:agentMutex) { $script:agentMutex.Dispose() } } catch { }
                    $script:agentMutexHeld = $false
                    $script:agentMutex = $null
                  }

                  # Hook runners may still be writing stdin when a status-only branch exits.
                  # Always own/read the pipe through EOF first (also supplies agent_id below).
                  $raw = ''
                  try { $raw = [System.IO.StreamReader]::new([Console]::OpenStandardInput()).ReadToEnd() } catch { }
                  $j = $null
                  try { $j = $raw | ConvertFrom-Json } catch { }

                  # 입력 대기 ❗ 진입 신호.
                  #  • notify(PermissionRequest / PreToolUse AskUserQuestion): 실제 권한·선택지 → 서브 실행중이라도 항상 무장.
                  #  • notifyidle(Notification): notification_type 으로 분기.
                  #      permission_prompt / agent_needs_input / elicitation_dialog = 진짜 유저 블로킹 → 서브 유무·busy 와 무관 무장.
                  #      idle_prompt 등 소프트 알림 = 살아있는 서브>0 이면 스킵(서브 완료 대기 중 60초 idle 오탐 방지),
                  #        busy=running 일 때만 무장(완전 idle 알림 제외).
                  #      agent_completed / auth_success / elicitation_complete|response = 무장 안 함.
                  # 두 번째 연속 선택지도 매번 재무장.
                  if ($status -eq 'notify' -or $status -eq 'notifyidle') {
                    $nType = ''
                    try { $nType = ('' + $j.notification_type).Trim().ToLowerInvariant() } catch { }
                    $hardNeed = $status -eq 'notify' -or $nType -in @('permission_prompt','agent_needs_input','elicitation_dialog')
                    $ignoreNeed = $nType -in @('agent_completed','auth_success','elicitation_complete','elicitation_response')
                    if ($ignoreNeed) { exit 0 }
                    if (-not $hardNeed) {
                      # 소프트 알림(idle_prompt 등): 서브 생존 중이면 메인 유저대기 오탐 → 스킵.
                      if ((Get-LiveSubCount $runDir) -gt 0) { exit 0 }
                      $b = ''; try { if (Test-Path -LiteralPath $busyFile) { $b = (Get-Content -LiteralPath $busyFile -Raw -ErrorAction SilentlyContinue).Trim() } } catch { }
                      if ($b -ne 'running') { exit 0 }
                    }
                    $waitValue = if ($hardNeed) { 'permission' } else { 'input' }
                    Write-State $waitFile $waitValue
                    exit 0
                  }
                  # PostToolUse 등 = 답변 처리 재개 → 선택지 대기 해제.
                  # 서브에이전트 훅에도 같은 room 설정이 붙으므로, 서브 툴 완료 unwait 가 메인 권한/선택지 ❗ 을
                  # 지울 수 있다. 메인 턴이 살아 있으면(main 플래그) 서브 unwait 는 무시해 메인 대기를 보호.
                  # 메인 턴이 이미 끝난 뒤 서브만 남는 구간에서는 서브 unwait 를 허용해, 서브 권한 승인 후 ❗ 고착을 막는다.
                  if ($status -eq 'unwait') {
                    $aid = ''; try { $aid = ('' + $j.agent_id) -replace '[^\w\-]', '' } catch { }
                    if ($aid -and (Test-Path -LiteralPath $mainFile)) { exit 0 }
                    Touch-LiveSubruns $runDir
                    Write-State $waitFile 'idle'
                    exit 0
                  }
                  if ($status -eq 'pulse') {
                    Touch-LiveSubruns $runDir
                    exit 0
                  }

                  # ── SubagentStart: run 파일 생성 → 즉시 busy=running (agent_id 로 개별 추적) ──
                  if ($status -eq 'substart') {
                    $aid = ''; try { $aid = ('' + $j.agent_id) -replace '[^\w\-]', '' } catch { }
                    if ($aid -and (Enter-AgentLock $aid)) {
                      try {
                        New-Item -ItemType Directory -Force -Path $runDir | Out-Null
                        $agentRun = Join-Path $runDir ($aid + '.run')
                        $agentDone = Join-Path $runDir ($aid + '.done')
                        # Stop 프로세스가 나중 세대면 이 지연 Start는 폐기. 더 오래된 done이면
                        # same agent_id resume이므로 Test-AgentCompleted가 fence를 제거하고 허용한다.
                        if (-not (Test-AgentCompleted $agentDone $hookStartTicks)) {
                          Write-State $agentRun ([string]$hookStartTicks)
                          Write-State $busyFile 'running'
                        } elseif ((Test-Path -LiteralPath $mainFile) -or ((Get-LiveSubCount $runDir) -gt 0)) {
                          Write-State $busyFile 'running'
                        } else {
                          Write-State $busyFile 'idle'
                        }
                      } finally {
                        Exit-AgentLock
                      }
                    }
                    exit 0
                  }
                  # ── SubagentStop: run 파일 삭제 → (메인 진행중 OR 남은 서브>0)로 busy 재평가 ──
                  if ($status -eq 'substop') {
                    $aid = ''; try { $aid = ('' + $j.agent_id) -replace '[^\w\-]', '' } catch { }
                    if ($aid -and (Enter-AgentLock $aid)) {
                      try {
                        New-Item -ItemType Directory -Force -Path $runDir | Out-Null
                        $agentRun = Join-Path $runDir ($aid + '.run')
                        $runTicks = [long]0
                        try {
                          if (Test-Path -LiteralPath $agentRun) {
                            $rawRun = (Get-Content -LiteralPath $agentRun -Raw -ErrorAction SilentlyContinue).Trim()
                            [void][long]::TryParse($rawRun, [ref]$runTicks)
                          }
                        } catch { $runTicks = [long]0 }
                        if ($runTicks -le $hookStartTicks) {
                          # done을 먼저 남겨 아직 기동 중인 이전 Start도 완료 사실을 보게 한다.
                          Write-State (Join-Path $runDir ($aid + '.done')) ([string]$hookStartTicks)
                          Remove-Item -LiteralPath $agentRun -Force -ErrorAction SilentlyContinue
                        }
                        # runTicks > Stop process start means this is an old delayed Stop; the
                        # same agent_id has already resumed, so preserve the newer generation.
                      } finally {
                        Exit-AgentLock
                      }
                    }
                    if ((Test-Path -LiteralPath $mainFile) -or ((Get-LiveSubCount $runDir) -gt 0)) {
                      Write-State $busyFile 'running'
                    } else {
                      Write-State $busyFile 'idle'
                    }
                    exit 0
                  }

                  $sid = ''; try { $sid = ('' + $j.session_id) -replace '[^\w\-]', '' } catch { }
                  $tp = '';  try { $tp = '' + $j.transcript_path } catch { }
                  # session_id 가 비면(claude stdin 포맷/필드명 변경 대비) transcript_path 파일명(<sid>.jsonl)에서 복구한다.
                  if (-not $sid -and $tp) { try { $sid = ([System.IO.Path]::GetFileNameWithoutExtension($tp)) -replace '[^\w\-]', '' } catch { } }

                  if ($status -eq 'running') {
                    New-Item -ItemType Directory -Force -Path $sdir | Out-Null
                    Set-Content -LiteralPath $mainFile -Value 'running' -Encoding Ascii -Force  # 메인 턴 진행중 마킹
                    # 새 턴 시작 → 미소비 턴종료 마커 무효화. 소비 안 된 옛 마커가 이번 턴 중간 flap idle 에
                    # 오발행되는 것을 막는다(디바운스 취소와 같은 의미 — Stop 직후 1.2s 내 재프롬프트 시 카드 없음도 기존과 동일).
                    Remove-Item -LiteralPath $doneFile -Force -ErrorAction SilentlyContinue
                    Write-State $busyFile 'running'
                    Write-State $waitFile 'idle'
                    $prompt = ''
                    try { $prompt = '' + $j.prompt } catch { }
                    # <task-notification> 재주입은 세션추적/헤더 제외. 그 외엔 프롬프트가 비어도(이미지·슬래시 커맨드)
                    # 세션추적은 확정한다 — 새 세션 첫 턴이 여기서만 앵커되므로 프롬프트 유무로 유실되면 안 됨.
                    if ($sid -and -not $prompt.StartsWith('<task-notification>')) {
                      # 사용자가 실제로 메시지를 보낸 세션 = 이 방의 진짜 현재 대화. 추적파일에 확정 기록(resume 용).
                      # tmp+교체(원자적) 쓰기 — 강제종료가 이 순간 끼어들어도 파일이 잘려서 세션을 통째로
                      # 잃는 일이 없도록 한다(직접 Set-Content 는 쓰기 도중 끊기면 손상/빈 파일이 남는다).
                      Write-Tracked $room $sid
                    }
                    # 헤더 lastmsg 는 실제 텍스트 프롬프트가 있을 때만 기록.
                    if ($prompt -and -not $prompt.StartsWith('<task-notification>')) {
                      $prompt = ($prompt -replace '\s+', ' ').Trim()
                      if ($prompt.Length -gt 200) { $prompt = $prompt.Substring(0, 200) }
                      $mdir = Join-Path $env:APPDATA 'DevezCode\claude\lastmsg'
                      New-Item -ItemType Directory -Force -Path $mdir | Out-Null
                      Set-Content -LiteralPath (Join-Path $mdir ($room + '.txt')) -Value $prompt -Encoding UTF8 -Force
                    }
                    exit 0
                  }

                  # status = idle (Stop/SessionEnd) — 메인 턴 종료. 선택지 대기 해제 + main 플래그 clear.
                  Write-State $waitFile 'idle'
                  Remove-Item -LiteralPath $mainFile -Force -ErrorAction SilentlyContinue
                  # 진짜 턴 종료 증표(완료카드 게이트). busy 'idle' 쓰기보다 먼저 남겨 C# 디바운스가 항상 마커를 본다.
                  # Stop 시 살아있는 서브가 남아 busy 가 running 을 유지해도 마커는 남는다 → 드레인 완료 idle 에서 소비돼 카드 1장.
                  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $doneFile) | Out-Null
                  Write-State $doneFile ([string][DateTime]::UtcNow.Ticks)
                  # 응답 완료 시에도 현재 세션을 추적에 확정 기록 — running 훅을 놓쳤거나(경합) 첫 프롬프트가
                  # 비었어도(이미지·슬래시) 완결된 대화가 재실행 때 새 세션으로 유실되는 것을 막는 최종 앵커.
                  if ($sid) {
                    Write-Tracked $room $sid
                  }
                  $evt = ''; try { $evt = '' + $j.hook_event_name } catch { }
                  if ($evt -eq 'SessionEnd') {
                    # 세션 종료 → 이 방 서브에이전트도 모두 소멸.
                    try { if (Test-Path -LiteralPath $runDir) { Remove-Item -LiteralPath $runDir -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
                    Write-State $busyFile 'idle'
                    exit 0
                  }
                  # Stop: 살아있는 서브가 있으면 running 유지(스피너 조기소멸 방지), 없으면 idle + 마지막 답변 기록.
                  if ((Get-LiveSubCount $runDir) -gt 0) {
                    Write-State $busyFile 'running'
                  } else {
                    Write-State $busyFile 'idle'
                    # 진짜 응답 완료 → claude 가 stdin 으로 준 마지막 답변을 방별로 기록(lastreply).
                    try {
                      $lastMsg = '' + $j.last_assistant_message
                      if ($lastMsg) {
                        $rdir = Join-Path $env:APPDATA 'DevezCode\claude\lastreply'
                        New-Item -ItemType Directory -Force -Path $rdir | Out-Null
                        Set-Content -LiteralPath (Join-Path $rdir ($room + '.txt')) -Value $lastMsg -Encoding UTF8 -Force
                      }
                    } catch { }
                  }
                } catch { }
                exit 0
                """;
            // A hot-patched hook may be temporarily read-only while an older DevezCode process is
            // still alive, preventing that loaded binary from restoring its embedded old script.
            // The first rebuilt/new process owns the current source and safely removes that guard.
            if (File.Exists(BusyHookScriptPath))
            {
                var attributes = File.GetAttributes(BusyHookScriptPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(BusyHookScriptPath, attributes & ~FileAttributes.ReadOnly);
            }
            ScriptFile.WritePs1(BusyHookScriptPath, busyScript);
        }
        catch (Exception) { /* 추적 실패해도 claude 실행은 계속 — flags 에서 파일 존재 확인 */ }
    }

    /// <summary>외부 세션이 claude 를 이어갈 때도 방별 busy/lastmsg/waiting 훅이 계속 기록하도록
    /// 넘길 방별 --settings 파일 경로. 아직 생성 전이면 null(내부 실행이 한 번이라도 있었으면 존재).</summary>
    public static string? GetClaudeRoomSettingsPath(string roomId)
    {
        var path = RoomSettingsPath(roomId);
        return File.Exists(path) ? path : null;
    }

    /// <summary>방별 claude --settings 파일을 생성하고 경로를 반환한다.
    /// 각 hook/statusLine command 에 roomId 를 인자로 박아, claude 가 부모 환경변수
    /// (DEVEZCODE_ROOM_ID)를 자식 hook 프로세스에 넘기지 못하는 환경에서도 어느 방인지 확실히 알게 한다.
    /// (공통 settings + env 의존 방식은 claude 버전/타이밍에 따라 추적이 끊겼다 — 인자 전달로 견고화.)</summary>
    private static string BuildRoomSettings(string roomId)
    {
        EnsureSessionHookAssets(); // 공통 스크립트 보장
        var arg = SafeRoomFileName(roomId); // 영숫자/-/_ 만 → 공백·특수문자 없음(명령 인자 안전)

        const string powershellHook = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden";
        var command         = $"{powershellHook} -File \"{HookScriptPath}\" {arg}";
        // statusLine node 는 hidden PowerShell proxy 가 CreateNoWindow 로 실행한다. node.exe 직접 command 는
        // Claude 실행 환경에 따라 별도 콘솔 창이 순간 표시될 수 있어 사용하지 않는다.
        var statusCommand = UserStatusLineInstaller.TryBuildHiddenNodeCommand(RoomStatusLineJsPath, arg)
            ?? $"{powershellHook} -File \"{StatusLineScriptPath}\" {arg}";
        var busyRunCommand  = $"{powershellHook} -File \"{BusyHookScriptPath}\" running {arg}";
        var busyIdleCommand = $"{powershellHook} -File \"{BusyHookScriptPath}\" idle {arg}";
        var busyNotifyCommand = $"{powershellHook} -File \"{BusyHookScriptPath}\" notify {arg}";
        var busyNotifyIdleCommand = $"{powershellHook} -File \"{BusyHookScriptPath}\" notifyidle {arg}";
        var busySubStartCommand = $"{powershellHook} -File \"{BusyHookScriptPath}\" substart {arg}";
        var busySubStopCommand  = $"{powershellHook} -File \"{BusyHookScriptPath}\" substop {arg}";
        // 응답 대기(❗/🔒) 해제. cmd /c echo 는 별도 콘솔 창이 순간 표시될 수 있으므로
        // 기존 hidden PowerShell busy hook 의 unwait 분기를 재사용한다.
        var waitDir             = Path.Combine(ClaudeTrackDir, "waiting");
        try { Directory.CreateDirectory(waitDir); } catch { /* SessionBusyService 도 생성 — 경합 무시 */ }
        var busyUnwaitCommand   = $"{powershellHook} -File \"{BusyHookScriptPath}\" unwait {arg}";
        // refreshInterval: 전역 settings 와 달리 room 은 event-driven 만으로는 1회 렌더 실패(느린 시작/타임아웃)
        // 시 빈 줄이 고착됐다(resume 세션 statusLine 안 뜨던 원인). 주기 재렌더로 자동 복구한다.
        // 3초 주기 절충: 빈 줄 자동 복구는 유지하되 다세션 idle 부하를 낮춘다(타임아웃이 없어
        // 느린 렌더도 죽지 않으므로 공격적 주기 불필요).
        var statusLine = new { type = "command", command = statusCommand, refreshInterval = 3000 };
        var hooks = new Dictionary<string, object>
        {
            ["SessionStart"] = new[] { new { hooks = new[] { new { type = "command", command } } } },
            ["UserPromptSubmit"] = new[] { new { hooks = new[] { new { type = "command", command = busyRunCommand } } } },
            ["Stop"] = new[] { new { hooks = new[] { new { type = "command", command = busyIdleCommand } } } },
            ["SessionEnd"] = new[] { new { hooks = new[] { new { type = "command", command = busyIdleCommand } } } },
            ["Notification"] = new[] { new { hooks = new[] { new { type = "command", command = busyNotifyIdleCommand } } } },
            ["PreToolUse"] = new[] { new { matcher = "AskUserQuestion", hooks = new[] { new { type = "command", command = busyNotifyCommand } } } },
            ["PostToolUse"] = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command = busyUnwaitCommand } } } },
        };

        // 알 수 없는 이벤트가 settings 전체를 거부하는 구버전을 위해 도입 버전별로만 추가한다.
        // 버전 판별 실패 시 핵심 훅만 남겨 세션 자체는 항상 실행 가능하게 한다.
        var claudeVersion = InstalledClaudeVersion.Value;
        // PostToolUseFailure의 정확한 1.x 도입점은 보장되지 않으므로 2.x부터만 사용한다.
        if (claudeVersion >= new Version(2, 0, 0))
            hooks["PostToolUseFailure"] = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command = busyUnwaitCommand } } } };
        if (claudeVersion >= new Version(1, 0, 41))
            hooks["SubagentStop"] = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command = busySubStopCommand } } } };
        if (claudeVersion >= new Version(2, 0, 43))
            hooks["SubagentStart"] = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command = busySubStartCommand } } } };
        if (claudeVersion >= new Version(2, 0, 45))
            hooks["PermissionRequest"] = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command = busyNotifyCommand } } } };
        if (claudeVersion >= new Version(2, 1, 78))
            hooks["StopFailure"] = new[] { new { hooks = new[] { new { type = "command", command = busyIdleCommand } } } };

        var settings = new
        {
            // 세션 기록 보존 기간 — 마지막 활동일부터 이 일수가 지나면 claude 가 트랜스크립트를 자동 삭제(resume 불가).
            // 방은 오래 두고 다시 여는 물건이라 90일로 넉넉히(30일이었을 땐 한 달 방치한 방 대화가 증발했다). 비용은 텍스트 디스크뿐.
            cleanupPeriodDays = 90,
            // theme 을 command-line scope(최우선)에 박아 auto(배경 자동감지) 경로를 제거 — ConPTY 에서 흰 화면 고착 방지.
            theme = ClaudeCustomThemes.MapToClaudeTheme(DevezCode.App.CurrentTheme),
            statusLine,
            hooks,
        };
        var path = RoomSettingsPath(roomId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(settings));
        }
        catch (Exception) { /* 생성 실패 시 호출부가 파일 존재로 판단 */ }
        return path;
    }

    private static Version? DetectInstalledClaudeVersion()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /s /c \"claude --version\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (!output.Wait(300)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(output.Result, @"\d+(?:\.\d+){1,3}");
            return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
        }
        catch { return null; }
    }

    /// <summary>방의 추적 세션 파일(sessions\<room>.txt)을 제거. 빈 세션 ID 고착을 풀 때 호출.</summary>
    private static void DeleteTrackedSessionFile(string roomId)
    {
        try { File.Delete(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".txt")); }
        catch (Exception) { }
        // 고착 해제 = 이 방을 새 세션으로 리셋하려는 의도 — prev·good 를 남기면 실행부 폴백이 옛 대화를 되살린다.
        try { File.Delete(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".prev.txt")); }
        catch (Exception) { }
        try { File.Delete(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".good.txt")); }
        catch (Exception) { }
    }

    /// <summary>방이 현재 가리키는 CLI 세션 ID(에이전트별 추적값 우선, 없으면 settings 스냅샷).
    /// 세션ID 복사·진단용. 아직 대화가 없으면 null.</summary>
    public static string? CurrentSessionId(string roomId, string agentId)
    {
        var sid = agentId switch
        {
            "claude"      => LoadTrackedSessionId(roomId) ?? SettingsService.LoadClaudeCodeRoomSession(roomId),
            "opencode"    => OpenCodePluginInstaller.LoadTrackedSessionId(roomId) ?? SettingsService.LoadOpenCodeRoomSession(roomId),
            "gajae"       => FindLatestGajaeSessionId(GajaeSessionDir(roomId)) ?? SettingsService.LoadGajaeRoomSession(roomId),
            "codex"       => SettingsService.LoadCodexRoomSession(roomId),
            "grok"        => GrokHookService.LoadTrackedSessionId(roomId) ?? SettingsService.LoadGrokRoomSession(roomId),
            "kimi"        => KimiHookService.LoadTrackedSessionId(roomId) ?? SettingsService.LoadKimiRoomSession(roomId),
            "antigravity" => AntigravityHookService.LoadTrackedSessionId(roomId) ?? SettingsService.LoadAntigravityRoomSession(roomId),
            // dvz 추적값은 백엔드 접두사(claude:)가 붙을 수 있다 — 붙은 채로는 어디에도 못 쓰므로 벗겨서 준다.
            "devezvibe"   => DevezVibeStateService.LoadTrackedSessionId(roomId)
                             ?? SettingsService.LoadDevezVibeRoomSession(roomId),
            _             => null,
        };
        if (string.IsNullOrWhiteSpace(sid)) return null;
        return agentId == "devezvibe" ? DevezVibeStateService.StripBackendPrefix(sid!.Trim()) : sid!.Trim();
    }

    /// <summary>훅이 기록한 방의 최신 세션 ID. 없거나 UUID가 아니면 null.</summary>
    private static string? LoadTrackedSessionId(string roomId)
    {
        try
        {
            var path = Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".txt");
            if (!File.Exists(path)) return null;
            var id = File.ReadAllText(path).Trim();
            return Guid.TryParse(id, out _) ? id.ToLowerInvariant() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>추적 sid 가 바뀌기 직전의 이전 sid(sessions\&lt;room&gt;.prev.txt, 훅 Write-Tracked 가 보존).
    /// resume 은 새 sid 로 포크하지만 새 .jsonl 은 첫 활동 전까지 안 생기므로(lazy), 그 사이 재시작하면
    /// 추적이 transcript 없는 유령 sid 가 된다 — 이때 이전 대화로 복원하는 폴백 체인용.</summary>
    private static string? LoadPrevTrackedSessionId(string roomId)
    {
        try
        {
            var path = Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".prev.txt");
            if (!File.Exists(path)) return null;
            var id = File.ReadAllText(path).Trim();
            return Guid.TryParse(id, out _) ? id.ToLowerInvariant() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>훅이 실활동(running/idle) 시점에 적재한 방별 known-good sid 링(sessions\&lt;room&gt;.good.txt,
    /// 최근 8개, 최신이 마지막 줄). tracked·prev 가 모두 유령(연속 무활동 재시작)일 때 살아있는 transcript 를
    /// 가진 최신 good sid 로 복원하는 최종 폴백용. 방 단위 파일이라 cwd 공유 프로젝트에서도 다른 방 대화를 안 끌어온다.</summary>
    private static List<string> LoadGoodTrackedSessionIds(string roomId)
    {
        try
        {
            var path = Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".good.txt");
            if (!File.Exists(path)) return new List<string>();
            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => Guid.TryParse(l, out _))
                .Select(l => l.ToLowerInvariant())
                .ToList();
        }
        catch (Exception) { return new List<string>(); }
    }

    /// <summary>해당 세션 ID의 claude 대화 transcript(.jsonl) 실제 경로. 없으면 null.
    /// claude 는 대화를 %USERPROFILE%\.claude\projects\&lt;경로 인코딩&gt;\&lt;세션ID&gt;.jsonl 로 저장한다.
    /// 1) workingDir 인코딩으로 바로 확인(빠른 경로, 대부분 적중)
    /// 2) 실패하면 projects 하위 폴더 전체에서 &lt;sid&gt;.jsonl 을 검색한다 — working dir 이동/이름변경,
    ///    경로 인코딩 엣지(UNC·네트워크 드라이브 등), claude 의 인코딩 규칙 변경에도 sessionId(GUID 는 projects
    ///    전역에서 유일)로 정확히 찾아낸다. (예전엔 인코딩이 어긋나면 "대화 없음"으로 오판해 멀쩡한 세션
    ///    추적을 폐기 → 재실행 시 그 대화에 영영 못 붙었다.)</summary>
    /// <summary>주어진 cwd 에 대응하는 claude 프로젝트 폴더(%USERPROFILE%\.claude\projects\&lt;인코딩&gt;).
    /// claude 는 cwd 절대경로의 비영숫자를 '-' 로 바꾼 이름으로 대화를 저장한다. workingDir 없으면 null.</summary>
    private static string? ClaudeProjectDir(string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(workingDir)) return null;
        var projects = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
        var full = Path.GetFullPath(workingDir);
        if (full.Length > 3) full = full.TrimEnd('\\', '/'); // 드라이브 루트(C:\)는 백슬래시 유지 — claude 인코딩(C--)과 일치
        var encoded = System.Text.RegularExpressions.Regex.Replace(full, "[^a-zA-Z0-9]", "-");
        return Path.Combine(projects, encoded);
    }

    public static string? FindClaudeTranscriptPath(string? workingDir, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !Guid.TryParse(sessionId, out _)) return null;
        try
        {
            var projects = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");
            if (!Directory.Exists(projects)) return null;
            var file = sessionId + ".jsonl";

            // 1) 빠른 경로: working dir 인코딩으로 바로 확인.
            var pdir = ClaudeProjectDir(workingDir);
            if (pdir != null)
            {
                var fast = Path.Combine(pdir, file);
                if (File.Exists(fast)) return fast;
            }

            // 2) 폴백: 빠른 경로가 빗나갔을 때만 projects 하위 폴더 전체를 스캔(폴더 변경/인코딩 무관).
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var p = Path.Combine(dir, file);
                if (File.Exists(p)) return p;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>주어진 세션 ID의 claude transcript 가 디스크에 존재하는지(폴더 변경/인코딩 무관).</summary>
    private static bool ClaudeTranscriptExists(string? workingDir, string? sessionId)
        => FindClaudeTranscriptPath(workingDir, sessionId) != null;

    /// <summary>채팅방 삭제 시 호출 — 해당 방의 셸 프로세스 정리.
    /// <paramref name="purgeTracking"/> 이 false 면 추적 파일(sessions/*.txt, launch batch)을 보존한다.
    /// 재시작 경로에서 resume 에 필요한 session id 추적 파일이 지워지면 대화가 날아가므로 보존.</summary>
    /// <summary>닫을 세션이 하나라도 등록돼 있는지(살았든 죽었든) — 종료 시 안전종료 오버레이 + graceful 정리 판단.
    /// 죽은 세션도 graceful 경로에서 훅 기록 flush·Dispose 를 거치므로, IsAlive 가 아니라 세션 등록 유무로 본다.
    /// (이전엔 IsAlive 기준이라, 죽은 탭만 남으면 — 예: 터미널에서 exit·크래시·외부 kill — 안전종료가 통째로
    /// 스킵돼 그냥 꺼졌다. ConPTY 가 아직 안 만들어진 세션(_sessions 미등록)은 닫을 프로세스가 없어 제외.)</summary>
    public bool HasSessionsToClose()
    {
        lock (_lock) return _sessions.Count > 0;
    }

    /// <summary>앱 종료 시: 일반 입력을 먼저 동결한 뒤 모든 세션에 에이전트별 종료 제어키만 보내
    /// transcript 를 flush 하고 Stop/SessionEnd 훅을 기록할 틈을 준 뒤, 잔여를 Dispose 로 하드 정리한다.
    /// 병렬 처리라 벽시계 시간은 가장 느린 세션 1개 기준(perGraceMs).
    /// WaitForExit 기반이라 빠르게 끝나는 세션은 즉시 통과 — timeout 을 키워도 정상 종료는 안 느려진다.
    /// 에이전트 프로세스가 죽은 직후에도 훅(별도 powershell)이 디스크에 마저 쓰는 텀이 있어,
    /// conpty 를 닫기 전 postFlushMs 만큼 추가로 기다려 종료 시 훅 기록 누락을 막는다.
    /// 특히 "/clear 직후 요청 → 받자마자 종료" 처럼 새 세션이 transcript(.jsonl)를 미처 다 쓰기
    /// 전에 종료 신호를 받으면, WaitForExit 가 빨리 통과해버려 그 세션을 못 불러왔다 →
    /// 프로세스가 빨리 죽어도 무조건 postFlushMs(기본 3s) 만큼은 기다려 기록을 보존한다.</summary>
    public async Task GracefulShutdownAllAsync(int perGraceMs = 2500, int postFlushMs = 5000)
    {
        // 앱 종료 시작 표시 — TerminalHostView 의 앱레벨 자동 재진입(codex/opencode)이 종료 중
        // 세션 Exited 를 "CLI 가 스스로 끝남"으로 오인해 새 ConPTY 로 resume 재기동하는 것을 막는다.
        // (재기동된 codex 가 곧바로 하드킬되며 세션 추적 ID 를 오염시키는 레이스의 원천 차단.)
        IsShuttingDown = true;
        List<KeyValuePair<string, TerminalSession>> snapshot;
        lock (_lock) snapshot = _sessions.ToList();
        if (snapshot.Count == 0) return;
        foreach (var kv in snapshot) kv.Value.BeginGracefulExit();
        DiagLog.Write($"GracefulShutdownAll: {snapshot.Count} sessions");

        try
        {
            await Task.WhenAll(snapshot.Select(kv =>
            {
                var (controlInput, repeatCount, escFirst) = GracefulExitPlan(kv.Key);
                return kv.Value.TryGracefulExitAsync(perGraceMs, controlInput, repeatCount, escFirst);
            }));
        }
        catch { /* best effort */ }

        // 프로세스 종료 후 별도 훅 프로세스(powershell)가 파일을 마저 쓸 여유.
        // 고정 3s 대기 → 폴링으로 개선: claude 방들의 busy 가 전부 running 이 아니면
        // (= Stop/SessionEnd 훅이 기록을 마침) 짧은 정착 후 조기 종료. 훅이 늦으면 cap(5s)까지
        // 기다려 종전(3s)보다 마진도 커졌다. 판정 불가(claude 방 없음)면 종전과 같은 3s 고정.
        if (postFlushMs > 0) { try { await WaitForHookFlushAsync(snapshot.Select(kv => kv.Key), postFlushMs); } catch { /* best effort */ } }

        // 종료 직전 최종 스냅샷 — 훅/플러그인이 마지막에 남긴 세션 ID(gjc 는 최신 .jsonl)를 settings 에
        // 확정 기록한다. 세션 도중 추적 파일만 갱신되고 settings 반영 전에 앱이 꺼지는 틈을 봉합.
        foreach (var kv in snapshot)
            TrySnapshotRoomSession(kv.Key);

        lock (_lock)
        {
            foreach (var kv in snapshot)
            {
                try { kv.Value.Dispose(); } catch (Exception) { }
                _sessions.Remove(kv.Key);
            }
        }
    }

    /// <summary>종료 훅 flush 대기. claude 방들의 busy 파일이 전부 running 이 아니게 되면
    /// (Stop/SessionEnd 훅이 마지막 기록까지 완료) 500ms 정착 후 반환, 아니면 capMs 까지 대기.
    /// 최소 1.5s 는 무조건 기다린다 — 프로세스가 즉사해도 훅 powershell 스폰·기록 시간이 필요하고,
    /// codex 등 다른 훅 에이전트의 잔여 기록 여유도 겸한다. claude 방이 없으면 종전 동작(3s 고정).</summary>
    private static async Task WaitForHookFlushAsync(IEnumerable<string> roomIds, int capMs)
    {
        var claudeRooms = new List<string>();
        foreach (var roomId in roomIds)
        {
            try
            {
                var agent = AgentRegistry.Find(SettingsService.LoadAgentForRoom(roomId)) ?? AgentRegistry.GetDefault();
                if (agent.Id == "claude") claudeRooms.Add(roomId);
            }
            catch { }
        }
        if (claudeRooms.Count == 0)
        {
            await Task.Delay(Math.Min(capMs, 3000));
            return;
        }

        const int floorMs = 1500, pollMs = 250, settleMs = 500;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(floorMs);
        while (sw.ElapsedMilliseconds < capMs)
        {
            if (claudeRooms.All(r => !IsClaudeBusyRunning(r)))
            {
                await Task.Delay(settleMs); // 파일 flush 정착 여유
                return;
            }
            await Task.Delay(pollMs);
        }
    }

    /// <summary>busy 훅 파일이 'running' 인가. 파일 없음/판독 실패 = 진행 중 턴 없음으로 간주.</summary>
    private static bool IsClaudeBusyRunning(string roomId)
    {
        try
        {
            var path = Path.Combine(ClaudeTrackDir, "busy", SafeRoomFileName(roomId) + ".txt");
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Trim() == "running";
        }
        catch { return false; }
    }

    /// <summary>claude 방이 권한/선택지 입력을 기다리는 중인가('permission'/'input', waiting 훅 파일).
    /// 이 상태에서 Esc 를 보내면 대기 중인 권한창·선택지가 취소되며 원치 않는 자동거부/중단이 날 수
    /// 있어, 종료 시 Esc 시도 여부를 이걸로 가른다(대기 중이면 Esc 생략, Ctrl+D만 시도).</summary>
    private static bool IsClaudeWaitingOnUser(string roomId)
    {
        try
        {
            var path = Path.Combine(ClaudeTrackDir, "waiting", SafeRoomFileName(roomId) + ".txt");
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var v = sr.ReadToEnd().Trim();
            return v == "permission" || v == "input";
        }
        catch { return false; }
    }

    private static string ClaudeQuitFlagPath(string roomId) =>
        Path.Combine(ClaudeTrackDir, "quitting", SafeRoomFileName(roomId) + ".txt");

    /// <summary>종료 시작 전에 방별 "종료중" 플래그를 남긴다. escFirst(Esc→Ctrl+D) 로 claude 가
    /// 예상보다 빨리 정상 종료(errorlevel 0)하면, ClaudeReentryLoop 배치가 Dispose 로 ConPTY 를
    /// 닫기 전 그 틈에 goto __reenter 로 새 claude 를 띄우는 레이스가 있다 — 이 플래그를 배치가
    /// 재진입 직전에 확인해 그 레이스를 막는다.</summary>
    private static void MarkClaudeQuitting(string roomId)
    {
        try
        {
            var path = ClaudeQuitFlagPath(roomId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "1");
        }
        catch { /* best effort — 실패해도 기존 하드킬 폴백이 종료를 보장 */ }
    }

    /// <summary>다음 실행 전에 종료중 플래그를 지운다 — 남아있으면 재시작 때 재진입 루프가
    /// 곧바로 "종료중"으로 오판해 resume 자체를 안 하게 된다.</summary>
    private static void ClearClaudeQuitFlag(string roomId)
    {
        try { File.Delete(ClaudeQuitFlagPath(roomId)); } catch { }
    }

    /// <summary>방의 최신 세션 추적값을 settings 에 확정 기록(종료 스냅샷). best-effort — 실패해도 종료 계속.
    /// claude 는 훅 기록 파일, opencode 는 플러그인 기록 파일, gjc 는 방 session-dir 의 최신 .jsonl 이 원천.</summary>
    private static void TrySnapshotRoomSession(string roomId)
    {
        try
        {
            var agent = AgentRegistry.Find(SettingsService.LoadAgentForRoom(roomId)) ?? AgentRegistry.GetDefault();
            switch (agent.Id)
            {
                case "claude":
                    SyncTrackedClaudeSessionId(roomId);
                    break;
                case "opencode":
                    var oc = OpenCodePluginInstaller.LoadTrackedSessionId(roomId);
                    if (oc != null && oc != SettingsService.LoadOpenCodeRoomSession(roomId))
                        SettingsService.SaveOpenCodeRoomSession(roomId, oc);
                    break;
                case "gajae":
                    var gj = FindLatestGajaeSessionId(GajaeSessionDir(roomId));
                    if (gj != null && gj != SettingsService.LoadGajaeRoomSession(roomId))
                        SettingsService.SaveGajaeRoomSession(roomId, gj);
                    break;
                case "codex":
                    // SessionStart 훅이 sessions\<room>.txt 에 기록한 최신 codex session_id 를 종료 시점에
                    // settings 로 확정 기록. 평시엔 CodexSessionChanged(FileSystemWatcher)가 라이브 저장하지만,
                    // 종료 직전 write 를 워처가 놓치면 stale ID 로 resume("예전 대화가 뜨는") 버그가 나므로 스냅샷으로 보강.
                    var cxDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DevezCode", "codex", "sessions");
                    var cxPath = Path.Combine(cxDir, SafeRoomFileName(roomId) + ".txt");
                    if (File.Exists(cxPath))
                    {
                        var cx = File.ReadAllText(cxPath).Trim();
                        if (Guid.TryParse(cx, out _) && FindCodexTranscriptPath(cx) != null
                            && cx != SettingsService.LoadCodexRoomSession(roomId))
                            SettingsService.SaveCodexRoomSession(roomId, cx);
                    }
                    break;
                case "devezvibe":
                    // dvz 가 sessions\<room>.txt 에 쓴 최신 thread ID 를 종료 시점에 확정 저장. 평시엔
                    // SessionChanged(워처)가 라이브 저장하지만, 종료 직전 write 를 놓치면 stale ID 로
                    // resume 돼 "예전 대화가 뜨는" 버그가 된다(codex 와 같은 유형).
                    var dz = ResolveDevezVibeResumeId(
                        DevezVibeStateService.LoadTrackedSessionId(roomId),
                        SettingsService.LoadClaudeCodeRoomDir(roomId));
                    if (dz != null && dz != SettingsService.LoadDevezVibeRoomSession(roomId))
                        SettingsService.SaveDevezVibeRoomSession(roomId, dz);
                    break;
                case "grok":
                    var gk = GrokHookService.LoadTrackedSessionId(roomId);
                    var gkDir = SettingsService.LoadClaudeCodeRoomDir(roomId);
                    if (gk != null && GrokHookService.IsRootTrackedSession(roomId, gk)
                        && FindGrokChatHistoryPathForWorkingDirectory(gk, gkDir) != null
                        && gk != SettingsService.LoadGrokRoomSession(roomId))
                        SettingsService.SaveGrokRoomSession(roomId, gk);
                    break;
                case "antigravity":
                    var ag = AntigravityHookService.LoadTrackedSessionId(roomId);
                    if (ag != null && AntigravityHookService.IsRootTrackedSession(roomId, ag)
                        && AntigravityConversationExists(ag)
                        && ag != SettingsService.LoadAntigravityRoomSession(roomId))
                        SettingsService.SaveAntigravityRoomSession(roomId, ag);
                    break;
                case "kimi":
                    // SessionStart 훅이 sessions\<room>.txt 에 기록한 최신 kimi session_id 를 종료 시점에 확정 저장.
                    // 평시엔 KimiSessionChanged(워처)가 라이브 저장하지만, 종료 직전 write 를 놓치면 stale ID 로
                    // resume 되는 버그가 나므로 스냅샷으로 보강. 세션 디렉터리 존재까지 검증.
                    var km = KimiHookService.LoadTrackedSessionId(roomId);
                    if (km != null && FindKimiSessionDir(km) != null
                        && km != SettingsService.LoadKimiRoomSession(roomId))
                        SettingsService.SaveKimiRoomSession(roomId, km);
                    break;
            }
        }
        catch (Exception) { /* 스냅샷 실패 — 종료는 계속 */ }
    }

    /// <summary>테마 변경 등 부분 재시작 시: 일반 입력을 동결하고 지정 세션에 에이전트별 종료 제어키만 보내 훅 flush 기회를
    /// 준 뒤 Dispose 한다. GracefulShutdownAllAsync 와 동일 흐름이나 전체가 아닌 지정 room 만 대상.</summary>
    public async Task GracefulDisposeRoomsAsync(IEnumerable<string> roomIds, int perGraceMs = 2500, int postFlushMs = 3000)
    {
        var idSet = new HashSet<string>(roomIds);
        List<KeyValuePair<string, TerminalSession>> snapshot;
        lock (_lock)
        {
            snapshot = _sessions.Where(kv => idSet.Contains(kv.Key)).ToList();
            // 종료 진행 표시 — 이 동안 WireSession/ActivateSession 이 죽어가는 세션에 재부착하지 않게.
            foreach (var kv in snapshot) _gracefulStopping.Add(kv.Key);
        }
        if (snapshot.Count == 0) return;
        foreach (var kv in snapshot) kv.Value.BeginGracefulExit();
        DiagLog.Write($"GracefulDisposeRooms: {string.Join(",", snapshot.Select(kv => kv.Key))}");

        try
        {
            await Task.WhenAll(snapshot.Select(kv =>
            {
                var (controlInput, repeatCount, escFirst) = GracefulExitPlan(kv.Key);
                return kv.Value.TryGracefulExitAsync(perGraceMs, controlInput, repeatCount, escFirst);
            }));
        }
        catch { /* best effort */ }

        if (postFlushMs > 0) { try { await WaitForHookFlushAsync(snapshot.Select(kv => kv.Key), postFlushMs); } catch { /* best effort */ } }

        foreach (var kv in snapshot)
            TrySnapshotRoomSession(kv.Key);

        lock (_lock)
        {
            foreach (var kv in snapshot)
            {
                try { kv.Value.Dispose(); } catch (Exception) { }
                _sessions.Remove(kv.Key);
                _pendingInitial.Remove(kv.Key);
                _disposedRooms.Add(kv.Key); // 이후 뒤늦은 생성 요청 차단(고아 claude 방지)
                _gracefulStopping.Remove(kv.Key); // 종료 완료 — 이후 생성은 새 세션(resume)으로 정상 진행
            }
        }
    }

    /// <summary>에이전트 TUI가 정의한 종료/입력 지우기 제어키 계획. CR/LF·Enter·슬래시 명령·셸 exit 는
    /// 절대 보내지 않는다. 종료키가 모달/셸에 소비돼 정상 종료하지 못하면 timeout 뒤 Job 을 닫는 쪽이
    /// 미완성 초안을 요청으로 제출하는 것보다 안전하다. 배치 재진입형 claude/gajae 는 종료 플래그도 먼저 남긴다.</summary>
    private static (string controlInput, int repeatCount, bool escFirst) GracefulExitPlan(string roomId)
    {
        var agent = SettingsService.LoadAgentForRoom(roomId);
        if (agent == "claude")
        {
            MarkClaudeQuitting(roomId);
            bool escFirst = !IsClaudeWaitingOnUser(roomId) && IsClaudeBusyRunning(roomId);
            // "작업 중 세션이 간헐적으로 멈춤" 조사 계측: 앱이 진행중 턴을 ESC 로 취소하는 지점은 여기뿐.
            // 다음 발생 때 diag.log 에서 이 줄이 있으면 원인=앱 종료/재시작, 없으면 claude 스스로 턴 종료.
            if (escFirst) DiagLog.Write($"GracefulExit: room={roomId} claude busy → ESC(진행중 턴 취소)+Ctrl+D");
            return ("\x04", 1, escFirst); // Claude: Ctrl+D 종료
        }
        if (agent == "codex")
        {
            // 첫 Ctrl+C는 입력 초안을 지우고 두 번째는 종료. busy 면 Esc 로 현재 턴을 먼저 취소한다.
            return ("\x03", 2, IsCodexBusyRunning(roomId));
        }
        if (agent == "grok")
        {
            // Ctrl+Q 두 번은 Grok의 전역 종료키. Esc는 실행 중 무시되고 Ctrl+D는 스크롤과 충돌한다.
            return ("\x11", 2, false);
        }
        if (agent == "devezvibe")
        {
            // dvz 의 Ctrl+C 는 codex 와 같은 의미다 — 초안이 있으면 지우고, 빈 입력이면 종료.
            // busy 면 Esc 로 진행 중 턴을 먼저 중단해야 종료 키가 먹는다.
            return ("\x03", 2, DevezVibeStateService.IsBusyRunning(roomId));
        }
        if (agent == "antigravity")
        {
            return ("\x04", 2, false); // 공식 종료키 Ctrl+D; 두 번 입력해야 하는 상태도 대응
        }
        if (agent == "kimi")
        {
            // Ctrl+D 를 빈 입력에서 두 번 = kimi 종료(더블프레스 확인). busy(스트리밍)면 Esc 로 현재 턴을 먼저 인터럽트.
            return ("\x04", 2, IsKimiBusyRunning(roomId));
        }
        return agent switch
        {
            "opencode" => ("\x03", 2, false), // 첫 Ctrl+C=입력 지우기, 두 번째=종료
            "gajae"    => MarkGajaeAndBuildExitPlan(roomId),
            _          => ("\x03", 2, false), // 미등록 에이전트도 Enter 없는 안전 폴백
        };
    }

    private static (string controlInput, int repeatCount, bool escFirst) MarkGajaeAndBuildExitPlan(string roomId)
    {
        MarkGajaeQuitting(roomId);
        return ("\x04", 1, false); // Ctrl+D → gjc shutdown()이 draft 저장·세션 flush
    }

    /// <summary>codex 훅(busy\&lt;room&gt;.txt)이 'running' 인가 — 종료 시 Esc 선행 여부 판단용.
    /// 파일 없음/판독 실패 = 진행 중 턴 없음으로 간주(Esc 생략).</summary>
    private static bool IsCodexBusyRunning(string roomId)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "codex", "busy", SafeRoomFileName(roomId) + ".txt");
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Trim() == "running";
        }
        catch { return false; }
    }

    /// <summary>kimi 훅(busy\&lt;room&gt;.txt)이 'running' 인가 — 종료 시 Esc 선행 여부 판단용.</summary>
    private static bool IsKimiBusyRunning(string roomId)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "kimi", "busy", SafeRoomFileName(roomId) + ".txt");
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Trim() == "running";
        }
        catch { return false; }
    }

    public void DisposeRoom(string roomId, bool purgeTracking = true)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(roomId, out var s))
            {
                try { s.Dispose(); } catch (Exception) { }
                _sessions.Remove(roomId);
            }
            _pendingInitial.Remove(roomId);
            _disposedRooms.Add(roomId); // 이후 뒤늦은 생성 요청 차단(고아 claude 방지)
        }
        if (purgeTracking)
        {
            // 추적 파일도 정리 (남아있으면 같은 roomId 재사용 시 엉뚱한 세션으로 이어붙음)
            try { File.Delete(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".txt")); }
            catch (Exception) { }
            try { File.Delete(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".prev.txt")); }
            catch (Exception) { }
            try { File.Delete(LaunchBatchPath(roomId)); } catch (Exception) { }
            try { File.Delete(GajaeQuitFlagPath(roomId)); } catch (Exception) { }
        }
    }

    /// <summary>세션 영구 삭제 — 셸 종료(DisposeRoom) + claude 대화 기록(.jsonl)을 디스크에서 제거.
    /// 추적 파일은 DisposeRoom 이 지우므로 세션 ID 후보를 먼저 수집한 뒤 삭제한다.</summary>
    public void PurgeRoom(string roomId, string? workingDir)
    {
        var ids = new List<string?>
        {
            LoadTrackedSessionId(roomId),
            LoadPrevTrackedSessionId(roomId), // resume 포크 직전 세대의 transcript 도 이 방의 기록
            SettingsService.LoadClaudeCodeRoomSession(roomId),
            // known-good 링의 이전 세대 sid 들도 이 방의 대화 → 방 삭제 시 함께 purge
        };
        ids.AddRange(LoadGoodTrackedSessionIds(roomId));
        ids.AddRange(new List<string?>
        {
            SettingsService.LoadCodexRoomSession(roomId),
            SettingsService.LoadOpenCodeRoomSession(roomId),
            SettingsService.LoadGajaeRoomSession(roomId),
            SettingsService.LoadGrokRoomSession(roomId),
            SettingsService.LoadAntigravityRoomSession(roomId),
        });
        // dvz 방의 claude 백엔드 대화도 이 방의 기록 — 후보에서 빠지면 방만 사라지고 transcript 는 고아로 남는다.
        ids.AddRange(new[]
        {
            SettingsService.LoadDevezVibeRoomSession(roomId),
            DevezVibeStateService.LoadTrackedSessionId(roomId),
        }
            .Where(v => !string.IsNullOrWhiteSpace(v)
                && v!.StartsWith("claude:", StringComparison.Ordinal))
            .Select(v => (string?)DevezVibeStateService.StripBackendPrefix(v!)));
        DisposeRoom(roomId);

        PurgeAppOwnedRoomArtifacts(roomId, ids);

        // claude transcript(.jsonl) 삭제 — FindClaudeTranscriptPath 로 위치 확정(빠른 경로 + 전역 스캔 폴백).
        // 예전엔 workingDir 인코딩을 직접 계산해 그 폴더만 지웠는데, 폴더 이동/인코딩 엣지면 못 지워
        // 대화가 디스크에 잔존했다. GUID 는 projects 전역에서 유일하므로 스캔 결과가 곧 이 방의 기록.
        // (GUID 검증은 FindClaudeTranscriptPath 내부에서 수행 — opencode ses_* 등은 null 로 걸러짐.)
        //
        // 오삭제 방어: cwd 를 공유하는 프로젝트에서 resume 로 sid 가 갈리면, 삭제 대상 방의 과거 tracked/prev/
        // good/settings sid 이력에 '지금 살아있는 다른 방'의 세션 ID가 섞여 있을 수 있다. 그대로 전역 삭제하면
        // 멀쩡한 방의 대화가 통째로 날아간다(실제 관측된 데이터 유실 원인). 다른 방이 현재 참조 중인 sid 는
        // 삭제에서 제외한다 — 남겨도 claude 는 영구보관(cleanupPeriodDays=99999)이라 무해하다.
        var otherRoomSids = SettingsService.ClaudeSessionIdsExcept(roomId);
        otherRoomSids.UnionWith(CollectOtherRoomTrackedSids(roomId));
        // dvz 방이 claude 백엔드로 이어가는 sid 는 claude 추적 파일·설정 어디에도 안 남는다(dvz 전용 저장소).
        // 위 두 집합만으로는 살아있는 dvz 방 대화가 방어 대상에서 통째로 빠진다 — 클리너 보호 목록과 같게 맞춘다.
        otherRoomSids.UnionWith(SettingsService.DevezVibeClaudeSessionIdsExcept(roomId));
        otherRoomSids.UnionWith(DevezVibeStateService.ClaudeBackedSessionIdsExcept(roomId));
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (otherRoomSids.Contains(id.ToLowerInvariant()))
            {
                DiagLog.Write($"purge[{roomId}]: sid={id} 는 다른 방이 참조 중 → transcript 삭제 스킵(오삭제 방지)");
                continue;
            }
            var tp = FindClaudeTranscriptPath(workingDir, id);
            if (tp != null)
                try { File.Delete(tp); } catch (Exception) { }
        }
    }

    /// <summary>지정 방을 제외한 모든 방의 추적 파일(sessions\*.txt/.prev.txt/.good.txt)에 들어있는 claude
    /// 세션 ID 집합(소문자). settings 에는 없고 훅 추적 파일에만 남은 이전 세대 sid 까지 포괄해, PurgeRoom 이
    /// 살아있는 다른 방의 transcript 를 오삭제하는 것을 막는다.</summary>
    private static HashSet<string> CollectOtherRoomTrackedSids(string exceptRoomId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var except = SafeRoomFileName(exceptRoomId);
            var dir = Path.Combine(ClaudeTrackDir, "sessions");
            if (!Directory.Exists(dir)) return set;
            foreach (var f in Directory.EnumerateFiles(dir, "*.txt"))
            {
                var fn = Path.GetFileName(f);
                // 삭제 대상 방 자신의 파일(<except>.txt / .prev.txt / .good.txt)은 제외 — 그 sid 는 삭제 후보.
                if (fn.Equals(except + ".txt", StringComparison.OrdinalIgnoreCase)
                    || fn.Equals(except + ".prev.txt", StringComparison.OrdinalIgnoreCase)
                    || fn.Equals(except + ".good.txt", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    foreach (var line in File.ReadAllLines(f))
                    {
                        var id = line.Trim();
                        if (Guid.TryParse(id, out _)) set.Add(id.ToLowerInvariant());
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return set;
    }

    private static void PurgeAppOwnedRoomArtifacts(string roomId, IEnumerable<string?> sessionIds)
    {
        var roomFile = SafeRoomFileName(roomId);

        TryDeleteFile(Path.Combine(ClaudeTrackDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "lastmsg", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "lastreply", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "modeleffort", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "room-settings", roomFile + ".json"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "sessions", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "sessions", roomFile + ".prev.txt"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "sessions", roomFile + ".good.txt"));
        TryDeleteFile(ClaudeQuitFlagPath(roomId));
        TryDeleteFiles(ClaudeTrackDir, "statusline-cache-" + roomFile + "-*.txt");

        // 서브에이전트 추적 상태(신규): 메인 턴 플래그 + 방별 run 파일 디렉터리.
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "busy", "_state", $"main_{roomFile}.flag"));
        var runDir = Path.Combine(ClaudeTrackDir, "subruns", roomFile);
        if (Directory.Exists(runDir)) { try { Directory.Delete(runDir, true); } catch { } }

        var codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "codex");
        TryDeleteFile(Path.Combine(codexDir, "sessions", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(codexDir, "sessions", roomFile + ".root.txt"));
        TryDeleteFile(Path.Combine(codexDir, "sessions", roomFile + ".prev.txt"));
        TryDeleteFile(Path.Combine(codexDir, "lastmsg", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(codexDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(codexDir, "waiting", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(codexDir, "active", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(CodexLaunchDir(), roomFile + ".cmd"));

        var grokDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "grok");
        TryDeleteFile(Path.Combine(grokDir, "sessions", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(grokDir, "sessions", roomFile + ".prev.txt"));
        TryDeleteFile(Path.Combine(grokDir, "sessions", roomFile + ".root.txt"));
        TryDeleteFile(Path.Combine(grokDir, "sessions", roomFile + ".ended.txt"));
        TryDeleteFile(Path.Combine(grokDir, "lastmsg", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(grokDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(grokDir, "waiting", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(grokDir, "completed", roomFile + ".flag"));
        TryDeleteFile(Path.Combine(GrokLaunchDir(), roomFile + ".cmd"));

        var kimiDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "kimi");
        TryDeleteFile(Path.Combine(kimiDir, "sessions", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(kimiDir, "sessions", roomFile + ".root.txt"));
        TryDeleteFile(Path.Combine(kimiDir, "sessions", roomFile + ".prev.txt"));
        TryDeleteFile(Path.Combine(kimiDir, "lastmsg", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(kimiDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(kimiDir, "waiting", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(kimiDir, "active", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(KimiLaunchDir(), roomFile + ".cmd"));

        // dvz 는 훅 없이 CLI 자신이 쓴다 — 파일 이름 규약은 위 에이전트들과 같다.
        DevezVibeStateService.DeleteRoomFiles(roomId);
        TryDeleteFile(Path.Combine(DevezVibeLaunchDir(), roomFile + ".cmd"));

        var opencodeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "opencode");
        TryDeleteFile(Path.Combine(opencodeDir, "sessions", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(opencodeDir, "lastmsg", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(opencodeDir, "lastreply", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(opencodeDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(opencodeDir, "waiting", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(opencodeDir, "todos", roomFile + ".json"));
        TryDeleteFile(Path.Combine(opencodeDir, "owners", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(OpenCodeLaunchDir(), roomFile + ".cmd"));

        TryDeleteFile(Path.Combine(GajaeLaunchDir(), roomFile + ".cmd"));
        TryDeleteFile(GajaeQuitFlagPath(roomId));
        TryDeleteDirectory(GajaeSessionDir(roomId));

        var antigravityDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "antigravity");
        TryDeleteFile(Path.Combine(antigravityDir, "sessions", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(antigravityDir, "sessions", roomFile + ".prev.txt"));
        TryDeleteFile(Path.Combine(antigravityDir, "sessions", roomFile + ".root.txt"));
        TryDeleteFile(Path.Combine(antigravityDir, "sessions", roomFile + ".ended.txt"));
        TryDeleteFile(Path.Combine(antigravityDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(antigravityDir, "waiting", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(antigravityDir, "completed", roomFile + ".flag"));
        TryDeleteFile(Path.Combine(AntigravityLaunchDir(), roomFile + ".cmd"));
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    private static void TryDeleteFiles(string dir, string pattern)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                TryDeleteFile(path);
        }
        catch (Exception) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (Exception) { }
    }

    // ── 유령 방 추적파일 GC (3일 유예) ─────────────────────────────────
    // 워크스페이스(활성+보관)에 더 이상 없는 roomId 의 앱 자체 북키핑 파일(추적/캐시)을 정리한다.
    // 실제 claude 대화 기록(.jsonl)은 절대 안 건드림 — PurgeAppOwnedRoomArtifacts 가 지우는 범위만.
    // 타이밍 레이스 등으로 인한 오탐을 막기 위해 발견 즉시 지우지 않고, ghost-registry.txt 에 최초
    // 발견일을 남겨 재시작해도 계속 3일 이상 유령으로 관측된 것만 실제로 지운다.
    private static string GhostRegistryPath => Path.Combine(ClaudeTrackDir, "ghost-registry.txt");
    private static readonly TimeSpan GhostGraceDays = TimeSpan.FromDays(3);

    /// <summary>앱 시작 시 1회 호출. validRoomIds 는 워크스페이스의 활성+보관 세션 Id 전체
    /// (호출부가 모델 타입을 몰라도 되게 문자열만 받는다). 실패해도 앱 동작엔 영향 없는 best-effort.</summary>
    public static void ReconcileGhostRoomTracking(IEnumerable<string> validRoomIds)
    {
        try
        {
            var valid = new HashSet<string>(
                validRoomIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(SafeRoomFileName),
                StringComparer.OrdinalIgnoreCase);

            var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Collect(string dir, string ext)
            {
                if (!Directory.Exists(dir)) return;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*" + ext, SearchOption.TopDirectoryOnly))
                    {
                        var room = Path.GetFileNameWithoutExtension(f);
                        foreach (var suffix in new[] { ".root", ".prev", ".good", ".ended" })
                        {
                            if (!room.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                            room = room[..^suffix.Length];
                            break;
                        }
                        tracked.Add(room);
                    }
                }
                catch { }
            }
            Collect(Path.Combine(ClaudeTrackDir, "sessions"), ".txt");
            Collect(Path.Combine(ClaudeTrackDir, "waiting"), ".txt");
            Collect(Path.Combine(ClaudeTrackDir, "busy"), ".txt");
            Collect(Path.Combine(ClaudeTrackDir, "lastmsg"), ".txt");
            Collect(Path.Combine(ClaudeTrackDir, "lastreply"), ".txt");
            Collect(Path.Combine(ClaudeTrackDir, "modeleffort"), ".txt");
            Collect(Path.Combine(ClaudeTrackDir, "room-settings"), ".json");
            Collect(Path.Combine(ClaudeTrackDir, "quitting"), ".txt");
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Collect(Path.Combine(appData, "DevezCode", "codex", "sessions"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "codex", "busy"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "codex", "waiting"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "codex", "lastmsg"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "grok", "sessions"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "grok", "busy"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "grok", "waiting"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "grok", "lastmsg"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "opencode", "sessions"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "opencode", "busy"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "opencode", "waiting"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "opencode", "lastmsg"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "opencode", "lastreply"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "opencode", "todos"), ".json");
            Collect(Path.Combine(appData, "DevezCode", "antigravity", "sessions"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "antigravity", "busy"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "antigravity", "waiting"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "antigravity", "completed"), ".flag");
            Collect(Path.Combine(appData, "DevezCode", "kimi", "sessions"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "kimi", "busy"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "kimi", "waiting"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "kimi", "lastmsg"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "devezvibe", "sessions"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "devezvibe", "busy"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "devezvibe", "waiting"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "devezvibe", "lastmsg"), ".txt");
            // statusline-cache-<room>-<sig>.txt 는 ClaudeTrackDir 루트에 바로 있고 접두사 매칭 필요.
            try
            {
                foreach (var f in Directory.EnumerateFiles(ClaudeTrackDir, "statusline-cache-*.txt", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(f); // statusline-cache-<room>-<sig>
                    var rest = name.Substring("statusline-cache-".Length);
                    var dash = rest.LastIndexOf('-');
                    if (dash > 0) tracked.Add(rest.Substring(0, dash));
                }
            }
            catch { }
            // subruns/<room>/ 는 디렉터리 단위(파일 아님).
            try
            {
                var subrunsDir = Path.Combine(ClaudeTrackDir, "subruns");
                if (Directory.Exists(subrunsDir))
                    foreach (var d in Directory.EnumerateDirectories(subrunsDir))
                        tracked.Add(Path.GetFileName(d));
            }
            catch { }

            var registry = LoadGhostRegistry();
            var now = DateTime.UtcNow;
            bool changed = false;

            // 유령 후보가 다시 유효해졌으면(재등장) 레지스트리에서 제거.
            foreach (var rid in registry.Keys.Where(valid.Contains).ToList())
            { registry.Remove(rid); changed = true; }

            foreach (var rid in tracked)
            {
                if (valid.Contains(rid)) continue; // 살아있는 방 — 손대지 않음
                if (registry.TryGetValue(rid, out var firstSeen))
                {
                    if (now - firstSeen >= GhostGraceDays)
                    {
                        PurgeAppOwnedRoomArtifacts(rid, Enumerable.Empty<string?>());
                        registry.Remove(rid);
                        changed = true;
                    }
                }
                else
                {
                    registry[rid] = now; // 첫 발견 — 이번엔 안 지우고 날짜만 기록
                    changed = true;
                }
            }

            // 레지스트리에 있는데 추적 흔적 자체가 이미 없어졌으면(수동삭제 등) 정리.
            foreach (var rid in registry.Keys.Where(r => !tracked.Contains(r)).ToList())
            { registry.Remove(rid); changed = true; }

            if (changed) SaveGhostRegistry(registry);
        }
        catch { /* GC 실패해도 앱 동작엔 영향 없음 */ }
    }

    private static Dictionary<string, DateTime> LoadGhostRegistry()
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(GhostRegistryPath)) return result;
            foreach (var line in File.ReadAllLines(GhostRegistryPath))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && DateTime.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    result[parts[0]] = dt;
            }
        }
        catch { }
        return result;
    }

    private static void SaveGhostRegistry(Dictionary<string, DateTime> registry)
    {
        try
        {
            Directory.CreateDirectory(ClaudeTrackDir);
            var lines = registry.Select(kv => kv.Key + "\t" + kv.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllLines(GhostRegistryPath, lines);
        }
        catch { }
    }

    /// <summary>앱 종료 시 호출 — 모든 셸 프로세스 정리 (좀비 방지).</summary>
    public void DisposeAll()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values)
            {
                try { s.Dispose(); } catch (Exception) { }
            }
            _sessions.Clear();
            _claudeSessionWatcher?.Dispose();
            _claudeSessionWatcher = null;
        }
    }
}
