using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Claude Code 의 <c>~/.claude.json</c> 최상위 <c>mcpServers</c> 섹션.
/// user-level(전역) 만 다룬다. 프로젝트별 <c>projects[&lt;path&gt;].mcpServers</c> 와
/// <c>plugin:*</c> / <c>claude.ai *</c>(OAuth/플러그인 제공) 는 read-only 라서 여기선 안 건드림.</summary>
public sealed class ClaudeMcpBackend : IMcpBackend
{
    public string Id => "claude";
    public string DisplayName => "Claude Code";
    public string ConfigPathHint => ".claude.json (user-level)";

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "claude",
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
        var r = root.Value;

        if (!r.TryGetProperty("mcpServers", out var ms) || ms.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var prop in ms.EnumerateObject())
        {
            var srv = ParseServer(prop.Name, prop.Value);
            if (srv != null) result.Add(srv);
        }
        return result;
    }

    /// <inheritdoc />
    public void Save(IEnumerable<McpServer> servers)
    {
        var (root, _) = LoadRaw();
        using var final = new MemoryStream();
        using (var writer = new Utf8JsonWriter(final, new JsonWriterOptions { Indented = true, IndentSize = 2 }))
        {
            if (root.HasValue && root.Value.ValueKind == JsonValueKind.Object)
            {
                bool first = true;
                writer.WriteStartObject();
                foreach (var prop in root.Value.EnumerateObject())
                {
                    if (prop.NameEquals("mcpServers")) continue;
                    if (!first) writer.WriteRawValue(",\n", false);
                    first = false;
                    prop.WriteTo(writer);
                }
                if (!first) writer.WriteRawValue(",\n", false);
                WriteMcpObject(writer, servers);
                writer.WriteEndObject();
            }
            else
            {
                WriteMcpObject(writer, servers);
            }
        }
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, Encoding.UTF8.GetString(final.ToArray()));
        if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
        else File.Move(tmp, ConfigPath);
    }

    /// <inheritdoc />
    public async Task RefreshStatusAsync(IEnumerable<McpServer> servers)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "claude",
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
            var err = await errTask;
            ParseStatusInto(servers, raw, err);
        }
        catch { /* 실패 시 조용히 Unknown 유지 */ }
    }

    // ── JSON 직렬화 ──────────────────────────────────────────────
    private static void WriteMcpObject(Utf8JsonWriter w, IEnumerable<McpServer> servers)
    {
        w.WritePropertyName("mcpServers");
        w.WriteStartObject();
        var first = true;
        foreach (var s in servers)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            if (!first) w.WriteRawValue(",\n", false);
            first = false;
            w.WritePropertyName(s.Name);
            WriteServer(w, s);
        }
        w.WriteEndObject();
    }

    private static void WriteServer(Utf8JsonWriter w, McpServer s)
    {
        w.WriteStartObject();
        if (s.Type == McpServerType.Local)
        {
            // Claude: "type":"stdio" + "command":"..." + "args":[...] + "env":{...}
            w.WriteString("type", "stdio");
            if (s.Command.Count > 0)
            {
                w.WriteString("command", s.Command[0]);
                if (s.Command.Count > 1)
                {
                    w.WritePropertyName("args");
                    w.WriteStartArray();
                    for (int i = 1; i < s.Command.Count; i++) w.WriteStringValue(s.Command[i]);
                    w.WriteEndArray();
                }
            }
            if (s.Environment.Count > 0)
            {
                w.WritePropertyName("env");
                w.WriteStartObject();
                foreach (var (k, v) in s.Environment) w.WriteString(k, v);
                w.WriteEndObject();
            }
        }
        else
        {
            // Claude: "type":"http" + "url":"..." + "headers":{...}
            w.WriteString("type", "http");
            w.WriteString("url", s.Url);
            if (s.Headers.Count > 0)
            {
                w.WritePropertyName("headers");
                w.WriteStartObject();
                foreach (var (k, v) in s.Headers) w.WriteString(k, v);
                w.WriteEndObject();
            }
        }
        // enabled/timeout은 Claude 스키마에 없음 (CLI 가 항상 활성으로 간주). 생략.
        w.WriteEndObject();
    }

    private static McpServer? ParseServer(string name, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : "stdio";

        var srv = new McpServer { Name = name };
        // Claude 의 type: "stdio" / "http" / "sse"
        if (type is "http" or "sse") srv.Type = McpServerType.Remote;

        if (srv.Type == McpServerType.Local)
        {
            // command (string) + args (array) → 단일 Command 리스트로 합침
            if (el.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
                srv.Command.Add(cmd.GetString()!);
            if (el.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                foreach (var a in args.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.String) srv.Command.Add(a.GetString()!);
            if (el.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                foreach (var kv in env.EnumerateObject())
                    srv.Environment[kv.Name] = kv.Value.ValueKind == JsonValueKind.String
                        ? kv.Value.GetString() ?? "" : kv.Value.ToString();
        }
        else
        {
            if (el.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                srv.Url = url.GetString() ?? "";
            if (el.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                foreach (var kv in h.EnumerateObject())
                    srv.Headers[kv.Name] = kv.Value.ValueKind == JsonValueKind.String
                        ? kv.Value.GetString() ?? "" : kv.Value.ToString();
        }
        // Claude 는 enabled/disabled 별도 필드 없음. 글로벌 `disabledMcpServers` 목록으로 관리 — 우리는 건드리지 않음.
        srv.Enabled = true;
        return srv;
    }

    private static (JsonElement? root, byte[] raw) LoadRaw()
    {
        if (!File.Exists(ConfigPath)) return (null, Array.Empty<byte>());
        try
        {
            var bytes = File.ReadAllBytes(ConfigPath);
            using var doc = JsonDocument.Parse(bytes);
            return (doc.RootElement.Clone(), bytes);
        }
        catch { return (null, Array.Empty<byte>()); }
    }

    // ── 상태 파서 — `claude mcp list` 출력 ────────────────────────
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    /// <summary>실제 포맷 (1.17.x):
    /// <code>
    /// Checking MCP server health??
    /// claude.ai Figma: https://mcp.figma.com/mcp - ??Connected
    /// claude.ai Notion: https://mcp.notion.com/mcp - ??Connected
    /// plugin:cloudflare:cloudflare-api: https://mcp.cloudflare.com/mcp (HTTP) - ! Needs authentication
    /// chrome-devtools: npx -y chrome-devtools-mcp@latest - ??Connected
    /// </code>
    /// 이름 + 콜론 + (command|url) + 대시 + 상태 단어. plugin:* / claude.ai * 는 우리 관리 밖이므로 매칭돼도 무시.</summary>
    private static readonly Regex StatusLineRx = new(
        @"^\s*(?<name>[^:\s][^:]*?):\s+(?<detail>.+?)\s+-\s+(?<status>Connected|Failed|Needs authentication|Disabled)\s*$",
        RegexOptions.Compiled);

    private static void ParseStatusInto(IEnumerable<McpServer> servers, string rawOutput, string rawError)
    {
        var text = Ansi.Replace(rawOutput, "");
        var byName = servers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var m = StatusLineRx.Match(line);
            if (!m.Success) continue;
            var name = m.Groups["name"].Value.Trim();
            if (!byName.TryGetValue(name, out var srv)) continue;  // plugin:*/claude.ai * 는 무시
            srv.Status = m.Groups["status"].Value.ToLowerInvariant() switch
            {
                "connected" => McpLiveStatus.Connected,
                "disabled" => McpLiveStatus.Disabled,
                "needs authentication" => McpLiveStatus.NeedsAuth,
                "failed" => McpLiveStatus.Failed,
                _ => McpLiveStatus.Unknown,
            };
        }
    }
}
