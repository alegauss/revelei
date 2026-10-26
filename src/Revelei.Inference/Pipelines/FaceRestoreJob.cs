using System.Diagnostics;
using Revelei.Imaging;

namespace Revelei.Inference.Pipelines;

/// <summary>A face restore as the UI asks for one (PB31): queued, reporting tiles then faces, cancellable, logged.</summary>
public static class FaceRestoreJob
{
    /// <summary>Queues a face restore behind whatever is running.</summary>
    /// <param name="service">The inference queue.</param>
    /// <param name="library">The models.</param>
    /// <param name="image">The photo.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <param name="strength">How much of the restored face shows, 0 to 1.</param>
    /// <param name="upscale">1, or 2 or 4 to upscale with Real-ESRGAN first.</param>
    /// <param name="progress">Tiles, then faces, each with an estimate of the time left.</param>
    /// <param name="log">Where the finished operation is recorded, whatever its outcome; null records nothing.</param>
    /// <returns>The job; cancelling it stops after the current tile or face.</returns>
    public static InferenceJob<FaceRestoreResult> Enqueue(InferenceService service, ModelLibrary library, RgbaImage image, string variant, double strength, int upscale = 1, IProgress<JobProgress>? progress = null, OperationLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(image);
        string operation = upscale == 1 ? "restore faces" : $"restore faces x{upscale}";
        return service.Enqueue(
            operation,
            context =>
            {
                Stopwatch clock = Stopwatch.StartNew();
                string outcome = "ok";
                int faces = 0;
                try
                {
                    FaceRestoreResult result = FaceRestore.Run(library, image, variant, strength, upscale, (stage, done, total) => context.Report(done, total, stage));
                    faces = result.Faces;
                    return result;
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
                        FaceRestore.Model,
                        FaceRestore.VariantFor(library, variant),
                        library.RunsOn(FaceRestore.Model, FaceRestore.VariantFor(library, variant)).Name,
                        library.Provider.Adapter?.Name,
                        image.Width,
                        image.Height,
                        FaceAlignment.CropSize,
                        faces,
                        [],
                        clock.Elapsed.TotalMilliseconds,
                        outcome));
                }
            },
            progress);
    }
}
