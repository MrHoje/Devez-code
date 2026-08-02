using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>Kimi Code의 사용자 전역 <c>$KIMI_CODE_HOME/mcp.json</c> 내
/// 최상위 <c>mcpServers</c> 섹션을 관리한다.</summary>
public sealed class KimiMcpBackend : IMcpBackend
{
    private static readonly HashSet<string> ManagedFields = new(StringComparer.Ordinal)
    {
        "transport", "command", "args", "env", "url", "headers", "enabled", "startupTimeoutMs",
    };

    private readonly string? _configPath;

    public KimiMcpBackend(string? configPath = null) => _configPath = configPath;

    public string Id => "kimi";
    public string DisplayName => "Kimi";
    public string ConfigPathHint => "$KIMI_CODE_HOME/mcp.json";

    public static string ConfigPath
    {
        get
        {
            var kimiHome = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
            if (string.IsNullOrWhiteSpace(kimiHome))
            {
                kimiHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");
            }
            return Path.Combine(kimiHome, "mcp.json");
        }
    }

    private string ActiveConfigPath => _configPath ?? ConfigPath;

    public bool IsAvailable
    {
        get
        {
            var agent = AgentRegistry.Find("kimi");
            return agent != null && AgentRegistry.IsInstalled(agent);
        }
    }

    public List<McpServer> Load()
    {
        var result = new List<McpServer>();
        if (!File.Exists(ActiveConfigPath)) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ActiveConfigPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("mcpServers", out var servers)
                || servers.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in servers.EnumerateObject())
            {
                var server = ParseServer(property.Name, property.Value);
                if (server != null) result.Add(server);
            }
        }
        catch
        {
            // 손상된 설정은 Save에서 덮어쓰지 않고 오류로 중단한다.
        }
        return result;
    }

    public void Save(IEnumerable<McpServer> servers)
    {
        var requested = servers
            .Where(server => !server.IsReadOnly && !string.IsNullOrWhiteSpace(server.Name))
            .ToList();
        var requestedByName = requested.ToDictionary(server => server.Name, StringComparer.Ordinal);

        JsonElement? root = LoadRootForSave();
        JsonElement existingServers = default;
        bool hasExistingServers = root.HasValue
            && root.Value.TryGetProperty("mcpServers", out existingServers);
        if (hasExistingServers && existingServers.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Kimi MCP 설정의 mcpServers 값이 객체가 아닙니다: {ActiveConfigPath}");

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, IndentSize = 2 }))
        {
            writer.WriteStartObject();
            if (root.HasValue)
            {
                foreach (var property in root.Value.EnumerateObject())
                {
                    if (!property.NameEquals("mcpServers")) property.WriteTo(writer);
                }
            }

            writer.WritePropertyName("mcpServers");
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.Ordinal);

            if (hasExistingServers)
            {
                foreach (var property in existingServers.EnumerateObject())
                {
                    if (requestedByName.TryGetValue(property.Name, out var requestedServer))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteServer(writer, requestedServer, property.Value);
                        written.Add(property.Name);
                    }
                    else if (ParseServer(property.Name, property.Value) == null)
                    {
                        // UI가 표현할 수 없는 기존 항목은 삭제 대상으로 오인하지 않고 그대로 둔다.
                        property.WriteTo(writer);
                    }
                }
            }

            foreach (var server in requested)
            {
                if (!written.Add(server.Name)) continue;
                writer.WritePropertyName(server.Name);
                WriteServer(writer, server, null);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var directory = Path.GetDirectoryName(ActiveConfigPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = ActiveConfigPath + ".devezcode.tmp";
        File.WriteAllText(temporaryPath, Encoding.UTF8.GetString(output.ToArray()), new UTF8Encoding(false));
        if (File.Exists(ActiveConfigPath)) File.Replace(temporaryPath, ActiveConfigPath, null);
        else File.Move(temporaryPath, ActiveConfigPath);
    }

    public Task RefreshStatusAsync(IEnumerable<McpServer> servers) => Task.CompletedTask;

    public IDisposable? WatchConfig(Action onChanged) => ConfigFileWatcher.Watch(ActiveConfigPath, onChanged);

    private JsonElement? LoadRootForSave()
    {
        if (!File.Exists(ActiveConfigPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ActiveConfigPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Kimi MCP 설정의 최상위 값이 객체가 아닙니다: {ActiveConfigPath}");
            return document.RootElement.Clone();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException($"Kimi MCP 설정을 읽을 수 없습니다: {ActiveConfigPath}", exception);
        }
    }

    private static void WriteServer(Utf8JsonWriter writer, McpServer server, JsonElement? existing)
    {
        var existingParsed = existing.HasValue ? ParseServer(server.Name, existing.Value) : null;
        if (existing.HasValue && existingParsed != null && Equivalent(server, existingParsed))
        {
            existing.Value.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        if (existing.HasValue && existing.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in existing.Value.EnumerateObject())
            {
                if (ManagedFields.Contains(property.Name)) continue;
                if (server.Type == McpServerType.Local
                    && property.Name is "auth" or "bearerTokenEnvVar") continue;
                if (server.Type == McpServerType.Remote
                    && property.Name is "cwd" or "executor") continue;
                property.WriteTo(writer);
            }
        }

        if (server.Type == McpServerType.Local)
        {
            if (existing.HasValue
                && existing.Value.TryGetProperty("transport", out var transport)
                && transport.ValueKind == JsonValueKind.String
                && transport.GetString() == "stdio")
            {
                writer.WriteString("transport", "stdio");
            }
            if (server.Command.Count > 0)
            {
                writer.WriteString("command", server.Command[0]);
                if (server.Command.Count > 1)
                {
                    writer.WritePropertyName("args");
                    writer.WriteStartArray();
                    foreach (var argument in server.Command.Skip(1)) writer.WriteStringValue(argument);
                    writer.WriteEndArray();
                }
            }
            if (server.Environment.Count > 0)
            {
                writer.WritePropertyName("env");
                writer.WriteStartObject();
                foreach (var (key, value) in server.Environment) writer.WriteString(key, value);
                writer.WriteEndObject();
            }
        }
        else
        {
            if (existing.HasValue
                && existing.Value.TryGetProperty("transport", out var transport)
                && transport.ValueKind == JsonValueKind.String
                && transport.GetString() is "http" or "sse")
            {
                writer.WriteString("transport", transport.GetString());
            }
            writer.WriteString("url", server.Url);
            if (server.Headers.Count > 0)
            {
                writer.WritePropertyName("headers");
                writer.WriteStartObject();
                foreach (var (key, value) in server.Headers) writer.WriteString(key, value);
                writer.WriteEndObject();
            }
        }

        if (!server.Enabled) writer.WriteBoolean("enabled", false);
        if (server.TimeoutMs > 0) writer.WriteNumber("startupTimeoutMs", server.TimeoutMs);
        writer.WriteEndObject();
    }

    private static McpServer? ParseServer(string name, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        bool hasCommand = element.TryGetProperty("command", out var command)
            && command.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(command.GetString());
        bool hasUrl = element.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(url.GetString());
        string? transport = element.TryGetProperty("transport", out var transportElement)
            && transportElement.ValueKind == JsonValueKind.String
                ? transportElement.GetString()
                : null;

        bool remote = transport is "http" or "sse" || (transport == null && hasUrl);
        bool local = transport == "stdio" || (transport == null && hasCommand);
        if (remote == local || (remote && !hasUrl) || (local && !hasCommand)) return null;

        var server = new McpServer { Name = name, Type = remote ? McpServerType.Remote : McpServerType.Local };
        if (local)
        {
            server.Command.Add(command.GetString()!);
            if (element.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                foreach (var argument in args.EnumerateArray())
                    if (argument.ValueKind == JsonValueKind.String) server.Command.Add(argument.GetString()!);
            }
            if (element.TryGetProperty("env", out var environment) && environment.ValueKind == JsonValueKind.Object)
            {
                foreach (var variable in environment.EnumerateObject())
                    if (variable.Value.ValueKind == JsonValueKind.String)
                        server.Environment[variable.Name] = variable.Value.GetString() ?? "";
            }
        }
        else
        {
            server.Url = url.GetString()!;
            if (element.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in headers.EnumerateObject())
                    if (header.Value.ValueKind == JsonValueKind.String)
                        server.Headers[header.Name] = header.Value.GetString() ?? "";
            }
        }

        if (element.TryGetProperty("enabled", out var enabled)
            && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            server.Enabled = enabled.GetBoolean();
        }
        if (element.TryGetProperty("startupTimeoutMs", out var timeout)
            && timeout.TryGetInt32(out var timeoutMs) && timeoutMs > 0)
        {
            server.TimeoutMs = timeoutMs;
        }
        return server;
    }

    private static bool Equivalent(McpServer left, McpServer right) =>
        left.Name == right.Name
        && left.Type == right.Type
        && left.Command.SequenceEqual(right.Command, StringComparer.Ordinal)
        && DictionariesEqual(left.Environment, right.Environment)
        && left.Url == right.Url
        && DictionariesEqual(left.Headers, right.Headers)
        && left.Enabled == right.Enabled
        && left.TimeoutMs == right.TimeoutMs;

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
