namespace Revelei.Imaging;

/// <summary>A point in pixel coordinates, where pixel (0, 0)'s centre is (0, 0), as in OpenCV.</summary>
/// <param name="X">Across.</param>
/// <param name="Y">Down.</param>
public readonly record struct PointD(double X, double Y);

/// <summary>A face a detector found (PB30).</summary>
/// <param name="X">The box's left edge.</param>
/// <param name="Y">The box's top edge.</param>
/// <param name="Width">The box's width.</param>
/// <param name="Height">The box's height.</param>
/// <param name="Score">The detector's confidence, 0 to 1.</param>
/// <param name="Landmarks">
/// Five points: the eye on the image's left, the other eye, the nose tip, and the mouth corners,
/// left first. The order <see cref="FaceAlignment.Ffhq512"/> expects.
/// </param>
public sealed record Face(double X, double Y, double Width, double Height, double Score, IReadOnlyList<PointD> Landmarks);

/// <summary>
/// A rotation, uniform scale and translation: (x, y) goes to (A x - B y + Tx, B x + A y + Ty).
/// What a face is aligned by, so it never shears or squashes the face.
/// </summary>
/// <param name="A">Scale times the cosine of the rotation.</param>
/// <param name="B">Scale times its sine.</param>
/// <param name="Tx">Translation across.</param>
/// <param name="Ty">Translation down.</param>
public readonly record struct Similarity(double A, double B, double Tx, double Ty)
{
    /// <summary>How much larger it makes things.</summary>
    public double Scale => Math.Sqrt((A * A) + (B * B));

    /// <summary>Where a point goes.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The point moved.</returns>
    public PointD Apply(PointD point) => new((A * point.X) - (B * point.Y) + Tx, (B * point.X) + (A * point.Y) + Ty);

    /// <summary>The transform that undoes this one.</summary>
    /// <returns>The inverse.</returns>
    public Similarity Inverse()
    {
        double d = (A * A) + (B * B);
        double a = A / d;
        double b = -B / d;
        return new(a, b, -((a * Tx) - (b * Ty)), -((b * Tx) + (a * Ty)));
    }
}

/// <summary>
/// Face alignment for GFPGAN (PB30), after facexlib's FaceRestoreHelper: five landmarks are fitted
/// to the FFHQ 512 template by a similarity, the face is warped into a 512 x 512 crop, and a
/// restored crop is pasted back through the inverse with a feathered mask, so its square edge
/// never shows. Pure arithmetic over <see cref="RgbaImage"/>; the detector lives in Inference.
/// </summary>
public static class FaceAlignment
{
    /// <summary>The side of the crop GFPGAN takes.</summary>
    public const int CropSize = 512;

    /// <summary>
    /// facexlib's five-point template for a 512 crop (face_template * 512 / 512 in its helper):
    /// the eyes, the nose tip and the mouth corners, in the order <see cref="Face.Landmarks"/> uses.
    /// </summary>
    public static IReadOnlyList<PointD> Ffhq512 { get; } =
    [
        new(192.98138, 239.94708),
        new(318.90277, 240.1936),
        new(256.63416, 314.01935),
        new(201.26117, 371.41043),
        new(313.08905, 371.15118),
    ];

    /// <summary>
    /// The least-squares similarity taking one point set onto another, in closed form: the two
    /// centroids line up, and the rotation and scale are the ones that best match what remains.
    /// </summary>
    /// <param name="from">The points where they are, e.g. a face's landmarks.</param>
    /// <param name="to">Where they should go, e.g. <see cref="Ffhq512"/>.</param>
    /// <returns>The transform.</returns>
    public static Similarity Estimate(IReadOnlyList<PointD> from, IReadOnlyList<PointD> to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Count != to.Count || from.Count < 2)
        {
            throw new ArgumentException($"Needs two matching sets of at least two points, not {from.Count} and {to.Count}.", nameof(to));
        }

        (double fx, double fy) = Centroid(from);
        (double tx, double ty) = Centroid(to);
        double dot = 0;
        double cross = 0;
        double norm = 0;
        for (int i = 0; i < from.Count; i++)
        {
            double x = from[i].X - fx;
            double y = from[i].Y - fy;
            double u = to[i].X - tx;
            double v = to[i].Y - ty;
            dot += (x * u) + (y * v);
            cross += (x * v) - (y * u);
            norm += (x * x) + (y * y);
        }

        if (norm == 0)
        {
            throw new ArgumentException("The points are all in one place.", nameof(from));
        }

        double a = dot / norm;
        double b = cross / norm;
        return new(a, b, tx - ((a * fx) - (b * fy)), ty - ((b * fx) + (a * fy)));
    }

    /// <summary>The similarity that warps a face onto the FFHQ template of a crop this size.</summary>
    /// <param name="face">The face.</param>
    /// <param name="size">The crop's side.</param>
    /// <returns>Image to crop coordinates.</returns>
    public static Similarity ToCrop(Face face, int size = CropSize)
    {
        ArgumentNullException.ThrowIfNull(face);
        double k = size / (double)CropSize;
        return Estimate(face.Landmarks, [.. Ffhq512.Select(p => new PointD(p.X * k, p.Y * k))]);
    }

    /// <summary>
    /// The aligned crop: each crop pixel read bilinearly from the image through the inverse.
    /// Beyond the image's edge the nearest edge pixel is repeated.
    /// </summary>
    /// <param name="image">The photo.</param>
    /// <param name="toCrop">Image to crop coordinates, from <see cref="ToCrop"/>.</param>
    /// <param name="size">The crop's side.</param>
    /// <returns>A size x size image.</returns>
    public static RgbaImage Crop(RgbaImage image, Similarity toCrop, int size = CropSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        Similarity back = toCrop.Inverse();
        RgbaImage crop = new(size, size, image.SourceBitDepth);
        Span<float> pixel = stackalloc float[4];
        for (int y = 0; y < size; y++)
        {
            Span<ushort> row = crop.Row(y);
            for (int x = 0; x < size; x++)
            {
                Sample(image, back.Apply(new(x, y)), pixel);
                for (int c = 0; c < 4; c++)
                {
                    row[(x * 4) + c] = (ushort)MathF.Round(pixel[c]);
                }
            }
        }

        return crop;
    }

    /// <summary>
    /// Blends a (restored) crop back into the photo where <see cref="Crop"/> took it from. Colour
    /// only: the photo's alpha is its own. The crop's outer sixteenth fades out, so the seam is a
    /// gradient rather than a square.
    /// </summary>
    /// <param name="image">The photo, changed in place.</param>
    /// <param name="crop">The crop, square.</param>
    /// <param name="toCrop">The transform it was cropped with.</param>
    /// <param name="strength">How much of the crop shows where the mask is whole, 0 to 1 (PB31's blend).</param>
    public static void PasteBack(RgbaImage image, RgbaImage crop, Similarity toCrop, double strength = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(crop);
        ArgumentOutOfRangeException.ThrowIfLessThan(strength, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(strength, 1);
        int size = crop.Width;
        if (crop.Height != size)
        {
            throw new ArgumentException($"A crop is square, not {crop.Width} x {crop.Height}.", nameof(crop));
        }

        PixelRect area = Footprint(image, toCrop.Inverse(), size);
        Span<float> pixel = stackalloc float[4];
        for (int y = area.Y; y < area.Y + area.Height; y++)
        {
            Span<ushort> row = image.Row(y);
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                PointD at = toCrop.Apply(new(x, y));
                double weight = MaskWeight(at, size) * strength;
                if (weight <= 0)
                {
                    continue;
                }

                Sample(crop, at, pixel);
                for (int c = 0; c < 3; c++)
                {
                    int i = (x * 4) + c;
                    row[i] = (ushort)Math.Round((row[i] * (1 - weight)) + (pixel[c] * weight));
                }
            }
        }
    }

    /// <summary>
    /// How much of the crop shows at a crop point: 1 inside, 0 outside, and a smoothstep across the
    /// outer sixteenth of the side (32 px at 512), facexlib's eroded and blurred mask in closed form.
    /// </summary>
    /// <param name="at">The point in crop coordinates.</param>
    /// <param name="size">The crop's side.</param>
    /// <returns>0 to 1.</returns>
    public static double MaskWeight(PointD at, int size)
    {
        double edge = Math.Min(Math.Min(at.X, at.Y), Math.Min(size - 1 - at.X, size - 1 - at.Y));
        double t = Math.Clamp(edge / (size / 16.0), 0, 1);
        return t * t * (3 - (2 * t));
    }

    private static (double X, double Y) Centroid(IReadOnlyList<PointD> points) =>
        (points.Average(p => p.X), points.Average(p => p.Y));

    // The image pixels a crop's square covers, clipped to the image.
    private static PixelRect Footprint(RgbaImage image, Similarity back, int size)
    {
        PointD[] corners = [back.Apply(new(0, 0)), back.Apply(new(size - 1, 0)), back.Apply(new(0, size - 1)), back.Apply(new(size - 1, size - 1))];
        int left = Math.Max(0, (int)Math.Floor(corners.Min(p => p.X)));
        int top = Math.Max(0, (int)Math.Floor(corners.Min(p => p.Y)));
        int right = Math.Min(image.Width - 1, (int)Math.Ceiling(corners.Max(p => p.X)));
        int bottom = Math.Min(image.Height - 1, (int)Math.Ceiling(corners.Max(p => p.Y)));
        return right < left || bottom < top ? default : new(left, top, right - left + 1, bottom - top + 1);
    }

    // Bilinear, all four samples, with the edge repeated beyond the image.
    private static void Sample(RgbaImage image, PointD at, Span<float> into)
    {
        double sx = Math.Clamp(at.X, 0, image.Width - 1);
        double sy = Math.Clamp(at.Y, 0, image.Height - 1);
        int x0 = (int)sx;
        int y0 = (int)sy;
        int x1 = Math.Min(x0 + 1, image.Width - 1);
        int y1 = Math.Min(y0 + 1, image.Height - 1);
        float fx = (float)(sx - x0);
        float fy = (float)(sy - y0);
        ReadOnlySpan<ushort> top = image.Row(y0);
        ReadOnlySpan<ushort> bottom = image.Row(y1);
        for (int c = 0; c < 4; c++)
        {
            float a = (top[(x0 * 4) + c] * (1 - fx)) + (top[(x1 * 4) + c] * fx);
            float b = (bottom[(x0 * 4) + c] * (1 - fx)) + (bottom[(x1 * 4) + c] * fx);
            into[c] = (a * (1 - fy)) + (b * fy);
        }
    }
}
