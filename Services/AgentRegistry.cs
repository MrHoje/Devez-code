using System.IO;

namespace DevezCode.Services;

/// <summary>에이전트 정의. id·표시명·제공자·PATH 감지 이름·실행 명령·훅 지원 여부.</summary>
public sealed class AgentDef
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    /// <summary>제공자(Anthropic / OpenAI / OpenCode 등). 피커 다이얼로그의 부제목으로 표시.</summary>
    public string Provider { get; init; } = "";
    /// <summary>PATH 스캔 시 검사할 실행 파일 이름 변형. (.exe/.cmd/.ps1/리눅스 등)</summary>
    public string[] ExeNames { get; init; } = Array.Empty<string>();
    /// <summary>프로젝트 디렉터리에서 띄울 기본 커맨드. (인자 없이; Claude 는 세션 ID·훅 등 별도 처리)</summary>
    public string Command { get; init; } = "";
    /// <summary>설정 UI에 보여줄 설치 명령(Windows 기준 권장 한 줄). 복사 버튼 대상.</summary>
    public string InstallCommand { get; init; } = "";
    /// <summary>최신 버전으로 갱신하는 명령(PowerShell 한 줄). 가능하면 각 CLI 자체 업데이터 사용.
    /// 이미 최신이면 no-op 이어야 함(앱 시작 시 매번 호출됨). 빈 값이면 자동 업데이트 대상에서 제외.</summary>
    public string UpdateCommand { get; init; } = "";
    /// <summary>Claude 만 — SessionStart/UserPromptSubmit 훅으로 busy 스피너·lastmsg 헤더 지원.</summary>
    public bool SupportsHooks { get; init; }
    /// <summary>alt-screen(풀스크린 TUI) 대신 인라인으로 렌더하는 에이전트(gjc 등).
    /// alt-screen 시퀀스가 없어 로딩 오버레이가 첫 출력 기준으로 해제돼야 함(무한 스피너 방지).</summary>
    public bool InlineTui { get; init; }
    /// <summary>기존 세션 재오픈 시 같이 넘기는 플래그. codex="--last", opencode="-c".
    /// Claude 는 resume/session-id 를 별도 처리하므로 null.</summary>
    public string? ResumeFlag { get; init; }
    /// <summary>준비 중 안내 문구. 비어 있지 않으면 설정 &gt; 에이전트 목록에 그대로 표시되고,
    /// 활성화 토글이 잠긴다(공개 전 기능 — 연속 클릭으로만 열린다). 첫 실행 기본값에서도 제외된다.</summary>
    public string PreviewNote { get; init; } = "";

    public override string ToString() => DisplayName;
}

/// <summary>사용 가능한 에이전트 목록 + PATH 감지. 결과는 프로세스 수명 동안 캐시.</summary>
public static class AgentRegistry
{
    public const string DefaultAgentId = "claude";

    public static readonly IReadOnlyList<AgentDef> All = new AgentDef[]
    {
        new()
        {
            Id = "claude", DisplayName = "Claude Code", Provider = "Anthropic",
            ExeNames = new[] { "claude.exe", "claude.cmd", "claude.bat", "claude.ps1", "claude" },
            Command = "claude",
            InstallCommand = "irm https://claude.ai/install.ps1 | iex",
            UpdateCommand = "claude update",
            SupportsHooks = true,
        },
        new()
        {
            Id = "codex", DisplayName = "Codex", Provider = "OpenAI",
            ExeNames = new[] { "codex.exe", "codex.cmd", "codex.bat", "codex.ps1", "codex" },
            Command = "codex",
            // 스탠드얼론 install.ps1 은 PS 5.1 에서 OSArchitecture 속성 오류로 실패(openai/codex#19559).
            // Windows 실사용은 npm 경로가 안정적(공식 대안). Node.js 필요.
            InstallCommand = "npm install -g @openai/codex",
            UpdateCommand = "npm install -g @openai/codex@latest",
            ResumeFlag = "--last",
            SupportsHooks = true, // ~/.codex/hooks.json 으로 lastmsg/busy/session_id 추적 (Claude 정합)
            InlineTui = true,     // codex 는 alt-screen(?1049h) 미사용 인라인 TUI — ?2026h 프레임 마커로
                                  // 로딩 오버레이를 즉시 dismiss(안 하면 6초 폴백까지 스피너가 안 꺼짐).
        },
        new()
        {
            // Grok Build CLI (xAI). 기본 설치: %USERPROFILE%\.grok\bin\grok.exe
            // Tier1: ResumeFlag -c (cwd 최근 세션). 방별 정확 복원은 Tier2 전용 런치에서 -r <id>.
            // 기본 TUI 는 alt-screen — InlineTui=false. 테마는 xterm 스킴 + (선택) 유저 프롬프트 recolor.
            Id = "grok", DisplayName = "Grok", Provider = "xAI",
            ExeNames = new[] { "grok.exe", "grok.cmd", "grok.bat", "grok.ps1", "grok" },
            Command = "grok",
            InstallCommand = "irm https://x.ai/cli/install.ps1 | iex",
            UpdateCommand = "grok update",
            ResumeFlag = "-c",
        },
        new()
        {
            // Google Antigravity CLI(agy). 기본 설치: %LOCALAPPDATA%\agy\bin\agy.exe (백그라운드 자체 업데이트).
            // 세션 사전 발급 플래그가 없어 ResumeFlag 단순 경로 미사용 — 방별 정확 복원은
            // TryBuildAntigravityDirectLaunch 가 cwd→conversation 매핑(last_conversations.json) 추종 +
            // `agy --conversation <id>` 로 처리. 훅(hooks.json: SessionStart/Stop 등)으로 busy/lastmsg 추적.
            Id = "antigravity", DisplayName = "Antigravity", Provider = "Google",
            ExeNames = new[] { "agy.exe", "agy.cmd", "agy.bat", "agy.ps1", "agy" },
            Command = "agy",
            InstallCommand = "irm https://antigravity.google/cli/install.ps1 | iex",
            UpdateCommand = "", // 자체 업데이트 — 앱 시작 시 갱신 명령 불필요
            SupportsHooks = true,
        },
        new()
        {
            // Kimi Code CLI(Moonshot AI, @moonshot-ai/kimi-code). 홈 = %USERPROFILE%\.kimi-code.
            // 훅 기반 추적: config.toml [[hooks]](SessionStart/UserPromptSubmit/Stop/PermissionRequest 등).
            // 훅 runner 는 spawn(shell:true, windowsHide:true) → Windows=cmd.exe, 콘솔창 안 뜸, env 상속.
            // 세션 복원은 TryBuildKimiDirectLaunch 가 방별 -S <sessionId>(전역 session_index.jsonl, cwd 무관) 로 처리.
            // (cwd 공유 시 -c 가 방끼리 섞이므로 ResumeFlag 단순 경로는 Tier1 폴백 전용.)
            // alt-screen(?1049) 미사용 인라인 TUI → InlineTui=true 로 로딩 오버레이 첫 출력에 해제.
            Id = "kimi", DisplayName = "Kimi", Provider = "Moonshot AI",
            ExeNames = new[] { "kimi.cmd", "kimi.ps1", "kimi.exe", "kimi" },
            Command = "kimi",
            InstallCommand = "npm install -g @moonshot-ai/kimi-code",
            UpdateCommand = "kimi upgrade",
            ResumeFlag = "-c",
            InlineTui = true,
        },
        new()
        {
            Id = "opencode", DisplayName = "OpenCode", Provider = "OpenCode",
            ExeNames = new[] { "opencode.exe", "opencode.cmd", "opencode.bat", "opencode.ps1", "opencode" },
            Command = "opencode",
            InstallCommand = "npm install -g opencode-ai",
            UpdateCommand = "opencode upgrade",
            ResumeFlag = "-c",
        },
        new()
        {
            // 가재코드: standalone CLI(`gjc`). 방별 JSONL 증분 폴링으로 상태를 추적한다.
            // 세션 복원은 TryBuildGajaeDirectLaunch 가 방별 --session-dir + 파일명 ID 추출 → `gjc -r <id>` 로 처리.
            // (cwd 공유 시 -c 가 방끼리 섞이므로 ResumeFlag 단순 경로 미사용.)
            // 인라인 렌더(alt-screen 미사용) → InlineTui=true 로 로딩 오버레이 첫 출력에 해제.
            Id = "gajae", DisplayName = "Gajae Code", Provider = "Gajae",
            ExeNames = new[] { "gjc.exe", "gjc.cmd", "gjc.bat", "gjc.ps1", "gjc" },
            Command = "gjc",
            InstallCommand = "bun install -g gajae-code",
            UpdateCommand = "bun install -g gajae-code@latest",
            InlineTui = true,
        },
        new()
        {
            // 하단 터미널 패널 전용 pseudo-agent — 에이전트 미연결 일반 셸(pwsh 우선, powershell 폴백).
            // HiddenFromUI 로 피커/설정 비노출. 실제 커맨드는 TerminalSessionManager 의 shell 분기가 조립.
            Id = "shell", DisplayName = "터미널", Provider = "Shell",
            ExeNames = new[] { "pwsh.exe", "powershell.exe" },
            Command = "pwsh",
            InlineTui = true, // alt-screen 없음 — 로딩 오버레이를 첫 출력 기준으로 해제
        },
        new()
        {
            // Devez Vibe(dvz) — 공식 codex app-server 를 쓰는 자체 TUI. 세션 실체는 codex thread 라
            // rollout(.codex\sessions)·MCP·인증을 codex 와 공유하고, 화면/입력만 dvz 가 소유한다.
            // 세션 ID 사전 발급 플래그가 없어(thread/start 가 발급) 방별 정확 복원은
            // TryBuildDevezVibeDirectLaunch 가 dvz 가 직접 기록한 sessions\<room>.txt → `dvz -r <id>` 로 처리.
            // 상태(busy/waiting/lastmsg)도 dvz 가 %APPDATA%\DevezCode\devezvibe\ 에 직접 쓴다(훅 없음).
            // 테마는 --theme minimal|soft|dark 로 DevezCode 3테마와 1:1. alt-screen TUI → InlineTui=false.
            // npm 전역 설치로 배포하며, 설치 후 실행 커맨드는 그대로 `dvz`.
            Id = "devezvibe", DisplayName = "Devez Vibe", Provider = "Devez",
            ExeNames = new[] { "dvz.exe", "dvz.cmd", "dvz.bat", "dvz.ps1", "dvz" },
            Command = "dvz",
            InstallCommand = "npm install -g devez-vibe",
            // `dvz update` 를 쓰지 않는다 — 그 자체 업데이터는 (사용자가 직접 dvz 를 실행 중일 때 exe 가 잠기는
            // 것을 피하려고) 새 콘솔 창을 띄워 npm 을 위임하고 즉시 exit 0 한다. 우리 경로는 dvz 가 아니라
            // PowerShell(CreateNoWindow) 이 실행 주체라 잠금이 없는데도 창이 뜨고(pause 로 남기까지 한다),
            // 설치 완료 전에 리턴해 before/after 버전 비교가 항상 '최신' 오판정 + 로그·복구(B/C) 무력화됐다.
            // npm 을 직접 호출해 codex/gajae 와 동일하게 조용히·동기적으로 처리한다.
            UpdateCommand = "npm install -g devez-vibe@latest",
            ResumeFlag = "-c",  // Tier1 폴백 전용(전용 분기는 -r <id>)
        },
    };

    /// <summary>개명 이전 ID → 현재 ID. 저장소(workspace.json 의 <c>Agent</c>, settings.json 의 RoomAgents)에는
    /// 옛 값이 그대로 남아 있고, 정규화하지 않으면 <see cref="Find"/> 가 null 을 돌려줘 세션이 조용히
    /// 기본 에이전트(claude)로 떨어진다(아이콘·실행 커맨드까지 claude 가 된다).
    /// 에이전트를 또 개명하면 여기에 한 줄 추가하는 것이 전부여야 한다.</summary>
    private static readonly Dictionary<string, string> RenamedIds =
        new(StringComparer.OrdinalIgnoreCase) { ["devezcli"] = "devezvibe" };

    /// <summary>저장된 에이전트 ID 를 현재 ID 로 정규화. 빈 값/미개명 값은 그대로 돌려준다.</summary>
    public static string NormalizeId(string? id)
        => id != null && RenamedIds.TryGetValue(id, out var current) ? current : id ?? "";

    public static AgentDef? Find(string? id)
        => id != null ? All.FirstOrDefault(a => a.Id.Equals(NormalizeId(id), StringComparison.OrdinalIgnoreCase)) : null;

    public static AgentDef GetDefault() => Find(DefaultAgentId) ?? All[0];

    private static readonly Dictionary<string, string?> _resolvedPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>에이전트 실행 파일의 PATH 첫 매치 경로. 못 찾으면 null.</summary>
    public static string? ResolvePath(AgentDef agent)
    {
        lock (_lock)
        {
            if (_resolvedPath.TryGetValue(agent.Id, out var cached)) return cached;
            string? found = null;
            try
            {
                var dirs = new List<string>();
                try { dirs.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); }
                catch { }
                try { dirs.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); }
                catch { }
                try { dirs.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); }
                catch { }
                // grok 기본 설치 경로 (PATH 미등록 환경 대비)
                if (agent.Id.Equals("grok", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        dirs.Insert(0, Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "bin"));
                    }
                    catch { }
                }
                // agy 기본 설치 경로 (PATH 미등록 환경 대비)
                if (agent.Id.Equals("antigravity", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        dirs.Insert(0, Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin"));
                    }
                    catch { }
                }
                foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var name in agent.ExeNames)
                    {
                        try
                        {
                            var full = Path.Combine(dir, name);
                            if (File.Exists(full)) { found = full; break; }
                        }
                        catch { /* 잘못된 경로 무시 */ }
                    }
                    if (found != null) break;
                }
            }
            catch { }
            _resolvedPath[agent.Id] = found;
            return found;
        }
    }

    /// <summary>에이전트가 PATH 어딘가에 설치되어 있는지.</summary>
    public static bool IsInstalled(AgentDef agent) => ResolvePath(agent) != null;

    /// <summary>설정에서 활성화 + 실제 설치된 + UI 노출 대상 에이전트 (세션 추가 피커용).</summary>
    public static IReadOnlyList<AgentDef> GetEnabledAndInstalled()
    {
        var enabled = new HashSet<string>(SettingsService.LoadEnabledAgents(), StringComparer.OrdinalIgnoreCase);
        return All.Where(a => !HiddenFromUI.Contains(a.Id) && IsInstalled(a) && enabled.Contains(a.Id)).ToList();
    }

    /// <summary>설정이 바뀐 뒤 캐시 무효화 (설치 감지 재실행).</summary>
    public static void InvalidateCache()
    {
        lock (_lock) _resolvedPath.Clear();
    }

    /// <summary>UI 에서 숨길 에이전트 ID. 세션 생성 피커·설정 다이얼로그에서 제외.
    /// 백엔드 코드(훅·런치 등)는 그대로 유지 — 나중에 다시 노출할 때 여기만 비우면 됨.</summary>
    public static readonly HashSet<string> HiddenFromUI = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell", // 하단 터미널 패널 전용 — 세션 피커/설정에 노출하지 않음
    };

    /// <summary>UI 노출 대상에서 제외한 에이전트만 반환.</summary>
    public static IReadOnlyList<AgentDef> GetVisibleAgents()
        => All.Where(a => !HiddenFromUI.Contains(a.Id)).ToList();
}
