using System.Buffers;
using System.Runtime.InteropServices;

namespace SpaceDeck.Core.Screens;

/// <summary>A picture in memory: 4 bytes a pixel in the order B, G, R, A, rows top to bottom, no padding.</summary>
public sealed class BgraImage
{
    /// <exception cref="ArgumentException">The size is not positive or <paramref name="pixels"/> is too short.</exception>
    public BgraImage(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width < 1 || height < 1 || (long)width * height * 4 > pixels.Length)
        {
            throw new ArgumentException("Expected a positive size and width × height × 4 bytes.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>A new picture of this size, every byte 0 (transparent black).</summary>
    public BgraImage(int width, int height)
        : this(width, height, new byte[checked(Math.Max(1, width) * Math.Max(1, height) * 4)])
    {
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The pixels (may be longer than <see cref="Width"/> × <see cref="Height"/> × 4; the rest is unused).</summary>
    public byte[] Pixels { get; }

    public int Stride => Width * 4;
}

/// <summary>
/// Pure pixel work for the cooler screen, done off the UI thread: scaling a picture onto the screen (fill or fit,
/// over a background colour) and turning it for a cooler mounted turned.
/// </summary>
public static class ScreenCompositor
{
    /// <summary>
    /// Draws <paramref name="source"/> into all of <paramref name="destination"/>, placed by <paramref name="fit"/>
    /// (<see cref="ScreenGeometry.ImageRect"/>), over <paramref name="backgroundRgb"/> (0xRRGGBB): the bars of a fit,
    /// and whatever shows through transparent pixels. The result is opaque (alpha 255).
    /// </summary>
    /// <param name="opaque">The source has no transparency (video frames, whose fourth byte is undefined).</param>
    /// <remarks>
    /// Scaled with a triangle filter as wide as the scale (bilinear when enlarging, an area average when shrinking, so a
    /// large picture doesn't shimmer), in premultiplied alpha so transparent edges don't darken.
    /// </remarks>
    public static void Compose(BgraImage source, bool opaque, BgraImage destination, ImageFit fit, int backgroundRgb)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var backB = backgroundRgb & 0xFF;
        var backG = (backgroundRgb >> 8) & 0xFF;
        var backR = (backgroundRgb >> 16) & 0xFF;

        var placed = ScreenGeometry.ImageRect(source.Width, source.Height, destination.Width, destination.Height, fit);
        var x0 = Math.Clamp((int)Math.Round(placed.X), 0, destination.Width);
        var x1 = Math.Clamp((int)Math.Round(placed.Right), x0, destination.Width);
        var y0 = Math.Clamp((int)Math.Round(placed.Y), 0, destination.Height);
        var y1 = Math.Clamp((int)Math.Round(placed.Bottom), y0, destination.Height);

        var output = destination.Pixels;
        var outStride = destination.Stride;
        for (var y = 0; y < destination.Height; y++)
        {
            for (var x = 0; x < destination.Width; x++)
            {
                if (y >= y0 && y < y1 && x >= x0 && x < x1)
                {
                    x = x1 - 1; // the picture's own pixels are written below
                    continue;
                }

                var o = (y * outStride) + (x * 4);
                output[o] = (byte)backB;
                output[o + 1] = (byte)backG;
                output[o + 2] = (byte)backR;
                output[o + 3] = 255;
            }
        }

        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        var columns = Taps(source.Width, placed.X, placed.Width, x0, x1);
        var rows = Taps(source.Height, placed.Y, placed.Height, y0, y1);
        var firstRow = rows.Min(t => t.Start);
        var lastRow = rows.Max(t => t.Start + t.Weights.Length - 1);
        var outWidth = x1 - x0;
        var rowCount = lastRow - firstRow + 1;
        var temp = ArrayPool<float>.Shared.Rent(rowCount * outWidth * 4);
        try
        {
            // Across: each needed source row, resampled to the output width, premultiplied.
            var input = source.Pixels;
            var inStride = source.Stride;
            for (var r = 0; r < rowCount; r++)
            {
                var rowStart = (firstRow + r) * inStride;
                for (var x = 0; x < outWidth; x++)
                {
                    var tap = columns[x];
                    float b = 0, g = 0, red = 0, a = 0;
                    for (var k = 0; k < tap.Weights.Length; k++)
                    {
                        var i = rowStart + ((tap.Start + k) * 4);
                        var weight = tap.Weights[k];
                        var alpha = opaque ? 1f : input[i + 3] / 255f;
                        var wa = weight * alpha;
                        b += input[i] * wa;
                        g += input[i + 1] * wa;
                        red += input[i + 2] * wa;
                        a += wa;
                    }

                    var t = ((r * outWidth) + x) * 4;
                    temp[t] = b;
                    temp[t + 1] = g;
                    temp[t + 2] = red;
                    temp[t + 3] = a;
                }
            }

            // Down: each output row from those, then over the background.
            for (var y = y0; y < y1; y++)
            {
                var tap = rows[y - y0];
                for (var x = 0; x < outWidth; x++)
                {
                    float b = 0, g = 0, red = 0, a = 0;
                    for (var k = 0; k < tap.Weights.Length; k++)
                    {
                        var t = (((tap.Start + k - firstRow) * outWidth) + x) * 4;
                        var weight = tap.Weights[k];
                        b += temp[t] * weight;
                        g += temp[t + 1] * weight;
                        red += temp[t + 2] * weight;
                        a += temp[t + 3] * weight;
                    }

                    var rest = 1 - Math.Clamp(a, 0f, 1f);
                    var o = (y * outStride) + ((x0 + x) * 4);
                    output[o] = ToByte(b + (backB * rest));
                    output[o + 1] = ToByte(g + (backG * rest));
                    output[o + 2] = ToByte(red + (backR * rest));
                    output[o + 3] = 255;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(temp);
        }
    }

    /// <summary>
    /// <paramref name="source"/> turned clockwise by <paramref name="rotation"/> (0, 90, 180, 270) into
    /// <paramref name="destination"/>, which must be the turned size (height × width for 90 and 270).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> has the wrong size.</exception>
    public static void Rotate(BgraImage source, int rotation, BgraImage destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        rotation = CoolerScreenSettings.NormalizeRotation(rotation);
        var (width, height) = ScreenGeometry.DrawSize(source.Width, source.Height, rotation);
        if (destination.Width != width || destination.Height != height)
        {
            throw new ArgumentException("The destination must be the turned size.", nameof(destination));
        }

        var from = MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan(0, source.Width * source.Height * 4));
        var to = MemoryMarshal.Cast<byte, uint>(destination.Pixels.AsSpan(0, width * height * 4));
        if (rotation == 0)
        {
            from.CopyTo(to);
            return;
        }

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var (tx, ty) = ScreenGeometry.Rotate(x, y, source.Width, source.Height, rotation);
                to[(ty * width) + tx] = from[(y * source.Width) + x];
            }
        }
    }

    /// <summary><paramref name="source"/> turned clockwise by <paramref name="rotation"/>, as a new picture.</summary>
    public static BgraImage Rotate(BgraImage source, int rotation)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (width, height) = ScreenGeometry.DrawSize(source.Width, source.Height, rotation);
        var result = new BgraImage(width, height);
        Rotate(source, rotation, result);
        return result;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);

    /// <summary>
    /// For output pixels <paramref name="from"/>…<paramref name="to"/> of a picture placed at
    /// <paramref name="offset"/> with <paramref name="size"/> output pixels, the source pixels each one reads and how much.
    /// </summary>
    private static Tap[] Taps(int sourceSize, double offset, double size, int from, int to)
    {
        var perOutput = sourceSize / size; // source pixels per output pixel
        var support = Math.Max(1.0, perOutput);
        var taps = new Tap[to - from];
        for (var i = from; i < to; i++)
        {
            var center = ((i + 0.5 - offset) * perOutput) - 0.5;
            var first = (int)Math.Ceiling(center - support);
            var last = (int)Math.Floor(center + support);
            var start = Math.Clamp(first, 0, sourceSize - 1);
            var end = Math.Clamp(last, 0, sourceSize - 1);
            var weights = new float[end - start + 1];
            var total = 0.0;
            for (var s = first; s <= last; s++)
            {
                var weight = Math.Max(0, 1 - (Math.Abs(s - center) / support));
                total += weight;
                weights[Math.Clamp(s, 0, sourceSize - 1) - start] += (float)weight; // past the edge: the edge pixel
            }

            if (total <= 0)
            {
                weights = [1f];
                start = Math.Clamp((int)Math.Round(center), 0, sourceSize - 1);
                total = 1;
            }

            for (var k = 0; k < weights.Length; k++)
            {
                weights[k] = (float)(weights[k] / total);
            }

            taps[i - from] = new Tap(start, weights);
        }

        return taps;
    }

    private readonly record struct Tap(int Start, float[] Weights);
}
