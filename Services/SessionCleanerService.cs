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

/// <summary>
/// DevezCode 가 추적 중인 세션 ID 를 보호 목록으로 잡고, 그 밖의 에이전트 세션만 정리한다.
/// 삭제 대상은 "관리되지 않음"이 확실한 파일/CLI 세션만 포함하며, 보호 목록에 걸리면 절대 삭제하지 않는다.
/// </summary>
public static class SessionCleanerService
{
    private static readonly Regex GuidRegex = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);

    public static SessionCleanerCounts GetCounts()
    {
        var managed = ManagedSnapshot.Load();
        return new SessionCleanerCounts(
            Claude: EnumerateUnmanagedClaude(managed).Count,
            OpenCode: EnumerateUnmanagedOpenCode(managed).Count,
            Gajae: EnumerateUnmanagedGajae(managed).Count);
    }

    public static SessionCleanerResult DeleteUnmanaged()
    {
        var before = GetCounts();
        var managed = ManagedSnapshot.Load();

        var cd = DeleteFiles(EnumerateUnmanagedClaude(managed));
        var gd = DeleteFiles(EnumerateUnmanagedGajae(managed));
        var od = DeleteOpenCode(EnumerateUnmanagedOpenCode(managed));

        return new SessionCleanerResult(
            before,
            new SessionCleanerCounts(cd.deleted, od.deleted, gd.deleted),
            new SessionCleanerCounts(cd.failed, od.failed, gd.failed));
    }

    private sealed record ManagedSnapshot(HashSet<string> ClaudeIds, HashSet<string> OpenCodeIds, HashSet<string> GajaeIds, HashSet<string> RoomIds)
    {
        public static ManagedSnapshot Load()
        {
            var (claude, opencode, gajae, rooms) = SettingsService.LoadManagedSessionSnapshot();
            return new ManagedSnapshot(
                ToSet(claude),
                ToSet(opencode),
                ToSet(gajae),
                ToSet(rooms));
        }

        private static HashSet<string> ToSet(IEnumerable<string> values)
            => values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        var output = Run("opencode", "session list", 4000);
        if (string.IsNullOrWhiteSpace(output)) return new();

        var list = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var t = line.TrimStart();
            if (!t.StartsWith("ses_", StringComparison.Ordinal)) continue;
            var end = t.IndexOfAny(new[] { ' ', '\t', '\r' });
            var id = end > 0 ? t[..end] : t.Trim();
            if (id.Length <= 4) continue;
            if (managed.OpenCodeIds.Contains(id)) continue;
            list.Add(id);
        }
        return list.Distinct(StringComparer.Ordinal).ToList();
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

    private static string? Run(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
            return p.StandardOutput.ReadToEnd();
        }
        catch { return null; }
    }

    private static int RunExit(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
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
