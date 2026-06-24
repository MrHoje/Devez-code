using System.IO;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>claude subagent 훅(subagent-hook.ps1)이 방별로 떨군 JSON 파일
/// (subagents\&lt;roomId&gt;\&lt;agentId&gt;.json)을 감시해 서브에이전트 시작/완료를 알린다.
/// 방 ID는 디렉터리명, agentId는 파일명(확장자 제외)에서 추출한다.</summary>
public sealed class SubagentStatusService : IDisposable
{
    private static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "claude", "subagents");

    private static string SessionDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "claude", "sessions");

    private static string ClaudeProjectsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private FileSystemWatcher? _watcher;

    /// <summary>(agent) — 서브에이전트 상태 변경(start/update/delete).</summary>
    public event Action<SubagentStatusItem>? SubagentChanged;
    public event Action<string, string>? SubagentRemoved; // (roomId, agentId)

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            // 앱 시작 시 기존 subagent 파일 모두 삭제(이전 세션의 stale 상태 제거).
            foreach (var roomDir in Directory.EnumerateDirectories(RootDir))
            {
                try { Directory.Delete(roomDir, recursive: true); }
                catch { /* hook write 와 경합 가능, 무시 */ }
            }
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(RootDir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
            _watcher.Created += (_, e) => OnFileEvent(e.FullPath);
            _watcher.Deleted += (_, e) => OnDeleted(e.FullPath);
        }
        catch { /* 감시 실패해도 앱은 계속 — 서브에이전트 패널만 안 뜸 */ }
    }

    private void OnFileEvent(string path)
    {
        var parsed = TryParsePath(path);
        if (parsed == null) return;
        var (roomId, agentId) = parsed.Value;
        var item = TryReadItem(path, roomId, agentId);
        if (item == null) return;
        SubagentChanged?.Invoke(item);
    }

    private void OnDeleted(string path)
    {
        var parsed = TryParsePath(path);
        if (parsed == null) return;
        SubagentRemoved?.Invoke(parsed.Value.roomId, parsed.Value.agentId);
    }

    /// <summary>경로에서 roomId(상위 디렉터리명)와 agentId(확장자 제외 파일명) 추출.
    /// 형식: subagents\&lt;roomId&gt;\&lt;agentId&gt;.json</summary>
    private static (string roomId, string agentId)? TryParsePath(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var roomId = dir != null ? Path.GetFileName(dir) : null;
        var agentId = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(agentId)) return null;
        return (roomId, agentId);
    }

    /// <summary>JSON 파일을 읽어 SubagentStatusItem 으로 변환. 쓰기 경합 시 짧게 재시도.</summary>
    private static SubagentStatusItem? TryReadItem(string path, string roomId, string agentId)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var json = sr.ReadToEnd().Trim();
                if (string.IsNullOrEmpty(json)) return null;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var item = new SubagentStatusItem
                {
                    RoomId = roomId,
                    AgentId = agentId,
                    AgentType = root.TryGetProperty("agentType", out var at) ? at.GetString() ?? "" : "",
                    TranscriptPath = ResolveConversationPath(roomId, agentId) ?? "",
                    Prompt = root.TryGetProperty("prompt", out var pr) ? pr.GetString() ?? "" : "",
                    Status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "running" : "running",
                    StartedAt = root.TryGetProperty("startedAt", out var sa) && sa.TryGetDateTime(out var sd) ? sd : DateTime.MinValue,
                    ToolCallCount = root.TryGetProperty("toolCallCount", out var tc) && tc.TryGetInt32(out var tn) ? tn : 0,
                    Reason = root.TryGetProperty("reason", out var re) ? re.GetString() ?? "" : "",
                };
                if (root.TryGetProperty("endedAt", out var ea) && ea.TryGetDateTime(out var ed))
                    item.EndedAt = ed;

                return item;
            }
            catch (IOException) { System.Threading.Thread.Sleep(20); }
            catch { return null; }
        }
        return null;
    }

    public static string? ResolveConversationPath(string roomId, string agentId)
    {
        try
        {
            var sessionPath = Path.Combine(SessionDir, SafeFileName(roomId) + ".txt");
            if (!File.Exists(sessionPath) || !Directory.Exists(ClaudeProjectsDir)) return null;

            var sessionId = File.ReadAllText(sessionPath).Trim();
            if (string.IsNullOrWhiteSpace(sessionId)) return null;

            var names = CandidateAgentFileNames(agentId).ToArray();
            foreach (var projectDir in Directory.EnumerateDirectories(ClaudeProjectsDir))
            {
                var subagentDir = Path.Combine(projectDir, sessionId, "subagents");
                if (!Directory.Exists(subagentDir)) continue;
                foreach (var name in names)
                {
                    var path = Path.Combine(subagentDir, name);
                    if (File.Exists(path)) return path;
                }
            }
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> CandidateAgentFileNames(string agentId)
    {
        foreach (var id in CandidateAgentIds(agentId))
            yield return id + ".jsonl";
    }

    private static IEnumerable<string> CandidateAgentIds(string agentId)
    {
        yield return agentId;
        if (agentId.StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
            yield return agentId.Substring("agent-".Length);
        else
            yield return "agent-" + agentId;
    }

    private static string SafeFileName(string value)
        => System.Text.RegularExpressions.Regex.Replace(value, @"[^\w\-]", "");

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
