using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DevezCode.Services.Dashboard;

public enum RelayConnectionState { Disconnected, Connecting, Connected }

/// <summary>
/// DevezCode PC 를 Cloudflare 릴레이(DeviceHub DO)에 아웃바운드 WSS 1개로 연결하는 어댑터.
/// 단일 소켓 위에서 clientId 봉투로 논리 브라우저 N명을 역다중화해 전송-무관 DashboardHub 로
/// 위임한다. 자동 재연결(지수 백오프) 포함. 헤더 X-Device-Token 으로 device secret 인증.
/// </summary>
public sealed class RelayConnector
{
    public static RelayConnector Instance { get; } = new();

    private const string DeviceTokenHeader = "X-Device-Secret";

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RelayClientSink> _clients = new();

    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private ClientWebSocket? _socket;
    private Channel<string>? _outgoing;

    public RelayConnectionState State { get; private set; } = RelayConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public bool IsEnabled { get; private set; }

    private RelayConnector() { }

    /// <summary>설정을 읽어 원격 활성 상태를 적용한다(토글/앱 시작 시 호출).</summary>
    public async Task ApplyFromConfigAsync()
    {
        var cfg = RemoteDashboardConfig.Current;
        if (cfg.Enabled && cfg.IsPaired) await StartAsync();
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

        if (cts != null) cts.Cancel();
        if (loop != null) { try { await loop; } catch { } }
        cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(30);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(cancellationToken);
                backoff = TimeSpan.FromSeconds(1); // 정상 종료 후엔 백오프 리셋
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                DiagLog.Write("relay connector: " + ex.Message);
            }

            State = RelayConnectionState.Disconnected;
            if (cancellationToken.IsCancellationRequested) break;
            try { await Task.Delay(backoff, cancellationToken); } catch { break; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
        }
        CleanupClients();
    }

    private async Task ConnectAndPumpAsync(CancellationToken cancellationToken)
    {
        var cfg = RemoteDashboardConfig.Current;
        if (!cfg.IsPaired) throw new InvalidOperationException("릴레이 페어링 정보가 없습니다.");

        State = RelayConnectionState.Connecting;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(DeviceTokenHeader, cfg.DeviceSecret);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(cfg.DeviceWebSocketUri(), cancellationToken);

        _socket = socket;
        _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        State = RelayConnectionState.Connected;
        LastError = null;
        DashboardHub.Instance.Activate();

        var sendTask = SendLoopAsync(socket, _outgoing, cancellationToken);
        try
        {
            await ReceiveLoopAsync(socket, cancellationToken);
        }
        finally
        {
            _outgoing.Writer.TryComplete();
            try { await sendTask; } catch { }
            CleanupClients();
            DashboardHub.Instance.Deactivate();
            _socket = null;
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

    // 봉투(DO->PC): {clientId, kind:"join"|"leave"|"msg", payload?}
    private void HandleEnvelope(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (!root.TryGetProperty("clientId", out var idEl) || !Guid.TryParse(idEl.GetString(), out var clientId)) return;
        var kind = root.TryGetProperty("kind", out var ke) ? ke.GetString() : null;
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

    // 봉투(PC->DO): payload 를 clientId 로 감싸 단일 소켓에 씀.
    private void SendToClient(Guid clientId, object payload)
    {
        var outgoing = _outgoing;
        if (outgoing == null) return;
        var envelope = JsonSerializer.Serialize(new { clientId = clientId.ToString(), kind = "msg", payload });
        outgoing.Writer.TryWrite(envelope);
    }

    private void CleanupClients()
    {
        foreach (var id in _clients.Keys.ToArray())
            if (_clients.TryRemove(id, out _)) DashboardHub.Instance.RemoveClient(id);
    }

    /// <summary>릴레이 논리 브라우저 1명. Queue 는 봉투로 감싸 단일 아웃바운드 소켓에 쓴다.</summary>
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
