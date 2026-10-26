using System.Diagnostics;
using Revelei.Imaging;

namespace Revelei.Inference.Pipelines;

/// <summary>An upscale as the UI asks for one: queued, reporting tile progress, cancellable (PB20), logged (PB22).</summary>
public static class UpscaleJob
{
    /// <summary>The stage name tile progress carries.</summary>
    public const string Stage = "upscale";

    /// <summary>Queues an upscale behind whatever is running.</summary>
    /// <param name="service">The inference queue.</param>
    /// <param name="library">The models.</param>
    /// <param name="image">The image.</param>
    /// <param name="factor">2 or 4.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <param name="progress">Tile X of Y with an estimate of the time left; on the UI thread when made there.</param>
    /// <param name="log">Where the finished operation is recorded, whatever its outcome; null records nothing.</param>
    /// <returns>The job; cancelling it stops after the current tile.</returns>
    public static InferenceJob<RgbaImage> Enqueue(InferenceService service, ModelLibrary library, RgbaImage image, int factor, string variant, IProgress<JobProgress>? progress = null, OperationLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(image);
        string operation = $"upscale x{factor}";
        return service.Enqueue(
            operation,
            context =>
            {
                UpscaleStats stats = new();
                Stopwatch clock = Stopwatch.StartNew();
                string outcome = "ok";
                try
                {
                    return Upscale.Run(library, image, factor, variant, progress: (done, total) => context.Report(done, total, Stage), stats: stats);
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
                        operation,
                        stats.Model.Length > 0 ? stats.Model : Upscale.ModelFor(factor),
                        variant,
                        library.RunsOn(Upscale.ModelFor(factor), variant).Name,
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
