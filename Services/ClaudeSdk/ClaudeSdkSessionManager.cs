using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using DevezCode.Models;
using DevezCode.Services.Terminal;

namespace DevezCode.Services.ClaudeSdk;

public sealed record ClaudeSdkAttachment(
    string Kind,
    string Name,
    string MediaType = "",
    string Data = "",
    string Preview = "",
    string Path = "",
    long Size = 0,
    int Width = 0,
    int Height = 0);

public sealed record ClaudeSdkAttachmentPreview(string Kind, string Name, string Preview = "");

public sealed record ClaudeSdkEvent(
    string Type,
    string Text = "",
    string ToolName = "",
    string RequestId = "",
    string SessionId = "",
    string Question = "",
    JsonElement? Input = null,
    bool IsError = false,
    string ToolUseId = "",
    string StreamId = "",
    IReadOnlyList<ClaudeSdkAttachmentPreview>? Attachments = null);

public sealed class ClaudeSdkSessionManager
{
    private sealed class ManagedSession
    {
        public required SessionItem Item { get; init; }
        public required ClaudeSdkBridgeProcess Bridge { get; init; }
        public List<ClaudeSdkEvent> Events { get; } = new();
        public int BufferedDeltaIndex { get; set; } = -1;
        public StringBuilder? BufferedDeltaText { get; set; }
        public bool StoppedPublished { get; set; }
    }

    public static ClaudeSdkSessionManager Instance { get; } = new();

    private readonly Dictionary<string, ManagedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ClaudeSdkEvent>> _retainedEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action<string, ClaudeSdkEvent>? EventReceived;

    public bool IsStarted(string roomId)
    {
        lock (_sessions) return _sessions.TryGetValue(roomId, out var session) && session.Bridge.IsRunning;
    }

    /// <summary>앱 종료 시 graceful 배수가 필요한 GUI(SDK) 세션이 하나라도 있는가.</summary>
    public bool HasSessions
    {
        get { lock (_sessions) return _sessions.Count > 0; }
    }

    public IReadOnlyList<ClaudeSdkEvent> GetEvents(string roomId)
    {
        lock (_sessions)
        {
            if (_sessions.TryGetValue(roomId, out var session)) return SnapshotEvents(session);
            return _retainedEvents.TryGetValue(roomId, out var retained)
                ? retained.ToArray()
                : Array.Empty<ClaudeSdkEvent>();
        }
    }

    public async Task EnsureStartedAsync(SessionItem item, string cwd)
    {
        lock (_sessions)
        {
            if (_sessions.TryGetValue(item.Id, out var existing) && existing.Bridge.IsRunning) return;
        }

        await _gate.WaitAsync();
        try
        {
            lock (_sessions)
            {
                if (_sessions.TryGetValue(item.Id, out var existing) && existing.Bridge.IsRunning) return;
            }

            ClaudeSdkEvent[] previousEvents;
            lock (_sessions)
            {
                if (_sessions.TryGetValue(item.Id, out var previous)) previousEvents = SnapshotEvents(previous);
                else if (_retainedEvents.TryGetValue(item.Id, out var retained)) previousEvents = retained.ToArray();
                else previousEvents = Array.Empty<ClaudeSdkEvent>();
            }

            var bridge = new ClaudeSdkBridgeProcess(item.Id);
            var managed = new ManagedSession { Item = item, Bridge = bridge };
            managed.Events.AddRange(previousEvents);
            bridge.EventReceived += evt => OnBridgeEvent(managed, evt);
            bridge.Exited += () => OnBridgeExited(managed);
            lock (_sessions)
            {
                _sessions[item.Id] = managed;
                _retainedEvents.Remove(item.Id);
            }

            item.IsAlive = true;
            DiagLog.Write($"ClaudeSdk start room={item.Id} cwd={cwd}");
            var sessionId = SettingsService.LoadClaudeCodeRoomSession(item.Id);
            if (!string.IsNullOrWhiteSpace(sessionId)
                && TerminalSessionManager.FindClaudeTranscriptPath(cwd, sessionId) == null)
            {
                // 이번 기동만 resume 을 건너뛴다. settings 에서 지우면 transcript 조회가 일시적으로
                // 실패했을 때(폴더 권한·동기화 지연·경로 이동)도 방↔대화 연결이 영구히 끊긴다.
                // 새 세션이 뜨면 session 이벤트가 어차피 같은 키를 덮어쓴다.
                DiagLog.Write($"ClaudeSdk resume skipped room={item.Id} sid={sessionId}: transcript 없음");
                sessionId = null;
            }
            var model = SettingsService.LoadClaudeCodeRoomModel(item.Id);
            var effort = SettingsService.LoadClaudeCodeRoomEffort(item.Id);
            var permissionMode = SettingsService.LoadClaudeCodeRoomPermissionMode(item.Id);
            var claudePath = AgentRegistry.ResolvePath(AgentRegistry.Find("claude")!);
            await bridge.StartAsync(cwd, sessionId, model, effort, permissionMode, claudePath);
            DiagLog.Write($"ClaudeSdk process started room={item.Id}");
        }
        catch (Exception ex)
        {
            DiagLog.Write($"ClaudeSdk start failed room={item.Id}: {ex}");
            item.IsAlive = false;
            item.IsBusy = false;
            Publish(item.Id, new ClaudeSdkEvent("error", ex.Message, IsError: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SendPromptAsync(
        SessionItem item,
        string cwd,
        string text,
        IReadOnlyList<ClaudeSdkAttachment>? attachments = null)
    {
        attachments ??= Array.Empty<ClaudeSdkAttachment>();
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0) return false;
        await EnsureStartedAsync(item, cwd);
        ManagedSession? session;
        lock (_sessions) _sessions.TryGetValue(item.Id, out session);
        if (session is not { Bridge.IsRunning: true }) return false;

        item.IsAlive = true;
        item.IsBusy = true;
        item.IsWaitingChoice = false;
        var trimmed = text.Trim();
        item.LastMessage = trimmed.Length > 0 ? trimmed : $"첨부 파일 {attachments.Count}개";
        var previews = attachments
            .Select(value => new ClaudeSdkAttachmentPreview(value.Kind, value.Name, value.Preview))
            .ToArray();
        Publish(item.Id, new ClaudeSdkEvent("user", trimmed, Attachments: previews));
        await session.Bridge.SendAsync(new
        {
            type = "prompt",
            text = trimmed,
            images = attachments.Where(value => value.Kind == "image").Select(value => new
            {
                name = value.Name,
                mediaType = value.MediaType,
                data = value.Data,
                size = value.Size,
                width = value.Width,
                height = value.Height,
            }).ToArray(),
            files = attachments.Where(value => value.Kind == "file").Select(value => new
            {
                name = value.Name,
                path = value.Path,
                size = value.Size,
            }).ToArray(),
        });
        return true;
    }

    public async Task RespondPermissionAsync(
        string roomId,
        string requestId,
        bool allow,
        string? answer = null,
        IReadOnlyDictionary<string, string>? answers = null)
    {
        ManagedSession? session;
        lock (_sessions) _sessions.TryGetValue(roomId, out session);
        if (session == null) return;
        session.Item.IsWaitingChoice = false;
        await session.Bridge.SendAsync(new
        {
            type = "permission",
            requestId,
            allow,
            answer = answer ?? "",
            answers = answers ?? new Dictionary<string, string>(),
        });
        Publish(roomId, new ClaudeSdkEvent(
            "permission_resolved", Text: allow ? "allow" : "deny", RequestId: requestId), session);
    }

    public async Task InterruptAsync(string roomId)
    {
        ManagedSession? session;
        lock (_sessions) _sessions.TryGetValue(roomId, out session);
        if (session == null) return;
        session.Item.IsBusy = false;
        session.Item.IsWaitingChoice = false;
        await session.Bridge.SendAsync(new { type = "interrupt" });
        Publish(roomId, new ClaudeSdkEvent("interrupting"), session);
    }

    public async Task SetModelAsync(string roomId, string? model)
    {
        ManagedSession? session;
        lock (_sessions) _sessions.TryGetValue(roomId, out session);
        if (session == null) return;
        await session.Bridge.SendAsync(new { type = "set_model", model = model ?? "" });
    }

    public async Task SetEffortAsync(string roomId, string effort)
    {
        if (!SettingsService.IsSupportedClaudeCodeEffort(effort)) return;
        ManagedSession? session;
        lock (_sessions) _sessions.TryGetValue(roomId, out session);
        if (session == null) return;
        await session.Bridge.SendAsync(new { type = "set_effort", effort });
    }

    public async Task SetPermissionModeAsync(string roomId, string permissionMode)
    {
        if (!ClaudeGlobalSettings.IsSupportedPermissionMode(permissionMode)) return;
        ManagedSession? session;
        lock (_sessions) _sessions.TryGetValue(roomId, out session);
        if (session == null) return;
        await session.Bridge.SendAsync(new { type = "set_permission_mode", permissionMode });
    }

    public async Task RefreshCapabilitiesAsync(string roomId)
    {
        ManagedSession? session;
        lock (_sessions) _sessions.TryGetValue(roomId, out session);
        if (session == null) return;
        await session.Bridge.SendAsync(new { type = "refresh_capabilities" });
    }

    public async Task StopAsync(string roomId, bool purge = false)
    {
        ManagedSession? session;
        lock (_sessions)
        {
            if (!_sessions.Remove(roomId, out session)) return;
        }
        await session.Bridge.DisposeAsync();
        session.Item.IsAlive = false;
        session.Item.IsBusy = false;
        session.Item.IsWaitingChoice = false;
        PublishStopped(session);
        lock (_sessions)
        {
            if (purge) _retainedEvents.Remove(roomId);
            else _retainedEvents[roomId] = session.Events.ToList();
        }
        if (purge) SettingsService.SaveClaudeCodeRoomSession(roomId, "");
    }

    public async Task ShutdownAllAsync()
    {
        ManagedSession[] sessions;
        lock (_sessions)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        await Task.WhenAll(sessions.Select(s => s.Bridge.DisposeAsync().AsTask()));
    }

    private void OnBridgeEvent(ManagedSession session, ClaudeSdkEvent evt)
    {
        if (evt.Type is "assistant_delta" or "thinking_delta")
        {
            Publish(session.Item.Id, evt, session);
            return;
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnBridgeEvent(session, evt));
            return;
        }
        var item = session.Item;
        DiagLog.Write($"ClaudeSdk event room={item.Id} type={evt.Type} error={evt.IsError}");
        switch (evt.Type)
        {
            case "ready":
            case "starting":
                item.IsAlive = true;
                break;
            case "session" when !string.IsNullOrWhiteSpace(evt.SessionId):
                SettingsService.SaveClaudeCodeRoomSession(item.Id, evt.SessionId);
                break;
            case "conversation_reset":
                lock (_sessions)
                {
                    session.Events.Clear();
                    session.BufferedDeltaIndex = -1;
                    session.BufferedDeltaText = null;
                }
                item.IsBusy = false;
                item.IsWaitingChoice = false;
                item.LastMessage = "";
                break;
            case "config_changed" when evt.Input is { } input
                                               && input.ValueKind == JsonValueKind.Object
                                               && input.TryGetProperty("persist", out var persist)
                                               && persist.ValueKind == JsonValueKind.True:
                if (input.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                    SettingsService.SaveClaudeCodeRoomModel(item.Id, model.GetString());
                if (input.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.String)
                    SettingsService.SaveClaudeCodeRoomEffort(item.Id, effort.GetString());
                if (input.TryGetProperty("permissionMode", out var permission)
                    && permission.ValueKind == JsonValueKind.String)
                {
                    var permissionMode = permission.GetString();
                    SettingsService.SaveClaudeCodeRoomPermissionMode(item.Id, permissionMode);
                    if (!ClaudeGlobalSettings.SetDefaultPermissionMode(permissionMode))
                        DiagLog.Write($"Claude permission default save failed mode={permissionMode}");
                }
                break;
            case "assistant":
            case "assistant_stream_start":
            case "thinking_stream_start":
                item.IsBusy = true;
                break;
            case "permission":
                item.IsWaitingChoice = true;
                break;
            case "result":
                item.IsBusy = false;
                item.IsWaitingChoice = false;
                break;
            case "error":
                item.IsBusy = false;
                item.IsWaitingChoice = false;
                break;
            case "stopped":
                item.IsAlive = false;
                item.IsBusy = false;
                item.IsWaitingChoice = false;
                session.StoppedPublished = true;
                break;
        }
        Publish(item.Id, evt, session);
    }

    private void OnBridgeExited(ManagedSession session)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnBridgeExited(session));
            return;
        }
        session.Item.IsAlive = false;
        session.Item.IsBusy = false;
        session.Item.IsWaitingChoice = false;
        PublishStopped(session);
    }

    private void PublishStopped(ManagedSession session)
    {
        if (session.StoppedPublished) return;
        session.StoppedPublished = true;
        Publish(session.Item.Id, new ClaudeSdkEvent("stopped"), session);
    }

    private void Publish(string roomId, ClaudeSdkEvent evt, ManagedSession? known = null)
    {
        lock (_sessions)
        {
            var session = known;
            if (session == null) _sessions.TryGetValue(roomId, out session);
            if (session != null)
            {
                bool delta = evt.Type is "assistant_delta" or "thinking_delta";
                if (delta)
                {
                    var continuesBufferedDelta = session.BufferedDeltaIndex == session.Events.Count - 1
                        && session.BufferedDeltaIndex >= 0
                        && session.Events[session.BufferedDeltaIndex] is var previous
                        && previous.Type == evt.Type
                        && previous.StreamId == evt.StreamId;
                    if (continuesBufferedDelta)
                        session.BufferedDeltaText!.Append(evt.Text);
                    else
                    {
                        CommitBufferedDelta(session);
                        session.Events.Add(evt);
                        session.BufferedDeltaIndex = session.Events.Count - 1;
                        session.BufferedDeltaText = new StringBuilder(evt.Text);
                    }
                }
                else
                {
                    CommitBufferedDelta(session);
                    session.Events.Add(evt);
                }
            }
        }
        EventReceived?.Invoke(roomId, evt);
    }

    private static ClaudeSdkEvent[] SnapshotEvents(ManagedSession session)
    {
        var snapshot = session.Events.ToArray();
        if (session.BufferedDeltaIndex >= 0 && session.BufferedDeltaIndex < snapshot.Length
            && session.BufferedDeltaText != null)
            snapshot[session.BufferedDeltaIndex] = snapshot[session.BufferedDeltaIndex] with
            {
                Text = session.BufferedDeltaText.ToString(),
            };
        return snapshot;
    }

    private static void CommitBufferedDelta(ManagedSession session)
    {
        if (session.BufferedDeltaIndex >= 0 && session.BufferedDeltaIndex < session.Events.Count
            && session.BufferedDeltaText != null)
            session.Events[session.BufferedDeltaIndex] = session.Events[session.BufferedDeltaIndex] with
            {
                Text = session.BufferedDeltaText.ToString(),
            };
        session.BufferedDeltaIndex = -1;
        session.BufferedDeltaText = null;
    }
}

internal sealed class ClaudeSdkBridgeProcess : IAsyncDisposable
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private readonly string _roomId;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Process? _process;

    public ClaudeSdkBridgeProcess(string roomId) => _roomId = roomId;

    public bool IsRunning => _process is { HasExited: false };
    public event Action<ClaudeSdkEvent>? EventReceived;
    public event Action? Exited;

    public async Task StartAsync(string cwd, string? sessionId, string? model, string? effort,
        string permissionMode, string? claudePath)
    {
        var bridgePath = await EnsureInstalledAsync();
        var nodePath = ResolveExecutable("node.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            WorkingDirectory = Path.GetDirectoryName(bridgePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(bridgePath);
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += (_, _) => Exited?.Invoke();
        if (!_process.Start()) throw new InvalidOperationException("Claude SDK 프로세스를 시작하지 못했습니다.");
        _ = ReadOutputAsync(_process);
        _ = ReadErrorsAsync(_process);
        await SendAsync(new { type = "start", cwd, sessionId, model, effort, permissionMode, claudePath });
    }

    public async Task SendAsync(object command)
    {
        var process = _process;
        if (process is not { HasExited: false }) return;
        var json = JsonSerializer.Serialize(command);
        await _writeGate.WaitAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(json);
            await process.StandardInput.FlushAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadOutputAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string Get(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? "" : "";
                    JsonElement? input = root.TryGetProperty("input", out var inputValue) ? inputValue.Clone() : null;
                    EventReceived?.Invoke(new ClaudeSdkEvent(
                        Type: Get("type"), Text: Get("text"), ToolName: Get("toolName"),
                        RequestId: Get("requestId"), SessionId: Get("sessionId"), Question: Get("question"),
                        Input: input,
                        IsError: root.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True,
                        ToolUseId: Get("toolUseId"), StreamId: Get("streamId")));
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"ClaudeSdk bridge parse room={_roomId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) { DiagLog.Write($"ClaudeSdk output room={_roomId}: {ex.Message}"); }
    }

    private async Task ReadErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) DiagLog.Write($"ClaudeSdk room={_roomId}: {line}");
        }
        catch { }
    }

    private static async Task<string> EnsureInstalledAsync()
    {
        await InstallGate.WaitAsync();
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "Resources", "ClaudeSdk");
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevezCode", "ClaudeSdk");
            Directory.CreateDirectory(target);
            foreach (var name in new[] { "bridge.mjs", "package.json", "package-lock.json" })
            {
                var from = Path.Combine(source, name);
                if (!File.Exists(from)) throw new FileNotFoundException("Claude SDK 리소스를 찾을 수 없습니다.", from);
                File.Copy(from, Path.Combine(target, name), overwrite: true);
            }

            var packageEntry = Path.Combine(target, "node_modules", "@anthropic-ai", "claude-agent-sdk", "package.json");
            var desiredVersion = ReadVersion(Path.Combine(target, "package.json"), dependency: true);
            var installedVersion = ReadVersion(packageEntry, dependency: false);
            if (string.IsNullOrWhiteSpace(installedVersion)
                || !string.Equals(installedVersion, desiredVersion, StringComparison.OrdinalIgnoreCase))
            {
                var nodePath = ResolveExecutable("node.exe");
                var npmPath = Path.Combine(Path.GetDirectoryName(nodePath)!, "npm.cmd");
                if (!File.Exists(npmPath)) npmPath = ResolveExecutable("npm.cmd");
                var npm = new ProcessStartInfo
                {
                    // .cmd를 파일명만 넘기면 cmd.exe가 WorkingDirectory를 %~dp0로 오인해
                    // <작업폴더>\node_modules\npm을 찾는다. 반드시 실제 절대 경로로 실행한다.
                    FileName = npmPath,
                    WorkingDirectory = target,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                npm.ArgumentList.Add("install");
                npm.ArgumentList.Add("--omit=dev");
                npm.ArgumentList.Add("--no-audit");
                npm.ArgumentList.Add("--no-fund");
                using var process = Process.Start(npm) ?? throw new InvalidOperationException("npm을 시작하지 못했습니다.");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Claude Agent SDK 설치에 실패했습니다. Node.js와 네트워크를 확인하세요.\n" + await error);
                _ = await output;
            }
            return Path.Combine(target, "bridge.mjs");

            static string? ReadVersion(string path, bool dependency)
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (!dependency)
                        return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
                    return document.RootElement.TryGetProperty("dependencies", out var dependencies)
                           && dependencies.TryGetProperty("@anthropic-ai/claude-agent-sdk", out var sdk)
                        ? sdk.GetString()?.TrimStart('^', '~') : null;
                }
                catch { return null; }
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private static string ResolveExecutable(string fileName)
    {
        if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return Path.GetFullPath(fileName);
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (directory.Length == 0) continue;
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
        }
        throw new FileNotFoundException($"{fileName} 실행 파일을 PATH에서 찾을 수 없습니다. Node.js 18 이상을 설치하세요.");
    }

    /// <summary>브리지가 SDK 쿼리를 배수하고 claude CLI 자식이 transcript 를 flush 할 시간.
    /// 브리지 자체 안전망(6s)보다 넉넉해야 정상 배수를 중간에 끊지 않는다.</summary>
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(8);

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                await SendDirectAsync(process, new { type = "shutdown" });
                process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(ShutdownWait);
            }
        }
        catch (TimeoutException)
        {
            // 여기까지 안 끝나면 브리지가 멈춘 것 — 트리째 정리하지 않으면 node 와 claude CLI 가
            // 고아로 남아 transcript 파일을 계속 붙잡는다. (DevezCode 본체가 아닌 에이전트 자식 프로세스)
            DiagLog.Write($"ClaudeSdk shutdown timeout room={_roomId}: 프로세스 트리 정리");
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        catch { }
        finally { process.Dispose(); }
    }

    private static async Task SendDirectAsync(Process process, object command)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync();
    }
}
