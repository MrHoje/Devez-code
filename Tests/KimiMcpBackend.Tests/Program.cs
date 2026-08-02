using System.Text.Json;
using DevezCode.Models;
using DevezCode.Services;

var tests = new (string Name, Action Run)[]
{
    ("기존 설정 보존 등록", RegisterPreservesExistingConfiguration),
    ("devez-browser 제거", RemoveOnlyBrowserServer),
    ("알려진 필드 수정 시 확장 필드 보존", UpdatePreservesExtendedFields),
    ("손상된 JSON 덮어쓰기 방지", InvalidJsonIsNotOverwritten),
    ("잘못된 mcpServers 덮어쓰기 방지", InvalidMcpServersIsNotOverwritten),
    ("KIMI_CODE_HOME 경로 우선", KimiCodeHomeTakesPrecedence),
};

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
        Environment.ExitCode = 1;
    }
}

static void RegisterPreservesExistingConfiguration()
{
    WithConfig(InitialJson(), (path, backend) =>
    {
        using var before = JsonDocument.Parse(File.ReadAllText(path));
        var originalExisting = before.RootElement.GetProperty("mcpServers").GetProperty("existing").Clone();
        var originalRemote = before.RootElement.GetProperty("mcpServers").GetProperty("remote").Clone();

        var servers = backend.Load();
        Assert(servers.Count == 2, "서버 두 개를 읽어야 합니다.");
        var existing = servers.Single(server => server.Name == "existing");
        Assert(!existing.Enabled && existing.TimeoutMs == 4321, "enabled/startupTimeoutMs를 읽어야 합니다.");
        Assert(existing.Command.SequenceEqual(new[] { "npx", "-y", "pkg" }), "command/args를 읽어야 합니다.");

        servers.Add(new McpServer
        {
            Name = "devez-browser",
            Command = new List<string> { "C:\\Program Files\\nodejs\\node.exe", "C:\\bridge\\devez-browser-mcp.js" },
        });
        backend.Save(servers);

        using var after = JsonDocument.Parse(File.ReadAllText(path));
        var root = after.RootElement;
        Assert(root.GetProperty("customSetting").GetProperty("keep").GetBoolean(), "최상위 설정을 보존해야 합니다.");
        Assert(JsonElement.DeepEquals(originalExisting, root.GetProperty("mcpServers").GetProperty("existing")),
            "기존 stdio 항목을 그대로 보존해야 합니다.");
        Assert(JsonElement.DeepEquals(originalRemote, root.GetProperty("mcpServers").GetProperty("remote")),
            "기존 SSE 항목을 그대로 보존해야 합니다.");
        var browser = root.GetProperty("mcpServers").GetProperty("devez-browser");
        Assert(browser.GetProperty("command").GetString()!.EndsWith("node.exe"), "브라우저 command를 저장해야 합니다.");
        Assert(browser.GetProperty("args")[0].GetString()!.EndsWith("devez-browser-mcp.js"),
            "브라우저 script 인자를 저장해야 합니다.");
    });
}

static void RemoveOnlyBrowserServer()
{
    WithConfig(InitialJson(includeBrowser: true), (path, backend) =>
    {
        var servers = backend.Load();
        servers.RemoveAll(server => server.Name.Equals("devez-browser", StringComparison.OrdinalIgnoreCase));
        backend.Save(servers);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var configured = document.RootElement.GetProperty("mcpServers");
        Assert(!configured.TryGetProperty("devez-browser", out _), "devez-browser만 제거해야 합니다.");
        Assert(configured.TryGetProperty("existing", out _), "다른 서버를 보존해야 합니다.");
        Assert(configured.TryGetProperty("remote", out _), "원격 서버를 보존해야 합니다.");
    });
}

static void UpdatePreservesExtendedFields()
{
    WithConfig(InitialJson(), (path, backend) =>
    {
        var servers = backend.Load();
        var existing = servers.Single(server => server.Name == "existing");
        existing.Enabled = true;
        existing.TimeoutMs = 7654;
        backend.Save(servers);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var saved = document.RootElement.GetProperty("mcpServers").GetProperty("existing");
        Assert(!saved.TryGetProperty("enabled", out _), "enabled=true는 기본값이므로 생략해야 합니다.");
        Assert(saved.GetProperty("startupTimeoutMs").GetInt32() == 7654, "변경된 timeout을 저장해야 합니다.");
        Assert(saved.GetProperty("cwd").GetString() == "C:\\work", "cwd를 보존해야 합니다.");
        Assert(saved.GetProperty("toolTimeoutMs").GetInt32() == 9876, "toolTimeoutMs를 보존해야 합니다.");
        Assert(saved.GetProperty("enabledTools")[0].GetString() == "read", "enabledTools를 보존해야 합니다.");
        Assert(saved.GetProperty("custom").GetProperty("nested").GetInt32() == 1, "알 수 없는 필드를 보존해야 합니다.");
    });
}

static void InvalidJsonIsNotOverwritten()
{
    WithConfig("{ broken", (path, backend) =>
    {
        var original = File.ReadAllText(path);
        bool threw = false;
        try
        {
            backend.Save(new[] { new McpServer { Name = "devez-browser", Command = new() { "node", "script.js" } } });
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        Assert(threw, "손상된 JSON 저장을 거부해야 합니다.");
        Assert(File.ReadAllText(path) == original, "손상된 원본을 덮어쓰면 안 됩니다.");
    });
}

static void InvalidMcpServersIsNotOverwritten()
{
    WithConfig("{ \"mcpServers\": [] }", (path, backend) =>
    {
        var original = File.ReadAllText(path);
        bool threw = false;
        try
        {
            backend.Save(new[] { new McpServer { Name = "devez-browser", Command = new() { "node", "script.js" } } });
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        Assert(threw, "객체가 아닌 mcpServers 저장을 거부해야 합니다.");
        Assert(File.ReadAllText(path) == original, "잘못된 원본을 덮어쓰면 안 됩니다.");
    });
}

static void KimiCodeHomeTakesPrecedence()
{
    var previous = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
    var customHome = Path.Combine(Path.GetTempPath(), "kimi-home-test");
    try
    {
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", customHome);
        Assert(KimiMcpBackend.ConfigPath == Path.Combine(customHome, "mcp.json"),
            "KIMI_CODE_HOME 아래 mcp.json을 사용해야 합니다.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", previous);
    }
}

static void WithConfig(string json, Action<string, KimiMcpBackend> action)
{
    var directory = Path.Combine(Path.GetTempPath(), "DevezCode-KimiMcpBackend-Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "mcp.json");
    File.WriteAllText(path, json);
    try
    {
        action(path, new KimiMcpBackend(path));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static string InitialJson(bool includeBrowser = false) => $$"""
{
  "customSetting": { "keep": true },
  "mcpServers": {
    "existing": {
      "command": "npx",
      "args": ["-y", "pkg"],
      "env": { "TOKEN": "keep" },
      "cwd": "C:\\work",
      "enabled": false,
      "startupTimeoutMs": 4321,
      "toolTimeoutMs": 9876,
      "enabledTools": ["read"],
      "custom": { "nested": 1 }
    },
    "remote": {
      "transport": "sse",
      "url": "https://example.test/sse",
      "headers": { "X-Test": "keep" },
      "bearerTokenEnvVar": "MCP_TOKEN"
    }{{(includeBrowser ? ",\n    \"devez-browser\": { \"command\": \"node\", \"args\": [\"old.js\"] }" : "")}}
  }
}
""";

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
