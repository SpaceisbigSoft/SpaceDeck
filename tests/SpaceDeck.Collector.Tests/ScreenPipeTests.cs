using System.Buffers.Binary;
using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using SpaceDeck.Collector.Plugins;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.PluginPipe;
using SpaceDeck.Core.Protocol;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.Collector.Tests;

/// <summary>The cooler screen path in the collector: bridge screens, the relay's checks, and the real screen pipe server.</summary>
public sealed class ScreenPipeTests : IAsyncLifetime
{
    private const string HardwareId = "lianli:/LianLi/0";

    private static readonly PluginCatalog WithScreen = new(
        [
            new PluginHardware("LianLi/0", "Lian Li GA II LCD", PluginHardwareKind.Cooler, new ScreenInfo(480, 480)),
            new PluginHardware("LianLi/w1", "Lian Li TL Wireless", PluginHardwareKind.Fans),
        ],
        [new PluginSensor("LianLi/0/coolant/temp", "LianLi/0", "Coolant", PluginSensorKind.Temperature)]);

    private readonly string _pipeName = $"SpaceDeckScreenTest-{Guid.NewGuid():N}";
    private readonly PluginBridgeSource _bridge = new();
    private readonly TestVerifier _verifier = new();
    private ScreenRelay _relay = null!;
    private ScreenPipeServer _server = null!;

    public async Task InitializeAsync()
    {
        _bridge.Open();
        _relay = new ScreenRelay(_bridge, NullLogger<ScreenRelay>.Instance);
        _server = new ScreenPipeServer(_relay, _verifier, NullLogger<ScreenPipeServer>.Instance, _pipeName);
        await _server.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
    }

    /// <summary>SOI, a frame header of the given size, a few scan bytes, EOI.</summary>
    private static byte[] Jpeg(int width, int height, byte marker = 0x42) =>
    [
        0xFF, 0xD8,
        0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00,
        0xFF, 0xDA, 0x00, 0x02, marker, marker,
        0xFF, 0xD9,
    ];

    // ---- Bridge -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Bridge_PublishesTheScreen_AndFollowsTheLatestCatalog()
    {
        _bridge.ApplyCatalog(1, WithScreen);
        Assert.True(_bridge.TryRefreshCatalog());
        Assert.Equal(new ScreenInfo(480, 480), _bridge.Catalog.Hardware[0].Screen);
        Assert.Null(_bridge.Catalog.Hardware[1].Screen);
        Assert.True(_bridge.HasScreens);
        Assert.True(_bridge.TryGetScreen(HardwareId, out var screen));
        Assert.Equal(480, screen.Width);
        Assert.False(_bridge.TryGetScreen("lianli:/LianLi/w1", out _));
        Assert.False(_bridge.TryGetScreen("lhm:/cpu/0", out _));

        // A plugin without the screen (an older build) takes it away again; the hardware itself stays.
        _bridge.ApplyCatalog(2, WithScreen with { Hardware = [new PluginHardware("LianLi/0", "Lian Li GA II LCD", PluginHardwareKind.Cooler)] });
        Assert.True(_bridge.TryRefreshCatalog());
        Assert.Null(_bridge.Catalog.Hardware[0].Screen);
        Assert.False(_bridge.HasScreens);
        Assert.Equal(2, _bridge.Catalog.Hardware.Count);

        // The same catalog again changes nothing.
        _bridge.ApplyCatalog(3, WithScreen with { Hardware = [new PluginHardware("LianLi/0", "Lian Li GA II LCD", PluginHardwareKind.Cooler)] });
        Assert.False(_bridge.TryRefreshCatalog());
    }

    [Theory]
    [InlineData("lianli:/LianLi/0", "LianLi/0")]
    [InlineData("lianli:/", null)]
    [InlineData("lhm:/cpu/0", null)]
    [InlineData(null, null)]
    public void Bridge_PluginIdOf_StripsThePrefix(string? hardwareId, string? expected)
    {
        Assert.Equal(expected, PluginBridgeSource.PluginIdOf(hardwareId));
    }

    // ---- Relay ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Relay_RefusesFramesForUnknownScreensWrongSizesAndBadMarkers()
    {
        _bridge.ApplyCatalog(1, WithScreen);
        _relay.Submit(new ScreenFrameRequest("lianli:/LianLi/w1", Jpeg(480, 480))); // no screen
        _relay.Submit(new ScreenFrameRequest("lhm:/cpu/0", Jpeg(480, 480)));
        _relay.Submit(new ScreenFrameRequest(HardwareId, Jpeg(320, 240)));
        _relay.Submit(new ScreenFrameRequest(HardwareId, [0xFF, 0xD8, 0x00]));
        _relay.Submit(new ScreenFrameRequest(null!, Jpeg(480, 480)));
        _relay.Submit(new ScreenFrameRequest(HardwareId, null!));

        Assert.Equal(6, _relay.Dropped);
        Assert.Null(_relay.Mailbox.Take());
    }

    [Fact]
    public void Relay_KeepsAtMostTwoFramesASecond_AndOnlyTheNewest()
    {
        _bridge.ApplyCatalog(1, WithScreen);
        _relay.Submit(new ScreenFrameRequest(HardwareId, Jpeg(480, 480, 1)));
        _relay.Submit(new ScreenFrameRequest(HardwareId, Jpeg(480, 480, 2))); // replaces the first (dropped)
        _relay.Submit(new ScreenFrameRequest(HardwareId, Jpeg(480, 480, 3))); // a third within a second: refused

        var frame = _relay.Mailbox.Take();
        Assert.NotNull(frame);
        Assert.Equal("LianLi/0", frame.ScreenId);
        Assert.Equal(Jpeg(480, 480, 2), frame.Jpeg);
        Assert.Equal(2, _relay.Dropped);
    }

    [Fact]
    public void Relay_Status_AppearsOnlyOnceAScreenIsKnown()
    {
        Assert.Empty(_relay.StatusEntries());

        _bridge.ApplyCatalog(1, WithScreen);
        var entry = Assert.Single(_relay.StatusEntries());
        Assert.Equal(ScreenPipeProtocol.StatusSource, entry.Source);
        Assert.Equal(SourceStatus.DeviceNotFound, entry.Status);
        Assert.Contains("0 frames sent", entry.Message, StringComparison.Ordinal);
    }

    // ---- Screen pipe ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Plugin_ReadsEachFrameOnce_InTheDocumentedFraming()
    {
        _bridge.ApplyCatalog(1, WithScreen);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(PipeDirection.In, timeout.Token);
        await WaitUntilAsync(() => _relay.IsConnected, timeout.Token);

        var jpeg = Jpeg(480, 480);
        _relay.Submit(new ScreenFrameRequest(HardwareId, jpeg));
        var message = await ReadMessageAsync(pipe, timeout.Token);

        Assert.Equal(ScreenPipeProtocol.Encode("LianLi/0", jpeg), message);
        Assert.Equal("SDSF"u8.ToArray(), message[..4]);
        Assert.Equal(1, _relay.Sent);
        Assert.Equal(Environment.ProcessId, _verifier.LastProcessId);
        Assert.Equal(SourceStatus.Ok, Assert.Single(_relay.StatusEntries()).Status);

        // Nothing more is sent without a new frame (never a repeat).
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        quiet.CancelAfter(TimeSpan.FromMilliseconds(600));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var read = await pipe.ReadAsync(new byte[1], quiet.Token);
            Assert.Fail($"The server sent {read} more byte(s) without a new frame.");
        });
    }

    [Fact]
    public async Task RejectedClient_IsClosedWithoutAByte()
    {
        _verifier.Accept = false;
        _bridge.ApplyCatalog(1, WithScreen);
        _relay.Submit(new ScreenFrameRequest(HardwareId, Jpeg(480, 480)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(PipeDirection.In, timeout.Token);

        Assert.Equal(0, await ReadOrClosedAsync(pipe, new byte[16], timeout.Token));
        Assert.False(_relay.IsConnected);
        Assert.Equal(0, _relay.Sent);
    }

    [Fact]
    public async Task ClientThatWrites_IsDisconnected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(PipeDirection.InOut, timeout.Token);
        await WaitUntilAsync(() => _relay.IsConnected, timeout.Token);

        await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
        Assert.Equal(0, await ReadOrClosedAsync(pipe, new byte[16], timeout.Token));
        await WaitUntilAsync(() => !_relay.IsConnected, timeout.Token);
    }

    [Fact]
    public async Task NewerConnection_ReplacesTheOlder_AndGetsTheFrames()
    {
        _bridge.ApplyCatalog(1, WithScreen);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var older = await ConnectAsync(PipeDirection.In, timeout.Token);
        await WaitUntilAsync(() => _relay.IsConnected, timeout.Token);
        await using var newer = await ConnectAsync(PipeDirection.In, timeout.Token);

        Assert.Equal(0, await ReadOrClosedAsync(older, new byte[16], timeout.Token));
        await WaitUntilAsync(() => _relay.IsConnected, timeout.Token);

        var jpeg = Jpeg(480, 480);
        _relay.Submit(new ScreenFrameRequest(HardwareId, jpeg));
        Assert.Equal(ScreenPipeProtocol.Encode("LianLi/0", jpeg), await ReadMessageAsync(newer, timeout.Token));
    }

    private async Task<NamedPipeClientStream> ConnectAsync(PipeDirection direction, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, direction, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, token);
        return pipe;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(20, token);
        }
    }

    /// <summary>Bytes read, or 0 once the server closed the pipe.</summary>
    private static async Task<int> ReadOrClosedAsync(NamedPipeClientStream pipe, byte[] buffer, CancellationToken token)
    {
        try
        {
            return await pipe.ReadAsync(buffer, token);
        }
        catch (IOException)
        {
            return 0; // broken pipe
        }
    }

    /// <summary>One SDSF message, read the way the plugin reads it: the fixed header, the id, the length, the payload.</summary>
    private static async Task<byte[]> ReadMessageAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        var header = await ReadExactlyAsync(pipe, ScreenPipeProtocol.HeaderBytes, token);
        var idLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        var id = await ReadExactlyAsync(pipe, idLength, token);
        var length = await ReadExactlyAsync(pipe, 4, token);
        var payload = await ReadExactlyAsync(pipe, (int)BinaryPrimitives.ReadUInt32LittleEndian(length), token);
        return [.. header, .. id, .. length, .. payload];
    }

    private static async Task<byte[]> ReadExactlyAsync(NamedPipeClientStream pipe, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        await pipe.ReadExactlyAsync(buffer, token);
        return buffer;
    }

    private sealed class TestVerifier : IPluginClientVerifier
    {
        private int _lastProcessId;

        public bool Accept { get; set; } = true;

        public int LastProcessId => Volatile.Read(ref _lastProcessId);

        public string? Check(int processId, string? imagePath)
        {
            Volatile.Write(ref _lastProcessId, processId);
            return Accept && processId == Environment.ProcessId ? null : "test verifier rejects this client";
        }
    }
}
