namespace Revelei.Imaging.Tests;

/// <summary>PB65, PB66, PB67: quarter turns, flips and crops are exact moves of every pixel, alpha included.</summary>
public sealed class ImageTransformsTests
{
    // A 3 x 2 image whose every sample is distinct, so any misplaced pixel or channel shows.
    private static RgbaImage Numbered(int width = 3, int height = 2, int depth = 16)
    {
        RgbaImage image = new(width, height, depth);
        Span<ushort> samples = image.Samples;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)((i * 997) + 1);
        }

        return image;
    }

    private static ushort[] Pixel(RgbaImage image, int x, int y) => image.Row(y).Slice(x * 4, 4).ToArray();

    [Fact]
    public void RotatingRightPutsTheBottomLeftCornerAtTheTopLeft()
    {
        RgbaImage image = Numbered();

        RgbaImage turned = ImageTransforms.RotateRight(image);

        Assert.Equal((2, 3), (turned.Width, turned.Height));
        Assert.Equal(Pixel(image, 0, 1), Pixel(turned, 0, 0));
        Assert.Equal(Pixel(image, 0, 0), Pixel(turned, 1, 0));
        Assert.Equal(Pixel(image, 2, 0), Pixel(turned, 1, 2));
    }

    [Fact]
    public void RotatingLeftPutsTheTopRightCornerAtTheTopLeft()
    {
        RgbaImage image = Numbered();

        RgbaImage turned = ImageTransforms.RotateLeft(image);

        Assert.Equal((2, 3), (turned.Width, turned.Height));
        Assert.Equal(Pixel(image, 2, 0), Pixel(turned, 0, 0));
        Assert.Equal(Pixel(image, 0, 0), Pixel(turned, 0, 2));
        Assert.Equal(Pixel(image, 0, 1), Pixel(turned, 1, 2));
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(130, 67)]
    public void FourTurnsEitherWayGiveBackTheSameBytes(int width, int height)
    {
        // 130 x 67 crosses the 64-pixel blocks the turn walks in, unevenly on both axes.
        RgbaImage image = Numbered(width, height);

        RgbaImage right = image, left = image;
        for (int i = 0; i < 4; i++)
        {
            right = ImageTransforms.RotateRight(right);
            left = ImageTransforms.RotateLeft(left);
        }

        Assert.True(image.Samples.SequenceEqual(right.Samples));
        Assert.True(image.Samples.SequenceEqual(left.Samples));
    }

    [Fact]
    public void FlippingHorizontallySwapsColumnsAndKeepsRows()
    {
        RgbaImage image = Numbered();

        RgbaImage flipped = ImageTransforms.FlipHorizontal(image);

        Assert.Equal((3, 2), (flipped.Width, flipped.Height));
        Assert.Equal(Pixel(image, 2, 0), Pixel(flipped, 0, 0));
        Assert.Equal(Pixel(image, 1, 1), Pixel(flipped, 1, 1));
        Assert.Equal(Pixel(image, 0, 1), Pixel(flipped, 2, 1));
    }

    [Fact]
    public void FlippingVerticallySwapsRowsAndKeepsColumns()
    {
        RgbaImage image = Numbered();

        RgbaImage flipped = ImageTransforms.FlipVertical(image);

        Assert.Equal((3, 2), (flipped.Width, flipped.Height));
        Assert.Equal(Pixel(image, 0, 1), Pixel(flipped, 0, 0));
        Assert.Equal(Pixel(image, 2, 0), Pixel(flipped, 2, 1));
    }

    [Theory]
    [InlineData(3, 2, 16)]
    [InlineData(130, 67, 8)]
    public void TwoFlipsEitherWayGiveBackTheSameBytesAndDepth(int width, int height, int depth)
    {
        RgbaImage image = Numbered(width, height, depth);

        RgbaImage horizontal = ImageTransforms.FlipHorizontal(ImageTransforms.FlipHorizontal(image));
        RgbaImage vertical = ImageTransforms.FlipVertical(ImageTransforms.FlipVertical(image));

        Assert.True(image.Samples.SequenceEqual(horizontal.Samples));
        Assert.True(image.Samples.SequenceEqual(vertical.Samples));
        Assert.Equal(depth, horizontal.SourceBitDepth);
        Assert.Equal(depth, vertical.SourceBitDepth);
    }

    [Fact]
    public void BothFlipsAreAHalfTurn()
    {
        RgbaImage image = Numbered(130, 67);

        RgbaImage flipped = ImageTransforms.FlipVertical(ImageTransforms.FlipHorizontal(image));
        RgbaImage turned = ImageTransforms.RotateRight(ImageTransforms.RotateRight(image));

        Assert.True(turned.Samples.SequenceEqual(flipped.Samples));
    }

    [Fact]
    public void CroppingCopiesThePixelsInsideTheRectangleUnchanged()
    {
        // PB67.
        RgbaImage image = Numbered(130, 67, depth: 8);

        RgbaImage cropped = ImageTransforms.Crop(image, new PixelRect(7, 3, 64, 20));

        Assert.Equal((64, 20, 8), (cropped.Width, cropped.Height, cropped.SourceBitDepth));
        Assert.Equal(Pixel(image, 7, 3), Pixel(cropped, 0, 0));
        Assert.Equal(Pixel(image, 70, 22), Pixel(cropped, 63, 19));
    }

    [Fact]
    public void ACropOutsideTheImageIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageTransforms.Crop(Numbered(), new PixelRect(1, 0, 3, 2)));

    [Fact]
    public void ATurnRightIsUndoneByATurnLeftAndKeepsTheSourceDepth()
    {
        RgbaImage image = Numbered(130, 67, depth: 8);

        RgbaImage back = ImageTransforms.RotateLeft(ImageTransforms.RotateRight(image));

        Assert.True(image.Samples.SequenceEqual(back.Samples));
        Assert.Equal(8, back.SourceBitDepth);
    }
}
