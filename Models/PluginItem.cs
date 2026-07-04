using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DevezCode.Models;

/// <summary>에이전트 공통 플러그인 항목(claude/opencode/gajae 통합).
/// 각 에이전트가 지원하는 동작은 Can* 플래그로 UI 에서 게이팅한다.
/// - claude/gjc: 토글·상세·업데이트·삭제 + 설치/마켓플레이스
/// - opencode: 추가·삭제만(네이티브 on/off 없음)</summary>
public sealed class PluginItem : INotifyPropertyChanged
{
    public string AgentId { get; set; } = "";   // claude / opencode / gajae
    /// <summary>CLI 조작에 쓰는 식별자. claude: name@marketplace, gjc: 이름, opencode: 모듈명 또는 파일경로.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>표시용 출처 칩(예: 마켓플레이스명 / npm / 폴더).</summary>
    public string Source { get; set; } = "";
    public string Version { get; set; } = "";
    public string Meta { get; set; } = "";       // 추가 표시(예: 갱신일, MCP 개수)

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } }
    }

    // ── 동작 지원 여부(에이전트별) ──
    public bool CanToggle { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDetails { get; set; }
    /// <summary>opencode 삭제 방식: "npm"(config 배열) / "file"(plugin 폴더 파일).</summary>
    public string RemoveKind { get; set; } = "";
    /// <summary>DevezCode 자체 연동 플러그인 등 — 제거 차단.</summary>
    public bool IsProtected { get; set; }

    public string VersionText => string.IsNullOrWhiteSpace(Version) || Version == "unknown"
        ? "" : "v" + Version;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
