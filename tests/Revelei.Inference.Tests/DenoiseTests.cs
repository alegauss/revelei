using Revelei.Imaging;
using Revelei.Inference.Pipelines;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB36: SCUNet through the shared tiling. The real model, when export-and-quantize.py has left
/// it, denoises a crop of its own noisy test photo, and its tiles are held against one whole run.
/// </summary>
public sealed class DenoiseTests : IDisposable
{
    private static readonly ProviderSelection Cpu = new(ExecutionProvider.Cpu, null, null);
    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-denoise-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AModelThatChangesTheSizeIsRefused()
    {
        // The probe is a 2x net; under the denoiser's name it must not be run.
        string folder = Path.Combine(root, Denoise.Model);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "fp32.onnx"), ProbeModel.Build());
        File.WriteAllText(Path.Combine(folder, "meta.json"), File.ReadAllText(Path.Combine(Repository.Root, "scripts", "meta.example.json")));
        using ModelLibrary library = new(root, Cpu);

        Assert.Throws<InvalidDataException>(() => Denoise.Run(library, new RgbaImage(8, 8), "fp32"));
    }

    [Fact]
    public void TheRealModelSmoothsNoiseAndItsTilesMatchAWholeRun()
    {
        string models = Path.Combine(Repository.Root, ".cache", "models");
        string image = Path.Combine(Repository.Root, ".cache", "weights", "test-images", "building.png");
        if (!File.Exists(Path.Combine(models, Denoise.Model, "fp32.onnx")) || !File.Exists(image))
        {
            Assert.Skip("needs scripts/download_models.py and scripts/export-and-quantize.py to have run");
        }

        using ModelLibrary library = new(models, Cpu);
        // Two 128 px tiles across, so there is one seam, and small enough for a CPU in seconds.
        RgbaImage photo = Crop(ImageDecoder.Decode(image), 250, 120);
        List<(int, int)> reports = [];

        RgbaImage tiled = Denoise.Run(library, photo, "fp32", tile: 128, progress: (done, total) => reports.Add((done, total)));
        RgbaImage whole = Denoise.Run(library, photo, "fp32", tile: 1024);

        Assert.Equal((photo.Width, photo.Height), (tiled.Width, tiled.Height));
        Assert.Equal(2, reports[^1].Item2);
        double seams = Psnr(tiled, whole);
        Assert.True(seams > 40, $"tiles differ from a whole run: {seams:0.0} dB");
        double before = Roughness(photo);
        double after = Roughness(tiled);
        Assert.True(after < before * 0.8, $"pixel-to-pixel variation {before:0} became {after:0}");
        TestContext.Current.TestOutputHelper?.WriteLine($"seams {seams:0.0} dB; roughness {before:0} -> {after:0}");
    }

    private static RgbaImage Crop(RgbaImage image, int width, int height)
    {
        RgbaImage crop = new(width, height, image.SourceBitDepth);
        for (int y = 0; y < height; y++)
        {
            image.Row(y)[..(width * 4)].CopyTo(crop.Row(y));
        }

        return crop;
    }

    // The mean absolute difference between horizontal neighbours, red channel: noise raises it.
    private static double Roughness(RgbaImage image)
    {
        double sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<ushort> row = image.Row(y);
            for (int x = 1; x < image.Width; x++)
            {
                sum += Math.Abs(row[x * 4] - row[(x - 1) * 4]);
            }
        }

        return sum / (image.Height * (image.Width - 1));
    }

    private static double Psnr(RgbaImage a, RgbaImage b)
    {
        double squared = 0;
        int count = 0;
        for (int i = 0; i < a.Samples.Length; i++)
        {
            if (i % 4 == 3)
            {
                continue;
            }

            double d = (a.Samples[i] - b.Samples[i]) / (double)RgbaImage.Max;
            squared += d * d;
            count++;
        }

        return squared == 0 ? double.PositiveInfinity : 10 * Math.Log10(count / squared);
    }
}
