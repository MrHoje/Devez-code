using System.IO;

namespace DevezCode.Services.Terminal;

/// <summary>
/// 채팅방(roomId)별 터미널 세션을 보관하는 전역 싱글톤.
/// 방 전환·탭 토글에도 세션은 유지되고, 앱 종료 시 DisposeAll로 일괄 정리.
/// </summary>
public sealed class TerminalSessionManager
{
    public static TerminalSessionManager Instance { get; } = new();

    private readonly Dictionary<string, TerminalSession> _sessions = new();
    private readonly Dictionary<string, string> _claudeRoomDirs = new();
    private readonly Dictionary<string, string> _opencodeRoomDirs = new();
    private readonly object _lock = new();
    private WtTerminalConfig? _config;

    /// <summary>삭제된 방 ID. 삭제 직후 뒤늦게 도착한 생성 요청으로 claude 가 다시 떠 고아가 되는 것을 막는다.</summary>
    private readonly HashSet<string> _disposedRooms = new();

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
            if (_sessions.TryGetValue(roomId, out var existing))
            {
                if (existing.IsAlive) { _pendingInitial[roomId] = null; return existing; } // 이미 실행 중 — 재주입 금지
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

            if (ccDir != null && agent.Id == "codex")
            {
                // codex: 클로드와 동일한 "직접 실행" 패턴 (--session-id/--resume, 훅으로 lastmsg/busy/session_id 추적).
                // SupportsHooks=true 인 Claude 의 TryBuildDirectLaunch 와 별도 경로 — 커맨드/훅 스키마가 다름.
                startDir = ccDir;
                var direct = TryBuildCodexDirectLaunch(roomId, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && agent.Id == "opencode")
            {
                // opencode: Devez 패턴 — 플러그인이 sessions\<room>.txt 에 기록한 session_id 로 --session <id> 로 정확히 복원.
                // 같은 폴더의 여러 방이 있어도 플러그인 $env:DEVEZCODE_ROOM_ID 로 분리됨.
                startDir = ccDir;
                var direct = TryBuildOpenCodeDirectLaunch(roomId, out inject);
                if (direct != null) commandLine = direct;
            }
            else if (ccDir != null && agent.SupportsHooks)
            {
                startDir = ccDir;
                var direct = TryBuildDirectLaunch(roomId, cfg.CommandLine, out inject);
                if (direct != null) commandLine = direct; // 성공 시 inject == null
            }
            else if (ccDir != null)
            {
                // 비-Claude 에이전트: cmd /k "<에이전트 커맨드>" 만 작성. 에러 시 프롬프트가 남아 진단 가능.
                startDir = ccDir;
                var simple = TryBuildSimpleLaunch(roomId, agent);
                if (simple != null) commandLine = simple;
            }

            // claude 세션이면 프로세스 시작 전에 프로젝트 local settings 에 theme 을 먼저 기록한다.
            // claude 가 시작 시 바로 맞는 테마를 읽도록 하기 위함. ~/.claude/settings.json 은 건드리지 않음.
            var isClaude = agent.Id == "claude" && agent.SupportsHooks;
            if (isClaude && !string.IsNullOrWhiteSpace(ccDir))
                ApplyClaudeProjectTheme(ccDir, DevezCode.App.CurrentTheme);

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

            // claude 세션이면 room → working directory 를 기억한다.
            // 테마 변경 시 settings.local.json 을 다시 갱신하기 위해 사용.
            if (isClaude)
            {
                if (!string.IsNullOrWhiteSpace(ccDir))
                    _claudeRoomDirs[roomId] = ccDir;
            }

            // opencode 세션이면 room → working directory 기억.
            if (isOpenCode)
            {
                if (!string.IsNullOrWhiteSpace(ccDir))
                    _opencodeRoomDirs[roomId] = ccDir;
            }

            return session;
        }
    }

    /// <summary>App.ThemeChanged → 살아있는 모든 claude/opencode 세션에 라이브 갱신.
    /// claude 는 settings.local.json 파일 감시로 즉시 반영.
    /// opencode 는 tui.json 을 시작 시에만 읽으므로 세션 재시작이 필요한데,
    /// 그 재시작은 JS 브리지를 가진 TerminalHostView 가 담당한다(rewire + "restarted" 통지로
    /// "Enter 로 재시작" 프롬프트 없이 매끄럽게 새 테마로 다시 띄움). 여기서는 tui.json 만 기록.</summary>
    private void OnAppThemeChanged_Broadcast(string theme)
    {
        List<string> claudeDirs;
        List<string> opencodeDirs;
        lock (_lock)
        {
            claudeDirs   = new List<string>(_claudeRoomDirs.Values);
            opencodeDirs = new List<string>(_opencodeRoomDirs.Values);
        }
        foreach (var dir in claudeDirs)
            ApplyClaudeProjectTheme(dir, theme);
        foreach (var dir in opencodeDirs)
            ApplyOpenCodeProjectTheme(dir, theme);
    }

    /// <summary>프로젝트 local settings 에 claude theme 을 기록한다.
    /// claude 는 settings 파일 변경을 감시하므로 이미 떠 있는 TUI 도 이 경로로 갱신된다.
    /// 파일: &lt;workingDir&gt;/.claude/settings.local.json. 기존 설정은 보존.</summary>
    private static void ApplyClaudeProjectTheme(string workingDir, string devezCodeTheme)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir)) return;
            var dir = Path.Combine(workingDir, ".claude");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.local.json");
            var theme = ClaudeCustomThemes.MapToClaudeTheme(devezCodeTheme);

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
                    return; // 깨진 사용자 설정 파일은 덮어쓰지 않음
                }
            }
            else
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }

            root["theme"] = theme;
            File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* local settings 갱신 실패 — best-effort */ }
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
    /// 첫 실행은 <c>codex</c> (codex 가 새 session_id 발급), 그 후엔 <c>codex resume &lt;sessionId&gt;</c> 로
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
        bool hasLaunched = SettingsService.IsAgentRoomLaunched(roomId, "codex");
        SettingsService.MarkAgentRoomLaunched(roomId, "codex");

        // 배치 본문. 첫 실행: codex (신세션). 이후: codex resume <id> (resume 실패 시 fresh 폴백).
        // codex 가 정상 시작하면 인터랙티브로 유지되어 뒤 폴백 줄은 실행되지 않음.
        string body;
        if (!hasLaunched || string.IsNullOrEmpty(sessionId))
        {
            // 첫 실행 — session_id 가 없으면 codex 가 새 세션 생성. 훅이 session_id 를 저장.
            body = "codex";
        }
        else
        {
            // 재실행 — 저장된 session_id 로 resume. resume 실패(세션 삭제 등) 시 fresh 폴백.
            body = $"codex resume {sessionId}\r\n"
                 + $"if errorlevel 1 codex";
        }

        try
        {
            // codex-launch\<room>.cmd (Claude 의 launch 디렉터리와 별도 — codex 전용)
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "codex", "launch");
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            File.WriteAllText(batchPath, "@echo off\r\n" + body + "\r\n");
            return $"cmd.exe /k \"{batchPath}\"";
        }
        catch
        {
            injectFallback = body + "\r";
            return null;
        }
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
    private string? TryBuildOpenCodeDirectLaunch(string roomId, out string? injectFallback)
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

        // (3) 플러그인도 settings 도 비어있으면 opencode session list 에서 workingDir 매칭 ID 찾기.
        // 플러그인 콜백 미작동·환경변수 누락 등 어떤 이유로든 (1)(2) 가 비어도 같은 폴더의
        // 마지막 대화를 정확히 복원 — 새 세션이 매번 만들어지는 현상 방지.
        if (sessionId == null)
        {
            var ccDir = SettingsService.LoadClaudeCodeRoomDir(roomId);
            var byCwd = OpenCodePluginInstaller.FindSessionIdByCwd(ccDir);
            if (byCwd != null)
            {
                sessionId = byCwd;
                SettingsService.SaveOpenCodeRoomSession(roomId, byCwd);
            }
        }

        // body: opencode 실행 라인. 실패 시 fresh 폴백.
        string opencodeCmd = sessionId != null
            ? $"opencode --session {sessionId} || opencode"
            : "opencode";

        // 배치: env 명시 set → opencode 실행. cmd 의 env 상속이 불안정해도 set 으로 확실히 전달.
        // roomId 에 공백/특수문자 가능 — set "VAR=value" 형식으로 안전하게.
        string body = $"@echo off\r\n" +
                      $"set \"DEVEZCODE_ROOM_ID={roomId}\"\r\n" +
                      $"{opencodeCmd}\r\n";

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevezCode", "opencode", "launch");
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            File.WriteAllText(batchPath, body);
            return $"cmd.exe /k \"{batchPath}\"";
        }
        catch
        {
            injectFallback = (sessionId != null ? $"opencode --session {sessionId}" : "opencode") + "\r";
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
        EnsureSessionHookAssets();
        string flags = "--dangerously-skip-permissions";
        if (File.Exists(HookSettingsPath)) flags += $" --settings \"{HookSettingsPath}\"";

        // 방별 model/effort 선택을 런치 플래그로 적용. 값은 콤보 화이트리스트지만 변조 대비 영숫자/하이픈만 허용.
        var model = SettingsService.LoadClaudeCodeRoomModel(roomId);
        var effort = SettingsService.LoadClaudeCodeRoomEffort(roomId);
        if (IsSafeFlagValue(model)) flags += $" --model {model}";
        if (IsSafeFlagValue(effort)) flags += $" --effort {effort}";

        var sessionId = SettingsService.LoadClaudeCodeRoomSession(roomId);
        // 불변식: 세션 ID는 항상 GUID 여야 한다. 비정상 값(설정 파일 변조 등)은 무시 →
        // 배치에 그대로 보간되어 cmd 명령이 주입되는 것을 원천 차단(새 세션처럼 시작).
        if (sessionId != null && !Guid.TryParse(sessionId, out _)) sessionId = null;

        // 훅이 기록한 마지막 세션 ID가 저장값과 다르면 그쪽이 최신 대화 — 교체 후 resume
        var tracked = LoadTrackedSessionId(roomId);
        if (tracked != null && tracked != sessionId)
        {
            sessionId = tracked;
            SettingsService.SaveClaudeCodeRoomSession(roomId, tracked);
            SettingsService.MarkClaudeCodeRoomLaunched(roomId); // 기록이 있다 = 이미 실행된 적 있음
        }

        // resume 은 추적된 세션의 대화 transcript 가 실제로 디스크에 있을 때만 한다.
        // (빈 세션 등 conversation 이 저장 안 된 경우 --resume 하면 "No conversation found" 에러가
        //  화면에 뜨므로, 없으면 --session-id 로 새로 시작해 에러를 원천 차단한다.)
        var ccDir = SettingsService.LoadClaudeCodeRoomDir(roomId);
        bool resume = sessionId != null
                      && SettingsService.IsClaudeCodeRoomLaunched(roomId)
                      && ClaudeTranscriptExists(ccDir, sessionId);
        if (sessionId != null && !resume)
            SettingsService.MarkClaudeCodeRoomLaunched(roomId); // 첫 실행 — 다음부터 resume

        // 배치 본문: 첫 실행/구버전은 단발, 재진입은 resume → 실패(외부 삭제 등) 시 fresh 폴백.
        // claude 가 정상 시작하면 인터랙티브로 유지되어 뒤 폴백 줄은 실행되지 않는다.
        string body;
        if (sessionId == null)
            body = $"claude {flags}";
        else if (resume)
            body = $"claude --resume {sessionId} {flags}\r\n"
                 + $"if errorlevel 1 claude --session-id {sessionId} {flags}";
        else
            body = $"claude --session-id {sessionId} {flags}";

        try
        {
            Directory.CreateDirectory(LaunchDir);
            File.WriteAllText(LaunchBatchPath(roomId), "@echo off\r\n" + body + "\r\n");
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
        // cmd.exe 는 `a || b`, PowerShell 은 `a; if ($LASTEXITCODE -ne 0) { b }`.
        bool isCmd = shellCommandLine.Contains("cmd", StringComparison.OrdinalIgnoreCase)
                     && !shellCommandLine.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                     && !shellCommandLine.Contains("pwsh", StringComparison.OrdinalIgnoreCase);
        return isCmd
            ? $"{r} || {f}\r"
            : $"{r}; if ($LASTEXITCODE -ne 0) {{ {f} }}\r";
    }

    // ── Claude 세션 ID 추적 (%APPDATA%\DevezCode\claude\) ──────────────
    // SessionStart 훅이 방별 현재 세션 ID를 sessions\<roomId>.txt 에 기록한다.

    private static string ClaudeTrackDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude");
    private static string HookScriptPath => Path.Combine(ClaudeTrackDir, "room-hook.ps1");
    private static string HookSettingsPath => Path.Combine(ClaudeTrackDir, "room-settings.json");
    // statusLine 훅: stdin 으로 받은 statusLine JSON 을 그대로 파일에 떨군다(계정 rate_limits 추출용).
    private static string StatusLineScriptPath => Path.Combine(ClaudeTrackDir, "statusline-hook.ps1");
    // busy 훅: UserPromptSubmit(running)/Stop(idle) 시 방별 상태 파일을 써 좌측 트리 스피너를 켜고 끈다.
    private static string BusyHookScriptPath => Path.Combine(ClaudeTrackDir, "busy-hook.ps1");

    // 방별 claude 직접 실행 배치(cmd /k 로 띄움). 매 실행 시 최신 커맨드로 덮어쓴다.
    private static string LaunchDir => Path.Combine(ClaudeTrackDir, "launch");
    private static string LaunchBatchPath(string roomId) => Path.Combine(LaunchDir, SafeRoomFileName(roomId) + ".cmd");

    /// <summary>런치 플래그 값 안전성 — 영숫자/하이픈만(공백·따옴표·세미콜론 등 주입 차단). 빈 값은 false.</summary>
    private static bool IsSafeFlagValue(string? v)
        => !string.IsNullOrEmpty(v) && System.Text.RegularExpressions.Regex.IsMatch(v, @"^[A-Za-z0-9\-]+$");

    /// <summary>roomId를 파일명으로 안전하게 (훅 ps1의 -replace 와 동일 규칙).</summary>
    private static string SafeRoomFileName(string roomId)
        => System.Text.RegularExpressions.Regex.Replace(roomId, @"[^\w\-]", "");

    /// <summary>훅 자산(스크립트·설정)이 모두 있고 busy 연동을 포함하는 최신본인지.
    /// 시작 시 배너 표시 판단용 — 하나라도 누락/구버전이면 false.</summary>
    public static bool HookAssetsHealthy()
    {
        try
        {
            if (!File.Exists(HookSettingsPath)) return false;
            if (!File.Exists(BusyHookScriptPath)) return false;
            if (!File.Exists(HookScriptPath)) return false;
            if (!File.Exists(StatusLineScriptPath)) return false;
            var json = File.ReadAllText(HookSettingsPath);
            // busy 연동(요청중 스피너) 훅이 들어있는 최신 설정인지 확인
            return json.Contains("UserPromptSubmit") && json.Contains("busy-hook");
        }
        catch { return false; }
    }

    /// <summary>훅 자산을 (재)생성한다. 배너의 원클릭 설정에서 호출.</summary>
    public static void EnsureHookAssets() => EnsureSessionHookAssets();

    /// <summary>SessionStart 훅 스크립트·설정 파일 생성 (항상 덮어써 최신 유지).</summary>
    private static void EnsureSessionHookAssets()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(ClaudeTrackDir, "sessions"));

            const string script = """
                # DevezCode claude room session tracker (SessionStart hook)
                # Records the live session id per chat room so the app resumes the latest conversation.
                try {
                  $j = [Console]::In.ReadToEnd() | ConvertFrom-Json
                  $room = $env:DEVEZCODE_ROOM_ID
                  if ($room -and $j.session_id) {
                    $room = $room -replace '[^\w\-]', ''
                    $dir = Join-Path $env:APPDATA 'DevezCode\claude\sessions'
                    New-Item -ItemType Directory -Force -Path $dir | Out-Null
                    Set-Content -LiteralPath (Join-Path $dir ($room + '.txt')) -Value $j.session_id -Encoding Ascii -Force
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
                [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
                $raw = [Console]::In.ReadToEnd()
                try {
                  $dir = Join-Path $env:APPDATA 'DevezCode\claude'
                  New-Item -ItemType Directory -Force -Path $dir | Out-Null
                  Set-Content -LiteralPath (Join-Path $dir 'ratelimit.json') -Value $raw -Encoding utf8 -Force
                } catch { }
                try {
                  $sp = Join-Path $env:USERPROFILE '.claude\settings.json'
                  if (Test-Path $sp) {
                    $cmd = (Get-Content -Raw -LiteralPath $sp | ConvertFrom-Json).statusLine.command
                    if ($cmd) {
                      # 사용자 statusLine 패스스루는 매 호출마다 cmd 프로세스를 새로 띄워 비싸다.
                      # 3초 캐시: 직전 출력을 파일로 두고 만료 전이면 재실행 없이 그대로 통과시킨다.
                      $cache = Join-Path $dir 'statusline-cache.txt'
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

            // busy 훅(clude-blinker 방식): claude 가 한 턴을 처리하는 동안만 좌측 스피너를 켠다.
            // UserPromptSubmit 에서 running, Stop(턴 종료)에서 idle 을 방별 파일로 기록한다.
            // 방 ID는 SessionStart 훅과 동일하게 $env:DEVEZCODE_ROOM_ID (claude 자식이 상속)로 구분.
            const string busyScript = """
                # DevezCode busy-state hook. Arg1 = running|idle. Writes per-room state for the sidebar spinner.
                # On 'running' (UserPromptSubmit) also records the last submitted prompt for the header title.
                param([string]$status = 'idle')
                try {
                  $room = $env:DEVEZCODE_ROOM_ID
                  if ($room) {
                    $room = $room -replace '[^\w\-]', ''
                    $dir = Join-Path $env:APPDATA 'DevezCode\claude\busy'
                    New-Item -ItemType Directory -Force -Path $dir | Out-Null
                    Set-Content -LiteralPath (Join-Path $dir ($room + '.txt')) -Value $status -Encoding Ascii -Force
                    if ($status -eq 'running') {
                      # UserPromptSubmit 은 stdin 으로 {"prompt":"..."} 를 넘긴다. 1줄로 요약해 별도 파일에 기록.
                      $raw = ''
                      try { $raw = [System.IO.StreamReader]::new([Console]::OpenStandardInput()).ReadToEnd() } catch { }
                      $prompt = ''
                      try { $prompt = ($raw | ConvertFrom-Json).prompt } catch { }
                      if ($prompt) {
                        $prompt = ($prompt -replace '\s+', ' ').Trim()
                        if ($prompt.Length -gt 200) { $prompt = $prompt.Substring(0, 200) }
                        $mdir = Join-Path $env:APPDATA 'DevezCode\claude\lastmsg'
                        New-Item -ItemType Directory -Force -Path $mdir | Out-Null
                        Set-Content -LiteralPath (Join-Path $mdir ($room + '.txt')) -Value $prompt -Encoding UTF8 -Force
                      }
                    }
                  }
                } catch { }
                exit 0
                """;
            File.WriteAllText(BusyHookScriptPath, busyScript);

            var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{HookScriptPath}\"";
            var statusCommand = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{StatusLineScriptPath}\"";
            var busyRunCommand  = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{BusyHookScriptPath}\" running";
            var busyIdleCommand = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{BusyHookScriptPath}\" idle";
            // refreshInterval 을 의도적으로 넣지 않는다 — 사용자 ~/.claude/settings.json 값과 무관하게 항상 생략.
            // statusLine 은 rate_limits 푸터 캡처가 목적이고 event-driven(메시지마다) 갱신으로 충분하므로,
            // 주기 갱신을 주입하면 idle 중에도 매초 statusLine 프로세스가 떠 CPU 가 튄다. 이를 원천 차단한다.
            var statusLine = new { type = "command", command = statusCommand };
            var settings = new
            {
                // 세션 기록 보존 기간 — 마지막 활동일부터 이 일수가 지나면 claude 가 트랜스크립트를
                // 자동 삭제한다(그 세션은 resume 불가). 30일 보존.
                cleanupPeriodDays = 30,
                statusLine,
                hooks = new
                {
                    SessionStart     = new[] { new { hooks = new[] { new { type = "command", command } } } },
                    UserPromptSubmit = new[] { new { hooks = new[] { new { type = "command", command = busyRunCommand } } } },
                    Stop             = new[] { new { hooks = new[] { new { type = "command", command = busyIdleCommand } } } },
                    SessionEnd       = new[] { new { hooks = new[] { new { type = "command", command = busyIdleCommand } } } },
                }
            };
            File.WriteAllText(HookSettingsPath, System.Text.Json.JsonSerializer.Serialize(settings));
        }
        catch (Exception) { /* 추적 실패해도 claude 실행은 계속 — flags 에서 파일 존재 확인 */ }
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

    /// <summary>해당 작업 디렉터리에 주어진 세션 ID의 claude 대화 transcript 가 실제로 존재하는지.
    /// claude 는 대화를 %USERPROFILE%\.claude\projects\&lt;경로 인코딩&gt;\&lt;세션ID&gt;.jsonl 로 저장한다.
    /// (경로 인코딩: 영숫자 외 문자를 모두 '-' 로 치환. Windows 경로는 대소문자 무시로 매칭됨)</summary>
    private static bool ClaudeTranscriptExists(string? workingDir, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(workingDir) || string.IsNullOrWhiteSpace(sessionId)) return false;
        try
        {
            var full = Path.GetFullPath(workingDir).TrimEnd('\\', '/');
            var encoded = System.Text.RegularExpressions.Regex.Replace(full, "[^a-zA-Z0-9]", "-");
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects", encoded);
            return File.Exists(Path.Combine(dir, sessionId + ".jsonl"));
        }
        catch (Exception) { return false; }
    }

    /// <summary>채팅방 삭제 시 호출 — 해당 방의 셸 프로세스 정리.
    /// <paramref name="purgeTracking"/> 이 false 면 추적 파일(sessions/*.txt, launch batch)을 보존한다.
    /// 재시작 경로에서 resume 에 필요한 session id 추적 파일이 지워지면 대화가 날아가므로 보존.</summary>
    public void DisposeRoom(string roomId, bool purgeTracking = true)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(roomId, out var s))
            {
                try { s.Dispose(); } catch (Exception) { }
                _sessions.Remove(roomId);
            }
            _claudeRoomDirs.Remove(roomId);
            _opencodeRoomDirs.Remove(roomId);
            _pendingInitial.Remove(roomId);
            _disposedRooms.Add(roomId); // 이후 뒤늦은 생성 요청 차단(고아 claude 방지)
        }
        if (purgeTracking)
        {
            // 추적 파일도 정리 (남아있으면 같은 roomId 재사용 시 엉뚱한 세션으로 이어붙음)
            try { File.Delete(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".txt")); }
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
            SettingsService.LoadClaudeCodeRoomSession(roomId),
        };
        DisposeRoom(roomId);

        if (string.IsNullOrWhiteSpace(workingDir)) return;
        try
        {
            var full = Path.GetFullPath(workingDir).TrimEnd('\\', '/');
            var encoded = System.Text.RegularExpressions.Regex.Replace(full, "[^a-zA-Z0-9]", "-");
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects", encoded);
            foreach (var id in ids)
                if (!string.IsNullOrWhiteSpace(id) && Guid.TryParse(id, out _))
                    try { File.Delete(Path.Combine(dir, id + ".jsonl")); } catch (Exception) { }
        }
        catch (Exception) { }
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
            _claudeRoomDirs.Clear();
        }
    }
}
