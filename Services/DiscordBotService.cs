using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Discord.Webhook;
using DevezCode.Models;
using DevezCode.Services.Terminal;

namespace DevezCode.Services;

/// <summary>Discord Gateway bridge. Disabled unless configured in SettingsService.</summary>
public sealed class DiscordBotService : IDisposable
{
    public static DiscordBotService Instance { get; } = new();

    private readonly object _sync = new();
    // 워크스페이스 동기화/리셋 직렬화 — 재연결 OnReady 동기화와 /reset 이 겹쳐(재진입) 채널이
    // 중복 생성·삭제되며 봇이 먹통이 되는 것을 막는다.
    private readonly SemaphoreSlim _workspaceLock = new(1, 1);
    // 세션 채널별 웹훅 클라이언트 캐시 — 완료 메시지를 에이전트 이름 작성자로 보내는 데 사용.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DiscordWebhookClient> _webhooks = new();
    // 자동 시작 대기 중인 입력: sessionId → 준비되면 주입할 메시지들.
    private readonly Dictionary<string, List<string>> _pendingInput = new(StringComparer.Ordinal);
    private ObservableCollection<ProjectItem>? _projects;
    private DiscordSocketClient? _client;
    private bool _starting;
    // 꺼진 세션을 UI 스레드에서 열어달라는 요청 핸들러. MainWindow 가 주입.
    private Action<string>? _openSessionRequest;
    // 입력 대기 선택지 메뉴 자동 전송 — 터미널 화면을 주기 폴링. 메뉴 등장 1회당 1번만 보내려고 active 세션 추적.
    private System.Threading.Timer? _promptPoll;
    private readonly HashSet<string> _promptActive = new(StringComparer.Ordinal);
    // 세션별 마지막으로 보낸 선택지 메뉴 텍스트 — 버튼 클릭 시 고른 옵션 라벨을 되살려 "내 메시지"로 표시.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastMenu = new(StringComparer.Ordinal);
    // 메뉴 답변(버튼/팝업) 후 claude 응답을 기다리는 중인 세션 — 중복 폴링 방지.
    private readonly HashSet<string> _awaitingReply = new(StringComparer.Ordinal);
    // 세션별 마지막으로 디스코드에 보낸 완료 본문 — busy 경로와 폴링 경로가 같은 답을 두 번 보내는 것 방지.
    private readonly Dictionary<string, string> _lastPostedBody = new(StringComparer.Ordinal);
    // /model·/effort 선택지(메타바 콤보와 동일). 클릭 시 "/model <value>" / "/effort <value>" 주입.
    private static readonly (string label, string value)[] ModelChoices =
        { ("Opus 4.8", "opus"), ("Sonnet 4.6", "sonnet"), ("Haiku 4.5", "haiku"), ("Fable 5", "fable") };
    private static readonly (string label, string value)[] EffortChoices =
        { ("low", "low"), ("medium", "medium"), ("high", "high"), ("xhigh", "xhigh"), ("max", "max") };

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
            {
                p.Sessions.CollectionChanged -= OnSessionsChanged;
                if (IsConfigured) _ = RemoveProjectAsync(p); // 카테고리·세션 채널 삭제
            }
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

    /// <summary>세션 삭제 시 Discord 세션 채널을 삭제하고 매핑·대기 입력을 정리한다.</summary>
    private async Task RemoveSessionThreadAsync(string sessionId)
    {
        var channelId = SettingsService.LoadDiscordSessionThread(sessionId);
        lock (_sync) _pendingInput.Remove(sessionId);
        if (channelId == 0) return;
        SettingsService.SaveDiscordSessionThread(sessionId, 0); // 매핑 제거
        var client = _client;
        if (client == null) return;
        try
        {
            // 채널/스레드 무엇이든 IDeletable 로 삭제(캐시 미스 시 REST 폴백).
            IDeletable? ch = client.GetChannel(channelId) as IDeletable
                             ?? await client.Rest.GetChannelAsync(channelId) as IDeletable;
            if (ch != null) await ch.DeleteAsync();
        }
        catch { /* 권한 없음/이미 삭제됨 — 무시 */ }
    }

    /// <summary>프로젝트 삭제 시 포럼 채널(과 그 안의 글 전부)을 삭제하고 매핑을 정리한다.</summary>
    private async Task RemoveProjectAsync(ProjectItem project)
    {
        foreach (var s in project.Sessions) SettingsService.SaveDiscordSessionThread(s.Id, 0);

        var forumId = SettingsService.LoadDiscordProjectChannel(project.Path);
        SettingsService.SaveDiscordProjectChannel(project.Path, 0);
        SettingsService.SaveDiscordProjectCategory(project.Path, 0); // 레거시(카테고리) 매핑도 정리
        if (forumId == 0) return;
        var client = _client;
        if (client == null) return;
        try
        {
            if (_webhooks.TryRemove(forumId, out var wh)) { try { wh.Dispose(); } catch { } }
            IDeletable? c = client.GetChannel(forumId) as IDeletable
                            ?? await client.Rest.GetChannelAsync(forumId) as IDeletable;
            if (c != null) await c.DeleteAsync();
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
            client.ModalSubmitted += OnModalSubmitted;
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
        try { _promptPoll?.Dispose(); } catch { }
        _promptPoll = null;
        lock (_sync) _promptActive.Clear();
        foreach (var wh in _webhooks.Values) { try { wh.Dispose(); } catch { } }
        _webhooks.Clear();
        if (client == null) return;
        try { client.MessageReceived -= OnMessageReceived; } catch { }
        try { client.ButtonExecuted -= OnButtonExecuted; } catch { }
        try { client.ModalSubmitted -= OnModalSubmitted; } catch { }
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
        _promptPoll ??= new System.Threading.Timer(_ => { try { PollPrompts(); } catch { } }, null, 3000, 2000);
    }

    /// <summary>Guild 범위 슬래시 명령을 일괄 등록한다(BulkOverwrite — Ready 마다 호출해도 안전, 옛 명령 정리).</summary>
    private async Task RegisterCommandsAsync()
    {
        var client = _client;
        if (client == null) return;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return;
        try
        {
            var cmds = new ApplicationCommandProperties[]
            {
                new SlashCommandBuilder().WithName("refresh")
                    .WithDescription("세션 동기화 + 스레드 이름 갱신 + 채널 입력창 권한 적용").Build(),
                new SlashCommandBuilder().WithName("status")
                    .WithDescription("등록된 프로젝트·세션 상태를 표시합니다.").Build(),
                new SlashCommandBuilder().WithName("keys")
                    .WithDescription("세션 스레드 조작용 키 컨트롤 버튼을 표시합니다.").Build(),
                new SlashCommandBuilder().WithName("info")
                    .WithDescription("이 세션의 모델·effort·컨텍스트 사용량을 표시합니다(claude).").Build(),
                new SlashCommandBuilder().WithName("model")
                    .WithDescription("이 세션의 claude 모델을 번호로 선택합니다.").Build(),
                new SlashCommandBuilder().WithName("effort")
                    .WithDescription("이 세션의 claude effort 를 번호로 선택합니다.").Build(),
                new SlashCommandBuilder().WithName("reset")
                    .WithDescription("봇이 만든 모든 채널을 삭제하고 새로 구성합니다(되돌릴 수 없음).")
                    .AddOption("confirm", ApplicationCommandOptionType.Boolean, "정말 초기화하려면 True 를 선택", isRequired: true)
                    .Build(),
            };
            // 같은 이름이면 덮어쓰기 — Ready 마다 호출해도 안전.
            foreach (var cmd in cmds)
                await guild.CreateApplicationCommandAsync(cmd);
        }
        catch { /* 등록 실패는 앱 동작을 막지 않는다. */ }
    }

    /// <summary>슬래시 명령 처리. /reset·/status·/refresh 는 #명령어 채널 한정, /keys 는 어디서나(주로 세션 스레드).</summary>
    private async Task OnSlashCommand(SocketSlashCommand command)
    {
        var name = command.Data.Name;

        // 세션 스레드에서 쓰는 명령(keys/info/model/effort)은 #명령어 채널 제한에서 예외.
        if (name is not ("keys" or "info" or "model" or "effort"))
        {
            var cmdId = SettingsService.LoadDiscordCommandChannel();
            if (cmdId != 0 && command.ChannelId != cmdId)
            {
                try { await command.RespondAsync($"이 명령은 <#{cmdId}> 채널에서만 사용할 수 있습니다.", ephemeral: true); } catch { }
                return;
            }
        }

        switch (name)
        {
            case "refresh":
                // 채널 생성·이름변경은 rate-limit 으로 오래 걸릴 수 있어 게이트웨이를 잡지 않게 백그라운드로.
                try { await command.RespondAsync("동기화를 시작합니다… (채널 변경은 잠시 걸릴 수 있어요)", ephemeral: true); } catch { }
                _ = Task.Run(SyncWorkspaceAsync);
                break;

            case "status":
                try { await command.RespondAsync(BuildStatusText(), ephemeral: true); } catch { }
                break;

            case "info":
            {
                var sid = SettingsService.FindDiscordSessionByThread(command.ChannelId ?? 0);
                var line = string.IsNullOrWhiteSpace(sid) ? null : SessionInfoLine(sid!);
                string msg;
                if (line == null) msg = "이 세션의 상태 정보를 찾을 수 없습니다. (세션 스레드에서, claude 가 한 번 이상 응답한 뒤 사용하세요.)";
                else { var (a, b) = SplitInfo(line); msg = b.Length > 0 ? $"{a}\n{b}" : a; }
                try { await command.RespondAsync(msg, ephemeral: true); } catch { }
                break;
            }

            case "model":
            case "effort":
            {
                var sid = SettingsService.FindDiscordSessionByThread(command.ChannelId ?? 0);
                if (string.IsNullOrWhiteSpace(sid))
                {
                    try { await command.RespondAsync("세션 스레드에서 사용하세요.", ephemeral: true); } catch { }
                    break;
                }
                bool isModel = name == "model";
                var choices = isModel ? ModelChoices : EffortChoices;
                var b = new ComponentBuilder();
                for (int i = 0; i < choices.Length; i++)
                    b.WithButton($"{i + 1}. {choices[i].label}", $"dc:{name}:{choices[i].value}", ButtonStyle.Secondary, row: i / 5);
                try { await command.RespondAsync(isModel ? "모델을 선택하세요:" : "effort 를 선택하세요:", components: b.Build()); } catch { }
                break;
            }

            case "keys":
                try { await command.RespondAsync("키 컨트롤", components: BuildKeyControls()); } catch { }
                break;

            case "reset":
                var confirm = command.Data.Options.FirstOrDefault(o => o.Name == "confirm")?.Value as bool? ?? false;
                if (!confirm)
                {
                    try { await command.RespondAsync("취소됨 — 초기화하려면 `confirm` 을 True 로 실행하세요.", ephemeral: true); } catch { }
                    return;
                }
                // 즉시 ack 후 백그라운드 실행 — 채널 대량 삭제는 rate-limit 으로 오래 걸려
                // 게이트웨이 스레드를 잡으면 봇이 먹통이 된다. 완료 결과는 #명령어 채널 리포트로.
                try { await command.RespondAsync("초기화를 시작합니다… 완료되면 #명령어 채널에 결과가 표시됩니다.", ephemeral: true); } catch { }
                _ = Task.Run(ResetWorkspaceAsync);
                break;
        }
    }

    /// <summary>워크스페이스 동기화 — 다른 동기화/리셋과 겹치지 않게 직렬화한다.</summary>
    public async Task SyncWorkspaceAsync()
    {
        if (!await _workspaceLock.WaitAsync(0)) return; // 이미 동기화/리셋 진행 중 — 중복 실행 방지
        try { await SyncWorkspaceCoreAsync(); }
        finally { _workspaceLock.Release(); }
    }

    /// <summary>실제 동기화 본체(락 없음 — reset 처럼 이미 락을 잡은 경로에서 직접 호출).</summary>
    private async Task SyncWorkspaceCoreAsync()
    {
        var projects = _projects;
        if (!IsConfigured || projects == null) return;

        await EnsureCommandChannelAsync();

        foreach (var project in projects.Where(p => p.IsActive))
        {
            var forum = await EnsureProjectForumAsync(project);
            if (forum == null) continue;
            foreach (var session in project.Sessions)
                await EnsureSessionThreadAsync(project, session);
        }

        // 포럼 채널 입력창 권한 일괄 재적용(글/스레드는 부모 권한 상속).
        await RefreshChannelRestrictionsAsync();
    }

    public async Task NotifySessionDoneAsync(ProjectItem? project, SessionItem session)
    {
        if (!SettingsService.LoadDiscordNotifySessionDone()) return;
        if (project == null || !IsConfigured) return;
        // 자동 시작으로 세션을 깨우는 중(주입 전/직후)이면 resume 직후의 가짜 idle 이다 —
        // 내 메시지를 처리한 게 아니라 이전 대화의 마지막 답이므로 완료 알림을 보내지 않는다.
        lock (_sync) { if (_pendingInput.ContainsKey(session.Id)) return; }

        var thread = await EnsureSessionThreadAsync(project, session);
        if (thread == null) return;

        // 카드 본문(첫 메시지)을 방금 보낸 프롬프트로 갱신(웹훅).
        await UpdatePostStarterAsync(project, thread.Id, session);

        // 작성자(에이전트 이름·아바타)로 누구 응답인지 보이므로 "프로젝트/세션 응답 완료" 머리글은 생략.
        // 어떤 질문에 대한 답인지 구분되도록 "내 질문 + 답변"만 보낸다.
        var question = string.IsNullOrWhiteSpace(session.LastMessage)
            ? "" : $"> {HeadForDiscord(session.LastMessage, 300)}";

        // 에이전트의 최종 답변 텍스트. 추출 불가(codex/opencode 등)면 질문만.
        var agentId = string.IsNullOrWhiteSpace(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;
        var reply = AgentReplyService.TryGetLastAssistantReply(session.Id, agentId);

        string body;
        if (!string.IsNullOrWhiteSpace(reply))
            body = string.IsNullOrWhiteSpace(question) ? HeadForDiscord(reply, 1500) : $"{question}\n\n{HeadForDiscord(reply, 1500)}";
        else
            body = string.IsNullOrWhiteSpace(question) ? "응답 완료" : question;

        // 중복 방지: 같은 본문을 직전에 보냈으면(busy 경로 + 폴링 경로 동시 발동) 한 번만 보낸다.
        // dedup 키는 정보줄(아래)을 제외한 본문으로 비교 — ctx 가 미세하게 달라도 중복 전송되지 않게.
        lock (_sync)
        {
            if (_lastPostedBody.TryGetValue(session.Id, out var prev) && prev == body) return;
            _lastPostedBody[session.Id] = body;
        }

        // 답변 + (구분선) + 모델·effort·ctx / 5h·week 정보를 Components V2(Container+Separator)로 전송.
        var info = SessionInfoLine(session.Id);
        string? a = null, b = null;
        if (info != null) { var split = SplitInfo(info); a = split.a; b = split.b; }
        await SendDoneV2Async(project, thread, agentId, body, a, b);
    }

    /// <summary>완료 응답을 Components V2(Container + Separator + subtext 정보)로 전송. 작성자=에이전트(웹훅).
    /// V2 전송 실패 시 기존 텍스트(subtext 2줄) 방식으로 폴백.</summary>
    private async Task SendDoneV2Async(ProjectItem project, IMessageChannel thread, string? agentId, string body, string? infoA, string? infoB)
    {
        var forumId = SettingsService.LoadDiscordProjectChannel(project.Path);
        var hook = forumId != 0 ? await GetWebhookAsync(forumId) : null;
        if (hook != null)
        {
            try
            {
                var container = new ContainerBuilder()
                    .AddComponent(new TextDisplayBuilder().WithContent(TrimForDiscord(body, 3500)));
                if (!string.IsNullOrEmpty(infoA))
                {
                    container.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));
                    var infoText = !string.IsNullOrEmpty(infoB) ? $"-# {infoA}\n-# {infoB}" : $"-# {infoA}";
                    container.AddComponent(new TextDisplayBuilder().WithContent(infoText));
                }
                var comp = new ComponentBuilderV2().AddComponent(container).Build();
                await hook.SendMessageAsync(text: null, username: AgentDisplayName(agentId), avatarUrl: AgentAvatarUrl(agentId),
                    components: comp, flags: MessageFlags.ComponentsV2, threadId: thread.Id);
                return;
            }
            catch { /* V2/웹훅 실패 → 텍스트 폴백 */ }
        }
        var fallback = !string.IsNullOrEmpty(infoA)
            ? (!string.IsNullOrEmpty(infoB) ? $"{body}\n-# {infoA}\n-# {infoB}" : $"{body}\n-# {infoA}")
            : body;
        await SendAsAgentAsync(project, thread, agentId, fallback);
    }

    private async Task OnMessageReceived(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        var content = message.Content?.Trim() ?? "";
        if (content.Length == 0) return;

        // 명령은 모두 슬래시 명령(/refresh, /status, /keys, /reset)으로 처리한다.
        // 세션 채널의 일반 메시지만 터미널로 전달.
        if (message.Channel is not ITextChannel ch) return;
        var sessionId = SettingsService.FindDiscordSessionByThread(ch.Id);
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var session = TerminalSessionManager.Instance.Get(sessionId);
        if (session is not { IsAlive: true })
        {
            await RequestAutoStartAsync(ch, sessionId, content);
            return;
        }

        // 텍스트 입력과 Enter 를 분리해 TUI 가 붙여넣은 텍스트를 입력란에 등록한 뒤 제출하게 한다.
        // (opencode 등 alt-screen TUI 는 즉시 \r 을 보내면 텍스트가 등록되기 전에 빈 제출이 되어 메시지가 누락됨.)
        bool inline = AgentRegistry.Find(AgentIdForSession(sessionId))?.InlineTui == true;
        session.Write(content);
        await Task.Delay(inline ? 500 : 250);
        session.Write("\r");
    }

    /// <summary>alive 한 claude 세션의 터미널 화면을 폴링해, 입력 대기 선택지 메뉴가 새로 뜨면
    /// 그 화면 텍스트 + 키 컨트롤 버튼을 해당 스레드로 보낸다(메뉴 등장 1회당 1번).</summary>
    private void PollPrompts()
    {
        if (!IsConnected) return;
        var projects = _projects;
        if (projects == null) return;

        foreach (var p in projects.ToArray())
        {
            if (!p.IsActive) continue;
            foreach (var s in p.Sessions.ToArray())
            {
                var agentId = string.IsNullOrWhiteSpace(s.AgentId) ? AgentRegistry.DefaultAgentId : s.AgentId;
                if (agentId != "claude") continue; // 우선 claude 만 — 선택지 UI 패턴이 에이전트마다 달라서.

                var session = TerminalSessionManager.Instance.Get(s.Id);
                if (session is not { IsAlive: true }) { lock (_sync) _promptActive.Remove(s.Id); continue; }
                lock (_sync) { if (_pendingInput.ContainsKey(s.Id)) continue; } // 자동시작 주입 중이면 스킵

                var menu = ClaudeMenuDetector.Extract(session.GetRecentText());
                if (menu == null)
                {
                    lock (_sync) _promptActive.Remove(s.Id); // 메뉴 사라짐 → 다음 등장 시 다시 보낼 수 있게.
                    continue;
                }
                bool already; lock (_sync) already = !_promptActive.Add(s.Id);
                if (already) continue; // 이미 이번 등장에서 보냄(리페인트 중복 방지).

                var proj = p; var sess = s; var m = menu.Value;
                _ = SendPromptAsync(proj, sess, m.text, m.maxOpt, m.typeOpt);
            }
        }
    }

    /// <summary>입력 대기 선택지 메뉴를 Components V2(Container + 텍스트 + 버튼)로 전송. 작성자=Claude(웹훅).
    /// V2/웹훅 실패 시 기존 텍스트+버튼 방식으로 폴백.</summary>
    private async Task SendPromptAsync(ProjectItem project, SessionItem session, string menu, int maxOpt, int typeOpt)
    {
        try
        {
            var thread = await EnsureSessionThreadAsync(project, session);
            if (thread == null) { lock (_sync) _promptActive.Remove(session.Id); return; }
            var (question, options) = ParseMenu(ForceSpacing(menu));
            // _lastMenu 는 ChoiceText(번호→라벨)·버튼 제거 표시에 쓰는 평문 버전으로 저장.
            _lastMenu[session.Id] = string.Join("\n", options);
            var agentId = string.IsNullOrWhiteSpace(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;

            var forumId = SettingsService.LoadDiscordProjectChannel(project.Path);
            var hook = forumId != 0 ? await GetWebhookAsync(forumId) : null;
            if (hook != null)
            {
                try
                {
                    var container = BuildPromptContainer(question, options, withButtons: true, maxOpt, typeOpt);
                    var comp = new ComponentBuilderV2().AddComponent(container).Build();
                    await hook.SendMessageAsync(text: null, username: AgentDisplayName(agentId), avatarUrl: AgentAvatarUrl(agentId),
                        components: comp, flags: MessageFlags.ComponentsV2, threadId: thread.Id);
                    return;
                }
                catch { /* V2/웹훅 실패 → 폴백 */ }
            }
            var flat = (question.Length > 0 ? question + "\n" : "") + string.Join("\n", options);
            await SendAsAgentAsync(project, thread, agentId, flat, BuildKeyControls(maxOpt, typeOpt));
        }
        catch { lock (_sync) _promptActive.Remove(session.Id); }
    }

    /// <summary>메뉴 텍스트(```펜스/질문/번호옵션)를 (질문, 옵션줄 목록)으로 파싱.</summary>
    private static (string question, List<string> options) ParseMenu(string menu)
    {
        var optRe = new Regex(@"^\d+[\.\)]\s");
        var qLines = new List<string>();
        var options = new List<string>();
        bool started = false;
        foreach (var raw in menu.Replace("```", "").Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0) continue;
            if (optRe.IsMatch(l)) { options.Add(l); started = true; }
            else if (!started) qLines.Add(l);
            else if (options.Count > 0) options[^1] += " " + l; // 줄바꿈된 설명 합치기
        }
        return (CleanQuestion(string.Join(" ", qLines).Trim()), options);
    }

    /// <summary>질문에서 claude 다중질문 UI 의 탭 바 chrome("← 탭들 ✔Submit →")을 제거한다.
    /// 실제 질문은 Submit/화살표 뒤에 오므로, 마지막 Submit(또는 →) 이후만 남기고 화살표·체크를 정리.</summary>
    private static string CleanQuestion(string q)
    {
        if (string.IsNullOrEmpty(q)) return q;
        var sub = q.LastIndexOf("Submit", StringComparison.OrdinalIgnoreCase);
        if (sub >= 0) q = q[(sub + "Submit".Length)..];
        else { var arrow = q.LastIndexOf('→'); if (arrow >= 0) q = q[(arrow + 1)..]; }
        q = q.Trim(' ', '\t', '→', '←', '✔', '✓', '·', ':', '-');
        return q.Trim();
    }

    /// <summary>선택지 메뉴 V2 컨테이너: (질문), 옵션마다 Separator 로 구분, 옵션 아래에도 Separator, (옵션) 버튼.</summary>
    private static ContainerBuilder BuildPromptContainer(string question, List<string> options, bool withButtons, int maxOpt, int typeOpt)
    {
        var container = new ContainerBuilder();
        if (question.Length > 0)
            container.AddComponent(new TextDisplayBuilder().WithContent($"**{question}**"));
        foreach (var opt in options)
        {
            container.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));
            container.AddComponent(new TextDisplayBuilder().WithContent(TrimForDiscord(opt, 1000)));
        }
        if (options.Count > 0) // 마지막 옵션 아래 구분선
            container.AddComponent(new SeparatorBuilder().WithIsDivider(true).WithSpacing(SeparatorSpacingSize.Small));
        if (withButtons)
            foreach (var row in BuildKeyRows(maxOpt, typeOpt)) container.AddComponent(row);
        return container;
    }

    /// <summary>번호 선택 버튼을 ActionRow 목록으로 만든다(행당 5개). typeOpt 번은 ✏️ 직접 입력 버튼.</summary>
    private static List<ActionRowBuilder> BuildKeyRows(int maxOpt, int typeOpt)
    {
        var n = Math.Clamp(maxOpt, 1, 9);
        var rows = new List<ActionRowBuilder>();
        ActionRowBuilder? cur = null;
        for (int i = 1; i <= n; i++)
        {
            if ((i - 1) % 5 == 0) { cur = new ActionRowBuilder(); rows.Add(cur); }
            if (i == typeOpt) cur!.WithButton($"✏️ {i} 직접 입력", $"dc:type:{i}", ButtonStyle.Primary);
            else cur!.WithButton(i.ToString(), $"dc:key:{i}", ButtonStyle.Secondary);
        }
        return rows;
    }

    /// <summary>V2 선택지 메시지의 버튼을 제거(중복 선택 방지) — 메뉴 텍스트만 남긴 V2 컨테이너로 다시 그린다.</summary>
    private async Task RemoveV2ButtonsAsync(ProjectItem? project, ulong threadId, ulong messageId, string? sessionId)
    {
        if (project == null) return;
        var forumId = SettingsService.LoadDiscordProjectChannel(project.Path);
        var hook = forumId != 0 ? await GetWebhookAsync(forumId) : null;
        if (hook == null) return;
        var menu = sessionId != null && _lastMenu.TryGetValue(sessionId, out var mm) ? mm : "";
        var options = menu.Split('\n').Where(x => x.Trim().Length > 0).ToList();
        try
        {
            var container = BuildPromptContainer("", options, withButtons: false, 0, 0);
            var comp = new ComponentBuilderV2().AddComponent(container).Build();
            await hook.ModifyMessageAsync(messageId, m => m.Components = comp, threadId: threadId);
        }
        catch { /* 무시 */ }
    }

    /// <summary>alt-screen 의 열 정렬 공백이 사라져 라벨이 설명에 들러붙는 경우 강제로 공백을 넣어 가독성 보정.
    /// "N번" 라벨이 한글에 바로 붙으면(예: "4번네") 사이를 띄운다.</summary>
    private static string ForceSpacing(string menu)
        => Regex.Replace(menu, @"(\d번)(?=[가-힣])", "$1 ");

    /// <summary>세션 스레드의 키 컨트롤 버튼 클릭을 받아 해당 키스트로크를 터미널 stdin 으로 전달한다.</summary>
    private async Task OnButtonExecuted(SocketMessageComponent component)
    {
        var id = component.Data?.CustomId ?? "";

        // "직접 입력" 버튼 → 디스코드 모달(텍스트 입력 팝업)을 띄운다. (모달은 즉시 응답이어야 해 Defer 금지)
        if (id.StartsWith("dc:type:", StringComparison.Ordinal))
        {
            var optNum = id["dc:type:".Length..];
            var modal = new ModalBuilder()
                .WithTitle("직접 입력")
                .WithCustomId($"dc:typemodal:{optNum}:{component.Message.Id}")
                .AddTextInput("내용", "dc:typeinput", TextInputStyle.Paragraph, "여기에 입력하세요", required: true)
                .Build();
            try { await component.RespondWithModalAsync(modal); } catch { }
            return;
        }

        // /model·/effort 선택 버튼 → claude 에 "/model <v>" / "/effort <v>" 주입 + 설정 저장.
        if (id.StartsWith("dc:model:", StringComparison.Ordinal) || id.StartsWith("dc:effort:", StringComparison.Ordinal))
        {
            try { await component.DeferAsync(); } catch { }
            bool isModel = id.StartsWith("dc:model:", StringComparison.Ordinal);
            var value = id[(isModel ? "dc:model:".Length : "dc:effort:".Length)..];
            var choices = isModel ? ModelChoices : EffortChoices;
            var match = choices.FirstOrDefault(c => c.value == value);
            if (match.value == null) return; // 화이트리스트 외 무시
            var tid = component.Channel?.Id ?? component.ChannelId ?? 0;
            var sid = SettingsService.FindDiscordSessionByThread(tid);
            var sess = string.IsNullOrWhiteSpace(sid) ? null : TerminalSessionManager.Instance.Get(sid);
            if (sess is { IsAlive: true })
            {
                sess.Write((isModel ? "/model " : "/effort ") + value + "\r");
                if (isModel) SettingsService.SaveClaudeCodeRoomModel(sid!, value);
                else SettingsService.SaveClaudeCodeRoomEffort(sid!, value);

                // 모델 변경 시 claude 가 "Switch model?"(캐시 무효화 경고) 확인 메뉴를 띄운다 → 자동으로 1.Yes.
                // 그 확인 메뉴는 디스코드로 보내지 않게 _promptActive 에 넣어 PollPrompts 전송을 억제한다.
                if (isModel)
                {
                    lock (_sync) _promptActive.Add(sid!);
                    var sidL = sid!;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(900);
                            var s2 = TerminalSessionManager.Instance.Get(sidL);
                            if (s2 is { IsAlive: true })
                            {
                                var txt = s2.GetRecentText();
                                if (txt.IndexOf("Switch model", StringComparison.OrdinalIgnoreCase) >= 0
                                    || txt.IndexOf("switch to", StringComparison.OrdinalIgnoreCase) >= 0)
                                { s2.Write("1"); await Task.Delay(250); s2.Write("\r"); }
                            }
                        }
                        catch { }
                    });
                }
            }
            try { await component.Message.ModifyAsync(m => { m.Content = (isModel ? "✅ 모델: " : "✅ effort: ") + match.label; m.Components = new ComponentBuilder().Build(); }); } catch { }
            return;
        }

        if (!id.StartsWith("dc:key:", StringComparison.Ordinal)) return;

        // 클릭을 조용히 ack(메시지/로딩 표시 없이) — 안 하면 Discord 가 "상호작용 실패" 를 표시한다.
        try { await component.DeferAsync(); } catch { }

        var threadId = component.Channel?.Id ?? component.ChannelId ?? 0;
        if (threadId == 0) return;
        var sessionId = SettingsService.FindDiscordSessionByThread(threadId);
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var session = TerminalSessionManager.Instance.Get(sessionId);
        if (session is not { IsAlive: true }) return;

        var keyName = id["dc:key:".Length..];
        var seq = MapKey(keyName);
        if (seq == null) return;
        session.Write(seq);

        // 선택을 "내 메시지"로 표시(웹훅 임퍼스네이션) + 중복 방지로 버튼 제거(V2 메시지 다시 그리기).
        var project = ProjectForSession(sessionId);
        if (project != null && component.Channel is IMessageChannel ch && int.TryParse(keyName, out var num))
            await SendAsUserAsync(project, ch, component.User, ChoiceText(sessionId, num));
        await RemoveV2ButtonsAsync(project, threadId, component.Message.Id, sessionId);

        // 메뉴 답변 완료 알림(busy 전환이 없으므로 lastreply 폴링으로 직접 전송).
        var sItem = project?.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (project != null && sItem != null) _ = WaitAndPostReplyAsync(project, sItem);
    }

    /// <summary>세션 ID 로 소속 프로젝트를 찾는다(없으면 null).</summary>
    private ProjectItem? ProjectForSession(string? sessionId)
    {
        var projects = _projects;
        if (projects == null || string.IsNullOrEmpty(sessionId)) return null;
        foreach (var p in projects)
            foreach (var s in p.Sessions)
                if (s.Id == sessionId) return p;
        return null;
    }

    /// <summary>저장해 둔 메뉴에서 n 번 옵션 줄("n. …")을 찾아 반환. 없으면 "n번".</summary>
    private string ChoiceText(string sessionId, int n)
    {
        if (_lastMenu.TryGetValue(sessionId, out var menu))
        {
            var m = Regex.Match(menu, $@"(?m)^{n}\. (.+)$");
            if (m.Success) return $"{n}. {m.Groups[1].Value.Trim()}";
        }
        return $"{n}번";
    }

    /// <summary>클릭/입력한 사용자의 이름·아바타로(웹훅) 메시지를 보내 "내가 보낸 것"처럼 표시한다.</summary>
    private async Task SendAsUserAsync(ProjectItem project, IMessageChannel thread, IUser user, string text)
    {
        var name = (user as IGuildUser)?.Nickname ?? user.GlobalName ?? user.Username;
        var avatar = user.GetAvatarUrl(size: 128) ?? user.GetDefaultAvatarUrl();
        var forumId = SettingsService.LoadDiscordProjectChannel(project.Path);
        var hook = forumId != 0 ? await GetWebhookAsync(forumId) : null;
        if (hook != null)
        {
            try { await hook.SendMessageAsync(TrimForDiscord(text, 1900), username: name, avatarUrl: avatar, threadId: thread.Id); return; }
            catch { /* 웹훅 실패 → 봇 폴백 */ }
        }
        await SafeSendAsync(thread, $"**{name}**: {text}");
    }

    /// <summary>메뉴 답변(버튼/팝업)을 주입한 뒤 claude 응답을 기다려 완료 알림을 보낸다.
    /// AskUserQuestion 답변은 같은 턴의 연속이라 busy(true→false) 전환이 안 생겨 일반 완료 알림이
    /// 안 뜬다 → lastreply 변화를 폴링해 직접 NotifySessionDoneAsync 를 호출한다.</summary>
    private async Task WaitAndPostReplyAsync(ProjectItem project, SessionItem sessionItem)
    {
        var roomId = sessionItem.Id;
        lock (_sync) { if (!_awaitingReply.Add(roomId)) return; } // 이미 대기 중이면 스킵
        try
        {
            var agentId = string.IsNullOrWhiteSpace(sessionItem.AgentId) ? AgentRegistry.DefaultAgentId : sessionItem.AgentId;
            var baseline = AgentReplyService.TryGetLastAssistantReply(roomId, agentId) ?? "";
            for (int i = 0; i < 80; i++) // 최대 ~120초
            {
                await Task.Delay(1500);
                var sess = TerminalSessionManager.Instance.Get(roomId);
                if (sess is not { IsAlive: true }) return;
                var cur = AgentReplyService.TryGetLastAssistantReply(roomId, agentId) ?? "";
                if (!string.IsNullOrWhiteSpace(cur) && cur != baseline)
                {
                    await NotifySessionDoneAsync(project, sessionItem);
                    return;
                }
            }
        }
        catch { /* 폴링 실패는 무시 */ }
        finally { lock (_sync) _awaitingReply.Remove(roomId); }
    }

    /// <summary>"직접 입력" 모달 제출 처리 — 옵션 선택(자유입력 진입) → 입력 텍스트 → Enter 를 터미널에 주입.</summary>
    private async Task OnModalSubmitted(SocketModal modal)
    {
        var id = modal.Data?.CustomId ?? "";
        if (!id.StartsWith("dc:typemodal:", StringComparison.Ordinal)) return;

        // customId = dc:typemodal:<optNum>:<messageId>
        var parts = id.Split(':');
        int optNum = parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : 0;
        ulong msgId = parts.Length > 3 && ulong.TryParse(parts[3], out var mid) ? mid : 0;
        var text = modal.Data?.Components?.FirstOrDefault(c => c.CustomId == "dc:typeinput")?.Value ?? "";

        // 봇 메시지 없이 ack: ephemeral defer 후 그 placeholder 를 삭제한다(고지/봇응답 안 보이게).
        try { await modal.DeferAsync(ephemeral: true); } catch { }

        var threadId = modal.Channel?.Id ?? modal.ChannelId ?? 0;
        if (threadId == 0) return;
        var sessionId = SettingsService.FindDiscordSessionByThread(threadId);
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var session = TerminalSessionManager.Instance.Get(sessionId);
        if (session is not { IsAlive: true }) return;

        // 옵션 선택(자유입력 칸으로 진입) → 텍스트 → Enter. (사이에 등록 여유를 둔다)
        if (optNum > 0) { session.Write(optNum.ToString()); await Task.Delay(500); }
        if (!string.IsNullOrEmpty(text)) { session.Write(text); await Task.Delay(300); }
        session.Write("\r");

        // 입력 내용을 "내 메시지"로 표시(웹훅) + ephemeral placeholder 삭제 + 버튼 제거.
        var project = ProjectForSession(sessionId);
        if (project != null && modal.Channel is IMessageChannel uch && !string.IsNullOrEmpty(text))
            await SendAsUserAsync(project, uch, modal.User, text);
        try { await modal.DeleteOriginalResponseAsync(); } catch { }
        await RemoveV2ButtonsAsync(project, threadId, msgId, sessionId);

        // 메뉴 답변 완료 알림(busy 전환이 없으므로 lastreply 폴링으로 직접 전송).
        var sItem = project?.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (project != null && sItem != null) _ = WaitAndPostReplyAsync(project, sItem);
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
        "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9" => key, // 번호 직접 선택
        _ => null,
    };

    /// <summary>세션 스레드용 번호 선택 버튼. <paramref name="maxOpt"/> 만큼(1~9, 행당 5개). 기본 5.
    /// 번호 입력 시 대부분 TUI 가 즉시 선택하므로 방향/Enter/Esc 버튼은 두지 않는다.
    /// <paramref name="typeOpt"/> 번 옵션("Type something")은 디스코드 모달(직접 입력)로 처리한다.</summary>
    private static MessageComponent BuildKeyControls(int maxOpt = 5, int typeOpt = 0)
    {
        var n = Math.Clamp(maxOpt, 1, 9);
        var b = new ComponentBuilder();
        for (var i = 1; i <= n; i++)
        {
            if (i == typeOpt)
                b.WithButton($"✏️ {i} 직접 입력", $"dc:type:{i}", ButtonStyle.Primary, row: (i - 1) / 5);
            else
                b.WithButton(i.ToString(), $"dc:key:{i}", ButtonStyle.Secondary, row: (i - 1) / 5);
        }
        return b.Build();
    }

    /// <summary>꺼진 세션에 메시지가 오면 그 메시지를 버퍼링하고 UI 에 세션 시작을 요청한다.
    /// 세션이 준비되면 <see cref="NotifySessionReady"/> 가 버퍼를 주입한다.</summary>
    private async Task RequestAutoStartAsync(IMessageChannel thread, string sessionId, string content)
    {
        if (_openSessionRequest == null)
        {
            await SafeSendAsync(thread, "해당 세션 터미널이 실행 중이 아닙니다. DevezCode에서 세션을 먼저 열어주세요.");
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

        await SafeSendAsync(thread, "세션을 시작하는 중입니다. 준비되면 메시지를 전달합니다…");
        try { _openSessionRequest.Invoke(sessionId); } catch { /* UI 디스패치 실패는 무시 */ }

        // 안전장치: 일정 시간 내 준비되지 않으면 버퍼를 폐기하고 안내한다.
        _ = Task.Run(async () =>
        {
            await Task.Delay(45000);
            bool stillPending;
            lock (_sync) stillPending = _pendingInput.Remove(sessionId);
            if (stillPending)
                await SafeSendAsync(thread, "세션 시작이 시간 내에 완료되지 않았습니다. DevezCode에서 직접 세션을 열어주세요.");
        });
    }

    /// <summary>UI 가 세션 터미널 준비 완료를 알리면 자동 시작 대기 중이던 메시지를 순서대로 주입한다.</summary>
    public void NotifySessionReady(string sessionId)
    {
        List<string>? queued;
        lock (_sync)
        {
            // 제거하지 않고 읽기만 — 주입이 끝나 claude 가 busy 로 전환할 때까지 _pendingInput 을 유지해
            // 그 전(resume 직후)의 가짜 완료 알림을 NotifySessionDoneAsync 가 억제하게 한다.
            if (!_pendingInput.TryGetValue(sessionId, out queued)) return;
        }
        if (queued == null || queued.Count == 0)
        {
            lock (_sync) _pendingInput.Remove(sessionId);
            return;
        }

        // 인라인 TUI(gjc)는 resume 시 대화 재생·리드로우가 길어 ready 통지 시점에도 입력 준비 전일 수 있다.
        // → 주입 지연을 넉넉히 두고, 텍스트 입력과 Enter 를 분리해 TUI 가 텍스트를 등록한 뒤 제출하게 한다.
        var agentId = AgentIdForSession(sessionId) ?? AgentRegistry.DefaultAgentId;
        bool inline = AgentRegistry.Find(agentId)?.InlineTui == true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(inline ? 3000 : 2000); // TUI 가 입력란을 그릴 여유
                var session = TerminalSessionManager.Instance.Get(sessionId);
                if (session is not { IsAlive: true }) return;
                foreach (var msg in queued)
                {
                    session.Write(msg);
                    await Task.Delay(inline ? 500 : 250); // 텍스트가 입력란에 등록될 시간
                    session.Write("\r");
                    await Task.Delay(inline ? 600 : 300);
                }
                // 주입한 메시지로 busy 전환할 여유를 준 뒤 억제 해제 → 이후 진짜 완료만 알림.
                await Task.Delay(1500);
            }
            finally
            {
                lock (_sync) _pendingInput.Remove(sessionId);
            }
        });
    }

    /// <summary>sessionId 로 등록된 세션의 에이전트 ID 를 찾는다(없으면 null).</summary>
    private string? AgentIdForSession(string sessionId)
    {
        var projects = _projects;
        if (projects == null) return null;
        foreach (var p in projects)
            foreach (var s in p.Sessions)
                if (s.Id == sessionId) return string.IsNullOrWhiteSpace(s.AgentId) ? null : s.AgentId;
        return null;
    }

    /// <summary>claude statusLine 훅이 캐시한 방의 렌더된 상태줄(모델·effort·ctx·사용량)을 읽어 ANSI 제거 후 반환.
    /// (%AppData%\DevezCode\claude\statusline-cache-&lt;room&gt;-*.txt 중 최신. 없으면 null.)</summary>
    private static string? ReadStatusLine(string roomId)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "claude");
            var room = Regex.Replace(roomId, @"[^\w\-]", "");
            if (!System.IO.Directory.Exists(dir)) return null;
            var newest = new System.IO.DirectoryInfo(dir)
                .GetFiles($"statusline-cache-{room}-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return null;
            var raw = System.IO.File.ReadAllText(newest.FullName);
            // ANSI(색) 제거 + 공백 정리.
            var clean = Regex.Replace(raw, @"\x1b\[[0-9;]*m", "");
            clean = Regex.Replace(clean, @"[ \t]{2,}", " ").Trim();
            return clean.Length == 0 ? null : clean;
        }
        catch { return null; }
    }

    /// <summary>상태줄을 "week: …" 세그먼트까지만 잘라서 반환(그 뒤 reset/usage 등은 제거). 없으면 null.</summary>
    private static string? SessionInfoLine(string roomId)
    {
        var s = ReadStatusLine(roomId);
        if (s == null) return null;
        var idx = s.IndexOf("week", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) { var bar = s.IndexOf('|', idx); if (bar > idx) s = s[..bar].TrimEnd(); }
        return s;
    }

    /// <summary>상태줄을 두 줄로 나눈다: (모델·effort·ctx, 5h·week). ctx 세그먼트 기준 분할.</summary>
    private static (string a, string b) SplitInfo(string info)
    {
        var segs = info.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        int ctx = segs.FindIndex(x => x.StartsWith("ctx", StringComparison.OrdinalIgnoreCase));
        if (ctx < 0 || ctx + 1 >= segs.Count) return (string.Join(" | ", segs), "");
        return (string.Join(" | ", segs.Take(ctx + 1)), string.Join(" | ", segs.Skip(ctx + 1)));
    }

    private string BuildStatusText()
    {
        var projects = _projects;
        if (projects == null || projects.Count == 0) return "등록된 프로젝트가 없습니다.";

        var sb = new StringBuilder();
        sb.AppendLine("**DevezCode 세션 상태**");
        foreach (var project in projects.Where(p => p.IsActive))
        {
            sb.AppendLine($"\n**{project.Name}**");
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
        if (!await _workspaceLock.WaitAsync(0)) return; // 이미 동기화/리셋 진행 중 — 중복 실행 방지
        try { await ResetWorkspaceCoreAsync(); }
        finally { _workspaceLock.Release(); }
    }

    private async Task ResetWorkspaceCoreAsync()
    {
        var client = _client;
        if (client == null || !IsConfigured) return;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return;

        // 저장된 매핑 + "봇 구조 시그니처" 스윕을 합친다(그동안 채널/카테고리/포럼 구조를 오가며 생긴 고아까지).
        // 봇이 만든 객체 공통 신호: Topic = 프로젝트 경로(포럼·세션채널), 이름 = 에이전트 접두/sessions/명령어,
        // 카테고리 = 현재 프로젝트명. 매핑이 유실돼도 이 신호로 잡는다.
        var ids = new HashSet<ulong>(SettingsService.LoadAllDiscordObjectIds());
        var projectNames = new HashSet<string>(
            (_projects ?? new()).Select(p => SafeDiscordName(p.Name)), StringComparer.OrdinalIgnoreCase);

        // 1) 포럼/텍스트 채널: Topic 이 경로거나, 봇 이름 규칙이면 수거 + 부모 카테고리.
        foreach (var ch in guild.Channels)
        {
            bool hit = ch switch
            {
                IForumChannel f => LooksLikePath(f.Topic),
                ITextChannel t => LooksLikePath(t.Topic) || StartsWithAgentEmoji(t.Name)
                                   || t.Name.Equals("sessions", StringComparison.OrdinalIgnoreCase)
                                   || (t.CategoryId == null && t.Name.Equals("명령어", StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
            if (!hit) continue;
            ids.Add(ch.Id);
            if (ch is INestedChannel nc && nc.CategoryId is ulong catId) ids.Add(catId);
        }
        // 2) 프로젝트명과 일치하는 카테고리(고아 카테고리 포함).
        foreach (var cat in guild.CategoryChannels.Where(c => projectNames.Contains(c.Name)))
            ids.Add(cat.Id);
        // 3) 수거된 카테고리의 모든 자식(매핑 안 된 잔여 채널 포함).
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

        // #명령어 재생성 + #일반 정리는 EnsureCommandChannelAsync 가 처리(채팅채널 카테고리로 이동).
        await SyncWorkspaceCoreAsync(); // 이미 _workspaceLock 보유 — core 직접 호출(재획득 시 데드락)

        // 진단 리포트 — 재생성된 #명령어 채널에 결과를 남겨 무엇이 왜 실패했는지 보이게.
        // 방금 만든 채널은 게이트웨이 캐시에 아직 없을 수 있어 REST 로 폴백.
        var cmdId = SettingsService.LoadDiscordCommandChannel();
        var cmd = (IMessageChannel?)guild.GetTextChannel(cmdId)
                  ?? await client.Rest.GetChannelAsync(cmdId) as IMessageChannel;
        if (cmd != null)
        {
            var report = $"초기화 완료 — 삭제 {deleted}건, 실패 {failed}건";
            if (errors.Count > 0)
                report += "\n실패 상세(권한 문제일 가능성 높음 — 봇 역할에 `채널 관리` 권한 확인):\n" + string.Join("\n", errors);
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
            // 채널을 한꺼번에 많이 지우면 디스코드가 긴 rate-limit 을 건다. 기본 RetryMode 는 그만큼
            // '대기'하므로 reset 이 수십 분 멈춘다 → AlwaysFail 로 즉시 포기하고 다음 reset 에서 재시도.
            await target.DeleteAsync(new RequestOptions { RetryMode = RetryMode.AlwaysFail, Timeout = 8000 });
            return null;
        }
        // 10003 Unknown Channel = 부모 삭제로 이미 사라진 스레드 등 — 실패 아님.
        catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.UnknownChannel) { return ""; }
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

        // #일반 이 속한 카테고리("채팅채널"/"텍스트 채널" 등)를 삭제 전에 파악 — #명령어 를 그 안에 둔다.
        var channelId = SettingsService.LoadDiscordCommandChannel();
        var general = guild.TextChannels.FirstOrDefault(c =>
            c.Id != channelId &&
            (c.Name.Trim().Equals("일반", StringComparison.OrdinalIgnoreCase) ||
             c.Name.Trim().Equals("general", StringComparison.OrdinalIgnoreCase)));
        ulong? targetCategory = general?.CategoryId
            ?? guild.CategoryChannels.FirstOrDefault(c => IsChatCategoryName(c.Name))?.Id;

        // #명령어 보장 — 없으면 그 카테고리에 생성, 이미 있으면 그 카테고리로 이동.
        ITextChannel? cmd = channelId == 0 ? null
            : guild.GetTextChannel(channelId) ?? await client.Rest.GetChannelAsync(channelId) as ITextChannel;
        if (cmd == null)
        {
            try
            {
                var channel = await guild.CreateTextChannelAsync("명령어", props => props.CategoryId = targetCategory);
                SettingsService.SaveDiscordCommandChannel(channel.Id);
                await ApplyChannelRestrictionsAsync(guild, channel); // 초대/첨부 등 차단
                await SafeSendAsync(channel,
                    "**DevezCode 명령어 채널** (명령은 이 채널에서만 동작)\n" +
                    "- `/refresh` — 세션 동기화 + 스레드 이름 갱신 + 채널 권한 적용\n" +
                    "- `/status` — 프로젝트·세션 상태\n" +
                    "- `/reset confirm:True` — 전체 초기화(되돌릴 수 없음)\n" +
                    "- `/keys` — 키 컨트롤 버튼(세션 스레드에서 사용)\n" +
                    "세션 조작은 각 세션 스레드에서 진행하세요.");
            }
            catch { /* 권한 부족 등은 무시 */ }
        }
        else if (targetCategory != null && cmd.CategoryId != targetCategory)
        {
            try { await cmd.ModifyAsync(p => p.CategoryId = targetCategory); } catch { }
        }

        // 마지막에 #일반/#general 삭제(카테고리는 남는다).
        _ = await DeleteDefaultGeneralChannelsAsync(guild);
    }

    /// <summary>"채팅채널"/"텍스트 채널"/"text channels" 류의 기본 텍스트 카테고리 이름인지(공백·대소문자 무시).</summary>
    private static bool IsChatCategoryName(string name)
    {
        var n = name.Replace(" ", "").Trim();
        return n.Equals("채팅채널", StringComparison.OrdinalIgnoreCase)
            || n.Equals("텍스트채널", StringComparison.OrdinalIgnoreCase)
            || n.Equals("textchannels", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>디스코드가 서버 생성 시 자동으로 만드는 기본 텍스트 채널(`일반`/`general`)을 삭제한다.
    /// 카테고리에 속하지 않은 루트 채널 중 이름이 일치하는 것만 지운다(프로젝트 채널·#명령어 는 건드리지 않음).
    /// 반환: 삭제 실패 사유 목록(없으면 빈 목록).</summary>
    private static async Task<List<string>> DeleteDefaultGeneralChannelsAsync(SocketGuild guild)
    {
        var errors = new List<string>();
        var cmdId = SettingsService.LoadDiscordCommandChannel();
        // 카테고리 위치와 무관하게 이름이 일반/general 인 텍스트 채널을 지운다.
        // (봇이 만든 sessions·명령어 채널만 제외)
        var targets = guild.TextChannels.Where(c =>
            c.Id != cmdId &&
            !c.Name.Equals("sessions", StringComparison.OrdinalIgnoreCase) &&
            (c.Name.Trim().Equals("일반", StringComparison.OrdinalIgnoreCase) ||
             c.Name.Trim().Equals("general", StringComparison.OrdinalIgnoreCase))).ToList();

        if (targets.Count == 0)
            errors.Add("`#일반`/`#general` 텍스트 채널을 못 찾음(이미 없거나, 봇이 View Channel 권한 없어 안 보이거나, 이름이 다름).");

        foreach (var ch in targets)
        {
            try { await ch.DeleteAsync(); }
            catch (Exception ex) { errors.Add($"`#{ch.Name}`: {ex.Message}"); }
        }
        return errors;
    }

    /// <summary>프로젝트 = 포럼 채널 하나를 보장하고 반환한다. 세션은 이 포럼의 글(스레드)로 만든다.
    /// (DiscordProjectChannels 매핑 값 = 포럼 채널 ID.)</summary>
    public async Task<IForumChannel?> EnsureProjectForumAsync(ProjectItem project)
    {
        var client = _client;
        if (client == null || !IsConfigured) return null;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return null;

        // 캐시 미스 시 REST 폴백 — 방금 만든 채널이 게이트웨이 이벤트 도착 전이라
        // 캐시에 없으면 같은 포럼을 또 만드는 중복을 막는다.
        var forumId = SettingsService.LoadDiscordProjectChannel(project.Path);
        IForumChannel? forum = forumId == 0 ? null
            : guild.GetForumChannel(forumId) ?? await client.Rest.GetChannelAsync(forumId) as IForumChannel;
        if (forum == null)
        {
            forum = await guild.CreateForumChannelAsync(SafeDiscordName(project.Name), props => props.Topic = project.Path);
            SettingsService.SaveDiscordProjectChannel(project.Path, forum.Id);
            await ApplyChannelRestrictionsAsync(guild, forum); // 글(스레드)이 상속할 입력창 제한
        }
        return forum;
    }

    /// <summary>세션 채널 입력창의 불필요한 디스코드 네이티브 기능(파일첨부/스티커/슬래시명령/음성메시지)을
    /// @everyone 권한 오버라이드로 막는다. 이모지·GIF 선택기는 디스코드가 차단 권한을 제공하지 않아 숨길 수 없다.</summary>
    private static async Task ApplyChannelRestrictionsAsync(SocketGuild guild, IGuildChannel channel)
    {
        try
        {
            var deny = new OverwritePermissions(
                attachFiles: PermValue.Deny,
                useExternalStickers: PermValue.Deny,
                useApplicationCommands: PermValue.Deny,
                sendVoiceMessages: PermValue.Deny,
                createInstantInvite: PermValue.Deny); // 빈 채널의 "채널로 초대하기" 버튼 제거
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
            var forumId = SettingsService.LoadDiscordProjectChannel(project.Path);
            if (forumId != 0 && guild.GetForumChannel(forumId) is { } forum)
                await ApplyChannelRestrictionsAsync(guild, forum);
        }
    }

    /// <summary>세션 = 프로젝트 포럼의 글(스레드)을 보장하고 반환한다. 글은 웹훅으로 만들어 작성자=에이전트.
    /// (DiscordSessionThreads 매핑 값 = 포럼 글/스레드 ID = 시작 메시지 ID.)</summary>
    public async Task<IMessageChannel?> EnsureSessionThreadAsync(ProjectItem project, SessionItem session)
    {
        var client = _client;
        if (client == null || !IsConfigured) return null;

        var threadId = SettingsService.LoadDiscordSessionThread(session.Id);
        if (threadId != 0)
        {
            var existing = client.GetChannel(threadId) as IThreadChannel
                           ?? await client.Rest.GetChannelAsync(threadId) as IThreadChannel;
            if (existing != null)
            {
                await EnsureThreadNameAsync(existing, session);
                return existing;
            }
        }

        var forum = await EnsureProjectForumAsync(project);
        if (forum == null) return null;

        var agentId = string.IsNullOrWhiteSpace(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;
        var title = ThreadName(session);
        var body = StarterText(session); // 카드 본문 = 마지막 보낸 프롬프트
        var name = AgentDisplayName(agentId);

        ulong postId;
        var hook = await GetWebhookAsync(forum.Id);
        if (hook != null)
        {
            // 웹훅 + threadName 으로 포럼 글 생성 → 작성자=에이전트. 반환 ID = 글(스레드) ID = 시작 메시지 ID.
            var icon = OpenAgentIcon(agentId);
            if (icon is { } ic)
            {
                using var s = ic.stream;
                postId = await hook.SendFileAsync(s, ic.fileName, body, username: name, avatarUrl: AgentAvatarUrl(agentId), threadName: title);
            }
            else
            {
                postId = await hook.SendMessageAsync(body, username: name, avatarUrl: AgentAvatarUrl(agentId), threadName: title);
            }
        }
        else
        {
            var post0 = await forum.CreatePostAsync(title, ThreadArchiveDuration.OneWeek, text: body);
            postId = post0.Id;
        }
        SettingsService.SaveDiscordSessionThread(session.Id, postId);
        return client.GetChannel(postId) as IMessageChannel ?? await client.Rest.GetChannelAsync(postId) as IMessageChannel;
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

    /// <summary>채널 Topic 이 파일 경로처럼 보이는지(봇이 프로젝트 채널 Topic 에 project.Path 를 넣음).</summary>
    private static bool LooksLikePath(string? topic)
        => !string.IsNullOrEmpty(topic)
           && (Regex.IsMatch(topic, @"^[A-Za-z]:[\\/]") || topic.StartsWith("/", StringComparison.Ordinal));

    private static string SafeDiscordName(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "project" : value.Trim().ToLowerInvariant();
        text = Regex.Replace(text, @"[^a-z0-9가-힣\-_]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(text) ? "project" : text[..Math.Min(text.Length, 90)];
    }

    /// <summary>스레드 이름 앞에 붙일 에이전트별 이모지(사이드바 목록에서 아이콘처럼 보인다).</summary>
    // 디스코드 채널명은 ASCII 괄호/대괄호 ()[] 를 제거하므로, 보존되는 언더바 _ 로 구분한다.
    private static string AgentEmoji(string? agentId) => (agentId ?? "").ToLowerInvariant() switch
    {
        "claude" => "",        // 클로드는 접두 없이 이름만
        "gajae" => "gjc_",
        "" => "",
        var id => $"{id}_",    // codex→codex_, opencode→opencode_, 기타→id_
    };

    /// <summary>알려진 에이전트 접두(세션 채널명 식별용 — AgentEmoji 와 동기화 유지).
    /// claude 는 접두가 없어 제외(빈 문자열은 StartsWith 가 항상 true 라 넣으면 안 됨).</summary>
    private static readonly string[] AllAgentEmojis = { "codex_", "opencode_", "gjc_" };

    /// <summary>채널명이 봇 세션 채널 접두(에이전트 이모지)로 시작하는지 — /reset 고아 정리용.</summary>
    private static bool StartsWithAgentEmoji(string? name)
        => !string.IsNullOrEmpty(name) && AllAgentEmojis.Any(e => name!.StartsWith(e, StringComparison.Ordinal));

    /// <summary>포럼 글 제목 = 세션명만(접두 없음). 글 제목은 정규화가 없어 자유 형식.</summary>
    private static string ThreadName(SessionItem session)
    {
        var name = string.IsNullOrWhiteSpace(session.Name) ? "세션" : session.Name.Trim();
        return name.Length <= 100 ? name : name[..100];
    }

    /// <summary>포럼 글 카드 본문(첫 메시지) = 마지막 보낸 프롬프트. 없으면 안내 문구.</summary>
    private static string StarterText(SessionItem session)
        => string.IsNullOrWhiteSpace(session.LastMessage)
            ? "아직 보낸 메시지가 없습니다."
            : HeadForDiscord(session.LastMessage, 1500);

    /// <summary>완료/메시지를 보낼 때 표시할 에이전트 작성자명(웹훅 username).</summary>
    private static string AgentDisplayName(string? agentId) => (agentId ?? "").ToLowerInvariant() switch
    {
        "codex" => "Codex",
        "opencode" => "OpenCode",
        "gajae" => "Gajae",
        "claude" => "Claude",
        _ => "Agent",
    };

    // 웹훅 아바타용 공개 아이콘 URL(Devez-models 릴리즈 에셋 — 디스코드가 읽을 수 있는 공개 주소).
    private const string IconBaseUrl = "https://github.com/MrHoje/Devez-models/releases/download/agent-icons";

    /// <summary>웹훅 메시지 아바타로 쓸 에이전트 아이콘 공개 URL.</summary>
    private static string AgentAvatarUrl(string? agentId) => (agentId ?? "").ToLowerInvariant() switch
    {
        "codex" => $"{IconBaseUrl}/codex.png",
        "opencode" => $"{IconBaseUrl}/opencode_icon_white_50.png",
        "gajae" => $"{IconBaseUrl}/gajae_code.png",
        _ => $"{IconBaseUrl}/claude_code.png",
    };

    /// <summary>글 제목이 현재 규칙과 다르면 갱신한다(rate-limit 즉시 포기).</summary>
    private static async Task EnsureThreadNameAsync(IThreadChannel thread, SessionItem session)
    {
        var expected = ThreadName(session);
        if (string.Equals(thread.Name, expected, StringComparison.Ordinal)) return;
        var opts = new RequestOptions { RetryMode = RetryMode.AlwaysFail, Timeout = 5000 };
        try { await thread.ModifyAsync(p => p.Name = expected, opts); }
        catch { /* rate limit/권한 실패는 무시 — 다음 동기화 때 다시 시도 */ }
    }

    /// <summary>포럼 채널의 웹훅을 가져오거나 만든다(포럼별 1개, 캐시). threadName/threadId 로 글 생성·답글에 사용.</summary>
    private async Task<DiscordWebhookClient?> GetWebhookAsync(ulong forumId)
    {
        if (_webhooks.TryGetValue(forumId, out var cached)) return cached;
        var client = _client;
        if (client == null) return null;
        try
        {
            if (client.GetChannel(forumId) is not IIntegrationChannel ch) return null;
            var hooks = await ch.GetWebhooksAsync();
            var wh = hooks.FirstOrDefault(h => h.Name == "devez" && h.Token != null)
                     ?? await ch.CreateWebhookAsync("devez");
            var whc = new DiscordWebhookClient(wh);
            _webhooks[forumId] = whc;
            return whc;
        }
        catch { return null; }
    }

    /// <summary>세션 글(스레드)에 에이전트 작성자명으로 메시지를 보낸다(포럼 웹훅 + threadId). 실패 시 봇 폴백.</summary>
    private async Task SendAsAgentAsync(ProjectItem project, IMessageChannel thread, string? agentId, string text, MessageComponent? components = null)
    {
        var forumId = SettingsService.LoadDiscordProjectChannel(project.Path);
        var hook = forumId != 0 ? await GetWebhookAsync(forumId) : null;
        if (hook != null)
        {
            try { await hook.SendMessageAsync(TrimForDiscord(text, 1900), username: AgentDisplayName(agentId), avatarUrl: AgentAvatarUrl(agentId), components: components, threadId: thread.Id); return; }
            catch { /* 웹훅 실패(버튼 미지원 등) → 봇으로 폴백 */ }
        }
        await SafeSendAsync(thread, text, components);
    }

    /// <summary>포럼 글 첫 메시지(카드 본문)를 최신 프롬프트로 갱신(웹훅). 글 ID = 시작 메시지 ID.</summary>
    private async Task UpdatePostStarterAsync(ProjectItem project, ulong postId, SessionItem session)
    {
        var forumId = SettingsService.LoadDiscordProjectChannel(project.Path);
        var hook = forumId != 0 ? await GetWebhookAsync(forumId) : null;
        if (hook == null) return;
        try { await hook.ModifyMessageAsync(postId, m => m.Content = StarterText(session), threadId: postId); }
        catch { /* 무시 */ }
    }

    /// <summary>에이전트 아이콘 리소스를 절반 여백 캔버스로 만들어 스트림으로 연다(포럼 Gallery 썸네일용).</summary>
    private static (System.IO.Stream stream, string fileName)? OpenAgentIcon(string? agentId)
    {
        var file = (agentId ?? "").ToLowerInvariant() switch
        {
            "codex" => "codex.png",
            "opencode" => "opencode_icon_white_50.png",
            "gajae" => "gajae_code.png",
            _ => "claude_code.png",
        };
        try
        {
            var app = System.Windows.Application.Current;
            if (app == null) return null;
            System.IO.MemoryStream? result = null;
            app.Dispatcher.Invoke(() =>
            {
                var uri = new Uri($"pack://application:,,,/Resources/Images/ShellPresets/{file}", UriKind.Absolute);
                var info = System.Windows.Application.GetResourceStream(uri);
                if (info == null) return;
                using var src = info.Stream;
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(src,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                // 원본 해상도가 제각각이라(opencode=50px, claude/gjc=큰 이미지) 본문 표시 크기가 다르다.
                // 아이콘을 고정 크기(50px)로 맞추고 2배 캔버스(100px)에 가운데 배치 → 모든 에이전트 동일 크기.
                const int target = 50, canvas = target * 2;
                double scale = target / (double)Math.Max(frame.PixelWidth, frame.PixelHeight);
                double w = frame.PixelWidth * scale, h = frame.PixelHeight * scale;
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(frame, new System.Windows.Rect((canvas - w) / 2.0, (canvas - h) / 2.0, w, h));
                }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(canvas, canvas, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                var ms = new System.IO.MemoryStream();
                enc.Save(ms);
                ms.Position = 0;
                result = ms;
            });
            return result == null ? null : (result, file);
        }
        catch { return null; }
    }

    private static string TrimForDiscord(string text, int max)
        => text.Length <= max ? text : text[^max..];

    /// <summary>앞에서부터 max 자 유지(답변 본문용 — 끝이 아니라 앞이 중요). 잘리면 말줄임 표시.</summary>
    private static string HeadForDiscord(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    public void Dispose() => Stop();
}
