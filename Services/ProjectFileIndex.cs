using System.Collections.Concurrent;
using System.IO;

namespace DevezCode.Services;

/// <summary>작업 폴더의 파일·폴더 목록을 캐시해 두고 @ 멘션 자동완성용 퍼지 검색을 제공한다.</summary>
public static class ProjectFileIndex
{
    public sealed record Entry(string Path, string Name, string Directory, bool IsDirectory, int Depth);

    public sealed record Result(string Path, string Name, string Dir, bool IsDirectory);

    private const int MaxEntries = 40000;
    private const int MaxDepth = 12;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(20);

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build",
        "out", "target", "__pycache__", ".next", ".nuxt", ".svelte-kit", ".turbo", ".parcel-cache",
        ".gradle", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".terraform",
        "venv", ".venv", "packages", "coverage", "TestResults", ".angular", ".cache",
    };

    private sealed class CacheSlot
    {
        public IReadOnlyList<Entry> Entries = Array.Empty<Entry>();
        public DateTime BuiltAtUtc = DateTime.MinValue;
        public Task? Refreshing;
        public readonly object Gate = new();
    }

    private static readonly ConcurrentDictionary<string, CacheSlot> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>쿼리에 맞는 항목을 점수순으로 돌려준다. 캐시가 비어 있으면 첫 스캔을 기다린다.</summary>
    public static async Task<IReadOnlyList<Result>> SearchAsync(string root, string query, int limit = 24)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Array.Empty<Result>();
        root = Path.GetFullPath(root);
        var entries = await GetEntriesAsync(root);
        return Rank(entries, query ?? "", limit);
    }

    private static async Task<IReadOnlyList<Entry>> GetEntriesAsync(string root)
    {
        var slot = Cache.GetOrAdd(root, _ => new CacheSlot());
        var stale = DateTime.UtcNow - slot.BuiltAtUtc > CacheLifetime;
        if (!stale) return slot.Entries;

        Task refresh;
        lock (slot.Gate)
        {
            slot.Refreshing ??= Task.Run(() =>
            {
                try
                {
                    var scanned = Scan(root);
                    slot.Entries = scanned;
                    slot.BuiltAtUtc = DateTime.UtcNow;
                }
                catch (Exception ex) { DiagLog.Write($"ProjectFileIndex scan failed: {ex.Message}"); }
                finally { lock (slot.Gate) slot.Refreshing = null; }
            });
            refresh = slot.Refreshing;
        }

        // 캐시가 한 번도 채워지지 않았을 때만 스캔을 기다린다. 이후에는 즉시 이전 결과를 쓰고 뒤에서 갱신한다.
        if (slot.Entries.Count == 0) await refresh;
        return slot.Entries;
    }

    private static List<Entry> Scan(string root)
    {
        var entries = new List<Entry>(2048);
        var queue = new Queue<(string FullPath, string Relative, int Depth)>();
        queue.Enqueue((root, "", 0));
        while (queue.Count > 0 && entries.Count < MaxEntries)
        {
            var (current, relative, depth) = queue.Dequeue();
            string[] directories;
            string[] files;
            try
            {
                directories = Directory.GetDirectories(current);
                files = Directory.GetFiles(current);
            }
            catch { continue; }

            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                if (name.Length == 0 || IgnoredDirectories.Contains(name)) continue;
                try
                {
                    var attributes = File.GetAttributes(directory);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                }
                catch { continue; }
                var childRelative = relative.Length == 0 ? name : $"{relative}/{name}";
                entries.Add(new Entry(childRelative, name, relative, true, depth));
                if (depth + 1 <= MaxDepth) queue.Enqueue((directory, childRelative, depth + 1));
            }

            foreach (var file in files)
            {
                if (entries.Count >= MaxEntries) break;
                var name = Path.GetFileName(file);
                if (name.Length == 0) continue;
                entries.Add(new Entry(relative.Length == 0 ? name : $"{relative}/{name}", name, relative, false, depth));
            }
        }
        return entries;
    }

    private static List<Result> Rank(IReadOnlyList<Entry> entries, string query, int limit)
    {
        query = query.Replace('\\', '/').TrimStart('.', '/').Trim().ToLowerInvariant();
        var scored = new List<(Entry Entry, int Score)>(Math.Min(entries.Count, 512));
        foreach (var entry in entries)
        {
            var score = Score(entry, query);
            if (score > 0) scored.Add((entry, score));
        }
        return scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entry.Depth)
            .ThenBy(item => item.Entry.Path.Length)
            .ThenBy(item => item.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(item => new Result(item.Entry.Path, item.Entry.Name, item.Entry.Directory, item.Entry.IsDirectory))
            .ToList();
    }

    private static int Score(Entry entry, string query)
    {
        var path = entry.Path.ToLowerInvariant();
        var name = entry.Name.ToLowerInvariant();
        int score;
        if (query.Length == 0) score = 100;
        else if (name == query) score = 1400;
        else if (name.StartsWith(query, StringComparison.Ordinal)) score = 1000;
        else if (path.EndsWith("/" + query, StringComparison.Ordinal)) score = 950;
        else
        {
            var nameIndex = name.IndexOf(query, StringComparison.Ordinal);
            if (nameIndex > 0) score = 720 - Math.Min(nameIndex, 60);
            else
            {
                var pathIndex = path.IndexOf(query, StringComparison.Ordinal);
                if (pathIndex >= 0) score = 480 - Math.Min(pathIndex, 60);
                else if (IsSubsequence(path, query)) score = 220;
                else return 0;
            }
        }
        score -= entry.Depth * 6;
        score -= Math.Min(entry.Path.Length, 160) / 8;
        if (entry.IsDirectory) score += 6;
        return Math.Max(1, score);
    }

    private static bool IsSubsequence(string text, string query)
    {
        var cursor = 0;
        foreach (var character in text)
        {
            if (character == query[cursor] && ++cursor == query.Length) return true;
        }
        return false;
    }
}
