using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Codex 의 <c>~/.codex/config.toml</c> 의 <c>[mcp_servers.*]</c> 섹션.
/// TOML 의 부분 파서(우리 용도에 한정):
/// <list type="bullet">
///   <item><c>[mcp_servers.NAME]</c> 섹션 헤더만 인식</item>
///   <item>key = "string" | = [array] | = { kv } | = number | = true/false</item>
///   <item>주석(#)·빈 줄 무시</item>
/// </list>
/// 그 외 TOML 기능(인라인 테이블·멀티라인 문자열·datetime 등) 은 만나면 string 으로 들어옴.</summary>
public sealed class CodexMcpBackend : IMcpBackend
{
    public string Id => "codex";
    public string DisplayName => "Codex";
    public string ConfigPathHint => ".codex/config.toml";

    public static string ConfigPath
    {
        get
        {
            // CODEX_HOME 우선 (--profile 으로도 바뀜). 기본은 %USERPROFILE%\.codex\config.toml.
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            var home = string.IsNullOrEmpty(codexHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : codexHome;
            return Path.Combine(home, "config.toml");
        }
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "codex",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return p != null && p.WaitForExit(3000) && p.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    /// <inheritdoc />
    public List<McpServer> Load()
    {
        var (root, _) = LoadRaw();
        var result = new List<McpServer>();
        if (root == null) return result;
        if (!root.TryGetTable("mcp_servers", out var ms))
            return result;

        foreach (var name in ms.Keys)
        {
            if (!ms.TryGetTable(name, out var sect)) continue;
            var srv = ParseServer(name, sect);
            if (srv != null) result.Add(srv);
        }
        return result;
    }

    /// <inheritdoc />
    public void Save(IEnumerable<McpServer> servers)
    {
        // 기존 텍스트를 보존하려면 부분 갱신이 필요하지만, codex config 는 다른 키(model, sandbox, ...)도 있어서
        // 안전을 위해 기존 파일을 그대로 두고 mcp_servers 섹션만 제거·대체한다.
        // (devez 이식 정책: 다른 필드는 보존)
        var existing = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : "";
        var lines = string.IsNullOrEmpty(existing) ? new List<string>() : existing.Split('\n').ToList();
        // Remove existing [mcp_servers.*] sections (and any nested [mcp_servers.*.sub] just in case).
        var pruned = StripMcpSections(lines);

        // 새 섹션 직렬화
        var sb = new StringBuilder();
        if (pruned.Count > 0 && pruned.Any(l => l.Trim().Length > 0))
        {
            foreach (var l in pruned) sb.AppendLine(l.TrimEnd('\r'));
            sb.AppendLine();
        }
        sb.AppendLine("[mcp_servers]");
        var first = true;
        foreach (var s in servers)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            if (!first) sb.AppendLine();
            first = false;
            sb.AppendLine($"[mcp_servers.\"{s.Name}\"]");
            WriteServer(sb, s);
        }

        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, sb.ToString());
    }

    /// <inheritdoc />
    public async Task RefreshStatusAsync(IEnumerable<McpServer> servers)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "codex",
                Arguments = "mcp list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return;
            var outputTask = p.StandardOutput.ReadToEndAsync();
            var errTask    = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return; }
            var raw = await outputTask;
            // codex mcp list 는 표 형식. Name 컬럼이 첫 컬럼. 매칭 이름 + 끝 Status/Auth 컬럼 파싱.
            ParseStatusInto(servers, raw);
        }
        catch { /* 실패 시 조용히 Unknown */ }
    }

    /// <inheritdoc />
    public IDisposable? WatchConfig(Action onChanged) => ConfigFileWatcher.Watch(ConfigPath, onChanged);

    // ── 파서/직렬화 ──────────────────────────────────────────────
    private static McpServer? ParseServer(string name, TomlTable sect)
    {
        var srv = new McpServer { Name = name };
        var type = sect.TryGetString("type", out var ts) ? ts.ToLowerInvariant() : "stdio";
        // Codex: type=stdio|http|https|sse (사실상 http/sse 모두 Remote 로 취급)
        if (type is "http" or "https" or "sse") srv.Type = McpServerType.Remote;

        if (srv.Type == McpServerType.Local)
        {
            if (sect.TryGetString("command", out var cmd)) srv.Command.Add(cmd);
            if (sect.TryGetArray("args", out var args))
                foreach (var a in args.OfType<string>()) srv.Command.Add(a);
            if (sect.TryGetTable("env", out var env))
                foreach (var (k, v) in env)
                    srv.Environment[k] = v is string s ? s : v?.ToString() ?? "";
        }
        else
        {
            if (sect.TryGetString("url", out var url)) srv.Url = url;
            if (sect.TryGetTable("headers", out var h))
                foreach (var (k, v) in h)
                    srv.Headers[k] = v is string s ? s : v?.ToString() ?? "";
            // bearer_token_env_var, http_headers, env_http_headers 등 codex 고유 필드는 일단 무시.
        }

        // codex 에는 enabled 가 없음 (모든 서버 활성). timeout 도 별도 필드 없음.
        srv.Enabled = true;
        return srv;
    }

    private static void WriteServer(StringBuilder sb, McpServer s)
    {
        sb.Append("type = ");
        if (s.Type == McpServerType.Local) sb.AppendLine("\"stdio\"");
        else sb.AppendLine("\"http\"");

        if (s.Type == McpServerType.Local)
        {
            if (s.Command.Count > 0)
            {
                sb.Append("command = ");
                sb.AppendLine(TomlQuote(s.Command[0]));
            }
            if (s.Command.Count > 1)
            {
                sb.Append("args = [");
                sb.AppendLine(string.Join(", ", s.Command.Skip(1).Select(TomlQuote)));
                sb.AppendLine("]");
            }
            if (s.Environment.Count > 0)
            {
                sb.AppendLine("env = {");
                var kv = string.Join(",\n", s.Environment.Select(p => $"  {p.Key} = {TomlQuote(p.Value)}"));
                sb.AppendLine(kv);
                sb.AppendLine("}");
            }
        }
        else
        {
            sb.Append("url = ");
            sb.AppendLine(TomlQuote(s.Url));
            if (s.Headers.Count > 0)
            {
                sb.AppendLine("headers = {");
                var kv = string.Join(",\n", s.Headers.Select(p => $"  {p.Key} = {TomlQuote(p.Value)}"));
                sb.AppendLine(kv);
                sb.AppendLine("}");
            }
        }
    }

    private static string TomlQuote(string? s)
    {
        // TOML basic string: 큰따옴표 + 백슬래시 이스케이프
        s ??= "";
        var escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        return $"\"{escaped}\"";
    }

    /// <summary>기존 텍스트에서 <c>[mcp_servers.*]</c> 섹션만 제거한다. 다른 섹션/키는 그대로 둔다.</summary>
    private static List<string> StripMcpSections(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        bool skip = false;
        var sectionRx = new Regex(@"^\s*\[(mcp_servers(?:\.[^\]]*)?)\]");
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (sectionRx.IsMatch(line))
            {
                skip = true;
                continue;
            }
            if (skip)
            {
                // 새 최상위 섹션 ([name]) 이 시작되면 중단
                if (Regex.IsMatch(line, @"^\s*\[[^\[\]]+\]"))
                {
                    skip = false;
                }
                else
                {
                    continue;
                }
            }
            result.Add(line);
        }
        return result;
    }

    private static (TomlTable? root, byte[] raw) LoadRaw()
    {
        if (!File.Exists(ConfigPath)) return (null, Array.Empty<byte>());
        try
        {
            var bytes = File.ReadAllBytes(ConfigPath);
            var parser = new TomlParser(Encoding.UTF8.GetString(bytes));
            return (parser.Parse(), bytes);
        }
        catch { return (null, Array.Empty<byte>()); }
    }

    // ── `codex mcp list` 표 파서 ─────────────────────────────────
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private static void ParseStatusInto(IEnumerable<McpServer> servers, string raw)
    {
        // codex mcp list 표 형태:
        //   Name             Command                                                Args  Env ... Cwd  Status   Auth
        //   node_repl        C:\...\node_repl.exe                                    -     ******     -    enabled  Unsupported
        // 헤더는 가변적이므로, "Status" 컬럼을 식별자로 끝에서 1~2 칸을 본다.
        var text = Ansi.Replace(raw, "");
        var byName = servers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var lines = text.Split('\n');
        // 헤더 라인 위치 추정: "Status" 가 포함된 줄
        int headerIdx = -1;
        for (int i = 0; i < Math.Min(lines.Length, 5); i++)
        {
            if (lines[i].Contains("Status", StringComparison.OrdinalIgnoreCase))
            {
                headerIdx = i;
                break;
            }
        }
        if (headerIdx < 0) return;
        for (int i = headerIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            // 첫 토큰이 서버 이름 (공백까지)
            var sp = line.IndexOfAny(new[] { ' ', '\t' });
            if (sp <= 0) continue;
            var name = line.Substring(0, sp).Trim();
            if (!byName.TryGetValue(name, out var srv)) continue;
            // 끝에서 2 토큰이 Status, Auth. 토큰 단위로 자르기.
            var tokens = Regex.Split(line.Trim(), @"\s+");
            if (tokens.Length < 2) continue;
            var status = tokens[^2];
            srv.Status = status.ToLowerInvariant() switch
            {
                "enabled" or "connected" => McpLiveStatus.Connected,
                "disabled" => McpLiveStatus.Disabled,
                "failed" => McpLiveStatus.Failed,
                "needs_auth" or "unsupported" => McpLiveStatus.NeedsAuth,
                _ => McpLiveStatus.Unknown,
            };
        }
    }
}
