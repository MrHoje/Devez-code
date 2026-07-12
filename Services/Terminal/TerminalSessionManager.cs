using System.IO;

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
    private readonly Dictionary<string, string> _opencodeRoomDirs = new();
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

    /// <summary>graceful 종료(숨김 지연 종료·부분 재시작)가 진행 중인 방. 이 동안(최대 수 초) 새 배선/
    /// 재부착을 막는다 — 죽어가는 세션에 붙으면 종료 트랜스크립트가 재생되고 "[세션 종료됨]" 죽은 방으로
    /// 굳어 자동 resume 이 안 된다(다른 패널에서의 재오픈·프리로드 경로).</summary>
    private readonly HashSet<string> _gracefulStopping = new();

    /// <summary>이 방이 graceful 종료 진행 중인지 — WireSession/ActivateSession 재부착 가드용.</summary>
    public bool IsGracefulStopping(string roomId)
    {
        lock (_lock) return _gracefulStopping.Contains(roomId);
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
            if (agent.Id is "claude" or "codex" or "grok")
                AgentModelCatalogRefreshRequested?.Invoke(agent.Id);

            if (ccDir != null && agent.Id == "codex")
            {
                // codex: 클로드와 동일한 "직접 실행" 패턴 (--session-id/--resume, 훅으로 lastmsg/busy/session_id 추적).
                // SupportsHooks=true 인 Claude 의 TryBuildDirectLaunch 와 별도 경로 — 커맨드/훅 스키마가 다름.
                startDir = ccDir;
                var direct = TryBuildCodexDirectLaunch(roomId, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && agent.Id == "grok")
            {
                // grok: 훅 + `grok -r <id>` 복원. 앱레벨 자동 재진입(codex 패턴).
                startDir = ccDir;
                var direct = TryBuildGrokDirectLaunch(roomId, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && agent.Id == "opencode")
            {
                // opencode: Devez 패턴 — 플러그인이 sessions\<room>.txt 에 기록한 session_id 로 --session <id> 로 정확히 복원.
                // 같은 폴더의 여러 방이 있어도 플러그인 $env:DEVEZCODE_ROOM_ID 로 분리됨.
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

            // opencode 세션이면 시작 전에 프로젝트 tui.json 에 theme 을 기록한다.
            var isOpenCode = agent.Id == "opencode";
            if (isOpenCode && !string.IsNullOrWhiteSpace(ccDir))
                ApplyOpenCodeProjectTheme(ccDir, DevezCode.App.CurrentTheme);

            TerminalSession session;
            try
            {
                // SessionStart 훅(room-hook.ps1)이 어느 방의 claude 세션인지 알 수 있게
                // 방 ID를 자식(cmd→claude→훅)에 상속시킨다. 생성 직후 해제해 다른 자식 프로세스로 새지 않게 한다.
                // (Claude 외 에이전트는 훅이 없으므로 무해.)
                Environment.SetEnvironmentVariable("DEVEZCODE_ROOM_ID", roomId);
                session = new TerminalSession(commandLine, startDir, cols, rows);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVEZCODE_ROOM_ID", null);
            }
            // 직접 실행이면 inject==null → 주입 없음. 폴백 셸이면 첫 출력 후 WireSession 에서 inject 전송.
            _pendingInitial[roomId] = inject;
            _sessions[roomId] = session;

            // opencode 세션이면 room → working directory 기억.
            if (isOpenCode)
            {
                if (!string.IsNullOrWhiteSpace(ccDir))
                    _opencodeRoomDirs[roomId] = ccDir;
            }

            return session;
        }
    }

    /// <summary>App.ThemeChanged → 살아있는 opencode 세션의 프로젝트 tui.json 갱신.
    /// opencode 는 tui.json 을 시작 시에만 읽으므로 세션 재시작이 필요한데,
    /// 그 재시작은 JS 브리지를 가진 TerminalHostView 가 담당한다(rewire + "restarted" 통지로
    /// "Enter 로 재시작" 프롬프트 없이 매끄럽게 새 테마로 다시 띄움). 여기서는 tui.json 만 기록.
    /// claude 는 테마 변경 시 세션 자체가 재시작되고(SettingsDialog → ReloadAllSessionsForTheme),
    /// 재시작된 프로세스가 room-settings.json(--settings 커맨드라인)으로 새 테마를 받으므로
    /// 프로젝트 local settings 를 따로 건드릴 필요가 없다(예전엔 건드렸으나, 그 프로젝트 폴더에서
    /// DevezCode 밖의 claude 를 켜도 테마가 새어나가는 부작용만 있었다).</summary>
    private void OnAppThemeChanged_Broadcast(string theme)
    {
        List<string> opencodeDirs;
        lock (_lock)
        {
            opencodeDirs = new List<string>(_opencodeRoomDirs.Values);
        }
        foreach (var dir in opencodeDirs)
            ApplyOpenCodeProjectTheme(dir, theme);
    }

    /// <summary>프로젝트 루트 tui.json 에 opencode theme 을 기록한다.
    /// opencode 가 시작 시 또는 파일 변경을 감지해 반영한다.
    /// 파일: &lt;workingDir&gt;/tui.json. 기존 설정은 보존.</summary>
    private static void ApplyOpenCodeProjectTheme(string workingDir, string devezCodeTheme)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir)) return;
            var path = Path.Combine(workingDir, "tui.json");
            var theme = OpenCodeCustomThemes.MapToOpenCodeTheme(devezCodeTheme);

            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(path))
            {
                try
                {
                    root = System.Text.Json.Nodes.JsonNode.Parse(
                        File.ReadAllText(path),
                        documentOptions: new System.Text.Json.JsonDocumentOptions
                        {
                            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            AllowTrailingCommas = true,
                        }) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                }
                catch
                {
                    return;
                }
            }
            else
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }

            root["theme"] = theme;
            File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* tui.json 갱신 실패 — best-effort */ }
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
    private string? TryBuildCodexDirectLaunch(string roomId, out string? injectFallback)
    {
        injectFallback = null;
        CodexHookInstaller.EnsureScriptInstalled();
        CodexHookInstaller.InstallHooksJson();

        var sessionId = SettingsService.LoadCodexRoomSession(roomId);
        if (sessionId != null && !Guid.TryParse(sessionId, out _)) sessionId = null;
        SettingsService.MarkAgentRoomLaunched(roomId, "codex"); // 추적용

        string options = "--no-alt-screen";

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
            File.WriteAllText(batchPath, "@echo off\r\n" + body + "\r\n");
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
                                  $"call opencode --session {forkSrc} --fork || call opencode\r\n";
                try
                {
                    var fdir = OpenCodeLaunchDir();
                    Directory.CreateDirectory(fdir);
                    var fbatch = Path.Combine(fdir, SafeRoomFileName(roomId) + ".cmd");
                    // 위와 동일 — opencode 는 exit 로 cmd 를 닫고 앱이 새 세션으로 재시작(0xc0000142 회피).
                    File.WriteAllText(fbatch, forkBody + "exit\r\n");
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
                      $"{opencodeCmd}\r\n";

        try
        {
            var dir = OpenCodeLaunchDir();
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            // opencode 는 opencode.exe(bun TUI) 를 종료한 ConPTY 에서 in-place 재기동하면 0xc0000142
            // (DLL init 실패)가 난다. 그래서 배치 루프(claude/gjc 방식)를 쓰지 않고, 세션이 끝나면 exit 로
            // cmd 를 닫아 앱(TerminalHostView.onExited)이 "새 ConPTY 세션"으로 같은 세션을 resume 재시작한다.
            File.WriteAllText(batchPath, body + "exit\r\n");
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

        // 훅 파일이 settings 보다 최신이면 동기화
        var tracked = GrokHookService.LoadTrackedSessionId(roomId);
        var sessionId = SettingsService.LoadGrokRoomSession(roomId);
        if (tracked != null && tracked != sessionId)
        {
            sessionId = tracked;
            SettingsService.SaveGrokRoomSession(roomId, tracked);
        }
        if (sessionId != null && !Guid.TryParse(sessionId, out _)) sessionId = null;
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
            File.WriteAllText(batchPath,
                "@echo off\r\n" +
                $"set \"DEVEZCODE_ROOM_ID={roomId}\"\r\n" +
                body + "\r\n");
            return $"cmd.exe /c \"{batchPath}\"";
        }
        catch
        {
            injectFallback = body + "\r";
            return null;
        }
    }

    private static string OpenCodeLaunchDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "opencode", "launch");

    private static string GajaeLaunchDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "gajae", "launch");

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
            content = content.Replace(oldId, newId);

            // 복사본은 반드시 "fork 방이 실행될 cwd(workingDir)의 인코딩 폴더"에 심어야 한다 — claude --resume 은
            // 그 폴더에서만 <id>.jsonl 을 찾는다. srcPath 는 전역 폴백 스캔으로 다른 프로젝트 폴더에서 왔을 수
            // 있어(그 경우 소스 폴더에 두면 resume 이 못 찾아 빈 세션이 됨) srcPath 폴더를 그대로 쓰면 안 된다.
            var destDir = ClaudeProjectDir(workingDir) ?? Path.GetDirectoryName(srcPath)!;
            Directory.CreateDirectory(destDir);
            var newPath = Path.Combine(destDir, newId + ".jsonl");
            File.WriteAllText(newPath, content);
            return newId;
        }
        catch { return null; }
    }

    /// <summary>가재코드(gjc)를 cmd /k 배치로 직접 실행. 방별 --session-dir 로 세션을 격리하고,
    /// 그 폴더의 최신 세션 ID 를 영속(SettingsService)한 뒤 `gjc -r &lt;id&gt;` 로 같은 대화를 복원한다.
    /// 첫 실행(저장·추출 ID 모두 없음)은 plain `gjc` 로 새 세션 생성. resume 실패(외부 삭제 등) 시 fresh 폴백.
    /// gjc 는 claude 의 --session-id 같은 사전 발급이 없어, 만들어진 ID 를 파일명에서 캡처하는 방식을 쓴다.</summary>
    private string? TryBuildGajaeDirectLaunch(string roomId, out string? injectFallback)
    {
        injectFallback = null;
        var sessionDir = GajaeSessionDir(roomId);
        try { Directory.CreateDirectory(sessionDir); } catch { }

        // 폴더 최신 세션 ID 가 저장값과 다르면 그쪽이 최신 대화 → 교체(새 대화·/clear 추종).
        var sessionId = SettingsService.LoadGajaeRoomSession(roomId);
        var latest = FindLatestGajaeSessionId(sessionDir);
        if (latest != null && latest != sessionId)
        {
            sessionId = latest;
            SettingsService.SaveGajaeRoomSession(roomId, latest);
        }

        // --session-dir 토큰은 따옴표로 감싸 공백 경로 안전. -r <id> 는 GUID 만(파일명에서 검증) → 주입 차단.
        // busy/lastmsg 는 앱이 이 session-dir 의 .jsonl 을 폴링해 처리(GajaeLastMessageService). gjc 확장/훅 불필요.
        string sd = $"--session-dir \"{sessionDir}\"";
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
            File.WriteAllText(batchPath, "@echo off\r\n" + cmd + "\r\n" + GajaeReentryLoop(sd, RegisterReenterFlag(roomId)));
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
                DiagLog.Write($"launch[{roomId}]: 추적 sid={sessionId} transcript 없음, prev 폴백 불가 → 새 세션으로 시작");
                sessionId = null;
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
            File.WriteAllText(LaunchBatchPath(roomId), "@echo off\r\n" + body + "\r\n" + ClaudeReentryLoop(flags, trackFile, ClaudeQuitFlagPath(roomId), RegisterReenterFlag(roomId)));
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
    // /exit·Ctrl+C 로 CLI 를 닫아도 셸로 빠지지 않고, 방이 살아있는 동안은 같은 세션으로 다시 띄운다.
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
        "ping -n 2 127.0.0.1 >nul\r\n" +      // ≈1s 스로틀 — 정상 /exit 엔 무해, 크래시 루프 스핀 방지
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
        ":__quitflag\r\n";
    }

    /// <summary>가재(gjc) 재진입 루프 — 방별 --session-dir 로 격리돼 있어 -c(최신 이어가기)가 곧 이 방의
    /// 마지막 대화. 앱의 session-dir 폴링(GajaeLastMessageService)이 재진입 세션도 그대로 추적한다.</summary>
    private static string GajaeReentryLoop(string sd, string reenterFlagPath) =>
        "set FAILS=0\r\n" +
        ":__reenter\r\n" +
        $"type nul >\"{reenterFlagPath}\"\r\n" + // 재실행 직전 앱 신호(touch) — 로딩 커버용. 첫 실행은 루프 밖
        $"call gjc {sd} -c\r\n" +   // call: gjc 가 gjc.cmd(npm) 인 환경에서도 종료 후 제어가 루프로 복귀
        $"if errorlevel 1 call gjc {sd}\r\n" +
        ReentryTail();

    // ── Claude 세션 ID 추적 (%APPDATA%\DevezCode\claude\) ──────────────
    // SessionStart 훅이 방별 현재 세션 ID를 sessions\<roomId>.txt 에 기록한다.

    private static string ClaudeTrackDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude");
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
                   busy.Contains("Touch-LiveSubruns") && busy.Contains("'permission'");
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
            File.WriteAllText(HookScriptPath, script);

            // statusLine 훅: 받은 JSON 을 ratelimit.json 에 저장(앱 푸터용)한 뒤, 사용자의 원래
            // statusLine(~/.claude/settings.json)에 같은 JSON 을 넘겨 그 출력을 그대로 통과시킨다.
            // → CLI 에 원래 뜨던 statusLine 이 그대로 유지되면서 앱도 계정 rate_limits 를 캡처한다.
            // 여러 세션이 같은 파일에 써도 rate_limits 는 계정 전역값이라 마지막으로 쓴 것이 곧 최신.
            const string statusScript = """
                # DevezCode statusLine: capture account rate_limits, then pass through to the
                # user's own statusLine so the original CLI status line keeps rendering.
                # roomId 는 settings command 인자(우선) 또는 env(폴백)로 받는다.
                param([string]$roomArg = '')
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
                # 방별 실제 model/effort 를 떨군다(콤보 라이브 연동). claude statusLine JSON 의
                # model.id / effort.level 이 곧 이 세션의 현재 적용값. roomId 는 claude 가 상속한 env.
                $sig = ''
                try {
                  if ($o) { $sig = (('' + $o.model.id) + '-' + ('' + $o.effort.level)) -replace '[^\w\-]', '' }
                  $room = $rid
                  if ($room -and $o) {
                    $room = $room -replace '[^\w\-]', ''
                    $md = Join-Path $dir 'modeleffort'
                    New-Item -ItemType Directory -Force -Path $md | Out-Null
                    Set-Content -LiteralPath (Join-Path $md ($room + '.txt')) -Value ("{0}`n{1}" -f $o.model.id, $o.effort.level) -Encoding utf8 -Force
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
            File.WriteAllText(StatusLineScriptPath, statusScript);

            // statusLine 렌더 node 스크립트: powershell→cmd→node 체인(1.4~2초) 대신 node 한 번(~150ms)으로
            // ① rate_limits 캡처(ratelimit.json) ② 방별 model/effort 기록 ③ 사용자 statusline.js 스폰 렌더.
            // DevezCode 내부 세션이 일반 터미널과 동일하게 빠르게 statusLine 을 그리게 한다(resume 빈 줄 해소).
            const string roomStatusJs = """
                // DEVEZCODE-ROOM-STATUSLINE v2 (방별 statusline 캐시 기록 추가)
                const fs = require("fs"), path = require("path"), os = require("os"), cp = require("child_process");
                const roomArg = (process.argv[2] || process.env.DEVEZCODE_ROOM_ID || "").replace(/[^\w\-]/g, "");
                let raw = "";
                process.stdin.on("data", c => raw += c);
                process.stdin.on("end", () => {
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
                  // 2) 방별 실제 model/effort 기록(콤보 라이브 연동).
                  try {
                    if (appData && roomArg && o) {
                      const md = path.join(appData, "DevezCode", "claude", "modeleffort");
                      fs.mkdirSync(md, { recursive: true });
                      const mid = (o.model && o.model.id) || "";
                      const eff = (o.effort && o.effort.level) || "";
                      fs.writeFileSync(path.join(md, roomArg + ".txt"), mid + "\n" + eff);
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
            // ghost 방어: SubagentStop 누락(크래시/kill) 대비 1시간 초과 run 파일은 카운트 전 prune,
            // SessionEnd 시 방 run 디렉터리 전량 제거, 앱 시작 시 subruns/main 플래그 wipe(C# SessionBusyService).
            const string busyScript = """
                # DevezCode busy-state hook. Arg1 = running|idle|notify|unwait|pulse|substart|substop. Per-room sidebar spinner state.
                # 스피너 = (메인 턴 진행중) OR (살아있는 서브에이전트 >=1). 둘 다 room 키 → resume 로 session_id 바뀌어도 안 깨짐.
                param([string]$status = 'idle', [string]$roomArg = '')
                try {
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
                  }

                  function Touch-LiveSubruns($rd) {
                    try {
                      if (-not (Test-Path -LiteralPath $rd)) { return }
                      $cut = (Get-Date).AddHours(-1)
                      foreach ($f in @(Get-ChildItem -LiteralPath $rd -Filter '*.run' -ErrorAction SilentlyContinue)) {
                        if ($f.LastWriteTime -lt $cut) { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue }
                        else { Write-State $f.FullName ((Get-Date).ToString('o')) }
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

                  # 입력 대기 ❗ 진입 신호.
                  #  • notify(PermissionRequest): 실제 권한창 → 서브 실행중이라도 항상 무장(서브가 툴 권한 대기).
                  #  • notifyidle(Notification 폴백): 살아있는 서브>0 이면 무장 안 함 — 이건 서브 완료를 기다리는
                  #    동안 뜨는 '60초 idle' 알림이라 유저 블로킹이 아니다(Task 가 메인 턴을 블로킹하므로 서브 실행중
                  #    메인이 유저에게 물을 수 없다 → 이때 Notification 은 오탐). 서브 없으면 메인이 진짜 유저 대기 → 무장.
                  # 둘 다 busy=running 일 때만 기록(완전 idle 알림 제외). 두 번째 연속 선택지도 매번 재무장.
                  if ($status -eq 'notify' -or $status -eq 'notifyidle') {
                    if ($status -eq 'notifyidle' -and (Get-LiveSubCount $runDir) -gt 0) { exit 0 }
                    $b = ''; try { if (Test-Path -LiteralPath $busyFile) { $b = (Get-Content -LiteralPath $busyFile -Raw -ErrorAction SilentlyContinue).Trim() } } catch { }
                    if ($b -eq 'running') {
                      $waitValue = if ($status -eq 'notify') { 'permission' } else { 'input' }
                      Write-State $waitFile $waitValue
                    }
                    exit 0
                  }
                  # PostToolUse 등 = 답변 처리 재개 → 선택지 대기 해제.
                  if ($status -eq 'unwait') {
                    Touch-LiveSubruns $runDir
                    Write-State $waitFile 'idle'
                    exit 0
                  }
                  if ($status -eq 'pulse') {
                    Touch-LiveSubruns $runDir
                    exit 0
                  }

                  $raw = ''
                  try { $raw = [System.IO.StreamReader]::new([Console]::OpenStandardInput()).ReadToEnd() } catch { }
                  $j = $null
                  try { $j = $raw | ConvertFrom-Json } catch { }

                  # ── SubagentStart: run 파일 생성 → 즉시 busy=running (agent_id 로 개별 추적) ──
                  if ($status -eq 'substart') {
                    $aid = ''; try { $aid = ('' + $j.agent_id) -replace '[^\w\-]', '' } catch { }
                    if ($aid) {
                      New-Item -ItemType Directory -Force -Path $runDir | Out-Null
                      Write-State (Join-Path $runDir ($aid + '.run')) ((Get-Date).ToString('o'))
                      Write-State $busyFile 'running'
                    }
                    exit 0
                  }
                  # ── SubagentStop: run 파일 삭제 → (메인 진행중 OR 남은 서브>0)로 busy 재평가 ──
                  if ($status -eq 'substop') {
                    $aid = ''; try { $aid = ('' + $j.agent_id) -replace '[^\w\-]', '' } catch { }
                    if ($aid) { Remove-Item -LiteralPath (Join-Path $runDir ($aid + '.run')) -Force -ErrorAction SilentlyContinue }
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
            File.WriteAllText(BusyHookScriptPath, busyScript);
        }
        catch (Exception) { /* 추적 실패해도 claude 실행은 계속 — flags 에서 파일 존재 확인 */ }
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
        var settings = new
        {
            // 세션 기록 보존 기간 — 마지막 활동일부터 이 일수가 지나면 claude 가 트랜스크립트를 자동 삭제(resume 불가).
            // 방은 오래 두고 다시 여는 물건이라 90일로 넉넉히(30일이었을 땐 한 달 방치한 방 대화가 증발했다). 비용은 텍스트 디스크뿐.
            cleanupPeriodDays = 90,
            // theme 을 command-line scope(최우선)에 박아 auto(배경 자동감지) 경로를 제거 — ConPTY 에서 흰 화면 고착 방지.
            theme = ClaudeCustomThemes.MapToClaudeTheme(DevezCode.App.CurrentTheme),
            statusLine,
            hooks = new
            {
                SessionStart     = new[] { new { hooks = new[] { new { type = "command", command } } } },
                UserPromptSubmit = new[] { new { hooks = new[] { new { type = "command", command = busyRunCommand } } } },
                Stop             = new[] { new { hooks = new[] { new { type = "command", command = busyIdleCommand } } } },
                SessionEnd       = new[] { new { hooks = new[] { new { type = "command", command = busyIdleCommand } } } },
                // 선택지/권한 입력 대기 ❗ — 진입 신호 둘:
                //  • PermissionRequest: 툴 권한 대화창이 뜨는 '즉시' 발화(matcher * = 모든 툴) → 지연 없음.
                //  • Notification: 그 외 입력 대기(AskUserQuestion 등) 폴백(claude 측 타이밍상 수 초 지연 가능).
                //    단 살아있는 서브에이전트가 있으면 이 Notification 은 '서브 완료 대기중 60초 idle' 오탐이므로
                //    notifyidle 로 보내 subcount>0 일 때 무장하지 않는다(서브 도는 동안 ❗ 대신 스피너만 유지).
                // 해제는 PostToolUse/Stop + 답변 입력(즉시 UI).
                PermissionRequest = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command = busyNotifyCommand } } } },
                Notification     = new[] { new { hooks = new[] { new { type = "command", command = busyNotifyIdleCommand } } } },
                // PostToolUse: 툴 처리 재개 → 대기 해제(hidden PowerShell unwait). PreToolUse(pulse)는 서브런 keep-alive 가
                // C# reconcile/SubMaxAge 와 중복 + 서브 실행 중엔 메인이 블로킹돼 발화도 안 해 실효 없음 → 제거(툴당 오버헤드 제거).
                PostToolUse      = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command = busyUnwaitCommand } } } },
                // 서브에이전트 생존 추적(스피너 조기소멸 방지): Start=run 파일 생성, Stop=삭제 → busy 재평가.
                SubagentStart    = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command = busySubStartCommand } } } },
                SubagentStop     = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command = busySubStopCommand } } } },
            }
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

    /// <summary>방의 추적 세션 파일(sessions\<room>.txt)을 제거. 빈 세션 ID 고착을 풀 때 호출.</summary>
    private static void DeleteTrackedSessionFile(string roomId)
    {
        try { File.Delete(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".txt")); }
        catch (Exception) { }
        // 고착 해제 = 이 방을 새 세션으로 리셋하려는 의도 — prev 를 남기면 실행부 폴백이 옛 대화를 되살린다.
        try { File.Delete(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".prev.txt")); }
        catch (Exception) { }
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

    /// <summary>앱 종료 시: 모든 세션을 동시에 graceful 종료(Ctrl+C×2 + exit)해 claude/codex 가
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
        DiagLog.Write($"GracefulShutdownAll: {snapshot.Count} sessions");

        try
        {
            await Task.WhenAll(snapshot.Select(kv =>
            {
                var (sendShellExit, quitInput, escFirst) = GracefulExitPlan(kv.Key);
                return kv.Value.TryGracefulExitAsync(perGraceMs, sendShellExit, quitInput, escFirst);
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
    /// 있어, 종료 시 Esc 시도 여부를 이걸로 가른다(대기 중이면 Esc 생략, /exit 만 시도).</summary>
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

    /// <summary>종료 시작 전에 방별 "종료중" 플래그를 남긴다. escFirst(Esc→/exit) 로 claude 가
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
                        if (Guid.TryParse(cx, out _) && cx != SettingsService.LoadCodexRoomSession(roomId))
                            SettingsService.SaveCodexRoomSession(roomId, cx);
                    }
                    break;
                case "grok":
                    var gk = GrokHookService.LoadTrackedSessionId(roomId);
                    if (gk != null && gk != SettingsService.LoadGrokRoomSession(roomId))
                        SettingsService.SaveGrokRoomSession(roomId, gk);
                    break;
            }
        }
        catch (Exception) { /* 스냅샷 실패 — 종료는 계속 */ }
    }

    /// <summary>테마 변경 등 부분 재시작 시: 지정된 세션들만 graceful 종료(Ctrl+C×2 + exit)해 훅 flush 기회를
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
        DiagLog.Write($"GracefulDisposeRooms: {string.Join(",", snapshot.Select(kv => kv.Key))}");

        try
        {
            await Task.WhenAll(snapshot.Select(kv =>
            {
                var (sendShellExit, quitInput, escFirst) = GracefulExitPlan(kv.Key);
                return kv.Value.TryGracefulExitAsync(perGraceMs, sendShellExit, quitInput, escFirst);
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
                _opencodeRoomDirs.Remove(kv.Key);
                _pendingInitial.Remove(kv.Key);
                _disposedRooms.Add(kv.Key); // 이후 뒤늦은 생성 요청 차단(고아 claude 방지)
                _gracefulStopping.Remove(kv.Key); // 종료 완료 — 이후 생성은 새 세션(resume)으로 정상 진행
            }
        }
    }

    /// <summary>graceful 종료 시 (셸에 exit 을 보내도 되는가, 종료를 요청할 때 무엇을 입력하는가).
    /// claude/gajae 는 항상 재진입 배치 루프(cmd `goto __reenter`) 로 실행된다. 그 루프를 돌리는
    /// cmd.exe 는 콘솔 Ctrl+C(CTRL_C_EVENT)를 배치 인터프리터 레벨에서도 독립적으로 받아 에이전트가
    /// 스스로 정상 종료해도 무관하게 "Terminate batch job (Y/N)?"(일괄 작업을 끝내시겠습니까)로 멈춘다.
    /// 그래서 claude 는 콘솔 브레이크를 만들지 않는 순수 텍스트 명령 "/exit"(claude 바이너리에 실제
    /// 등록된 슬래시 명령, alias "quit")로 종료시키고, 이어 exit 도 보내지 않는다(루프가 셸까지 정리).
    /// gajae 는 이런 텍스트 종료 명령이 확인되지 않아 아무 입력도 안 보내고 timeout 후 하드킬에 맡긴다.
    /// 그 외(opencode 등)는 재진입 루프가 없는 플레인 셸이라 기존 Ctrl+C×2 + exit 그대로 사용.</summary>
    /// <summary>claude 는 응답 생성 중(busy)이고 권한/선택지 대기(waiting)가 아닐 때만 Esc 를
    /// 먼저 보낸다(escFirst) — REPL 이 idle 프롬프트로 복귀해야 "/exit" 가 씹히지 않는다.
    /// claude 방이면 여기서 종료중 플래그도 남긴다(레이스 방지, <see cref="MarkClaudeQuitting"/>).</summary>
    private static (bool sendShellExit, string? quitInput, bool escFirst) GracefulExitPlan(string roomId)
    {
        var agent = SettingsService.LoadAgentForRoom(roomId);
        if (agent == "claude")
        {
            MarkClaudeQuitting(roomId);
            bool escFirst = !IsClaudeWaitingOnUser(roomId) && IsClaudeBusyRunning(roomId);
            // "작업 중 세션이 간헐적으로 멈춤" 조사 계측: 앱이 진행중 턴을 ESC 로 취소하는 지점은 여기뿐.
            // 다음 발생 때 diag.log 에서 이 줄이 있으면 원인=앱 종료/재시작, 없으면 claude 스스로 턴 종료.
            if (escFirst) DiagLog.Write($"GracefulExit: room={roomId} claude busy → ESC(진행중 턴 취소)+/exit");
            return (false, "/exit\r\n", escFirst);
        }
        if (agent == "codex")
        {
            // codex 는 cmd /c 배치(codex → exit)로 실행된다. 기본 경로(Ctrl+C×2)를 쓰면 배치
            // 인터프리터가 콘솔 브레이크를 "Terminate batch job (Y/N)?" 로 붙잡아 cmd 가 안 닫혀
            // 매번 perGraceMs 타임아웃 후 하드킬로만 정리됐다. codex TUI 의 슬래시 명령 "/quit" 으로
            // 곱게 종료시키면 배치가 exit 까지 진행돼 cmd /c 가 스스로 닫힌다(sendShellExit 불필요).
            // 응답 생성 중(busy)이면 composer 가 입력을 못 받으므로 claude 처럼 Esc 로 턴을 먼저 끊는다.
            return (false, "/quit\r\n", IsCodexBusyRunning(roomId));
        }
        if (agent == "grok")
        {
            // grok 도 cmd /c 배치 + /quit|/exit. busy 중이면 Esc 선행(codex 와 동일).
            return (false, "/quit\r\n", IsGrokBusyRunning(roomId));
        }
        return agent switch
        {
            "gajae" => (false, "", false),
            _       => (true, null, false),
        };
    }

    /// <summary>grok 훅 busy\&lt;room&gt;.txt 가 running 인가.</summary>
    private static bool IsGrokBusyRunning(string roomId)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "grok", "busy", SafeRoomFileName(roomId) + ".txt");
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Trim() == "running";
        }
        catch { return false; }
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

    public void DisposeRoom(string roomId, bool purgeTracking = true)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(roomId, out var s))
            {
                try { s.Dispose(); } catch (Exception) { }
                _sessions.Remove(roomId);
            }
            _opencodeRoomDirs.Remove(roomId);
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
            SettingsService.LoadCodexRoomSession(roomId),
            SettingsService.LoadOpenCodeRoomSession(roomId),
            SettingsService.LoadGajaeRoomSession(roomId),
            SettingsService.LoadGrokRoomSession(roomId),
        };
        DisposeRoom(roomId);

        PurgeAppOwnedRoomArtifacts(roomId, ids);

        // claude transcript(.jsonl) 삭제 — FindClaudeTranscriptPath 로 위치 확정(빠른 경로 + 전역 스캔 폴백).
        // 예전엔 workingDir 인코딩을 직접 계산해 그 폴더만 지웠는데, 폴더 이동/인코딩 엣지면 못 지워
        // 대화가 디스크에 잔존했다. GUID 는 projects 전역에서 유일하므로 스캔 결과가 곧 이 방의 기록.
        // (GUID 검증은 FindClaudeTranscriptPath 내부에서 수행 — opencode ses_* 등은 null 로 걸러짐.)
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var tp = FindClaudeTranscriptPath(workingDir, id);
            if (tp != null)
                try { File.Delete(tp); } catch (Exception) { }
        }
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
        TryDeleteFiles(ClaudeTrackDir, "statusline-cache-" + roomFile + "-*.txt");

        // 서브에이전트 추적 상태(신규): 메인 턴 플래그 + 방별 run 파일 디렉터리.
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "busy", "_state", $"main_{roomFile}.flag"));
        var runDir = Path.Combine(ClaudeTrackDir, "subruns", roomFile);
        if (Directory.Exists(runDir)) { try { Directory.Delete(runDir, true); } catch { } }

        var codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "codex");
        TryDeleteFile(Path.Combine(codexDir, "sessions", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(codexDir, "lastmsg", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(codexDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(CodexLaunchDir(), roomFile + ".cmd"));

        var grokDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "grok");
        TryDeleteFile(Path.Combine(grokDir, "sessions", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(grokDir, "lastmsg", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(grokDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(grokDir, "waiting", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(GrokLaunchDir(), roomFile + ".cmd"));

        var opencodeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "opencode");
        TryDeleteFile(Path.Combine(opencodeDir, "sessions", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(opencodeDir, "lastmsg", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(opencodeDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(opencodeDir, "todos", roomFile + ".json"));
        TryDeleteFile(Path.Combine(OpenCodeLaunchDir(), roomFile + ".cmd"));

        TryDeleteFile(Path.Combine(GajaeLaunchDir(), roomFile + ".cmd"));
        TryDeleteDirectory(GajaeSessionDir(roomId));
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
                        tracked.Add(Path.GetFileNameWithoutExtension(f));
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
            Collect(Path.Combine(appData, "DevezCode", "codex", "lastmsg"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "grok", "sessions"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "grok", "busy"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "grok", "waiting"), ".txt");
            Collect(Path.Combine(appData, "DevezCode", "grok", "lastmsg"), ".txt");
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
