using System.Numerics;

namespace Revelei.Imaging;

/// <summary>
/// Images to model tensors and back (PB17). Every model takes Float32 NCHW RGB with the value
/// range its meta.json names: [0, 1] for Real-ESRGAN, [-1, 1] for GFPGAN, [-0.5, 0.5] for RMBG.
/// One bug here corrupts every output, so each direction is exact and unit tested.
/// </summary>
public static class TensorConversion
{
    /// <summary>Writes a region's RGB as a 1 x 3 x H x W tensor; alpha is left to the caller.</summary>
    /// <param name="image">The source.</param>
    /// <param name="region">The pixels to take, e.g. one tile.</param>
    /// <param name="low">What sample 0 becomes.</param>
    /// <param name="high">What sample 65535 becomes.</param>
    /// <param name="tensor">Exactly 3 x region area floats, planar R then G then B.</param>
    public static void ToTensor(RgbaImage image, PixelRect region, float low, float high, Span<float> tensor)
    {
        ArgumentNullException.ThrowIfNull(image);
        CheckRegion(image, region);
        int plane = region.Area;
        if (tensor.Length != plane * 3)
        {
            throw new ArgumentException($"The tensor holds {tensor.Length} values; 3 x {region.Width} x {region.Height} needs {plane * 3}.", nameof(tensor));
        }

        float scale = (high - low) / RgbaImage.Max;
        Span<float> r = tensor[..plane];
        Span<float> g = tensor.Slice(plane, plane);
        Span<float> b = tensor.Slice(plane * 2, plane);
        for (int y = 0; y < region.Height; y++)
        {
            ReadOnlySpan<ushort> row = image.Row(region.Y + y).Slice(region.X * 4, region.Width * 4);
            int o = y * region.Width;
            for (int x = 0, s = 0; x < region.Width; x++, s += 4)
            {
                r[o + x] = row[s] * scale + low;
                g[o + x] = row[s + 1] * scale + low;
                b[o + x] = row[s + 2] * scale + low;
            }
        }
    }

    /// <summary>
    /// Writes a 1 x 3 x H x W tensor back into an image region, clamping to the range, rounding to
    /// the nearest sample and leaving alpha as it is.
    /// </summary>
    /// <param name="tensor">Planar R, G, B for the region.</param>
    /// <param name="low">The value that becomes sample 0.</param>
    /// <param name="high">The value that becomes sample 65535.</param>
    /// <param name="image">The target.</param>
    /// <param name="region">Where in it.</param>
    public static void FromTensor(ReadOnlySpan<float> tensor, float low, float high, RgbaImage image, PixelRect region)
    {
        ArgumentNullException.ThrowIfNull(image);
        CheckRegion(image, region);
        int plane = region.Area;
        if (tensor.Length != plane * 3)
        {
            throw new ArgumentException($"The tensor holds {tensor.Length} values; 3 x {region.Width} x {region.Height} needs {plane * 3}.", nameof(tensor));
        }

        float[] scratch = new float[region.Width];
        for (int channel = 0; channel < 3; channel++)
        {
            ReadOnlySpan<float> source = tensor.Slice(channel * plane, plane);
            for (int y = 0; y < region.Height; y++)
            {
                ToSamples(source.Slice(y * region.Width, region.Width), low, high, scratch);
                Span<ushort> row = image.Row(region.Y + y).Slice(region.X * 4, region.Width * 4);
                for (int x = 0; x < region.Width; x++)
                {
                    row[(x * 4) + channel] = (ushort)scratch[x];
                }
            }
        }
    }

    /// <summary>Writes a 1 x 1 x H x W mask (RMBG's output) into a region's alpha.</summary>
    /// <param name="mask">The mask values.</param>
    /// <param name="low">The value that becomes transparent.</param>
    /// <param name="high">The value that becomes opaque.</param>
    /// <param name="image">The target.</param>
    /// <param name="region">Where in it.</param>
    public static void MaskToAlpha(ReadOnlySpan<float> mask, float low, float high, RgbaImage image, PixelRect region)
    {
        ArgumentNullException.ThrowIfNull(image);
        CheckRegion(image, region);
        if (mask.Length != region.Area)
        {
            throw new ArgumentException($"The mask holds {mask.Length} values; {region.Width} x {region.Height} needs {region.Area}.", nameof(mask));
        }

        float[] scratch = new float[region.Width];
        for (int y = 0; y < region.Height; y++)
        {
            ToSamples(mask.Slice(y * region.Width, region.Width), low, high, scratch);
            Span<ushort> row = image.Row(region.Y + y).Slice(region.X * 4, region.Width * 4);
            for (int x = 0; x < region.Width; x++)
            {
                row[(x * 4) + 3] = (ushort)scratch[x];
            }
        }
    }

    /// <summary>A copy of the alpha channel, to carry through a model that sees only RGB.</summary>
    /// <param name="image">The source.</param>
    /// <returns>Width x height alpha samples.</returns>
    public static ushort[] ExtractAlpha(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ushort[] alpha = new ushort[image.Width * image.Height];
        ReadOnlySpan<ushort> samples = image.Samples;
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = samples[(i * 4) + 3];
        }

        return alpha;
    }

    /// <summary>Scales, clamps and rounds model values to samples, eight or more at a time.</summary>
    private static void ToSamples(ReadOnlySpan<float> values, float low, float high, Span<float> samples)
    {
        float scale = RgbaImage.Max / (high - low);
        Vector<float> vLow = new(low);
        Vector<float> vScale = new(scale);
        Vector<float> zero = Vector<float>.Zero;
        Vector<float> max = new(RgbaImage.Max);
        Vector<float> half = new(0.5f);
        int i = 0;
        for (; i <= values.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            Vector<float> v = (new Vector<float>(values[i..]) - vLow) * vScale;
            Vector.Clamp(v, zero, max).CopyTo(samples[i..]);
        }

        for (; i < values.Length; i++)
        {
            samples[i] = Math.Clamp((values[i] - low) * scale, 0f, RgbaImage.Max);
        }

        // Round to nearest: floor(v + 0.5) after the clamp keeps 65535 at 65535.
        i = 0;
        for (; i <= samples.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            Vector.Floor(Vector.Min(new Vector<float>(samples[i..]) + half, max)).CopyTo(samples[i..]);
        }

        for (; i < samples.Length; i++)
        {
            samples[i] = MathF.Floor(Math.Min(samples[i] + 0.5f, RgbaImage.Max));
        }
    }

    private static void CheckRegion(RgbaImage image, PixelRect region)
    {
        if (region.X < 0 || region.Y < 0 || region.Width < 1 || region.Height < 1
            || region.X + region.Width > image.Width || region.Y + region.Height > image.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), region, $"Not inside the {image.Width} x {image.Height} image.");
        }
    }
}
