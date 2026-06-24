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

    // 마지막 jsonl 쓰기 후 이 시간 안이면 "처리중"으로 본다. gjc 에이전트 루프는 스트리밍·툴 사이
    // 수 초 간격으로 jsonl 을 쓰므로, 이 창으로 깜빡임을 줄이고 완료 후 자연스럽게 idle 로 내린다.
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(12);

    private readonly DispatcherTimer _poll;
    // roomId → 최신 jsonl 의 (경로, mtime). 변화 없으면 lastmsg 재파싱은 스킵(단 busy 는 매 틱 재평가).
    private readonly Dictionary<string, (string path, DateTime mtime)> _seen = new();
    private readonly Dictionary<string, string> _lastMsg = new();
    private readonly Dictionary<string, string?> _lastRole = new();
    private readonly Dictionary<string, bool> _busy = new();

    /// <summary>(roomId, message) — gjc 세션이 마지막으로 보낸 프롬프트(1줄 요약). 빈 문자열이면 세션명으로 표시.</summary>
    public event Action<string, string>? MessageChanged;

    /// <summary>(roomId, busy) — busy=true 면 요청 처리중(스피너).</summary>
    public event Action<string, bool>? BusyChanged;

    public GajaeLastMessageService()
    {
        // 스피너 반응성을 위해 1초 폴링(파일 작고 mtime 게이트라 비용 낮음).
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
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
        }
        catch { /* 다음 폴링 */ }
    }

    private void ScanRoom(string roomDir)
    {
        var roomId = Path.GetFileName(roomDir);
        if (string.IsNullOrEmpty(roomId)) return;

        var newest = new DirectoryInfo(roomDir)
            .GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newest == null) return;

        // 같은 파일·같은 mtime 이면 상태 변화 없음 → 스킵(파일 다시 안 읽음).
        if (_seen.TryGetValue(roomId, out var prev) && prev.path == newest.FullName && prev.mtime == newest.LastWriteTimeUtc)
            return;
        _seen[roomId] = (newest.FullName, newest.LastWriteTimeUtc);

        var (msg, busy) = ParseState(newest.FullName);

        // null(새 빈 세션) 이면 "" 로 emit → 헤더를 세션명으로 되돌린다.
        msg ??= "";
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
    }

    /// <summary>jsonl 을 뒤에서부터 훑어 (마지막 user 메시지 1줄 요약, 처리중 여부) 를 구한다.
    /// 줄 구조: {"type":"message","message":{"role":"user|assistant|toolResult","content":[{"type":"text","text":"..."}]}}
    /// busy = 마지막 message 엔트리 role 이 assistant 가 아님(user/toolResult = 응답 생성 대기/진행).</summary>
    private static (string? lastUserMsg, bool busy) ParseState(string path)
    {
        string[] lines;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            lines = sr.ReadToEnd().Split('\n');
        }
        catch { return (null, false); }

        string? lastUserMsg = null;
        bool busy = false;
        bool roleSeen = false;

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            string? role;
            string? userText = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "message") continue;
                if (!root.TryGetProperty("message", out var m)) continue;
                if (!m.TryGetProperty("role", out var r)) continue;
                role = r.GetString();

                if (role == "user" && m.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text"
                            && part.TryGetProperty("text", out var txt))
                        {
                            var s = txt.GetString();
                            if (string.IsNullOrWhiteSpace(s)) continue;
                            userText = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                            break;
                        }
                    }
                }
            }
            catch { continue; }

            // 가장 마지막 message 엔트리의 role 로 busy 판정(한 번만).
            if (!roleSeen)
            {
                roleSeen = true;
                busy = role is "user" or "toolResult";
            }

            if (role == "user" && lastUserMsg == null && userText != null)
                lastUserMsg = userText.Length > 200 ? userText.Substring(0, 200) : userText;

            if (roleSeen && lastUserMsg != null) break;
        }
        return (lastUserMsg, busy);
    }

    public void Dispose() => _poll.Stop();
}
