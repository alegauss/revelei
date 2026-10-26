using Revelei.Imaging;
using Revelei.Inference.Pipelines;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB32: the mask a stand-in model returns becomes the alpha, stretched to its own range as BRIA's
/// example does, with colour untouched; the real RMBG-1.4 runs too when it has been exported.
/// </summary>
public sealed class RemoveBackgroundTests
{
    private const int Side = 8;
    private static readonly ProviderSelection Cpu = new(ExecutionProvider.Cpu, null, null);

    private static readonly ModelMeta Meta = new(
        1,
        RemoveBackground.Model,
        "background-removal",
        1,
        new ModelInput("input", "NCHW", "RGB", 3, [-0.5, 0.5], 1024, 1, 1),
        new ModelOutput("output", 1, [0.0, 1.0]),
        new ModelTile(null, 1),
        new Dictionary<string, ModelVariant>(),
        new ModelUpstream("https://example.org", "test", "https://example.org"));

    [Fact]
    public void TheMaskBecomesTheAlphaAndColourIsLeftAlone()
    {
        RgbaImage photo = Opaque(32, 16, 12345);

        // Left half subject, right half background, in a range the stretch must widen to 0 to 1.
        RgbaImage result = RemoveBackground.Matte(photo, Side, Meta, _ => [.. Enumerable.Range(0, Side * Side).Select(i => i % Side < Side / 2 ? 0.7f : 0.2f)]);

        Assert.Equal((32, 16), (result.Width, result.Height));
        Assert.Equal(RgbaImage.Max, result.Row(8)[(2 * 4) + 3]);
        Assert.Equal(0, result.Row(8)[(29 * 4) + 3]);
        Assert.Equal(12345, result.Row(8)[29 * 4]);
        Assert.Equal(RgbaImage.Max, photo.Row(8)[(29 * 4) + 3]);
    }

    [Fact]
    public void TheModelSeesThePhotoInItsRange()
    {
        RgbaImage photo = Opaque(20, 10, RgbaImage.Max);
        float[]? seen = null;

        RemoveBackground.Matte(photo, Side, Meta, input =>
        {
            seen = input;
            return new float[Side * Side];
        });

        Assert.Equal(3 * Side * Side, seen!.Length);
        Assert.All(seen, v => Assert.Equal(0.5f, v, 5));
    }

    [Fact]
    public void AFlatMaskIsReadInTheModelsRange()
    {
        RgbaImage result = RemoveBackground.Matte(Opaque(8, 8, 0), Side, Meta, _ => [.. Enumerable.Repeat(1f, Side * Side)]);

        Assert.All(result.Samples.ToArray().Where((_, i) => i % 4 == 3), a => Assert.Equal(RgbaImage.Max, a));
    }

    [Fact]
    public void AnAlphaThePhotoHadIsKeptWhereItIsLower()
    {
        // The mask calls the corner (0, 0) background and the rest subject; the photo was already
        // nearly transparent at (7, 7), and stays so.
        RgbaImage photo = Opaque(8, 8, 0);
        photo.Row(7)[(7 * 4) + 3] = 1000;

        RgbaImage result = RemoveBackground.Matte(photo, Side, Meta, _ => [.. Enumerable.Range(0, Side * Side).Select(i => i == 0 ? 0f : 1f)]);

        Assert.Equal(0, result.Row(0)[3]);
        Assert.Equal(1000, result.Row(7)[(7 * 4) + 3]);
        Assert.Equal(RgbaImage.Max, result.Row(4)[(4 * 4) + 3]);
    }

    [Fact]
    public void RefiningZeroesFaintSpecks()
    {
        // Subject on the left; on the right a background of 0 with one model pixel at 5%, a faint
        // speck such as RMBG leaves in a sky. The stretch keeps it; refining removes it.
        // Far enough from the subject that its edge, smoothed on this flat photo, cannot reach.
        const int side = 16;
        RgbaImage photo = Opaque(32, 32, 20000);
        int speck = (8 * side) + 13;
        float[] Specks(float[] _) => [.. Enumerable.Range(0, side * side).Select(i => i % side < 8 ? 1f : i == speck ? 0.05f : 0f)];

        RgbaImage raw = RemoveBackground.Matte(photo, side, Meta, Specks);
        RgbaImage refined = RemoveBackground.Matte(photo, side, Meta, Specks, refine: true);

        Assert.True(raw.Row(17)[(27 * 4) + 3] > 1000, "the speck should be in the raw mask");
        Assert.Equal(0, refined.Row(17)[(27 * 4) + 3]);
        Assert.Equal(RgbaImage.Max, refined.Row(16)[(4 * 4) + 3]);
    }

    [Fact]
    public void TheRadiusCoversTwoModelPixelsAtThePhotosScale()
    {
        Assert.Equal(2, RemoveBackground.RefineRadius(new RgbaImage(500, 300), 1024));
        Assert.Equal(8, RemoveBackground.RefineRadius(new RgbaImage(4000, 3000), 1024));
    }

    [Fact]
    public void TheRealModelSeparatesTheSubjectInTheTestPhoto()
    {
        string models = Path.Combine(Repository.Root, ".cache", "models");
        string image = Path.Combine(Repository.Root, ".cache", "weights", "test-images", "example_input.jpg");
        if (!File.Exists(Path.Combine(models, RemoveBackground.Model, "fp32.onnx")) || !File.Exists(image))
        {
            Assert.Skip("needs scripts/download_models.py and scripts/export-and-quantize.py to have run");
        }

        using ModelLibrary library = new(models, Cpu);
        RgbaImage photo = ImageDecoder.Decode(image);

        RgbaImage result = RemoveBackground.Run(library, photo, "fp32");
        RgbaImage raw = RemoveBackground.Run(library, photo, "fp32", refine: false);

        ushort[] alpha = Alpha(result);
        double opaque = alpha.Count(a => a > 60000) / (double)alpha.Length;
        double clear = alpha.Count(a => a < 5000) / (double)alpha.Length;
        Assert.InRange(opaque, 0.05, 0.95);
        Assert.InRange(clear, 0.05, 0.95);

        // PB47: refining leaves fewer faint pixels (alpha above 0, under 10%) than BRIA's own result.
        int Faint(ushort[] values) => values.Count(a => a is > 0 and < 6554);
        int before = Faint(Alpha(raw));
        int after = Faint(alpha);
        TestContext.Current.TestOutputHelper?.WriteLine($"faint pixels {before} -> {after}");
        Assert.True(after < before, $"faint pixels {before} became {after}");
    }

    private static ushort[] Alpha(RgbaImage image) => [.. image.Samples.ToArray().Where((_, i) => i % 4 == 3)];

    private static RgbaImage Opaque(int width, int height, ushort value)
    {
        RgbaImage image = new(width, height);
        for (int i = 0; i < image.Samples.Length; i++)
        {
            image.Samples[i] = i % 4 == 3 ? RgbaImage.Max : value;
        }

        return image;
    }
}
