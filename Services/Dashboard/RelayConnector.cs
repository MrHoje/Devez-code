using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DevezCode.Services.Dashboard;

public enum RelayConnectionState { Disconnected, Connecting, Connected }

/// <summary>PC에서 Cloudflare DeviceHub로 나가는 단일 WSS 연결과 브라우저 다중화를 담당한다.</summary>
public sealed class RelayConnector
{
    public static RelayConnector Instance { get; } = new();
    private const string DeviceSecretHeader = "X-Device-Secret";

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RelayClientSink> _clients = new();
    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private Channel<string>? _outgoing;

    public RelayConnectionState State { get; private set; } = RelayConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public bool IsEnabled { get; private set; }

    private RelayConnector() { }

    public async Task ApplyFromConfigAsync()
    {
        var config = RemoteDashboardConfig.Current;
        if (config.Enabled && config.IsPaired) await StartAsync();
        else await StopAsync();
    }

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_runLoop != null) return;
            IsEnabled = true;
            LastError = null;
            _cts = new CancellationTokenSource();
            _runLoop = RunAsync(_cts.Token);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        await _lifecycle.WaitAsync();
        try
        {
            IsEnabled = false;
            loop = _runLoop;
            cts = _cts;
            _runLoop = null;
            _cts = null;
        }
        finally { _lifecycle.Release(); }

        cts?.Cancel();
        if (loop != null) { try { await loop; } catch { } }
        cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(cancellationToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                DiagLog.Write("relay connector: " + ex);
            }

            State = RelayConnectionState.Disconnected;
            if (cancellationToken.IsCancellationRequested) break;
            try { await Task.Delay(backoff, cancellationToken); } catch { break; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
        }
        CleanupClients();
    }

    private async Task ConnectAndPumpAsync(CancellationToken cancellationToken)
    {
        var config = RemoteDashboardConfig.Current;
        if (!config.IsPaired) throw new InvalidOperationException("릴레이 페어링 정보가 없습니다.");

        State = RelayConnectionState.Connecting;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(DeviceSecretHeader, config.DeviceSecret);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(config.DeviceWebSocketUri(), cancellationToken);

        _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        State = RelayConnectionState.Connected;
        LastError = null;
        DashboardHub.Instance.Activate();
        var sendTask = SendLoopAsync(socket, _outgoing, cancellationToken);
        try { await ReceiveLoopAsync(socket, cancellationToken); }
        finally
        {
            _outgoing.Writer.TryComplete();
            try { await sendTask; } catch { }
            CleanupClients();
            DashboardHub.Instance.Deactivate();
            _outgoing = null;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                if (message.Length > 512 * 1024) break;
                continue;
            }
            try { HandleEnvelope(message.ToArray()); }
            catch (Exception ex) { DiagLog.Write("relay envelope: " + ex.Message); }
            message.SetLength(0);
        }
    }

    private void HandleEnvelope(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (!root.TryGetProperty("clientId", out var id) || !Guid.TryParse(id.GetString(), out var clientId)) return;
        var kind = root.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
        switch (kind)
        {
            case "join":
            {
                var sink = new RelayClientSink(clientId, this);
                if (_clients.TryAdd(clientId, sink)) DashboardHub.Instance.AddClient(sink);
                break;
            }
            case "leave":
                if (_clients.TryRemove(clientId, out _)) DashboardHub.Instance.RemoveClient(clientId);
                break;
            case "msg":
                if (_clients.TryGetValue(clientId, out var target) && root.TryGetProperty("payload", out var payload))
                    DashboardHub.Instance.HandleClientMessage(target, payload);
                break;
        }
    }

    private static async Task SendLoopAsync(ClientWebSocket socket, Channel<string> outgoing, CancellationToken cancellationToken)
    {
        await foreach (var json in outgoing.Reader.ReadAllAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open) break;
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private void SendToClient(Guid clientId, object payload)
    {
        var outgoing = _outgoing;
        if (outgoing == null) return;
        outgoing.Writer.TryWrite(JsonSerializer.Serialize(new
        {
            clientId = clientId.ToString(), kind = "msg", payload,
        }));
    }

    private void CleanupClients()
    {
        foreach (var id in _clients.Keys.ToArray())
            if (_clients.TryRemove(id, out _)) DashboardHub.Instance.RemoveClient(id);
    }

    private sealed class RelayClientSink : IClientSink
    {
        public Guid Id { get; }
        public object Sync { get; } = new();
        public string? SelectedRoomId { get; set; }
        public long LastOutputSequence { get; set; }
        private readonly RelayConnector _owner;
        public RelayClientSink(Guid id, RelayConnector owner) { Id = id; _owner = owner; }
        public void Queue(object message) => _owner.SendToClient(Id, message);
    }
}
