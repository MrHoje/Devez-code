namespace DevezCode.Services.Terminal;

/// <summary>
/// TerminalHostView가 에이전트/테마별 색상 보정을 끝낸 실제 표시 바이트를 공유한다.
/// 웹 대시보드는 이 스트림을 받아 데스크톱 xterm과 같은 결과를 그린다.
/// </summary>
public static class TerminalDisplayOutputHub
{
    private const int ReplayCap = 2 * 1024 * 1024;
    private static readonly object _lock = new();
    private static readonly Dictionary<string, ReplayBuffer> _replays = new(StringComparer.Ordinal);

    public static event Action<string, long, byte[]>? OutputReceived;
    public static event Action<string, int, int>? SizeChanged;

    internal static void Publish(string roomId, byte[] data)
    {
        long sequence;
        lock (_lock)
        {
            if (!_replays.TryGetValue(roomId, out var replay)) _replays[roomId] = replay = new ReplayBuffer();
            sequence = replay.Append(data);
        }
        OutputReceived?.Invoke(roomId, sequence, data);
    }

    internal static void PublishSize(string roomId, int cols, int rows) => SizeChanged?.Invoke(roomId, cols, rows);

    public static TerminalDisplaySnapshot GetReplaySnapshot(string roomId)
    {
        lock (_lock) return _replays.TryGetValue(roomId, out var replay)
            ? new TerminalDisplaySnapshot(replay.Snapshot(), replay.Sequence)
            : new TerminalDisplaySnapshot(Array.Empty<byte>(), 0);
    }

    public static void Remove(string roomId)
    {
        lock (_lock) _replays.Remove(roomId);
    }

    private sealed class ReplayBuffer
    {
        private readonly Queue<byte[]> _chunks = new();
        private int _size;
        public long Sequence { get; private set; }

        public long Append(byte[] data)
        {
            var copy = data.Length <= ReplayCap ? data.ToArray() : data[^ReplayCap..];
            _chunks.Enqueue(copy);
            _size += copy.Length;
            while (_size > ReplayCap && _chunks.Count > 1) _size -= _chunks.Dequeue().Length;
            return ++Sequence;
        }

        public byte[] Snapshot()
        {
            var result = new byte[_size];
            int offset = 0;
            foreach (var chunk in _chunks)
            {
                Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
            return result;
        }
    }
}

public readonly record struct TerminalDisplaySnapshot(byte[] Data, long Sequence);

