using Microsoft.ML.OnnxRuntime;
using Revelei.Imaging;

namespace Revelei.Inference.Pipelines;

/// <summary>
/// An image-to-image model run over tiles, whatever it does to them: Real-ESRGAN (PB19) and SCUNet
/// (PB36) share it. The starting tile comes from free memory (PB41), an out-of-memory tile is
/// retried smaller and then on the CPU (PB21), and alpha is resized beside the model.
/// </summary>
internal static class TiledModel
{
    /// <summary>Runs a model over an image; call it from the inference queue.</summary>
    /// <param name="library">The models and their sessions.</param>
    /// <param name="model">The model's folder name.</param>
    /// <param name="image">The image.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <param name="tile">The tile side, or null to size it from free memory.</param>
    /// <param name="overlap">Context read around each tile.</param>
    /// <param name="minTile">The smallest tile an out-of-memory retry halves down to.</param>
    /// <param name="progress">Called after each tile with (done, total); may throw to cancel.</param>
    /// <param name="log">Told each recovery step.</param>
    /// <param name="stats">Filled with the tile plan and recovery steps.</param>
    /// <returns>The image, meta.json's scale times larger.</returns>
    public static RgbaImage Run(ModelLibrary library, string model, RgbaImage image, string variant, int? tile, int overlap, int minTile, Action<int, int>? progress, Action<string>? log, UpscaleStats? stats)
    {
        ModelMeta meta = library.Meta(model);
        using SessionLease<InferenceSession> lease = library.Sessions.Acquire(new ModelKey(model, variant));
        if (tile is null)
        {
            // PB41: the starting tile from the memory there is, measured after the session loaded.
            long free = MemoryBudget.Free(library.Provider);
            tile = free > 0
                ? TileSizer.Choose(free, meta.Tile.LargestActivationBytesPerPixel, overlap)
                : meta.Tile.Recommended ?? Tiler.DefaultTile;
            log?.Invoke($"Tile {tile} px from {free / 1048576.0:0} MB free on {library.RunsOn(model, variant).Name}.");
        }

        TileOptions options = new(
            Scale: meta.Scale,
            Low: (float)meta.Input.ValueRange[0],
            High: (float)meta.Input.ValueRange[1],
            Tile: tile.Value,
            Overlap: overlap,
            Multiple: meta.Input.Multiple);

        // PB21: an out-of-memory tile is retried smaller down to minTile, then the run moves to the
        // CPU, whose session is only loaded if that ever happens.
        SessionLease<InferenceSession>? cpuLease = null;
        try
        {
            TileModel? fallback = library.CpuFallback is { } cpu
                ? (input, width, height) => RunTile((cpuLease ??= cpu.Acquire(new ModelKey(model, variant))).Session, meta, input, width, height)
                : null;
            if (stats is not null)
            {
                stats.Model = model;
                stats.Tile = options.Tile;
            }

            TileRetry retry = new(MemoryFailures.Classify, MinTile: minTile, Fallback: fallback, Log: line =>
            {
                stats?.Retries.Add(line);
                log?.Invoke(line);
            });

            RgbaImage result = Tiler.Run(
                image,
                (input, width, height) => RunTile(lease.Session, meta, input, width, height),
                options,
                (done, total) =>
                {
                    stats?.Tiles = done;
                    progress?.Invoke(done, total);
                },
                retry);
            if (image.HasTransparency)
            {
                AlphaResampler.ScaleInto(image, result, meta.Scale);
            }

            return result;
        }
        finally
        {
            cpuLease?.Dispose();
        }
    }

    private static float[] RunTile(InferenceSession session, ModelMeta meta, float[] input, int width, int height)
    {
        using OrtValue tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, height, width]);
        using RunOptions run = new();
        using IDisposableReadOnlyCollection<OrtValue> outputs = session.Run(run, [meta.Input.Name], [tensor], [meta.Output.Name]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }
}
