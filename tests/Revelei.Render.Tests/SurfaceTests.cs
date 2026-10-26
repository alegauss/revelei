using Revelei.Imaging;
using Vortice.Mathematics;

namespace Revelei.Render.Tests;

/// <summary>PB27: the view arithmetic, and the comparison as drawn, read back from a WARP render.</summary>
public sealed class SurfaceTests
{
    private static readonly SurfacePalette Palette = new(
        new Color4(0.5f, 0.5f, 0.5f, 1), new Color4(1, 1, 1, 1), new Color4(0.8f, 0.8f, 0.8f, 1), new Color4(1, 0.35f, 0.05f, 1));

    [Fact]
    public void FitShowsTheWholeImageAndTheCentreMapsToTheMiddle()
    {
        (double x, double y) = ViewMath.ImagePoint(View.Fit, 400, 200, 800, 400, 400, 200);

        Assert.Equal((0.5, 0.5), (x, y));
        Assert.Equal((1.0, 1.0), ViewMath.Span(400, 200, 800, 400, 1));
    }

    [Fact]
    public void ALetterboxedImageLeavesRoomOnTheLongSide()
    {
        // A square image in a 2:1 surface spans half the width.
        (double sx, double sy) = ViewMath.Span(100, 100, 200, 100, 1);

        Assert.Equal((2.0, 1.0), (sx, sy));
    }

    [Fact]
    public void ZoomingKeepsThePointUnderTheCursorStill()
    {
        View zoomed = ViewMath.ZoomAt(View.Fit, 4, 400, 300, 800, 600, 200, 150);

        (double before, double beforeY) = ViewMath.ImagePoint(View.Fit, 400, 300, 800, 600, 200, 150);
        (double after, double afterY) = ViewMath.ImagePoint(zoomed, 400, 300, 800, 600, 200, 150);

        Assert.Equal(4, zoomed.Zoom);
        Assert.Equal(before, after, 9);
        Assert.Equal(beforeY, afterY, 9);
    }

    [Fact]
    public void ZoomingOutPastFitReturnsToFit() =>
        Assert.Equal(View.Fit, ViewMath.ZoomAt(new View(2, 0.2, 0.7), 0.25, 400, 300, 800, 600, 10, 10));

    [Fact]
    public void PanningMovesTheCentreAgainstTheDragAndStaysInTheImage()
    {
        View panned = ViewMath.Pan(new View(2, 0.5, 0.5), 100, 0, 400, 300, 800, 600);
        View clamped = ViewMath.Pan(new View(2, 0.5, 0.5), -100_000, 0, 400, 300, 800, 600);

        Assert.True(panned.CenterX < 0.5);
        Assert.Equal(1, clamped.CenterX);
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(407, true)]
    [InlineData(420, false)]
    public void TheDividerIsGrabbedNearItsLine(double x, bool expected) =>
        Assert.Equal(expected, ViewMath.OnDivider(0.5, x, 800));

    [Fact]
    public void DividerModeShowsBeforeLeftAndAfterRightWithTheLineBetween()
    {
        using SurfaceRenderer renderer = Offscreen(200, 100);
        renderer.SetImages(Flat(20, 10, 255, 0, 0), Flat(20, 10, 0, 0, 255));

        renderer.Render(View.Fit, CompareMode.Divider, 0.5, Palette);
        byte[] pixels = renderer.ReadPixels();

        Assert.Equal((255, 0, 0), Rgb(pixels, 200, 50, 50));    // before, red
        Assert.Equal((0, 0, 255), Rgb(pixels, 200, 150, 50));   // after, blue
        Assert.Equal((255, 89, 13), Rgb(pixels, 200, 100, 50)); // the divider
    }

    [Fact]
    public void SingleModeShowsTheAfterImageAcross()
    {
        using SurfaceRenderer renderer = Offscreen(200, 100);
        renderer.SetImages(Flat(20, 10, 255, 0, 0), Flat(20, 10, 0, 0, 255));

        renderer.Render(View.Fit, CompareMode.Single, 0.5, Palette);
        byte[] pixels = renderer.ReadPixels();

        Assert.Equal((0, 0, 255), Rgb(pixels, 200, 20, 50));
        Assert.Equal((0, 0, 255), Rgb(pixels, 200, 180, 50));
    }

    [Fact]
    public void SideBySideFitsEachImageToItsHalf()
    {
        // Square images in a 200 x 100 surface fill each half exactly.
        using SurfaceRenderer renderer = Offscreen(200, 100);
        renderer.SetImages(Flat(10, 10, 255, 0, 0), Flat(10, 10, 0, 255, 0));

        renderer.Render(View.Fit, CompareMode.SideBySide, 0.5, Palette);
        byte[] pixels = renderer.ReadPixels();

        Assert.Equal((255, 0, 0), Rgb(pixels, 200, 50, 50));
        Assert.Equal((0, 255, 0), Rgb(pixels, 200, 150, 50));
    }

    [Fact]
    public void TransparencyShowsTheCheckerboardAndOutsideTheImageTheGround()
    {
        using SurfaceRenderer renderer = Offscreen(200, 100);
        RgbaImage clear = new(10, 10); // all zero: transparent
        renderer.SetImages(null, clear);

        renderer.Render(View.Fit, CompareMode.Single, 0.5, Palette);
        byte[] pixels = renderer.ReadPixels();

        Assert.Equal((255, 255, 255), Rgb(pixels, 200, 52, 2));   // a light square
        Assert.Equal((204, 204, 204), Rgb(pixels, 200, 60, 2));   // the next, dark
        Assert.Equal((128, 128, 128), Rgb(pixels, 200, 10, 50));  // ground beside the square image
    }

    [Fact]
    public void CroppingDimsOutsideTheRectangleAndDrawsItsBorder()
    {
        // PB67: a 20 x 10 blue photo fills the 200 x 100 surface at 10 px a photo pixel; the crop
        // keeps photo columns 5 to 15, i.e. surface x 50 to 150.
        using SurfaceRenderer renderer = Offscreen(200, 100);
        renderer.SetImages(null, Flat(20, 10, 0, 0, 255));

        renderer.Render(View.Fit, CompareMode.Single, 0.5, Palette, new PixelRect(5, 0, 10, 10));
        byte[] pixels = renderer.ReadPixels();

        Assert.Equal((0, 0, 255), Rgb(pixels, 200, 100, 30));    // inside: the photo as it is
        Assert.Equal((76, 76, 178), Rgb(pixels, 200, 20, 30));   // outside: 60% towards the ground, a hair under .5
        Assert.Equal((255, 89, 13), Rgb(pixels, 200, 50, 30));   // the left border
    }

    private static SurfaceRenderer Offscreen(int width, int height)
    {
        SurfaceRenderer renderer = new(warp: true);
        renderer.AttachOffscreen(width, height);
        return renderer;
    }

    private static RgbaImage Flat(int w, int h, byte r, byte g, byte b)
    {
        RgbaImage image = new(w, h);
        Span<ushort> s = image.Samples;
        for (int i = 0; i < s.Length; i += 4)
        {
            (s[i], s[i + 1], s[i + 2], s[i + 3]) = (RgbaImage.From8(r), RgbaImage.From8(g), RgbaImage.From8(b), RgbaImage.Max);
        }

        return image;
    }

    /// <summary>The offscreen texture is BGRA.</summary>
    private static (int R, int G, int B) Rgb(byte[] pixels, int width, int x, int y)
    {
        int i = ((y * width) + x) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i]);
    }
}
