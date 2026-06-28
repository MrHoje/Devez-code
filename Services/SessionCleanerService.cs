using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DevezCode.Services;

public sealed record SessionCleanerCounts(int Claude, int OpenCode, int Gajae)
{
    public int Total => Claude + OpenCode + Gajae;
}

public sealed record SessionCleanerResult(SessionCleanerCounts Before, SessionCleanerCounts Deleted, SessionCleanerCounts Failed)
{
    public int DeletedTotal => Deleted.Total;
    public int FailedTotal => Failed.Total;
}
public enum CleanerAgentKind { Claude, OpenCode, Gajae }
public sealed record CleanerScanInfo(int Count, long Bytes);

public sealed record SessionCleanerAgentCounts(int Claude, int OpenCode, int Gajae, bool ShowClaude, bool ShowOpenCode, bool ShowGajae)
{
    public int Total => (ShowClaude ? Claude : 0) + (ShowOpenCode ? OpenCode : 0) + (ShowGajae ? Gajae : 0);
}


/// <summary>
/// DevezCode 가 관리 중인 세션 ID 를 보호 목록으로 잡고, 그 밖의 에이전트 세션만 정리한다.
/// 삭제 대상은 "DevezCode에서 관리중이지 않음"이 확실한 파일/CLI 세션만 포함하며, 관리 중인 ID는 절대 삭제하지 않는다.
/// </summary>
public static class SessionCleanerService
{
    private static readonly Regex GuidRegex = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);

    public static SessionCleanerAgentCounts GetCounts()
    {
        var managed = ManagedSnapshot.Load();
        var enabled = EnabledSnapshot.Load();
        return new SessionCleanerAgentCounts(
            Claude: enabled.ShowClaude ? EnumerateUnmanagedClaude(managed).Count : 0,
            OpenCode: enabled.ShowOpenCode ? EnumerateUnmanagedOpenCode(managed).Count : 0,
            Gajae: enabled.ShowGajae ? EnumerateUnmanagedGajae(managed).Count : 0,
            enabled.ShowClaude,
            enabled.ShowOpenCode,
            enabled.ShowGajae);
    }

    public static int GetCount(CleanerAgentKind kind) => GetScanInfo(kind).Count;

    public static CleanerScanInfo GetScanInfo(CleanerAgentKind kind)
    {
        var managed = ManagedSnapshot.Load();
        return kind switch
        {
            CleanerAgentKind.Claude => FromFiles(EnumerateUnmanagedClaude(managed)),
            CleanerAgentKind.OpenCode => new CleanerScanInfo(EnumerateUnmanagedOpenCode(managed).Count, OpenCodeStoreBytes()),
            CleanerAgentKind.Gajae => FromFiles(EnumerateUnmanagedGajae(managed)),
            _ => new CleanerScanInfo(0, 0),
        };
    }

    private static CleanerScanInfo FromFiles(IReadOnlyCollection<string> files)
    {
        long bytes = 0;
        foreach (var f in files)
        {
            try { if (File.Exists(f)) bytes += new FileInfo(f).Length; } catch { }
        }
        return new CleanerScanInfo(files.Count, bytes);
    }



    public static SessionCleanerResult DeleteUnmanaged()
    {
        var before = GetCounts();
        var enabled = EnabledSnapshot.Load();

        (int deleted, int failed) cd = enabled.ShowClaude ? DeleteUnmanaged(CleanerAgentKind.Claude) : (0, 0);
        (int deleted, int failed) od = enabled.ShowOpenCode ? DeleteUnmanaged(CleanerAgentKind.OpenCode) : (0, 0);
        (int deleted, int failed) gd = enabled.ShowGajae ? DeleteUnmanaged(CleanerAgentKind.Gajae) : (0, 0);

        return new SessionCleanerResult(
            new SessionCleanerCounts(before.Claude, before.OpenCode, before.Gajae),
            new SessionCleanerCounts(cd.deleted, od.deleted, gd.deleted),
            new SessionCleanerCounts(cd.failed, od.failed, gd.failed));
    }

    public static (int deleted, int failed) DeleteUnmanaged(CleanerAgentKind kind)
    {
        var managed = ManagedSnapshot.Load();
        return kind switch
        {
            CleanerAgentKind.Claude => DeleteFiles(EnumerateUnmanagedClaude(managed)),
            CleanerAgentKind.OpenCode => DeleteOpenCode(EnumerateUnmanagedOpenCode(managed)),
            CleanerAgentKind.Gajae => DeleteFiles(EnumerateUnmanagedGajae(managed)),
            _ => (0, 0),
        };
    }


    private sealed record ManagedSnapshot(HashSet<string> ClaudeIds, HashSet<string> OpenCodeIds, HashSet<string> GajaeIds, HashSet<string> RoomIds)
    {
        public static ManagedSnapshot Load()
        {
            var (claude, opencode, gajae, rooms) = SettingsService.LoadManagedSessionSnapshot();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var claudeIds = ToSet(claude);
            var openCodeIds = ToSet(opencode);
            var gajaeIds = ToSet(gajae);

            AddTrackedFileIds(claudeIds, Path.Combine(appData, "DevezCode", "claude", "sessions"), GuidRegex);
            AddTrackedFileIds(openCodeIds, Path.Combine(appData, "DevezCode", "opencode", "sessions"), new Regex(@"^ses_[A-Za-z0-9]+$", RegexOptions.Compiled));
            AddTrackedFileIds(gajaeIds, Path.Combine(appData, "DevezCode", "gajae", "sessions"), GuidRegex, fromGajaeRoomDir: true);

            return new ManagedSnapshot(
                claudeIds,
                openCodeIds,
                gajaeIds,
                ToSet(rooms));
        }

        private static HashSet<string> ToSet(IEnumerable<string> values)
            => values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static void AddTrackedFileIds(HashSet<string> target, string dir, Regex valid, bool fromGajaeRoomDir = false)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                if (fromGajaeRoomDir)
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
                    {
                        var id = ExtractGajaeId(f);
                        if (id != null && valid.IsMatch(id)) target.Add(id);
                    }
                    return;
                }

                foreach (var f in Directory.EnumerateFiles(dir, "*.txt", SearchOption.TopDirectoryOnly))
                {
                    string id;
                    try { id = File.ReadAllText(f).Trim(); }
                    catch { continue; }
                    if (valid.IsMatch(id)) target.Add(id);
                }
            }
            catch { }
        }
    }
    private sealed record EnabledSnapshot(bool ShowClaude, bool ShowOpenCode, bool ShowGajae)
    {
        public static EnabledSnapshot Load()
        {
            var enabled = SettingsService.LoadEnabledAgents().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new EnabledSnapshot(
                enabled.Contains("claude"),
                enabled.Contains("opencode"),
                enabled.Contains("gajae"));
        }
    }


    private static List<string> EnumerateUnmanagedClaude(ManagedSnapshot managed)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(root)) return new();

        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (!GuidRegex.IsMatch(id)) continue;
                if (managed.ClaudeIds.Contains(id)) continue;
                list.Add(f);
            }
        }
        catch { }
        return list;
    }

    private static List<string> EnumerateUnmanagedGajae(ManagedSnapshot managed)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".gjc", "sessions"),
            Path.Combine(home, ".gjc", "agent", "sessions"),
            Path.Combine(home, ".gajae", "sessions"),
        };

        var list = new List<string>();
        foreach (var root in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    var id = ExtractGajaeId(f);
                    if (id == null) continue;
                    if (managed.GajaeIds.Contains(id)) continue;
                    list.Add(f);
                }
            }
            catch { }
        }
        return list;
    }

    private static string? ExtractGajaeId(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var us = name.LastIndexOf('_');
        var id = us >= 0 && us + 1 < name.Length ? name[(us + 1)..] : name;
        return GuidRegex.IsMatch(id) ? id : null;
    }

    private static List<string> EnumerateUnmanagedOpenCode(ManagedSnapshot managed)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        var output = Run("opencode", "session list", 8000);
        if (!string.IsNullOrWhiteSpace(output))
        {
            foreach (var line in output.Split('\n'))
            {
                var t = line.TrimStart();
                if (!t.StartsWith("ses_", StringComparison.Ordinal)) continue;
                var end = t.IndexOfAny(new[] { ' ', '\t', '\r' });
                var id = end > 0 ? t[..end] : t.Trim();
                if (id.Length > 4) ids.Add(id);
            }
        }

        foreach (var id in EnumerateOpenCodeIdsFromDb())
            ids.Add(id);

        return ids
            .Where(id => !managed.OpenCodeIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> EnumerateOpenCodeIdsFromDb()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(home, ".local", "share", "opencode", "opencode.db"),
            Path.Combine(appData, "opencode", "opencode.db"),
        };

        var rx = new Regex(@"ses_[A-Za-z0-9]+", RegexOptions.Compiled);
        foreach (var db in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(db)) continue;
            List<string> found;
            try
            {
                var text = Encoding.UTF8.GetString(File.ReadAllBytes(db));
                found = rx.Matches(text).Select(m => m.Value).ToList();
            }
            catch { continue; }

            foreach (var id in found)
                yield return id;
        }
    }

    private static long OpenCodeStoreBytes()
    {
        long bytes = 0;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var p in new[]
        {
            Path.Combine(home, ".local", "share", "opencode", "opencode.db"),
            Path.Combine(home, ".local", "share", "opencode", "opencode.db-wal"),
            Path.Combine(home, ".local", "share", "opencode", "opencode.db-shm"),
        })
        {
            try { if (File.Exists(p)) bytes += new FileInfo(p).Length; } catch { }
        }
        return bytes;
    }

    private static (int deleted, int failed) DeleteFiles(IEnumerable<string> files)
    {
        int deleted = 0, failed = 0;
        foreach (var f in files)
        {
            try
            {
                if (File.Exists(f)) File.Delete(f);
                deleted++;
            }
            catch { failed++; }
        }
        return (deleted, failed);
    }

    private static (int deleted, int failed) DeleteOpenCode(IEnumerable<string> ids)
    {
        int deleted = 0, failed = 0;
        foreach (var id in ids)
        {
            if (TryDeleteOpenCode(id)) deleted++;
            else failed++;
        }
        return (deleted, failed);
    }

    private static bool TryDeleteOpenCode(string id)
    {
        // opencode 버전별 명령 차이를 흡수한다. 보호 목록 검사 후에만 호출된다.
        return RunExit("opencode", $"session delete {id}", 8000) == 0
            || RunExit("opencode", $"session rm {id}", 8000) == 0
            || RunExit("opencode", $"session remove {id}", 8000) == 0;
    }

    private static string ResolveCommand(string file)
    {
        if (file.Equals("opencode", StringComparison.OrdinalIgnoreCase)
            && AgentRegistry.Find("opencode") is { } agent
            && AgentRegistry.ResolvePath(agent) is { } path)
            return path;
        return file;
    }
    private static string? Run(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveCommand(file),
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var outputTask = p.StandardOutput.ReadToEndAsync();
            var errorTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
            var output = outputTask.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(output)) return output;
            return errorTask.GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    private static int RunExit(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveCommand(file),
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
