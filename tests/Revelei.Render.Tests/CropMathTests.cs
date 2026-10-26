using Revelei.Imaging;

namespace Revelei.Render.Tests;

/// <summary>
/// PB67: the crop rectangle's arithmetic. The handle drag itself has no UI case, since winwright
/// cannot drag between two points of one element (WW572), so the gesture's maths is held here.
/// </summary>
public sealed class CropMathTests
{
    private static readonly PixelRect Middle = new(20, 10, 40, 30);

    [Fact]
    public void FreeFitsTheWholePhotoAndAnAspectTheLargestCentredRectangle()
    {
        Assert.Equal(new PixelRect(0, 0, 400, 300), CropMath.Fit(null, 400, 300));
        Assert.Equal(new PixelRect(50, 0, 300, 300), CropMath.Fit(1, 400, 300));
        Assert.Equal(new PixelRect(0, 37, 400, 225), CropMath.Fit(16.0 / 9, 400, 300));
    }

    [Fact]
    public void ClampingKeepsARectangleInsideThePhotoAndAtLeastAPixel()
    {
        Assert.Equal(new PixelRect(60, 0, 40, 30), CropMath.Clamp(new PixelRect(90, -5, 40, 30), 100, 50));
        Assert.Equal(new PixelRect(0, 0, 1, 50), CropMath.Clamp(new PixelRect(0, 0, 0, 80), 100, 50));
    }

    [Theory]
    [InlineData(20, 10, CropHandle.TopLeft)]
    [InlineData(40, 11, CropHandle.Top)]
    [InlineData(61, 40, CropHandle.BottomRight)]
    [InlineData(19, 25, CropHandle.Left)]
    [InlineData(26, 9, CropHandle.Top)]      // PB69: an edge is grabbed along its length, not only at its middle
    [InlineData(61, 36, CropHandle.Right)]
    [InlineData(52, 41, CropHandle.Bottom)]
    [InlineData(21, 12, CropHandle.TopLeft)] // and a corner wins where an edge and it overlap
    [InlineData(35, 30, CropHandle.Move)]
    [InlineData(5, 5, CropHandle.None)]
    public void APressGrabsTheNearestHandleTheInsideOrNothing(double x, double y, CropHandle expected) =>
        Assert.Equal(expected, CropMath.HitTest(Middle, x, y, 3));

    [Fact]
    public void ACornerDragKeepsTheOppositeCornerAndCannotCrossIt()
    {
        Assert.Equal(new PixelRect(30, 15, 30, 25), CropMath.Drag(Middle, CropHandle.TopLeft, 10, 5, null, 100, 50));
        Assert.Equal(new PixelRect(59, 39, 1, 1), CropMath.Drag(Middle, CropHandle.TopLeft, 500, 500, null, 100, 50));
    }

    [Fact]
    public void AnEdgeDragMovesOnlyThatEdgeAndStopsAtThePhoto()
    {
        Assert.Equal(new PixelRect(20, 10, 80, 30), CropMath.Drag(Middle, CropHandle.Right, 500, 7, null, 100, 50));
        Assert.Equal(new PixelRect(20, 0, 40, 40), CropMath.Drag(Middle, CropHandle.Top, 3, -50, null, 100, 50));
    }

    [Fact]
    public void MovingKeepsTheSizeAndStaysInside() =>
        Assert.Equal(new PixelRect(60, 0, 40, 30), CropMath.Drag(Middle, CropHandle.Move, 1000, -1000, null, 100, 50));

    [Fact]
    public void WithAnAspectACornerDragKeepsItAndShrinksToWhatFits()
    {
        // 1:1 from the top-left corner anchored at (60, 40): the width leads, the height follows,
        // and a drag past the photo's top stops both at the 40 pixels above the anchor.
        Assert.Equal(new PixelRect(30, 10, 30, 30), CropMath.Drag(Middle, CropHandle.TopLeft, 10, 0, 1, 100, 50));
        Assert.Equal(new PixelRect(20, 0, 40, 40), CropMath.Drag(Middle, CropHandle.TopLeft, -100, -100, 1, 100, 50));
    }

    [Fact]
    public void WithAnAspectAnEdgeDragGrowsTheOtherSideAboutTheCentre()
    {
        // 2:1, the right edge to 80 wide: 40 high about the old centre (25), 5 to 45.
        Assert.Equal(new PixelRect(20, 5, 80, 40), CropMath.Drag(Middle, CropHandle.Right, 40, 0, 2, 100, 50));
    }
}
