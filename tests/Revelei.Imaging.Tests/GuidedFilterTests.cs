namespace Revelei.Imaging.Tests;

/// <summary>PB47: the guided filter that refines a scaled-up mask against the full-size photo.</summary>
public sealed class GuidedFilterTests
{
    [Fact]
    public void TheBoxMeanIsClippedAtTheEdge()
    {
        // 1 2 3 over one row: the corner window holds two values, the middle one three.
        float[] means = GuidedFilter.Box([1f, 2f, 3f], 3, 1, 1);

        Assert.Equal([1.5f, 2f, 2.5f], means);
    }

    [Fact]
    public void OnAFlatPhotoItIsTheBoxMeanOfTheMask()
    {
        RgbaImage flat = Uniform(9, 1, 30000);
        float[] mask = [0, 0, 0, 0, 1, 1, 1, 1, 1];

        float[] refined = GuidedFilter.Refine(flat, mask, 1, 1e-3f);

        // A flat guide has no edge to follow, so a = 0 and the mask is box-averaged twice.
        float[] expected = GuidedFilter.Box(GuidedFilter.Box(mask, 9, 1, 1), 9, 1, 1);
        for (int i = 0; i < mask.Length; i++)
        {
            Assert.Equal(expected[i], refined[i], 4);
        }
    }

    [Fact]
    public void ABlurredMaskSnapsToThePhotosEdge()
    {
        // The photo steps from dark to light between columns 9 and 10; the mask ramps across it,
        // as a mask scaled up from a smaller size does.
        RgbaImage photo = new(20, 4);
        float[] mask = new float[20 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                ushort v = x < 10 ? (ushort)5000 : (ushort)60000;
                photo.Row(y)[x * 4] = photo.Row(y)[(x * 4) + 1] = photo.Row(y)[(x * 4) + 2] = v;
                mask[(y * 20) + x] = Math.Clamp((x - 6) / 8f, 0f, 1f);
            }
        }

        float[] refined = GuidedFilter.Refine(photo, mask, 3, 1e-4f);

        // Either side of the photo's edge the mask moves towards 0 and 1 respectively.
        Assert.True(refined[(2 * 20) + 8] < mask[(2 * 20) + 8], $"dark side {refined[(2 * 20) + 8]} vs {mask[(2 * 20) + 8]}");
        Assert.True(refined[(2 * 20) + 11] > mask[(2 * 20) + 11], $"light side {refined[(2 * 20) + 11]} vs {mask[(2 * 20) + 11]}");
    }

    private static RgbaImage Uniform(int width, int height, ushort value)
    {
        RgbaImage image = new(width, height);
        image.Samples.Fill(value);
        return image;
    }
}
