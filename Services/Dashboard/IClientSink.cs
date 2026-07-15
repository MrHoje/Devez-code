namespace DevezCode.Services.Dashboard;

/// <summary>
/// 대시보드의 논리 클라이언트(브라우저) 1명. 전송(LAN 실소켓 / 릴레이 봉투)에 무관하게
/// DashboardHub 가 이 인터페이스 위에서만 동작한다.
/// </summary>
public interface IClientSink
{
    Guid Id { get; }
    // snapshot 직후 live output 순서를 원자화하는 lock 및 구독 상태.
    object Sync { get; }
    string? SelectedRoomId { get; set; }
    long LastOutputSequence { get; set; }
    void Queue(object message);
}
