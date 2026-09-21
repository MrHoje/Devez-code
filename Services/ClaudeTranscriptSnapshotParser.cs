using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DevezCode.Services;

public sealed record ClaudeTranscriptTurn(string Role, string Text);

public sealed record ClaudeTranscriptSnapshot(
    IReadOnlyList<ClaudeTranscriptTurn> Turns,
    string? Model = null,
    string? Effort = null,
    string? PermissionMode = null,
    long? ContextTokens = null,
    long? ContextWindow = null)
{
    public static ClaudeTranscriptSnapshot Empty { get; } = new(Array.Empty<ClaudeTranscriptTurn>());
}

/// <summary>Claude Code jsonl에서 GUI 복원에 필요한 주 대화와 마지막 실행 메타데이터를 읽는다.</summary>
public static class ClaudeTranscriptSnapshotParser
{
    private const string RequestInterruptedMarkerPrefix = "[Request interrupted";
    private sealed record CacheEntry(long Length, long WriteTicks, ClaudeTranscriptSnapshot Snapshot);

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheEntries = 12;
    private const int MetadataProbeChars = 4096;
    private const int MaxRestorableLineChars = 8 * 1024 * 1024;

    /// <summary>포크 transcript의 세션 ID를 바꾸고, 대화가 아닌 현재 아티팩트 패널 UI 상태를 제외한다.</summary>
    public static string CloneForFork(string content, string oldSessionId, string newSessionId)
    {
        var forked = new StringBuilder(content.Length);
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (IsFrameLink(line)) continue;
            forked.AppendLine(line.Replace(oldSessionId, newSessionId));
        }
        return forked.ToString();
    }

    private static bool IsFrameLink(string line)
    {
        if (!line.Contains("\"frame-link\"", StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("type", out var type)
                   && type.GetString() == "frame-link";
        }
        catch (JsonException) { return false; }
    }

    public static ClaudeTranscriptSnapshot ParseFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ClaudeTranscriptSnapshot.Empty;
        try
        {
            var before = new FileInfo(path);
            if (!before.Exists) return ClaudeTranscriptSnapshot.Empty;
            lock (CacheLock)
            {
                if (Cache.TryGetValue(path, out var cached)
                    && cached.Length == before.Length
                    && cached.WriteTicks == before.LastWriteTimeUtc.Ticks)
                    return cached.Snapshot;
            }

            ClaudeTranscriptSnapshot snapshot;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                snapshot = ParseLines(ReadLines(reader));
            }

            var after = new FileInfo(path);
            if (after.Exists
                && after.Length == before.Length
                && after.LastWriteTimeUtc.Ticks == before.LastWriteTimeUtc.Ticks)
            {
                lock (CacheLock)
                {
                    if (Cache.Count >= MaxCacheEntries && !Cache.ContainsKey(path))
                        Cache.Remove(Cache.Keys.First());
                    Cache[path] = new CacheEntry(after.Length, after.LastWriteTimeUtc.Ticks, snapshot);
                }
            }
            return snapshot;
        }
        catch (IOException) { return ClaudeTranscriptSnapshot.Empty; }
        catch (UnauthorizedAccessException) { return ClaudeTranscriptSnapshot.Empty; }
    }

    public static ClaudeTranscriptSnapshot ParseLines(IEnumerable<string> lines)
    {
        var turns = new List<ClaudeTranscriptTurn>();
        // 한 턴(사용자 프롬프트 사이)에서 마지막 assistant 텍스트만 남긴다. 툴 호출 직전 narration 은
        // 툴 카드가 복원되지 않는 이 화면에서 최종 답변 위에 붙어 답변 일부처럼 읽히기 때문.
        var lastAssistantIndex = -1;
        string? model = null;
        string? effort = null;
        string? permissionMode = null;
        long? contextTokens = null;
        long? contextWindow = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (CanSkipLargeMetadataLine(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !TryString(root, "type", out var type)) continue;
                if (TryBoolean(root, "isSidechain", out var sidechain) && sidechain) continue;
                if ((TryBoolean(root, "isMeta", out var meta) && meta)
                    || (TryBoolean(root, "isCompactSummary", out var compactSummary) && compactSummary))
                    continue;

                UpdateContext(root, ref contextTokens, ref contextWindow);

                if (type == "permission-mode")
                {
                    if (TryString(root, "permissionMode", out var value)) permissionMode = value;
                    continue;
                }

                if (type is not ("user" or "assistant")) continue;
                var message = root.TryGetProperty("message", out var nested)
                              && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : root;

                if (type == "assistant")
                {
                    if (TryString(message, "model", out var currentModel) && IsUsefulModel(currentModel))
                        model = currentModel;
                    if (TryString(root, "effort", out var currentEffort)
                        || TryString(message, "effort", out currentEffort))
                        effort = currentEffort;
                    UpdateContext(message, ref contextTokens, ref contextWindow);
                    if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                        UpdateAssistantUsage(usage, ref contextTokens, ref contextWindow);
                }

                var text = ExtractContentText(message);
                if (type == "user" && IsInternalCommandEnvelope(text)) continue;
                text = StripRequestInterruptedMarker(text);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (type == "assistant" && lastAssistantIndex >= 0)
                {
                    turns[lastAssistantIndex] = new ClaudeTranscriptTurn(type, text);
                    continue;
                }
                turns.Add(new ClaudeTranscriptTurn(type, text));
                lastAssistantIndex = type == "assistant" ? turns.Count - 1 : -1;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException) { }
        }

        return new ClaudeTranscriptSnapshot(
            turns.ToArray(), model, effort, permissionMode, contextTokens, contextWindow);
    }

    private static string StripRequestInterruptedMarker(string text)
    {
        if (!text.Contains(RequestInterruptedMarkerPrefix, StringComparison.Ordinal)) return text;
        return string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(line => !line.Trim().StartsWith(RequestInterruptedMarkerPrefix, StringComparison.Ordinal)))
            .Trim();
    }

    private static bool IsInternalCommandEnvelope(string text)
    {
        var value = text.TrimStart();
        return value.StartsWith("<command-name>", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("<command-message>", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("<command-args>", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("<local-command-", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        bool skip = false;
        bool probed = false;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == '\n')
                {
                    if (!skip)
                    {
                        if (line.Length > 0 && line[^1] == '\r') line.Length--;
                        if (line.Length > 0) yield return line.ToString();
                    }
                    line.Clear();
                    skip = false;
                    probed = false;
                    continue;
                }
                if (skip) continue;
                line.Append(value);
                if (!probed && line.Length >= MetadataProbeChars)
                {
                    probed = true;
                    if (IsSkippableMetadataPrefix(line.ToString()))
                    {
                        skip = true;
                        line.Clear();
                    }
                }
                if (!skip && line.Length >= MaxRestorableLineChars)
                {
                    skip = true;
                    line.Clear();
                }
            }
        }
        if (!skip && line.Length > 0)
        {
            if (line[^1] == '\r') line.Length--;
            if (line.Length > 0) yield return line.ToString();
        }
    }

    private static void UpdateAssistantUsage(
        JsonElement usage, ref long? contextTokens, ref long? contextWindow)
    {
        long total = 0;
        bool found = false;
        foreach (var name in new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" })
        {
            if (!TryLong(usage, name, out var value)) continue;
            total += Math.Max(0, value);
            found = true;
        }
        if (found) contextTokens = total;
        if (TryLong(usage, "contextWindow", out var window)
            || TryLong(usage, "context_window_size", out window))
            if (window > 0) contextWindow = window;
    }

    private static void UpdateContext(
        JsonElement node, ref long? contextTokens, ref long? contextWindow)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        if (node.TryGetProperty("context_window", out var context)
            && context.ValueKind == JsonValueKind.Object)
        {
            if (TryLong(context, "total_input_tokens", out var used) && used >= 0) contextTokens = used;
            if (TryLong(context, "context_window_size", out var window) && window > 0) contextWindow = window;
        }
        if ((TryLong(node, "contextWindow", out var directWindow)
             || TryLong(node, "context_window_size", out directWindow))
            && directWindow > 0)
            contextWindow = directWindow;
    }

    private static string ExtractContentText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !TryString(block, "type", out var blockType)
                || blockType != "text"
                || !TryString(block, "text", out var value))
                continue;
            text.Append(value).Append('\n');
        }
        return text.ToString().Trim();
    }

    private static bool IsUsefulModel(string value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.StartsWith('<')
           && value.Length <= 160;

    private static bool CanSkipLargeMetadataLine(string line)
    {
        if (line.Length < 1024 * 1024) return false;
        var prefix = line.AsSpan(0, Math.Min(line.Length, 4096));
        return IsSkippableMetadataPrefix(prefix);
    }

    private static bool IsSkippableMetadataPrefix(ReadOnlySpan<char> prefix)
        => prefix.Contains("\"type\":\"last-prompt\"", StringComparison.Ordinal)
               || prefix.Contains("\"type\": \"last-prompt\"", StringComparison.Ordinal)
               || prefix.Contains("\"type\":\"attachment\"", StringComparison.Ordinal)
               || prefix.Contains("\"type\": \"attachment\"", StringComparison.Ordinal)
               || prefix.Contains("\"attachment\":", StringComparison.Ordinal)
               || prefix.Contains("\"type\":\"file-history-", StringComparison.Ordinal)
               || prefix.Contains("\"type\": \"file-history-", StringComparison.Ordinal);

    private static bool TryString(JsonElement node, string name, out string value)
    {
        value = "";
        if (!node.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? "";
        return true;
    }

    private static bool TryBoolean(JsonElement node, string name, out bool value)
    {
        value = false;
        if (!node.TryGetProperty(name, out var property)
            || (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False))
            return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryLong(JsonElement node, string name, out long value)
    {
        value = 0;
        if (!node.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number)
        {
            if (property.TryGetInt64(out value)) return true;
            if (property.TryGetDouble(out var number) && double.IsFinite(number))
            {
                value = (long)Math.Round(number);
                return true;
            }
        }
        if (property.ValueKind == JsonValueKind.String)
            return long.TryParse(property.GetString(), out value);
        return false;
    }
}
