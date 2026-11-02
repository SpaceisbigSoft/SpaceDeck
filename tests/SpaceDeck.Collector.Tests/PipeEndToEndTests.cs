using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpaceDeck.Collector.Pipe;
using SpaceDeck.Collector.Polling;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Protocol;

namespace SpaceDeck.Collector.Tests;

/// <summary>Runs the real poll loop and pipe server against a fake source and talks to it like the UI will.</summary>
public sealed class PipeEndToEndTests : IAsyncLifetime
{
    private readonly string _pipeName = $"SpaceDeckTest-{Guid.NewGuid():N}";
    private SensorHub _hub = null!;
    private PollingService _polling = null!;
    private PipeServer _server = null!;

    public async Task InitializeAsync()
    {
        var options = Options.Create(new CollectorOptions { PollIntervalMs = 250, HistorySeconds = 60, PipeName = _pipeName });
        _hub = new SensorHub(options.Value.HistoryCapacity(), options.Value.PollIntervalMs);
        _polling = new PollingService(_hub, new FakeFactory(), options, NullLogger<PollingService>.Instance,
            Path.Combine(Path.GetTempPath(), $"spacedeck-e2e-{Guid.NewGuid():N}.json")); // never touch the real collector.json
        _server = new PipeServer(_hub, options, NullLogger<PipeServer>.Instance);
        await _polling.StartAsync(CancellationToken.None);
        await _server.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        await _polling.StopAsync(CancellationToken.None);
        _server.Dispose();
        _polling.Dispose();
    }

    [Fact]
    public async Task Client_ReceivesHelloStatusCatalogAndValues_ThenHistory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(timeout.Token);

        var hello = Assert.IsType<HelloMessage>(await FrameCodec.ReadAsync(pipe, timeout.Token));
        Assert.Equal(ProtocolInfo.Version, hello.ProtocolVersion);

        SensorCatalog? catalog = null;
        ValuesMessage? values = null;
        SourceStatusMessage? status = null;
        while (values is null)
        {
            switch (await FrameCodec.ReadAsync(pipe, timeout.Token))
            {
                case CatalogMessage c:
                    catalog = c.Catalog;
                    break;
                case SourceStatusMessage s:
                    status = s;
                    break;
                case ValuesMessage v:
                    // Values must never arrive before the catalog they belong to.
                    Assert.NotNull(catalog);
                    Assert.Equal(catalog.Version, v.CatalogVersion);
                    values = v;
                    break;
                case null:
                    Assert.Fail("Server closed the pipe.");
                    break;
            }
        }

        Assert.NotNull(status);
        Assert.Equal("fake", Assert.Single(status.Sources).Source);
        Assert.Equal(2, catalog!.Sensors.Count);
        Assert.Equal(2, values.Values.Length);
        Assert.Equal((float?)12.0f, values.Values[0]);

        // Let a few samples accumulate, then ask for history.
        await Task.Delay(800, timeout.Token);
        await FrameCodec.WriteAsync(pipe, new HistoryRequest(42, ["fake:/board/voltage/0", "fake:/board/fan/0"], 60), timeout.Token);

        HistoryResponse? history = null;
        while (history is null)
        {
            if (await FrameCodec.ReadAsync(pipe, timeout.Token) is HistoryResponse h)
            {
                history = h;
            }
        }

        Assert.Equal(42, history.RequestId);
        Assert.True(history.TimestampsUnixMs.Length >= 2, $"expected several samples, got {history.TimestampsUnixMs.Length}");
        Assert.All(history.Values[0], v => Assert.Equal((float?)12.0f, v));
        Assert.All(history.Values[1], v => Assert.NotNull(v));
    }

    [Fact]
    public async Task SetConfig_ChangesPollInterval()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(timeout.Token);

        await FrameCodec.WriteAsync(pipe, new SetConfigRequest(750, null, null), timeout.Token);

        while (_hub.PollIntervalMs != 750)
        {
            await Task.Delay(50, timeout.Token);
        }

        Assert.Equal(750, _hub.PollIntervalMs);
    }

    [Fact]
    public async Task SetConfig_BroadcastsCollectorStatus()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(timeout.Token);

        await FrameCodec.WriteAsync(pipe, new SetConfigRequest(600, null, null, ControllersEnabled: false), timeout.Token);

        CollectorStatusMessage? status = null;
        while (status is not { PollIntervalMs: 600 })
        {
            var message = await FrameCodec.ReadAsync(pipe, timeout.Token);
            Assert.NotNull(message);
            status = message as CollectorStatusMessage ?? status;
        }

        Assert.False(status.ControllersEnabled);
        Assert.Null(status.ConfigError);
    }

    [Fact]
    public async Task ResetMinMax_RestartsTrackingFromCurrentValue()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(timeout.Token);

        // Let the fake fan climb past its source minimum (1000 RPM), then reset everything.
        ValuesMessage? values = null;
        while (values is not { Sequence: > 3 })
        {
            values = await FrameCodec.ReadAsync(pipe, timeout.Token) as ValuesMessage ?? values;
        }

        await FrameCodec.WriteAsync(pipe, new ResetMinMaxRequest(null), timeout.Token);

        CollectorStatusMessage? status = null;
        while (status?.MinMaxResetUnixMs is null)
        {
            status = await FrameCodec.ReadAsync(pipe, timeout.Token) as CollectorStatusMessage ?? status;
        }

        ValuesMessage? after = null;
        while (after is null)
        {
            after = await FrameCodec.ReadAsync(pipe, timeout.Token) as ValuesMessage;
        }

        Assert.True(after.Min[1] > 1000, $"min {after.Min[1]} should restart above the source's 1000 RPM");
        Assert.Equal(12f, after.Min[0]);
    }

    [Fact]
    public async Task UnknownMessageType_IsSkipped()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(timeout.Token);

        // A frame from a "newer" client: union key 200, which this version doesn't know.
        byte[] payload = [0x92, 0xCC, 200, 0x90];
        var frame = new byte[4 + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await pipe.WriteAsync(frame, timeout.Token);

        // The session stays up and still answers.
        await FrameCodec.WriteAsync(pipe, new HistoryRequest(5, ["fake:/board/fan/0"], 60), timeout.Token);
        HistoryResponse? response = null;
        while (response is null)
        {
            var message = await FrameCodec.ReadAsync(pipe, timeout.Token);
            Assert.NotNull(message);
            response = message as HistoryResponse;
        }

        Assert.Equal(5, response.RequestId);
    }

    [Fact]
    public async Task SeveralClients_CanConnectAtOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var a = await ConnectAsync(timeout.Token);
        await using var b = await ConnectAsync(timeout.Token);

        Assert.IsType<HelloMessage>(await FrameCodec.ReadAsync(a, timeout.Token));
        Assert.IsType<HelloMessage>(await FrameCodec.ReadAsync(b, timeout.Token));
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, token);
        return pipe;
    }

    private sealed class FakeFactory : ISensorSourceFactory
    {
        public IReadOnlyList<ISensorSource> Create(CollectorOptions options) => [new FakeSource()];
    }

    /// <summary>One board with a steady +12V rail and a fan whose speed climbs each poll.</summary>
    private sealed class FakeSource : ISensorSource
    {
        private float _rpm = 1000;

        public string Name => "fake";

        public SourceStatus Status { get; private set; } = SourceStatus.NotOpened;

        public string? StatusMessage => null;

        public SensorCatalog Catalog { get; private set; } = SensorCatalog.Empty;

        public void Open()
        {
            Catalog = new SensorCatalog(
                1,
                [new HardwareInfo("fake:/board", "Fake Board", HardwareKind.Motherboard, null, "fake")],
                [
                    new SensorInfo("fake:/board/voltage/0", "fake:/board", "+12V", SensorKind.Voltage, "V", "fake", 12.0),
                    new SensorInfo("fake:/board/fan/0", "fake:/board", "CPU Fan", SensorKind.Fan, "RPM", "fake", null),
                ]);
            Status = SourceStatus.Ok;
        }

        public void Update() => _rpm += 10;

        public bool TryRefreshCatalog() => false;

        public void Read(Span<SensorReading> into)
        {
            into[0] = new SensorReading(12.0f, 12.0f, 12.0f);
            into[1] = new SensorReading(_rpm, 1000, _rpm);
        }

        public void Dispose()
        {
        }
    }
}
