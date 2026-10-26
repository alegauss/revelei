namespace Revelei.Inference.Tests;

/// <summary>PB15: one inference at a time, in order, cancellable while queued or between tiles.</summary>
public sealed class InferenceServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task JobsNeverOverlapAndRunInOrder()
    {
        await using InferenceService service = new();
        int running = 0;
        int most = 0;
        List<int> order = [];

        int Work(int n)
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            Thread.Sleep(20);
            lock (order)
            {
                order.Add(n);
            }

            Interlocked.Decrement(ref running);
            return n;
        }

        InferenceJob<int>[] jobs = [.. Enumerable.Range(0, 6).Select(n => service.Enqueue($"job {n}", _ => Work(n)))];
        await Task.WhenAll(jobs.Select(job => job.Completion)).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(1, most);
        Assert.Equal([0, 1, 2, 3, 4, 5], order);
        Assert.Equal(jobs.Select(job => job.Id).Order(), jobs.Select(job => job.Id));
    }

    [Fact]
    public async Task AJobCancelledWhileQueuedNeverRuns()
    {
        await using InferenceService service = new();
        using ManualResetEventSlim release = new();
        InferenceJob<int> blocker = service.Enqueue("blocker", _ => release.Wait(Patience, TestContext.Current.CancellationToken) ? 1 : 0);
        bool ran = false;
        InferenceJob<int> queued = service.Enqueue("queued", _ =>
        {
            ran = true;
            return 2;
        });

        queued.Cancel();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.Equal(1, await blocker.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.False(ran);
    }

    [Fact]
    public async Task AJobCancelledBetweenTilesStopsAtTheNextReport()
    {
        await using InferenceService service = new();
        using ManualResetEventSlim firstTile = new();
        int tiles = 0;
        InferenceJob<int> job = service.Enqueue("upscale", context =>
        {
            for (int tile = 1; tile <= 100; tile++)
            {
                tiles = tile;
                firstTile.Set();
                Thread.Sleep(5);
                context.Report(tile, 100);
            }

            return tiles;
        });

        Assert.True(firstTile.Wait(Patience, TestContext.Current.CancellationToken));
        job.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.True(tiles < 100, $"ran {tiles} tiles after the cancel");
    }

    [Fact]
    public async Task AFailingJobFaultsAloneAndTheQueueGoesOn()
    {
        await using InferenceService service = new();
        InferenceJob<int> failing = service.Enqueue<int>("bad", _ => throw new InvalidOperationException("out of memory"));
        InferenceJob<int> next = service.Enqueue("good", _ => 7);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.Equal("out of memory", error.Message);
        Assert.Equal(7, await next.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProgressCarriesTheJobAndTileCounts()
    {
        await using InferenceService service = new(progressInterval: TimeSpan.Zero);
        List<JobProgress> seen = [];
        SynchronousProgress progress = new(seen);

        InferenceJob<int> job = service.Enqueue("upscale", context =>
        {
            for (int tile = 1; tile <= 3; tile++)
            {
                context.Report(tile, 3);
            }

            return 3;
        }, progress);
        await job.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal([(job.Id, 1, 3), (job.Id, 2, 3), (job.Id, 3, 3)], seen.Select(p => (p.JobId, p.Done, p.Total)));
    }

    [Fact]
    public async Task ReportsAreThrottledButTheFirstAndLastAlwaysGoOut()
    {
        // PB20: ten 30 ms tiles against a 100 ms interval.
        ManualClock clock = new();
        await using InferenceService service = new(TimeSpan.FromMilliseconds(100), clock);
        List<JobProgress> seen = [];

        InferenceJob<int> job = service.Enqueue("upscale", context =>
        {
            for (int tile = 1; tile <= 10; tile++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(30));
                context.Report(tile, 10, "upscale");
            }

            return 10;
        }, new SynchronousProgress(seen));
        await job.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal([1, 5, 9, 10], seen.Select(p => p.Done));
    }

    [Fact]
    public async Task TheTimeLeftComesFromThePaceOfTheStage()
    {
        ManualClock clock = new();
        await using InferenceService service = new(TimeSpan.Zero, clock);
        List<JobProgress> seen = [];

        InferenceJob<int> job = service.Enqueue("restore", context =>
        {
            context.Report(0, 5, "detect");
            clock.Advance(TimeSpan.FromSeconds(1));
            context.Report(5, 5, "detect");
            context.Report(0, 10, "restore");
            clock.Advance(TimeSpan.FromSeconds(4));
            context.Report(2, 10, "restore");
            return 0;
        }, new SynchronousProgress(seen));
        await job.Completion.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(["detect", "detect", "restore", "restore"], seen.Select(p => p.Stage));
        Assert.Null(seen[0].Remaining);
        Assert.Equal(TimeSpan.Zero, seen[1].Remaining);
        Assert.Null(seen[2].Remaining);
        Assert.Equal(TimeSpan.FromSeconds(16), seen[3].Remaining);
    }

    [Fact]
    public async Task DisposingCancelsWhatIsStillQueued()
    {
        InferenceService service = new();
        using ManualResetEventSlim release = new();
        InferenceJob<int> running = service.Enqueue("running", context =>
        {
            release.Wait(Patience, TestContext.Current.CancellationToken);
            context.Cancellation.ThrowIfCancellationRequested();
            return 1;
        });
        InferenceJob<int> queued = service.Enqueue("queued", _ => 2);

        ValueTask disposing = service.DisposeAsync();
        release.Set();
        await disposing.AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.Completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Completion);
        Assert.Throws<ObjectDisposedException>(() => service.Enqueue("late", _ => 3));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class SynchronousProgress(List<JobProgress> seen) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => seen.Add(value);
    }

    /// <summary>A timestamp clock the job itself moves, so pacing is exact.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }
}
