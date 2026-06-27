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
    private FileSystemWatcher? _claudeSessionWatcher;
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
            else if (ccDir != null && agent.Id == "gajae")
            {
                // 가재코드(gjc): 방별 격리 --session-dir + 그 폴더 최신 세션 ID 추출 → `gjc -r <id>` 로 복원.
                // gjc 는 --session-id 사전 발급이 없어 cwd 공유 시 -c 가 섞이므로, 방마다 별도 session-dir 로 분리.
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
        SettingsService.MarkAgentRoomLaunched(roomId, "codex"); // 추적용

        // 배치 본문. 저장된 session_id(훅이 기록) 가 있으면 무조건 resume(실패 시 fresh 폴백).
        // launched 플래그에 의존하지 않는다 — 작업 중 강제 종료로 플래그가 유실돼도 session_id 가
        // 살아있으면 이어가야 하기 때문. codex 가 정상 시작하면 뒤 폴백 줄은 실행되지 않음.
        string body;
        if (string.IsNullOrEmpty(sessionId))
        {
            // session_id 없음 — codex 가 새 세션 생성. 훅이 session_id 를 저장.
            body = "codex";
        }
        else
        {
            // 저장된 session_id 로 resume. resume 실패(세션 삭제 등) 시 fresh 폴백.
            body = $"codex resume {sessionId}\r\n"
                 + $"if errorlevel 1 codex";
        }

        try
        {
            // codex-launch\<room>.cmd (Claude 의 launch 디렉터리와 별도 — codex 전용)
            var dir = CodexLaunchDir();
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
            var byCwd = OpenCodePluginInstaller.FindSessionIdByCwd(ccDir, roomId);
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
            var dir = OpenCodeLaunchDir();
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

    /// <summary>가재코드(gjc) 방별 세션 디렉터리. gjc 가 여기 안에 &lt;timestamp&gt;_&lt;sessionId&gt;.jsonl 로 세션을 저장.
    /// 방마다 분리해 같은 폴더의 여러 방이 서로의 대화를 침범하지 않게 한다.</summary>
    private static string GajaeSessionDir(string roomId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "gajae", "sessions", SafeRoomFileName(roomId));

    private static string CodexLaunchDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "codex", "launch");

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
        string cmd = sessionId != null
            ? $"gjc {sd} -r {sessionId}\r\nif errorlevel 1 gjc {sd}"
            : $"gjc {sd}";

        try
        {
            var dir = GajaeLaunchDir();
            Directory.CreateDirectory(dir);
            var batchPath = Path.Combine(dir, SafeRoomFileName(roomId) + ".cmd");
            // STY(멀티플렉서 감지용) 를 세팅해 gjc 가 "멀티플렉서 모드" 로 렌더하게 한다:
            //  · 전체 재페인트 시 스크롤백 비우기(\x1b[3J)를 생략 → xterm 스크롤백 보존(휠로 과거 대화 스크롤 가능)
            //  · 전체 클리어(\x1b[2J) 대신 동기화 viewport 재페인트(\x1b[?2026h + 줄별 \x1b[2K) → 스크롤 튐 제거
            // (gjc tui.ts isMultiplexerSession = TMUX||STY||ZELLIJ. STY 는 그 외 분기가 없어 부작용 없음.)
            File.WriteAllText(batchPath, "@echo off\r\nset \"STY=devezcode\"\r\n" + cmd + "\r\n");
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
        var roomSettings = BuildRoomSettings(roomId); // 방별 settings 생성(roomId 인자 박힌 hook command 포함)
        string flags = "--dangerously-skip-permissions";
        if (File.Exists(roomSettings)) flags += $" --settings \"{roomSettings}\"";

        // 방별 model/effort 를 런치 플래그로 적용. 값은 콤보 화이트리스트지만 변조 대비 영숫자/하이픈만 허용.
        // 우선순위: (1) statusLine 이 영속한 라이브값 = 세션이 마지막에 쓰던 model/effort(TUI 안 /model 변경 포함)
        //          (2) 콤보로 명시 저장한 값  (3) 콤보 기본값(opus). → 세션이 opus 로 끝났으면 reopen 도 opus.
        var (liveModelId, liveEffort) = ModelEffortService.ReadPersisted(roomId);
        var model = ModelEffortService.ToModelValue(liveModelId)
                    ?? SettingsService.LoadClaudeCodeRoomModel(roomId) ?? "opus";
        var effort = liveEffort ?? SettingsService.LoadClaudeCodeRoomEffort(roomId);
        if (IsSafeFlagValue(model)) flags += $" --model {model}";
        if (IsSafeFlagValue(effort)) flags += $" --effort {effort}";

        var sessionId = SettingsService.LoadClaudeCodeRoomSession(roomId);
        // 불변식: 세션 ID는 항상 GUID 여야 한다. 비정상 값(설정 파일 변조 등)은 무시 →
        // 배치에 그대로 보간되어 cmd 명령이 주입되는 것을 원천 차단(새 세션처럼 시작).
        if (sessionId != null && !Guid.TryParse(sessionId, out _)) sessionId = null;

        // 훅이 기록한 마지막 세션 ID가 저장값과 다르면 그쪽이 최신 대화 — 교체 후 resume
        sessionId = SyncTrackedClaudeSessionId(roomId) ?? sessionId;

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
            sessionId = null;
        }

        // 배치 본문: 세션 없으면 단발(새 세션), 있으면(=transcript 확인됨) resume → 실패(외부 삭제 등) 시 fresh 폴백.
        // claude 가 정상 시작하면 인터랙티브로 유지되어 뒤 폴백 줄은 실행되지 않는다.
        string body;
        if (sessionId == null)
            body = $"claude {flags}";
        else
            body = $"claude --resume {sessionId} {flags}\r\n"
                 + $"if errorlevel 1 claude --session-id {sessionId} {flags}";

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
    // 방별 claude --settings 파일. hook/statusLine command 에 roomId 인자가 박혀 있다(BuildRoomSettings).
    private static string RoomSettingsPath(string roomId) => Path.Combine(ClaudeTrackDir, "room-settings", SafeRoomFileName(roomId) + ".json");
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
            if (!File.Exists(BusyHookScriptPath)) return false;
            if (!File.Exists(HookScriptPath)) return false;
            if (!File.Exists(StatusLineScriptPath)) return false;
            // settings 는 방별로 세션마다 생성되므로 공통 파일 대신 스크립트가 최신본인지 확인한다.
            // (roomId 인자 수신 + 응답 기록 기능이 들어있어야 최신)
            var busy = File.ReadAllText(BusyHookScriptPath);
            return busy.Contains("roomArg") && busy.Contains("last_assistant_message");
        }
        catch { return false; }
    }

    /// <summary>훅 자산을 (재)생성한다. 배너의 원클릭 설정에서 호출.</summary>
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
                # 세션 ID 추적은 UserPromptSubmit(busy 'running')에서만 한다 — 사용자가 실제로 메시지를 보낸
                # 세션만 기록해, 방을 열고 대화하지 않은 빈 세션(--session-id 시작 등)이 직전의 실제 대화 ID 를
                # 덮어써 영구 소실시키는 것을 막는다. SessionStart 는 /clear 전환만 처리한다.
                # roomId 는 settings command 인자(우선) 또는 env 로 받는다(claude 가 env 를 자식에 못 넘기는
                # 환경 대비 — 인자가 1차, env 는 폴백).
                param([string]$roomArg = '')
                try {
                  $j = [Console]::In.ReadToEnd() | ConvertFrom-Json
                  $room = if ($roomArg) { $roomArg } else { $env:DEVEZCODE_ROOM_ID }
                  # session_id 가 비면(claude stdin 포맷/필드명 변경 대비) transcript_path 파일명(<sid>.jsonl)에서 복구한다.
                  $sid = '' + $j.session_id
                  if (-not $sid) { try { $sid = [System.IO.Path]::GetFileNameWithoutExtension('' + $j.transcript_path) } catch { } }
                  # /clear: 새 (빈) 세션으로 명시 전환. 이전 대화는 복원하지 않는다(정책). 새 session_id 를 추적에
                  # 박아 다음 실행이 빈 새 세션으로 시작하게 하고, 헤더 lastmsg 를 비워 세션 타이틀로 복귀시킨다.
                  if ($room -and $sid -and $j.source -eq 'clear') {
                    $room = $room -replace '[^\w\-]', ''
                    $dir = Join-Path $env:APPDATA 'DevezCode\claude\sessions'
                    New-Item -ItemType Directory -Force -Path $dir | Out-Null
                    Set-Content -LiteralPath (Join-Path $dir ($room + '.txt')) -Value $sid -Encoding Ascii -Force
                    $mdir = Join-Path $env:APPDATA 'DevezCode\claude\lastmsg'
                    New-Item -ItemType Directory -Force -Path $mdir | Out-Null
                    Set-Content -LiteralPath (Join-Path $mdir ($room + '.txt')) -Value '' -Encoding UTF8 -Force
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

            // busy 훅(clude-blinker 방식): claude 가 한 턴을 처리하는 동안만 좌측 스피너를 켠다.
            // UserPromptSubmit 에서 running, Stop(턴 종료)에서 idle 을 방별 파일로 기록한다.
            // 방 ID는 SessionStart 훅과 동일하게 $env:DEVEZCODE_ROOM_ID (claude 자식이 상속)로 구분.
            //
            // 백그라운드 서브에이전트 보정: 메인이 Agent 도구로 백그라운드 서브에이전트를 띄우면 메인 턴이
            // 즉시 끝나 Stop 이 발화한다. 이때 그대로 idle 을 쓰면 서브에이전트들이 아직 도는데도 "응답 완료"
            // 알림이 조기에(그리고 각 서브 완료 재주입마다 반복) 뜬다. 그래서 idle(Stop) 시점에 세션
            // transcript 를 읽어 미완료 백그라운드 작업 수를 센다:
            //   pending = (Agent tool_use launch 수) - (재주입된 <task-notification> 의 distinct <task-id> 수)
            // pending>0 이면 idle 대신 running 을 유지해 스피너를 켜두고 완료 알림을 보류한다. 모든 서브가
            // 끝나(pending=0) 메인이 최종 응답 후 Stop 할 때 단 한 번 idle→알림이 뜬다. SessionEnd 는 세션
            // 종료이므로 무조건 idle.
            const string busyScript = """
                # DevezCode busy-state hook. Arg1 = running|idle. Writes per-room state for the sidebar spinner.
                # On 'running' (UserPromptSubmit): record the last submitted prompt for the header title.
                #   백그라운드 서브에이전트가 완료되면 그 결과가 <task-notification> 프롬프트로 재주입돼 다시
                #   running→idle 사이클이 돈다. 메인 턴이 서브에이전트를 띄우고 끝나면(또는 각 서브 완료 재주입마다)
                #   Stop 이 발화하는데, 그대로 idle 을 쓰면 서브가 아직 도는데도 "응답 완료" 알림이 조기에/반복해서 뜬다.
                # On 'idle' (Stop): 미완료 백그라운드 작업 수(pending)를 계산해 0 일 때만 idle 을 쓴다.
                #   pending = (이번 루트턴에서 새로 뜬 서브에이전트 수) - (그 사이 완료된 수)
                #   • 새 launch 수: 세션 subagents 디렉터리의 agent-*.meta.json 개수(launch 즉시 생성 — 메인
                #     transcript 는 resume/버퍼링으로 Stop 시점에 stale 할 수 있어 신뢰 불가). 단 누적이므로
                #     실제 사용자 프롬프트(루트턴 시작) 때 현재 개수를 baseline 으로 떠 과거분을 제외한다.
                #   • 완료 수: <task-notification> 재주입 프롬프트의 distinct <task-id> — 훅이 직접 누적 기록.
                #     (메인 transcript 엔 Stop 시점에 아직 안 박혀있을 수 있어 훅이 직접 센다.)
                # SessionEnd 는 세션 종료이므로 무조건 idle.
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

                  # Notification 훅 = 권한/선택지 입력 대기 진입. busy=running 일 때만 기록(60초 idle 알림 제외).
                  if ($status -eq 'notify') {
                    $b = ''; try { if (Test-Path -LiteralPath $busyFile) { $b = (Get-Content -LiteralPath $busyFile -Raw -ErrorAction SilentlyContinue).Trim() } } catch { }
                    if ($b -eq 'running') { Set-Content -LiteralPath $waitFile -Value 'waiting' -Encoding Ascii -Force }
                    exit 0
                  }
                  # PostToolUse 등 = 답변 처리 재개 → 선택지 대기 해제.
                  if ($status -eq 'unwait') {
                    Set-Content -LiteralPath $waitFile -Value 'idle' -Encoding Ascii -Force
                    exit 0
                  }

                  $raw = ''
                  try { $raw = [System.IO.StreamReader]::new([Console]::OpenStandardInput()).ReadToEnd() } catch { }
                  $j = $null
                  try { $j = $raw | ConvertFrom-Json } catch { }
                  $sid = ''; try { $sid = ('' + $j.session_id) -replace '[^\w\-]', '' } catch { }
                  $tp = '';  try { $tp = '' + $j.transcript_path } catch { }
                  # session_id 가 비면(claude stdin 포맷/필드명 변경 대비) transcript_path 파일명(<sid>.jsonl)에서 복구한다.
                  if (-not $sid -and $tp) { try { $sid = ([System.IO.Path]::GetFileNameWithoutExtension($tp)) -replace '[^\w\-]', '' } catch { } }

                  # 세션 subagents 디렉터리의 agent-*.meta.json 개수 = 그 세션에서 launch 된 누적 서브에이전트 수.
                  function Get-MetaCount($transcriptPath) {
                    try {
                      if (-not $transcriptPath) { return 0 }
                      $sub = Join-Path (Join-Path ([System.IO.Path]::GetDirectoryName($transcriptPath)) ([System.IO.Path]::GetFileNameWithoutExtension($transcriptPath))) 'subagents'
                      if (Test-Path -LiteralPath $sub) { return @(Get-ChildItem -LiteralPath $sub -Filter 'agent-*.meta.json' -ErrorAction SilentlyContinue).Count }
                    } catch { }
                    return 0
                  }
                  $sdir = Join-Path $dir '_state'
                  New-Item -ItemType Directory -Force -Path $sdir | Out-Null
                  $baseFile = Join-Path $sdir ('base_' + $sid + '.txt')
                  $doneFile = Join-Path $sdir ('done_' + $sid + '.txt')

                  if ($status -eq 'running') {
                    Set-Content -LiteralPath $busyFile -Value 'running' -Encoding Ascii -Force
                    Set-Content -LiteralPath $waitFile -Value 'idle' -Encoding Ascii -Force  # 새 턴 → 대기 해제
                    $prompt = ''
                    try { $prompt = '' + $j.prompt } catch { }
                    if ($prompt -and $prompt.StartsWith('<task-notification>')) {
                      # 백그라운드 서브에이전트 1건 완료 재주입 → distinct task-id 를 세션별 완료목록에 누적(헤더엔 안 씀).
                      if ($sid -and ($prompt -match '<task-id>\s*([A-Za-z0-9]+)')) {
                        $tid = $matches[1]
                        $have = @(); if (Test-Path -LiteralPath $doneFile) { $have = @(Get-Content -LiteralPath $doneFile -ErrorAction SilentlyContinue) }
                        if ($have -notcontains $tid) { Add-Content -LiteralPath $doneFile -Value $tid -Encoding Ascii }
                      }
                    } elseif ($prompt) {
                      # 실제 사용자 프롬프트 = 새 루트턴 시작 → baseline(현재 meta 수) 스냅샷 + 완료목록 리셋.
                      if ($sid) {
                        Set-Content -LiteralPath $baseFile -Value ([string](Get-MetaCount $tp)) -Encoding Ascii -Force
                        if (Test-Path -LiteralPath $doneFile) { Remove-Item -LiteralPath $doneFile -Force -ErrorAction SilentlyContinue }
                        # 사용자가 실제로 메시지를 보낸 세션 = 이 방의 진짜 현재 대화. 추적파일에 확정 기록한다.
                        # (SessionStart 가 아닌 여기서만 기록 → 대화 없는 빈 세션이 직전 대화 ID 를 덮지 않는다.)
                        $tdir = Join-Path $env:APPDATA 'DevezCode\claude\sessions'
                        New-Item -ItemType Directory -Force -Path $tdir | Out-Null
                        Set-Content -LiteralPath (Join-Path $tdir ($room + '.txt')) -Value $sid -Encoding Ascii -Force
                      }
                      $prompt = ($prompt -replace '\s+', ' ').Trim()
                      if ($prompt.Length -gt 200) { $prompt = $prompt.Substring(0, 200) }
                      $mdir = Join-Path $env:APPDATA 'DevezCode\claude\lastmsg'
                      New-Item -ItemType Directory -Force -Path $mdir | Out-Null
                      Set-Content -LiteralPath (Join-Path $mdir ($room + '.txt')) -Value $prompt -Encoding UTF8 -Force
                    }
                    exit 0
                  }

                  # status = idle (Stop/SessionEnd) — 턴 종료/중단이므로 선택지 대기도 해제.
                  Set-Content -LiteralPath $waitFile -Value 'idle' -Encoding Ascii -Force
                  $evt = ''; try { $evt = '' + $j.hook_event_name } catch { }
                  $pending = 0
                  if ($evt -ne 'SessionEnd' -and $sid) {
                    try {
                      $meta = Get-MetaCount $tp
                      # baseline 미존재(훅 배포 직후 중간턴 등)면 현재 meta 를 기준으로 삼아 newLaunched=0 → idle(스피너 영구 회전 방지).
                      $base = $meta; if (Test-Path -LiteralPath $baseFile) { [int]::TryParse((Get-Content -LiteralPath $baseFile -Raw -ErrorAction SilentlyContinue).Trim(), [ref]$base) | Out-Null }
                      $newLaunched = $meta - $base
                      if ($newLaunched -lt 0) { $newLaunched = 0 }
                      $completed = 0; if (Test-Path -LiteralPath $doneFile) { $completed = @(Get-Content -LiteralPath $doneFile -ErrorAction SilentlyContinue | Sort-Object -Unique).Count }
                      $pending = $newLaunched - $completed
                      if ($pending -lt 0) { $pending = 0 }
                    } catch { $pending = 0 }
                  }

                  if ($pending -gt 0) {
                    Set-Content -LiteralPath $busyFile -Value 'running' -Encoding Ascii -Force
                  } else {
                    Set-Content -LiteralPath $busyFile -Value 'idle' -Encoding Ascii -Force
                    # 진짜 응답 완료(pending=0) → claude 가 stdin 으로 준 마지막 답변을 방별로 기록한다.
                    # Discord reply 가 이 파일을 바로 읽으므로 transcript 경로/세션ID 추적이 필요 없다.
                    if ($evt -ne 'SessionEnd') {
                      try {
                        $lastMsg = '' + $j.last_assistant_message
                        if ($lastMsg) {
                          $rdir = Join-Path $env:APPDATA 'DevezCode\claude\lastreply'
                          New-Item -ItemType Directory -Force -Path $rdir | Out-Null
                          Set-Content -LiteralPath (Join-Path $rdir ($room + '.txt')) -Value $lastMsg -Encoding UTF8 -Force
                        }
                      } catch { }
                    }
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

        var command         = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{HookScriptPath}\" {arg}";
        var statusCommand   = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{StatusLineScriptPath}\" {arg}";
        var busyRunCommand  = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{BusyHookScriptPath}\" running {arg}";
        var busyIdleCommand = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{BusyHookScriptPath}\" idle {arg}";
        var busyNotifyCommand = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{BusyHookScriptPath}\" notify {arg}";
        // refreshInterval 은 의도적으로 생략 — statusLine 은 event-driven 갱신으로 충분(idle 중 CPU 튐 방지).
        var statusLine = new { type = "command", command = statusCommand };
        var settings = new
        {
            // 세션 기록 보존 기간 — 마지막 활동일부터 이 일수가 지나면 claude 가 트랜스크립트를 자동 삭제(resume 불가). 30일.
            cleanupPeriodDays = 30,
            // theme 을 command-line scope(최우선)에 박아 auto(배경 자동감지) 경로를 제거 — ConPTY 에서 흰 화면 고착 방지.
            theme = ClaudeCustomThemes.MapToClaudeTheme(DevezCode.App.CurrentTheme),
            statusLine,
            hooks = new
            {
                SessionStart     = new[] { new { hooks = new[] { new { type = "command", command } } } },
                UserPromptSubmit = new[] { new { hooks = new[] { new { type = "command", command = busyRunCommand } } } },
                Stop             = new[] { new { hooks = new[] { new { type = "command", command = busyIdleCommand } } } },
                SessionEnd       = new[] { new { hooks = new[] { new { type = "command", command = busyIdleCommand } } } },
                // 선택지/권한 입력 대기 ❗ — Notification 진입. 해제는 UserPromptSubmit/Stop(파일) + 답변 입력(즉시 UI).
                Notification     = new[] { new { hooks = new[] { new { type = "command", command = busyNotifyCommand } } } },
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

    /// <summary>해당 세션 ID의 claude 대화 transcript(.jsonl) 실제 경로. 없으면 null.
    /// claude 는 대화를 %USERPROFILE%\.claude\projects\&lt;경로 인코딩&gt;\&lt;세션ID&gt;.jsonl 로 저장한다.
    /// 1) workingDir 인코딩으로 바로 확인(빠른 경로, 대부분 적중)
    /// 2) 실패하면 projects 하위 폴더 전체에서 &lt;sid&gt;.jsonl 을 검색한다 — working dir 이동/이름변경,
    ///    경로 인코딩 엣지(UNC·네트워크 드라이브 등), claude 의 인코딩 규칙 변경에도 sessionId(GUID 는 projects
    ///    전역에서 유일)로 정확히 찾아낸다. (예전엔 인코딩이 어긋나면 "대화 없음"으로 오판해 멀쩡한 세션
    ///    추적을 폐기 → 재실행 시 그 대화에 영영 못 붙었다.)</summary>
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
            if (!string.IsNullOrWhiteSpace(workingDir))
            {
                var full = Path.GetFullPath(workingDir);
                if (full.Length > 3) full = full.TrimEnd('\\', '/'); // 드라이브 루트(C:\)는 백슬래시 유지 — claude 인코딩(C--)과 일치
                var encoded = System.Text.RegularExpressions.Regex.Replace(full, "[^a-zA-Z0-9]", "-");
                var fast = Path.Combine(projects, encoded, file);
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
    /// <summary>현재 살아있는(셸 실행 중) 세션이 하나라도 있는지 — 종료 시 graceful 오버레이 표시 판단.</summary>
    public bool HasLiveSessions()
    {
        lock (_lock) return _sessions.Values.Any(s => s.IsAlive);
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
    public async Task GracefulShutdownAllAsync(int perGraceMs = 2500, int postFlushMs = 3000)
    {
        List<KeyValuePair<string, TerminalSession>> snapshot;
        lock (_lock) snapshot = _sessions.ToList();
        if (snapshot.Count == 0) return;

        try { await Task.WhenAll(snapshot.Select(kv => kv.Value.TryGracefulExitAsync(perGraceMs))); }
        catch { /* best effort */ }

        // 프로세스 종료 후 별도 훅 프로세스가 파일을 마저 쓸 여유(짧은 고정 지연).
        if (postFlushMs > 0) { try { await Task.Delay(postFlushMs); } catch { /* best effort */ } }

        lock (_lock)
        {
            foreach (var kv in snapshot)
            {
                try { kv.Value.Dispose(); } catch (Exception) { }
                _sessions.Remove(kv.Key);
            }
        }
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
            SettingsService.LoadCodexRoomSession(roomId),
            SettingsService.LoadOpenCodeRoomSession(roomId),
            SettingsService.LoadGajaeRoomSession(roomId),
        };
        DisposeRoom(roomId);

        PurgeAppOwnedRoomArtifacts(roomId, ids);

        if (string.IsNullOrWhiteSpace(workingDir)) return;
        try
        {
            var full = Path.GetFullPath(workingDir);
            if (full.Length > 3) full = full.TrimEnd('\\', '/'); // 드라이브 루트(C:\)는 백슬래시 유지 — claude 인코딩(C--)과 일치
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

    private static void PurgeAppOwnedRoomArtifacts(string roomId, IEnumerable<string?> sessionIds)
    {
        var roomFile = SafeRoomFileName(roomId);

        TryDeleteFile(Path.Combine(ClaudeTrackDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "lastmsg", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "lastreply", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "modeleffort", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "room-settings", roomFile + ".json"));
        TryDeleteFile(Path.Combine(ClaudeTrackDir, "sessions", roomFile + ".txt"));
        TryDeleteFiles(ClaudeTrackDir, "statusline-cache-" + roomFile + "-*.txt");

        foreach (var id in sessionIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDeleteFile(Path.Combine(ClaudeTrackDir, "busy", "_state", $"base_{id}.txt"));
            TryDeleteFile(Path.Combine(ClaudeTrackDir, "busy", "_state", $"done_{id}.txt"));
        }

        var codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "codex");
        TryDeleteFile(Path.Combine(codexDir, "sessions", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(codexDir, "lastmsg", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(codexDir, "busy", roomFile + ".txt"));
        TryDeleteFile(Path.Combine(CodexLaunchDir(), roomFile + ".cmd"));

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
            _claudeSessionWatcher?.Dispose();
            _claudeSessionWatcher = null;
        }
    }
}
