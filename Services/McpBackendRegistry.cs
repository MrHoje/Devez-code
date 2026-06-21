using System.Collections.Generic;
using System.Linq;

namespace DevezCode.Services;

/// <summary>사용 가능한 MCP 백엔드(opencode / Claude / Codex) 레지스트리.
/// UI 가 이걸 enumerate 해서 탭을 만든다. 설치 안 된 에이전트는 IsAvailable=false 로 비활성 탭.</summary>
public static class McpBackendRegistry
{
    public static readonly IReadOnlyList<IMcpBackend> All = new IMcpBackend[]
    {
        new OpenCodeMcpBackend(),
        new ClaudeMcpBackend(),
        new CodexMcpBackend(),
    };

    public static IMcpBackend? Get(string id) => All.FirstOrDefault(b =>
        string.Equals(b.Id, id, System.StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<IMcpBackend> Available => All.Where(b => b.IsAvailable);
}
