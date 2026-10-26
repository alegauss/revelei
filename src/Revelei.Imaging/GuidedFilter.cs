namespace Revelei.Imaging;

/// <summary>
/// The guided filter (He, Sun and Tang, 2010) with a grey guide (PB47): it smooths a mask where
/// the photo is flat and lets the mask follow the photo's own edges where it is not, so a mask
/// made at 1024 px and scaled up takes the sharpness of the full-size photo. Box means come from
/// summed-area tables, so the cost is linear in the pixels whatever the radius.
/// </summary>
public static class GuidedFilter
{
    /// <summary>Refines a mask against the photo it belongs to.</summary>
    /// <param name="guide">The photo; its luminance guides the filter.</param>
    /// <param name="mask">Width x height values, row by row, about 0 to 1.</param>
    /// <param name="radius">The window's half side in pixels.</param>
    /// <param name="epsilon">How much detail the guide needs before an edge is followed; larger is smoother.</param>
    /// <returns>The refined mask, same layout, not clamped.</returns>
    public static float[] Refine(RgbaImage guide, ReadOnlySpan<float> mask, int radius, float epsilon)
    {
        ArgumentNullException.ThrowIfNull(guide);
        int width = guide.Width;
        int height = guide.Height;
        if (mask.Length != width * height)
        {
            throw new ArgumentException($"{mask.Length} values are not {width} x {height}.", nameof(mask));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        float[] luma = Luminance(guide);
        float[] p = mask.ToArray();
        float[] meanI = Box(luma, width, height, radius);
        float[] meanP = Box(p, width, height, radius);
        float[] corrIP = Box(Multiply(luma, p), width, height, radius);
        float[] corrII = Box(Multiply(luma, luma), width, height, radius);

        float[] a = new float[p.Length];
        float[] b = new float[p.Length];
        for (int i = 0; i < p.Length; i++)
        {
            float variance = corrII[i] - (meanI[i] * meanI[i]);
            float covariance = corrIP[i] - (meanI[i] * meanP[i]);
            a[i] = covariance / (variance + epsilon);
            b[i] = meanP[i] - (a[i] * meanI[i]);
        }

        float[] meanA = Box(a, width, height, radius);
        float[] meanB = Box(b, width, height, radius);
        float[] q = new float[p.Length];
        for (int i = 0; i < q.Length; i++)
        {
            q[i] = (meanA[i] * luma[i]) + meanB[i];
        }

        return q;
    }

    /// <summary>
    /// The mean over each pixel's (2r + 1)-square window, clipped at the image's edge so the
    /// border is averaged over the pixels it has.
    /// </summary>
    /// <param name="values">Width x height values.</param>
    /// <param name="width">Columns.</param>
    /// <param name="height">Rows.</param>
    /// <param name="radius">The window's half side.</param>
    /// <returns>The means.</returns>
    public static float[] Box(ReadOnlySpan<float> values, int width, int height, int radius)
    {
        // A summed-area table one row and column larger, in doubles so a 12 MP sum keeps its precision.
        int stride = width + 1;
        double[] table = new double[stride * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            double row = 0;
            for (int x = 0; x < width; x++)
            {
                row += values[(y * width) + x];
                table[((y + 1) * stride) + x + 1] = table[(y * stride) + x + 1] + row;
            }
        }

        float[] means = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            int top = Math.Max(0, y - radius);
            int bottom = Math.Min(height, y + radius + 1);
            for (int x = 0; x < width; x++)
            {
                int left = Math.Max(0, x - radius);
                int right = Math.Min(width, x + radius + 1);
                double sum = table[(bottom * stride) + right] - table[(top * stride) + right] - table[(bottom * stride) + left] + table[(top * stride) + left];
                means[(y * width) + x] = (float)(sum / ((bottom - top) * (right - left)));
            }
        }

        return means;
    }

    // Rec. 709 luma of the samples as stored, 0 to 1.
    private static float[] Luminance(RgbaImage image)
    {
        float[] luma = new float[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<ushort> row = image.Row(y);
            for (int x = 0; x < image.Width; x++)
            {
                luma[(y * image.Width) + x] = ((0.2126f * row[x * 4]) + (0.7152f * row[(x * 4) + 1]) + (0.0722f * row[(x * 4) + 2])) / RgbaImage.Max;
            }
        }

        return luma;
    }

    private static float[] Multiply(float[] a, float[] b)
    {
        float[] product = new float[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            product[i] = a[i] * b[i];
        }

        return product;
    }
}
