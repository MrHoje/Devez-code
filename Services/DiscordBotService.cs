using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

    public void SetProjects(ObservableCollection<ProjectItem> projects)
    {
        // 기존 구독 해제(재호출 대비) → 새 컬렉션 구독.
        if (_projects != null)
        {
            _projects.CollectionChanged -= OnProjectsChanged;
            foreach (var p in _projects) p.Sessions.CollectionChanged -= OnSessionsChanged;
        }
        _projects = projects;
        projects.CollectionChanged += OnProjectsChanged;
        foreach (var p in projects)
        {
            p.Sessions.CollectionChanged -= OnSessionsChanged;
            p.Sessions.CollectionChanged += OnSessionsChanged;
        }
    }

    /// <summary>프로젝트 추가/삭제 — 추가된 프로젝트의 세션 변경을 구독하고, 삭제된 프로젝트는 해제.</summary>
    private void OnProjectsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (ProjectItem p in e.NewItems)
            {
                p.Sessions.CollectionChanged -= OnSessionsChanged;
                p.Sessions.CollectionChanged += OnSessionsChanged;
                if (IsConfigured && p.IsActive)
                    foreach (var s in p.Sessions) _ = EnsureSessionThreadAsync(p, s);
            }
        if (e.OldItems != null)
            foreach (ProjectItem p in e.OldItems)
                p.Sessions.CollectionChanged -= OnSessionsChanged;
    }

    /// <summary>세션 추가 → 스레드 생성, 세션 삭제 → 스레드 삭제·매핑 정리.
    /// client 가 아직 없으면 EnsureSessionThreadAsync 가 무시하고, OnReady 가 일괄 생성한다.</summary>
    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // IsConnected 는 재연결(Resumed) 후 false 로 남을 수 있어 가드로 부적합 —
        // 실제 생성은 EnsureSessionThreadAsync 가 client==null 로 보호한다.
        if (!IsConfigured) return;
        var project = _projects?.FirstOrDefault(p => ReferenceEquals(p.Sessions, sender));

        if (e.NewItems != null && project is { IsActive: true })
            foreach (SessionItem s in e.NewItems) _ = EnsureSessionThreadAsync(project, s);

        if (e.OldItems != null)
            foreach (SessionItem s in e.OldItems) _ = RemoveSessionThreadAsync(s.Id);
    }

    /// <summary>세션 삭제 시 Discord 스레드를 삭제하고 매핑·대기 입력을 정리한다.</summary>
    private async Task RemoveSessionThreadAsync(string sessionId)
    {
        var threadId = SettingsService.LoadDiscordSessionThread(sessionId);
        lock (_sync) _pendingInput.Remove(sessionId);
        if (threadId == 0) return;
        SettingsService.SaveDiscordSessionThread(sessionId, 0); // 매핑 제거
        var client = _client;
        if (client == null) return;
        try
        {
            if (client.GetChannel(threadId) is IThreadChannel thread)
                await thread.DeleteAsync();
        }
        catch { /* 권한 없음/이미 삭제됨 — 무시 */ }
    }

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
            client.ButtonExecuted += OnButtonExecuted;
            client.SlashCommandExecuted += OnSlashCommand;
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
        try { client.ButtonExecuted -= OnButtonExecuted; } catch { }
        try { client.SlashCommandExecuted -= OnSlashCommand; } catch { }
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
        await RegisterCommandsAsync();
        await SyncWorkspaceAsync();
    }

    /// <summary>Guild 범위 슬래시 명령을 등록한다(같은 이름이면 덮어쓰기 — Ready 마다 호출해도 안전).</summary>
    private async Task RegisterCommandsAsync()
    {
        var client = _client;
        if (client == null) return;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return;
        try
        {
            var refresh = new SlashCommandBuilder()
                .WithName("refresh")
                .WithDescription("세션 스레드를 동기화하고 이름을 최신 상태로 갱신합니다.")
                .Build();
            await guild.CreateApplicationCommandAsync(refresh);
        }
        catch { /* 등록 실패는 앱 동작을 막지 않는다. */ }
    }

    /// <summary>슬래시 명령 처리. 현재는 /refresh(동기화 + 스레드 이름 갱신)만 지원.</summary>
    private async Task OnSlashCommand(SocketSlashCommand command)
    {
        if (!command.Data.Name.Equals("refresh", StringComparison.Ordinal)) return;
        // 동기화에 시간이 걸릴 수 있으니 먼저 ack(나에게만 보이는 ephemeral).
        try { await command.DeferAsync(ephemeral: true); } catch { }
        await SyncWorkspaceAsync();
        try { await command.FollowupAsync("✅ 새로고침 완료 — 세션 스레드 동기화·이름 갱신 및 채널 입력창 권한 적용.", ephemeral: true); } catch { }
    }

    public async Task SyncWorkspaceAsync()
    {
        var projects = _projects;
        if (!IsConfigured || projects == null) return;

        await EnsureCommandChannelAsync();

        foreach (var project in projects.Where(p => p.IsActive))
        {
            var channel = await EnsureProjectChannelAsync(project);
            if (channel == null) continue;
            foreach (var session in project.Sessions)
                await EnsureSessionThreadAsync(project, session);
        }

        // 기존 채널은 EnsureProjectChannelAsync 가 조기 반환하므로 권한이 안 걸린다 — 여기서 일괄 재적용.
        await RefreshChannelRestrictionsAsync();
    }

    public async Task NotifySessionDoneAsync(ProjectItem? project, SessionItem session)
    {
        if (!SettingsService.LoadDiscordNotifySessionDone()) return;
        if (project == null || !IsConfigured) return;

        var thread = await EnsureSessionThreadAsync(project, session);
        if (thread == null) return;

        var title = string.IsNullOrWhiteSpace(project.Name) ? "프로젝트" : project.Name;
        var sess = string.IsNullOrWhiteSpace(session.Name) ? "세션" : session.Name;

        // 어떤 질문에 대한 답인지 구분되도록 "내 질문 + 답변"을 함께 보낸다.
        // (질문은 PC/Discord 어디서 보냈든 busy 훅이 기록한 마지막 프롬프트.)
        var question = string.IsNullOrWhiteSpace(session.LastMessage)
            ? "" : $"\n> {HeadForDiscord(session.LastMessage, 300)}";

        // 에이전트의 최종 답변 텍스트. 추출 불가(codex/opencode 등)면 질문만.
        var agentId = string.IsNullOrWhiteSpace(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;
        var reply = AgentReplyService.TryGetLastAssistantReply(session.Id, agentId);

        var body = string.IsNullOrWhiteSpace(reply)
            ? $"✅ **{title} / {sess}** 응답 완료{question}"
            : $"✅ **{title} / {sess}** 응답 완료{question}\n\n{HeadForDiscord(reply, 1500)}";
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

    /// <summary>세션 스레드의 키 컨트롤 버튼 클릭을 받아 해당 키스트로크를 터미널 stdin 으로 전달한다.</summary>
    private async Task OnButtonExecuted(SocketMessageComponent component)
    {
        var id = component.Data?.CustomId ?? "";
        if (!id.StartsWith("dc:key:", StringComparison.Ordinal)) return;

        // 클릭을 조용히 ack(메시지/로딩 표시 없이) — 안 하면 Discord 가 "상호작용 실패" 를 표시한다.
        try { await component.DeferAsync(); } catch { }

        var threadId = component.Channel?.Id ?? component.ChannelId ?? 0;
        if (threadId == 0) return;
        var sessionId = SettingsService.FindDiscordSessionByThread(threadId);
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var session = TerminalSessionManager.Instance.Get(sessionId);
        if (session is not { IsAlive: true }) return;

        var seq = MapKey(id["dc:key:".Length..]);
        if (seq == null) return;
        session.Write(seq);
    }

    /// <summary>버튼 customId 의 키 이름을 터미널이 이해하는 입력 바이트열로 변환한다.</summary>
    private static string? MapKey(string key) => key switch
    {
        "up" => "\x1b[A",
        "down" => "\x1b[B",
        "left" => "\x1b[D",
        "right" => "\x1b[C",
        "enter" => "\r",
        "esc" => "\x1b",
        "1" or "2" or "3" or "4" or "5" => key, // 번호 직접 선택(대부분 TUI 는 숫자 입력 즉시 선택)
        _ => null,
    };

    /// <summary>세션 스레드용 키 컨트롤(방향/선택/취소/번호) 버튼 메시지 컴포넌트.</summary>
    private static MessageComponent BuildKeyControls()
    {
        var b = new ComponentBuilder();
        b.WithButton("위", "dc:key:up", ButtonStyle.Secondary, new Emoji("⬆️"), row: 0);
        b.WithButton("아래", "dc:key:down", ButtonStyle.Secondary, new Emoji("⬇️"), row: 0);
        b.WithButton("선택(Enter)", "dc:key:enter", ButtonStyle.Success, row: 0);
        b.WithButton("취소(Esc)", "dc:key:esc", ButtonStyle.Danger, row: 0);
        for (var i = 1; i <= 5; i++)
            b.WithButton(i.ToString(), $"dc:key:{i}", ButtonStyle.Secondary, row: 1);
        return b.Build();
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
        if (content.Equals("!dc status", StringComparison.OrdinalIgnoreCase) ||
            content.Equals("!dc list", StringComparison.OrdinalIgnoreCase))
        {
            await SafeSendAsync(message.Channel, BuildStatusText());
            return;
        }

        if (content.Equals("!dc keys", StringComparison.OrdinalIgnoreCase))
        {
            await SafeSendAsync(message.Channel, "⌨️ 키 컨트롤", BuildKeyControls());
            return;
        }

        if (content.Equals("!dc reset", StringComparison.OrdinalIgnoreCase))
        {
            await SafeSendAsync(message.Channel, "⚠️ 봇이 만든 **모든 채널·카테고리·세션 스레드**를 삭제하고 새로 구성합니다. 되돌릴 수 없습니다.\n진행하려면 `!dc reset confirm` 을 입력하세요.");
            return;
        }

        if (content.Equals("!dc reset confirm", StringComparison.OrdinalIgnoreCase))
        {
            await ResetWorkspaceAsync();
            return; // 재구성된 #명령어 채널에 안내가 다시 올라온다.
        }

        await SafeSendAsync(message.Channel, "사용법: `/refresh`(동기화+이름 갱신+권한 적용), `!dc status`, `!dc list`, `!dc keys`, `!dc reset`(전체 초기화)\n세션 스레드에 일반 메시지를 보내면 해당 터미널로 전달됩니다. 선택지 메뉴는 키 컨트롤 버튼(`!dc keys`)으로 조작하세요.");
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

    /// <summary>봇이 만든 모든 채널·카테고리·세션 스레드를 삭제하고 매핑을 비운 뒤 새로 구성한다(!dc reset confirm).
    /// 개발 중 깨끗한 상태에서 다시 테스트하기 위한 용도.</summary>
    private async Task ResetWorkspaceAsync()
    {
        var client = _client;
        if (client == null || !IsConfigured) return;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return;

        // 저장된 매핑 + "봇 구조 시그니처" 스윕을 합친다.
        // 이전 reset 이 매핑만 비우고 삭제에 실패했으면 고아 채널이 남는데, 매핑엔 없으므로
        // 시그니처(프로젝트 채널=이름 "sessions", 명령어 채널=루트 "명령어")로 길드를 직접 훑어 잡는다.
        var ids = new HashSet<ulong>(SettingsService.LoadAllDiscordObjectIds());

        // 1) 프로젝트 채널(이름 "sessions") + 그 부모 카테고리
        foreach (var ch in guild.TextChannels.Where(c => c.Name.Equals("sessions", StringComparison.OrdinalIgnoreCase)))
        {
            ids.Add(ch.Id);
            if (ch.CategoryId is ulong catId) ids.Add(catId);
        }
        // 2) 루트 명령어 채널(이름 "명령어")
        foreach (var ch in guild.TextChannels.Where(c => c.CategoryId == null && c.Name.Equals("명령어", StringComparison.OrdinalIgnoreCase)))
            ids.Add(ch.Id);
        // 3) 위에서 모인 카테고리의 모든 자식(매핑 안 된 잔여 채널 포함)
        foreach (var cat in guild.CategoryChannels.Where(c => ids.Contains(c.Id)))
            foreach (var child in cat.Channels)
                ids.Add(child.Id);

        SettingsService.ClearDiscordWorkspace(); // 수집 후 매핑 비우기 — 재구성이 새로 만들도록

        int deleted = 0, failed = 0;
        var errors = new List<string>();
        foreach (var id in ids)
        {
            var err = await TryDeleteChannelAsync(client, guild, id);
            if (err == null) deleted++;
            else if (err.Length > 0) { failed++; if (errors.Count < 8) errors.Add(err); }
            // err == "" : 캐시·REST 모두 없음(이미 삭제됨) → 성공/실패 어디에도 안 셈
        }

        // #일반/#general 기본 채널 정리 — 실패 사유를 직접 받아 리포트에 합친다.
        var genErrors = await DeleteDefaultGeneralChannelsAsync(guild);

        await SyncWorkspaceAsync(); // #명령어 + 프로젝트 채널/스레드 재생성

        // 진단 리포트 — 재생성된 #명령어 채널에 결과를 남겨 무엇이 왜 실패했는지 보이게.
        // 방금 만든 채널은 게이트웨이 캐시에 아직 없을 수 있어 REST 로 폴백.
        var cmdId = SettingsService.LoadDiscordCommandChannel();
        var cmd = (IMessageChannel?)guild.GetTextChannel(cmdId)
                  ?? await client.Rest.GetChannelAsync(cmdId) as IMessageChannel;
        if (cmd != null)
        {
            var report = $"♻️ 초기화 완료 — 삭제 {deleted}건, 실패 {failed}건";
            errors.AddRange(genErrors);
            if (errors.Count > 0)
                report += "\n⚠️ 실패 상세(권한 문제일 가능성 높음 — 봇 역할에 `채널 관리` 권한 확인):\n" + string.Join("\n", errors);
            await SafeSendAsync(cmd, report);
        }
    }

    /// <summary>채널/스레드/카테고리를 캐시→REST 순으로 찾아 삭제한다.
    /// 반환: null=삭제 성공, ""=대상 없음(이미 삭제), 그 외=실패 사유 메시지.</summary>
    private static async Task<string?> TryDeleteChannelAsync(DiscordSocketClient client, SocketGuild guild, ulong id)
    {
        try
        {
            IDeletable? target = guild.GetChannel(id) as IDeletable
                                 ?? client.GetChannel(id) as IDeletable
                                 ?? await client.Rest.GetChannelAsync(id) as IDeletable;
            if (target == null) return ""; // 어디에도 없음 — 이미 삭제됐다고 간주
            await target.DeleteAsync();
            return null;
        }
        catch (Exception ex) { return $"`{id}`: {ex.Message}"; }
    }

    /// <summary>명령어 입력용 서버 루트 채널 `#명령어`를 보장한다(디스코드 기본 `#일반` 대체).
    /// 카테고리에 속하지 않은 최상단 텍스트 채널로 만들고, 사용법 안내를 한 번 남긴다.</summary>
    public async Task EnsureCommandChannelAsync()
    {
        var client = _client;
        if (client == null || !IsConfigured) return;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return;

        _ = await DeleteDefaultGeneralChannelsAsync(guild);

        var channelId = SettingsService.LoadDiscordCommandChannel();
        if (channelId != 0 && guild.GetTextChannel(channelId) != null) return;

        try
        {
            var channel = await guild.CreateTextChannelAsync("명령어", props => props.CategoryId = null);
            SettingsService.SaveDiscordCommandChannel(channel.Id);
            await SafeSendAsync(channel,
                "🛠️ **DevezCode 명령어 채널**\n" +
                "- `/refresh` — 세션 동기화 + 스레드 이름 갱신 + 채널 권한 적용\n" +
                "- `!dc status` / `!dc list` — 세션 상태\n" +
                "- `!dc keys` — 키 컨트롤 버튼\n" +
                "세션 조작은 각 세션 스레드에서 진행하세요.");
        }
        catch { /* 권한 부족 등은 무시 */ }
    }

    /// <summary>디스코드가 서버 생성 시 자동으로 만드는 기본 텍스트 채널(`일반`/`general`)을 삭제한다.
    /// 카테고리에 속하지 않은 루트 채널 중 이름이 일치하는 것만 지운다(프로젝트 채널·#명령어 는 건드리지 않음).
    /// 반환: 삭제 실패 사유 목록(없으면 빈 목록).</summary>
    private static async Task<List<string>> DeleteDefaultGeneralChannelsAsync(SocketGuild guild)
    {
        var errors = new List<string>();
        var cmdId = SettingsService.LoadDiscordCommandChannel();
        foreach (var ch in guild.TextChannels.Where(c =>
                     c.CategoryId == null && c.Id != cmdId &&
                     (c.Name.Equals("일반", StringComparison.OrdinalIgnoreCase) ||
                      c.Name.Equals("general", StringComparison.OrdinalIgnoreCase))).ToList())
        {
            try { await ch.DeleteAsync(); }
            catch (Exception ex) { errors.Add($"`#{ch.Name}`: {ex.Message}"); }
        }
        return errors;
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
        await ApplyChannelRestrictionsAsync(guild, channel);
        return channel;
    }

    /// <summary>세션 채널 입력창의 불필요한 디스코드 네이티브 기능(파일첨부/스티커/슬래시명령/음성메시지)을
    /// @everyone 권한 오버라이드로 막는다. 이모지·GIF 선택기는 디스코드가 차단 권한을 제공하지 않아 숨길 수 없다.</summary>
    private static async Task ApplyChannelRestrictionsAsync(SocketGuild guild, ITextChannel channel)
    {
        try
        {
            var deny = new OverwritePermissions(
                attachFiles: PermValue.Deny,
                useExternalStickers: PermValue.Deny,
                useApplicationCommands: PermValue.Deny,
                sendVoiceMessages: PermValue.Deny);
            await channel.AddPermissionOverwriteAsync(guild.EveryoneRole, deny);
        }
        catch { /* 권한 부족 등은 무시 — 메시징 자체는 계속 동작 */ }
    }

    /// <summary>이미 생성된 모든 활성 프로젝트 채널에 입력창 기능 차단 권한을 다시 적용한다(!dc refresh).</summary>
    private async Task RefreshChannelRestrictionsAsync()
    {
        var client = _client;
        var projects = _projects;
        if (client == null || projects == null || !IsConfigured) return;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return;

        foreach (var project in projects.Where(p => p.IsActive))
        {
            var channelId = SettingsService.LoadDiscordProjectChannel(project.Path);
            if (channelId == 0) continue;
            if (guild.GetTextChannel(channelId) is { } channel)
                await ApplyChannelRestrictionsAsync(guild, channel);
        }
    }

    public async Task<IMessageChannel?> EnsureSessionThreadAsync(ProjectItem project, SessionItem session)
    {
        var client = _client;
        if (client == null || !IsConfigured) return null;

        var threadId = SettingsService.LoadDiscordSessionThread(session.Id);
        if (threadId != 0 && client.GetChannel(threadId) is IMessageChannel existing)
        {
            if (existing is SocketThreadChannel st) await EnsureThreadNameAsync(st, session);
            return existing;
        }

        var channel = await EnsureProjectChannelAsync(project);
        if (channel == null) return null;

        var threadName = $"{AgentEmoji(session.AgentId)} {SafeThreadName(session.Name)}".Trim();
        var thread = await channel.CreateThreadAsync(threadName, ThreadType.PublicThread, ThreadArchiveDuration.OneDay);
        SettingsService.SaveDiscordSessionThread(session.Id, thread.Id);
        await SafeSendAsync(thread,
            $"🔗 DevezCode 세션 연결: `{session.Name}` / `{session.AgentId}`\n선택지 메뉴는 아래 버튼으로 조작하세요. (버튼이 사라지면 `!dc keys`)",
            BuildKeyControls());
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

    private static async Task SafeSendAsync(IMessageChannel channel, string text, MessageComponent? components)
    {
        try { await channel.SendMessageAsync(TrimForDiscord(text, 1900), components: components); }
        catch { /* Discord 연결/권한 실패는 앱 동작을 막지 않는다. */ }
    }

    private static string SafeDiscordName(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "project" : value.Trim().ToLowerInvariant();
        text = Regex.Replace(text, @"[^a-z0-9가-힣\-_]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(text) ? "project" : text[..Math.Min(text.Length, 90)];
    }

    /// <summary>스레드 이름 앞에 붙일 에이전트별 이모지(사이드바 목록에서 아이콘처럼 보인다).</summary>
    private static string AgentEmoji(string? agentId) => (agentId ?? "").ToLowerInvariant() switch
    {
        "claude" => "✳️",
        "codex" => "🟢",
        "opencode" => "🔷",
        "gajae" => "🦞",
        _ => "💠",
    };

    /// <summary>기존 스레드 이름이 현재 에이전트 이모지 규칙과 다르면 갱신한다.
    /// Discord 는 스레드 rename 을 10분당 2회로 제한하므로 다를 때만 호출한다.</summary>
    private static async Task EnsureThreadNameAsync(SocketThreadChannel thread, SessionItem session)
    {
        var expected = $"{AgentEmoji(session.AgentId)} {SafeThreadName(session.Name)}".Trim();
        if (string.Equals(thread.Name, expected, StringComparison.Ordinal)) return;
        try { await thread.ModifyAsync(p => p.Name = expected); }
        catch { /* rate limit/권한 실패는 무시 */ }
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
