using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevezCode.Services;

/// <summary>세션 안의 MCP 브리지(node)가 내장 브라우저를 조작하기 위한 로컬 전용 IPC 서버.
/// <para>Named Pipe + 줄단위 JSON. 요청 <c>{id,token,room,cmd,args}</c> → 응답 <c>{id,ok,result|error}</c>.</para>
/// <para>파이프 이름·토큰은 앱 시작 시 프로세스 환경변수로 심어 자식(세션→CLI→브리지)이 상속받는다.
/// 상속이 끊긴 경우를 위해 <c>%AppData%\DevezCode\browser-bridge.json</c> 에도 남긴다.</para>
/// <para>토큰은 같은 사용자 계정의 프로세스만 읽을 수 있는 위치에 두므로 계정 경계까지가 신뢰 범위다.
/// 파이프도 현재 사용자만 접근 가능하게 만든다.</para></summary>
public sealed class BrowserBridgeServer
{
    public static BrowserBridgeServer Instance { get; } = new();

    public const string PipeEnvVar = "DEVEZCODE_BRIDGE_PIPE";
    public const string TokenEnvVar = "DEVEZCODE_BRIDGE_TOKEN";

    private const int MaxConcurrentClients = 8;

    private string _pipeName = "";
    private string _token = "";
    private CancellationTokenSource? _cts;
    private bool _started;

    public string PipeName => _pipeName;

    /// <summary>디스커버리 파일 경로(환경변수 상속이 끊긴 브리지의 폴백).</summary>
    public static string DiscoveryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "browser-bridge.json");

    /// <summary>앱 시작 시 1회 호출. 실패해도 앱 동작에 영향 없도록 조용히 넘어간다.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            _pipeName = "devezcode-browser-" + Environment.ProcessId;
            _token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            _cts = new CancellationTokenSource();

            // 자식 프로세스(세션 셸 → CLI → node 브리지)가 상속받는다.
            Environment.SetEnvironmentVariable(PipeEnvVar, _pipeName);
            Environment.SetEnvironmentVariable(TokenEnvVar, _token);
            WriteDiscovery();

            for (int i = 0; i < MaxConcurrentClients; i++)
                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

            DiagLog.Write($"BrowserBridgeServer start pipe={_pipeName}");
        }
        catch (Exception ex)
        {
            DiagLog.Write("BrowserBridgeServer start failed: " + ex.Message);
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (File.Exists(DiscoveryPath)) File.Delete(DiscoveryPath); } catch { }
    }

    private void WriteDiscovery()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiscoveryPath)!);
            var json = new JsonObject
            {
                ["pipe"] = _pipeName,
                ["token"] = _token,
                ["pid"] = Environment.ProcessId,
            }.ToJsonString();
            File.WriteAllText(DiscoveryPath, json, new UTF8Encoding(false));
        }
        catch { /* 폴백 경로일 뿐이라 실패해도 무시 */ }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, MaxConcurrentClients,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                await ServeAsync(pipe, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                DiagLog.Write("BrowserBridge accept error: " + ex.Message);
                try { await Task.Delay(500, ct); } catch { return; }
            }
        }
    }

    /// <summary>연결 1개를 오래 유지하며 줄단위 요청을 순차 처리한다(브리지 프로세스 1개 = 연결 1개).</summary>
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) return;
            if (line.Length == 0) continue;

            JsonNode? id = null;
            try
            {
                var req = JsonNode.Parse(line)?.AsObject()
                          ?? throw new ArgumentException("잘못된 요청 형식");
                id = req["id"]?.DeepClone();

                if (req["token"]?.GetValue<string>() != _token)
                    throw new UnauthorizedAccessException("브리지 토큰이 일치하지 않습니다.");

                var room = req["room"]?.GetValue<string>() ?? "";
                var cmd = req["cmd"]?.GetValue<string>() ?? "";
                var args = req["args"]?.AsObject() ?? new JsonObject();

                var result = await BrowserAutomationService.ExecuteAsync(room, cmd, args);
                await writer.WriteLineAsync(Reply(id, true, result).ToJsonString());
            }
            catch (Exception ex)
            {
                try { await writer.WriteLineAsync(Reply(id, false, ex.Message).ToJsonString()); }
                catch { return; }
            }
        }
    }

    private static JsonObject Reply(JsonNode? id, bool ok, string payload)
    {
        var o = new JsonObject { ["id"] = id, ["ok"] = ok };
        if (ok) o["result"] = payload; else o["error"] = payload;
        return o;
    }
}
