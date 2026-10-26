using Revelei.Imaging;

namespace Revelei.Inference.Pipelines;

/// <summary>
/// Real-world denoising with SCUNet (PB36), the PSNR-trained weights, so noise is removed rather
/// than texture invented. Tiled like an upscale at scale 1; SCUNet's three halvings of 8 px
/// attention windows make every tile side, and the overlap, a multiple of 64.
/// </summary>
public static class Denoise
{
    /// <summary>The model's folder name.</summary>
    public const string Model = "scunet-color-real-psnr";

    /// <summary>The stage name tile progress carries.</summary>
    public const string Stage = "denoise";

    /// <summary>
    /// Context read around each tile: one multiple, the least SCUNet can take. DenoiseTests holds
    /// 128 px tiles of building.png above 40 dB against one whole-image run.
    /// </summary>
    public const int DefaultOverlap = 64;

    /// <summary>The smallest tile an out-of-memory retry halves down to before moving to the CPU.</summary>
    public const int MinTile = 64;

    /// <summary>Denoises an image; call it from the inference queue, one run at a time.</summary>
    /// <param name="library">The models and their sessions.</param>
    /// <param name="image">The image.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <param name="tile">The tile side, or null to size it from free memory.</param>
    /// <param name="progress">Called after each tile with (done, total); may throw to cancel.</param>
    /// <param name="stats">Filled with the tile plan and recovery steps, for the operation log.</param>
    /// <returns>The image, the same size, alpha kept.</returns>
    public static RgbaImage Run(ModelLibrary library, RgbaImage image, string variant, int? tile = null, Action<int, int>? progress = null, UpscaleStats? stats = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(image);
        if (library.Meta(Model).Scale != 1)
        {
            throw new InvalidDataException($"{Model}'s meta.json does not keep the size.");
        }

        return TiledModel.Run(library, Model, image, variant, tile, DefaultOverlap, MinTile, progress, log: null, stats);
    }
}
