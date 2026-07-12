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

/// <summary>Grok Build의 사용자 설정 <c>~/.grok/config.toml</c> 내 <c>[mcp_servers.*]</c> 관리.</summary>
public sealed class GrokMcpBackend : IMcpBackend
{
    public string Id => "grok";
    public string DisplayName => "Grok";
    public string ConfigPathHint => ".grok/config.toml";
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "config.toml");

    private static string? ExecutablePath
    {
        get
        {
            var agent = AgentRegistry.Find("grok");
            return agent == null ? null : AgentRegistry.ResolvePath(agent);
        }
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath);

    public List<McpServer> Load()
    {
        var result = new List<McpServer>();
        if (!File.Exists(ConfigPath)) return result;
        try
        {
            var root = new TomlParser(File.ReadAllText(ConfigPath)).Parse();
            if (!root.TryGetTable("mcp_servers", out var servers)) return result;
            foreach (var name in servers.Keys)
            {
                if (!servers.TryGetTable(name, out var table)) continue;
                var server = new McpServer { Name = name };
                if (table.TryGetString("url", out var url))
                {
                    server.Type = McpServerType.Remote;
                    server.Url = url;
                    if (table.TryGetTable("headers", out var headers))
                        foreach (var (key, value) in headers)
                            server.Headers[key] = value?.ToString() ?? "";
                }
                else
                {
                    if (table.TryGetString("command", out var command)) server.Command.Add(command);
                    if (table.TryGetArray("args", out var args))
                        server.Command.AddRange(args.OfType<string>());
                    if (table.TryGetTable("env", out var env))
                        foreach (var (key, value) in env)
                            server.Environment[key] = value?.ToString() ?? "";
                }
                if (table.TryGetValue("enabled", out var enabled) && enabled is bool flag)
                    server.Enabled = flag;
                if (table.TryGetValue("startup_timeout_sec", out var timeout))
                    server.TimeoutMs = timeout switch
                    {
                        long value => checked((int)Math.Min(value * 1000, int.MaxValue)),
                        double value => checked((int)Math.Min(value * 1000, int.MaxValue)),
                        _ => 0,
                    };
                result.Add(server);
            }
        }
        catch { }
        return result;
    }

    public void Save(IEnumerable<McpServer> servers)
    {
        var existing = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : "";
        var kept = StripMcpSections(existing.Replace("\r\n", "\n").Split('\n'));
        var sb = new StringBuilder();
        foreach (var line in kept) sb.AppendLine(line);
        if (sb.Length > 0 && !string.IsNullOrWhiteSpace(sb.ToString())) sb.AppendLine();
        foreach (var server in servers.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            sb.AppendLine($"[mcp_servers.{TomlQuote(server.Name)}]");
            sb.AppendLine($"enabled = {server.Enabled.ToString().ToLowerInvariant()}");
            if (server.TimeoutMs > 0)
                sb.AppendLine($"startup_timeout_sec = {Math.Max(1, server.TimeoutMs / 1000)}");
            if (server.Type == McpServerType.Local)
            {
                sb.AppendLine($"command = {TomlQuote(server.Command[0])}");
                if (server.Command.Count > 1)
                    sb.AppendLine($"args = [{string.Join(", ", server.Command.Skip(1).Select(TomlQuote))}]");
                if (server.Environment.Count > 0)
                    sb.AppendLine($"env = {{ {string.Join(", ", server.Environment.Select(p => $"{TomlQuote(p.Key)} = {TomlQuote(p.Value)}"))} }}");
            }
            else
            {
                sb.AppendLine($"url = {TomlQuote(server.Url)}");
                if (server.Headers.Count > 0)
                    sb.AppendLine($"headers = {{ {string.Join(", ", server.Headers.Select(p => $"{TomlQuote(p.Key)} = {TomlQuote(p.Value)}"))} }}");
            }
            sb.AppendLine();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var temp = ConfigPath + ".devezcode.tmp";
        File.WriteAllText(temp, sb.ToString().TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
        if (File.Exists(ConfigPath)) File.Replace(temp, ConfigPath, null);
        else File.Move(temp, ConfigPath);
    }

    public async Task RefreshStatusAsync(IEnumerable<McpServer> servers)
    {
        var executable = ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("mcp");
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("--json");
            using var process = Process.Start(psi);
            if (process == null) return;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { try { process.Kill(true); } catch { } return; }
            using var document = JsonDocument.Parse(await outputTask);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;
            var byName = servers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!TryString(item, "name", out var name) || !byName.TryGetValue(name, out var server)) continue;
                TryString(item, "status", out var status);
                TryString(item, "error", out var error);
                server.StatusMessage = error;
                server.Status = status.ToLowerInvariant() switch
                {
                    "connected" or "ready" or "healthy" => McpLiveStatus.Connected,
                    "disabled" => McpLiveStatus.Disabled,
                    "needs_auth" or "needs authentication" => McpLiveStatus.NeedsAuth,
                    "failed" or "error" => McpLiveStatus.Failed,
                    _ when !string.IsNullOrWhiteSpace(error) => McpLiveStatus.Failed,
                    _ => server.Enabled ? McpLiveStatus.Unknown : McpLiveStatus.Disabled,
                };
            }
        }
        catch { }
    }

    public IDisposable? WatchConfig(Action onChanged) => ConfigFileWatcher.Watch(ConfigPath, onChanged);

    private static List<string> StripMcpSections(IEnumerable<string> lines)
    {
        var result = new List<string>();
        bool skipping = false;
        foreach (var line in lines)
        {
            var header = Regex.Match(line, @"^\s*\[([^\]]+)\]");
            if (header.Success)
            {
                var section = header.Groups[1].Value.Trim();
                if (section.Equals("mcp_servers", StringComparison.Ordinal)
                    || section.StartsWith("mcp_servers.", StringComparison.Ordinal))
                {
                    skipping = true;
                    continue;
                }
                skipping = false;
            }
            if (!skipping) result.Add(line.TrimEnd('\r'));
        }
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static string TomlQuote(string? value) =>
        "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static bool TryString(JsonElement item, string property, out string value)
    {
        value = "";
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? "";
        return value.Length > 0;
    }
}
