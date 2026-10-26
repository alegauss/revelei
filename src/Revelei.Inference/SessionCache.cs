using Microsoft.ML.OnnxRuntime;

namespace Revelei.Inference;

/// <summary>A model and the variant of it to run, e.g. realesrgan-x4plus / int8.</summary>
/// <param name="Model">The model's folder name.</param>
/// <param name="Variant">fp32, fp16 or int8.</param>
public readonly record struct ModelKey(string Model, string Variant);

/// <summary>A session borrowed from the cache; it cannot be released while a lease is held.</summary>
/// <typeparam name="TSession">The session type.</typeparam>
public sealed class SessionLease<TSession> : IDisposable
    where TSession : class, IDisposable
{
    private readonly SessionCache<TSession> owner;
    private readonly SessionCache<TSession>.Entry entry;
    private int returned;

    internal SessionLease(SessionCache<TSession> owner, SessionCache<TSession>.Entry entry)
    {
        this.owner = owner;
        this.entry = entry;
    }

    /// <summary>The session.</summary>
    public TSession Session => entry.Session;

    /// <summary>Gives the session back to the cache.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref returned, 1) == 0)
        {
            owner.Return(entry);
        }
    }
}

/// <summary>
/// Loads a session the first time its model and variant are used, keeps it, and releases it
/// after it has sat idle (PB16): every model resident at once would take the memory a large image
/// needs. Loading one variant of a model drops that model's other variants, so switching from
/// INT8 to High quality does not hold both.
/// </summary>
/// <typeparam name="TSession">The session type; InferenceSession in the app.</typeparam>
public sealed class SessionCache<TSession> : IDisposable
    where TSession : class, IDisposable
{
    /// <summary>The idle time after which a session is released unless configured otherwise.</summary>
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(5);

    private readonly Func<ModelKey, TSession> load;
    private readonly TimeProvider time;
    private readonly Dictionary<ModelKey, Entry> entries = [];
    private readonly Lock gate = new();
    private readonly ITimer sweeper;
    private bool disposed;

    /// <summary>Creates the cache and its idle sweep.</summary>
    /// <param name="load">Loads a session; called outside any lock the cache holds.</param>
    /// <param name="idle">How long an unused session is kept.</param>
    /// <param name="time">The clock and timer; TimeProvider.System in the app.</param>
    public SessionCache(Func<ModelKey, TSession> load, TimeSpan idle, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idle, TimeSpan.Zero);
        this.load = load;
        Idle = idle;
        this.time = time ?? TimeProvider.System;
        TimeSpan period = idle < TimeSpan.FromMinutes(1) ? idle : TimeSpan.FromMinutes(1);
        sweeper = this.time.CreateTimer(_ => Sweep(), null, period, period);
    }

    /// <summary>How long an unused session is kept.</summary>
    public TimeSpan Idle { get; }

    /// <summary>The keys whose sessions are loaded now.</summary>
    public IReadOnlyList<ModelKey> Loaded
    {
        get
        {
            lock (gate)
            {
                return [.. entries.Keys];
            }
        }
    }

    /// <summary>Borrows the session for a model and variant, loading it if it is not loaded.</summary>
    /// <param name="key">The model and variant.</param>
    /// <returns>A lease to dispose when the run is done.</returns>
    public SessionLease<TSession> Acquire(ModelKey key)
    {
        List<TSession> dropped = [];
        Entry entry;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(key);
                entries[key] = entry;
            }

            entry.Leases++;
            entry.LastUsed = time.GetUtcNow();

            foreach (Entry other in entries.Values.Where(e => e.Key.Model == key.Model && e.Key != key && e.Leases == 0).ToList())
            {
                entries.Remove(other.Key);
                if (other.Loaded is not null)
                {
                    dropped.Add(other.Loaded);
                }
            }
        }

        DisposeAll(dropped);

        try
        {
            entry.EnsureLoaded(load);
        }
        catch
        {
            lock (gate)
            {
                entry.Leases--;
                if (entry.Leases == 0 && entry.Loaded is null)
                {
                    entries.Remove(key);
                }
            }

            throw;
        }

        return new SessionLease<TSession>(this, entry);
    }

    /// <summary>Releases every session idle for longer than <see cref="Idle"/>; the timer calls it.</summary>
    /// <returns>How many were released.</returns>
    public int Sweep()
    {
        List<TSession> released = [];
        lock (gate)
        {
            if (disposed)
            {
                return 0;
            }

            DateTimeOffset now = time.GetUtcNow();
            foreach (Entry entry in entries.Values.Where(e => e.Leases == 0 && now - e.LastUsed >= Idle).ToList())
            {
                entries.Remove(entry.Key);
                if (entry.Loaded is not null)
                {
                    released.Add(entry.Loaded);
                }
            }
        }

        DisposeAll(released);
        return released.Count;
    }

    /// <summary>Stops the sweep and releases every session.</summary>
    public void Dispose()
    {
        List<TSession> all;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            all = [.. entries.Values.Select(e => e.Loaded).OfType<TSession>()];
            entries.Clear();
        }

        sweeper.Dispose();
        DisposeAll(all);
    }

    internal void Return(Entry entry)
    {
        lock (gate)
        {
            entry.Leases--;
            entry.LastUsed = time.GetUtcNow();
        }
    }

    private static void DisposeAll(List<TSession> sessions)
    {
        foreach (TSession session in sessions)
        {
            session.Dispose();
        }
    }

    /// <summary>One cached session and who is using it.</summary>
    internal sealed class Entry(ModelKey key)
    {
        private readonly Lock loading = new();

        public ModelKey Key { get; } = key;

        public int Leases { get; set; }

        public DateTimeOffset LastUsed { get; set; }

        public TSession? Loaded { get; private set; }

        public TSession Session => Loaded ?? throw new InvalidOperationException($"{Key} is not loaded.");

        public void EnsureLoaded(Func<ModelKey, TSession> load)
        {
            lock (loading)
            {
                Loaded ??= load(Key);
            }
        }
    }
}

/// <summary>The app's session cache: models from a folder, on the detected provider (PB16).</summary>
public static class ModelSessions
{
    /// <summary>A cache loading &lt;root&gt;/&lt;model&gt;/&lt;variant&gt;.onnx on the selected provider.</summary>
    /// <param name="root">The models folder.</param>
    /// <param name="provider">What PB14's detection chose.</param>
    /// <param name="idle">How long an unused session is kept.</param>
    /// <returns>The cache; the caller disposes it.</returns>
    public static SessionCache<InferenceSession> Create(string root, ProviderSelection provider, TimeSpan idle) =>
        Create(key => Path.Combine(root, key.Model, key.Variant + ".onnx"), provider, idle);

    /// <summary>A cache loading each model and variant from where <paramref name="path"/> says (PB51).</summary>
    /// <param name="path">The ONNX file for a key.</param>
    /// <param name="provider">What PB14's detection chose.</param>
    /// <param name="idle">How long an unused session is kept.</param>
    /// <param name="engines">PB52: compiled engines for a Windows ML provider; null builds every session from the model.</param>
    /// <returns>The cache; the caller disposes it.</returns>
    public static SessionCache<InferenceSession> Create(Func<ModelKey, string> path, ProviderSelection provider, TimeSpan idle, EngineCache? engines = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(provider);
        return new SessionCache<InferenceSession>(
            key =>
            {
                string file = path(key);
                ProviderSelection on = RunsOn(provider, engines, key.Model, file);
                if (engines is not null && on.Provider == ExecutionProvider.WindowsML)
                {
                    try
                    {
                        return engines.Open(file, on);
                    }
                    catch (NotSupportedException)
                    {
                        // PB55: a model the Windows ML provider cannot build runs on DirectML on the
                        // same GPU, or on the CPU, rather than failing its operation.
                        on = on.OnDirectML();
                    }
                }

                using SessionOptions options = SessionOptionsFactory.Create(on);
                return new InferenceSession(file, options);
            },
            idle);
    }

    /// <summary>
    /// Where one model's session runs (PB52): the selection, or DirectML on its GPU for a model it
    /// does not accelerate, one the provider could not build, or, under auto, one whose engine is
    /// not built yet. The log names this rather than the selection.
    /// </summary>
    /// <param name="provider">What detection chose.</param>
    /// <param name="engines">The Windows ML engines, or null.</param>
    /// <param name="model">The model's folder name.</param>
    /// <param name="file">Its ONNX file.</param>
    /// <returns>The selection the session is built on.</returns>
    public static ProviderSelection RunsOn(ProviderSelection provider, EngineCache? engines, string model, string file)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ProviderSelection on = provider.For(model);
        if (on.Provider != ExecutionProvider.WindowsML || engines is null)
        {
            return on;
        }

        if (engines.Unsupported(file, on) || (on.EnginesReadyOnly && !engines.Ready(file, on)))
        {
            return on.OnDirectML();
        }

        return on;
    }
}
