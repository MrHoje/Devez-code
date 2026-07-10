using System.IO;
using System.Text.Json;
using DevezCode.Services.Terminal;

namespace DevezCode.Services;

/// <summary>세션 간 지시 릴레이(수동 오케스트레이션) 수신부 + 자식 세션 위임.
///
/// (1) 지시 주입(/devez-send): commands\ 에 떨군 명령 파일을 감지해 대상 세션 터미널에 프롬프트를 주입.
/// (2) 자식 위임(/devez-send-new): action="new-child" 명령이 오면 ChildSessionRequested 이벤트로 MainWindow(UI스레드)에
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

    /// <summary>자식 세션 생성 요청(부모 roomId, 세션명, 브리핑). FSW 백그라운드 스레드에서 발화 → 구독자(MainWindow)가 UI 스레드로 마샬링.</summary>
    public event Action<string, string, string>? ChildSessionRequested;

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

    /// <summary>명령 파일 1개 처리 → (액션별) 주입 또는 자식생성 요청 → 삭제.
    /// FSW 백그라운드 스레드에서 호출됨(TerminalSession.Write / Manager.Get 은 자체 락으로 스레드 안전).</summary>
    private void ProcessFile(string path)
    {
        CommandDto? dto = null;
        for (int i = 0; i < 12; i++)   // 쓰기 완료 직전 이벤트 대비 짧게 재시도
        {
            try
            {
                if (!File.Exists(path)) return;
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

        try { File.Delete(path); } catch { }

        if (dto == null || string.IsNullOrEmpty(dto.Message)) return;

        // (2) 자식 세션 위임 — 세션이 아직 없으므로 MainWindow(UI)에 생성 요청만.
        if (string.Equals(dto.Action, "new-child", StringComparison.OrdinalIgnoreCase))
        {
            var parent = dto.ParentRoomId?.Trim();
            if (!string.IsNullOrEmpty(parent))
                ChildSessionRequested?.Invoke(parent!, (dto.SessionName ?? "").Trim(), dto.Message!);
            return;
        }

        // (1) 기존 세션에 주입.
        if (string.IsNullOrWhiteSpace(dto.TargetRoomId)) return;
        var session = TerminalSessionManager.Instance.Get(dto.TargetRoomId!);
        if (session == null || !session.IsAlive) return;   // 대상 미실행 — 조용히 무시.
        InjectText(session, dto.Message!, dto.Submit);
    }

    /// <summary>텍스트를 세션 터미널(TUI)에 주입. 멀티라인이면 bracketed paste 로 감싸(조기 제출/줄분해 방지)
    /// 뒤 Enter 로 제출한다. 단일 라인이면 그대로 쓴다. submit=false 면 붙여넣기만(제출 안 함).</summary>
    private static void InjectText(TerminalSession session, string text, bool submit)
    {
        bool multiline = text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0;
        if (multiline)
        {
            session.Write("\x1b[200~" + text + "\x1b[201~");   // bracketed paste
            if (submit) { System.Threading.Thread.Sleep(120); session.Write("\r"); }
        }
        else
        {
            session.Write(submit ? text + "\r" : text);
        }
    }

    /// <summary>새로 만든 자식 세션이 부팅 완료되면 브리핑을 주입한다.
    /// MainWindow.CreateChildAndDispatch 가 세션 생성·시작 직후 호출. 백그라운드 Task 에서 준비 신호를 폴링.</summary>
    public void InjectWhenReady(string childRoomId, string brief)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var readyFile = SessionStartFile(childRoomId);
                bool ready = false;
                // claude SessionStart 훅이 세션파일을 쓰면 준비됨(신규 세션도 SessionStart 발화). 최대 ~45s.
                for (int i = 0; i < 90; i++)
                {
                    var probe = TerminalSessionManager.Instance.Get(childRoomId);
                    if (probe != null && probe.IsAlive && File.Exists(readyFile)) { ready = true; break; }
                    await System.Threading.Tasks.Task.Delay(500);
                }

                var s = TerminalSessionManager.Instance.Get(childRoomId);
                if (s == null || !s.IsAlive) return;
                // 신호를 받았으면 TUI 입력 준비 여유만, 못 받았으면(훅 미설치 등) 조금 더 기다렸다 그래도 시도.
                await System.Threading.Tasks.Task.Delay(ready ? 1200 : 3000);

                InjectText(s, brief, true);   // 멀티라인 브리핑 → bracketed paste + 제출
            }
            catch { /* best-effort */ }
        });
    }
}
