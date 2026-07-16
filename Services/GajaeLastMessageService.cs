using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DevezCode.Services;

/// <summary>가재코드(gjc)가 직접 쓰는 세션 .jsonl을 증분 폴링해 두 가지를 알린다:
/// 1) 마지막 보낸 user 프롬프트(헤더 타이틀) — MessageChanged
/// 2) 요청 처리중 여부(좌측 스피너) — BusyChanged
/// 디렉터리: %AppData%\DevezCode\gajae\sessions\&lt;roomId&gt;\&lt;timestamp&gt;_&lt;id&gt;.jsonl
/// roomId 는 GUID("N") 라 디렉터리명이 곧 roomId.
/// <para>busy 판정: jsonl 의 마지막 message 엔트리 role 이 user/toolResult 면 처리중(assistant 응답 대기),
/// assistant 면 완료 후보이며, 같은 EOF가 다음 busy poll까지 유지되면 idle로 확정한다.
/// gjc 는 user 프롬프트를 보낼 때 즉시 jsonl 에 한 줄 기록하므로 "마지막이 user" = 응답 생성 중으로 본다.</para>
/// 외부 hook/extension 플래그는 일부 버전에서 도움말에만 노출되고 실제 파서가 값을 사용자 메시지로
/// 처리하므로 사용하지 않는다. JSONL은 구버전부터 유지된 GJC 자체 저장 형식이다.</summary>
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
    // GJC는 최종 assistant 레코드를 디스크에 먼저 기록한 뒤 TUI 출력 플러시를 마친다.
    // 같은 transcript 시그니처가 다음 poll까지 안정적으로 유지될 때만 idle을 확정해,
    // 최종 응답이 화면에 아직 출력 중인데 스피너가 먼저 사라지는 것을 막는다.
    private readonly Dictionary<string, (string Signature, DateTime FirstSeenUtc)> _idleCandidates = new();
    private readonly Dictionary<string, TranscriptCursor> _transcripts = new(StringComparer.OrdinalIgnoreCase);

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
    private static readonly TimeSpan IdleSettleDelay = TimeSpan.FromMilliseconds(900);
    private sealed class TranscriptCursor
    {
        public long Offset;
        public byte[] Pending = Array.Empty<byte>();
        public string? LastUserMessage;
        public bool Busy;
        public bool Waiting;
    }

    public GajaeLastMessageService()
    {
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = BusyInterval };
        _poll.Tick += (_, _) => Scan();
    }

    public void Start()
    {
        Scan(); // 시작 직후 한 번 확정해 이전 실행의 stale busy를 기다림 없이 idle로 정리.
        _poll.Start();
    }

    private void Scan()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var roomDir in Directory.EnumerateDirectories(Root))
                {
                    try { ScanRoom(roomDir); }
                    catch { /* 다음 폴링 */ }
                }
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
        var files = di.Exists ? di.GetFiles("*.jsonl", SearchOption.TopDirectoryOnly) : Array.Empty<FileInfo>();
        var newest = files
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        // /new 감지: gjc 는 새 세션의 디렉터리(<ts>_<id>/)를 즉시 만들지만 .jsonl 은 첫 메시지 전까지 안 쓴다.
        // 그래서 최신 .jsonl 보다 새로운 "orphan 디렉터리"(대응 .jsonl 없음)가 있으면 = 빈 새 세션 → 헤더 리셋.
        var jsonlIds = files
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newestOrphan = (di.Exists ? di.GetDirectories() : Array.Empty<DirectoryInfo>())
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
        string? sig = freshNew ? "NEW:" + newestOrphan!.Name
                : newest != null ? newest.FullName + "|" + contentLen + "|" + newest.LastWriteTimeUtc.Ticks
                : null;
        if (sig == null) return;
        // busy 로 마킹된 방은 sig 가 같아도 강제 재파싱한다.
        // gjc 는 .jsonl 핸들을 연 채 append 하는데, Windows 는 핸들이 열린 동안 디렉터리 엔트리의
        // LastWriteTime 을 즉시 안 갱신해(stale) 종료 줄(마지막 assistant text) append 가 mtime 에 안 잡힐 수 있다.
        // 그러면 sig 가 안 바뀌어 종료(busy=false) emit 을 놓치고 스피너가 영구히 도는 증상이 난다.
        // 파일 내용 읽기는 메타와 달리 항상 최신 바이트를 주므로, busy 인 동안 매 폴링 재파싱하면 종료를 확실히 감지한다.
        bool roomBusy = _busy.TryGetValue(roomId, out var wasBusyNow) && wasBusyNow;
        bool roomWaiting = _waiting.TryGetValue(roomId, out var wasWaitingNow) && wasWaitingNow;
        if (!roomBusy && !roomWaiting && _seen.TryGetValue(roomId, out var prev) && prev == sig) return;
        _seen[roomId] = sig;

        string? msg;
        bool busy;
        bool waiting;
        if (freshNew) { msg = ""; busy = false; waiting = false; }   // 빈 새 세션 → 헤더 세션명 복귀, idle
        else if (newest != null) (msg, busy, waiting) = ParseState(newest.FullName);
        else { msg = null; busy = false; waiting = false; }

        // 첫 스캔: 이전 실행에서 종료된 진행 상태는 취소된 것으로 간주, busy=false
        if (!_started)
        {
            busy = false;
            waiting = false;
            _idleCandidates.Remove(roomId);
        }
        else if (busy)
        {
            _idleCandidates.Remove(roomId);
        }
        else if (!freshNew && _busy.TryGetValue(roomId, out var wasBusy) && wasBusy)
        {
            // 최종 assistant JSONL append와 실제 TUI 출력 완료 사이에 짧은 시차가 있다.
            // 한 번 더 같은 EOF를 관찰한 뒤 idle로 내리면 중간 toolResult/user append도
            // 자연스럽게 후보를 취소하므로 고정 지연 타이머보다 상태 전이가 정확하다.
            var now = DateTime.UtcNow;
            if (!_idleCandidates.TryGetValue(roomId, out var candidate)
                || !string.Equals(candidate.Signature, sig, StringComparison.Ordinal))
            {
                _idleCandidates[roomId] = (sig, now);
                busy = true;
            }
            else if (now - candidate.FirstSeenUtc < IdleSettleDelay)
            {
                busy = true;
            }
            else
            {
                _idleCandidates.Remove(roomId);
            }
        }
        else
        {
            _idleCandidates.Remove(roomId);
        }

        msg ??= ""; // 안전망
        Publish(roomId, msg, busy, waiting);
    }

    private void Publish(string roomId, string msg, bool busy, bool waiting)
    {
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

    /// <summary>jsonl의 최초 스캔은 전체를 읽고 이후에는 append된 바이트만 순방향 파싱한다.
    /// 세션이 길어져도 매 1초마다 전체 대화를 다시 읽지 않으면서 마지막 user/상태 결과는 종전과 같다.
    /// busy 판정(시간 무관, 내용 기반): 마지막 message 엔트리 기준 —
    ///  • role=user/toolResult → 처리중(응답·다음 단계 대기)
    ///  • role=assistant + content 에 toolCall 있음 → 처리중(툴 실행/연속)
    ///  • role=assistant + text 만(toolCall 없음) → 완료(idle)
    /// gjc 한 턴: user → assistant(toolCall) → toolResult → … → assistant(text) 로 끝남.</summary>
    private (string? lastUserMsg, bool busy, bool waitingChoice) ParseState(string path)
    {
        if (!_transcripts.TryGetValue(path, out var cursor))
        {
            cursor = new TranscriptCursor();
            _transcripts[path] = cursor;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < cursor.Offset) // truncate/교체
            {
                cursor = new TranscriptCursor();
                _transcripts[path] = cursor;
            }
            fs.Position = cursor.Offset;

            using var line = new MemoryStream();
            if (cursor.Pending.Length > 0) line.Write(cursor.Pending);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                cursor.Offset += read;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        ProcessTranscriptLine(line.ToArray(), completeLine: true, cursor);
                        line.SetLength(0);
                    }
                    else line.WriteByte(buffer[i]);
                }
            }

            cursor.Pending = line.ToArray();
            // GJC는 보통 LF까지 한 번에 append한다. LF가 아직 관찰되지 않아도 완성 JSON이면 즉시 반영하고,
            // 쓰기 중인 부분 JSON이면 바이트를 보존해 다음 append와 합친다(UTF-8 경계도 손실 없음).
            if (cursor.Pending.Length > 0 && ProcessTranscriptLine(cursor.Pending, completeLine: false, cursor))
                cursor.Pending = Array.Empty<byte>();
        }
        catch
        {
            // 일시 잠금/IO 실패 때 직전 정상 상태를 보존한다. 다음 poll에서 append를 다시 읽는다.
        }
        return (cursor.LastUserMessage, cursor.Busy, cursor.Waiting);
    }

    /// <returns>부분 tail이 완성 JSON으로 소비됐거나, LF로 끝난 완전한 줄이면 true.</returns>
    private static bool ProcessTranscriptLine(byte[] bytes, bool completeLine, TranscriptCursor cursor)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).Trim();
            if (text.Length == 0) return true;
            if (text[0] != '{') return completeLine;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "message") return true;
            if (!root.TryGetProperty("message", out var m) || !m.TryGetProperty("role", out var r)) return true;

            var role = r.GetString();
            string? userText = null;
            bool hasToolCall = false;
            bool hasAskCall = false;
            if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (!part.TryGetProperty("type", out var pt)) continue;
                    var partType = pt.GetString();
                    if (partType == "toolCall")
                    {
                        hasToolCall = true;
                        if (part.TryGetProperty("name", out var name)
                            && string.Equals(name.GetString(), "ask", StringComparison.OrdinalIgnoreCase))
                            hasAskCall = true;
                    }
                    if (role == "user" && partType == "text" && userText == null
                        && part.TryGetProperty("text", out var txt))
                    {
                        var value = txt.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            userText = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    }
                }
            }

            cursor.Busy = role is "user" or "toolResult" || (role == "assistant" && hasToolCall);
            cursor.Waiting = role == "assistant" && hasAskCall;
            if (role == "user" && userText != null)
                cursor.LastUserMessage = userText.Length > 200 ? userText[..200] : userText;
            return true;
        }
        catch
        {
            // LF가 있으면 손상된 한 줄만 버리고 다음 줄을 계속 읽는다. tail이면 쓰기 중일 수 있어 보존.
            return completeLine;
        }
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
