using System.Collections.Generic;
using System.Threading.Tasks;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>에이전트별 MCP 백엔드 공통 인터페이스. 각 에이전트(opencode/Claude/codex)는
/// 자기 고유 설정 파일 포맷을 갖지만 UI 에서는 이 인터페이스로만 접근해 백엔드 교체만으로 동작.</summary>
public interface IMcpBackend
{
    /// <summary>UI 탭에 표시될 ID ("opencode" / "claude" / "codex"). 영문 소문자.</summary>
    string Id { get; }

    /// <summary>탭에 표시될 한글 이름.</summary>
    string DisplayName { get; }

    /// <summary>에이전트 CLI 가 PATH 에 있는지 등 — 미설치면 탭을 비활성화.</summary>
    bool IsAvailable { get; }

    /// <summary>헤더 우측에 표시할 설정 파일 경로 (짧은 형태, 예: "opencode.json").</summary>
    string ConfigPathHint { get; }

    /// <summary>디스크에서 서버 목록을 읽어온다. 파일 없거나 섹션 없으면 빈 리스트.</summary>
    List<McpServer> Load();

    /// <summary>서버 목록을 디스크에 쓴다. 다른 필드(plugin/model 등) 는 보존.</summary>
    void Save(IEnumerable<McpServer> servers);

    /// <summary><c>&lt;agent&gt; mcp list</c> 같은 CLI 호출로 각 서버의 라이브 상태를 채운다.</summary>
    Task RefreshStatusAsync(IEnumerable<McpServer> servers);
}
