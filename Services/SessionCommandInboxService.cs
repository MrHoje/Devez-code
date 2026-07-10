using System.IO;
using System.Text.Json;
using DevezCode.Services.Terminal;

namespace DevezCode.Services;

/// <summary>세션 간 지시 릴레이(수동 오케스트레이션) 수신부.
/// 플러그인 /send-to 커맨드가 %AppData%\DevezCode\commands\ 에 떨군 명령 파일(JSON)을
/// FileSystemWatcher 로 감지해, 대상 세션 터미널(ConPTY)에 프롬프트를 그대로 주입한다.
///
/// 단방향(fire-and-forget). 이름→roomId 해석은 플러그인이 sessions-index.json 을 읽어 이미 끝내므로,
/// 여기서는 targetRoomId 로 세션을 찾아 주입만 한다(앱은 해석 로직 없음).
/// 설계·근거: _reenterWatcher(TerminalSessionManager) 와 동일한 "파일 드롭 → FSW 감지" 패턴.</summary>
public sealed class SessionCommandInboxService
{
    private static string CommandDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "commands");

    private FileSystemWatcher? _watcher;

    private sealed class CommandDto
    {
        public string? TargetRoomId { get; set; }
        public string? Message { get; set; }
        /// <summary>true(기본): 주입 후 Enter(\r)로 즉시 제출. false: 붙여넣기만(제출 안 함).</summary>
        public bool Submit { get; set; } = true;
    }

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(CommandDir);
            // 앱이 꺼져 있는 동안 쌓였을 수 있는 파일을 먼저 처리(있다면).
            foreach (var f in Directory.EnumerateFiles(CommandDir, "*.json"))
                ProcessFile(f);

            _watcher = new FileSystemWatcher(CommandDir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            // send.js 는 <uuid>.tmp 로 쓴 뒤 <uuid>.json 으로 rename 한다(원자적 완성).
            // 같은 폴더 내 rename 은 Created 가 아니라 Renamed 로 오므로 둘 다 구독한다.
            _watcher.Created += (_, e) => ProcessFile(e.FullPath);
            _watcher.Renamed += (_, e) => ProcessFile(e.FullPath);
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* 감시 실패 = 릴레이만 미동작. 앱 다른 기능엔 영향 없음. */ }
    }

    /// <summary>명령 파일 1개 처리 → 주입 → 삭제. FSW 백그라운드 스레드에서 호출된다
    /// (TerminalSession.Write / Manager.Get 은 자체 락으로 스레드 안전).</summary>
    private static void ProcessFile(string path)
    {
        CommandDto? dto = null;
        // 쓰기 완료 직전 이벤트가 올 수 있어 짧게 재시도(잠김/부분쓰기 방어).
        for (int i = 0; i < 12; i++)
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
            catch { /* 아직 쓰는 중 / 잠김 → 잠깐 대기 후 재시도 */ }
            System.Threading.Thread.Sleep(30);
        }

        try { File.Delete(path); } catch { /* 이미 지워짐 등 */ }

        if (dto == null || string.IsNullOrWhiteSpace(dto.TargetRoomId) || string.IsNullOrEmpty(dto.Message))
            return;

        var session = TerminalSessionManager.Instance.Get(dto.TargetRoomId!);
        if (session == null || !session.IsAlive) return;   // 대상 세션 미실행 — v1 은 조용히 무시.
        session.Write(dto.Submit ? dto.Message + "\r" : dto.Message);
    }
}
