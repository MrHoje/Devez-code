using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DevezCode.Services;

public sealed record ClaudePersistedRuntimeMetadata(
    string? Model,
    string? Effort,
    long? ContextTokens,
    long? ContextWindow,
    string? SessionId);

/// <summary>claude statusLine 훅이 방별로 떨군 실제 model/effort(modeleffort\&lt;room&gt;.txt = "modelId\neffortLevel")를
/// 감시해 메타바 콤보에 라이브 연동한다. statusLine 은 매 메시지/턴마다 호출되므로 TUI 안에서 /model 로 바꿔도 반영된다.</summary>
public sealed class ModelEffortService : IDisposable
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude", "modeleffort");
    private static string CapturedStatusLinePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude", "ratelimit.json");

    private FileSystemWatcher? _watcher;

    /// <summary>(roomId, modelId, effortLevel) — 파일에 기록된 원본 값(매핑 전).</summary>
    public event Action<string, string?, string?>? Changed;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(Dir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, e) => Emit(e.FullPath);
            _watcher.Created += (_, e) => Emit(e.FullPath);
        }
        catch { /* 감시 실패해도 앱은 계속 — 콤보가 저장값/기본값으로만 동작 */ }
    }

    /// <summary>현재 디스크에 기록된 방의 (modelId, effortLevel). 없으면 (null, null). 초기 표시용 동기 조회.</summary>
    public (string? model, string? effort) Read(string roomId)
    {
        try
        {
            var path = Path.Combine(Dir, SafeRoom(roomId) + ".txt");
            return ParseFile(path);
        }
        catch { return (null, null); }
    }

    private void Emit(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var (model, effort) = ParseFile(path);
        if (model == null && effort == null) return;
        Changed?.Invoke(room, model, effort);
    }

    /// <summary>"modelId\neffortLevel" 2줄 파일을 읽는다. 쓰기 경합 시 짧게 재시도.</summary>
    private static (string? model, string? effort) ParseFile(string path)
    {
        var metadata = ParseMetadataFile(path);
        return (metadata.Model, metadata.Effort);
    }

    private static ClaudePersistedRuntimeMetadata ParseMetadataFile(string path)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!File.Exists(path)) return new(null, null, null, null, null);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return ParsePersistedContent(sr.ReadToEnd());
            }
            catch (IOException) { System.Threading.Thread.Sleep(20); }
            catch { return new(null, null, null, null, null); }
        }
        return new(null, null, null, null, null);
    }

    public static ClaudePersistedRuntimeMetadata ParsePersistedContent(string? content)
    {
        var lines = (content ?? "").Replace("\r", "").Split('\n');
        var model = lines.Length > 0 && lines[0].Trim().Length > 0 ? lines[0].Trim() : null;
        var effort = lines.Length > 1 && lines[1].Trim().Length > 0 ? lines[1].Trim() : null;
        long? contextTokens = lines.Length > 2 && long.TryParse(lines[2].Trim(), out var used) ? used : null;
        long? contextWindow = lines.Length > 3 && long.TryParse(lines[3].Trim(), out var window) ? window : null;
        var sessionId = lines.Length > 4 && lines[4].Trim().Length > 0 ? lines[4].Trim() : null;
        return new(model, effort, contextTokens, contextWindow, sessionId);
    }

    /// <summary>statusLine 훅이 영속한 방의 마지막 (modelId, effort). 인스턴스 없이 런치 시점에 조회용.</summary>
    public static (string? model, string? effort) ReadPersisted(string roomId)
    {
        try { return ParseFile(Path.Combine(Dir, SafeRoom(roomId) + ".txt")); }
        catch { return (null, null); }
    }

    /// <summary>새 포맷의 context/session ID까지 포함한 마지막 statusLine 상태. 기존 2줄 파일도 호환한다.</summary>
    public static ClaudePersistedRuntimeMetadata ReadPersistedMetadata(string roomId)
    {
        try { return ParseMetadataFile(Path.Combine(Dir, SafeRoom(roomId) + ".txt")); }
        catch { return new(null, null, null, null, null); }
    }

    /// <summary>기존 설치도 가진 마지막 원본 statusLine 캡처. session ID가 일치할 때만 복원값으로 반환한다.</summary>
    public static ClaudePersistedRuntimeMetadata ReadCapturedStatusLine(string? expectedSessionId)
    {
        if (string.IsNullOrWhiteSpace(expectedSessionId)) return new(null, null, null, null, null);
        try
        {
            using var stream = new FileStream(
                CapturedStatusLinePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!TryJsonString(root, "session_id", out var sessionId)
                || !string.Equals(sessionId, expectedSessionId, StringComparison.OrdinalIgnoreCase))
                return new(null, null, null, null, null);

            string? model = null;
            if (root.TryGetProperty("model", out var modelNode)
                && modelNode.ValueKind == JsonValueKind.Object
                && TryJsonString(modelNode, "id", out var modelId))
                model = modelId;
            string? effort = null;
            if (root.TryGetProperty("effort", out var effortNode)
                && effortNode.ValueKind == JsonValueKind.Object
                && TryJsonString(effortNode, "level", out var effortLevel))
                effort = effortLevel;
            long? used = null;
            long? window = null;
            if (root.TryGetProperty("context_window", out var context)
                && context.ValueKind == JsonValueKind.Object)
            {
                if (TryJsonLong(context, "total_input_tokens", out var usedValue)) used = usedValue;
                if (TryJsonLong(context, "context_window_size", out var windowValue)) window = windowValue;
            }
            return new(model, effort, used, window, sessionId);
        }
        catch { return new(null, null, null, null, null); }
    }

    /// <summary>GUI에서 바꾼 model/effort를 같은 런타임 상태 파일에 반영해 다음 재개 때 옛 CLI 값이 우선하지 않게 한다.</summary>
    public static void SavePersistedConfiguration(
        string roomId, string? model, string? effort, string? sessionId,
        long? contextTokens = null, long? contextWindow = null)
    {
        try
        {
            var path = Path.Combine(Dir, SafeRoom(roomId) + ".txt");
            var current = ParseMetadataFile(path);
            var sameSession = string.IsNullOrWhiteSpace(current.SessionId)
                              || string.Equals(current.SessionId, sessionId, StringComparison.OrdinalIgnoreCase);
            contextTokens ??= sameSession ? current.ContextTokens : null;
            contextWindow ??= sameSession ? current.ContextWindow : null;
            var content = string.Join('\n',
                model ?? current.Model ?? "",
                effort ?? current.Effort ?? "",
                contextTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
                contextWindow?.ToString(CultureInfo.InvariantCulture) ?? "",
                sessionId ?? "");
            Directory.CreateDirectory(Dir);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); } catch { }
            }
        }
        catch { }
    }

    /// <summary>claude model.id("claude-opus-4-8…") → 런치 플래그/콤보 값(opus/sonnet/haiku/fable). 미상이면 null.</summary>
    public static string? ToModelValue(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var s = id.ToLowerInvariant();
        if (s.Contains("opus")) return "opus";
        if (s.Contains("sonnet")) return "sonnet";
        if (s.Contains("haiku")) return "haiku";
        if (s.Contains("fable") || s.Contains("mythos")) return "fable";
        return null;
    }

    private static string SafeRoom(string roomId)
        => System.Text.RegularExpressions.Regex.Replace(roomId, @"[^\w\-]", "");

    private static bool TryJsonString(JsonElement node, string name, out string value)
    {
        value = "";
        if (!node.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? "";
        return true;
    }

    private static bool TryJsonLong(JsonElement node, string name, out long value)
    {
        value = 0;
        return node.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt64(out value);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
