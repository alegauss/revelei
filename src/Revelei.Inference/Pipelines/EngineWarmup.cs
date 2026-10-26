namespace Revelei.Inference.Pipelines;

/// <summary>
/// Builds the Windows ML engines the operations will use before anyone asks for them (PB52):
/// TensorRT-RTX takes 10 to 17 s to build one, and a user's first upscale should not pay it. One
/// queued job per model, so an operation the user starts waits behind one build at most, and each
/// session is released as soon as its engine is on disk.
/// </summary>
public static class EngineWarmup
{
    /// <summary>The operation's name on the queue.</summary>
    public const string Operation = "prepare engines";

    /// <summary>The models an operation runs in this variant whose engine is not built yet.</summary>
    /// <param name="library">The models, on a Windows ML provider; any other has nothing to build.</param>
    /// <param name="variant">The variant the operations run in.</param>
    /// <returns>The models and variants to build, in the order the operations panel lists them.</returns>
    public static IReadOnlyList<ModelKey> Needed(ModelLibrary library, string variant)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (library.Engines is not { } engines)
        {
            return [];
        }

        List<ModelKey> keys =
        [
            new(Upscale.ModelFor(4), variant),
            new(Upscale.ModelFor(2), variant),
            new(FaceDetection.Model, "fp32"),
            new(FaceRestore.Model, FaceRestore.VariantFor(library, variant)),
            new(RemoveBackground.Model, RemoveBackground.VariantFor(library, variant)),
            new(Denoise.Model, variant),
        ];
        if (RemoveBackground.MatteVariant(library, variant) is { } matte)
        {
            keys.Insert(5, new(Matting.Model, matte));
        }

        // PB52: under auto only the upscalers run on the Windows ML provider, so only they are built.
        return [.. keys.Where(key =>
            library.Provider.For(key.Model).Provider == ExecutionProvider.WindowsML
            && library.Has(key.Model, key.Variant)
            && !engines.Has(library.FileOf(key.Model, key.Variant), library.Provider))];
    }

    /// <summary>Queues one build per model behind whatever is running.</summary>
    /// <param name="service">The inference queue.</param>
    /// <param name="library">The models, on a Windows ML provider.</param>
    /// <param name="keys">What <see cref="Needed"/> returned.</param>
    /// <returns>One job per model, each completing with the model it built.</returns>
    public static IReadOnlyList<InferenceJob<ModelKey>> Enqueue(InferenceService service, ModelLibrary library, IReadOnlyList<ModelKey> keys)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(keys);
        EngineCache engines = library.Engines ?? throw new InvalidOperationException("Only a Windows ML provider has engines to build.");
        return [.. keys.Select(key => service.Enqueue(Operation, context =>
        {
            context.Cancellation.ThrowIfCancellationRequested();
            try
            {
                using Microsoft.ML.OnnxRuntime.InferenceSession session = engines.Open(library.FileOf(key.Model, key.Variant), library.Provider);
            }
            catch (NotSupportedException)
            {
                // Recorded by the cache: this model runs on DirectML instead, and is not built again.
            }

            return key;
        }))];
    }
}
