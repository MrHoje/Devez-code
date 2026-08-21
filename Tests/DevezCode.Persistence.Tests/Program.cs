using System.Collections.Concurrent;
using System.Diagnostics;
using DevezCode.Services;

static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected={expected}, Actual={actual}");
    }
}

internal static class Program
{
static void NonBlockingAndCoalescing()
{
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var writes = new ConcurrentQueue<string>();
    var queue = new CoalescingSaveQueue<string>(value =>
    {
        writes.Enqueue(value);
        if (value == "one")
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        }
        return true;
    });

    var sw = Stopwatch.StartNew();
    queue.Enqueue("one");
    sw.Stop();
    Assert.True(sw.ElapsedMilliseconds < 100, "Enqueue must not wait for the writer.");
    Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "The background writer did not start.");

    queue.Enqueue("two");
    queue.Enqueue("three");
    release.Set();
    Assert.True(queue.FlushAll(), "FlushAll must persist the latest queued snapshot.");
    Assert.Equal("one,three", string.Join(',', writes), "Intermediate snapshots must be coalesced.");
}

static void FlushReportsFailureAndRecovers()
{
    int attempts = 0;
    var queue = new CoalescingSaveQueue<string>(_ => Interlocked.Increment(ref attempts) > 1);
    var failedVersion = queue.Enqueue("first");
    Assert.True(!queue.Flush(failedVersion), "Flush must report a failed target write.");

    var recoveredVersion = queue.Enqueue("second");
    Assert.True(queue.Flush(recoveredVersion), "A later snapshot must recover after a failed write.");
    Assert.Equal(2, attempts, "Recovery must perform exactly one later write.");
}

static void DurableLatestSnapshot()
{
    var dir = Path.Combine(Path.GetTempPath(), "DevezCode-Persistence-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var path = Path.Combine(dir, "state.json");
        var queue = new CoalescingSaveQueue<string>(json =>
        {
            AtomicFile.WriteAllText(path, json);
            return true;
        });

        queue.Enqueue("{\"version\":1}");
        queue.Enqueue("{\"version\":2}");
        Assert.True(queue.FlushAll(), "Normal-close flush must complete the durable write.");
        Assert.Equal("{\"version\":2}", File.ReadAllText(path), "The newest snapshot must win on disk.");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
}

static void RepeatedIdleRestartDoesNotLoseWork()
{
    int last = 0;
    var queue = new CoalescingSaveQueue<int>(value => { Volatile.Write(ref last, value); return true; });
    for (int i = 1; i <= 500; i++)
    {
        var version = queue.Enqueue(i);
        Assert.True(queue.Flush(version), "A worker restarted from idle lost a queued snapshot.");
    }
    Assert.Equal(500, last, "The final restarted worker snapshot was not written.");
}

static void FlushAllIncludesWorkQueuedDuringDrain()
{
    using var firstEntered = new ManualResetEventSlim();
    using var releaseFirst = new ManualResetEventSlim();
    var writes = new ConcurrentQueue<string>();
    var queue = new CoalescingSaveQueue<string>(value =>
    {
        writes.Enqueue(value);
        if (value == "first")
        {
            firstEntered.Set();
            releaseFirst.Wait(TimeSpan.FromSeconds(5));
        }
        return true;
    });

    queue.Enqueue("first");
    Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)), "The first close-flush write did not start.");
    var flushing = Task.Run(queue.FlushAll);
    queue.Enqueue("during-close");
    releaseFirst.Set();
    Assert.True(flushing.GetAwaiter().GetResult(), "FlushAll failed while new work arrived during drain.");
    Assert.Equal("first,during-close", string.Join(',', writes), "Close flush returned before later queued work was written.");
}

public static void Main()
{
    NonBlockingAndCoalescing();
    FlushReportsFailureAndRecovers();
    DurableLatestSnapshot();
    RepeatedIdleRestartDoesNotLoseWork();
    FlushAllIncludesWorkQueuedDuringDrain();
    Console.WriteLine("PASS: persistence queue is non-blocking, ordered, coalescing, recoverable, and flush-safe.");
}
}
