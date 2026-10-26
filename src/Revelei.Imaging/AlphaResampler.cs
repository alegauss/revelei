namespace Revelei.Imaging;

/// <summary>
/// Scales an alpha channel by a whole factor with bilinear filtering (PB19). Models see RGB only,
/// so a transparent image's alpha is resized beside them with a plain resampler: smooth edges
/// without inventing detail.
/// </summary>
public static class AlphaResampler
{
    /// <summary>Writes the source's alpha, scaled bilinearly, into the target's alpha.</summary>
    /// <param name="source">The original image.</param>
    /// <param name="target">The scaled image, exactly scale times larger each way.</param>
    /// <param name="scale">The factor.</param>
    public static void ScaleInto(RgbaImage source, RgbaImage target, int scale)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Width != source.Width * scale || target.Height != source.Height * scale)
        {
            throw new ArgumentException($"{target.Width} x {target.Height} is not {source.Width} x {source.Height} times {scale}.", nameof(target));
        }

        for (int y = 0; y < target.Height; y++)
        {
            // Pixel centres: target pixel y sits at source coordinate (y + 0.5) / scale - 0.5.
            float sy = Math.Clamp(((y + 0.5f) / scale) - 0.5f, 0, source.Height - 1);
            int y0 = (int)sy;
            int y1 = Math.Min(y0 + 1, source.Height - 1);
            float fy = sy - y0;
            ReadOnlySpan<ushort> top = source.Row(y0);
            ReadOnlySpan<ushort> bottom = source.Row(y1);
            Span<ushort> row = target.Row(y);
            for (int x = 0; x < target.Width; x++)
            {
                float sx = Math.Clamp(((x + 0.5f) / scale) - 0.5f, 0, source.Width - 1);
                int x0 = (int)sx;
                int x1 = Math.Min(x0 + 1, source.Width - 1);
                float fx = sx - x0;
                float a = (top[(x0 * 4) + 3] * (1 - fx)) + (top[(x1 * 4) + 3] * fx);
                float b = (bottom[(x0 * 4) + 3] * (1 - fx)) + (bottom[(x1 * 4) + 3] * fx);
                row[(x * 4) + 3] = (ushort)MathF.Round((a * (1 - fy)) + (b * fy));
            }
        }
    }
}
