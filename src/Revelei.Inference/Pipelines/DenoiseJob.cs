using System.Diagnostics;
using Revelei.Imaging;

namespace Revelei.Inference.Pipelines;

/// <summary>A denoise as the UI asks for one (PB36): queued, reporting tiles, cancellable, logged.</summary>
public static class DenoiseJob
{
    /// <summary>The operation's name in the log.</summary>
    public const string Operation = "denoise";

    /// <summary>Queues a denoise behind whatever is running.</summary>
    /// <param name="service">The inference queue.</param>
    /// <param name="library">The models.</param>
    /// <param name="image">The image.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <param name="progress">Tile X of Y with an estimate of the time left.</param>
    /// <param name="log">Where the finished operation is recorded, whatever its outcome; null records nothing.</param>
    /// <returns>The job; cancelling it stops after the current tile.</returns>
    public static InferenceJob<RgbaImage> Enqueue(InferenceService service, ModelLibrary library, RgbaImage image, string variant, IProgress<JobProgress>? progress = null, OperationLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(image);
        return service.Enqueue(
            Operation,
            context =>
            {
                UpscaleStats stats = new();
                Stopwatch clock = Stopwatch.StartNew();
                string outcome = "ok";
                try
                {
                    return Denoise.Run(library, image, variant, progress: (done, total) => context.Report(done, total, Denoise.Stage), stats: stats);
                }
                catch (OperationCanceledException)
                {
                    outcome = "cancelled";
                    throw;
                }
                catch (Exception failure)
                {
                    outcome = failure.Message;
                    throw;
                }
                finally
                {
                    log?.Append(new OperationRecord(
                        DateTimeOffset.Now,
                        Operation,
                        Denoise.Model,
                        variant,
                        library.RunsOn(Denoise.Model, variant).Name,
                        library.Provider.Adapter?.Name,
                        image.Width,
                        image.Height,
                        stats.Tile,
                        stats.Tiles,
                        [.. stats.Retries],
                        clock.Elapsed.TotalMilliseconds,
                        outcome));
                }
            },
            progress);
    }
}
