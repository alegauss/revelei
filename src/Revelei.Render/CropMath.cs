using Revelei.Imaging;

namespace Revelei.Render;

/// <summary>Which part of a crop rectangle a press landed on (PB67).</summary>
public enum CropHandle
{
    /// <summary>Outside it: the press pans the view instead.</summary>
    None,

    /// <summary>Inside it, away from the handles: the press moves the whole rectangle.</summary>
    Move,

    /// <summary>The top-left corner.</summary>
    TopLeft,

    /// <summary>The middle of the top edge.</summary>
    Top,

    /// <summary>The top-right corner.</summary>
    TopRight,

    /// <summary>The middle of the right edge.</summary>
    Right,

    /// <summary>The bottom-right corner.</summary>
    BottomRight,

    /// <summary>The middle of the bottom edge.</summary>
    Bottom,

    /// <summary>The bottom-left corner.</summary>
    BottomLeft,

    /// <summary>The middle of the left edge.</summary>
    Left,
}

/// <summary>
/// The crop rectangle's arithmetic (PB67), in the photo's own pixels: where a press lands, what a
/// drag of a handle makes of it, the largest rectangle of an aspect, and keeping it whole pixels
/// inside the photo. Pure, as ViewMath is, so the surface's mouse handling and the tests share it.
/// </summary>
public static class CropMath
{
    /// <summary>The largest rectangle of an aspect the photo holds, centred; the whole photo when free.</summary>
    /// <param name="aspect">Width over height, or null for any.</param>
    /// <param name="width">Photo pixels across.</param>
    /// <param name="height">Photo pixels down.</param>
    /// <returns>The rectangle.</returns>
    public static PixelRect Fit(double? aspect, int width, int height)
    {
        if (aspect is not { } a)
        {
            return new PixelRect(0, 0, width, height);
        }

        (int w, int h) = (double)width / height > a
            ? (Math.Max(1, (int)Math.Round(height * a)), height)
            : (width, Math.Max(1, (int)Math.Round(width / a)));
        return new PixelRect((width - w) / 2, (height - h) / 2, w, h);
    }

    /// <summary>A rectangle moved and shrunk until it is at least a pixel and inside the photo.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="width">Photo pixels across.</param>
    /// <param name="height">Photo pixels down.</param>
    /// <returns>The rectangle inside the photo.</returns>
    public static PixelRect Clamp(PixelRect rect, int width, int height)
    {
        int w = Math.Clamp(rect.Width, 1, width), h = Math.Clamp(rect.Height, 1, height);
        return new PixelRect(Math.Clamp(rect.X, 0, width - w), Math.Clamp(rect.Y, 0, height - h), w, h);
    }

    /// <summary>
    /// What a press at a photo point grabs: a corner within the grab distance, then an edge anywhere
    /// along its length (PB69: only its middle handle counted, so most presses on an edge missed),
    /// then the inside, or nothing.
    /// </summary>
    /// <param name="rect">The crop rectangle.</param>
    /// <param name="x">The point, in photo pixels from the left.</param>
    /// <param name="y">The point, in photo pixels from the top.</param>
    /// <param name="grab">How far from a handle still counts, in photo pixels.</param>
    /// <returns>The handle.</returns>
    public static CropHandle HitTest(PixelRect rect, double x, double y, double grab)
    {
        double left = rect.X, top = rect.Y, right = rect.X + rect.Width, bottom = rect.Y + rect.Height;
        bool nearLeft = Math.Abs(x - left) <= grab, nearRight = Math.Abs(x - right) <= grab;
        bool nearTop = Math.Abs(y - top) <= grab, nearBottom = Math.Abs(y - bottom) <= grab;
        bool alongX = x >= left - grab && x <= right + grab, alongY = y >= top - grab && y <= bottom + grab;
        return (nearLeft, nearRight, nearTop, nearBottom) switch
        {
            (true, _, true, _) => CropHandle.TopLeft,
            (_, true, true, _) => CropHandle.TopRight,
            (_, true, _, true) => CropHandle.BottomRight,
            (true, _, _, true) => CropHandle.BottomLeft,
            (_, _, true, _) when alongX => CropHandle.Top,
            (_, _, _, true) when alongX => CropHandle.Bottom,
            (true, _, _, _) when alongY => CropHandle.Left,
            (_, true, _, _) when alongY => CropHandle.Right,
            _ when x > left && x < right && y > top && y < bottom => CropHandle.Move,
            _ => CropHandle.None,
        };
    }

    /// <summary>
    /// The rectangle after a handle is dragged by so many photo pixels. A corner keeps the opposite
    /// corner where it was, an edge keeps the opposite edge, and with an aspect the other side follows
    /// about the centre; nothing crosses its anchor or leaves the photo.
    /// </summary>
    /// <param name="start">The rectangle when the press began.</param>
    /// <param name="handle">What the press grabbed.</param>
    /// <param name="dx">Photo pixels the pointer moved right since then.</param>
    /// <param name="dy">Photo pixels the pointer moved down.</param>
    /// <param name="aspect">Width over height to keep, or null for any.</param>
    /// <param name="width">Photo pixels across.</param>
    /// <param name="height">Photo pixels down.</param>
    /// <returns>The new rectangle, in whole pixels.</returns>
    public static PixelRect Drag(PixelRect start, CropHandle handle, double dx, double dy, double? aspect, int width, int height)
    {
        double left = start.X, top = start.Y, right = start.X + start.Width, bottom = start.Y + start.Height;
        (double x, double y, double w, double h) = handle switch
        {
            CropHandle.Move => (Math.Clamp(left + dx, 0, width - start.Width), Math.Clamp(top + dy, 0, height - start.Height), start.Width, start.Height),
            CropHandle.TopLeft => Corner(right, bottom, -1, -1, left + dx, top + dy),
            CropHandle.TopRight => Corner(left, bottom, 1, -1, right + dx, top + dy),
            CropHandle.BottomRight => Corner(left, top, 1, 1, right + dx, bottom + dy),
            CropHandle.BottomLeft => Corner(right, top, -1, 1, left + dx, bottom + dy),
            CropHandle.Left => Across(right, -1, left + dx),
            CropHandle.Right => Across(left, 1, right + dx),
            CropHandle.Top => Down(bottom, -1, top + dy),
            CropHandle.Bottom => Down(top, 1, bottom + dy),
            _ => (left, top, start.Width, start.Height),
        };

        PixelRect snapped = new((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h));
        return Clamp(snapped, width, height);

        // The corner at (cx, cy), anchored at (ax, ay) and growing in the direction (sx, sy).
        (double, double, double, double) Corner(double ax, double ay, int sx, int sy, double cx, double cy)
        {
            double roomX = sx > 0 ? width - ax : ax, roomY = sy > 0 ? height - ay : ay;
            double cw = Math.Clamp(sx * (cx - ax), 1, roomX), ch = Math.Clamp(sy * (cy - ay), 1, roomY);
            if (aspect is { } a)
            {
                ch = cw / a;
                if (ch > roomY)
                {
                    (ch, cw) = (roomY, roomY * a);
                }
            }

            return (sx > 0 ? ax : ax - cw, sy > 0 ? ay : ay - ch, cw, ch);
        }

        // An upright edge at ex, the opposite one anchored at ax; with an aspect the height follows.
        (double, double, double, double) Across(double ax, int sx, double ex)
        {
            double room = sx > 0 ? width - ax : ax;
            double cw = Math.Clamp(sx * (ex - ax), 1, room), ch = start.Height, cy = (top + bottom) / 2;
            if (aspect is { } a)
            {
                double roomY = 2 * Math.Min(cy, height - cy);
                (cw, ch) = cw / a > roomY ? (roomY * a, roomY) : (cw, cw / a);
            }

            return (sx > 0 ? ax : ax - cw, aspect is null ? top : cy - (ch / 2), cw, ch);
        }

        // A level edge at ey, the opposite one anchored at ay; with an aspect the width follows.
        (double, double, double, double) Down(double ay, int sy, double ey)
        {
            double room = sy > 0 ? height - ay : ay;
            double ch = Math.Clamp(sy * (ey - ay), 1, room), cw = start.Width, cx = (left + right) / 2;
            if (aspect is { } a)
            {
                double roomX = 2 * Math.Min(cx, width - cx);
                (cw, ch) = ch * a > roomX ? (roomX, roomX / a) : (ch * a, ch);
            }

            return (aspect is null ? left : cx - (cw / 2), sy > 0 ? ay : ay - ch, cw, ch);
        }
    }
}
