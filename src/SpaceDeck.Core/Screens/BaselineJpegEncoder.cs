namespace SpaceDeck.Core.Screens;

/// <summary>
/// A small baseline JPEG encoder (ITU T.81: JFIF, 8-bit YCbCr 4:4:4, the standard Annex K tables), so the CLI can make
/// test pictures for a cooler screen without WPF or System.Drawing. Slow but simple; meant for a few frames a second at
/// screen sizes. The app uses WPF's encoder.
/// </summary>
public static class BaselineJpegEncoder
{
    /// <summary>Zig-zag position → natural (row-major) index in an 8×8 block.</summary>
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly byte[] LuminanceQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly byte[] ChrominanceQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static readonly byte[] DcLuminanceBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];

    private static readonly byte[] DcChrominanceBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];

    private static readonly byte[] DcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] AcLuminanceBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];

    private static readonly byte[] AcLuminanceValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    private static readonly byte[] AcChrominanceBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    private static readonly byte[] AcChrominanceValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    /// <summary>cos((2x+1)uπ/16), [u * 8 + x].</summary>
    private static readonly double[] Cosines = BuildCosines();

    /// <summary>
    /// Encodes an RGB picture (3 bytes per pixel, rows top to bottom, no padding) as a baseline JPEG.
    /// </summary>
    /// <param name="quality">1–100, scaled like libjpeg's (50 = the Annex K tables).</param>
    /// <exception cref="ArgumentException">The size is out of range or <paramref name="rgb"/> isn't width × height × 3 bytes.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height, int quality)
    {
        if (width is < 1 or > 65535 || height is < 1 or > 65535)
        {
            throw new ArgumentException("Width and height must be 1–65535.", nameof(width));
        }

        if (rgb.Length != (long)width * height * 3)
        {
            throw new ArgumentException("Expected width × height × 3 bytes.", nameof(rgb));
        }

        var lumaQuant = Scale(LuminanceQuant, quality);
        var chromaQuant = Scale(ChrominanceQuant, quality);
        var dcLuma = BuildCodes(DcLuminanceBits, DcValues);
        var acLuma = BuildCodes(AcLuminanceBits, AcLuminanceValues);
        var dcChroma = BuildCodes(DcChrominanceBits, DcValues);
        var acChroma = BuildCodes(AcChrominanceBits, AcChrominanceValues);

        var output = new List<byte>(width * height / 4);
        WriteHeaders(output, width, height, lumaQuant, chromaQuant);

        var bits = new BitWriter(output);
        Span<double> y = stackalloc double[64];
        Span<double> cb = stackalloc double[64];
        Span<double> cr = stackalloc double[64];
        Span<int> zigzag = stackalloc int[64];
        Span<double> rows = stackalloc double[64];
        var previousDc = new int[3];
        for (var blockY = 0; blockY < height; blockY += 8)
        {
            for (var blockX = 0; blockX < width; blockX += 8)
            {
                for (var row = 0; row < 8; row++)
                {
                    var py = Math.Min(blockY + row, height - 1); // edge pixels repeat past the picture
                    for (var col = 0; col < 8; col++)
                    {
                        var px = Math.Min(blockX + col, width - 1);
                        var i = ((py * width) + px) * 3;
                        double r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
                        y[(row * 8) + col] = (0.299 * r) + (0.587 * g) + (0.114 * b) - 128;
                        cb[(row * 8) + col] = (-0.168736 * r) - (0.331264 * g) + (0.5 * b);
                        cr[(row * 8) + col] = (0.5 * r) - (0.418688 * g) - (0.081312 * b);
                    }
                }

                EncodeBlock(y, rows, zigzag, lumaQuant, ref previousDc[0], dcLuma, acLuma, bits);
                EncodeBlock(cb, rows, zigzag, chromaQuant, ref previousDc[1], dcChroma, acChroma, bits);
                EncodeBlock(cr, rows, zigzag, chromaQuant, ref previousDc[2], dcChroma, acChroma, bits);
            }
        }

        bits.Flush();
        output.Add(0xFF);
        output.Add(0xD9);
        return [.. output];
    }

    private static void EncodeBlock(Span<double> samples, Span<double> rows, Span<int> zigzag, byte[] quant, ref int previousDc,
        (int Code, int Length)[] dc, (int Code, int Length)[] ac, BitWriter bits)
    {
        // Separable 2-D DCT-II: along each row, then down each column.
        for (var row = 0; row < 8; row++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0.0;
                for (var x = 0; x < 8; x++)
                {
                    sum += samples[(row * 8) + x] * Cosines[(u * 8) + x];
                }

                rows[(row * 8) + u] = sum;
            }
        }

        for (var k = 0; k < 64; k++)
        {
            var natural = ZigZag[k];
            var v = natural / 8;
            var u = natural % 8;
            var sum = 0.0;
            for (var row = 0; row < 8; row++)
            {
                sum += rows[(row * 8) + u] * Cosines[(v * 8) + row];
            }

            var cu = u == 0 ? 1 / Math.Sqrt(2) : 1.0;
            var cv = v == 0 ? 1 / Math.Sqrt(2) : 1.0;
            zigzag[k] = (int)Math.Round(0.25 * cu * cv * sum / quant[natural]);
        }

        var diff = zigzag[0] - previousDc;
        previousDc = zigzag[0];
        var category = BitLength(diff);
        bits.Write(dc[category]);
        if (category > 0)
        {
            bits.Write(diff > 0 ? diff : diff + (1 << category) - 1, category);
        }

        var run = 0;
        for (var k = 1; k < 64; k++)
        {
            var value = zigzag[k];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                bits.Write(ac[0xF0]); // ZRL: sixteen zeros
                run -= 16;
            }

            category = BitLength(value);
            bits.Write(ac[(run << 4) | category]);
            bits.Write(value > 0 ? value : value + (1 << category) - 1, category);
            run = 0;
        }

        if (run > 0)
        {
            bits.Write(ac[0x00]); // EOB
        }
    }

    private static void WriteHeaders(List<byte> output, int width, int height, byte[] lumaQuant, byte[] chromaQuant)
    {
        output.AddRange([0xFF, 0xD8]);

        // APP0 JFIF 1.01, no density, no thumbnail.
        output.AddRange([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);

        // DQT: both tables, in zig-zag order.
        output.AddRange([0xFF, 0xDB, 0x00, 132, 0x00]);
        for (var k = 0; k < 64; k++)
        {
            output.Add(lumaQuant[ZigZag[k]]);
        }

        output.Add(0x01);
        for (var k = 0; k < 64; k++)
        {
            output.Add(chromaQuant[ZigZag[k]]);
        }

        // SOF0: 8-bit, three components, no subsampling; Y uses table 0, Cb and Cr table 1.
        output.AddRange([0xFF, 0xC0, 0x00, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
            1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1]);

        // DHT: DC/AC luminance (class/id 0x00, 0x10), DC/AC chrominance (0x01, 0x11).
        (byte Id, byte[] Bits, byte[] Values)[] tables =
        [
            (0x00, DcLuminanceBits, DcValues), (0x10, AcLuminanceBits, AcLuminanceValues),
            (0x01, DcChrominanceBits, DcValues), (0x11, AcChrominanceBits, AcChrominanceValues),
        ];
        var length = 2 + tables.Sum(t => 1 + 16 + t.Values.Length);
        output.AddRange([0xFF, 0xC4, (byte)(length >> 8), (byte)length]);
        foreach (var (id, bits, values) in tables)
        {
            output.Add(id);
            output.AddRange(bits);
            output.AddRange(values);
        }

        // SOS: all three components, full spectral range.
        output.AddRange([0xFF, 0xDA, 0x00, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
    }

    /// <summary>libjpeg's quality scaling of a base table.</summary>
    private static byte[] Scale(byte[] table, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        var scale = quality < 50 ? 5000 / quality : 200 - (2 * quality);
        var result = new byte[64];
        for (var i = 0; i < 64; i++)
        {
            result[i] = (byte)Math.Clamp(((table[i] * scale) + 50) / 100, 1, 255);
        }

        return result;
    }

    /// <summary>Canonical Huffman codes (T.81 Annex C), indexed by symbol.</summary>
    private static (int Code, int Length)[] BuildCodes(byte[] bits, byte[] values)
    {
        var codes = new (int Code, int Length)[256];
        var code = 0;
        var k = 0;
        for (var length = 1; length <= 16; length++)
        {
            for (var i = 0; i < bits[length - 1]; i++)
            {
                codes[values[k++]] = (code++, length);
            }

            code <<= 1;
        }

        return codes;
    }

    private static int BitLength(int value)
    {
        var magnitude = Math.Abs(value);
        var length = 0;
        while (magnitude != 0)
        {
            length++;
            magnitude >>= 1;
        }

        return length;
    }

    private static double[] BuildCosines()
    {
        var table = new double[64];
        for (var u = 0; u < 8; u++)
        {
            for (var x = 0; x < 8; x++)
            {
                table[(u * 8) + x] = Math.Cos(((2 * x) + 1) * u * Math.PI / 16);
            }
        }

        return table;
    }

    /// <summary>Entropy-coded bits, most significant first, with a 00 stuffed after every FF byte.</summary>
    private sealed class BitWriter(List<byte> output)
    {
        private int _buffer;
        private int _count;

        public void Write((int Code, int Length) code) => Write(code.Code, code.Length);

        public void Write(int bits, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((bits >> i) & 1);
                if (++_count == 8)
                {
                    output.Add((byte)_buffer);
                    if (_buffer == 0xFF)
                    {
                        output.Add(0x00);
                    }

                    _buffer = 0;
                    _count = 0;
                }
            }
        }

        /// <summary>Pads the last byte with 1 bits.</summary>
        public void Flush()
        {
            if (_count > 0)
            {
                Write((1 << (8 - _count)) - 1, 8 - _count);
            }
        }
    }
}
