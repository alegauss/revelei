using Revelei.Imaging;

namespace Revelei.Inference.Pipelines;

/// <summary>
/// Real-ESRGAN upscaling, 2x or 4x (PB19), tiled through <see cref="Tiler"/> so a large photo
/// stays within memory. 2x runs the native x2plus model rather than x4 and a downscale: the same
/// download size, trained for the factor, and a quarter of x4's activation memory per pixel.
/// </summary>
public static class Upscale
{
    /// <summary>The 4x model's folder name.</summary>
    public const string X4 = "realesrgan-x4plus";

    /// <summary>The 2x model's folder name.</summary>
    public const string X2 = "realesrgan-x2plus";

    /// <summary>
    /// Context read around each tile: 32, not the tiler's generic 16, because RRDBNet sees further.
    /// Measured on 0014.jpg in 64 px tiles against one whole-image run (PB19): x4 gave 31.3, 36.2,
    /// 40.0, 45.0 and 58.3 dB at overlaps 8, 16, 24, 32 and 48; x2 gave 31.1, 33.6, 37.5, 39.9 and
    /// 43.1 dB. At 32 the seams sit under INT8's own 33 dB loss, for 1.56x the work of a bare
    /// 256 px tile, where 48 would cost 1.89x.
    /// </summary>
    public const int DefaultOverlap = 32;

    /// <summary>
    /// The largest result an upscale may make: 16384 x 16384, 2 GB as a 16-bit RGBA version, and
    /// the history keeps every version. A 4x of an 8192 px photo would be 32768 px, past what an
    /// array can index, and failed with an overflow after filling the memory.
    /// </summary>
    public const long MaxOutputPixels = 16384L * 16384;

    /// <summary>Whether upscaling a photo by a factor stays within <see cref="MaxOutputPixels"/>.</summary>
    /// <param name="width">The photo's width.</param>
    /// <param name="height">The photo's height.</param>
    /// <param name="factor">1, 2 or 4.</param>
    /// <returns>True when the result fits.</returns>
    public static bool Fits(int width, int height, int factor) => (long)width * factor * height * factor <= MaxOutputPixels;

    /// <summary>The model that upscales by a factor.</summary>
    /// <param name="factor">2 or 4.</param>
    /// <returns>Its folder name.</returns>
    public static string ModelFor(int factor) => factor switch
    {
        2 => X2,
        4 => X4,
        _ => throw new ArgumentOutOfRangeException(nameof(factor), factor, "Upscaling is 2x or 4x."),
    };

    /// <summary>Upscales an image; call it from the inference queue, one run at a time.</summary>
    /// <param name="library">The models and their sessions.</param>
    /// <param name="image">The image.</param>
    /// <param name="factor">2 or 4.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <param name="tile">The tile side, or null for the model's recommendation.</param>
    /// <param name="progress">Called after each tile with (done, total); may throw to cancel.</param>
    /// <param name="overlap">Context read around each tile, or null for <see cref="DefaultOverlap"/>.</param>
    /// <param name="log">Told each recovery step (smaller tiles, CPU fallback; PB21).</param>
    /// <param name="stats">Filled with the tile plan and recovery steps, for the operation log (PB22).</param>
    /// <returns>The image, factor times larger, alpha resized bilinearly.</returns>
    public static RgbaImage Run(ModelLibrary library, RgbaImage image, int factor, string variant, int? tile = null, Action<int, int>? progress = null, int? overlap = null, Action<string>? log = null, UpscaleStats? stats = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(image);
        string model = ModelFor(factor);
        ModelMeta meta = library.Meta(model);
        if (meta.Scale != factor)
        {
            throw new InvalidDataException($"{model}'s meta.json says x{meta.Scale}, not x{factor}.");
        }

        return TiledModel.Run(library, model, image, variant, tile, overlap ?? DefaultOverlap, MinTile, progress, log, stats);
    }

    /// <summary>The smallest tile an out-of-memory retry halves down to before moving to the CPU.</summary>
    public const int MinTile = 64;
}
