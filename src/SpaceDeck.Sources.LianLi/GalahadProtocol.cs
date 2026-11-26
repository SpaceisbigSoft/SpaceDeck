using System.Buffers.Binary;

namespace SpaceDeck.Sources.LianLi;

/// <summary>One decoded status reply (command 0x81) from a Lian Li Galahad II cooler.</summary>
/// <param name="FanRpm">Radiator fan speed.</param>
/// <param name="PumpRpm">Pump speed.</param>
/// <param name="PayloadLength">Payload length byte of the reply (4 = fan and pump only, 7 = with coolant).</param>
/// <param name="CoolantValid">The cooler's own "coolant temperature is valid" flag (false when the reply has none).</param>
/// <param name="CoolantCelsius">Coolant temperature, or null when the reply has no valid one.</param>
public readonly record struct GalahadStatus(int FanRpm, int PumpRpm, int PayloadLength, bool CoolantValid, double? CoolantCelsius);

/// <summary>
/// The Galahad II status reply. Byte layout: <c>docs/registers/lianli-galahad2.md</c> (facts from liquidctl's capture
/// and lian-li-linux, both MIT; no code copied).
/// </summary>
public static class GalahadProtocol
{
    public const ushort VendorId = 0x0416;

    /// <summary>Galahad II LCD (firmware codename "Vision").</summary>
    public const ushort LcdProductId = 0x7391;

    /// <summary>Second product id of the Galahad II LCD family.</summary>
    public const ushort LcdAlternateProductId = 0x7395;

    /// <summary>Galahad II Trinity Performance. Its status reply has no coolant temperature.</summary>
    public const ushort TrinityPerformanceProductId = 0x7371;

    /// <summary>Galahad II Trinity. Its status reply has no coolant temperature.</summary>
    public const ushort TrinityProductId = 0x7373;

    /// <summary>Report id of the 64-byte command packets (0x02 and 0x03 carry the screen's image data).</summary>
    public const byte ReportId = 0x01;

    /// <summary>Status query ("handshake"): the reply carries fan RPM, pump RPM and the coolant temperature.</summary>
    public const byte StatusCommand = 0x81;

    /// <summary>Firmware query: answered with two packets of ASCII text.</summary>
    public const byte FirmwareCommand = 0x86;

    /// <summary>Size of a command packet on the wire.</summary>
    public const int CommandPacketLength = 64;

    // [0] report id, [1] command, [2] 0, [3..4] packet number, [5] payload length, payload from [6].
    private const int HeaderLength = 6;
    private const int PayloadLengthOffset = 5;
    private const int MinStatusPayload = 4;
    private const int CoolantPayload = 7;

    /// <summary>True for every Galahad II product id (LCD and Trinity).</summary>
    public static bool IsGalahad2(ushort productId) =>
        productId is LcdProductId or LcdAlternateProductId or TrinityPerformanceProductId or TrinityProductId;

    /// <summary>True for the models whose status reply carries the coolant temperature (the LCD family).</summary>
    public static bool HasCoolantSensor(ushort productId) => productId is LcdProductId or LcdAlternateProductId;

    /// <summary>
    /// The only two requests SpaceDeck ever sends to the cooler: <see cref="StatusCommand"/> and
    /// <see cref="FirmwareCommand"/>, each with no payload. Both are questions; neither changes anything on the device.
    /// </summary>
    /// <param name="command">Must be <see cref="StatusCommand"/> or <see cref="FirmwareCommand"/>.</param>
    /// <param name="outputReportLength">The collection's output report length. Windows wants every write padded to it
    /// (this cooler's collection is sized for its 1024-byte image reports); the padding is zeros.</param>
    public static byte[] Query(byte command, int outputReportLength)
    {
        if (command is not (StatusCommand or FirmwareCommand))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "Only the status and firmware queries may be sent.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(outputReportLength, CommandPacketLength);
        var report = new byte[outputReportLength];
        report[0] = ReportId;
        report[1] = command;
        return report;
    }

    /// <summary>
    /// True only for a request <see cref="Query"/> builds: report id 0x01, the status or firmware command, and
    /// nothing else but zeros. Writers check this immediately before sending, so no other bytes can reach the device.
    /// </summary>
    public static bool IsQuery(ReadOnlySpan<byte> report)
    {
        if (report.Length < CommandPacketLength || report[0] != ReportId || report[1] is not (StatusCommand or FirmwareCommand))
        {
            return false;
        }

        return !report[2..].ContainsAnyExcept((byte)0);
    }

    /// <summary>The ASCII text in a firmware reply's payload (up to the first zero), or null for any other report.</summary>
    public static string? TryDecodeFirmwareText(ReadOnlySpan<byte> report)
    {
        if (report.Length < HeaderLength || report[0] != ReportId || report[1] != FirmwareCommand)
        {
            return null;
        }

        int length = Math.Min(report[PayloadLengthOffset], report.Length - HeaderLength);
        var payload = report.Slice(HeaderLength, length);
        var end = payload.IndexOf((byte)0);
        var text = payload[..(end >= 0 ? end : payload.Length)];
        var chars = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            chars[i] = text[i] is >= 0x20 and < 0x7F ? (char)text[i] : '?';
        }

        return new string(chars);
    }

    /// <summary>
    /// Decodes a status reply. Returns false for anything else (another command's reply, a short or damaged report).
    /// Doesn't check the product id: on the Trinity, bytes 10–12 are not a temperature, so callers that publish
    /// <see cref="GalahadStatus.CoolantCelsius"/> must check <see cref="HasCoolantSensor"/> first.
    /// </summary>
    public static bool TryDecodeStatus(ReadOnlySpan<byte> report, out GalahadStatus status)
    {
        status = default;
        if (report.Length < HeaderLength + MinStatusPayload || report[0] != ReportId || report[1] != StatusCommand)
        {
            return false;
        }

        int length = report[PayloadLengthOffset];
        if (length < MinStatusPayload || HeaderLength + length > report.Length)
        {
            return false;
        }

        var payload = report.Slice(HeaderLength, length);
        int fan = BinaryPrimitives.ReadUInt16BigEndian(payload);
        int pump = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        var valid = length >= 5 && payload[4] != 0;
        double? coolant = null;
        if (valid && length >= CoolantPayload)
        {
            // Whole degrees, then tenths. liquidctl adds the tenths byte / 10; lian-li-linux takes it modulo 10. They
            // agree for 0–9; a larger value would be a different encoding, so keep whole degrees only.
            int whole = payload[5];
            int tenths = payload[6];
            coolant = tenths <= 9 ? whole + (tenths / 10.0) : whole;
        }

        status = new GalahadStatus(fan, pump, length, valid, coolant);
        return true;
    }
}
