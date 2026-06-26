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
    // 워크스페이스 동기화/리셋 직렬화 — 재연결 OnReady 동기화와 /reset 이 겹쳐(재진입) 채널이
    // 중복 생성·삭제되며 봇이 먹통이 되는 것을 막는다.
    private readonly SemaphoreSlim _workspaceLock = new(1, 1);
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

        // /keys 외 관리 명령은 #명령어 채널에서만 받는다(채널이 아직 없으면 부트스트랩 허용).
        if (name != "keys")
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
                try { await command.DeferAsync(ephemeral: true); } catch { }
                await SyncWorkspaceAsync();
                try { await command.FollowupAsync("✅ 새로고침 완료 — 동기화·이름 갱신·채널 권한 적용.", ephemeral: true); } catch { }
                break;

            case "status":
                try { await command.RespondAsync(BuildStatusText(), ephemeral: true); } catch { }
                break;

            case "keys":
                try { await command.RespondAsync("⌨️ 키 컨트롤", components: BuildKeyControls()); } catch { }
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
                try { await command.RespondAsync("♻️ 초기화를 시작합니다… 완료되면 #명령어 채널에 결과가 표시됩니다.", ephemeral: true); } catch { }
                _ = Task.Run(ResetWorkspaceAsync);
                break;
        }
    }

    /// <summary>워크스페이스 동기화 — 다른 동기화/리셋과 겹치지 않게 직렬화한다.</summary>
    public async Task SyncWorkspaceAsync()
    {
        await _workspaceLock.WaitAsync();
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
            var category = await EnsureProjectCategoryAsync(project);
            if (category == null) continue;
            foreach (var session in project.Sessions)
                await EnsureSessionThreadAsync(project, session);
        }

        // 기존 세션 채널은 EnsureSessionThreadAsync 가 조기 반환하므로 권한이 안 걸린다 — 여기서 일괄 재적용.
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
    private async Task RequestAutoStartAsync(IMessageChannel thread, string sessionId, string content)
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
            // 제거하지 않고 읽기만 — 주입이 끝나 claude 가 busy 로 전환할 때까지 _pendingInput 을 유지해
            // 그 전(resume 직후)의 가짜 완료 알림을 NotifySessionDoneAsync 가 억제하게 한다.
            if (!_pendingInput.TryGetValue(sessionId, out queued)) return;
        }
        if (queued == null || queued.Count == 0)
        {
            lock (_sync) _pendingInput.Remove(sessionId);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
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
                // claude 가 주입된 메시지로 busy 전환할 여유를 준 뒤 억제 해제 → 이후 진짜 완료만 알림.
                await Task.Delay(1500);
            }
            finally
            {
                lock (_sync) _pendingInput.Remove(sessionId);
            }
        });
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
        await _workspaceLock.WaitAsync();
        try { await ResetWorkspaceCoreAsync(); }
        finally { _workspaceLock.Release(); }
    }

    private async Task ResetWorkspaceCoreAsync()
    {
        var client = _client;
        if (client == null || !IsConfigured) return;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return;

        // 저장된 매핑 + "봇 구조 시그니처" 스윕을 합친다.
        // 이전 reset 이 매핑만 비우고 삭제에 실패했으면 고아 채널이 남는데, 매핑엔 없으므로
        // 시그니처(프로젝트 채널=이름 "sessions", 명령어 채널=루트 "명령어")로 길드를 직접 훑어 잡는다.
        var ids = new HashSet<ulong>(SettingsService.LoadAllDiscordObjectIds());

        // 1) 봇이 만든 세션 채널(이름이 에이전트 이모지로 시작) + 그 부모 카테고리.
        //    매핑이 유실된 고아도 이 시그니처로 잡는다(카테고리가 잡히면 3)에서 자식 전부 수거).
        foreach (var ch in guild.TextChannels.Where(c => StartsWithAgentEmoji(c.Name)))
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

        // #명령어 재생성 + #일반 정리는 EnsureCommandChannelAsync 가 처리(채팅채널 카테고리로 이동).
        await SyncWorkspaceCoreAsync(); // 이미 _workspaceLock 보유 — core 직접 호출(재획득 시 데드락)

        // 진단 리포트 — 재생성된 #명령어 채널에 결과를 남겨 무엇이 왜 실패했는지 보이게.
        // 방금 만든 채널은 게이트웨이 캐시에 아직 없을 수 있어 REST 로 폴백.
        var cmdId = SettingsService.LoadDiscordCommandChannel();
        var cmd = (IMessageChannel?)guild.GetTextChannel(cmdId)
                  ?? await client.Rest.GetChannelAsync(cmdId) as IMessageChannel;
        if (cmd != null)
        {
            var report = $"♻️ 초기화 완료 — 삭제 {deleted}건, 실패 {failed}건";
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
                    "🛠️ **DevezCode 명령어 채널** (명령은 이 채널에서만 동작)\n" +
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
            errors.Add("ℹ️ `#일반`/`#general` 텍스트 채널을 못 찾음(이미 없거나, 봇이 View Channel 권한 없어 안 보이거나, 이름이 다름).");

        foreach (var ch in targets)
        {
            try { await ch.DeleteAsync(); }
            catch (Exception ex) { errors.Add($"`#{ch.Name}`: {ex.Message}"); }
        }
        return errors;
    }

    /// <summary>프로젝트 카테고리를 보장하고 반환한다. 세션은 이 카테고리 바로 아래의 텍스트 채널로 만든다
    /// (중간 sessions 채널 없이 프로젝트 → 세션 채널 구조).</summary>
    public async Task<ICategoryChannel?> EnsureProjectCategoryAsync(ProjectItem project)
    {
        var client = _client;
        if (client == null || !IsConfigured) return null;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return null;

        // 캐시 미스 시 REST 폴백 — 방금 만든 카테고리가 게이트웨이 이벤트 도착 전이라
        // 캐시에 없으면 세션마다 같은 카테고리를 또 만드는 중복을 막는다.
        var categoryId = SettingsService.LoadDiscordProjectCategory(project.Path);
        ICategoryChannel? category = categoryId == 0 ? null
            : guild.GetCategoryChannel(categoryId) ?? await client.Rest.GetChannelAsync(categoryId) as ICategoryChannel;
        if (category == null)
        {
            category = await guild.CreateCategoryChannelAsync(SafeDiscordName(project.Name));
            SettingsService.SaveDiscordProjectCategory(project.Path, category.Id);
        }
        return category;
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
            foreach (var session in project.Sessions)
            {
                var channelId = SettingsService.LoadDiscordSessionThread(session.Id);
                if (channelId != 0 && guild.GetTextChannel(channelId) is { } channel)
                    await ApplyChannelRestrictionsAsync(guild, channel);
            }
    }

    /// <summary>세션을 프로젝트 카테고리 바로 아래의 텍스트 채널로 보장하고 반환한다.
    /// (이전엔 sessions 채널 아래 스레드였으나, 프로젝트 → 세션 채널 구조로 변경됨.
    /// 메서드명/매핑 키는 호환을 위해 유지 — DiscordSessionThreads 의 값이 이제 채널 ID 다.)</summary>
    public async Task<IMessageChannel?> EnsureSessionThreadAsync(ProjectItem project, SessionItem session)
    {
        var client = _client;
        if (client == null || !IsConfigured) return null;
        var guild = client.GetGuild(SettingsService.LoadDiscordGuildId());
        if (guild == null) return null;

        var channelId = SettingsService.LoadDiscordSessionThread(session.Id);
        if (channelId != 0)
        {
            // 캐시 미스 시 REST 폴백 — 방금 만든 채널을 못 찾아 중복 생성하는 것 방지.
            var existing = client.GetChannel(channelId) as ITextChannel
                           ?? await client.Rest.GetChannelAsync(channelId) as ITextChannel;
            if (existing != null)
            {
                await EnsureChannelNameAsync(existing, session);
                return existing;
            }
        }

        var category = await EnsureProjectCategoryAsync(project);
        if (category == null) return null;

        var ch = await guild.CreateTextChannelAsync(SessionChannelName(session), props =>
        {
            props.CategoryId = category.Id;
            props.Topic = $"{session.AgentId} · {session.Name}";
        });
        SettingsService.SaveDiscordSessionThread(session.Id, ch.Id);
        await ApplyChannelRestrictionsAsync(guild, ch);
        // 새 채널이 비어 보이지 않게 transcript 의 최근 대화를 먼저 채운다(claude/gajae 만 가능).
        // 연결 안내·키 버튼은 자동으로 띄우지 않는다(필요 시 /keys 로 호출).
        await PostRecentConversationAsync(ch, session);
        return ch;
    }

    /// <summary>세션 채널을 처음 만들 때 transcript 의 최근 대화(최대 10개 메시지 ≈ 5턴)를 시간순으로 게시한다.
    /// claude/gajae 만 transcript 접근 가능 — 그 외 에이전트는 게시할 게 없어 조용히 넘어간다.</summary>
    private static async Task PostRecentConversationAsync(IMessageChannel channel, SessionItem session)
    {
        var agentId = string.IsNullOrWhiteSpace(session.AgentId) ? AgentRegistry.DefaultAgentId : session.AgentId;
        var convo = AgentReplyService.GetRecentConversation(session.Id, agentId, 10);
        if (convo.Count == 0) return;

        await SafeSendAsync(channel, "🗂️ **이전 대화** (최근 일부)");
        foreach (var (role, text) in convo)
        {
            var body = role == "user"
                ? $"🧑 **나**\n> {HeadForDiscord(text, 1500)}"
                : $"🤖 **{agentId}**\n{HeadForDiscord(text, 1800)}";
            await SafeSendAsync(channel, body);
        }
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

    /// <summary>기존 스레드 이름이 현재 에이전트 이모지 규칙과 다르면 갱신한다.
    /// Discord 는 스레드 rename 을 10분당 2회로 제한하므로 다를 때만 호출한다.</summary>
    /// <summary>세션 채널 이름 = 에이전트 이모지 + 안전화한 세션명. (Discord 채널명은 소문자/하이픈으로 정규화됨.)</summary>
    private static string SessionChannelName(SessionItem session)
        => $"{AgentEmoji(session.AgentId)}{SafeDiscordName(session.Name)}";

    private static async Task EnsureChannelNameAsync(ITextChannel channel, SessionItem session)
    {
        var expected = SessionChannelName(session);
        if (string.Equals(channel.Name, expected, StringComparison.Ordinal)) return;
        try { await channel.ModifyAsync(p => p.Name = expected); }
        catch { /* rate limit(채널 rename 은 빡셈)/권한 실패는 무시 */ }
    }

    private static string TrimForDiscord(string text, int max)
        => text.Length <= max ? text : text[^max..];

    /// <summary>앞에서부터 max 자 유지(답변 본문용 — 끝이 아니라 앞이 중요). 잘리면 말줄임 표시.</summary>
    private static string HeadForDiscord(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    public void Dispose() => Stop();
}
