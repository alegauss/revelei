using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revelei.Inference;

/// <summary>One model waiting in the queue.</summary>
/// <param name="Model">The model's name in the catalog.</param>
/// <param name="Quantisation">The quantisation to fetch.</param>
/// <param name="Paused">Set aside until resumed; the queue passes over it.</param>
/// <param name="Failure">Why its last try failed, or null; a failed item is paused until resumed.</param>
public sealed record QueuedDownload(string Model, string Quantisation, bool Paused = false, string? Failure = null);

/// <summary>
/// PB73: several models requested at once download one at a time, since the bandwidth is the
/// limit, and the rest wait in order. Each can be paused, resumed, moved up or cancelled, and the
/// queue is written to disk on every change so it survives a restart. Pausing or cancelling the
/// model being fetched stops it between chunks; its .part file stays, so it resumes by Range.
/// What a download does is the caller's (<see cref="ModelDownloads.DownloadAsync"/> for a model);
/// the queue owns only the order and the state.
/// </summary>
public sealed class DownloadQueue
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        WriteIndented = true,
    };

    private readonly Lock gate = new();
    private readonly string file;
    private readonly List<QueuedDownload> items;
    private (string Model, CancellationTokenSource Stop)? running;

    /// <summary>Opens the queue written at <paramref name="file"/>, or an empty one.</summary>
    /// <param name="file">Where the queue is kept, e.g. &lt;models&gt;\queue.json.</param>
    public DownloadQueue(string file)
    {
        this.file = file;
        items = Load(file);
    }

    /// <summary>Raised after every change, from whichever thread made it.</summary>
    public event EventHandler? Changed;

    /// <summary>The queue in order.</summary>
    public IReadOnlyList<QueuedDownload> Items
    {
        get
        {
            lock (gate)
            {
                return [.. items];
            }
        }
    }

    /// <summary>The model being fetched now, or null.</summary>
    public string? Running
    {
        get
        {
            lock (gate)
            {
                return running?.Model;
            }
        }
    }

    /// <summary>Adds a model at the end; one already queued keeps its place and is resumed.</summary>
    /// <param name="model">The model.</param>
    /// <param name="quantisation">The quantisation.</param>
    public void Enqueue(string model, string quantisation) => Change(() =>
    {
        int at = items.FindIndex(i => i.Model == model);
        if (at < 0)
        {
            items.Add(new QueuedDownload(model, quantisation));
        }
        else
        {
            items[at] = items[at] with { Quantisation = quantisation, Paused = false, Failure = null };
        }
    });

    /// <summary>Sets a model aside; the one being fetched stops between chunks and keeps its .part.</summary>
    /// <param name="model">The model.</param>
    public void Pause(string model) => Change(() =>
    {
        int at = items.FindIndex(i => i.Model == model);
        if (at >= 0)
        {
            items[at] = items[at] with { Paused = true };
            StopIfRunning(model);
        }
    });

    /// <summary>Lets a paused or failed model wait its turn again.</summary>
    /// <param name="model">The model.</param>
    public void Resume(string model) => Change(() =>
    {
        int at = items.FindIndex(i => i.Model == model);
        if (at >= 0)
        {
            items[at] = items[at] with { Paused = false, Failure = null };
        }
    });

    /// <summary>Moves a model one place towards the front.</summary>
    /// <param name="model">The model.</param>
    public void MoveUp(string model) => Change(() =>
    {
        int at = items.FindIndex(i => i.Model == model);
        if (at > 0)
        {
            (items[at - 1], items[at]) = (items[at], items[at - 1]);
        }
    });

    /// <summary>Removes a model from the queue; the one being fetched stops between chunks.</summary>
    /// <param name="model">The model.</param>
    public void Cancel(string model) => Change(() =>
    {
        items.RemoveAll(i => i.Model == model);
        StopIfRunning(model);
    });

    /// <summary>
    /// Fetches the first model that is not paused, one at a time, until none is left waiting.
    /// A model fetched is removed; one that fails is paused with its failure, and the next goes.
    /// </summary>
    /// <param name="download">Fetches one model; it must stop when its token is cancelled.</param>
    /// <param name="cancellation">Stops the whole queue, leaving every item where it is.</param>
    /// <returns>When nothing is left waiting.</returns>
    public async Task RunAsync(Func<QueuedDownload, CancellationToken, Task> download, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(download);
        while (!cancellation.IsCancellationRequested)
        {
            QueuedDownload? next;
            CancellationTokenSource stop;
            lock (gate)
            {
                next = items.FirstOrDefault(i => !i.Paused);
                if (next is null)
                {
                    return;
                }

                stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                running = (next.Model, stop);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            using (stop)
            {
                try
                {
                    await download(next, stop.Token).ConfigureAwait(false);
                    Change(() => items.Remove(items.Find(i => i.Model == next.Model)!));
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // Paused, cancelled, or the whole queue stopped: the item already says which.
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    Change(() =>
                    {
                        int at = items.FindIndex(i => i.Model == next.Model);
                        if (at >= 0)
                        {
                            items[at] = items[at] with { Paused = true, Failure = failure.Message };
                        }
                    });
                }
                finally
                {
                    lock (gate)
                    {
                        running = null;
                    }

                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    private static List<QueuedDownload> Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<QueuedDownload>>(File.ReadAllText(file), Options) ?? [] : [];
        }
        catch (JsonException)
        {
            return []; // A queue that does not read is started over; the .part files stay and resume.
        }
    }

    private void StopIfRunning(string model)
    {
        if (running is { } now && now.Model == model)
        {
            now.Stop.Cancel();
        }
    }

    private void Change(Action change)
    {
        lock (gate)
        {
            change();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            string temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(items, Options));
            File.Move(temporary, file, overwrite: true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
