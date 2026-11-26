using SpaceDeck.Sources.LianLi;

namespace SpaceDeck.Collector.Tests;

/// <summary>Galahad II status reply (command 0x81). Byte layout: docs/registers/lianli-galahad2.md.</summary>
public class LianLiGalahadProtocolTests
{
    /// <summary>A real reply from a Galahad II LCD, as captured in liquidctl's tests: fan 1440, pump 2670, 37.8 °C.</summary>
    private const string CapturedReply = "01810000000705a00a6e01250800000000000000000000000000000000000000";

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void CapturedReply_DecodesFanPumpAndCoolant()
    {
        Assert.True(GalahadProtocol.TryDecodeStatus(Bytes(CapturedReply), out var status));

        Assert.Equal(1440, status.FanRpm);
        Assert.Equal(2670, status.PumpRpm);
        Assert.Equal(7, status.PayloadLength);
        Assert.True(status.CoolantValid);
        Assert.NotNull(status.CoolantCelsius);
        Assert.Equal(37.8, status.CoolantCelsius!.Value, 3);
    }

    [Fact]
    public void ValidFlagClear_HasNoCoolant()
    {
        var reply = Bytes(CapturedReply);
        reply[10] = 0;

        Assert.True(GalahadProtocol.TryDecodeStatus(reply, out var status));

        Assert.False(status.CoolantValid);
        Assert.Null(status.CoolantCelsius);
        Assert.Equal(1440, status.FanRpm);
    }

    [Fact]
    public void FourBytePayload_HasFanAndPumpOnly()
    {
        // The Trinity-style reply: payload length 4, nothing after the pump speed.
        var reply = Bytes("018100000004" + "05a00a6e" + new string('0', 108));

        Assert.True(GalahadProtocol.TryDecodeStatus(reply, out var status));

        Assert.Equal(4, status.PayloadLength);
        Assert.Equal(1440, status.FanRpm);
        Assert.Equal(2670, status.PumpRpm);
        Assert.False(status.CoolantValid);
        Assert.Null(status.CoolantCelsius);
    }

    [Fact]
    public void TenthsAboveNine_KeepsWholeDegrees()
    {
        var reply = Bytes(CapturedReply);
        reply[12] = 12;

        Assert.True(GalahadProtocol.TryDecodeStatus(reply, out var status));

        Assert.Equal(37.0, status.CoolantCelsius!.Value, 3);
    }

    [Theory]
    [InlineData("018600000007" + "05a00a6e012508")] // another command (firmware query)
    [InlineData("028100000007" + "05a00a6e012508")] // another report id
    [InlineData("0181000000")] // shorter than the header
    [InlineData("018100000003" + "05a00a")] // payload too short for fan and pump
    [InlineData("018100000007" + "05a00a6e01")] // claims 7 payload bytes, has 5
    public void NotAStatusReply_IsRejected(string hex)
    {
        Assert.False(GalahadProtocol.TryDecodeStatus(Bytes(hex), out var status));
        Assert.Equal(default, status);
    }

    [Theory]
    [InlineData(0x7391, true, true)] // Galahad II LCD
    [InlineData(0x7395, true, true)] // Galahad II LCD, second id
    [InlineData(0x7371, true, false)] // Trinity Performance: bytes 10–12 aren't a temperature
    [InlineData(0x7373, true, false)] // Trinity
    [InlineData(0x8041, false, false)] // wireless receiver
    public void ProductIds(int productId, bool galahad, bool coolant)
    {
        Assert.Equal(galahad, GalahadProtocol.IsGalahad2((ushort)productId));
        Assert.Equal(coolant, GalahadProtocol.HasCoolantSensor((ushort)productId));
    }

    [Theory]
    [InlineData(0x81)]
    [InlineData(0x86)]
    public void Query_IsReportIdCommandAndZeros_PaddedToTheCollection(int command)
    {
        // The dev PC's Galahad II LCD (0x7395, firmware v1.6) has one collection sized for its 1024-byte image reports.
        var report = GalahadProtocol.Query((byte)command, 1024);

        Assert.Equal(1024, report.Length);
        Assert.Equal(0x01, report[0]);
        Assert.Equal(command, report[1]);
        Assert.All(report[2..], b => Assert.Equal(0, b));
        Assert.True(GalahadProtocol.IsQuery(report));
    }

    [Theory]
    [InlineData(0x8A)] // set pump duty
    [InlineData(0x8B)] // set fan duty
    [InlineData(0x83)] // pump lighting
    [InlineData(0x8E)] // reset
    [InlineData(0x00)]
    public void Query_RefusesEveryOtherCommand(int command)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GalahadProtocol.Query((byte)command, 64));
    }

    [Fact]
    public void Query_RefusesAReportShorterThanACommandPacket()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GalahadProtocol.Query(GalahadProtocol.StatusCommand, 63));
    }

    [Fact]
    public void IsQuery_RejectsAnythingWithAPayload()
    {
        var withPayload = GalahadProtocol.Query(GalahadProtocol.StatusCommand, 64);
        withPayload[6] = 0x32; // a duty value: never allowed through

        Assert.False(GalahadProtocol.IsQuery(withPayload));
    }

    [Fact]
    public void IsQuery_RejectsSetCommandsAndOtherReportIds()
    {
        var setPump = new byte[64];
        setPump[0] = 0x01;
        setPump[1] = 0x8A;
        var imageReport = new byte[1024];
        imageReport[0] = 0x02;
        imageReport[1] = 0x81;

        Assert.False(GalahadProtocol.IsQuery(setPump));
        Assert.False(GalahadProtocol.IsQuery(imageReport));
        Assert.False(GalahadProtocol.IsQuery(new byte[10]));
    }

    [Fact]
    public void FirmwareReply_DecodesItsText()
    {
        // "N9,01" as the start of a firmware string, then zeros.
        var reply = Bytes("018600000005" + "4e392c3031" + new string('0', 106));

        Assert.Equal("N9,01", GalahadProtocol.TryDecodeFirmwareText(reply));
        Assert.Null(GalahadProtocol.TryDecodeFirmwareText(Bytes(CapturedReply)));
    }
}
