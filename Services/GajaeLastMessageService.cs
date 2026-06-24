using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DevezCode.Services;

/// <summary>가재코드(gjc)는 훅/플러그인 lastmsg 를 안 떨궈서, 방별 세션 디렉터리의 .jsonl 을
/// 폴링해 "마지막 보낸 user 메시지"를 알린다. (claude/codex 는 훅, opencode 는 플러그인+폴링)
/// 디렉터리: %AppData%\DevezCode\gajae\sessions\&lt;roomId&gt;\&lt;timestamp&gt;_&lt;id&gt;.jsonl
/// roomId 는 GUID("N") 라 디렉터리명이 곧 roomId → 그대로 MessageChanged 로 흘린다.</summary>
public sealed class GajaeLastMessageService : IDisposable
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "gajae", "sessions");

    private readonly DispatcherTimer _poll;
    // roomId → (마지막으로 읽은 최신 jsonl 의 mtime, 마지막으로 emit 한 메시지)
    private readonly Dictionary<string, DateTime> _seenMtime = new();
    private readonly Dictionary<string, string> _lastMsg = new();

    /// <summary>(roomId, message) — gjc 세션이 마지막으로 보낸 프롬프트(1줄 요약).</summary>
    public event Action<string, string>? MessageChanged;

    public GajaeLastMessageService()
    {
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
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

        // mtime 변화 없으면 스킵 (파일 다시 안 읽음).
        if (_seenMtime.TryGetValue(roomId, out var prev) && prev == newest.LastWriteTimeUtc) return;
        _seenMtime[roomId] = newest.LastWriteTimeUtc;

        var msg = ExtractLastUserMessage(newest.FullName);
        if (string.IsNullOrEmpty(msg)) return;
        if (_lastMsg.TryGetValue(roomId, out var was) && was == msg) return;
        _lastMsg[roomId] = msg;
        MessageChanged?.Invoke(roomId, msg);
    }

    /// <summary>jsonl 을 뒤에서부터 훑어 마지막 user 메시지의 첫 text 를 1줄로 압축해 반환.
    /// 줄 구조: {"type":"message","message":{"role":"user","content":[{"type":"text","text":"..."}]}}</summary>
    private static string? ExtractLastUserMessage(string path)
    {
        string[] lines;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            lines = sr.ReadToEnd().Split('\n');
        }
        catch { return null; }

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "message") continue;
                if (!root.TryGetProperty("message", out var m)) continue;
                if (!m.TryGetProperty("role", out var r) || r.GetString() != "user") continue;
                if (!m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text"
                        && part.TryGetProperty("text", out var txt))
                    {
                        var s = txt.GetString();
                        if (string.IsNullOrWhiteSpace(s)) continue;
                        var compact = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                        return compact.Length > 200 ? compact.Substring(0, 200) : compact;
                    }
                }
            }
            catch { /* 다음 줄 */ }
        }
        return null;
    }

    public void Dispose() => _poll.Stop();
}
