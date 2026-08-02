using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Gajae Code 사용자 MCP 저장소(<c>~/.gjc/agent/mcp.json</c>) 관리.
/// gjc 0.11.11의 일반 세션은 이 저장소를 자동 로드하지 않으므로, 실행 시 동일 파일을
/// <c>--mcp-config</c>로 명시해 tools-only MCP로 사용한다.</summary>
public sealed class GajaeMcpBackend : IMcpBackend
{
    private const string SchemaUrl =
        "https://raw.githubusercontent.com/Yeachan-Heo/gajae-code/main/packages/coding-agent/src/config/mcp-schema.json";
    private static readonly Regex ValidServerName = new(
        @"^[a-zA-Z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);

    private readonly string _configPath;

    public GajaeMcpBackend() : this(ConfigPath) { }

    internal GajaeMcpBackend(string configPath)
    {
        _configPath = Path.GetFullPath(configPath);
    }

    public string Id => "gajae";
    public string DisplayName => "Gajae Code";
    public string ConfigPathHint => ".gjc/agent/mcp.json";

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gjc", "agent", "mcp.json");

    public bool IsAvailable
    {
        get
        {
            var agent = AgentRegistry.Find("gajae");
            return agent != null && AgentRegistry.IsInstalled(agent);
        }
    }

    public List<McpServer> Load()
    {
        var result = new List<McpServer>();
        if (!TryReadRoot(_configPath, out var root)) return result;

        var disabled = ReadDisabledServers(root);
        if (!root.TryGetProperty("mcpServers", out var servers)
            || servers.ValueKind != JsonValueKind.Object) return result;

        foreach (var property in servers.EnumerateObject())
        {
            var server = ParseServer(property.Name, property.Value, disabled);
            if (server != null) result.Add(server);
        }
        return result;
    }

    public void Save(IEnumerable<McpServer> servers)
    {
        var desired = servers
            .Where(server => !server.IsReadOnly && !string.IsNullOrWhiteSpace(server.Name))
            .ToList();
        Validate(desired);

        var root = LoadRootForSave(_configPath);
        var existing = ReadExistingServers(root);
        var disabled = ReadDisabledServers(root);

        // 현재 파일에 실제 정의된 서버의 enabled 상태는 각 서버 객체로 다시 기록한다.
        // 다른 범위의 서버를 막기 위한 dangling denylist 항목은 그대로 보존한다.
        foreach (var name in existing.Keys) disabled.Remove(name);

        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Path.GetFileName(_configPath) + ".devezcode-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("$schema", ReadSchema(root) ?? SchemaUrl);
                writer.WritePropertyName("mcpServers");
                writer.WriteStartObject();
                foreach (var server in desired)
                {
                    writer.WritePropertyName(server.Name);
                    existing.TryGetValue(server.Name, out var previous);
                    WriteServer(writer, server, previous);
                }
                writer.WriteEndObject();
                if (disabled.Count > 0)
                {
                    writer.WritePropertyName("disabledServers");
                    writer.WriteStartArray();
                    foreach (var name in disabled.OrderBy(name => name, StringComparer.Ordinal))
                        writer.WriteStringValue(name);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }

            if (File.Exists(_configPath)) File.Replace(temporary, _configPath, null);
            else File.Move(temporary, _configPath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public Task RefreshStatusAsync(IEnumerable<McpServer> servers)
    {
        // `gjc mcp list`는 저장 상태만 반환하며 실행 중 연결 상태를 검사하지 않는다.
        foreach (var server in servers)
            server.Status = server.Enabled ? McpLiveStatus.Unknown : McpLiveStatus.Disabled;
        return Task.CompletedTask;
    }

    public IDisposable? WatchConfig(Action onChanged) => ConfigFileWatcher.Watch(_configPath, onChanged);

    /// <summary>Gajae의 모든 신규/복원/재진입 명령에 공통으로 쓸 실행 인자를 만든다.</summary>
    public static string AppendLaunchArgument(string arguments) =>
        AppendLaunchArgument(arguments, ConfigPath);

    internal static string AppendLaunchArgument(string arguments, string configPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(configPath);
            if (fullPath.Contains('"') || !IsDirectRegularFile(fullPath)) return arguments;
            return $"{arguments} --mcp-config \"{fullPath}\"";
        }
        catch
        {
            return arguments;
        }
    }

    private static bool IsDirectRegularFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
        var current = new FileInfo(path);
        if ((current.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
        for (var directory = current.Directory; directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static McpServer? ParseServer(string name, JsonElement element, HashSet<string> disabled)
    {
        if (!ValidServerName.IsMatch(name) || element.ValueKind != JsonValueKind.Object) return null;
        var remote = element.TryGetProperty("url", out _)
            || (TryString(element, "type", out var transport) && transport is "http" or "sse");
        var server = new McpServer
        {
            Name = name,
            Type = remote ? McpServerType.Remote : McpServerType.Local,
            Enabled = !disabled.Contains(name)
                && (!element.TryGetProperty("enabled", out var enabled)
                    || enabled.ValueKind != JsonValueKind.False),
        };

        if (element.TryGetProperty("timeout", out var timeout)
            && timeout.TryGetInt32(out var timeoutMs) && timeoutMs > 0)
            server.TimeoutMs = timeoutMs;

        if (remote)
        {
            if (TryString(element, "url", out var url)) server.Url = url;
            ReadStringMap(element, "headers", server.Headers);
            if (element.TryGetProperty("oauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object)
            {
                server.OAuthMode = McpOAuthMode.Explicit;
                if (TryString(oauth, "clientId", out var clientId)) server.OAuthClientId = clientId;
                if (TryString(oauth, "clientSecret", out var clientSecret)) server.OAuthClientSecret = clientSecret;
            }
        }
        else
        {
            if (!TryString(element, "command", out var command)) return null;
            server.Command.Add(command);
            if (element.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                foreach (var argument in args.EnumerateArray())
                    if (argument.ValueKind == JsonValueKind.String) server.Command.Add(argument.GetString() ?? "");
            ReadStringMap(element, "env", server.Environment);
        }
        return server;
    }

    private static void WriteServer(Utf8JsonWriter writer, McpServer server, JsonElement previous)
    {
        writer.WriteStartObject();
        if (server.Type == McpServerType.Local)
        {
            writer.WriteString("command", server.Command[0]);
            if (server.Command.Count > 1)
            {
                writer.WritePropertyName("args");
                writer.WriteStartArray();
                foreach (var argument in server.Command.Skip(1)) writer.WriteStringValue(argument);
                writer.WriteEndArray();
            }
            WriteStringMap(writer, "env", server.Environment);
            CopyProperty(writer, previous, "noInheritEnv", JsonValueKind.True, JsonValueKind.False);
            CopyProperty(writer, previous, "cwd", JsonValueKind.String);
        }
        else
        {
            var transport = TryString(previous, "type", out var previousType) && previousType == "sse"
                ? "sse" : "http";
            writer.WriteString("type", transport);
            writer.WriteString("url", server.Url);
            WriteStringMap(writer, "headers", server.Headers);
        }

        writer.WriteBoolean("enabled", server.Enabled);
        if (server.TimeoutMs > 0) writer.WriteNumber("timeout", server.TimeoutMs);
        CopyProperty(writer, previous, "autoload", JsonValueKind.True, JsonValueKind.False);
        CopyProperty(writer, previous, "auth", JsonValueKind.Object);
        WriteOAuth(writer, server, previous);
        writer.WriteEndObject();
    }

    private static void WriteOAuth(Utf8JsonWriter writer, McpServer server, JsonElement previous)
    {
        if (server.Type == McpServerType.Remote)
        {
            if (server.OAuthMode != McpOAuthMode.Explicit) return;
            writer.WritePropertyName("oauth");
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(server.OAuthClientId))
                writer.WriteString("clientId", server.OAuthClientId);
            if (!string.IsNullOrWhiteSpace(server.OAuthClientSecret))
                writer.WriteString("clientSecret", server.OAuthClientSecret);
            if (previous.ValueKind == JsonValueKind.Object
                && previous.TryGetProperty("oauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object)
            {
                CopyProperty(writer, oauth, "redirectUri", JsonValueKind.String);
                CopyProperty(writer, oauth, "callbackPort", JsonValueKind.Number);
                CopyProperty(writer, oauth, "callbackPath", JsonValueKind.String);
            }
            writer.WriteEndObject();
            return;
        }
        CopyProperty(writer, previous, "oauth", JsonValueKind.Object);
    }

    private static void Validate(IReadOnlyCollection<McpServer> servers)
    {
        if (servers.GroupBy(server => server.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("Gajae MCP server names must be unique.");
        foreach (var server in servers)
        {
            if (!ValidServerName.IsMatch(server.Name))
                throw new InvalidDataException($"Invalid Gajae MCP server name: {server.Name}");
            if (server.Type == McpServerType.Local && server.Command.Count == 0)
                throw new InvalidDataException($"Gajae MCP stdio server requires a command: {server.Name}");
            if (server.Type == McpServerType.Remote && string.IsNullOrWhiteSpace(server.Url))
                throw new InvalidDataException($"Gajae MCP remote server requires a URL: {server.Name}");
        }
    }

    private static bool TryReadRoot(string path, out JsonElement root)
    {
        root = default;
        try
        {
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            root = document.RootElement.Clone();
            return true;
        }
        catch { return false; }
    }

    private static JsonElement LoadRootForSave(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Gajae MCP 설정의 최상위 값이 객체가 아닙니다: {path}");
            if (document.RootElement.TryGetProperty("mcpServers", out var servers)
                && servers.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Gajae MCP 설정의 mcpServers 값이 객체가 아닙니다: {path}");
            if (document.RootElement.TryGetProperty("disabledServers", out var disabled)
                && disabled.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Gajae MCP 설정의 disabledServers 값이 배열이 아닙니다: {path}");
            return document.RootElement.Clone();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException($"Gajae MCP 설정을 읽을 수 없습니다: {path}", exception);
        }
    }

    private static Dictionary<string, JsonElement> ReadExistingServers(JsonElement root)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("mcpServers", out var servers)
            || servers.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in servers.EnumerateObject()) result[property.Name] = property.Value.Clone();
        return result;
    }

    private static HashSet<string> ReadDisabledServers(JsonElement root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("disabledServers", out var disabled)
            || disabled.ValueKind != JsonValueKind.Array) return result;
        foreach (var value in disabled.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                result.Add(value.GetString()!);
        return result;
    }

    private static string? ReadSchema(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && TryString(root, "$schema", out var schema) ? schema : null;

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? "";
        return value.Length > 0;
    }

    private static void ReadStringMap(JsonElement element, string name, IDictionary<string, string> target)
    {
        if (!element.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object) return;
        foreach (var property in map.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String)
                target[property.Name] = property.Value.GetString() ?? "";
    }

    private static void WriteStringMap(Utf8JsonWriter writer, string name, IEnumerable<KeyValuePair<string, string>> values)
    {
        var entries = values.Where(pair => !string.IsNullOrWhiteSpace(pair.Key)).ToList();
        if (entries.Count == 0) return;
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach (var pair in entries) writer.WriteString(pair.Key, pair.Value);
        writer.WriteEndObject();
    }

    private static void CopyProperty(Utf8JsonWriter writer, JsonElement source, string name, params JsonValueKind[] kinds)
    {
        if (source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty(name, out var property)
            || !kinds.Contains(property.ValueKind)) return;
        writer.WritePropertyName(name);
        property.WriteTo(writer);
    }
}
