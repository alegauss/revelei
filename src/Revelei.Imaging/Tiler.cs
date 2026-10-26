namespace Revelei.Imaging;

/// <summary>One tile: the pixels a model reads, and the part of them whose output is kept.</summary>
/// <param name="Input">What is fed to the model: the core plus the overlap, clamped to the image.</param>
/// <param name="Core">What this tile owns; the cores of a plan partition the image exactly.</param>
public readonly record struct Tile(PixelRect Input, PixelRect Core);

/// <summary>A model as the tiler sees it: a 1 x 3 x H x W tensor in, 1 x 3 x (H x scale) x (W x scale) out.</summary>
/// <param name="input">Planar RGB.</param>
/// <param name="width">W.</param>
/// <param name="height">H.</param>
/// <returns>Planar RGB, scale times larger each way.</returns>
public delegate float[] TileModel(float[] input, int width, int height);

/// <summary>
/// Runs a model over an image in overlapping tiles and stitches the result (PB18): a 12 MP photo
/// through Real-ESRGAN at once exhausts memory, and tiles keep the peak bounded. Each tile reads
/// <c>overlap</c> pixels of context on every side and keeps only its core, so a model whose output
/// depends on neighbours within the overlap gives exactly what one whole-image run would.
/// </summary>
public static class Tiler
{
    /// <summary>The tile side a run starts from unless told otherwise (PB41 sizes it from VRAM).</summary>
    public const int DefaultTile = 256;

    /// <summary>The context read around each tile unless told otherwise.</summary>
    public const int DefaultOverlap = 16;

    /// <summary>Splits an image into tiles whose cores partition it.</summary>
    /// <param name="width">Image columns.</param>
    /// <param name="height">Image rows.</param>
    /// <param name="tile">The core side.</param>
    /// <param name="overlap">Context on each side.</param>
    /// <param name="multiple">Every input side must be a multiple of this; the image's must be too.</param>
    /// <returns>The tiles, row by row.</returns>
    public static IReadOnlyList<Tile> Plan(int width, int height, int tile, int overlap, int multiple = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tile, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(overlap);
        ArgumentOutOfRangeException.ThrowIfLessThan(multiple, 1);
        if (width % multiple != 0 || height % multiple != 0)
        {
            throw new ArgumentException($"{width} x {height} is not a multiple of {multiple}; pad the image first.");
        }

        // Rounding the core and the overlap up to the multiple keeps every input side a multiple,
        // since each input edge is a core edge or a clamped image edge, both multiples.
        tile = RoundUp(tile, multiple);
        overlap = RoundUp(overlap, multiple);
        List<Tile> tiles = [];
        for (int y = 0; y < height; y += tile)
        {
            for (int x = 0; x < width; x += tile)
            {
                PixelRect core = new(x, y, Math.Min(tile, width - x), Math.Min(tile, height - y));
                int left = Math.Max(0, x - overlap);
                int top = Math.Max(0, y - overlap);
                int right = Math.Min(width, core.X + core.Width + overlap);
                int bottom = Math.Min(height, core.Y + core.Height + overlap);
                tiles.Add(new Tile(new PixelRect(left, top, right - left, bottom - top), core));
            }
        }

        return tiles;
    }

    /// <summary>Runs a model over the image tile by tile and returns the stitched result.</summary>
    /// <param name="source">The image.</param>
    /// <param name="model">The model.</param>
    /// <param name="options">Scale, tiling and the model's value range.</param>
    /// <param name="progress">Called after each tile with (done, total); may throw to cancel.</param>
    /// <param name="retry">What to do when a tile runs out of memory (PB21); null lets every failure through.</param>
    /// <returns>An image scale times larger, its alpha the source's resized by nearest neighbour.</returns>
    public static RgbaImage Run(RgbaImage source, TileModel model, TileOptions options, Action<int, int>? progress = null, TileRetry? retry = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        RgbaImage padded = PadToMultiple(source, options.Multiple);
        int scale = options.Scale;
        RgbaImage output = new(padded.Width * scale, padded.Height * scale, source.SourceBitDepth);
        int overlap = RoundUp(options.Overlap, options.Multiple);
        int planned = RoundUp(options.Tile, options.Multiple);
        int floor = Math.Min(planned, RoundUp(retry?.MinTile ?? planned, options.Multiple));
        int side = planned;
        TileModel current = model;
        bool fellBack = false;

        LinkedList<PixelRect> pending = new(Plan(padded.Width, padded.Height, planned, 0, options.Multiple).Select(t => t.Core));
        int done = 0;
        while (pending.First is { } next)
        {
            PixelRect core = next.Value;
            pending.RemoveFirst();
            if (core.Width > side || core.Height > side)
            {
                // A core planned before the tile shrank: split it at the current side, in order.
                LinkedListNode<PixelRect>? after = null;
                foreach (Tile part in Plan(core.Width, core.Height, side, 0, options.Multiple))
                {
                    PixelRect moved = part.Core with { X = core.X + part.Core.X, Y = core.Y + part.Core.Y };
                    after = after is null ? pending.AddFirst(moved) : pending.AddAfter(after, moved);
                }

                continue;
            }

            Tile tile = new(Expand(core, overlap, padded.Width, padded.Height), core);
            float[] result;
            try
            {
                result = RunTile(padded, tile, current, options);
            }
            catch (Exception failure) when (retry is not null && retry.Classify(failure) != TileFailure.Other)
            {
                TileFailure kind = retry.Classify(failure);
                pending.AddFirst(core);
                int smaller = RoundUp(side / 2, options.Multiple);
                if (kind == TileFailure.OutOfMemory && smaller >= floor && smaller < side)
                {
                    retry.Log?.Invoke($"Out of memory on a {side} px tile; retrying at {smaller} px.");
                    side = smaller;
                    continue;
                }

                if (retry.Fallback is not null && !fellBack)
                {
                    retry.Log?.Invoke(kind == TileFailure.DeviceLost
                        ? $"The GPU was lost ({failure.Message}); continuing on {retry.FallbackName} at {planned} px."
                        : $"Out of memory even at {side} px; continuing on {retry.FallbackName} at {planned} px.");
                    current = retry.Fallback;
                    fellBack = true;
                    side = planned;
                    continue;
                }

                throw new InsufficientMemoryException(
                    $"Not enough memory to run this model even on {side} px tiles{(fellBack ? $" on {retry.FallbackName}" : string.Empty)}. Close other applications or use a smaller image.",
                    failure);
            }

            WriteCore(result, tile, options, output);
            done++;
            progress?.Invoke(done, done + pending.Count);
        }

        ScaleAlpha(padded, output, scale);
        return padded == source ? output : CropImage(output, source.Width * scale, source.Height * scale);
    }

    private static PixelRect Expand(PixelRect core, int overlap, int width, int height)
    {
        int left = Math.Max(0, core.X - overlap);
        int top = Math.Max(0, core.Y - overlap);
        int right = Math.Min(width, core.X + core.Width + overlap);
        int bottom = Math.Min(height, core.Y + core.Height + overlap);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static float[] RunTile(RgbaImage padded, Tile tile, TileModel model, TileOptions options)
    {
        float[] input = new float[3 * tile.Input.Area];
        TensorConversion.ToTensor(padded, tile.Input, options.Low, options.High, input);
        return model(input, tile.Input.Width, tile.Input.Height);
    }

    private static void WriteCore(float[] result, Tile tile, TileOptions options, RgbaImage output)
    {
        int scale = options.Scale;
        int outWidth = tile.Input.Width * scale;
        int outHeight = tile.Input.Height * scale;
        if (result.Length != 3 * outWidth * outHeight)
        {
            throw new InvalidOperationException(
                $"The model returned {result.Length} values for a {tile.Input.Width} x {tile.Input.Height} tile; x{scale} needs {3 * outWidth * outHeight}.");
        }

        PixelRect keep = new(
            (tile.Core.X - tile.Input.X) * scale,
            (tile.Core.Y - tile.Input.Y) * scale,
            tile.Core.Width * scale,
            tile.Core.Height * scale);
        float[] core = Crop(result, outWidth, outHeight, keep);
        TensorConversion.FromTensor(core, options.Low, options.High, output, new PixelRect(tile.Core.X * scale, tile.Core.Y * scale, keep.Width, keep.Height));
    }

    /// <summary>A copy grown right and down to a multiple, repeating the last column and row.</summary>
    /// <param name="image">The image.</param>
    /// <param name="multiple">The multiple.</param>
    /// <returns>The image itself when it already fits.</returns>
    public static RgbaImage PadToMultiple(RgbaImage image, int multiple)
    {
        ArgumentNullException.ThrowIfNull(image);
        int width = RoundUp(image.Width, multiple);
        int height = RoundUp(image.Height, multiple);
        if (width == image.Width && height == image.Height)
        {
            return image;
        }

        RgbaImage padded = new(width, height, image.SourceBitDepth);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<ushort> from = image.Row(Math.Min(y, image.Height - 1));
            Span<ushort> to = padded.Row(y);
            from.CopyTo(to);
            ReadOnlySpan<ushort> last = from[^4..];
            for (int x = image.Width; x < width; x++)
            {
                last.CopyTo(to.Slice(x * 4, 4));
            }
        }

        return padded;
    }

    private static float[] Crop(float[] tensor, int width, int height, PixelRect keep)
    {
        float[] cropped = new float[3 * keep.Area];
        for (int channel = 0; channel < 3; channel++)
        {
            for (int y = 0; y < keep.Height; y++)
            {
                tensor.AsSpan((channel * width * height) + ((keep.Y + y) * width) + keep.X, keep.Width)
                    .CopyTo(cropped.AsSpan((channel * keep.Area) + (y * keep.Width), keep.Width));
            }
        }

        return cropped;
    }

    private static void ScaleAlpha(RgbaImage source, RgbaImage output, int scale)
    {
        for (int y = 0; y < output.Height; y++)
        {
            ReadOnlySpan<ushort> from = source.Row(y / scale);
            Span<ushort> to = output.Row(y);
            for (int x = 0; x < output.Width; x++)
            {
                to[(x * 4) + 3] = from[((x / scale) * 4) + 3];
            }
        }
    }

    private static RgbaImage CropImage(RgbaImage image, int width, int height)
    {
        RgbaImage cropped = new(width, height, image.SourceBitDepth);
        for (int y = 0; y < height; y++)
        {
            image.Row(y)[..(width * 4)].CopyTo(cropped.Row(y));
        }

        return cropped;
    }

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;
}

/// <summary>What a tile's failure was, as far as retrying goes.</summary>
public enum TileFailure
{
    /// <summary>Anything else: not retried.</summary>
    Other,

    /// <summary>An allocation failed: a smaller tile may fit.</summary>
    OutOfMemory,

    /// <summary>The GPU went away: no tile size helps, only another device.</summary>
    DeviceLost,
}

/// <summary>How a tiled run recovers from running out of memory (PB21).</summary>
/// <param name="Classify">Says whether a model's exception was a memory failure.</param>
/// <param name="MinTile">The smallest side to halve down to before giving up on the device.</param>
/// <param name="Fallback">The model on another device (the CPU), used once, or null.</param>
/// <param name="FallbackName">What to call that device in messages.</param>
/// <param name="Log">Told each step taken.</param>
public sealed record TileRetry(
    Func<Exception, TileFailure> Classify,
    int MinTile = 64,
    TileModel? Fallback = null,
    string FallbackName = "the CPU",
    Action<string>? Log = null);

/// <summary>How to tile a model run.</summary>
/// <param name="Scale">Output size over input size (4 for x4plus, 1 for a mask).</param>
/// <param name="Low">The model's value for sample 0, from meta.json.</param>
/// <param name="High">The model's value for sample 65535.</param>
/// <param name="Tile">The core side; rounded up to <paramref name="Multiple"/>.</param>
/// <param name="Overlap">Context on each side; rounded up to <paramref name="Multiple"/>.</param>
/// <param name="Multiple">Input sides must be a multiple of this (2 for x2plus).</param>
public sealed record TileOptions(int Scale, float Low, float High, int Tile = Tiler.DefaultTile, int Overlap = Tiler.DefaultOverlap, int Multiple = 1);
