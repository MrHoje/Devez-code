using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using DevezCode.Models;
using DevezCode.Services.Terminal;

namespace DevezCode.Services;

/// <summary>Discord Gateway bridge. Disabled unless configured in SettingsService.</summary>
public sealed class DiscordBotService : IDisposable
{
    public static DiscordBotService Instance { get; } = new();

    private readonly object _sync = new();
    // 자동 시작 대기 중인 입력: sessionId → 준비되면 주입할 메시지들.
    private readonly Dictionary<string, List<string>> _pendingInput = new(StringComparer.Ordinal);
    private ObservableCollection<ProjectItem>? _projects;
    private DiscordSocketClient? _client;
    private bool _starting;
    // 꺼진 세션을 UI 스레드에서 열어달라는 요청 핸들러. MainWindow 가 주입.
    private Action<string>? _openSessionRequest;

    public bool IsConnected { get; private set; }

    private DiscordBotService() { }

    public void SetProjects(ObservableCollection<ProjectItem> projects) => _projects = projects;

    /// <summary>꺼진 세션 스레드에 메시지가 오면 호출할 "세션 열기" 핸들러를 등록한다(MainWindow 가 UI 스레드에서 처리).</summary>
    public void SetOpenSessionRequest(Action<string> handler) => _openSessionRequest = handler;

    public void Start()
    {
        if (!SettingsService.LoadDiscordEnabled()) return;
        if (string.IsNullOrWhiteSpace(SettingsService.LoadDiscordBotToken())) return;
        _ = StartAsync();
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    private async Task StartAsync()
    {
        lock (_sync)
        {
            if (_starting || _client != null) return;
            _starting = true;
        }

        try
        {
            var client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
                AlwaysDownloadUsers = false,
                LogGatewayIntentWarnings = false,
            });

            client.Ready += OnReady;
            client.MessageReceived += OnMessageReceived;
            client.Disconnected += _ => { IsConnected = false; return Task.CompletedTask; };

            await client.LoginAsync(TokenType.Bot, SettingsService.LoadDiscordBotToken());
            await client.StartAsync();

            lock (_sync) _client = client;
        }
        catch
        {
            IsConnected = false;
            lock (_sync)
            {
                _client?.Dispose();
                _client = null;
            }
        }
        finally
        {
            lock (_sync) _starting = false;
        }
    }

    public void Stop()
    {
        DiscordSocketClient? client;
        lock (_sync)
        {
            client = _client;
            _client = null;
            _starting = false;
            _pendingInput.Clear();
        }

        IsConnected = false;
        if (client == null) return;
        try { client.MessageReceived -= OnMessageReceived; } catch { }
        try { client.Ready -= OnReady; } catch { }
        _ = Task.Run(async () =>
        {
            try { await client.StopAsync(); } catch { }
            try { await client.LogoutAsync(); } catch { }
            client.Dispose();
        });
    }

    private async Task OnReady()
    {
        IsConnected = true;
        await SyncWorkspaceAsync();
    }

    public async Task SyncWorkspaceAsync()
    {
        var projects = _projects;
        if (!IsConfigured || projects == null) return;

        foreach (var project in projects.Where(p => p.IsActive))
        {
            var channel = await EnsureProjectChannelAsync(project);
            if (channel == null) continue;
            foreach (var session in project.Sessions)
                await EnsureSessionThreadAsync(project, session);
        }
    }

    public async Task NotifySessionDoneAsync(ProjectItem? project, SessionItem session)
    {
        if (!SettingsService.LoadDiscordNotifySessionDone()) return;
        if (project == null || !IsConfigured) return;

        var thread = await EnsureSessionThreadAsync(project, session);
        if (thread == null) return;

        var title = string.IsNullOrWhiteSpace(project.Name) ? "프로젝트" : project.Name;
        var sess = string.IsNullOrWhiteSpace(session.Name) ? "세션" : session.Name;

        // 에이전트의 최종 답변 텍스트를 우선 전송. 추출 불가(codex/opencode 등)면 질문 폴백.
        var agentId = string.IsNullOrWhiteSpace(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;
        var reply = AgentReplyService.TryGetLastAssistantReply(session.Id, agentId);

        string body;
        if (!string.IsNullOrWhiteSpace(reply))
            body = $"✅ **{title} / {sess}** 응답 완료\n{HeadForDiscord(reply, 1800)}";
        else
        {
            var last = string.IsNullOrWhiteSpace(session.LastMessage) ? "" : $"\n> {TrimForDiscord(session.LastMessage, 500)}";
            body = $"✅ **{title} / {sess}** 응답 완료{last}";
        }
        await SafeSendAsync(thread, body);
    }

    private async Task OnMessageReceived(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        var content = message.Content?.Trim() ?? "";
        if (content.Length == 0) return;

        if (content.StartsWith("!dc", StringComparison.OrdinalIgnoreCase))
        {
            await HandleCommandAsync(message, content);
            return;
        }

        if (message.Channel is not SocketThreadChannel thread) return;
        var sessionId = SettingsService.FindDiscordSessionByThread(thread.Id);
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var session = TerminalSessionManager.Instance.Get(sessionId);
        if (session is not { IsAlive: true })
        {
            await RequestAutoStartAsync(thread, sessionId, content);
            return;
        }

        session.Write(content);
        session.Write("\r");
    }

    /// <summary>꺼진 세션에 메시지가 오면 그 메시지를 버퍼링하고 UI 에 세션 시작을 요청한다.
    /// 세션이 준비되면 <see cref="NotifySessionReady"/> 가 버퍼를 주입한다.</summary>
    private async Task RequestAutoStartAsync(SocketThreadChannel thread, string sessionId, string content)
    {
        if (_openSessionRequest == null)
        {
            await SafeSendAsync(thread, "⚠️ 해당 세션 터미널이 실행 중이 아닙니다. DevezCode에서 세션을 먼저 열어주세요.");
            return;
        }

        bool firstRequest;
        lock (_sync)
        {
            if (!_pendingInput.TryGetValue(sessionId, out var queue))
            {
                queue = new List<string>();
                _pendingInput[sessionId] = queue;
                firstRequest = true;
            }
            else firstRequest = false;
            queue.Add(content);
        }

        if (!firstRequest) return; // 이미 시작 요청 진행 중 — 메시지만 버퍼에 누적

        await SafeSendAsync(thread, "⏳ 세션을 시작하는 중입니다. 준비되면 메시지를 전달합니다…");
        try { _openSessionRequest.Invoke(sessionId); } catch { /* UI 디스패치 실패는 무시 */ }

        // 안전장치: 일정 시간 내 준비되지 않으면 버퍼를 폐기하고 안내한다.
        _ = Task.Run(async () =>
        {
            await Task.Delay(45000);
            bool stillPending;
            lock (_sync) stillPending = _pendingInput.Remove(sessionId);
            if (stillPending)
                await SafeSendAsync(thread, "⚠️ 세션 시작이 시간 내에 완료되지 않았습니다. DevezCode에서 직접 세션을 열어주세요.");
        });
    }

    /// <summary>UI 가 세션 터미널 준비 완료를 알리면 자동 시작 대기 중이던 메시지를 순서대로 주입한다.</summary>
    public void NotifySessionReady(string sessionId)
    {
        List<string>? queued;
        lock (_sync)
        {
            if (!_pendingInput.Remove(sessionId, out queued)) return;
        }
        if (queued == null || queued.Count == 0) return;

        _ = Task.Run(async () =>
        {
            // TUI 가 입력란을 그릴 약간의 여유를 둔 뒤 주입한다.
            await Task.Delay(500);
            var session = TerminalSessionManager.Instance.Get(sessionId);
            if (session is not { IsAlive: true }) return;
            foreach (var msg in queued)
            {
                session.Write(msg);
                session.Write("\r");
                await Task.Delay(200);
            }
        });
    }

    private async Task HandleCommandAsync(SocketMessage message, string content)
    {
        if (content.Equals("!dc sync", StringComparison.OrdinalIgnoreCase))
        {
            await SyncWorkspaceAsync();
            await SafeSendAsync(message.Channel, "동기화 완료");
            return;
        }

        if (content.Equals("!dc status", StringComparison.OrdinalIgnoreCase) ||
            content.Equals("!dc list", StringComparison.OrdinalIgnoreCase))
        {
            await SafeSendAsync(message.Channel, BuildStatusText());
            return;
        }

        await SafeSendAsync(message.Channel, "사용법: `!dc status`, `!dc list`, `!dc sync`\n세션 스레드에 일반 메시지를 보내면 해당 터미널로 전달됩니다.");
    }

    private string BuildStatusText()
    {
        var projects = _projects;
        if (projects == null || projects.Count == 0) return "등록된 프로젝트가 없습니다.";

        var sb = new StringBuilder();
        sb.AppendLine("**DevezCode 세션 상태**");
        foreach (var project in projects.Where(p => p.IsActive))
        {
            sb.AppendLine($"\n📁 {project.Name}");
            foreach (var session in project.Sessions)
            {
                var state = session.IsBusy ? "작업중" : session.IsAlive ? "실행중" : "중지";
                var agent = string.IsNullOrWhiteSpace(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;
                sb.AppendLine($"- {session.Name} · {agent} · {state}");
            }
        }
        return TrimForDiscord(sb.ToString(), 1900);
    }

    public async Task<ITextChannel?> EnsureProjectChannelAsync(ProjectItem project)
    {
        var client = _client;
        if (client == null || !IsConfigured) return null;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return null;

        var channelId = SettingsService.LoadDiscordProjectChannel(project.Path);
        var existing = channelId == 0 ? null : guild.GetTextChannel(channelId);
        if (existing != null) return existing;

        var safeProjectName = SafeDiscordName(project.Name);
        var categoryId = SettingsService.LoadDiscordProjectCategory(project.Path);
        ICategoryChannel? category = categoryId == 0 ? null : guild.GetCategoryChannel(categoryId);
        if (category == null)
        {
            category = await guild.CreateCategoryChannelAsync(safeProjectName);
            SettingsService.SaveDiscordProjectCategory(project.Path, category.Id);
        }

        var channel = await guild.CreateTextChannelAsync("sessions", props =>
        {
            props.CategoryId = category.Id;
            props.Topic = project.Path;
        });
        SettingsService.SaveDiscordProjectChannel(project.Path, channel.Id);
        return channel;
    }

    public async Task<IMessageChannel?> EnsureSessionThreadAsync(ProjectItem project, SessionItem session)
    {
        var client = _client;
        if (client == null || !IsConfigured) return null;

        var threadId = SettingsService.LoadDiscordSessionThread(session.Id);
        if (threadId != 0 && client.GetChannel(threadId) is IMessageChannel existing)
            return existing;

        var channel = await EnsureProjectChannelAsync(project);
        if (channel == null) return null;

        var thread = await channel.CreateThreadAsync(SafeThreadName(session.Name), ThreadType.PublicThread, ThreadArchiveDuration.OneDay);
        SettingsService.SaveDiscordSessionThread(session.Id, thread.Id);
        await SafeSendAsync(thread, $"🔗 DevezCode 세션 연결: `{session.Name}` / `{session.AgentId}`");
        return thread;
    }

    private bool IsConfigured => SettingsService.LoadDiscordEnabled()
        && !string.IsNullOrWhiteSpace(SettingsService.LoadDiscordBotToken())
        && SettingsService.LoadDiscordGuildId() != 0;

    private static async Task SafeSendAsync(IMessageChannel channel, string text)
    {
        try { await channel.SendMessageAsync(TrimForDiscord(text, 1900)); }
        catch { /* Discord 연결/권한 실패는 앱 동작을 막지 않는다. */ }
    }

    private static string SafeDiscordName(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "project" : value.Trim().ToLowerInvariant();
        text = Regex.Replace(text, @"[^a-z0-9가-힣\-_]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(text) ? "project" : text[..Math.Min(text.Length, 90)];
    }

    private static string SafeThreadName(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "session" : value.Trim();
        return text[..Math.Min(text.Length, 90)];
    }

    private static string TrimForDiscord(string text, int max)
        => text.Length <= max ? text : text[^max..];

    /// <summary>앞에서부터 max 자 유지(답변 본문용 — 끝이 아니라 앞이 중요). 잘리면 말줄임 표시.</summary>
    private static string HeadForDiscord(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    public void Dispose() => Stop();
}
