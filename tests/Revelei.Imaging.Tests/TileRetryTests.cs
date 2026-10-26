namespace Revelei.Imaging.Tests;

/// <summary>
/// PB21: a tile that runs out of memory is retried smaller, then on the fallback device, and the
/// stitched result is the same as a run that never failed.
/// </summary>
public sealed class TileRetryTests
{
    private static readonly TileOptions Options = new(Scale: 1, Low: 0f, High: 1f, Tile: 64, Overlap: 2);

    [Fact]
    public void AnOutOfMemoryTileIsRetriedAtHalfSizeAndTheResultIsUnchanged()
    {
        RgbaImage image = Pattern(100, 70);
        List<string> log = [];
        List<int> sides = [];
        TileModel limited = (input, w, h) =>
        {
            sides.Add(Math.Max(w, h));
            return w * h > 40 * 40 ? throw new OutOfMemoryException("8007000E") : input;
        };

        RgbaImage result = Tiler.Run(image, limited, Options, retry: new TileRetry(Classify, MinTile: 16, Log: log.Add));

        Assert.Equal(image.Samples.ToArray(), result.Samples.ToArray());
        Assert.Equal(["Out of memory on a 64 px tile; retrying at 32 px."], log);
        Assert.True(sides.Skip(1).All(side => side <= 36), "the smaller size sticks for the rest of the run");
    }

    [Fact]
    public void AtTheFloorItFallsBackOnce()
    {
        RgbaImage image = Pattern(50, 30);
        List<string> log = [];
        List<int> fallbackSides = [];
        TileModel gpu = (_, _, _) => throw new OutOfMemoryException();
        TileModel cpu = (input, w, h) =>
        {
            fallbackSides.Add(Math.Max(w, h));
            return input;
        };

        RgbaImage result = Tiler.Run(image, gpu, Options, retry: new TileRetry(Classify, MinTile: 32, Fallback: cpu, Log: log.Add));

        Assert.Equal(image.Samples.ToArray(), result.Samples.ToArray());
        Assert.Equal(2, log.Count);
        Assert.EndsWith("continuing on the CPU at 64 px.", log[1], StringComparison.Ordinal);
        // The failed core had already been split to 32 px; cores not yet split would run at 64.
        Assert.Equal(34, fallbackSides[0]);
    }

    [Fact]
    public void ALostDeviceGoesStraightToTheFallback()
    {
        List<string> log = [];
        TileModel gpu = (_, _, _) => throw new InvalidOperationException("887A0005 device removed");

        Tiler.Run(Pattern(40, 40), gpu, Options, retry: new TileRetry(Classify, Fallback: (input, _, _) => input, Log: log.Add));

        Assert.Single(log);
        Assert.StartsWith("The GPU was lost", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void WhenEvenTheFallbackRunsOutTheErrorSaysWhatToDo()
    {
        TileModel never = (_, _, _) => throw new OutOfMemoryException();

        InsufficientMemoryException error = Assert.Throws<InsufficientMemoryException>(() =>
            Tiler.Run(Pattern(40, 40), never, Options, retry: new TileRetry(Classify, MinTile: 32, Fallback: never)));

        Assert.Contains("smaller image", error.Message, StringComparison.Ordinal);
        Assert.IsType<OutOfMemoryException>(error.InnerException);
    }

    [Fact]
    public void OtherFailuresAreNotRetried()
    {
        int calls = 0;
        TileModel broken = (_, _, _) =>
        {
            calls++;
            throw new ArgumentException("bad input name");
        };

        Assert.Throws<ArgumentException>(() => Tiler.Run(Pattern(40, 40), broken, Options, retry: new TileRetry(Classify)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void HalvingKeepsTilesAMultipleOfTheModelsStep()
    {
        List<(int W, int H)> seen = [];
        TileModel evenOnly = (input, w, h) =>
        {
            seen.Add((w, h));
            return w * h > 30 * 30 ? throw new OutOfMemoryException() : input;
        };

        Tiler.Run(Pattern(60, 44), evenOnly, Options with { Multiple = 2, Tile = 50 }, retry: new TileRetry(Classify, MinTile: 8));

        Assert.All(seen, size => Assert.True(size.W % 2 == 0 && size.H % 2 == 0, $"{size.W} x {size.H}"));
    }

    private static TileFailure Classify(Exception failure) => failure switch
    {
        OutOfMemoryException => TileFailure.OutOfMemory,
        InvalidOperationException e when e.Message.Contains("887A0005", StringComparison.Ordinal) => TileFailure.DeviceLost,
        _ => TileFailure.Other,
    };

    private static RgbaImage Pattern(int w, int h)
    {
        RgbaImage image = new(w, h, 16);
        Span<ushort> s = image.Samples;
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = i % 4 == 3 ? RgbaImage.Max : (ushort)((i * 7919L) % (RgbaImage.Max + 1));
        }

        return image;
    }
}
