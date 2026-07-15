using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DevezCode.Services.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevezCode.Services;

/// <summary>
/// 같은 사설망의 브라우저를 현재 DevezCode ConPTY 세션에 연결하는 LAN 전송 어댑터.
/// 프로토콜 로직은 전송-무관 <see cref="DashboardHub"/> 가 담당하고, 이 클래스는 Kestrel
/// 인바운드 소켓을 받아 <see cref="LanClientSink"/> 로 감싸 허브에 위임한다.
/// </summary>
public sealed class LanDashboardService
{
    public const int Port = 17865;
    public static LanDashboardService Instance { get; } = new();

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private IHost? _host;

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
            DashboardHub.Instance.Activate();
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
            if (host != null)
            {
                DashboardHub.Instance.Deactivate();
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
        var client = new LanClientSink(socket);
        DashboardHub.Instance.AddClient(client);
        var sendTask = client.SendLoopAsync(context.RequestAborted);
        try
        {
            await ReceiveLoopAsync(client, context.RequestAborted);
        }
        finally
        {
            DashboardHub.Instance.RemoveClient(client.Id);
            client.Complete();
            try { await sendTask; } catch { }
        }
    }

    private static async Task ReceiveLoopAsync(LanClientSink client, CancellationToken cancellationToken)
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
                DashboardHub.Instance.HandleClientMessage(client, doc.RootElement);
            }
            catch { client.Queue(new { type = "error", message = "잘못된 요청입니다." }); }
            message.SetLength(0);
        }
    }

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

    /// <summary>LAN 브라우저 1명 = 실제 WebSocket 1개를 감싸는 sink.</summary>
    private sealed class LanClientSink : IClientSink
    {
        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; }
        public object Sync { get; } = new();
        public string? SelectedRoomId { get; set; }
        public long LastOutputSequence { get; set; }

        private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public LanClientSink(WebSocket socket) => Socket = socket;

        public void Queue(object message) => _outgoing.Writer.TryWrite(JsonSerializer.Serialize(message));
        public void Complete() => _outgoing.Writer.TryComplete();

        public async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            await foreach (var json in _outgoing.Reader.ReadAllAsync(cancellationToken))
            {
                if (Socket.State != WebSocketState.Open) break;
                await Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);
            }
        }
    }
}
