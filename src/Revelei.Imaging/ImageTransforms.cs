using System.Runtime.InteropServices;

namespace Revelei.Imaging;

/// <summary>
/// The edits that move pixels without changing any: a quarter turn either way (PB65), a mirror
/// either way (PB66) and a crop (PB67). Each pixel's eight bytes are copied whole, alpha included, so nothing is
/// resampled, four turns give back the same bytes and so do two flips. Rows are walked in blocks so the column-wise writes of a turn stay in the cache,
/// which keeps an 8192 px photo well under a second on the UI thread.
/// </summary>
public static class ImageTransforms
{
    private const int Block = 64;

    /// <summary>The photo turned a quarter clockwise.</summary>
    /// <param name="image">The source.</param>
    /// <returns>A new image, its width the source's height.</returns>
    public static RgbaImage RotateRight(RgbaImage image) => Turn(image, clockwise: true);

    /// <summary>The photo turned a quarter counter-clockwise.</summary>
    /// <param name="image">The source.</param>
    /// <returns>A new image, its width the source's height.</returns>
    public static RgbaImage RotateLeft(RgbaImage image) => Turn(image, clockwise: false);

    /// <summary>The pixels inside a rectangle, copied row by row with no resampling (PB67).</summary>
    /// <param name="image">The source.</param>
    /// <param name="rect">What to keep; it must lie inside the image.</param>
    /// <returns>A new image the rectangle's size.</returns>
    public static RgbaImage Crop(RgbaImage image, PixelRect rect)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (rect.Width < 1 || rect.Height < 1 || rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > image.Width || rect.Y + rect.Height > image.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), rect, $"Not inside the {image.Width} x {image.Height} image.");
        }

        RgbaImage result = new(rect.Width, rect.Height, image.SourceBitDepth);
        for (int y = 0; y < rect.Height; y++)
        {
            image.Row(rect.Y + y).Slice(rect.X * 4, rect.Width * 4).CopyTo(result.Row(y));
        }

        return result;
    }

    /// <summary>The photo mirrored left to right.</summary>
    /// <param name="image">The source.</param>
    /// <returns>A new image of the same size.</returns>
    public static RgbaImage FlipHorizontal(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        RgbaImage result = new(image.Width, image.Height, image.SourceBitDepth);
        for (int y = 0; y < image.Height; y++)
        {
            Span<ulong> row = MemoryMarshal.Cast<ushort, ulong>(result.Row(y));
            MemoryMarshal.Cast<ushort, ulong>(image.Row(y)).CopyTo(row);
            row.Reverse();
        }

        return result;
    }

    /// <summary>The photo mirrored top to bottom.</summary>
    /// <param name="image">The source.</param>
    /// <returns>A new image of the same size.</returns>
    public static RgbaImage FlipVertical(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        RgbaImage result = new(image.Width, image.Height, image.SourceBitDepth);
        for (int y = 0; y < image.Height; y++)
        {
            image.Row(y).CopyTo(result.Row(image.Height - 1 - y));
        }

        return result;
    }

    private static RgbaImage Turn(RgbaImage image, bool clockwise)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height;
        RgbaImage result = new(h, w, image.SourceBitDepth);
        ReadOnlySpan<ulong> source = MemoryMarshal.Cast<ushort, ulong>(image.Samples);
        Span<ulong> target = MemoryMarshal.Cast<ushort, ulong>(result.Samples);
        for (int by = 0; by < h; by += Block)
        {
            int yEnd = Math.Min(by + Block, h);
            for (int bx = 0; bx < w; bx += Block)
            {
                int xEnd = Math.Min(bx + Block, w);
                for (int y = by; y < yEnd; y++)
                {
                    int row = y * w;
                    for (int x = bx; x < xEnd; x++)
                    {
                        // Clockwise, (x, y) lands at (h - 1 - y, x); counter-clockwise at (y, w - 1 - x).
                        int to = clockwise ? (x * h) + (h - 1 - y) : ((w - 1 - x) * h) + y;
                        target[to] = source[row + x];
                    }
                }
            }
        }

        return result;
    }
}
