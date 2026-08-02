using System.Collections.Concurrent;
using System.Text.Json;
using DevezCode.Services.Terminal;

namespace DevezCode.Services.Dashboard;

/// <summary>LAN과 외부 릴레이가 공유하는 대시보드 프로토콜 코어.</summary>
public sealed class DashboardHub
{
    public static DashboardHub Instance { get; } = new();

    private readonly ConcurrentDictionary<Guid, IClientSink> _clients = new();
    private readonly object _controllerLock = new();
    private Guid? _controllerId;
    private readonly object _activeLock = new();
    private int _activeTransports;
    private CancellationTokenSource? _watchCts;

    private DashboardHub() { }

    public void Activate()
    {
        lock (_activeLock)
        {
            if (_activeTransports++ != 0) return;
            TerminalDisplayOutputHub.OutputReceived += BroadcastOutput;
            TerminalDisplayOutputHub.SizeChanged += BroadcastSize;
            _watchCts = new CancellationTokenSource();
            _ = WatchSessionsAsync(_watchCts.Token);
        }
    }

    public void Deactivate()
    {
        lock (_activeLock)
        {
            if (_activeTransports == 0 || --_activeTransports != 0) return;
            TerminalDisplayOutputHub.OutputReceived -= BroadcastOutput;
            TerminalDisplayOutputHub.SizeChanged -= BroadcastSize;
            _watchCts?.Cancel();
            _watchCts?.Dispose();
            _watchCts = null;
        }
    }

    public void AddClient(IClientSink client)
    {
        _clients[client.Id] = client;
        var workspace = WorkspaceStore.LoadDashboardSnapshot();
        DiagLog.Write($"relay dashboard join: projects={workspace.Projects.Count}, sessions={workspace.Projects.Sum(project => project.Sessions.Count)}");
        client.Queue(new { type = "hello", clientId = client.Id, controllerId = CurrentControllerId() });
        client.Queue(BuildSessionsMessage());
    }

    public void RemoveClient(Guid clientId)
    {
        _clients.TryRemove(clientId, out _);
        ReleaseControl(clientId);
    }

    public void HandleClientMessage(IClientSink client, JsonElement root)
    {
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        switch (type)
        {
            case "subscribe":
            {
                var roomId = root.TryGetProperty("roomId", out var roomElement) ? roomElement.GetString() : null;
                var session = string.IsNullOrWhiteSpace(roomId) ? null : TerminalSessionManager.Instance.Get(roomId!);
                if (session is not { IsAlive: true })
                {
                    var exists = WorkspaceStore.LoadDashboardSnapshot().Projects
                        .SelectMany(project => project.Sessions).Any(item => item.Id == roomId);
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
                var roomId = root.TryGetProperty("roomId", out var roomElement) ? roomElement.GetString() : null;
                var data = root.TryGetProperty("data", out var dataElement) ? dataElement.GetString() ?? "" : "";
                if (roomId != client.SelectedRoomId || data.Length > 65536) return;
                TerminalSessionManager.Instance.Get(roomId!)?.TryWrite(data);
                break;
            }
            case "resize":
            {
                if (!HasControl(client.Id)) return;
                var roomId = root.TryGetProperty("roomId", out var roomElement) ? roomElement.GetString() : null;
                if (roomId != client.SelectedRoomId || string.IsNullOrWhiteSpace(roomId)) return;
                var cols = root.TryGetProperty("cols", out var colsElement) ? colsElement.GetInt32() : 0;
                var rows = root.TryGetProperty("rows", out var rowsElement) ? rowsElement.GetInt32() : 0;
                if (cols is < 2 or > 500 || rows is < 2 or > 200) return;
                var session = TerminalSessionManager.Instance.Get(roomId);
                if (session is not { IsAlive: true }) return;
                session.Resize(cols, rows);
                TerminalDisplayOutputHub.PublishSize(roomId, cols, rows);
                Broadcast(BuildSessionsMessage());
                break;
            }
            case "imeProbe":
            {
                var roomId = root.TryGetProperty("roomId", out var roomElement) ? roomElement.GetString() : null;
                var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(message))
                    DiagLog.Write($"[relay-ime room={roomId}] {message}");
                break;
            }
            case "refresh":
                client.Queue(BuildSessionsMessage());
                break;
        }
    }

    private void StartAndSubscribe(IClientSink client, string roomId)
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
            catch { }

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
            DiagLog.Write($"dashboard start session {roomId}: {ex}");
            client.Queue(new { type = "error", message = "세션을 시작하지 못했습니다." });
        }
    }

    private static void Subscribe(IClientSink client, string roomId, TerminalSession session)
    {
        lock (client.Sync)
        {
            if (client.SelectedRoomId != roomId) return;
            var replay = GetReplay(roomId, session);
            client.LastOutputSequence = replay.Sequence;
            client.Queue(new
            {
                type = "snapshot", roomId, cols = session.Cols, rows = session.Rows,
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
                DiagLog.Write("dashboard session watch: " + ex.Message);
                try { await Task.Delay(1000, cancellationToken); } catch { break; }
            }
        }
    }

    private static TerminalDisplaySnapshot GetReplay(string roomId, TerminalSession session)
    {
        var display = TerminalDisplayOutputHub.GetReplaySnapshot(roomId);
        return display.Data.Length > 0
            ? display
            : new TerminalDisplaySnapshot(session.GetRecentOutputSnapshot(), 0);
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
            id = folder.Id, name = folder.Name, rootOrder = folder.RootOrder, isExpanded = folder.IsExpanded,
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
        return new
        {
            type = "sessions",
            folders,
            projects,
            sessions = projects.SelectMany(project => project.sessions).ToArray(),
            controllerId = CurrentControllerId(),
            appTheme = App.CurrentTheme,
            fontFamily = config.FontFamily,
            fontSize = config.FontSizePx,
            terminalTheme = ToXtermTheme(config.Scheme),
            uiTheme = UiTheme(App.CurrentTheme),
        };
    }

    private static object ToXtermTheme(WtColorScheme scheme) => new
    {
        background = scheme.Background, foreground = scheme.Foreground, cursor = scheme.CursorColor,
        cursorAccent = scheme.Background, selectionBackground = scheme.SelectionBackground,
        black = scheme.Black, red = scheme.Red, green = scheme.Green, yellow = scheme.Yellow,
        blue = scheme.Blue, magenta = scheme.Purple, cyan = scheme.Cyan, white = scheme.White,
        brightBlack = scheme.BrightBlack, brightRed = scheme.BrightRed, brightGreen = scheme.BrightGreen,
        brightYellow = scheme.BrightYellow, brightBlue = scheme.BrightBlue, brightMagenta = scheme.BrightPurple,
        brightCyan = scheme.BrightCyan, brightWhite = scheme.BrightWhite,
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
        var message = new { type = "output", roomId, data = Convert.ToBase64String(bytes) };
        foreach (var client in _clients.Values)
        {
            lock (client.Sync)
            {
                if (client.SelectedRoomId != roomId || sequence <= client.LastOutputSequence) continue;
                client.LastOutputSequence = sequence;
                client.Queue(message);
            }
        }
    }

    private void BroadcastSize(string roomId, int cols, int rows)
    {
        foreach (var client in _clients.Values)
            lock (client.Sync)
                if (client.SelectedRoomId == roomId) client.Queue(new { type = "size", roomId, cols, rows });
    }

    public void Broadcast(object message)
    {
        foreach (var client in _clients.Values) client.Queue(message);
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
}
