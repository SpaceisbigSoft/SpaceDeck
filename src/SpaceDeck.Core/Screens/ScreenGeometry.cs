namespace SpaceDeck.Core.Screens;

/// <summary>A rectangle in pixels (fractions allowed).</summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>Placing a picture on a screen of another shape, and turning a picture for a cooler mounted turned.</summary>
public static class ScreenGeometry
{
    /// <summary>
    /// Where a <paramref name="sourceWidth"/> × <paramref name="sourceHeight"/> picture goes in a box, centred:
    /// <see cref="ImageFit.Fill"/> covers the box (the rectangle sticks out on two sides, that part is cut off),
    /// <see cref="ImageFit.Fit"/> fits inside it (bars on two sides). An empty picture or box gets the whole box.
    /// </summary>
    public static PixelRect ImageRect(double sourceWidth, double sourceHeight, double boxWidth, double boxHeight, ImageFit fit)
    {
        boxWidth = Math.Max(0, boxWidth);
        boxHeight = Math.Max(0, boxHeight);
        if (!(sourceWidth > 0) || !(sourceHeight > 0) || boxWidth == 0 || boxHeight == 0)
        {
            return new PixelRect(0, 0, boxWidth, boxHeight);
        }

        var scale = fit == ImageFit.Fit
            ? Math.Min(boxWidth / sourceWidth, boxHeight / sourceHeight)
            : Math.Max(boxWidth / sourceWidth, boxHeight / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new PixelRect((boxWidth - width) / 2, (boxHeight - height) / 2, width, height);
    }

    /// <summary>
    /// The part of the source that shows in the box (all of it for <see cref="ImageFit.Fit"/>, the centred middle for
    /// <see cref="ImageFit.Fill"/>), in source pixels.
    /// </summary>
    public static PixelRect VisibleSource(double sourceWidth, double sourceHeight, double boxWidth, double boxHeight, ImageFit fit)
    {
        var placed = ImageRect(sourceWidth, sourceHeight, boxWidth, boxHeight, fit);
        if (!(sourceWidth > 0) || !(sourceHeight > 0) || placed.Width <= 0 || placed.Height <= 0)
        {
            return new PixelRect(0, 0, Math.Max(0, sourceWidth), Math.Max(0, sourceHeight));
        }

        var perPixelX = sourceWidth / placed.Width;
        var perPixelY = sourceHeight / placed.Height;
        var left = Math.Max(0, -placed.X) * perPixelX;
        var top = Math.Max(0, -placed.Y) * perPixelY;
        var width = (Math.Min(boxWidth, placed.Right) - Math.Max(0, placed.X)) * perPixelX;
        var height = (Math.Min(boxHeight, placed.Bottom) - Math.Max(0, placed.Y)) * perPixelY;
        return new PixelRect(left, top, width, height);
    }

    /// <summary>
    /// The size a picture is drawn at, upright, so that turning it clockwise by <paramref name="rotation"/> gives the
    /// screen's own width × height (90 and 270 swap them).
    /// </summary>
    public static (int Width, int Height) DrawSize(int screenWidth, int screenHeight, int rotation) =>
        CoolerScreenSettings.NormalizeRotation(rotation) is 90 or 270 ? (screenHeight, screenWidth) : (screenWidth, screenHeight);

    /// <summary>
    /// Where pixel (<paramref name="x"/>, <paramref name="y"/>) of an upright <paramref name="width"/> ×
    /// <paramref name="height"/> picture lands once it is turned clockwise by <paramref name="rotation"/> (the turned
    /// picture is height × width for 90 and 270). Same direction as WPF's RotateTransform.
    /// </summary>
    public static (int X, int Y) Rotate(int x, int y, int width, int height, int rotation) => CoolerScreenSettings.NormalizeRotation(rotation) switch
    {
        90 => (height - 1 - y, x),
        180 => (width - 1 - x, height - 1 - y),
        270 => (y, width - 1 - x),
        _ => (x, y),
    };
}
