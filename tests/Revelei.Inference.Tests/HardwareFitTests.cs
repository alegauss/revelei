namespace Revelei.Inference.Tests;

/// <summary>
/// PB72: the verdict per model and quantisation over fixed machine profiles, so it is decided by
/// numbers a test can hold and not by the desk the suite runs on.
/// </summary>
public sealed class HardwareFitTests
{
    private const long GB = 1L << 30;

    // The PB14 desk: an RTX 4060 with 8 GB, 64 GB of RAM.
    private static readonly MachineProfile Rtx4060 = new(8 * GB, 64 * GB, 500 * GB);

    private static readonly MachineProfile Rtx4090 = new(24 * GB, 64 * GB, 500 * GB);

    private static readonly MachineProfile NoGpu = new(0, 64 * GB, 500 * GB);

    [Fact]
    public void AModelThatFitsInVideoMemoryWithAMarginRunsWell()
    {
        FitVerdict verdict = HardwareFit.Judge(Quantisation(6 * GB, 4 * GB), Rtx4060);

        Assert.Equal(Fit.RunsWell, verdict.Fit);
        Assert.Contains("8 GB", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelLargerThanVideoMemoryRunsSlowerAndSaysRoughlyHowMuch()
    {
        FitVerdict verdict = HardwareFit.Judge(Quantisation(12 * GB, 6 * GB), Rtx4060);

        Assert.Equal(Fit.RunsSlower, verdict.Fit);
        Assert.Equal(2, verdict.Slowdown);
        Assert.Contains("roughly 2 times slower", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoGpuAModelWithEnoughRamIsCpuOnly() =>
        Assert.Equal(Fit.CpuOnly, HardwareFit.Judge(Quantisation(6 * GB, 4 * GB), NoGpu).Fit);

    [Fact]
    public void ShortOfRamOrDiskDoesNotFitAndNamesWhatIsShort()
    {
        FitVerdict ram = HardwareFit.Judge(Quantisation(20 * GB, 10 * GB), new MachineProfile(8 * GB, 16 * GB, 500 * GB));
        FitVerdict disk = HardwareFit.Judge(Quantisation(6 * GB, 4 * GB), Rtx4060 with { FreeDiskBytes = GB });

        Assert.Equal((Fit.DoesNotFit, Fit.DoesNotFit), (ram.Fit, disk.Fit));
        Assert.Contains("RAM", ram.Reason, StringComparison.Ordinal);
        Assert.Contains("disk", disk.Reason, StringComparison.Ordinal);
        Assert.Equal(Fit.RunsWell, HardwareFit.Judge(Quantisation(6 * GB, 4 * GB), Rtx4060 with { FreeDiskBytes = GB }, installed: true).Fit);
    }

    [Fact]
    public void TodaysSmallOnnxModelsWithNoMemoryListedRunWell()
    {
        ManifestQuantisation fp32 = new([new ManifestPart("weights", new ManifestFile("https://host/m.onnx", 67 << 20, "00"))]);

        Assert.Equal(Fit.RunsWell, HardwareFit.Judge(fp32, Rtx4060).Fit);
    }

    [Fact]
    public void TheRecommendationIsTheLargestThatRunsWellElseTheLeastOffloaded()
    {
        ManifestModel model = Model(("fp16", 20 * GB, 8 * GB), ("q8", 12 * GB, 6 * GB), ("q4", 6 * GB, 4 * GB));

        Assert.Equal("q4", HardwareFit.Recommend(model, Rtx4060)?.Quantisation);
        Assert.Equal("fp16", HardwareFit.Recommend(model, Rtx4090)?.Quantisation);
        Assert.Equal("q8", HardwareFit.Recommend(Model(("fp16", 20 * GB, 8 * GB), ("q8", 12 * GB, 6 * GB)), Rtx4060)?.Quantisation);
        Assert.Equal((Fit.CpuOnly, "q4"), (HardwareFit.Recommend(model, NoGpu)!.Value.Verdict.Fit, HardwareFit.Recommend(model, NoGpu)!.Value.Quantisation));
        Assert.Null(HardwareFit.Recommend(model, new MachineProfile(0, 4 * GB, 500 * GB)));
    }

    [Fact]
    public void ThisPcIsMeasuredWithoutTheModelsFolderExisting()
    {
        MachineProfile here = MachineProfile.Measure(Path.Combine(Path.GetTempPath(), "pb-fit-" + Guid.NewGuid().ToString("N")));

        Assert.True(here.SystemBytes > GB);
        Assert.True(here.FreeDiskBytes > 0);
        Assert.True(here.VideoBytes >= 0);
    }

    private static ManifestQuantisation Quantisation(long video, long system, long disk = 0) =>
        new([new ManifestPart("weights", new ManifestFile("https://host/w", disk == 0 ? video / 2 : disk, "00"))], new ManifestMemory(video, system));

    private static ManifestModel Model(params (string Name, long Video, long System)[] quantisations) =>
        new(
            "tiny-diffusion",
            new ManifestFile("https://host/meta.json", 1, "00"),
            new Dictionary<string, ManifestFile>(),
            "test",
            "1",
            ModelKind.Diffusion,
            Quantisations: quantisations.ToDictionary(q => q.Name, q => Quantisation(q.Video, q.System)));
}
