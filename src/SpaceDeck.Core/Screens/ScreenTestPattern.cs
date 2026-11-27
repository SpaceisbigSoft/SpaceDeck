namespace SpaceDeck.Core.Screens;

/// <summary>
/// A test picture for a cooler screen (<c>spacedeck-cli screen-test</c>): colour bars, the time in large digits, a frame
/// number and a marker that moves with each frame, so a live screen is easy to tell from a stuck one. RGB, 3 bytes per pixel.
/// </summary>
public static class ScreenTestPattern
{
    /// <summary>5×7 glyphs for 0–9 and ':', one row per byte (bit 4 = leftmost pixel).</summary>
    private static readonly byte[][] Glyphs =
    [
        [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110], // 0
        [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110], // 1
        [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111], // 2
        [0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110], // 3
        [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010], // 4
        [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110], // 5
        [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110], // 6
        [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000], // 7
        [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110], // 8
        [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100], // 9
        [0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b01100, 0b00000], // :
    ];

    /// <summary>White, yellow, cyan, green, magenta, red, blue, black.</summary>
    private static readonly (byte R, byte G, byte B)[] Bars =
    [
        (235, 235, 235), (235, 235, 16), (16, 235, 235), (16, 235, 16), (235, 16, 235), (235, 16, 16), (16, 16, 235), (16, 16, 16),
    ];

    /// <summary>Draws the pattern for <paramref name="time"/> and frame number <paramref name="frame"/>.</summary>
    public static byte[] Render(int width, int height, TimeOnly time, int frame)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var rgb = new byte[width * height * 3];
        Fill(rgb, width, 0, 0, width, height, (16, 20, 26));

        // Colour bars across the top third.
        var barsHeight = height / 3;
        for (var i = 0; i < Bars.Length; i++)
        {
            var left = i * width / Bars.Length;
            var right = (i + 1) * width / Bars.Length;
            Fill(rgb, width, left, 0, right - left, barsHeight, Bars[i]);
        }

        // The time, as wide as fits (8 glyphs of 6 cells with a space between).
        var text = time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var cell = Math.Max(1, Math.Min(width * 9 / 10 / ((text.Length * 6) - 1), height / 4 / 7));
        var textWidth = ((text.Length * 6) - 1) * cell;
        DrawText(rgb, width, height, text, (width - textWidth) / 2, barsHeight + (height / 12), cell, (255, 255, 255));

        // The frame number below, smaller, in the accent orange.
        var number = Math.Max(0, frame).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var small = Math.Max(1, cell / 2);
        var numberWidth = ((number.Length * 6) - 1) * small;
        var numberTop = barsHeight + (height / 12) + (8 * cell);
        DrawText(rgb, width, height, number, (width - numberWidth) / 2, numberTop, small, (255, 140, 0));

        // A grey ramp along the bottom, with a marker that steps along it every frame.
        var rampTop = height - Math.Max(1, height / 10);
        for (var x = 0; x < width; x++)
        {
            var level = (byte)(x * 255 / Math.Max(1, width - 1));
            Fill(rgb, width, x, rampTop, 1, height - rampTop, (level, level, level));
        }

        var markerSize = Math.Max(2, height / 20);
        var steps = Math.Max(1, width / markerSize);
        var markerLeft = Math.Max(0, frame) % steps * markerSize;
        Fill(rgb, width, markerLeft, rampTop - markerSize - 2, markerSize, markerSize, (255, 140, 0));
        return rgb;
    }

    private static void DrawText(byte[] rgb, int width, int height, string text, int left, int top, int cell, (byte R, byte G, byte B) color)
    {
        for (var c = 0; c < text.Length; c++)
        {
            var glyph = text[c] switch
            {
                >= '0' and <= '9' => Glyphs[text[c] - '0'],
                ':' => Glyphs[10],
                _ => null,
            };
            if (glyph is null)
            {
                continue;
            }

            for (var row = 0; row < 7; row++)
            {
                for (var col = 0; col < 5; col++)
                {
                    if ((glyph[row] & (1 << (4 - col))) != 0)
                    {
                        var x = left + (((c * 6) + col) * cell);
                        var y = top + (row * cell);
                        if (y < height)
                        {
                            Fill(rgb, width, x, y, cell, Math.Min(cell, height - y), color);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Fills a rectangle, clipped to the picture's width (callers keep it inside the height).</summary>
    private static void Fill(byte[] rgb, int width, int left, int top, int w, int h, (byte R, byte G, byte B) color)
    {
        var x0 = Math.Clamp(left, 0, width);
        var x1 = Math.Clamp(left + w, 0, width);
        var height = rgb.Length / 3 / width;
        var y1 = Math.Min(top + h, height);
        for (var y = Math.Max(0, top); y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = ((y * width) + x) * 3;
                rgb[i] = color.R;
                rgb[i + 1] = color.G;
                rgb[i + 2] = color.B;
            }
        }
    }
}
