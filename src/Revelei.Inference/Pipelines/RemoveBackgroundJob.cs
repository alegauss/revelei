using System.Diagnostics;
using Revelei.Imaging;

namespace Revelei.Inference.Pipelines;

/// <summary>A background removal as the UI asks for one (PB32): queued, cancellable before it starts, logged.</summary>
public static class RemoveBackgroundJob
{
    /// <summary>The operation's name in the log.</summary>
    public const string Operation = "remove background";

    /// <summary>Queues a background removal behind whatever is running.</summary>
    /// <param name="service">The inference queue.</param>
    /// <param name="library">The models.</param>
    /// <param name="image">The photo.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <param name="refine">Whether to refine the mask against the photo (PB47).</param>
    /// <param name="progress">Its one step, done.</param>
    /// <param name="log">Where the finished operation is recorded, whatever its outcome; null records nothing.</param>
    /// <returns>The job.</returns>
    public static InferenceJob<RgbaImage> Enqueue(InferenceService service, ModelLibrary library, RgbaImage image, string variant, bool refine = true, IProgress<JobProgress>? progress = null, OperationLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(image);
        return service.Enqueue(
            Operation,
            context =>
            {
                Stopwatch clock = Stopwatch.StartNew();
                string outcome = "ok";
                try
                {
                    context.Cancellation.ThrowIfCancellationRequested();
                    // PB62: the subject, then the edge's tiles, each with the time left.
                    return RemoveBackground.Run(library, image, variant, refine, (stage, done, total) => context.Report(done, total, stage));
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
                        RemoveBackground.Model,
                        RemoveBackground.VariantFor(library, variant),
                        library.RunsOn(RemoveBackground.Model, RemoveBackground.VariantFor(library, variant)).Name,
                        library.Provider.Adapter?.Name,
                        image.Width,
                        image.Height,
                        0,
                        1,
                        [],
                        clock.Elapsed.TotalMilliseconds,
                        outcome));
                }
            },
            progress);
    }
}
