namespace DevezCode.Services.Dashboard;

/// <summary>전송 방식(LAN WebSocket/Cloudflare 릴레이)과 무관한 논리 브라우저 연결.</summary>
public interface IClientSink
{
    Guid Id { get; }
    object Sync { get; }
    string? SelectedRoomId { get; set; }
    long LastOutputSequence { get; set; }
    void Queue(object message);
}
