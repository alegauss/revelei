namespace Revelei.Imaging.Tests;

/// <summary>
/// PB18: tiling then stitching gives exactly what one whole-image run gives, edge tiles of odd
/// sizes included, as long as the model looks no further than the overlap.
/// </summary>
public sealed class TilerTests
{
    [Theory]
    [InlineData(37, 23, 8, 2)]
    [InlineData(64, 64, 16, 4)]
    [InlineData(5, 3, 256, 16)]
    [InlineData(17, 1, 4, 0)]
    public void TheCoresPartitionTheImage(int width, int height, int tile, int overlap)
    {
        IReadOnlyList<Tile> tiles = Tiler.Plan(width, height, tile, overlap);
        int[] owned = new int[width * height];
        foreach (Tile t in tiles)
        {
            Assert.True(t.Input.X <= t.Core.X && t.Input.Y <= t.Core.Y);
            Assert.True(t.Input.X + t.Input.Width >= t.Core.X + t.Core.Width);
            Assert.True(t.Input.X + t.Input.Width <= width && t.Input.Y + t.Input.Height <= height);
            for (int y = t.Core.Y; y < t.Core.Y + t.Core.Height; y++)
            {
                for (int x = t.Core.X; x < t.Core.X + t.Core.Width; x++)
                {
                    owned[(y * width) + x]++;
                }
            }
        }

        Assert.All(owned, count => Assert.Equal(1, count));
    }

    [Fact]
    public void AnIdentityModelReproducesTheImageExactly()
    {
        RgbaImage image = Pattern(37, 23);

        RgbaImage result = Tiler.Run(image, (input, _, _) => input, new TileOptions(Scale: 1, Low: 0f, High: 1f, Tile: 8, Overlap: 3));

        Assert.Equal(image.Samples.ToArray(), result.Samples.ToArray());
    }

    [Fact]
    public void ABlurThatReadsNeighboursMatchesAWholeImageRunWhenOverlapCoversIt()
    {
        RgbaImage image = Pattern(29, 19);
        TileOptions options = new(Scale: 1, Low: 0f, High: 1f, Tile: 7, Overlap: 1);

        RgbaImage tiled = Tiler.Run(image, Blur3, options);
        RgbaImage whole = Tiler.Run(image, Blur3, options with { Tile = 1000 });

        Assert.Equal(whole.Samples.ToArray(), tiled.Samples.ToArray());
    }

    [Fact]
    public void ABlurWithoutOverlapShowsSeamsSoTheOverlapIsWhatMatters()
    {
        RgbaImage image = Pattern(29, 19);
        TileOptions options = new(Scale: 1, Low: 0f, High: 1f, Tile: 7, Overlap: 0);

        RgbaImage tiled = Tiler.Run(image, Blur3, options);
        RgbaImage whole = Tiler.Run(image, Blur3, options with { Tile = 1000 });

        Assert.NotEqual(whole.Samples.ToArray(), tiled.Samples.ToArray());
    }

    [Fact]
    public void ANearestX4UpscaleIsStitchedExactly()
    {
        RgbaImage image = Pattern(13, 9);

        RgbaImage result = Tiler.Run(image, Nearest(4), new TileOptions(Scale: 4, Low: -1f, High: 1f, Tile: 4, Overlap: 2));

        Assert.Equal((52, 36), (result.Width, result.Height));
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                Assert.Equal(image.Row(y / 4).Slice((x / 4) * 4, 4).ToArray(), result.Row(y).Slice(x * 4, 4).ToArray());
            }
        }
    }

    [Fact]
    public void AnOddImageIsPaddedForAModelThatNeedsEvenSidesAndCroppedBack()
    {
        RgbaImage image = Pattern(37, 23);
        List<(int W, int H)> seen = [];

        RgbaImage result = Tiler.Run(
            image,
            (input, w, h) =>
            {
                seen.Add((w, h));
                return Nearest(2)(input, w, h);
            },
            new TileOptions(Scale: 2, Low: 0f, High: 1f, Tile: 9, Overlap: 3, Multiple: 2));

        Assert.All(seen, size => Assert.True(size.W % 2 == 0 && size.H % 2 == 0, $"{size.W} x {size.H}"));
        Assert.Equal((74, 46), (result.Width, result.Height));
        Assert.Equal(image.Row(22).Slice(36 * 4, 4).ToArray(), result.Row(45).Slice(73 * 4, 4).ToArray());
    }

    [Fact]
    public void ProgressCountsEveryTileAndCanStopTheRun()
    {
        RgbaImage image = Pattern(20, 20);
        List<(int Done, int Total)> reports = [];

        Assert.Throws<OperationCanceledException>(() => Tiler.Run(
            image,
            (input, _, _) => input,
            new TileOptions(Scale: 1, Low: 0f, High: 1f, Tile: 10, Overlap: 2),
            (done, total) =>
            {
                reports.Add((done, total));
                if (done == 2)
                {
                    throw new OperationCanceledException();
                }
            }));

        Assert.Equal([(1, 4), (2, 4)], reports);
    }

    [Fact]
    public void AModelReturningTheWrongSizeIsReported() =>
        Assert.Throws<InvalidOperationException>(() =>
            Tiler.Run(Pattern(8, 8), (input, _, _) => input, new TileOptions(Scale: 2, Low: 0f, High: 1f)));

    /// <summary>A 3 x 3 box blur per channel, clamping at the input's edges, like a real conv's padding.</summary>
    private static float[] Blur3(float[] input, int width, int height)
    {
        float[] output = new float[input.Length];
        for (int c = 0; c < 3; c++)
        {
            int plane = c * width * height;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int sx = Math.Clamp(x + dx, 0, width - 1);
                            int sy = Math.Clamp(y + dy, 0, height - 1);
                            sum += input[plane + (sy * width) + sx];
                        }
                    }

                    output[plane + (y * width) + x] = sum / 9f;
                }
            }
        }

        return output;
    }

    private static TileModel Nearest(int scale) => (input, width, height) =>
    {
        int ow = width * scale;
        int oh = height * scale;
        float[] output = new float[3 * ow * oh];
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < oh; y++)
            {
                for (int x = 0; x < ow; x++)
                {
                    output[(c * ow * oh) + (y * ow) + x] = input[(c * width * height) + ((y / scale) * width) + (x / scale)];
                }
            }
        }

        return output;
    };

    private static RgbaImage Pattern(int w, int h)
    {
        RgbaImage image = new(w, h, 16);
        Span<ushort> s = image.Samples;
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = (ushort)((i * 7919L) % (RgbaImage.Max + 1));
        }

        return image;
    }
}
