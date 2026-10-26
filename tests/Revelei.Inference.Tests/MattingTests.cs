using Revelei.Imaging;
using Revelei.Inference.Pipelines;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB48: ViTMatte refines only RMBG's unsure band, from a trimap made of RMBG's own mask, in tiles
/// that skip what is far from the edge; the real model narrows the band on the test photo.
/// </summary>
public sealed class MattingTests
{
    private static readonly ProviderSelection Cpu = new(ExecutionProvider.Cpu, null, null);

    private static readonly ModelMeta Meta = new(
        1,
        Matting.Model,
        "matting",
        1,
        new ModelInput("input", "NCHW", "RGB", 4, [-1.0, 1.0], null, 32, null),
        new ModelOutput("output", 1, [0.0, 1.0]),
        new ModelTile(null, 128),
        new Dictionary<string, ModelVariant>(),
        new ModelUpstream("https://example.org", "test", "https://example.org"));

    [Fact]
    public void TheTrimapGrowsTheUnsureBandAndThresholdsTheRest()
    {
        // As export-and-quantize.py's trimap_from_mask: subject left, one soft column at x = 9.
        const int width = 20;
        const int height = 9;
        float[] mask = [.. Enumerable.Range(0, width * height).Select(i => (i % width) switch { < 9 => 1f, 9 => 0.5f, _ => 0f })];

        float[] trimap = Matting.Trimap(mask, width, height, 2, 0.02f);

        float At(int x, int y) => trimap[(y * width) + x];
        Assert.All(Enumerable.Range(0, height), y => Assert.Equal([1f, Matting.Unknown, Matting.Unknown, Matting.Unknown, Matting.Unknown, Matting.Unknown, 0f], Enumerable.Range(6, 7).Select(x => At(x, y))));
    }

    [Fact]
    public void OnlyTheUnknownPixelsTakeTheModelsAlphaAndFarTilesAreSkipped()
    {
        // A 1200 x 64 photo: the edge sits in the first tile, so the second and third never run.
        const int width = 1200;
        const int height = 64;
        RgbaImage photo = new(width, height);
        float[] mask = [.. Enumerable.Range(0, width * height).Select(i => (i % width) switch { < 100 => 1f, 100 => 0.5f, _ => 0f })];
        List<(int Width, int Height, float[] Trimap)> calls = [];

        int ran = Matting.Refine(photo, mask, Meta, (input, w, h) =>
        {
            calls.Add((w, h, input[(3 * w * h)..]));
            return [.. Enumerable.Repeat(0.25f, w * h)];
        }, 0.02f);

        Assert.Equal(1, ran);
        Assert.Equal(0, calls[0].Width % 32);
        Assert.Equal(0, calls[0].Height % 32);
        Assert.Contains(Matting.Unknown, calls[0].Trimap);
        Assert.Equal(0.25f, mask[(10 * width) + 100]);
        Assert.Equal(1f, mask[(10 * width) + 50]);
        Assert.Equal(0f, mask[(10 * width) + 400]);
    }

    [Fact]
    public void ProgressCountsOnlyTheTilesTheEdgeCrosses()
    {
        // PB62: two edges in a 2500 px strip, in the first and third tiles, so the total is two of
        // three and the report says so before the first tile runs, not once at the end.
        const int width = 2500;
        const int height = 64;
        float[] mask = [.. Enumerable.Range(0, width * height).Select(i => (i % width) switch { < 100 => 1f, 100 or 2200 => 0.5f, > 2200 => 1f, _ => 0f })];
        List<(int Done, int Total)> reports = [];

        int ran = Matting.Refine(new RgbaImage(width, height), mask, Meta, (_, w, h) => [.. Enumerable.Repeat(0.25f, w * h)], 0.02f, progress: (done, total) => reports.Add((done, total)));

        Assert.Equal(2, ran);
        Assert.Equal([(0, 2), (1, 2), (2, 2)], reports);
    }

    [Fact]
    public void TheGrowRadiusIsEightPixelsOnTheTestPhotoAndScalesWithTheSide()
    {
        Assert.Equal(8, Matting.GrowRadius(933, 1405));
        Assert.Equal(4, Matting.GrowRadius(300, 200));
        Assert.Equal(23, Matting.GrowRadius(4000, 3000));
    }

    [Fact]
    public void TheRealModelNarrowsRmbgsEdgeBandOnTheTestPhoto()
    {
        string models = Path.Combine(Repository.Root, ".cache", "models");
        string image = Path.Combine(Repository.Root, ".cache", "weights", "test-images", "example_input.jpg");
        if (!File.Exists(Path.Combine(models, Matting.Model, "fp32.onnx")) || !File.Exists(Path.Combine(models, RemoveBackground.Model, "fp32.onnx")) || !File.Exists(image))
        {
            Assert.Skip("needs scripts/download_models.py and scripts/export-and-quantize.py to have run");
        }

        using ModelLibrary library = new(models, Cpu);
        RgbaImage photo = ImageDecoder.Decode(image);

        RgbaImage raw = RemoveBackground.Run(library, photo, "fp32", refine: false);
        RgbaImage matted = RemoveBackground.Run(library, photo, "fp32");

        // The done-when: the band (alpha 10% to 90%) narrower than RMBG-1.4's own.
        int Band(RgbaImage result) => result.Samples.ToArray().Where((_, i) => i % 4 == 3).Count(a => a is > 6553 and < 58982);
        int before = Band(raw);
        int after = Band(matted);
        TestContext.Current.TestOutputHelper?.WriteLine($"band pixels {before} -> {after}");
        Assert.True(after < before, $"the band went from {before} to {after} pixels");
    }
}
