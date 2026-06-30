using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DevezCode.Services;

/// <summary>가재코드(gjc)는 훅/플러그인/확장 콜백을 신뢰성 있게 못 받는다(확장 auto-discovery 가 앱 ConPTY 에서
/// 로드되지 않음). 그래서 gjc 가 직접 쓰는 세션 .jsonl 을 폴링해 두 가지를 알린다:
/// 1) 마지막 보낸 user 프롬프트(헤더 타이틀) — MessageChanged
/// 2) 요청 처리중 여부(좌측 스피너) — BusyChanged
/// 디렉터리: %AppData%\DevezCode\gajae\sessions\&lt;roomId&gt;\&lt;timestamp&gt;_&lt;id&gt;.jsonl
/// roomId 는 GUID("N") 라 디렉터리명이 곧 roomId.
/// <para>busy 판정: jsonl 의 마지막 message 엔트리 role 이 user/toolResult 면 처리중(assistant 응답 대기),
/// assistant 면 완료(idle). gjc 는 user 프롬프트를 보낼 때 즉시 jsonl 에 한 줄 기록하므로
/// "마지막이 user" = 응답 생성 중으로 본다.</para>
/// <para>한계: gjc /new 는 새 세션을 메모리에만 만들고 첫 메시지 전까지 .jsonl 을 안 써서, /new 직후엔
/// 헤더가 즉시 안 비워지고 새 프롬프트를 보내야 갱신된다(확장 콜백이 없어 즉시 신호를 못 받음).</para></summary>
public sealed class GajaeLastMessageService : IDisposable
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "gajae", "sessions");

    private readonly DispatcherTimer _poll;
    // roomId → 마지막으로 처리한 상태 시그니처(최신 .jsonl 경로+mtime, 또는 빈 새 세션 마커). 같으면 스킵.
    private readonly Dictionary<string, string> _seen = new();
    private readonly Dictionary<string, string> _lastMsg = new();
    private readonly Dictionary<string, bool> _busy = new();
    private readonly Dictionary<string, bool> _waiting = new();

    /// <summary>(roomId, message) — gjc 세션이 마지막으로 보낸 프롬프트(1줄 요약). 빈 문자열이면 세션명으로 표시.</summary>
    public event Action<string, string>? MessageChanged;

    /// <summary>(roomId, busy) — busy=true 면 요청 처리중(스피너).</summary>
    public event Action<string, bool>? BusyChanged;

    /// <summary>(roomId, waiting) — waiting=true 면 선택지('ask') 응답 대기 중(❗). jsonl 의 'ask' 툴콜로 판정.</summary>
    public event Action<string, bool>? WaitingChoiceChanged;
    private bool _started;

    // 적응형 폴링 주기: 처리중인 방이 있으면 스피너 종료를 빨리 감지하도록 BusyInterval(1s),
    // 전부 idle 이면 IdleInterval(2.5s)로 늦춰 다세션 시 디렉터리 열거·파일 open 부하를 낮춘다.
    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(2.5);

    public GajaeLastMessageService()
    {
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = BusyInterval };
        _poll.Tick += (_, _) => Scan();
    }

    public void Start() => _poll.Start();

    private void Scan()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var roomDir in Directory.EnumerateDirectories(Root))
            {
                try { ScanRoom(roomDir); }
                catch { /* 다음 폴링 */ }
            }
            _started = true;
        }
        catch { /* 다음 폴링 */ }

        // 처리중/대기 방이 하나라도 있으면 빠른 주기, 전부 idle 이면 느린 주기로 전환.
        var want = (_busy.Values.Any(b => b) || _waiting.Values.Any(w => w)) ? BusyInterval : IdleInterval;
        if (_poll.Interval != want) _poll.Interval = want;
    }

    private void ScanRoom(string roomDir)
    {
        var roomId = Path.GetFileName(roomDir);
        if (string.IsNullOrEmpty(roomId)) return;

        var di = new DirectoryInfo(roomDir);
        var newest = di.GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        // /new 감지: gjc 는 새 세션의 디렉터리(<ts>_<id>/)를 즉시 만들지만 .jsonl 은 첫 메시지 전까지 안 쓴다.
        // 그래서 최신 .jsonl 보다 새로운 "orphan 디렉터리"(대응 .jsonl 없음)가 있으면 = 빈 새 세션 → 헤더 리셋.
        var jsonlIds = di.GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newestOrphan = di.GetDirectories()
            .Where(sd => !jsonlIds.Contains(sd.Name))
            .OrderByDescending(sd => sd.LastWriteTimeUtc)
            .FirstOrDefault();
        bool freshNew = newestOrphan != null
            && (newest == null || newestOrphan.LastWriteTimeUtc > newest.LastWriteTimeUtc);

        // 상태 시그니처 — 빈 새 세션이면 orphan 디렉터리 기준, 아니면 최신 .jsonl(경로+실제길이+mtime) 기준.
        // 시그니처가 그대로면 변화 없음 → 스킵(busy 는 순수 내용 기반이라 파일/세션이 바뀔 때만 변한다).
        // mtime 만으로는 부족: gjc 가 핸들을 연 채 append 하면 디렉터리 엔트리의 mtime 이 stale(미갱신)일 수 있어
        // user 줄 append(=busy 진입)가 mtime 에 안 잡히면 sig 가 안 바뀌어 skip → 그 턴 내내 스피너가 안 뜬다(간헐적).
        // FileStream.Length(커널 실제 EOF)는 append 마다 항상 증가하므로 시그니처에 포함하면 stale 을 우회한다.
        long contentLen = newest != null ? RealContentLength(newest.FullName) : 0;
        var sig = freshNew ? "NEW:" + newestOrphan!.Name
                : newest != null ? newest.FullName + "|" + contentLen + "|" + newest.LastWriteTimeUtc.Ticks
                : null;
        if (sig == null) return;
        // busy 로 마킹된 방은 sig 가 같아도 강제 재파싱한다.
        // gjc 는 .jsonl 핸들을 연 채 append 하는데, Windows 는 핸들이 열린 동안 디렉터리 엔트리의
        // LastWriteTime 을 즉시 안 갱신해(stale) 종료 줄(마지막 assistant text) append 가 mtime 에 안 잡힐 수 있다.
        // 그러면 sig 가 안 바뀌어 종료(busy=false) emit 을 놓치고 스피너가 영구히 도는 증상이 난다.
        // 파일 내용 읽기는 메타와 달리 항상 최신 바이트를 주므로, busy 인 동안 매 폴링 재파싱하면 종료를 확실히 감지한다.
        bool roomBusy = _busy.TryGetValue(roomId, out var wasBusyNow) && wasBusyNow;
        if (!roomBusy && _seen.TryGetValue(roomId, out var prev) && prev == sig) return;
        _seen[roomId] = sig;

        string? msg;
        bool busy;
        bool waiting;
        if (freshNew) { msg = ""; busy = false; waiting = false; }   // 빈 새 세션 → 헤더 세션명 복귀, idle
        else (msg, busy, waiting) = ParseState(newest!.FullName);
        // 첫 스캔: 이전 실행에서 종료된 진행 상태는 취소된 것으로 간주, busy=false
        if (!_started) { busy = false; waiting = false; }

        msg ??= ""; // 안전망
        if (!_lastMsg.TryGetValue(roomId, out var wasMsg) || wasMsg != msg)
        {
            _lastMsg[roomId] = msg;
            MessageChanged?.Invoke(roomId, msg);
        }
        if (!_busy.TryGetValue(roomId, out var wasBusy) || wasBusy != busy)
        {
            _busy[roomId] = busy;
            BusyChanged?.Invoke(roomId, busy);
        }
        if (!_waiting.TryGetValue(roomId, out var wasWaiting) || wasWaiting != waiting)
        {
            _waiting[roomId] = waiting;
            WaitingChoiceChanged?.Invoke(roomId, waiting);
        }
    }

    /// <summary>jsonl 을 뒤에서부터 훑어 (마지막 user 메시지 1줄 요약, 처리중 여부) 를 구한다.
    /// busy 판정(시간 무관, 내용 기반): 마지막 message 엔트리 기준 —
    ///  • role=user/toolResult → 처리중(응답·다음 단계 대기)
    ///  • role=assistant + content 에 toolCall 있음 → 처리중(툴 실행/연속)
    ///  • role=assistant + text 만(toolCall 없음) → 완료(idle)
    /// gjc 한 턴: user → assistant(toolCall) → toolResult → … → assistant(text) 로 끝남.</summary>
    private static (string? lastUserMsg, bool busy, bool waitingChoice) ParseState(string path)
    {
        string[] lines;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            lines = sr.ReadToEnd().Split('\n');
        }
        catch { return (null, false, false); }

        string? lastUserMsg = null;
        bool busy = false;
        bool waitingChoice = false;
        bool busySeen = false;

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            string? role;
            string? userText = null;
            bool hasToolCall = false;
            bool hasAskCall = false; // 사용자에게 선택지를 묻는 'ask' 툴콜(응답 대기 신호)
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "message") continue;
                if (!root.TryGetProperty("message", out var m)) continue;
                if (!m.TryGetProperty("role", out var r)) continue;
                role = r.GetString();

                if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (!part.TryGetProperty("type", out var pt)) continue;
                        var ptype = pt.GetString();
                        if (ptype == "toolCall")
                        {
                            hasToolCall = true;
                            if (part.TryGetProperty("name", out var nm) && nm.GetString() == "ask") hasAskCall = true;
                        }
                        if (role == "user" && ptype == "text" && userText == null
                            && part.TryGetProperty("text", out var txt))
                        {
                            var s = txt.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                                userText = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                        }
                    }
                }
            }
            catch { continue; }

            // 가장 마지막 message 엔트리로 busy/대기 판정(한 번만).
            if (!busySeen)
            {
                busySeen = true;
                // user/toolResult = 진행중. assistant 는 toolCall 있을 때만 진행중(text 만이면 완료).
                // 그 외 role 은 idle(스턱 방지).
                busy = role is "user" or "toolResult" || (role == "assistant" && hasToolCall);
                // 마지막 엔트리가 assistant 의 'ask' 툴콜이면(뒤에 toolResult 없음) = 선택지 응답 대기.
                waitingChoice = role == "assistant" && hasAskCall;
            }

            if (role == "user" && lastUserMsg == null && userText != null)
                lastUserMsg = userText.Length > 200 ? userText.Substring(0, 200) : userText;

            if (busySeen && lastUserMsg != null) break;
        }
        return (lastUserMsg, busy, waitingChoice);
    }

    /// <summary>파일의 실제 콘텐츠 길이(바이트). gjc 가 핸들을 연 채 append 하는 동안 디렉터리 엔트리 기반
    /// FileInfo.Length/LastWriteTime 은 stale 일 수 있으나, FileStream 으로 열면 커널이 실제 EOF 를 반환해
    /// 항상 최신 길이를 준다. 변화 감지 시그니처에 사용해 busy 진입(append) 누락을 막는다.</summary>
    private static long RealContentLength(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.Length;
        }
        catch { return 0; }
    }

    public void Dispose() => _poll.Stop();
}
