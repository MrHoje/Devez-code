namespace DevezCode.Models;

/// <summary>서브에이전트 상태 항목. Claude Code SubagentStart/Stop 훅으로 수집.
/// 방별 agentId 당 하나, 시작·종료 시 파일이 생성/갱신된다.</summary>
public sealed class SubagentStatusItem : NotifyBase
{
    public string RoomId { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string AgentType { get; init; } = "";

    private string _status = "running"; // running / completed / error / cancelled
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string Prompt { get; init; } = "";

    private DateTime _startedAt;
    public DateTime StartedAt
    {
        get => _startedAt;
        set => Set(ref _startedAt, value);
    }

    private DateTime? _endedAt;
    public DateTime? EndedAt
    {
        get => _endedAt;
        set => Set(ref _endedAt, value);
    }

    private int _toolCallCount;
    public int ToolCallCount
    {
        get => _toolCallCount;
        set => Set(ref _toolCallCount, value);
    }

    private string _reason = "";
    public string Reason
    {
        get => _reason;
        set => Set(ref _reason, value);
    }

    /// <summary>종료 여부 (running 이외 모든 상태). UI 에서 running / completed 그룹 분류용.</summary>
    public bool IsFinished => Status is "completed" or "error" or "cancelled";
}
