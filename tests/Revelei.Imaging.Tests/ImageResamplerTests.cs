namespace Revelei.Imaging.Tests;

/// <summary>PB32: resizing to and from a model's fixed square.</summary>
public sealed class ImageResamplerTests
{
    [Fact]
    public void ShrinkingAveragesEachFootprint()
    {
        // A 4 x 2 checker of 0 and 40000, halved: every output pixel covers two of each.
        RgbaImage image = new(4, 2);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                image.Row(y)[x * 4] = (ushort)((x + y) % 2 == 0 ? 40000 : 0);
                image.Row(y)[(x * 4) + 3] = RgbaImage.Max;
            }
        }

        RgbaImage half = ImageResampler.Resize(image, 2, 1);

        Assert.Equal([20000, 20000], [half.Row(0)[0], half.Row(0)[4]]);
        Assert.Equal(RgbaImage.Max, half.Row(0)[3]);
    }

    [Fact]
    public void AnUnevenShrinkWeighsPartlyCoveredPixels()
    {
        // Three pixels into two: the middle one is split between both outputs.
        RgbaImage image = new(3, 1);
        image.Row(0)[0] = 30000;
        image.Row(0)[4] = 0;
        image.Row(0)[8] = 60000;

        RgbaImage two = ImageResampler.Resize(image, 2, 1);

        Assert.Equal(20000, two.Row(0)[0]);
        Assert.Equal(40000, two.Row(0)[4]);
    }

    [Fact]
    public void GrowingIsBilinearBetweenCentres()
    {
        RgbaImage image = new(2, 1);
        image.Row(0)[0] = 0;
        image.Row(0)[4] = 40000;

        RgbaImage four = ImageResampler.Resize(image, 4, 1);

        Assert.Equal([0, 10000, 30000, 40000], [four.Row(0)[0], four.Row(0)[4], four.Row(0)[8], four.Row(0)[12]]);
    }

    [Fact]
    public void TheSameSizeIsACopy()
    {
        RgbaImage image = new(3, 2);
        for (int i = 0; i < image.Samples.Length; i++)
        {
            image.Samples[i] = (ushort)(i * 1000);
        }

        Assert.Equal(image.Samples.ToArray(), ImageResampler.Resize(image, 3, 2).Samples.ToArray());
    }

    [Fact]
    public void APlaneGrowsBilinearly()
    {
        float[] plane = ImageResampler.ResizePlane([0f, 1f], 2, 1, 4, 2);

        Assert.Equal([0f, 0.25f, 0.75f, 1f, 0f, 0.25f, 0.75f, 1f], plane);
    }
}
