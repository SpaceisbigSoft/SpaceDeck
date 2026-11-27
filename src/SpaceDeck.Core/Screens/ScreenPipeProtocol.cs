using System.Buffers.Binary;
using SpaceDeck.Core.PluginPipe;

namespace SpaceDeck.Core.Screens;

/// <summary>
/// The screen pipe (<c>\\.\pipe\SpaceDeck.Screen</c>, <c>docs/SCREEN-PIPE.md</c> §3): binary messages from the collector to
/// SpaceDeck's FanControl plugin, one JPEG frame each. Collector → plugin only.
/// </summary>
/// <remarks>
/// A message, all integers little-endian: magic <c>"SDSF"</c> (53 44 53 46), version (1 byte, 1), kind (1 byte, 1 = JPEG
/// frame), id length N (u16, 1–128), the plugin's own hardware id (N ASCII bytes, the catalog id rule), payload length L
/// (u32, 1–262144), then the L JPEG bytes.
/// </remarks>
public static class ScreenPipeProtocol
{
    /// <summary>Pipe name (<c>\\.\pipe\SpaceDeck.Screen</c>).</summary>
    public const string PipeName = "SpaceDeck.Screen";

    public const byte Version = 1;

    /// <summary>Message kind: one JPEG frame.</summary>
    public const byte KindJpeg = 1;

    /// <summary>Fixed bytes before the id: magic, version, kind, id length.</summary>
    public const int HeaderBytes = 8;

    public const int MaxIdLength = PluginPipeProtocol.MaxIdLength;

    public const int MaxPayloadBytes = ScreenFrameRules.MaxJpegBytes;

    /// <summary>The status entry the collector shows for this pipe, next to the <c>lianli</c> source.</summary>
    public const string StatusSource = "lianli-screen";

    /// <summary>"SDSF".</summary>
    public static ReadOnlySpan<byte> Magic => "SDSF"u8;

    /// <summary>
    /// One message carrying <paramref name="jpeg"/> for the plugin's hardware <paramref name="pluginHardwareId"/> (its own
    /// id, e.g. <c>LianLi/0</c>, without the collector's <c>lianli:/</c> prefix).
    /// </summary>
    /// <exception cref="ArgumentException">The id breaks the catalog id rule, or the payload is empty, over 256 KiB or not
    /// framed by FF D8 … FF D9 (the plugin would disconnect for any of these).</exception>
    public static byte[] Encode(string pluginHardwareId, ReadOnlySpan<byte> jpeg)
    {
        ArgumentNullException.ThrowIfNull(pluginHardwareId);
        if (!PluginPipeSession.IsValidId(pluginHardwareId))
        {
            throw new ArgumentException("The hardware id must be 1–128 characters of A–Z a–z 0–9 / . _ : -.", nameof(pluginHardwareId));
        }

        if (jpeg.Length is 0 or > MaxPayloadBytes)
        {
            throw new ArgumentException("The payload must be 1 byte to 256 KiB.", nameof(jpeg));
        }

        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[^2] != 0xFF || jpeg[^1] != 0xD9)
        {
            throw new ArgumentException("The payload must start with FF D8 and end with FF D9.", nameof(jpeg));
        }

        var idLength = pluginHardwareId.Length; // ASCII only (checked above): one byte per character
        var message = new byte[HeaderBytes + idLength + 4 + jpeg.Length];
        var span = message.AsSpan();
        Magic.CopyTo(span);
        span[4] = Version;
        span[5] = KindJpeg;
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)idLength);
        for (var i = 0; i < idLength; i++)
        {
            span[HeaderBytes + i] = (byte)pluginHardwareId[i];
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span[(HeaderBytes + idLength)..], (uint)jpeg.Length);
        jpeg.CopyTo(span[(HeaderBytes + idLength + 4)..]);
        return message;
    }
}
