namespace Revelei.Imaging;

/// <summary>A rectangle of pixels.</summary>
/// <param name="X">Left column.</param>
/// <param name="Y">Top row.</param>
/// <param name="Width">Columns.</param>
/// <param name="Height">Rows.</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>Pixels in it.</summary>
    public int Area => Width * Height;
}

/// <summary>
/// An image as every pipeline sees it (PB17): interleaved R, G, B, A, each sample scaled to the
/// full 16-bit range whatever the source depth, so an 8-bit JPEG and a 16-bit PNG take one path
/// and neither loses precision. Eight bytes a pixel: a 12 MP photo is 96 MB.
/// </summary>
public sealed class RgbaImage
{
    /// <summary>The largest sample value.</summary>
    public const ushort Max = ushort.MaxValue;

    private readonly ushort[] samples;

    /// <summary>A transparent black image.</summary>
    /// <param name="width">Columns.</param>
    /// <param name="height">Rows.</param>
    /// <param name="sourceBitDepth">The depth the pixels came from, 8 or 16, which an encoder keeps.</param>
    public RgbaImage(int width, int height, int sourceBitDepth = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (sourceBitDepth is not (8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceBitDepth), sourceBitDepth, "8 or 16.");
        }

        Width = width;
        Height = height;
        SourceBitDepth = sourceBitDepth;
        samples = new ushort[checked(width * height * 4)];
    }

    /// <summary>Columns.</summary>
    public int Width { get; }

    /// <summary>Rows.</summary>
    public int Height { get; }

    /// <summary>8 or 16: what the decoder read, and what an encoder should write back.</summary>
    public int SourceBitDepth { get; }

    /// <summary>The whole image as a rectangle.</summary>
    public PixelRect Bounds => new(0, 0, Width, Height);

    /// <summary>Every sample, row by row, RGBA.</summary>
    public Span<ushort> Samples => samples;

    /// <summary>One row's samples.</summary>
    /// <param name="y">The row.</param>
    /// <returns>Width x 4 samples.</returns>
    public Span<ushort> Row(int y) => samples.AsSpan(y * Width * 4, Width * 4);

    /// <summary>Whether any pixel is less than fully opaque, i.e. alpha must be carried through.</summary>
    public bool HasTransparency
    {
        get
        {
            ReadOnlySpan<ushort> all = samples;
            for (int i = 3; i < all.Length; i += 4)
            {
                if (all[i] != Max)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>An 8-bit sample scaled to 16 bits exactly (0 to 0, 255 to 65535).</summary>
    /// <param name="value">The 8-bit sample.</param>
    /// <returns>The 16-bit sample.</returns>
    public static ushort From8(byte value) => (ushort)(value * 257);

    /// <summary>A 16-bit sample rounded to 8 bits.</summary>
    /// <param name="value">The 16-bit sample.</param>
    /// <returns>The 8-bit sample.</returns>
    public static byte To8(ushort value) => (byte)((value + 128) / 257);
}
