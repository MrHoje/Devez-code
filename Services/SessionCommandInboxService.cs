using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using DevezCode.Services.Terminal;

namespace DevezCode.Services;

/// <summary>세션 간 지시 릴레이(수동 오케스트레이션) 수신부 + 자식 세션 위임.
///
/// (1) 지시 주입(/dvzc:send): commands\ 에 떨군 명령 파일을 감지해 대상 세션 터미널에 프롬프트를 주입.
///     대상이 꺼져 있으면 MainWindow 에 백그라운드 시작을 요청하고, 실제 주입 성공 전까지 명령 파일을 보존한다.
/// (2) 자식 위임(/dvzc:child): action="new-child" 명령이 오면 ChildSessionRequested 이벤트로 MainWindow(UI스레드)에
///     "부모 A의 자식 세션 생성"을 요청. 생성 후 InjectWhenReady 가 자식 부팅 완료를 기다렸다 브리핑을 주입한다.
///
/// 단방향(fire-and-forget) — 대상/자식의 완료를 발신자로 되돌리는 콜백은 없다.
/// 설계·근거: _reenterWatcher(TerminalSessionManager) 와 동일한 "파일 드롭 → FSW 감지" 패턴.</summary>
public sealed class SessionCommandInboxService
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode");
    private static string CommandDir => Path.Combine(BaseDir, "commands");
    // claude SessionStart 훅(room-hook.ps1)이 부팅 시 <roomId>.txt 를 쓰는 곳 — 자식 "준비됨" 신호로 사용.
    private static string ClaudeSessionsDir => Path.Combine(BaseDir, "claude", "sessions");
    private static string SessionStartFile(string roomId) => Path.Combine(ClaudeSessionsDir, SafeName(roomId) + ".txt");

    /// <summary>roomId → 안전한 파일명(GUID 라 보통 그대로지만 방어적으로 영숫자/하이픈/밑줄만).</summary>
    private static string SafeName(string roomId)
    {
        var chars = (roomId ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        return new string(chars);
    }

    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _injectLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _readyGeneration = new(StringComparer.Ordinal);

    /// <summary>자식 세션 생성 요청(부모 roomId, 세션명, 브리핑). FSW 백그라운드 스레드에서 발화 → 구독자(MainWindow)가 UI 스레드로 마샬링.</summary>
    public event Action<string, string, string>? ChildSessionRequested;
    /// <summary>꺼진 기존 세션 시작 요청(roomId, 메시지, submit, 원본 명령 파일). 원본 파일은 주입 성공 후 삭제한다.</summary>
    public event Action<string, string, bool, string>? DormantSessionRequested;

    private sealed class CommandDto
    {
        /// <summary>null/"inject"=대상에 주입, "new-child"=부모의 자식 세션 생성 후 주입.</summary>
        public string? Action { get; set; }
        public string? TargetRoomId { get; set; }
        /// <summary>new-child 전용: 부모(A) roomId.</summary>
        public string? ParentRoomId { get; set; }
        /// <summary>new-child 전용: 자식 세션 이름(A가 지시 내용으로 15자 이내로 지음). 비면 기본명 폴백.</summary>
        public string? SessionName { get; set; }
        public string? Message { get; set; }
        /// <summary>true(기본): 주입 후 Enter(\r)로 즉시 제출. false: 붙여넣기만.</summary>
        public bool Submit { get; set; } = true;
    }

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(CommandDir);
            foreach (var f in Directory.EnumerateFiles(CommandDir, "*.json"))
                ProcessFile(f);

            _watcher = new FileSystemWatcher(CommandDir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            // send*.js 는 <uuid>.tmp 로 쓴 뒤 <uuid>.json 으로 rename(원자적). 같은 폴더 rename 은 Renamed 로 온다.
            _watcher.Created += (_, e) => ProcessFile(e.FullPath);
            _watcher.Renamed += (_, e) => ProcessFile(e.FullPath);
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* 감시 실패 = 릴레이만 미동작. 앱 다른 기능엔 영향 없음. */ }
    }

    /// <summary>명령 파일 1개 처리. 즉시 처리 가능한 명령은 삭제하고, 꺼진 대상용 명령은 주입 성공까지 보존한다.
    /// FSW 백그라운드 스레드에서 호출됨(TerminalSession.Write / Manager.Get 은 자체 락으로 스레드 안전).</summary>
    private void ProcessFile(string path)
    {
        if (!_inFlight.TryAdd(path, 0)) return;

        CommandDto? dto = null;
        for (int i = 0; i < 12; i++)   // 쓰기 완료 직전 이벤트 대비 짧게 재시도
        {
            try
            {
                if (!File.Exists(path)) { Release(path); return; }
                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    dto = JsonSerializer.Deserialize<CommandDto>(text,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    break;
                }
            }
            catch { /* 아직 쓰는 중 / 잠김 */ }
            System.Threading.Thread.Sleep(30);
        }

        if (dto == null || string.IsNullOrEmpty(dto.Message)) { Complete(path); return; }

        // (2) 자식 세션 위임 — 세션이 아직 없으므로 MainWindow(UI)에 생성 요청만.
        if (string.Equals(dto.Action, "new-child", StringComparison.OrdinalIgnoreCase))
        {
            var parent = dto.ParentRoomId?.Trim();
            Complete(path);
            if (!string.IsNullOrEmpty(parent))
                ChildSessionRequested?.Invoke(parent!, (dto.SessionName ?? "").Trim(), dto.Message!);
            return;
        }

        // (1) 기존 세션에 주입.
        if (string.IsNullOrWhiteSpace(dto.TargetRoomId)) { Complete(path); return; }
        var session = TerminalSessionManager.Instance.Get(dto.TargetRoomId!);
        if (session != null && session.IsAlive)
        {
            if (InjectText(dto.TargetRoomId!, session, dto.Message!, dto.Submit)) Complete(path);
            else ScheduleRetry(path);
            return;
        }

        // 대상이 꺼져 있으면 파일을 남긴 채 UI에 백그라운드 시작을 요청한다.
        // 구독자가 없으면 처리할 수 없으므로 무한 잔류시키지 않고 폐기한다.
        var wake = DormantSessionRequested;
        if (wake != null)
        {
            try { wake.Invoke(dto.TargetRoomId!, dto.Message!, dto.Submit, path); }
            catch { ScheduleRetry(path); }
        }
        else Complete(path);
    }

    private void Complete(string path)
    {
        // 주입 완료 파일이 잠깐 잠겨 삭제에 실패해도 다음 앱 실행에서 중복 전달되지 않도록
        // 먼저 watcher 대상이 아닌 확장자로 원자적 은퇴시킨 뒤 삭제한다.
        try
        {
            if (File.Exists(path))
            {
                var retired = path + ".done";
                File.Move(path, retired, overwrite: true);
                try { File.Delete(retired); } catch { }
            }
        }
        catch
        {
            try { File.Delete(path); } catch { }
        }
        Release(path);
    }

    private void Release(string path) => _inFlight.TryRemove(path, out _);

    /// <summary>워크스페이스에 더 이상 없는 대상 등 복구 불가능한 보류 명령을 폐기한다.</summary>
    public void DiscardPendingCommand(string path) => Complete(path);

    /// <summary>UI 처리 중 일시 오류가 난 보류 명령을 잠시 뒤 다시 처리한다.</summary>
    public void RetryPendingCommand(string path) => ScheduleRetry(path);

    /// <summary>TerminalHostView가 실제 TUI 준비 완료를 알리면 세대값을 올려 대기 중 주입을 깨운다.</summary>
    public void NotifyTerminalReady(string roomId)
        => _readyGeneration.AddOrUpdate(roomId, 1, static (_, current) => current + 1);

    /// <summary>텍스트를 세션 터미널(TUI)에 주입. 멀티라인이면 bracketed paste 로 감싸(조기 제출/줄분해 방지)
    /// 뒤 Enter 로 제출한다. 단일 라인이면 그대로 쓴다. submit=false 면 붙여넣기만(제출 안 함).</summary>
    private bool InjectText(string roomId, TerminalSession session, string text, bool submit)
    {
        // 같은 방으로 동시에 도착한 메시지들이 bracketed paste와 Enter 사이에 끼어들지 않게
        // 메시지 하나의 전체 입력을 방 단위로 직렬화한다.
        lock (_injectLocks.GetOrAdd(roomId, static _ => new object()))
        {
            bool multiline = text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0;
            if (multiline)
            {
                if (!session.TryWrite("\x1b[200~" + text + "\x1b[201~")) return false; // bracketed paste
                if (submit) { System.Threading.Thread.Sleep(120); return session.TryWrite("\r"); }
                return true;
            }
            return session.TryWrite(submit ? text + "\r" : text);
        }
    }

    /// <summary>새로 만든 자식 세션이 부팅 완료되면 브리핑을 주입한다.
    /// MainWindow.CreateChildAndDispatch 가 세션 생성·시작 직후 호출. 백그라운드 Task 에서 준비 신호를 폴링.</summary>
    public void InjectWhenReady(string childRoomId, string brief)
        => InjectWhenReadyCore(childRoomId, brief, submit: true, commandPath: null,
            agentId: "claude", requireFreshReadySignal: false);

    /// <summary>꺼져 있던 기존 세션이 새로 준비되면 보존 중인 명령을 주입하고, 성공한 경우에만 파일을 삭제한다.
    /// 호출 시점의 준비 파일 시각을 먼저 캡처하므로 이전 실행의 stale 파일을 준비 완료로 오인하지 않는다.</summary>
    public void InjectPendingWhenReady(string roomId, string message, bool submit, string commandPath,
        string agentId, bool requireFreshReadySignal)
        => InjectWhenReadyCore(roomId, message, submit, commandPath, agentId, requireFreshReadySignal);

    private void InjectWhenReadyCore(string roomId, string text, bool submit, string? commandPath,
        string agentId, bool requireFreshReadySignal)
    {
        var readyFile = ReadyFile(roomId, agentId);
        var baselineReadyGeneration = _readyGeneration.TryGetValue(roomId, out var generation) ? generation : 0;
        DateTime? baselineWriteUtc = null;
        if (requireFreshReadySignal && readyFile != null)
        {
            try { if (File.Exists(readyFile)) baselineWriteUtc = File.GetLastWriteTimeUtc(readyFile); }
            catch { }
        }

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                bool ready = false;
                int alivePolls = 0;
                // 훅/플러그인 기반 에이전트는 이번 시작에서 준비 파일이 새로 기록될 때까지 기다린다.
                // 모든 에이전트 공통으로 TerminalReady 세대를 우선 사용하고, 신호가 누락돼도 살아 있는
                // 프로세스는 약 8초 뒤 루프를 빠져나와 아래 3초 안전 여유 후 주입한다.
                for (int i = 0; i < 90; i++)
                {
                    var probe = TerminalSessionManager.Instance.Get(roomId);
                    if (probe != null && probe.IsAlive)
                    {
                        alivePolls++;
                        if (_readyGeneration.TryGetValue(roomId, out var currentGeneration)
                            && currentGeneration > baselineReadyGeneration)
                        {
                            ready = true;
                            break;
                        }
                        try
                        {
                            if (readyFile != null && File.Exists(readyFile)
                                && (!requireFreshReadySignal || baselineWriteUtc == null
                                    || File.GetLastWriteTimeUtc(readyFile) > baselineWriteUtc.Value))
                            {
                                ready = true;
                                break;
                            }
                        }
                        catch { }
                        if (alivePolls >= 16) break;
                    }
                    await System.Threading.Tasks.Task.Delay(500);
                }

                var s = TerminalSessionManager.Instance.Get(roomId);
                if (s == null || !s.IsAlive)
                {
                    if (commandPath != null) ScheduleRetry(commandPath);
                    return;
                }
                // 신호를 받았으면 TUI 입력 준비 여유만, 못 받았으면(훅 미설치 등) 조금 더 기다렸다 그래도 시도.
                await System.Threading.Tasks.Task.Delay(ready ? 1200 : 3000);

                var injected = InjectText(roomId, s, text, submit);
                if (commandPath != null)
                {
                    if (injected) Complete(commandPath);
                    else ScheduleRetry(commandPath);
                }
            }
            catch
            {
                // 파일을 남겨 다음 재시도/앱 재실행에서 다시 전달한다.
                if (commandPath != null) ScheduleRetry(commandPath);
            }
        });
    }

    private static string? ReadyFile(string roomId, string agentId)
    {
        var safe = SafeName(roomId) + ".txt";
        return (agentId ?? "").Trim().ToLowerInvariant() switch
        {
            "codex" => Path.Combine(BaseDir, "codex", "sessions", safe),
            "opencode" => Path.Combine(BaseDir, "opencode", "sessions", safe),
            "gajae" => null,
            _ => SessionStartFile(roomId),
        };
    }

    private void ScheduleRetry(string path)
    {
        Release(path);
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(5000);
            if (File.Exists(path)) ProcessFile(path);
        });
    }
}
