namespace DevezCode.Services;

/// <summary>
/// 최신 스냅샷만 유지하며 한 작업자에서 순서대로 저장한다. 호출 스레드는 디스크 flush 를 기다리지 않고,
/// 정상 종료나 즉시 성공 확인이 필요한 경로만 <see cref="Flush"/>로 지정 버전까지 기다린다.
/// </summary>
internal sealed class CoalescingSaveQueue<T>
{
    private readonly object _gate = new();
    private readonly Func<T, bool> _write;
    private T? _pending;
    private bool _hasPending;
    private bool _workerRunning;
    private Task? _worker;
    private long _enqueuedVersion;
    private long _writtenVersion;
    private long _failedVersion;

    public CoalescingSaveQueue(Func<T, bool> write)
        => _write = write ?? throw new ArgumentNullException(nameof(write));

    public long Enqueue(T value)
    {
        lock (_gate)
        {
            _pending = value;
            _hasPending = true;
            var version = ++_enqueuedVersion;
            EnsureWorkerLocked();
            return version;
        }
    }

    public bool Flush(long targetVersion)
    {
        if (targetVersion <= 0) return true;

        while (true)
        {
            Task? worker;
            lock (_gate)
            {
                if (_writtenVersion >= targetVersion) return true;
                if (_failedVersion >= targetVersion && !_hasPending && !_workerRunning) return false;

                EnsureWorkerLocked();
                worker = _worker;
                if (worker == null) return false;
            }

            try { worker.GetAwaiter().GetResult(); }
            catch { /* writer failures are recorded as versions and returned below */ }
        }
    }

    public bool FlushAll()
    {
        while (true)
        {
            long target;
            lock (_gate) target = _enqueuedVersion;
            if (!Flush(target)) return false;
            lock (_gate)
            {
                if (_enqueuedVersion == target && !_hasPending && !_workerRunning) return true;
            }
        }
    }

    private void EnsureWorkerLocked()
    {
        if (!_hasPending || _workerRunning) return;
        _workerRunning = true;
        _worker = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            T value;
            long version;
            lock (_gate)
            {
                if (!_hasPending)
                {
                    _workerRunning = false;
                    return;
                }

                value = _pending!;
                version = _enqueuedVersion;
                _pending = default;
                _hasPending = false;
            }

            bool written;
            try { written = _write(value); }
            catch { written = false; }

            lock (_gate)
            {
                if (written) _writtenVersion = Math.Max(_writtenVersion, version);
                else _failedVersion = Math.Max(_failedVersion, version);
            }
        }
    }
}
