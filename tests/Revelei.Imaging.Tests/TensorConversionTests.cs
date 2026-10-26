namespace Revelei.Imaging.Tests;

/// <summary>PB17: images to NCHW tensors and back, exact for every model's value range.</summary>
public sealed class TensorConversionTests
{
    public static TheoryData<float, float> Ranges => new()
    {
        { 0f, 1f },       // Real-ESRGAN
        { -1f, 1f },      // GFPGAN
        { -0.5f, 0.5f },  // RMBG-1.4
    };

    [Theory]
    [MemberData(nameof(Ranges))]
    public void A16BitImageRoundTripsExactly(float low, float high)
    {
        RgbaImage image = Pattern(37, 23, sourceBitDepth: 16);
        float[] tensor = new float[3 * image.Width * image.Height];

        TensorConversion.ToTensor(image, image.Bounds, low, high, tensor);
        RgbaImage back = Pattern(37, 23, sourceBitDepth: 16, fill: false);
        TensorConversion.FromTensor(tensor, low, high, back, back.Bounds);

        AssertRgbEqual(image, back);
    }

    [Fact]
    public void An8BitImageRoundTripsThrough8Bits()
    {
        RgbaImage image = new(4, 1);
        byte[] values = [0, 1, 128, 255];
        for (int x = 0; x < 4; x++)
        {
            image.Row(0)[x * 4] = RgbaImage.From8(values[x]);
        }

        float[] tensor = new float[12];
        TensorConversion.ToTensor(image, image.Bounds, 0f, 1f, tensor);
        RgbaImage back = new(4, 1);
        TensorConversion.FromTensor(tensor, 0f, 1f, back, back.Bounds);

        Assert.Equal(values, Enumerable.Range(0, 4).Select(x => RgbaImage.To8(back.Row(0)[x * 4])));
        Assert.Equal(1f / 255f, tensor[1], 6);
    }

    [Fact]
    public void TheTensorIsPlanarRgbInRowOrder()
    {
        RgbaImage image = new(2, 2);
        Set(image, 0, 0, RgbaImage.Max, 0, 0);
        Set(image, 1, 0, 0, RgbaImage.Max, 0);
        Set(image, 0, 1, 0, 0, RgbaImage.Max);
        float[] tensor = new float[12];

        TensorConversion.ToTensor(image, image.Bounds, 0f, 1f, tensor);

        Assert.Equal([1f, 0f, 0f, 0f], tensor[0..4]);   // R plane
        Assert.Equal([0f, 1f, 0f, 0f], tensor[4..8]);   // G plane
        Assert.Equal([0f, 0f, 1f, 0f], tensor[8..12]);  // B plane
    }

    [Fact]
    public void ARegionIsReadAndWrittenInPlace()
    {
        RgbaImage image = Pattern(10, 8, sourceBitDepth: 16);
        PixelRect tile = new(3, 2, 4, 5);
        float[] tensor = new float[3 * tile.Area];
        TensorConversion.ToTensor(image, tile, 0f, 1f, tensor);

        RgbaImage target = new(10, 8, 16);
        TensorConversion.FromTensor(tensor, 0f, 1f, target, tile);

        Assert.Equal(image.Row(4)[(3 * 4)..(7 * 4)].ToArray().Where((_, i) => i % 4 != 3), target.Row(4)[(3 * 4)..(7 * 4)].ToArray().Where((_, i) => i % 4 != 3));
        Assert.Equal(0, target.Row(0)[0]);
    }

    [Fact]
    public void ValuesOutsideTheRangeAreClamped()
    {
        RgbaImage image = new(9, 1);
        float[] tensor = [.. Enumerable.Repeat(-3f, 9), .. Enumerable.Repeat(7f, 9), .. Enumerable.Repeat(float.NegativeInfinity, 9)];

        TensorConversion.FromTensor(tensor, 0f, 1f, image, image.Bounds);

        for (int x = 0; x < 9; x++)
        {
            Assert.Equal(0, image.Row(0)[x * 4]);
            Assert.Equal(RgbaImage.Max, image.Row(0)[(x * 4) + 1]);
            Assert.Equal(0, image.Row(0)[(x * 4) + 2]);
        }
    }

    [Fact]
    public void AlphaIsLeftAloneByRgbAndCarriedSeparately()
    {
        RgbaImage image = Pattern(5, 3, sourceBitDepth: 16);
        ushort[] alpha = TensorConversion.ExtractAlpha(image);
        float[] tensor = new float[3 * 15];
        TensorConversion.ToTensor(image, image.Bounds, 0f, 1f, tensor);

        TensorConversion.FromTensor(tensor, 0f, 1f, image, image.Bounds);

        Assert.Equal(alpha, TensorConversion.ExtractAlpha(image));
        Assert.True(image.HasTransparency);
    }

    [Fact]
    public void AMaskBecomesAlpha()
    {
        RgbaImage image = new(3, 1);
        float[] mask = [0f, 0.5f, 1f];

        TensorConversion.MaskToAlpha(mask, 0f, 1f, image, image.Bounds);

        Assert.Equal([0, 32768, RgbaImage.Max], TensorConversion.ExtractAlpha(image));
    }

    [Fact]
    public void AGrayImageGivesThreeEqualPlanes()
    {
        RgbaImage image = new(6, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 6; x++)
            {
                ushort v = (ushort)((x + (y * 6)) * 2000);
                Set(image, x, y, v, v, v);
            }
        }

        float[] tensor = new float[3 * 24];
        TensorConversion.ToTensor(image, image.Bounds, -1f, 1f, tensor);

        Assert.Equal(tensor[0..24], tensor[24..48]);
        Assert.Equal(tensor[0..24], tensor[48..72]);
    }

    [Fact]
    public void AWrongTensorLengthOrRegionIsRefused()
    {
        RgbaImage image = new(4, 4);
        Assert.Throws<ArgumentException>(() => TensorConversion.ToTensor(image, image.Bounds, 0f, 1f, new float[47]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TensorConversion.ToTensor(image, new PixelRect(2, 2, 3, 3), 0f, 1f, new float[27]));
    }

    [Fact]
    public void An8BitImageWithFullAlphaIsOpaque() => Assert.False(Opaque(3, 3).HasTransparency);

    private static RgbaImage Opaque(int w, int h)
    {
        RgbaImage image = new(w, h);
        for (int i = 3; i < image.Samples.Length; i += 4)
        {
            image.Samples[i] = RgbaImage.Max;
        }

        return image;
    }

    /// <summary>Every sample a different value, alpha included, spanning the 16-bit range.</summary>
    private static RgbaImage Pattern(int w, int h, int sourceBitDepth, bool fill = true)
    {
        RgbaImage image = new(w, h, sourceBitDepth);
        if (fill)
        {
            Span<ushort> s = image.Samples;
            for (int i = 0; i < s.Length; i++)
            {
                s[i] = (ushort)((i * 7919L) % (RgbaImage.Max + 1));
            }
        }

        return image;
    }

    private static void Set(RgbaImage image, int x, int y, ushort r, ushort g, ushort b)
    {
        Span<ushort> row = image.Row(y);
        row[x * 4] = r;
        row[(x * 4) + 1] = g;
        row[(x * 4) + 2] = b;
        row[(x * 4) + 3] = RgbaImage.Max;
    }

    private static void AssertRgbEqual(RgbaImage expected, RgbaImage actual)
    {
        ReadOnlySpan<ushort> e = expected.Samples;
        ReadOnlySpan<ushort> a = actual.Samples;
        for (int i = 0; i < e.Length; i++)
        {
            if (i % 4 != 3)
            {
                Assert.True(e[i] == a[i], $"sample {i}: expected {e[i]}, got {a[i]}");
            }
        }
    }
}
