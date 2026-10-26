namespace Revelei.Imaging.Tests;

/// <summary>
/// PB30: the alignment GFPGAN's crops go through. The similarity is checked by recovering one it
/// was built from, and the crop and paste by what they do to pixels on either side of the seam.
/// </summary>
public sealed class FaceAlignmentTests
{
    private static readonly Similarity Known = new(0.6 * Math.Cos(0.3), 0.6 * Math.Sin(0.3), 40, -25);

    [Fact]
    public void EstimateRecoversTheSimilarityThePointsWereMovedBy()
    {
        PointD[] moved = [.. FaceAlignment.Ffhq512.Select(Known.Apply)];

        Similarity found = FaceAlignment.Estimate(FaceAlignment.Ffhq512, moved);

        Assert.Equal(Known.A, found.A, 9);
        Assert.Equal(Known.B, found.B, 9);
        Assert.Equal(Known.Tx, found.Tx, 6);
        Assert.Equal(Known.Ty, found.Ty, 6);
    }

    [Fact]
    public void TheInverseUndoesTheTransform()
    {
        PointD point = new(123.5, -7.25);

        PointD back = Known.Inverse().Apply(Known.Apply(point));

        Assert.Equal(point.X, back.X, 9);
        Assert.Equal(point.Y, back.Y, 9);
    }

    [Fact]
    public void AFaceIsAlignedOntoTheTemplate()
    {
        // A face at half the template's size, turned and moved: its landmarks land on the template.
        Similarity placed = new(0.5 * Math.Cos(-0.2), 0.5 * Math.Sin(-0.2), 300, 100);
        Face face = new(0, 0, 1, 1, 1, [.. FaceAlignment.Ffhq512.Select(placed.Apply)]);

        Similarity toCrop = FaceAlignment.ToCrop(face);

        Assert.Equal(2.0, toCrop.Scale, 9);
        for (int i = 0; i < 5; i++)
        {
            PointD at = toCrop.Apply(face.Landmarks[i]);
            Assert.Equal(FaceAlignment.Ffhq512[i].X, at.X, 6);
            Assert.Equal(FaceAlignment.Ffhq512[i].Y, at.Y, 6);
        }
    }

    [Fact]
    public void TheCropReadsThePhotoThroughTheTransform()
    {
        // A shift by whole pixels: the crop is the photo's window, exactly.
        RgbaImage photo = Gradient(64, 48);
        Similarity shift = new(1, 0, -10, -5);

        RgbaImage crop = FaceAlignment.Crop(photo, shift, 16);

        Assert.Equal(photo.Row(5 + 7).Slice((10 + 3) * 4, 4).ToArray(), crop.Row(7).Slice(3 * 4, 4).ToArray());
    }

    [Fact]
    public void PastingBackAnUnchangedCropChangesNothingMuch()
    {
        RgbaImage photo = Gradient(200, 160);
        RgbaImage original = Copy(photo);
        Similarity toCrop = new(1.7 * Math.Cos(0.25), 1.7 * Math.Sin(0.25), -60, -90);

        FaceAlignment.PasteBack(photo, FaceAlignment.Crop(photo, toCrop, 128), toCrop);

        // Two bilinear resamplings of a smooth gradient: off by rounding, never by structure.
        int worst = 0;
        for (int i = 0; i < photo.Samples.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(photo.Samples[i] - original.Samples[i]));
        }

        Assert.InRange(worst, 0, RgbaImage.Max / 200);
    }

    [Fact]
    public void APastedCropShowsInsideFadesAtItsEdgeAndLeavesTheRestAlone()
    {
        RgbaImage photo = new(100, 100);
        RgbaImage white = new(64, 64);
        white.Samples.Fill(RgbaImage.Max);
        Similarity toCrop = new(1, 0, -20, -20);

        FaceAlignment.PasteBack(photo, white, toCrop);

        Assert.Equal(RgbaImage.Max, photo.Row(52)[52 * 4]);
        Assert.InRange(photo.Row(52)[22 * 4], (ushort)1, (ushort)(RgbaImage.Max - 1));
        Assert.Equal(0, photo.Row(52)[90 * 4]);
        Assert.Equal(0, photo.Row(52)[(52 * 4) + 3]);
    }

    [Fact]
    public void TheMaskIsWholeInsideAndNothingOutside()
    {
        Assert.Equal(1, FaceAlignment.MaskWeight(new(256, 256), 512));
        Assert.Equal(0, FaceAlignment.MaskWeight(new(-1, 256), 512));
        Assert.Equal(0.5, FaceAlignment.MaskWeight(new(16, 256), 512), 9);
    }

    private static RgbaImage Gradient(int width, int height)
    {
        RgbaImage image = new(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<ushort> row = image.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 4] = (ushort)(x * RgbaImage.Max / (width - 1));
                row[(x * 4) + 1] = (ushort)(y * RgbaImage.Max / (height - 1));
                row[(x * 4) + 2] = (ushort)((x + y) * RgbaImage.Max / (width + height - 2));
                row[(x * 4) + 3] = RgbaImage.Max;
            }
        }

        return image;
    }

    private static RgbaImage Copy(RgbaImage image)
    {
        RgbaImage copy = new(image.Width, image.Height);
        image.Samples.CopyTo(copy.Samples);
        return copy;
    }
}
