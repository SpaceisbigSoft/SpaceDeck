using System.Buffers.Binary;
using SensorDeck.Core.Model;
using SensorDeck.Core.Protocol;

namespace SensorDeck.Core.Tests;

public class FrameCodecTests
{
    private static SensorCatalog SampleCatalog() => new(
        Version: 3,
        Hardware:
        [
            new HardwareInfo("lhm:/motherboard", "ASUS ROG STRIX X670E-E", HardwareKind.Motherboard, null, "lhm"),
            new HardwareInfo("lhm:/lpc/nct6799d/0", "Nuvoton NCT6799D", HardwareKind.SuperIo, "lhm:/motherboard", "lhm"),
        ],
        Sensors:
        [
            new SensorInfo("lhm:/lpc/nct6799d/0/voltage/1", "lhm:/lpc/nct6799d/0", "+12V", SensorKind.Voltage, "V", "lhm", 12.0),
            new SensorInfo("lhm:/lpc/nct6799d/0/fan/0", "lhm:/lpc/nct6799d/0", "CPU Fan", SensorKind.Fan, "RPM", "lhm", null),
        ]);

    [Fact]
    public async Task Read_UnknownMessageType_IsSkippedNotADisconnect()
    {
        byte[] payload = [0x92, 0xCC, 200, 0x90]; // union key 200: a message from a newer version
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        await FrameCodec.WriteAsync(stream, new ResetMinMaxRequest(null));
        stream.Position = 0;

        Assert.IsType<UnknownMessage>(await FrameCodec.ReadAsync(stream));
        Assert.IsType<ResetMinMaxRequest>(await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task RoundTrip_PreservesEveryMessageType()
    {
        IProtocolMessage[] messages =
        [
            new HelloMessage(ProtocolInfo.Version, "0.1.0", 1000),
            new CatalogMessage(SampleCatalog()),
            new ValuesMessage(3, 42, 1_761_177_600_000, [12.096f, null], [11.9f, null], [12.2f, 1400f]),
            new HistoryRequest(7, ["lhm:/lpc/nct6799d/0/fan/0"], 300),
            new HistoryResponse(7, [1, 2], ["a"], [[1f, null]]),
            new SetConfigRequest(500, true, null),
            new SetConfigRequest(null, null, null, ControllersEnabled: true, Persist: true),
            new CollectorStatusMessage(1000, false, true, false, 3600, IsService: true, IsElevated: true, @"C:\ProgramData\SensorDeck\collector.json", null),
            new SourceStatusMessage([new SourceStatusEntry("lhm", SourceStatus.DriverMissing, "PawnIO is not installed.")]),
            new HistoryRequest(8, ["a", "b"], 0, 1_000, 2_000, MaxPoints: 600),
            new HistoryResponse(8, [1, 2], ["a"], [[1f, null]], [[0.5f, null]], [[2f, null]], NextFromUnixMs: 3),
            new ResetMinMaxRequest(null),
            new ResetMinMaxRequest(["a"]),
            new CollectorStatusMessage(1000, false, true, false, 3600, true, true, "x", null, StartedUnixMs: 5, MinMaxResetUnixMs: 6, DiskHistory: true),
        ];

        using var stream = new MemoryStream();
        foreach (var message in messages)
        {
            await FrameCodec.WriteAsync(stream, message);
        }

        stream.Position = 0;
        foreach (var expected in messages)
        {
            var actual = await FrameCodec.ReadAsync(stream);
            Assert.NotNull(actual);
            Assert.IsType(expected.GetType(), actual);
            // Records compare arrays by reference, so compare the serialized form instead.
            Assert.Equal(FrameCodec.Serialize(expected), FrameCodec.Serialize(actual));
        }

        Assert.Null(await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Catalog_SurvivesRoundTripFieldByField()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new CatalogMessage(SampleCatalog()));
        stream.Position = 0;

        var result = Assert.IsType<CatalogMessage>(await FrameCodec.ReadAsync(stream));
        Assert.Equal(3, result.Catalog.Version);
        Assert.Equal(2, result.Catalog.Sensors.Count);
        Assert.Equal(SampleCatalog().Sensors[0], result.Catalog.Sensors[0]);
        Assert.Equal(SampleCatalog().Hardware[1], result.Catalog.Hardware[1]);
    }

    [Fact]
    public async Task Read_RejectsOversizedLength()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, FrameCodec.MaxFrameBytes + 1);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_RejectsFrameOverCallersLimit()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new HistoryRequest(1, [new string('x', 2000)], 60));
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadAsync(stream, 1000));
    }

    [Fact]
    public async Task Read_ThrowsOnTruncatedFrame()
    {
        using var full = new MemoryStream();
        await FrameCodec.WriteAsync(full, new HelloMessage(1, "x", 1000));
        var truncated = full.ToArray()[..^2];
        using var stream = new MemoryStream(truncated);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await FrameCodec.ReadAsync(stream));
    }
}
