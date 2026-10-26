using Revelei.Imaging;
using Revelei.Inference.Pipelines;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB31: the per-face half driven by stand-in models, so what lands where is checkable: an
/// identity leaves the photo, an inverting model changes the face and nothing far from it, and
/// strength blends. The real GFPGAN runs too when export-and-quantize.py has left it.
/// </summary>
public sealed class FaceRestoreTests
{
    private const int Side = 64;
    private static readonly ProviderSelection Cpu = new(ExecutionProvider.Cpu, null, null);

    // GFPGAN's ranges; only Input and Output are read.
    private static readonly ModelMeta Meta = new(
        1,
        FaceRestore.Model,
        "face-restore",
        1,
        new ModelInput("input", "NCHW", "RGB", 3, [-1.0, 1.0], 512, 1, 1),
        new ModelOutput("output", 3, [-1.0, 1.0]),
        new ModelTile(null, 1),
        new Dictionary<string, ModelVariant>(),
        new ModelUpstream("https://example.org", "test", "https://example.org"));

    // A face at a quarter of the template's scale, centred near (60, 50) in a 200 x 120 photo.
    private static readonly Face Small = new(40, 30, 40, 40, 0.99, [.. FaceAlignment.Ffhq512.Select(p => new PointD(28 + (p.X / 8), 15 + (p.Y / 8)))]);

    [Fact]
    public void AnIdentityModelLeavesThePhotoAsItWas()
    {
        RgbaImage photo = Grey(200, 120, 20000);

        FaceRestore.Restore(photo, [Small], Side, Meta, crop => crop, 1);

        Assert.All(photo.Samples.ToArray().Where((_, i) => i % 4 != 3), s => Assert.InRange(s, 19990, 20010));
    }

    [Fact]
    public void AnInvertingModelChangesTheFaceAndNothingFarFromIt()
    {
        RgbaImage photo = Grey(200, 120, 10000);
        List<(int, int)> reports = [];

        FaceRestore.Restore(photo, [Small], Side, Meta, crop => [.. crop.Select(v => -v)], 1, (done, total) => reports.Add((done, total)));

        Assert.InRange(photo.Row(50)[60 * 4], 55000, 56000);
        Assert.Equal(10000, photo.Row(50)[190 * 4]);
        Assert.Equal(10000, photo.Row(110)[60 * 4]);
        Assert.Equal(RgbaImage.Max, photo.Row(50)[(60 * 4) + 3]);
        Assert.Equal([(1, 1)], reports);
    }

    [Fact]
    public void StrengthBlendsTheRestoredFaceWithTheOriginal()
    {
        RgbaImage photo = Grey(200, 120, 10000);

        FaceRestore.Restore(photo, [Small], Side, Meta, crop => [.. crop.Select(v => -v)], 0.5);

        Assert.InRange(photo.Row(50)[60 * 4], 32000, 33500);
    }

    [Fact]
    public void ThePhotoWithoutFacesComesBackUnchangedAndSaysSo()
    {
        RgbaImage photo = Grey(64, 48, 30000);

        FaceRestore.Restore(photo, [], Side, Meta, _ => throw new InvalidOperationException("no face, no model"), 1);

        Assert.All(photo.Samples.ToArray(), s => Assert.True(s is 30000 or RgbaImage.Max));
    }

    [Fact]
    public void TheRealModelsRestoreBothFacesInTheTestPhoto()
    {
        string models = Path.Combine(Repository.Root, ".cache", "models");
        string image = Path.Combine(Repository.Root, ".cache", "weights", "test-images", "Blake_Lively.jpg");
        if (!File.Exists(Path.Combine(models, FaceRestore.Model, "fp32.onnx")) || !File.Exists(Path.Combine(models, FaceDetection.Model, "fp32.onnx")) || !File.Exists(image))
        {
            Assert.Skip("needs scripts/download_models.py and scripts/export-and-quantize.py to have run");
        }

        using ModelLibrary library = new(models, Cpu);
        RgbaImage photo = ImageDecoder.Decode(image);
        List<(string, int, int)> reports = [];

        FaceRestoreResult result = FaceRestore.Run(library, photo, "fp32", progress: (stage, done, total) => reports.Add((stage, done, total)));

        Assert.Equal(2, result.Faces);
        Assert.Equal((photo.Width, photo.Height), (result.Image.Width, result.Image.Height));
        Assert.Equal([(FaceRestore.Stage, 1, 2), (FaceRestore.Stage, 2, 2)], reports);
        // The faces changed, the corners did not.
        Assert.True(Difference(photo, result.Image, new(450, 120, 100, 100)) > 500, "the adult's face is unchanged");
        Assert.Equal(0, Difference(photo, result.Image, new(0, 0, 40, 40)));
    }

    private static RgbaImage Grey(int width, int height, ushort value)
    {
        RgbaImage image = new(width, height);
        for (int i = 0; i < image.Samples.Length; i++)
        {
            image.Samples[i] = i % 4 == 3 ? RgbaImage.Max : value;
        }

        return image;
    }

    // The mean absolute difference of the red samples over a rectangle.
    private static double Difference(RgbaImage a, RgbaImage b, PixelRect area)
    {
        double sum = 0;
        for (int y = area.Y; y < area.Y + area.Height; y++)
        {
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                sum += Math.Abs(a.Row(y)[x * 4] - b.Row(y)[x * 4]);
            }
        }

        return sum / area.Area;
    }
}
