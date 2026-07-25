using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DevezCode.Services;

public sealed record SessionCleanerCounts(int Claude, int OpenCode, int Gajae, int Codex, int Grok, int Antigravity, int Kimi)
{
    public int Total => Claude + OpenCode + Gajae + Codex + Grok + Antigravity + Kimi;
}

public sealed record SessionCleanerResult(SessionCleanerCounts Before, SessionCleanerCounts Deleted, SessionCleanerCounts Failed)
{
    public int DeletedTotal => Deleted.Total;
    public int FailedTotal => Failed.Total;
}
public enum CleanerAgentKind { Claude, OpenCode, Gajae, Codex, Grok, Antigravity, Kimi }
public sealed record CleanerScanInfo(int Count, long Bytes);

public sealed record SessionCleanerAgentCounts(int Claude, int OpenCode, int Gajae, int Codex, int Grok, int Antigravity, int Kimi, bool ShowClaude, bool ShowOpenCode, bool ShowGajae, bool ShowCodex, bool ShowGrok, bool ShowAntigravity, bool ShowKimi)
{
    public int Total => (ShowClaude ? Claude : 0) + (ShowOpenCode ? OpenCode : 0) + (ShowGajae ? Gajae : 0) + (ShowCodex ? Codex : 0) + (ShowGrok ? Grok : 0) + (ShowAntigravity ? Antigravity : 0) + (ShowKimi ? Kimi : 0);
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
            Codex: enabled.ShowCodex ? EnumerateUnmanagedCodex(managed).Count : 0,
            Grok: enabled.ShowGrok ? EnumerateUnmanagedGrok(managed).Count : 0,
            Antigravity: enabled.ShowAntigravity ? EnumerateUnmanagedAntigravity(managed).Count : 0,
            Kimi: enabled.ShowKimi ? EnumerateUnmanagedKimi(managed).Count : 0,
            enabled.ShowClaude,
            enabled.ShowOpenCode,
            enabled.ShowGajae,
            enabled.ShowCodex,
            enabled.ShowGrok,
            enabled.ShowAntigravity,
            enabled.ShowKimi);
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
            CleanerAgentKind.Codex => FromFiles(EnumerateUnmanagedCodex(managed)),
            CleanerAgentKind.Grok => FromDirectories(EnumerateUnmanagedGrok(managed)),
            CleanerAgentKind.Antigravity => FromFiles(EnumerateUnmanagedAntigravity(managed)),
            CleanerAgentKind.Kimi => FromDirectories(EnumerateUnmanagedKimi(managed)),
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

    private static CleanerScanInfo FromDirectories(IReadOnlyCollection<string> directories)
    {
        long bytes = 0;
        foreach (var directory in directories)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    try { bytes += new FileInfo(file).Length; } catch { }
            }
            catch { }
        }
        return new CleanerScanInfo(directories.Count, bytes);
    }



    public static SessionCleanerResult DeleteUnmanaged()
    {
        var before = GetCounts();
        var enabled = EnabledSnapshot.Load();

        (int deleted, int failed) cd = enabled.ShowClaude ? DeleteUnmanaged(CleanerAgentKind.Claude) : (0, 0);
        (int deleted, int failed) od = enabled.ShowOpenCode ? DeleteUnmanaged(CleanerAgentKind.OpenCode) : (0, 0);
        (int deleted, int failed) gd = enabled.ShowGajae ? DeleteUnmanaged(CleanerAgentKind.Gajae) : (0, 0);
        (int deleted, int failed) xd = enabled.ShowCodex ? DeleteUnmanaged(CleanerAgentKind.Codex) : (0, 0);
        (int deleted, int failed) rd = enabled.ShowGrok ? DeleteUnmanaged(CleanerAgentKind.Grok) : (0, 0);
        (int deleted, int failed) ad = enabled.ShowAntigravity ? DeleteUnmanaged(CleanerAgentKind.Antigravity) : (0, 0);
        (int deleted, int failed) kd = enabled.ShowKimi ? DeleteUnmanaged(CleanerAgentKind.Kimi) : (0, 0);

        return new SessionCleanerResult(
            new SessionCleanerCounts(before.Claude, before.OpenCode, before.Gajae, before.Codex, before.Grok, before.Antigravity, before.Kimi),
            new SessionCleanerCounts(cd.deleted, od.deleted, gd.deleted, xd.deleted, rd.deleted, ad.deleted, kd.deleted),
            new SessionCleanerCounts(cd.failed, od.failed, gd.failed, xd.failed, rd.failed, ad.failed, kd.failed));
    }

    public static (int deleted, int failed) DeleteUnmanaged(CleanerAgentKind kind)
    {
        var managed = ManagedSnapshot.Load();
        return kind switch
        {
            CleanerAgentKind.Claude => DeleteFiles(EnumerateUnmanagedClaude(managed)),
            CleanerAgentKind.OpenCode => DeleteOpenCode(EnumerateUnmanagedOpenCode(managed)),
            CleanerAgentKind.Gajae => DeleteFiles(EnumerateUnmanagedGajae(managed)),
            CleanerAgentKind.Codex => DeleteFiles(EnumerateUnmanagedCodex(managed)),
            CleanerAgentKind.Grok => DeleteGrok(EnumerateUnmanagedGrok(managed)),
            CleanerAgentKind.Antigravity => DeleteAntigravity(EnumerateUnmanagedAntigravity(managed)),
            CleanerAgentKind.Kimi => DeleteDirectories(EnumerateUnmanagedKimi(managed)),
            _ => (0, 0),
        };
    }


    private sealed record ManagedSnapshot(HashSet<string> ClaudeIds, HashSet<string> OpenCodeIds, HashSet<string> GajaeIds, HashSet<string> CodexIds, HashSet<string> GrokIds, HashSet<string> AntigravityIds, HashSet<string> KimiIds, HashSet<string> RoomIds)
    {
        public static ManagedSnapshot Load()
        {
            var (claude, opencode, gajae, codex, grok, antigravity, kimi, rooms) = SettingsService.LoadManagedSessionSnapshot();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var claudeIds = ToSet(claude);
            var openCodeIds = ToSet(opencode);
            var gajaeIds = ToSet(gajae);
            var codexIds = ToSet(codex);
            var grokIds = ToSet(grok);
            var antigravityIds = ToSet(antigravity);
            var kimiIds = ToSet(kimi);

            AddTrackedFileIds(claudeIds, Path.Combine(appData, "DevezCode", "claude", "sessions"), GuidRegex);
            AddTrackedFileIds(openCodeIds, Path.Combine(appData, "DevezCode", "opencode", "sessions"), new Regex(@"^ses_[A-Za-z0-9]+$", RegexOptions.Compiled));
            AddTrackedFileIds(gajaeIds, Path.Combine(appData, "DevezCode", "gajae", "sessions"), GuidRegex, fromGajaeRoomDir: true);
            AddTrackedFileIds(codexIds, Path.Combine(appData, "DevezCode", "codex", "sessions"), GuidRegex);
            // dvz 세션은 codex rollout 그 자체라 codex 보호 목록에 합친다 — 안 그러면 codex 정리에 쓸려간다.
            AddTrackedFileIds(codexIds, Path.Combine(appData, "DevezCode", "devezcli", "sessions"), GuidRegex);
            AddTrackedFileIds(grokIds, Path.Combine(appData, "DevezCode", "grok", "sessions"), GuidRegex);
            AddTrackedFileIds(antigravityIds, Path.Combine(appData, "DevezCode", "antigravity", "sessions"), GuidRegex);
            AddTrackedFileIds(kimiIds, Path.Combine(appData, "DevezCode", "kimi", "sessions"), KimiIdRegex);

            return new ManagedSnapshot(
                claudeIds,
                openCodeIds,
                gajaeIds,
                codexIds,
                grokIds,
                antigravityIds,
                kimiIds,
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
    private sealed record EnabledSnapshot(bool ShowClaude, bool ShowOpenCode, bool ShowGajae, bool ShowCodex, bool ShowGrok, bool ShowAntigravity, bool ShowKimi)
    {
        public static EnabledSnapshot Load()
        {
            var enabled = SettingsService.LoadEnabledAgents().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new EnabledSnapshot(
                enabled.Contains("claude"),
                enabled.Contains("opencode"),
                enabled.Contains("gajae"),
                enabled.Contains("codex"),
                enabled.Contains("grok"),
                enabled.Contains("antigravity"),
                enabled.Contains("kimi"));
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

    // codex 세션: ~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<uuid>.jsonl. 파일명 끝의 GUID 를 추출.
    private static readonly Regex CodexIdRegex =
        new(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.Compiled);

    private static string? ExtractCodexId(string path)
    {
        var m = CodexIdRegex.Match(Path.GetFileNameWithoutExtension(path));
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<string> EnumerateUnmanagedCodex(ManagedSnapshot managed)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (!Directory.Exists(root)) return new();
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                var id = ExtractCodexId(f);
                if (id == null) continue;
                if (managed.CodexIds.Contains(id)) continue;
                list.Add(f);
            }
        }
        catch { }
        return list;
    }

    // kimi 세션: ~/.kimi-code/sessions/<wdKey>/<session_uuid>/ (디렉터리 단위). 디렉터리명이 세션 id.
    private static readonly Regex KimiIdRegex =
        new(@"^session_[0-9A-Za-z_-]+$", RegexOptions.Compiled);

    private static string KimiSessionsRoot()
    {
        var configured = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        var home = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        return Path.Combine(home, "sessions");
    }

    private static List<string> EnumerateUnmanagedKimi(ManagedSnapshot managed)
    {
        var root = KimiSessionsRoot();
        if (!Directory.Exists(root)) return new();
        var list = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "session_*", SearchOption.AllDirectories))
            {
                var id = Path.GetFileName(dir);
                if (!KimiIdRegex.IsMatch(id) || managed.KimiIds.Contains(id)) continue;
                list.Add(dir);
            }
        }
        catch { }
        return list;
    }

    private static (int deleted, int failed) DeleteDirectories(IEnumerable<string> directories)
    {
        int deleted = 0, failed = 0;
        foreach (var d in directories)
        {
            try
            {
                if (Directory.Exists(d)) Directory.Delete(d, true);
                deleted++;
            }
            catch { failed++; }
        }
        return (deleted, failed);
    }

    private static List<string> EnumerateUnmanagedGrok(ManagedSnapshot managed)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "sessions");
        if (!Directory.Exists(root)) return new();
        var directories = new List<string>();
        try
        {
            foreach (var summary in Directory.EnumerateFiles(root, "summary.json", SearchOption.AllDirectories))
            {
                var directory = Path.GetDirectoryName(summary);
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var id = Path.GetFileName(directory);
                if (!GuidRegex.IsMatch(id) || managed.GrokIds.Contains(id)) continue;
                directories.Add(directory);
            }
        }
        catch { }
        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>agy 대화 db: ~/.gemini/antigravity-cli/conversations/&lt;guid&gt;.db.
    /// 관리 중이지 않은 GUID 파일명만 대상 (사이드카 -wal/-shm 은 삭제 시 함께 정리).</summary>
    private static List<string> EnumerateUnmanagedAntigravity(ManagedSnapshot managed)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "antigravity-cli", "conversations");
        if (!Directory.Exists(root)) return new();
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.db", SearchOption.TopDirectoryOnly))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (!GuidRegex.IsMatch(id)) continue;
                if (managed.AntigravityIds.Contains(id)) continue;
                list.Add(f);
            }
        }
        catch { }
        return list;
    }

    private static (int deleted, int failed) DeleteAntigravity(IEnumerable<string> files)
    {
        int deleted = 0, failed = 0;
        foreach (var f in files)
        {
            try
            {
                if (File.Exists(f)) File.Delete(f);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var side = f + suffix;
                    try { if (File.Exists(side)) File.Delete(side); } catch { }
                }
                deleted++;
            }
            catch { failed++; }
        }
        return (deleted, failed);
    }

    private static (int deleted, int failed) DeleteGrok(IEnumerable<string> directories)
    {
        var agent = AgentRegistry.Find("grok");
        var executable = agent == null ? null : AgentRegistry.ResolvePath(agent);
        if (string.IsNullOrWhiteSpace(executable)) return (0, directories.Count());

        int deleted = 0, failed = 0;
        foreach (var directory in directories)
        {
            var id = Path.GetFileName(directory);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("sessions");
                psi.ArgumentList.Add("delete");
                psi.ArgumentList.Add(id);
                using var process = Process.Start(psi);
                if (process == null || !process.WaitForExit(15000))
                {
                    try { process?.Kill(true); } catch { }
                    failed++;
                    continue;
                }
                if (process.ExitCode == 0 && !Directory.Exists(directory)) deleted++;
                else failed++;
            }
            catch { failed++; }
        }
        return (deleted, failed);
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

    private static IEnumerable<string> OpenCodeDbCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[]
        {
            Path.Combine(home, ".local", "share", "opencode", "opencode.db"),
            Path.Combine(appData, "opencode", "opencode.db"),
        }.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> EnumerateOpenCodeIdsFromDb()
    {
        var results = new List<string>();
        foreach (var db in OpenCodeDbCandidates())
        {
            if (!File.Exists(db)) continue;
            try
            {
                using var conn = new SqliteConnection($"Data Source={db};Pooling=False;Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id FROM session WHERE id IS NOT NULL AND id != ''";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (!string.IsNullOrEmpty(id))
                        results.Add(id);
                }
            }
            catch
            {
                // fallback: DB 잠김 등으로 읽을 수 없으면 skip
            }
        }
        return results;
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

    // CLI(session delete) 는 세션마다 opencode 프로세스를 새로 띄워 매우 느리다(startup 1~3초 × N).
    // 세션 데이터는 전부 SQLite(opencode.db)에 있고 message/part/todo/session_share 가 session 에
    // ON DELETE CASCADE FK 로 묶여 있으므로, 실행 중 opencode 를 종료해 DB 잠금을 풀고 한 번의
    // DELETE FROM session 으로 자식 행까지 일괄 삭제한다. 삭제 후 WAL 체크포인트 + VACUUM 으로 용량 회수.
    private static (int deleted, int failed) DeleteOpenCode(IEnumerable<string> ids)
    {
        var idList = ids.Distinct(StringComparer.Ordinal).ToList();
        if (idList.Count == 0) return (0, 0);

        // 실행 중 opencode 전부 종료(활성 세션 포함) — DB 잠금 해제 목적.
        foreach (var proc in Process.GetProcessesByName("opencode"))
        {
            try { proc.Kill(); proc.WaitForExit(5000); }
            catch { }
        }

        var remaining = new HashSet<string>(idList, StringComparer.Ordinal);
        foreach (var db in OpenCodeDbCandidates())
        {
            if (remaining.Count == 0) break;
            if (!File.Exists(db)) continue;
            try
            {
                using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
                conn.Open();

                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }

                // 파라미터 바인딩으로 한 번에 삭제 (CASCADE 가 자식 테이블 정리).
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var names = remaining.Select((_, i) => "@p" + i).ToList();
                cmd.CommandText = $"DELETE FROM session WHERE id IN ({string.Join(",", names)})";
                int idx = 0;
                foreach (var id in remaining) cmd.Parameters.AddWithValue("@p" + idx++, id);
                cmd.ExecuteNonQuery();
                tx.Commit();

                // 실제 삭제된 id 만 remaining 에서 제거(남은 db 후보 대상으로 재시도 불필요화).
                var beforeCount = remaining.Count;
                using (var check = conn.CreateCommand())
                {
                    check.CommandText = "SELECT id FROM session";
                    using var reader = check.ExecuteReader();
                    var stillThere = new HashSet<string>(StringComparer.Ordinal);
                    while (reader.Read()) stillThere.Add(reader.GetString(0));
                    remaining.RemoveWhere(id => !stillThere.Contains(id));
                }

                // 이 DB 에서 실제 삭제가 있었으면 WAL 체크포인트 + VACUUM 으로 용량 회수.
                if (remaining.Count < beforeCount)
                {
                    using var ck = conn.CreateCommand();
                    ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    ck.ExecuteNonQuery();
                    using var vac = conn.CreateCommand();
                    vac.CommandText = "VACUUM;";
                    vac.ExecuteNonQuery();
                }
            }
            catch
            {
                // 잠김/스키마 차이 등 → 다음 후보로
            }
        }

        int deleted = idList.Count - remaining.Count;
        return (deleted, remaining.Count);
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

    public static string VacuumOpenCodeDb()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dbPath = Path.Combine(home, ".local", "share", "opencode", "opencode.db");
        if (!File.Exists(dbPath)) return "DB 파일이 없습니다.";

        var before = OpenCodeStoreBytes();

        // 실행 중인 opencode 프로세스 종료
        var killed = false;
        foreach (var proc in Process.GetProcessesByName("opencode"))
        {
            try { proc.Kill(); proc.WaitForExit(5000); killed = true; }
            catch { }
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();

            // WAL 체크포인트 + VACUUM
            using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd1.ExecuteNonQuery();

            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "VACUUM;";
            cmd2.ExecuteNonQuery();

            var after = OpenCodeStoreBytes();
            var saved = before - after;

            var parts = new List<string>();
            if (killed) parts.Add("OpenCode를 종료했습니다.");
            parts.Add($"DB를 정리했습니다: {FormatBytes(saved)} 확보 (이전 {FormatBytes(before)} → 이후 {FormatBytes(after)})");
            return string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            return $"DB 정리 실패: {ex.Message}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
