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
    private readonly Dictionary<string, StringBuilder> _outputBuffers = new();
    private readonly HashSet<string> _flushScheduled = new(StringComparer.Ordinal);
    private ObservableCollection<ProjectItem>? _projects;
    private DiscordSocketClient? _client;
    private bool _starting;

    public bool IsConnected { get; private set; }

    private DiscordBotService() { }

    public void SetProjects(ObservableCollection<ProjectItem> projects) => _projects = projects;

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
            _outputBuffers.Clear();
            _flushScheduled.Clear();
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
        var last = string.IsNullOrWhiteSpace(session.LastMessage) ? "" : $"\n> {TrimForDiscord(session.LastMessage, 500)}";
        await SafeSendAsync(thread, $"✅ **{title} / {sess}** 응답 완료{last}");
    }

    public void ForwardTerminalOutput(string sessionId, byte[] bytes)
    {
        if (!IsConfigured || bytes.Length == 0) return;
        if (SettingsService.LoadDiscordSessionThread(sessionId) == 0) return;

        var text = Encoding.UTF8.GetString(bytes);
        text = StripAnsi(text);
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_sync)
        {
            if (!_outputBuffers.TryGetValue(sessionId, out var sb))
            {
                sb = new StringBuilder();
                _outputBuffers[sessionId] = sb;
            }
            sb.Append(text);
            if (_flushScheduled.Contains(sessionId)) return;
            _flushScheduled.Add(sessionId);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            await FlushOutputAsync(sessionId);
        });
    }

    private async Task FlushOutputAsync(string sessionId)
    {
        string text;
        lock (_sync)
        {
            _flushScheduled.Remove(sessionId);
            if (!_outputBuffers.TryGetValue(sessionId, out var sb) || sb.Length == 0) return;
            text = sb.ToString();
            sb.Clear();
        }

        text = TrimForDiscord(text.Trim(), 1800);
        if (string.IsNullOrWhiteSpace(text)) return;
        var channel = await GetThreadChannelAsync(sessionId);
        if (channel == null) return;
        await SafeSendAsync(channel, $"```\n{text}\n```");
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
            await SafeSendAsync(thread, "⚠️ 해당 세션 터미널이 실행 중이 아닙니다. DevezCode에서 세션을 먼저 열어주세요.");
            return;
        }

        session.Write(content);
        session.Write("\r");
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

    private Task<IMessageChannel?> GetThreadChannelAsync(string sessionId)
    {
        var client = _client;
        if (client == null) return Task.FromResult<IMessageChannel?>(null);
        var threadId = SettingsService.LoadDiscordSessionThread(sessionId);
        if (threadId == 0) return Task.FromResult<IMessageChannel?>(null);
        return Task.FromResult(client.GetChannel(threadId) as IMessageChannel);
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

    private static string StripAnsi(string text)
        => Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");

    private static string TrimForDiscord(string text, int max)
        => text.Length <= max ? text : text[^max..];

    public void Dispose() => Stop();
}
