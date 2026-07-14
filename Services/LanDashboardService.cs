using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DevezCode.Services.Terminal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevezCode.Services;

/// <summary>
/// 같은 사설망의 브라우저를 현재 DevezCode ConPTY 세션에 연결한다.
/// 프로세스를 복제하지 않고 출력 이벤트를 multicast하고 입력은 기존 TerminalSession.Write로 되돌린다.
/// </summary>
public sealed class LanDashboardService
{
    public const int Port = 17865;
    public static LanDashboardService Instance { get; } = new();

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private IHost? _host;
    private CancellationTokenSource? _watchCts;
    private Guid? _controllerId;
    private readonly object _controllerLock = new();

    public bool IsRunning => _host != null;
    public string? LastError { get; private set; }

    private LanDashboardService() { }

    public static IReadOnlyList<string> GetAccessUrls()
    {
        var urls = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivateAddress(ua.Address)) continue;
                    var url = $"http://{ua.Address}:{Port}";
                    if (!urls.Contains(url, StringComparer.OrdinalIgnoreCase)) urls.Add(url);
                }
            }
        }
        catch { /* 네트워크 어댑터 조회 실패 시 localhost만 제공 */ }
        urls.Add($"http://localhost:{Port}");
        return urls;
    }

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_host != null) return;
            LastError = null;
            var dashboardRoot = Path.Combine(AppContext.BaseDirectory, "Resources", "Dashboard", "web");
            var terminalRoot = Path.Combine(AppContext.BaseDirectory, "Resources", "Terminal", "web");
            var agentIconRoot = Path.Combine(AppContext.BaseDirectory, "Resources", "Images", "ShellPresets");
            if (!Directory.Exists(dashboardRoot) || !Directory.Exists(terminalRoot) || !Directory.Exists(agentIconRoot))
                throw new DirectoryNotFoundException("LAN 대시보드 웹 자산을 찾을 수 없습니다.");

            var host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureWebHostDefaults(web =>
                {
                    web.UseKestrel(options => options.ListenAnyIP(Port));
                    web.Configure(app => ConfigureApp(app, dashboardRoot, terminalRoot, agentIconRoot));
                })
                .Build();
            await host.StartAsync();
            _host = host;
            TerminalDisplayOutputHub.OutputReceived += BroadcastOutput;
            TerminalDisplayOutputHub.SizeChanged += BroadcastSize;
            _watchCts = new CancellationTokenSource();
            _ = WatchSessionsAsync(_watchCts.Token);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DiagLog.Write("LAN dashboard start failed: " + ex);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            var host = _host;
            _host = null;
            _watchCts?.Cancel();
            _watchCts?.Dispose();
            _watchCts = null;

            foreach (var client in _clients.Values) client.Outgoing.Writer.TryComplete();
            _clients.Clear();
            lock (_controllerLock) _controllerId = null;
            TerminalDisplayOutputHub.OutputReceived -= BroadcastOutput;
            TerminalDisplayOutputHub.SizeChanged -= BroadcastSize;
            if (host != null)
            {
                try { await host.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
                host.Dispose();
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ApplyEnabledAsync(bool enabled)
    {
        if (enabled) await StartAsync();
        else await StopAsync();
    }

    private void ConfigureApp(IApplicationBuilder app, string dashboardRoot, string terminalRoot, string agentIconRoot)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Use(async (context, next) =>
        {
            if (!IsPrivateAddress(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("LAN access only");
                return;
            }

            await next();
        });

        app.Map("/ws", branch => branch.Run(HandleWebSocketAsync));
        app.Map("/terminal-assets", branch => branch.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(terminalRoot),
        }));
        app.Map("/agent-assets", branch => branch.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(agentIconRoot),
        }));
        var dashboardProvider = new PhysicalFileProvider(dashboardRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = dashboardProvider });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = dashboardProvider });
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var client = new Client(socket);
        _clients[client.Id] = client;
        client.Queue(new { type = "hello", clientId = client.Id, controllerId = CurrentControllerId() });
        client.Queue(BuildSessionsMessage());
        var sendTask = client.SendLoopAsync(context.RequestAborted);
        try
        {
            await ReceiveLoopAsync(client, context.RequestAborted);
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Outgoing.Writer.TryComplete();
            ReleaseControl(client.Id);
            try { await sendTask; } catch { }
        }
    }

    private async Task ReceiveLoopAsync(Client client, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested && client.Socket.State == WebSocketState.Open)
        {
            var result = await client.Socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                if (message.Length > 256 * 1024) break;
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(message.ToArray());
                HandleClientMessage(client, doc.RootElement);
            }
            catch { client.Queue(new { type = "error", message = "잘못된 요청입니다." }); }
            message.SetLength(0);
        }
    }

    private void HandleClientMessage(Client client, JsonElement root)
    {
        var type = root.TryGetProperty("type", out var te) ? te.GetString() : null;
        switch (type)
        {
            case "subscribe":
            {
                var roomId = root.TryGetProperty("roomId", out var re) ? re.GetString() : null;
                var session = string.IsNullOrWhiteSpace(roomId) ? null : TerminalSessionManager.Instance.Get(roomId!);
                if (session is not { IsAlive: true })
                {
                    var exists = WorkspaceStore.LoadDashboardSnapshot().Projects
                        .SelectMany(project => project.Sessions)
                        .Any(item => item.Id == roomId);
                    if (!exists)
                    {
                        client.Queue(new { type = "error", message = "세션을 찾을 수 없습니다." });
                        return;
                    }
                    lock (client.Sync)
                    {
                        client.SelectedRoomId = roomId;
                        client.LastOutputSequence = 0;
                    }
                    client.Queue(new { type = "starting", roomId });
                    _ = Task.Run(() => StartAndSubscribe(client, roomId!));
                    return;
                }
                lock (client.Sync)
                {
                    client.SelectedRoomId = roomId;
                    client.LastOutputSequence = 0;
                }
                Subscribe(client, roomId!, session);
                break;
            }
            case "claimControl":
                ClaimControl(client.Id);
                break;
            case "input":
            {
                if (!HasControl(client.Id))
                {
                    client.Queue(new { type = "error", message = "먼저 제어권을 가져오세요." });
                    return;
                }
                var roomId = root.TryGetProperty("roomId", out var re) ? re.GetString() : null;
                var data = root.TryGetProperty("data", out var de) ? de.GetString() ?? "" : "";
                if (roomId != client.SelectedRoomId || data.Length > 65536) return;
                TerminalSessionManager.Instance.Get(roomId!)?.TryWrite(data);
                break;
            }
            case "refresh":
                client.Queue(BuildSessionsMessage());
                break;
        }
    }

    private void StartAndSubscribe(Client client, string roomId)
    {
        try
        {
            var requested = false;
            try
            {
                requested = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    System.Windows.Application.Current.MainWindow is MainWindow main
                    && main.StartSessionForLanDashboard(roomId));
            }
            catch { /* UI 프리로드가 불가능하면 아래 직접 생성으로 폴백 */ }

            TerminalSession? session = null;
            if (requested)
            {
                for (var i = 0; i < 100 && session is not { IsAlive: true }; i++)
                {
                    Thread.Sleep(50);
                    session = TerminalSessionManager.Instance.Get(roomId);
                }
            }
            if (session is not { IsAlive: true })
                session = TerminalSessionManager.Instance.GetOrCreate(roomId, 120, 30);
            Subscribe(client, roomId, session);
            Broadcast(BuildSessionsMessage());
        }
        catch (Exception ex)
        {
            DiagLog.Write($"LAN dashboard start session {roomId}: {ex}");
            client.Queue(new { type = "error", message = "세션을 시작하지 못했습니다." });
        }
    }

    private static void Subscribe(Client client, string roomId, TerminalSession session)
    {
        // snapshot과 그 직후 live output의 순서를 client lock으로 원자화한다.
        lock (client.Sync)
        {
            if (client.SelectedRoomId != roomId) return;
            var replay = GetReplay(roomId, session);
            client.SelectedRoomId = roomId;
            client.LastOutputSequence = replay.Sequence;
            client.Queue(new
            {
                type = "snapshot",
                roomId,
                cols = session.Cols,
                rows = session.Rows,
                data = Convert.ToBase64String(replay.Data),
            });
        }
    }

    private async Task WatchSessionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Broadcast(BuildSessionsMessage());
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                DiagLog.Write("LAN dashboard session watch: " + ex.Message);
                try { await Task.Delay(1000, cancellationToken); } catch { break; }
            }
        }
    }

    private static TerminalDisplaySnapshot GetReplay(string roomId, TerminalSession session)
    {
        var display = TerminalDisplayOutputHub.GetReplaySnapshot(roomId);
        return display.Data.Length > 0 ? display : new TerminalDisplaySnapshot(session.GetRecentOutputSnapshot(), 0);
    }

    private object BuildSessionsMessage()
    {
        var config = TerminalSessionManager.Instance.Config;
        var live = TerminalSessionManager.Instance.GetSessionsSnapshot()
            .Where(pair => pair.Value.IsAlive)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var workspace = WorkspaceStore.LoadDashboardSnapshot();
        var folders = workspace.Folders.Select(folder => new
        {
            id = folder.Id,
            name = folder.Name,
            rootOrder = folder.RootOrder,
            isExpanded = folder.IsExpanded,
        }).ToArray();
        var projects = workspace.Projects.Select(project => new
        {
            path = project.Path,
            name = project.Name,
            folderId = project.FolderId,
                    rootOrder = project.RootOrder,
                    isExpanded = project.IsExpanded,
                    showHiddenSessions = project.ShowHiddenSessions,
                    sessions = project.Sessions.Select(item =>
            {
                var alive = live.TryGetValue(item.Id, out var terminal);
                return new
                {
                    roomId = item.Id,
                    name = item.Name,
                    projectName = project.Name,
                    projectPath = project.Path,
                    agent = string.IsNullOrWhiteSpace(item.Agent) ? SettingsService.LoadAgentForRoom(item.Id) : item.Agent,
                    hidden = item.Hidden,
                    parentId = item.ParentId,
                    childrenExpanded = item.ChildrenExpanded,
                    alive,
                    cols = terminal?.Cols ?? 120,
                    rows = terminal?.Rows ?? 30,
                };
            }).ToArray(),
        }).ToArray();
        var sessions = projects.SelectMany(project => project.sessions).ToArray();
        return new
        {
            type = "sessions",
            folders,
            projects,
            sessions,
            controllerId = CurrentControllerId(),
            appTheme = App.CurrentTheme,
            fontFamily = config.FontFamily,
            fontSize = config.FontSizePx,
            terminalTheme = ToXtermTheme(config.Scheme),
            uiTheme = UiTheme(App.CurrentTheme),
        };
    }

    private static object ToXtermTheme(WtColorScheme s) => new
    {
        background = s.Background, foreground = s.Foreground, cursor = s.CursorColor,
        cursorAccent = s.Background, selectionBackground = s.SelectionBackground,
        black = s.Black, red = s.Red, green = s.Green, yellow = s.Yellow,
        blue = s.Blue, magenta = s.Purple, cyan = s.Cyan, white = s.White,
        brightBlack = s.BrightBlack, brightRed = s.BrightRed, brightGreen = s.BrightGreen,
        brightYellow = s.BrightYellow, brightBlue = s.BrightBlue, brightMagenta = s.BrightPurple,
        brightCyan = s.BrightCyan, brightWhite = s.BrightWhite,
    };

    private static object UiTheme(string theme) => theme switch
    {
        "soft" => new { bg = "#F2EDE6", panel = "#FAF7F2", panelSoft = "#ECE7DE", line = "#D8D2C6", text = "#2A2620", muted = "#5A5448", primary = "#5C8C4A", primarySoft = "#DEECD6" },
        "minimal" => new { bg = "#F8FAFC", panel = "#FFFFFF", panelSoft = "#F1F5F9", line = "#E2E8F0", text = "#0F172A", muted = "#475569", primary = "#2563EB", primarySoft = "#DBEAFE" },
        _ => new { bg = "#1F1F1E", panel = "#272727", panelSoft = "#2F2F2F", line = "#404040", text = "#E8E8E8", muted = "#AAAAAA", primary = "#C2622A", primarySoft = "#434343" },
    };

    private void BroadcastOutput(string roomId, long sequence, byte[] bytes)
    {
        if (_clients.IsEmpty) return;
        var json = JsonSerializer.Serialize(new { type = "output", roomId, data = Convert.ToBase64String(bytes) });
        foreach (var client in _clients.Values)
        {
            lock (client.Sync)
            {
                if (client.SelectedRoomId != roomId || sequence <= client.LastOutputSequence) continue;
                client.LastOutputSequence = sequence;
                client.Outgoing.Writer.TryWrite(json);
            }
        }
    }

    private void BroadcastSize(string roomId, int cols, int rows)
    {
        if (_clients.IsEmpty) return;
        var json = JsonSerializer.Serialize(new { type = "size", roomId, cols, rows });
        foreach (var client in _clients.Values)
        {
            lock (client.Sync)
                if (client.SelectedRoomId == roomId) client.Outgoing.Writer.TryWrite(json);
        }
    }

    private void Broadcast(object message)
    {
        if (_clients.IsEmpty) return;
        var json = JsonSerializer.Serialize(message);
        foreach (var client in _clients.Values) client.Outgoing.Writer.TryWrite(json);
    }

    private void ClaimControl(Guid clientId)
    {
        lock (_controllerLock) _controllerId = clientId;
        Broadcast(new { type = "control", controllerId = clientId });
    }

    private void ReleaseControl(Guid clientId)
    {
        bool released;
        lock (_controllerLock)
        {
            released = _controllerId == clientId;
            if (released) _controllerId = null;
        }
        if (released) Broadcast(new { type = "control", controllerId = (Guid?)null });
    }

    private bool HasControl(Guid clientId) { lock (_controllerLock) return _controllerId == clientId; }
    private Guid? CurrentControllerId() { lock (_controllerLock) return _controllerId; }

    private static bool IsPrivateAddress(IPAddress? address)
    {
        if (address == null || IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    private sealed class Client
    {
        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; }
        public object Sync { get; } = new();
        public Channel<string> Outgoing { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        public string? SelectedRoomId { get; set; }
        public long LastOutputSequence { get; set; }

        public Client(WebSocket socket) => Socket = socket;
        public void Queue(object message) => Outgoing.Writer.TryWrite(JsonSerializer.Serialize(message));
        public async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            await foreach (var json in Outgoing.Reader.ReadAllAsync(cancellationToken))
            {
                if (Socket.State != WebSocketState.Open) break;
                await Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);
            }
        }
    }
}
