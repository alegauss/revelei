using Microsoft.ML.OnnxRuntime;

namespace Revelei.Inference.Tests;

/// <summary>PB16: sessions load on first use, are reused, drop on a variant switch and after idle time.</summary>
public sealed class SessionCacheTests
{
    private static readonly ModelKey X4Int8 = new("realesrgan-x4plus", "int8");
    private static readonly ModelKey X4Fp32 = new("realesrgan-x4plus", "fp32");
    private static readonly ModelKey Rmbg = new("rmbg-1.4", "int8");

    [Fact]
    public void ASessionLoadsOnFirstUseAndIsReused()
    {
        Loader loader = new();
        using SessionCache<FakeSession> cache = new(loader.Load, TimeSpan.FromMinutes(5), new ManualTime());

        FakeSession first;
        using (SessionLease<FakeSession> lease = cache.Acquire(X4Int8))
        {
            first = lease.Session;
        }

        using SessionLease<FakeSession> again = cache.Acquire(X4Int8);

        Assert.Same(first, again.Session);
        Assert.Equal(1, loader.Loads);
    }

    [Fact]
    public void AnIdleSessionIsReleasedAfterTheTimeout()
    {
        ManualTime time = new();
        Loader loader = new();
        using SessionCache<FakeSession> cache = new(loader.Load, TimeSpan.FromMinutes(5), time);
        FakeSession session;
        using (SessionLease<FakeSession> lease = cache.Acquire(X4Int8))
        {
            session = lease.Session;
        }

        time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, cache.Sweep());
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, cache.Sweep());

        Assert.True(session.Disposed);
        Assert.Empty(cache.Loaded);
    }

    [Fact]
    public void ASessionInUseIsNeverReleased()
    {
        ManualTime time = new();
        using SessionCache<FakeSession> cache = new(new Loader().Load, TimeSpan.FromMinutes(5), time);
        using SessionLease<FakeSession> lease = cache.Acquire(X4Int8);

        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, cache.Sweep());
        Assert.False(lease.Session.Disposed);
    }

    [Fact]
    public void IdleTimeCountsFromTheLastRunNotTheFirst()
    {
        ManualTime time = new();
        using SessionCache<FakeSession> cache = new(new Loader().Load, TimeSpan.FromMinutes(5), time);
        cache.Acquire(X4Int8).Dispose();
        time.Advance(TimeSpan.FromMinutes(4));
        cache.Acquire(X4Int8).Dispose();
        time.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(0, cache.Sweep());
    }

    [Fact]
    public void SwitchingVariantDropsTheOtherVariantOfThatModelOnly()
    {
        using SessionCache<FakeSession> cache = new(new Loader().Load, TimeSpan.FromMinutes(5), new ManualTime());
        FakeSession int8;
        using (SessionLease<FakeSession> lease = cache.Acquire(X4Int8))
        {
            int8 = lease.Session;
        }

        cache.Acquire(Rmbg).Dispose();
        cache.Acquire(X4Fp32).Dispose();

        Assert.True(int8.Disposed);
        Assert.Equal([X4Fp32, Rmbg], cache.Loaded.OrderBy(k => k.Model, StringComparer.Ordinal));
    }

    [Fact]
    public void AFailedLoadLeavesNothingBehind()
    {
        using SessionCache<FakeSession> cache = new(_ => throw new FileNotFoundException("int8.onnx"), TimeSpan.FromMinutes(5), new ManualTime());

        Assert.Throws<FileNotFoundException>(() => cache.Acquire(X4Int8));
        Assert.Empty(cache.Loaded);
    }

    [Fact]
    public void DisposingTheCacheReleasesEverySession()
    {
        Loader loader = new();
        SessionCache<FakeSession> cache = new(loader.Load, TimeSpan.FromMinutes(5), new ManualTime());
        cache.Acquire(X4Int8).Dispose();
        cache.Acquire(Rmbg).Dispose();

        cache.Dispose();

        Assert.All(loader.Made, session => Assert.True(session.Disposed));
        Assert.Throws<ObjectDisposedException>(() => cache.Acquire(X4Int8));
    }

    [Fact]
    public void TheAppCacheLoadsARealSessionFromTheModelsFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "pb-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "probe"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "probe", "fp32.onnx"), ProbeModel.Build());
            using SessionCache<InferenceSession> cache = ModelSessions.Create(root, new ProviderSelection(ExecutionProvider.Cpu, null, null), SessionCache<InferenceSession>.DefaultIdle);

            using SessionLease<InferenceSession> lease = cache.Acquire(new ModelKey("probe", "fp32"));

            Assert.Equal("x", lease.Session.InputNames[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeSession : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class Loader
    {
        public List<FakeSession> Made { get; } = [];

        public int Loads => Made.Count;

        public FakeSession Load(ModelKey key)
        {
            FakeSession session = new();
            Made.Add(session);
            return session;
        }
    }

    /// <summary>A clock the test moves, and a timer that never fires, so Sweep is called by hand.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Idle();

        private sealed class Idle : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
