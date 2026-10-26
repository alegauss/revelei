using Microsoft.ML.OnnxRuntime;
using Revelei.Imaging;
using Revelei.Inference.Pipelines;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB19: the upscale pipeline end to end through ONNX Runtime. The probe model is itself a 2x
/// conv-resize-conv net, so it stands in for realesrgan-x2plus in a temporary models folder; the
/// real x4plus runs too when export-and-quantize.py has left it in .cache/models.
/// </summary>
public sealed class UpscaleTests : IDisposable
{
    private static readonly ProviderSelection Cpu = new(ExecutionProvider.Cpu, null, null);
    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-upscale-" + Guid.NewGuid().ToString("N"));

    public UpscaleTests()
    {
        string folder = Path.Combine(root, Upscale.X2);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "fp32.onnx"), ProbeModel.Build());
        string meta = File.ReadAllText(Path.Combine(Repository.Root, "scripts", "meta.example.json"))
            .Replace("\"realesrgan-x4plus\"", "\"realesrgan-x2plus\"", StringComparison.Ordinal)
            .Replace("\"scale\": 4", "\"scale\": 2", StringComparison.Ordinal)
            .Replace("\"name\": \"input\"", "\"name\": \"x\"", StringComparison.Ordinal)
            .Replace("\"name\": \"output\"", "\"name\": \"y\"", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(folder, "meta.json"), meta);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData(8192, 8192, 2, true)]
    [InlineData(8192, 8192, 4, false)]
    [InlineData(4100, 4100, 4, false)]
    [InlineData(4096, 4096, 4, true)]
    [InlineData(65536, 65536, 4, false)]
    public void AnUpscalePastSixteenThousandPixelsASideIsRefused(int width, int height, int factor, bool fits)
    {
        // PB60: 8192 px at 4x is 32768 px, which overflowed the sample array and closed the app.
        Assert.Equal(fits, Upscale.Fits(width, height, factor));
    }

    [Fact]
    public void TiledUpscaleMatchesOneWholeImageRun()
    {
        using ModelLibrary library = new(root, Cpu);
        RgbaImage image = Pattern(45, 31, opaque: true);

        RgbaImage tiled = Upscale.Run(library, image, 2, "fp32", tile: 16);
        RgbaImage whole = Upscale.Run(library, image, 2, "fp32", tile: 4096);

        Assert.Equal((90, 62), (tiled.Width, tiled.Height));
        Assert.True(MaxDifference(tiled, whole) <= 1, $"tiles differ from a whole run by {MaxDifference(tiled, whole)}");
    }

    [Fact]
    public void ProgressReportsEveryTile()
    {
        using ModelLibrary library = new(root, Cpu);
        List<(int, int)> reports = [];

        Upscale.Run(library, Pattern(40, 20, opaque: true), 2, "fp32", tile: 20, progress: (done, total) => reports.Add((done, total)));

        Assert.Equal([(1, 2), (2, 2)], reports);
    }

    [Fact]
    public void TransparencyIsResizedBesideTheModel()
    {
        using ModelLibrary library = new(root, Cpu);
        RgbaImage image = new(4, 2);
        for (int x = 0; x < 4; x++)
        {
            image.Row(0)[(x * 4) + 3] = RgbaImage.Max;
            image.Row(1)[(x * 4) + 3] = 0;
        }

        RgbaImage result = Upscale.Run(library, image, 2, "fp32");

        Assert.Equal(RgbaImage.Max, result.Row(0)[3]);
        Assert.Equal(0, result.Row(3)[3]);
        Assert.InRange(result.Row(1)[3], RgbaImage.Max / 2, RgbaImage.Max - 1);
    }

    [Fact]
    public async Task AQueuedUpscaleReportsTilesUnderItsStageAndFinishes()
    {
        // PB20: the job the operations panel will start, through the queue with tile progress.
        using ModelLibrary library = new(root, Cpu);
        await using InferenceService service = new(TimeSpan.Zero);
        List<JobProgress> seen = [];
        SyncProgress progress = new(seen);

        InferenceJob<RgbaImage> job = UpscaleJob.Enqueue(service, library, Pattern(300, 40, opaque: true), 2, "fp32", progress);
        RgbaImage result = await job.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal((600, 80), (result.Width, result.Height));
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(UpscaleJob.Stage, p.Stage));
        Assert.Equal(seen[^1].Total, seen[^1].Done);
        Assert.Equal(TimeSpan.Zero, seen[^1].Remaining);
    }

    [Fact]
    public async Task AQueuedUpscaleIsLoggedWithItsTilesProviderAndTime()
    {
        // PB22: the record a bug report and the README table read.
        using ModelLibrary library = new(root, Cpu);
        await using InferenceService service = new();
        OperationLog log = new(Path.Combine(root, "logs"));

        await UpscaleJob.Enqueue(service, library, Pattern(300, 40, opaque: true), 2, "fp32", log: log)
            .Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        OperationRecord record = Assert.Single(log.ReadCurrent());
        Assert.Equal(("upscale x2", Upscale.X2, "fp32", "Cpu", "ok"), (record.Operation, record.Model, record.Variant, record.Provider, record.Outcome));
        // The starting tile comes from the memory free on this desk (PB41); the count follows from it.
        Assert.Equal((300, 40), (record.Width, record.Height));
        Assert.InRange(record.Tile, TileSizer.MinTile, TileSizer.MaxTile);
        Assert.Equal((int)Math.Ceiling(300.0 / record.Tile) * (int)Math.Ceiling(40.0 / record.Tile), record.Tiles);
        Assert.True(record.Milliseconds > 0);
        Assert.Empty(record.Retries);
    }

    [Fact]
    public async Task ACancelledUpscaleIsLoggedAsCancelled()
    {
        using ModelLibrary library = new(root, Cpu);
        await using InferenceService service = new(TimeSpan.Zero);
        OperationLog log = new(Path.Combine(root, "logs"));
        InferenceJob<RgbaImage>? job = null;
        CancelOnFirst progress = new(() => job!.Cancel());

        job = UpscaleJob.Enqueue(service, library, Pattern(600, 40, opaque: true), 2, "fp32", progress, log);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        Assert.Equal("cancelled", Assert.Single(log.ReadCurrent()).Outcome);
    }

    [Fact]
    public void FactorsOtherThanTwoAndFourAreRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Upscale.ModelFor(3));

    [Fact]
    public void ARealX4ModelUpscalesTheTestImageAcrossTileSeams()
    {
        string models = Path.Combine(Repository.Root, ".cache", "models");
        string image = Path.Combine(Repository.Root, ".cache", "weights", "test-images", "0014.jpg");
        if (!File.Exists(Path.Combine(models, Upscale.X4, "fp32.onnx")) || !File.Exists(image))
        {
            Assert.Skip("needs scripts/download_models.py and scripts/export-and-quantize.py to have run");
        }

        using ModelLibrary library = new(models, Cpu);
        RgbaImage source = ImageDecoder.Decode(image);

        RgbaImage tiled = Upscale.Run(library, source, 4, "fp32", tile: 64);
        RgbaImage whole = Upscale.Run(library, source, 4, "fp32", tile: 4096);

        Assert.Equal((source.Width * 4, source.Height * 4), (tiled.Width, tiled.Height));
        // RRDBNet sees further than any practical overlap, so seams are close rather than exact:
        // 45.0 dB at the default 32 px when measured, 36.2 dB at the tiler's generic 16.
        Assert.True(Psnr(tiled, whole) > 42, $"tiled vs whole: {Psnr(tiled, whole):F1} dB");
    }

    private sealed class SyncProgress(List<JobProgress> seen) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => seen.Add(value);
    }

    private sealed class CancelOnFirst(Action cancel) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => cancel();
    }

    private static RgbaImage Pattern(int w, int h, bool opaque)
    {
        RgbaImage image = new(w, h, 16);
        Span<ushort> s = image.Samples;
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = opaque && i % 4 == 3 ? RgbaImage.Max : (ushort)((i * 7919L) % (RgbaImage.Max + 1));
        }

        return image;
    }

    private static int MaxDifference(RgbaImage a, RgbaImage b)
    {
        int most = 0;
        for (int i = 0; i < a.Samples.Length; i++)
        {
            most = Math.Max(most, Math.Abs(a.Samples[i] - b.Samples[i]));
        }

        return most;
    }

    private static double Psnr(RgbaImage a, RgbaImage b)
    {
        double sum = 0;
        int count = 0;
        for (int i = 0; i < a.Samples.Length; i++)
        {
            if (i % 4 != 3)
            {
                double d = (a.Samples[i] - b.Samples[i]) / (double)RgbaImage.Max;
                sum += d * d;
                count++;
            }
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(count / sum);
    }
}
