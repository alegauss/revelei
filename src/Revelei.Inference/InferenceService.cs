using System.Threading.Channels;

namespace Revelei.Inference;

/// <summary>How far a job has got, e.g. tile 3 of 12 of the upscale stage, about 40 s left.</summary>
/// <param name="JobId">The job reporting.</param>
/// <param name="Done">Steps finished in this stage.</param>
/// <param name="Total">Steps in this stage.</param>
/// <param name="Stage">What the steps are, e.g. "upscale"; empty for a job with one stage.</param>
/// <param name="Remaining">The time left in this stage from the steps' pace so far, or null before the first.</param>
public readonly record struct JobProgress(long JobId, int Done, int Total, string Stage = "", TimeSpan? Remaining = null);

/// <summary>What a running job sees: its cancellation and its progress report.</summary>
public sealed class JobContext
{
    private readonly IProgress<JobProgress>? progress;
    private readonly TimeProvider time;
    private readonly TimeSpan interval;
    private string stage = string.Empty;
    private long stageStarted;
    private long lastSent;
    private bool sentAny;

    internal JobContext(long id, string operation, CancellationToken cancellation, IProgress<JobProgress>? progress, TimeProvider time, TimeSpan interval)
    {
        Id = id;
        Operation = operation;
        Cancellation = cancellation;
        this.progress = progress;
        this.time = time;
        this.interval = interval;
        stageStarted = time.GetTimestamp();
    }

    /// <summary>The job's id.</summary>
    public long Id { get; }

    /// <summary>What it does, e.g. "upscale x4".</summary>
    public string Operation { get; }

    /// <summary>Signalled when the job is cancelled; a job checks it between tiles.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>
    /// Reports progress, at most once per interval (PB20: a 4x upscale of a large photo has
    /// thousands of tiles, and the UI thread should not hear of every one), and then stops the job
    /// if it was cancelled meanwhile. The first step, the last step and a new stage always go out.
    /// </summary>
    /// <param name="done">Steps finished in this stage.</param>
    /// <param name="total">Steps in this stage.</param>
    /// <param name="stageName">What the steps are; a new name starts the stage's clock again.</param>
    public void Report(int done, int total, string stageName = "")
    {
        long now = time.GetTimestamp();
        bool newStage = !string.Equals(stageName, stage, StringComparison.Ordinal);
        if (newStage)
        {
            stage = stageName;
            stageStarted = now;
        }

        if (progress is not null && (newStage || !sentAny || done >= total || time.GetElapsedTime(lastSent, now) >= interval))
        {
            TimeSpan elapsed = time.GetElapsedTime(stageStarted, now);
            // Finished is nothing left, even for a stage whose only step is also its first report.
            TimeSpan? remaining = done >= total ? TimeSpan.Zero
                : done > 0 && !newStage ? elapsed / done * (total - done)
                : null;
            progress.Report(new JobProgress(Id, done, total, stage, remaining));
            lastSent = now;
            sentAny = true;
        }

        Cancellation.ThrowIfCancellationRequested();
    }
}

/// <summary>A queued or running job.</summary>
/// <typeparam name="T">What it produces.</typeparam>
public sealed class InferenceJob<T>
{
    private readonly CancellationTokenSource cancellation;

    internal InferenceJob(long id, string operation, CancellationTokenSource cancellation, Task<T> completion)
    {
        Id = id;
        Operation = operation;
        this.cancellation = cancellation;
        Completion = completion;
    }

    /// <summary>The job's id, in the order jobs were queued.</summary>
    public long Id { get; }

    /// <summary>What it does.</summary>
    public string Operation { get; }

    /// <summary>The result; cancelled when the job was, faulted when the work threw.</summary>
    public Task<T> Completion { get; }

    /// <summary>Cancels the job: before it starts it never runs, while running it stops at its next check.</summary>
    public void Cancel() => cancellation.Cancel();
}

/// <summary>
/// The one queue every inference goes through (PB15): jobs run strictly one at a time, in the
/// order queued, on a single background thread. Two sessions at once on one GPU, or in limited
/// RAM, is the fastest route to running out of memory, so nothing runs beside this.
/// </summary>
public sealed class InferenceService : IAsyncDisposable
{
    private readonly Channel<Func<Task>> queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private readonly TimeProvider time;
    private readonly TimeSpan progressInterval;
    private long lastId;

    /// <summary>The longest a progress report waits behind the one before it, unless configured.</summary>
    public static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Starts the queue's thread.</summary>
    /// <param name="progressInterval">The least time between two progress reports of a job.</param>
    /// <param name="time">The clock progress is paced and estimated with; TimeProvider.System in the app.</param>
    public InferenceService(TimeSpan? progressInterval = null, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        this.progressInterval = progressInterval ?? DefaultProgressInterval;
        worker = Task.Factory.StartNew(Drain, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Queues work. The progress callback runs where <paramref name="progress"/> was made,
    /// so a <see cref="Progress{T}"/> created on the UI thread reports there.</summary>
    /// <typeparam name="T">What the work produces.</typeparam>
    /// <param name="operation">What it does, for logs and the UI.</param>
    /// <param name="work">The work, run on the queue's thread.</param>
    /// <param name="progress">Where progress goes, or null.</param>
    /// <returns>The job.</returns>
    public InferenceJob<T> Enqueue<T>(string operation, Func<JobContext, T> work, IProgress<JobProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(shutdown.IsCancellationRequested, this);

        long id = Interlocked.Increment(ref lastId);
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task Run()
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                completion.TrySetResult(work(new JobContext(id, operation, cancellation.Token, progress, time, progressInterval)));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellation.Token);
            }
#pragma warning disable CA1031 // A job's failure belongs to that job; the queue goes on.
            catch (Exception failure)
#pragma warning restore CA1031
            {
                completion.TrySetException(failure);
            }
            finally
            {
                cancellation.Dispose();
            }

            return Task.CompletedTask;
        }

        if (!queue.Writer.TryWrite(Run))
        {
            cancellation.Dispose();
            throw new ObjectDisposedException(nameof(InferenceService));
        }

        return new InferenceJob<T>(id, operation, cancellation, completion.Task);
    }

    /// <summary>Cancels every queued and running job and stops the thread.</summary>
    /// <returns>When the thread has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (shutdown.IsCancellationRequested)
        {
            return;
        }

        // Cancel, not CancelAsync: every job's token is linked to this one, and only the
        // synchronous form has reached all of them by the time it returns, so a running job's
        // next check already sees it.
#pragma warning disable CA1849
        shutdown.Cancel();
#pragma warning restore CA1849
        queue.Writer.TryComplete();
        await worker.ConfigureAwait(false);
        shutdown.Dispose();
    }

    private async Task Drain()
    {
        // Every queued job still runs its Run, which sees the shutdown and completes as cancelled.
        await foreach (Func<Task> job in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await job().ConfigureAwait(false);
        }
    }
}
