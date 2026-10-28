namespace Revelei.Inference.Tests;

/// <summary>
/// PB76: a model's speed on this PC is the median of its recent successful runs per megapixel,
/// read from the operation log, and it replaces the estimate on its card.
/// </summary>
public sealed class MeasuredSpeedTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheMedianOfTheRecentRunsIsTakenSoOneSlowFirstRunDoesNotSetIt()
    {
        // 1 MP each: 10 s for the first run, which built the engine, then about 2 s.
        OperationRecord[] runs = [Run(0, 10_000), Run(1, 2_000), Run(2, 2_200), Run(3, 1_800)];

        Measured measured = Assert.Single(MeasuredSpeed.From(runs));

        Assert.Equal(("realesrgan-x4plus", "fp16", "DirectML", 4), (measured.Model, measured.Variant, measured.Provider, measured.Runs));
        Assert.Equal(2.1, measured.SecondsPerMegapixel, 3);
        Assert.Equal(Start.AddMinutes(3), measured.At);
    }

    [Fact]
    public void OnlyTheLastFewSuccessfulRunsCountAndEachProviderIsItsOwn()
    {
        OperationRecord[] runs =
        [
            .. Enumerable.Range(0, 10).Select(i => Run(i, i < 5 ? 9_000 : 1_000)),
            Run(20, 50, outcome: "cancelled"),
            Run(21, 30_000, provider: "Cpu"),
        ];

        IReadOnlyList<Measured> measured = MeasuredSpeed.From(runs);

        Assert.Equal(1.0, Assert.Single(measured, m => m.Provider == "DirectML").SecondsPerMegapixel, 3);
        Assert.Equal(MeasuredSpeed.Window, Assert.Single(measured, m => m.Provider == "DirectML").Runs);
        Assert.Equal("Cpu", MeasuredSpeed.For(measured, "realesrgan-x4plus")!.Provider);
        Assert.Null(MeasuredSpeed.For(measured, "gfpgan-v1.4"));
    }

    [Fact]
    public void TheCardShowsTheMeasurementInsteadOfTheEstimate()
    {
        IReadOnlyList<Measured> measured = MeasuredSpeed.From([Run(0, 1_500)]);

        ModelCard card = Assert.Single(
            ModelCards.Build(ModelDownloads.Embedded, Path.Combine(Path.GetTempPath(), "pb-none-" + Guid.NewGuid().ToString("N")), null, new MachineProfile(8L << 30, 64L << 30, 500L << 30), measured),
            c => c.Name == "realesrgan-x4plus");

        Assert.Same(measured[0], card.Measured);
        Assert.Matches(@"^Measured here: 1[.,]5 s per megapixel on DirectML, over 1 run\.$", MeasuredSpeed.Line(card.Measured!));
    }

    private static OperationRecord Run(int minute, double milliseconds, string provider = "DirectML", string outcome = "ok") =>
        new(Start.AddMinutes(minute), "upscale x4", "realesrgan-x4plus", "fp16", provider, null, 1000, 1000, 512, 4, [], milliseconds, outcome);
}
