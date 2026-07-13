using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>안티그래비티(agy)의 MCP 설정 <c>~/.gemini/config/mcp_config.json</c> 최상위
/// <c>mcpServers</c> 섹션 관리 (claude 와 동일한 command/args/env·url/headers 스키마).
/// 경로·스키마는 agy 1.1.1 바이너리 실측 기반 — 런타임 검증 항목.
/// 라이브 상태 조회 CLI(<c>agy mcp list</c>류)는 미확인이라 상태는 Unknown 유지.</summary>
public sealed class AntigravityMcpBackend : IMcpBackend
{
    public string Id => "antigravity";
    public string DisplayName => "Antigravity";
    public string ConfigPathHint => ".gemini/config/mcp_config.json";

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini", "config", "mcp_config.json");

    public bool IsAvailable
    {
        get
        {
            var agent = AgentRegistry.Find("antigravity");
            return agent != null && AgentRegistry.IsInstalled(agent);
        }
    }

    public List<McpServer> Load()
    {
        var result = new List<McpServer>();
        if (!File.Exists(ConfigPath)) return result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (!doc.RootElement.TryGetProperty("mcpServers", out var ms)
                || ms.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in ms.EnumerateObject())
            {
                var srv = ParseServer(prop.Name, prop.Value);
                if (srv != null) result.Add(srv);
            }
        }
        catch { }
        return result;
    }

    public void Save(IEnumerable<McpServer> servers)
    {
        JsonElement? root = null;
        try
        {
            if (File.Exists(ConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                root = doc.RootElement.Clone();
            }
        }
        catch { }

        using var final = new MemoryStream();
        using (var w = new Utf8JsonWriter(final, new JsonWriterOptions { Indented = true, IndentSize = 2 }))
        {
            w.WriteStartObject();
            if (root.HasValue && root.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.Value.EnumerateObject())
                {
                    if (prop.NameEquals("mcpServers")) continue;
                    prop.WriteTo(w); // 다른 설정 키 보존
                }
            }
            w.WritePropertyName("mcpServers");
            w.WriteStartObject();
            foreach (var s in servers.Where(s => !string.IsNullOrWhiteSpace(s.Name) && !s.IsReadOnly))
            {
                w.WritePropertyName(s.Name);
                WriteServer(w, s);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, Encoding.UTF8.GetString(final.ToArray()), new UTF8Encoding(false));
        if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
        else File.Move(tmp, ConfigPath);
    }

    public Task RefreshStatusAsync(IEnumerable<McpServer> servers) => Task.CompletedTask;

    public IDisposable? WatchConfig(Action onChanged) => ConfigFileWatcher.Watch(ConfigPath, onChanged);

    private static void WriteServer(Utf8JsonWriter w, McpServer s)
    {
        w.WriteStartObject();
        if (s.Type == McpServerType.Local)
        {
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
            w.WriteString("url", s.Url);
            if (s.Headers.Count > 0)
            {
                w.WritePropertyName("headers");
                w.WriteStartObject();
                foreach (var (k, v) in s.Headers) w.WriteString(k, v);
                w.WriteEndObject();
            }
        }
        if (!s.Enabled) w.WriteBoolean("disabled", true);
        w.WriteEndObject();
    }

    private static McpServer? ParseServer(string name, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var srv = new McpServer { Name = name };

        bool remote = el.TryGetProperty("url", out _)
            || (el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                && t.GetString() is "http" or "sse");
        if (remote)
        {
            srv.Type = McpServerType.Remote;
            if (el.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                srv.Url = url.GetString() ?? "";
            if (el.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                foreach (var kv in h.EnumerateObject())
                    srv.Headers[kv.Name] = kv.Value.ValueKind == JsonValueKind.String
                        ? kv.Value.GetString() ?? "" : kv.Value.ToString();
        }
        else
        {
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

        srv.Enabled = !(el.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True);
        return srv;
    }
}
