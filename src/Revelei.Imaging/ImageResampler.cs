namespace Revelei.Imaging;

/// <summary>
/// Resizes an image to any size (PB32), for models with a fixed input. Shrinking averages each
/// output pixel's footprint, since one bilinear tap per pixel aliases a large photo into noise;
/// growing is bilinear between pixel centres.
/// </summary>
public static class ImageResampler
{
    /// <summary>The image at a new size, all four channels.</summary>
    /// <param name="image">The source.</param>
    /// <param name="width">Columns wanted.</param>
    /// <param name="height">Rows wanted.</param>
    /// <returns>A new image.</returns>
    public static RgbaImage Resize(RgbaImage image, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        RgbaImage result = new(width, height, image.SourceBitDepth);
        Axis[] columns = Weights(image.Width, width);
        Axis[] rows = Weights(image.Height, height);
        Span<double> sum = stackalloc double[4];
        for (int y = 0; y < height; y++)
        {
            Span<ushort> target = result.Row(y);
            for (int x = 0; x < width; x++)
            {
                sum.Clear();
                foreach ((int sy, double wy) in rows[y].Taps)
                {
                    ReadOnlySpan<ushort> row = image.Row(sy);
                    foreach ((int sx, double wx) in columns[x].Taps)
                    {
                        double w = wx * wy;
                        for (int c = 0; c < 4; c++)
                        {
                            sum[c] += row[(sx * 4) + c] * w;
                        }
                    }
                }

                for (int c = 0; c < 4; c++)
                {
                    target[(x * 4) + c] = (ushort)Math.Clamp(Math.Round(sum[c]), 0, RgbaImage.Max);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// A single-channel plane at a new size, bilinear between pixel centres, as a model's mask is
    /// brought back to the photo's size.
    /// </summary>
    /// <param name="plane">width x height values, row by row.</param>
    /// <param name="width">Its columns.</param>
    /// <param name="height">Its rows.</param>
    /// <param name="toWidth">Columns wanted.</param>
    /// <param name="toHeight">Rows wanted.</param>
    /// <returns>toWidth x toHeight values.</returns>
    public static float[] ResizePlane(ReadOnlySpan<float> plane, int width, int height, int toWidth, int toHeight)
    {
        if (plane.Length != width * height)
        {
            throw new ArgumentException($"{plane.Length} values are not {width} x {height}.", nameof(plane));
        }

        float[] result = new float[toWidth * toHeight];
        for (int y = 0; y < toHeight; y++)
        {
            float sy = Math.Clamp((((y + 0.5f) * height) / toHeight) - 0.5f, 0, height - 1);
            int y0 = (int)sy;
            int y1 = Math.Min(y0 + 1, height - 1);
            float fy = sy - y0;
            for (int x = 0; x < toWidth; x++)
            {
                float sx = Math.Clamp((((x + 0.5f) * width) / toWidth) - 0.5f, 0, width - 1);
                int x0 = (int)sx;
                int x1 = Math.Min(x0 + 1, width - 1);
                float fx = sx - x0;
                float top = (plane[(y0 * width) + x0] * (1 - fx)) + (plane[(y0 * width) + x1] * fx);
                float bottom = (plane[(y1 * width) + x0] * (1 - fx)) + (plane[(y1 * width) + x1] * fx);
                result[(y * toWidth) + x] = (top * (1 - fy)) + (bottom * fy);
            }
        }

        return result;
    }

    private readonly record struct Axis((int Index, double Weight)[] Taps);

    // Per output index, which source indices contribute and how much.
    private static Axis[] Weights(int from, int to)
    {
        Axis[] axes = new Axis[to];
        double scale = from / (double)to;
        for (int i = 0; i < to; i++)
        {
            if (scale <= 1)
            {
                double s = Math.Clamp(((i + 0.5) * scale) - 0.5, 0, from - 1);
                int a = (int)s;
                int b = Math.Min(a + 1, from - 1);
                double f = s - a;
                axes[i] = new(a == b ? [(a, 1.0)] : [(a, 1 - f), (b, f)]);
                continue;
            }

            // The footprint [i, i + 1) x scale, with its partly covered ends weighted by coverage.
            double start = i * scale;
            double end = (i + 1) * scale;
            List<(int, double)> taps = [];
            for (int s = (int)start; s < Math.Min(from, (int)Math.Ceiling(end)); s++)
            {
                double covered = Math.Min(end, s + 1) - Math.Max(start, s);
                if (covered > 0)
                {
                    taps.Add((s, covered / scale));
                }
            }

            axes[i] = new([.. taps]);
        }

        return axes;
    }
}
