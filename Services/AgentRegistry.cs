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
    /// <summary>Claude 만 — SessionStart/UserPromptSubmit 훅으로 busy 스피너·lastmsg 헤더 지원.</summary>
    public bool SupportsHooks { get; init; }
    /// <summary>기존 세션 재오픈 시 같이 넘기는 플래그. codex="--last", opencode="-c".
    /// Claude 는 resume/session-id 를 별도 처리하므로 null.</summary>
    public string? ResumeFlag { get; init; }

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
            Command = "claude", SupportsHooks = true,
        },
        new()
        {
            Id = "codex", DisplayName = "Codex", Provider = "OpenAI",
            ExeNames = new[] { "codex.exe", "codex.cmd", "codex.bat", "codex.ps1", "codex" },
            Command = "codex",
            ResumeFlag = "--last",
            SupportsHooks = true, // ~/.codex/hooks.json 으로 lastmsg/busy/session_id 추적 (Claude 정합)
        },
        new()
        {
            Id = "opencode", DisplayName = "OpenCode", Provider = "OpenCode",
            ExeNames = new[] { "opencode.exe", "opencode.cmd", "opencode.bat", "opencode.ps1", "opencode" },
            Command = "opencode",
            ResumeFlag = "-c",
        },
        new()
        {
            // 가재코드: standalone CLI(`gjc`). bun install -g gajae-code. 훅/resume 미지원.
            Id = "gajae", DisplayName = "Gajae Code", Provider = "Gajae",
            ExeNames = new[] { "gjc.exe", "gjc.cmd", "gjc.bat", "gjc.ps1", "gjc" },
            Command = "gjc",
        },
    };

    public static AgentDef? Find(string? id)
        => id != null ? All.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) : null;

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
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    foreach (var name in agent.ExeNames)
                    {
                        try
                        {
                            var full = Path.Combine(dir.Trim(), name);
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
    /// 백엔드 코드(codex 훅·MCP 등)는 그대로 유지 — 기존 codex 세션이 있어도 동작은 계속.</summary>
    public static readonly HashSet<string> HiddenFromUI = new(StringComparer.OrdinalIgnoreCase) { "codex" };

    /// <summary>UI 노출 대상에서 제외한 에이전트만 반환.</summary>
    public static IReadOnlyList<AgentDef> GetVisibleAgents()
        => All.Where(a => !HiddenFromUI.Contains(a.Id)).ToList();
}
