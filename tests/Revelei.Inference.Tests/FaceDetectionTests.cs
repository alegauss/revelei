using Revelei.Imaging;
using Revelei.Inference.Pipelines;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB30: YuNet's heads decoded as OpenCV does, overlapping boxes suppressed, and the real detector,
/// when export-and-quantize.py has adopted it, finding and aligning the face in the test photo.
/// </summary>
public sealed class FaceDetectionTests
{
    private static readonly ProviderSelection Cpu = new(ExecutionProvider.Cpu, null, null);

    [Fact]
    public void AnAnchorDecodesToItsBoxAndLandmarks()
    {
        // One stride of 8 over a 16 px input: a 2 x 2 grid. Anchor 3 is row 1, column 1.
        Dictionary<string, float[]> heads = new()
        {
            ["cls_8"] = [0, 0, 0, 0.81f],
            ["obj_8"] = [1, 1, 1, 1],
            ["bbox_8"] = [.. new float[12], 0.5f, -0.25f, (float)Math.Log(2), 0],
            ["kps_8"] = [.. new float[30], 0, 0, 1, 0, 0.5f, 0.5f, 0, 1, 1, 1],
        };

        Face face = Assert.Single(FaceDetection.Decode(name => heads[name], 16, [8], 0.5));

        Assert.Equal(0.9, face.Score, 6);
        Assert.Equal(4, face.X, 5);
        Assert.Equal(2, face.Y, 5);
        Assert.Equal(16, face.Width, 5);
        Assert.Equal(8, face.Height, 5);
        Assert.Equal(new PointD(8, 8), face.Landmarks[0]);
        Assert.Equal(new PointD(16, 8), face.Landmarks[1]);
        Assert.Equal(new PointD(12, 12), face.Landmarks[2]);
        Assert.Equal(new PointD(16, 16), face.Landmarks[4]);
    }

    [Fact]
    public void AnchorsUnderTheThresholdAreDropped()
    {
        Dictionary<string, float[]> heads = new()
        {
            ["cls_8"] = [0.5f, 1.2f],
            ["obj_8"] = [0.5f, -0.1f],
            ["bbox_8"] = new float[8],
            ["kps_8"] = new float[20],
        };

        Assert.Empty(FaceDetection.Decode(name => heads[name], 16, [8], 0.9));
    }

    [Fact]
    public void AnOverlappingWeakerBoxIsSuppressedAndASeparateOneKept()
    {
        Face strong = new(0, 0, 10, 10, 0.95, []);
        Face shadow = new(1, 1, 10, 10, 0.92, []);
        Face other = new(50, 50, 10, 10, 0.91, []);

        List<Face> kept = FaceDetection.Suppress([shadow, other, strong], FaceDetection.NmsOverlap);

        Assert.Equal([strong, other], kept);
    }

    [Fact]
    public void TheDetectorFindsAndAlignsBothFacesInTheTestPhoto()
    {
        string models = Path.Combine(Repository.Root, ".cache", "models");
        string image = Path.Combine(Repository.Root, ".cache", "weights", "test-images", "Blake_Lively.jpg");
        if (!File.Exists(Path.Combine(models, FaceDetection.Model, "fp32.onnx")) || !File.Exists(image))
        {
            Assert.Skip("needs scripts/download_models.py and scripts/export-and-quantize.py to have run");
        }

        using ModelLibrary library = new(models, Cpu);
        RgbaImage photo = ImageDecoder.Decode(image);

        IReadOnlyList<Face> faces = FaceDetection.Run(library, photo);

        // GFPGAN's whole_imgs sample is two photos side by side, a child on the left, an adult on
        // the right; both are faces, strongest first.
        Assert.Equal(2, faces.Count);
        Assert.True(faces[0].Score >= faces[1].Score);
        Assert.True(faces[0].X > photo.Width / 2 && faces[1].X + faces[1].Width < photo.Width / 2, "one face per half");
        Assert.All(faces, face => AssertAligned(photo, face));
    }

    private static void AssertAligned(RgbaImage photo, Face face)
    {
        Assert.InRange(face.Score, FaceDetection.DefaultThreshold, 1);
        PointD[] marks = [.. face.Landmarks];
        Assert.True(marks[0].X < marks[1].X && marks[3].X < marks[4].X, "the left landmarks are not on the left");
        Assert.True(marks[2].Y > marks[0].Y && marks[3].Y > marks[2].Y, "eyes, nose and mouth are not in that order down");
        Assert.All(marks, p => Assert.True(p.X >= face.X && p.X <= face.X + face.Width && p.Y >= face.Y && p.Y <= face.Y + face.Height));

        // A real face fits the template within a few pixels of a 512 crop, and the crop is made.
        Similarity toCrop = FaceAlignment.ToCrop(face);
        double worst = marks.Select((p, i) => Distance(toCrop.Apply(p), FaceAlignment.Ffhq512[i])).Max();
        Assert.InRange(worst, 0, 20);
        RgbaImage crop = FaceAlignment.Crop(photo, toCrop);
        Assert.Equal((512, 512), (crop.Width, crop.Height));
    }

    private static double Distance(PointD a, PointD b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
