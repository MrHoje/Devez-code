using System.IO;
using System.Text;
using System.Text.Json;

namespace DevezCode.Services;

public static class CodexTranscriptMetadata
{
    /// <summary>파일 끝부터 최신 Codex 모델·effort 상태를 찾는다. 활성 rollout도 공유 읽기한다.</summary>
    public static (string? Model, string? Effort) ReadLatest(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = fs.Length;
        if (length == 0) return (null, null);
        long window = Math.Min(length, 64 * 1024L);
        while (true)
        {
            var start = length - window;
            fs.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[checked((int)window)];
            int read = 0;
            while (read < bytes.Length)
            {
                var count = fs.Read(bytes, read, bytes.Length - read);
                if (count == 0) break;
                read += count;
            }
            var lines = Encoding.UTF8.GetString(bytes, 0, read).Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (start > 0 && i == 0) continue;
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0 || (!line.Contains("\"turn_context\"", StringComparison.Ordinal)
                    && !line.Contains("\"thread_settings_applied\"", StringComparison.Ordinal))) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("payload", out var payload)) continue;
                    if (root.TryGetProperty("type", out var type) && type.GetString() == "turn_context")
                    {
                        var model = payload.TryGetProperty("model", out var modelNode) ? modelNode.GetString() : null;
                        var effort = payload.TryGetProperty("effort", out var effortNode) ? effortNode.GetString() : null;
                        return (model, effort);
                    }
                    if (payload.TryGetProperty("type", out var eventType)
                        && eventType.GetString() == "thread_settings_applied"
                        && payload.TryGetProperty("thread_settings", out var settings))
                    {
                        var model = settings.TryGetProperty("model", out var modelNode) ? modelNode.GetString() : null;
                        var effort = settings.TryGetProperty("reasoning_effort", out var effortNode) ? effortNode.GetString() : null;
                        return (model, effort);
                    }
                }
                catch (JsonException) { }
            }
            if (start == 0) return (null, null);
            window = Math.Min(length, window * 4);
        }
    }
}
