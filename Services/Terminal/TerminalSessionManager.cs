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
    private readonly object _lock = new();
    private WtTerminalConfig? _config;

    /// <summary>삭제된 방 ID. 삭제 직후 뒤늦게 도착한 생성 요청으로 claude 가 다시 떠 고아가 되는 것을 막는다.</summary>
    private readonly HashSet<string> _disposedRooms = new();

    /// <summary>방이 삭제되었는지(다시 세션을 만들면 안 됨).</summary>
    public bool IsRoomDisposed(string roomId)
    {
        lock (_lock) return _disposedRooms.Contains(roomId);
    }

    /// <summary>방별 "셸 준비 후 주입" 초기 커맨드. 직접 실행(cmd /k) 방·일반 방은 null.
    /// GetOrCreate 가 결정해 채우고 GetInitialCommand 가 1회 소비한다.</summary>
    private readonly Dictionary<string, string?> _pendingInitial = new();

    private TerminalSessionManager() { }

    /// <summary>WT settings.json 기반 구성 (최초 1회 로드 후 캐시).</summary>
    public WtTerminalConfig Config
    {
        get
        {
            lock (_lock) return _config ??= WtSettingsLoader.Load();
        }
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
            if (ccDir != null)
            {
                startDir = ccDir;
                var direct = TryBuildDirectLaunch(roomId, cfg.CommandLine, out inject);
                if (direct != null) commandLine = direct; // 성공 시 inject == null
            }

            TerminalSession session;
            try
            {
                // SessionStart 훅(room-hook.ps1)이 어느 방의 claude 세션인지 알 수 있게
                // 방 ID를 자식(cmd→claude→훅)에 상속시킨다. 생성 직후 해제해 다른 자식 프로세스로 새지 않게 한다.
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
            return session;
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

        var sessionId = SettingsService.LoadClaudeCodeRoomSession(roomId);

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

    // 방별 claude 직접 실행 배치(cmd /k 로 띄움). 매 실행 시 최신 커맨드로 덮어쓴다.
    private static string LaunchDir => Path.Combine(ClaudeTrackDir, "launch");
    private static string LaunchBatchPath(string roomId) => Path.Combine(LaunchDir, SafeRoomFileName(roomId) + ".cmd");

    /// <summary>roomId를 파일명으로 안전하게 (훅 ps1의 -replace 와 동일 규칙).</summary>
    private static string SafeRoomFileName(string roomId)
        => System.Text.RegularExpressions.Regex.Replace(roomId, @"[^\w\-]", "");

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

            var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{HookScriptPath}\"";
            var settings = new
            {
                // 세션 기록 보존 기간 — 마지막 활동일부터 이 일수가 지나면 claude 가 트랜스크립트를
                // 자동 삭제한다(그 세션은 resume 불가). 30일 보존.
                cleanupPeriodDays = 30,
                hooks = new
                {
                    SessionStart = new[] { new { hooks = new[] { new { type = "command", command } } } }
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

    /// <summary>채팅방 삭제 시 호출 — 해당 방의 셸 프로세스 정리.</summary>
    public void DisposeRoom(string roomId)
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
        // 추적 파일도 정리 (남아있으면 같은 roomId 재사용 시 엉뚱한 세션으로 이어붙음)
        try { File.Delete(Path.Combine(ClaudeTrackDir, "sessions", SafeRoomFileName(roomId) + ".txt")); }
        catch (Exception) { }
        try { File.Delete(LaunchBatchPath(roomId)); } catch (Exception) { }
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
        }
    }
}
