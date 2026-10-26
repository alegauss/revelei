namespace Revelei.Render;

/// <summary>How the surface shows its images (PB27).</summary>
public enum CompareMode
{
    /// <summary>The after image alone.</summary>
    Single,

    /// <summary>Before left of the divider, after right of it, aligned pixel for pixel.</summary>
    Divider,

    /// <summary>Before in the left half, after in the right, each fitted to its half.</summary>
    SideBySide,
}

/// <summary>
/// Where the image sits in the surface: zoom 1 fits it, and the centre is the image point (0 to 1
/// each way) in the middle of the view. Pure arithmetic, so the shader's uniforms and the mouse
/// handling share one definition and the tests can check it without a device.
/// </summary>
/// <param name="Zoom">1 fits the image; 2 doubles it.</param>
/// <param name="CenterX">The image's horizontal point at the view's centre, 0 to 1.</param>
/// <param name="CenterY">The image's vertical point at the view's centre, 0 to 1.</param>
public readonly record struct View(double Zoom, double CenterX, double CenterY)
{
    /// <summary>The whole image, fitted and centred.</summary>
    public static View Fit => new(1, 0.5, 0.5);
}

/// <summary>The transforms between surface pixels and image coordinates.</summary>
public static class ViewMath
{
    /// <summary>The smallest and largest zoom the surface allows.</summary>
    public const double MinZoom = 1, MaxZoom = 64;

    /// <summary>Image coordinates per surface unit at this view: the shader's scale.</summary>
    /// <param name="imageWidth">Image pixels across.</param>
    /// <param name="imageHeight">Image pixels down.</param>
    /// <param name="surfaceWidth">Surface pixels across.</param>
    /// <param name="surfaceHeight">Surface pixels down.</param>
    /// <param name="zoom">The zoom.</param>
    /// <returns>How much of the image, 0 to 1 each way, one whole surface spans.</returns>
    public static (double X, double Y) Span(int imageWidth, int imageHeight, double surfaceWidth, double surfaceHeight, double zoom)
    {
        double imageAspect = (double)imageWidth / imageHeight;
        double surfaceAspect = surfaceWidth / surfaceHeight;
        double x = surfaceAspect > imageAspect ? surfaceAspect / imageAspect : 1;
        double y = surfaceAspect > imageAspect ? 1 : imageAspect / surfaceAspect;
        return (x / zoom, y / zoom);
    }

    /// <summary>The image point under a surface point.</summary>
    /// <param name="view">The view.</param>
    /// <param name="imageWidth">Image pixels across.</param>
    /// <param name="imageHeight">Image pixels down.</param>
    /// <param name="surfaceWidth">Surface pixels across.</param>
    /// <param name="surfaceHeight">Surface pixels down.</param>
    /// <param name="px">The surface point, in pixels from the left.</param>
    /// <param name="py">The surface point, in pixels from the top.</param>
    /// <returns>The image point, 0 to 1 each way inside the image.</returns>
    public static (double X, double Y) ImagePoint(View view, int imageWidth, int imageHeight, double surfaceWidth, double surfaceHeight, double px, double py)
    {
        (double sx, double sy) = Span(imageWidth, imageHeight, surfaceWidth, surfaceHeight, view.Zoom);
        return (view.CenterX + (((px / surfaceWidth) - 0.5) * sx), view.CenterY + (((py / surfaceHeight) - 0.5) * sy));
    }

    /// <summary>Zooms by a factor keeping the image point under the cursor where it is.</summary>
    /// <param name="view">The view.</param>
    /// <param name="factor">More than 1 zooms in.</param>
    /// <param name="imageWidth">Image pixels across.</param>
    /// <param name="imageHeight">Image pixels down.</param>
    /// <param name="surfaceWidth">Surface pixels across.</param>
    /// <param name="surfaceHeight">Surface pixels down.</param>
    /// <param name="px">The cursor, in surface pixels from the left.</param>
    /// <param name="py">The cursor, in surface pixels from the top.</param>
    /// <returns>The new view.</returns>
    public static View ZoomAt(View view, double factor, int imageWidth, int imageHeight, double surfaceWidth, double surfaceHeight, double px, double py)
    {
        double zoom = Math.Clamp(view.Zoom * factor, MinZoom, MaxZoom);
        (double ix, double iy) = ImagePoint(view, imageWidth, imageHeight, surfaceWidth, surfaceHeight, px, py);
        (double sx, double sy) = Span(imageWidth, imageHeight, surfaceWidth, surfaceHeight, zoom);
        View zoomed = new(zoom, ix - (((px / surfaceWidth) - 0.5) * sx), iy - (((py / surfaceHeight) - 0.5) * sy));
        return zoom <= MinZoom ? View.Fit : zoomed;
    }

    /// <summary>Moves the view by a drag of so many surface pixels.</summary>
    /// <param name="view">The view.</param>
    /// <param name="dx">Pixels the cursor moved right.</param>
    /// <param name="dy">Pixels the cursor moved down.</param>
    /// <param name="imageWidth">Image pixels across.</param>
    /// <param name="imageHeight">Image pixels down.</param>
    /// <param name="surfaceWidth">Surface pixels across.</param>
    /// <param name="surfaceHeight">Surface pixels down.</param>
    /// <returns>The new view, its centre kept inside the image.</returns>
    public static View Pan(View view, double dx, double dy, int imageWidth, int imageHeight, double surfaceWidth, double surfaceHeight)
    {
        (double sx, double sy) = Span(imageWidth, imageHeight, surfaceWidth, surfaceHeight, view.Zoom);
        return view with
        {
            CenterX = Math.Clamp(view.CenterX - (dx / surfaceWidth * sx), 0, 1),
            CenterY = Math.Clamp(view.CenterY - (dy / surfaceHeight * sy), 0, 1),
        };
    }

    /// <summary>Whether a surface point is close enough to the divider to grab it.</summary>
    /// <param name="divider">The divider, 0 to 1 across the surface.</param>
    /// <param name="px">The point, in surface pixels from the left.</param>
    /// <param name="surfaceWidth">Surface pixels across.</param>
    /// <param name="grab">How many pixels either side count.</param>
    /// <returns>True within the grab distance.</returns>
    public static bool OnDivider(double divider, double px, double surfaceWidth, double grab = 8) =>
        Math.Abs(px - (divider * surfaceWidth)) <= grab;
}
