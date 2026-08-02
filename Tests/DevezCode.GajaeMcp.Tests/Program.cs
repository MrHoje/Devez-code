using System.Text.Json;
using DevezCode.Models;
using DevezCode.Services;

var failures = new List<string>();
var testRoot = Path.Combine(Path.GetTempPath(), "DevezCode-GajaeMcp-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    var configPath = Path.Combine(testRoot, "nested path", "mcp.json");
    Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
    File.WriteAllText(configPath, """
        {
          "$schema": "https://example.test/gjc-mcp-schema.json",
          "mcpServers": {
            "legacy": {
              "command": "old-node",
              "args": ["old.js"],
              "enabled": true,
              "autoload": false,
              "noInheritEnv": true,
              "cwd": "C:/existing"
            },
            "browser-old": {
              "type": "sse",
              "url": "https://example.test/sse",
              "enabled": true
            }
          },
          "disabledServers": ["legacy", "project-only"]
        }
        """);

    var backend = new GajaeMcpBackend(configPath);
    var loaded = backend.Load();
    Check(loaded.Count == 2, "기존 서버 로드");
    Check(loaded.Single(server => server.Name == "legacy").Enabled == false, "disabledServers 반영");
    Check(loaded.Single(server => server.Name == "browser-old").Type == McpServerType.Remote, "SSE 서버 로드");

    var legacy = loaded.Single(server => server.Name == "legacy");
    legacy.Command = new List<string> { "node", "new browser.js" };
    legacy.Enabled = true;
    legacy.TimeoutMs = 4500;
    backend.Save(new[]
    {
        legacy,
        new McpServer
        {
            Name = "remote",
            Type = McpServerType.Remote,
            Url = "https://example.test/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer test" },
            Enabled = false,
            TimeoutMs = 9000,
        },
    });

    using (var document = JsonDocument.Parse(File.ReadAllText(configPath)))
    {
        var root = document.RootElement;
        Check(root.GetProperty("$schema").GetString() == "https://example.test/gjc-mcp-schema.json", "기존 schema 보존");
        var servers = root.GetProperty("mcpServers");
        Check(servers.EnumerateObject().Count() == 2 && !servers.TryGetProperty("browser-old", out _), "삭제 서버 제외");

        var savedLegacy = servers.GetProperty("legacy");
        Check(savedLegacy.GetProperty("command").GetString() == "node", "stdio command 저장");
        Check(savedLegacy.GetProperty("args")[0].GetString() == "new browser.js", "stdio args 저장");
        Check(savedLegacy.GetProperty("enabled").GetBoolean(), "enabled 저장");
        Check(savedLegacy.GetProperty("timeout").GetInt32() == 4500, "timeout 저장");
        Check(!savedLegacy.GetProperty("autoload").GetBoolean(), "autoload 보존");
        Check(savedLegacy.GetProperty("noInheritEnv").GetBoolean(), "noInheritEnv 보존");
        Check(savedLegacy.GetProperty("cwd").GetString() == "C:/existing", "cwd 보존");

        var remote = servers.GetProperty("remote");
        Check(remote.GetProperty("type").GetString() == "http", "원격 transport 저장");
        Check(!remote.GetProperty("enabled").GetBoolean(), "비활성 서버 저장");
        Check(remote.GetProperty("headers").GetProperty("Authorization").GetString() == "Bearer test", "원격 header 저장");

        var disabled = root.GetProperty("disabledServers").EnumerateArray().Select(value => value.GetString()).ToArray();
        Check(disabled.SequenceEqual(new[] { "project-only" }), "외부 denylist만 보존");
    }

    var roundTrip = backend.Load();
    Check(roundTrip.Count == 2, "저장 후 재로드");
    Check(!roundTrip.Single(server => server.Name == "remote").Enabled, "비활성 상태 왕복");

    const string baseArguments = "--session-dir \"C:\\sessions\\room\"";
    var launchArguments = GajaeMcpBackend.AppendLaunchArgument(baseArguments, configPath);
    Check(launchArguments == $"{baseArguments} --mcp-config \"{Path.GetFullPath(configPath)}\"", "절대 MCP 실행 인자");
    Check(
        GajaeMcpBackend.AppendLaunchArgument(baseArguments, Path.Combine(testRoot, "missing.json")) == baseArguments,
        "설정 파일 없을 때 기존 실행 보존");
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(configPath)!, "*.tmp").Any(), "임시 파일 정리");

    var corruptPath = Path.Combine(testRoot, "corrupt.json");
    File.WriteAllText(corruptPath, "{ broken");
    var corruptBackend = new GajaeMcpBackend(corruptPath);
    bool rejectedCorruptConfig = false;
    try
    {
        corruptBackend.Save(new[]
        {
            new McpServer { Name = "devez-browser", Command = new() { "node", "browser.js" } },
        });
    }
    catch (InvalidDataException)
    {
        rejectedCorruptConfig = true;
    }
    Check(rejectedCorruptConfig, "손상된 설정 저장 거부");
    Check(File.ReadAllText(corruptPath) == "{ broken", "손상된 설정 원본 보존");

    var invalidServersPath = Path.Combine(testRoot, "invalid-servers.json");
    File.WriteAllText(invalidServersPath, "{ \"mcpServers\": [] }");
    var invalidServersBackend = new GajaeMcpBackend(invalidServersPath);
    bool rejectedInvalidServers = false;
    try
    {
        invalidServersBackend.Save(new[]
        {
            new McpServer { Name = "devez-browser", Command = new() { "node", "browser.js" } },
        });
    }
    catch (InvalidDataException)
    {
        rejectedInvalidServers = true;
    }
    Check(rejectedInvalidServers, "잘못된 mcpServers 저장 거부");
    Check(File.ReadAllText(invalidServersPath) == "{ \"mcpServers\": [] }", "잘못된 mcpServers 원본 보존");
}
finally
{
    try { Directory.Delete(testRoot, recursive: true); } catch { }
}

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine($"FAIL: {failure}");
    return 1;
}

Console.WriteLine("PASS: Gajae MCP backend and launch arguments");
return 0;

void Check(bool condition, string name)
{
    if (!condition) failures.Add(name);
}
