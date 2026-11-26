using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SpaceDeck.Collector.Plugins;
using SpaceDeck.Core.Model;

namespace SpaceDeck.Collector.Tests;

/// <summary>Runs the real plugin pipe server on a unique pipe and talks to it like the FanControl plugin will.</summary>
public sealed class PluginPipeEndToEndTests : IAsyncLifetime
{
    private const string Hello = """{"t":"hello","v":1,"plugin":"Lian Li Uni (Lighting)","version":"2.0.7","instance":"test"}""";

    private const string Catalog =
        """{"t":"catalog","hardware":[{"id":"LianLi/0","name":"Lian Li Galahad II LCD","kind":"cooler"}],"sensors":[{"id":"LianLi/0/coolant/temp","hardware":"LianLi/0","name":"Coolant","kind":"temperature"},{"id":"LianLi/0/ch1/fan","hardware":"LianLi/0","name":"Pump","kind":"fan"}]}""";

    private readonly string _pipeName = $"SpaceDeckPluginTest-{Guid.NewGuid():N}";
    private readonly PluginBridgeSource _bridge = new();
    private readonly TestVerifier _verifier = new();
    private PluginPipeServer _server = null!;

    public async Task InitializeAsync()
    {
        _bridge.Open();
        _server = new PluginPipeServer(_bridge, _verifier, NullLogger<PluginPipeServer>.Instance, _pipeName);
        await _server.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
    }

    [Fact]
    public async Task Plugin_FeedsCatalogAndValues()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(timeout.Token);

        await SendAsync(pipe, Hello, timeout.Token);
        await SendAsync(pipe, Catalog, timeout.Token);
        await SendAsync(pipe, """{"t":"values","values":[31.5,2900]}""", timeout.Token);

        // Poll like the collector's poll thread until the readings arrive.
        SensorReading[] readings = [];
        while (readings.Length == 0 || readings[1].Value is null)
        {
            await Task.Delay(50, timeout.Token);
            _bridge.TryRefreshCatalog();
            readings = new SensorReading[_bridge.Catalog.Sensors.Count];
            _bridge.Read(readings);
        }

        Assert.Equal(Environment.ProcessId, _verifier.LastProcessId);
        Assert.Equal(SourceStatus.Ok, _bridge.Status);
        Assert.Equal(new[] { "lianli:/LianLi/0/coolant/temp", "lianli:/LianLi/0/ch1/fan" }, _bridge.Catalog.Sensors.Select(s => s.Id).ToArray());
        Assert.Equal(HardwareKind.Cooler, Assert.Single(_bridge.Catalog.Hardware).Kind);
        Assert.Equal(31.5f, readings[0].Value);
        Assert.Equal(2900f, readings[1].Value);

        // Closing the pipe blanks the values and the status says so; the catalog stays.
        await pipe.DisposeAsync();
        while (_bridge.Status == SourceStatus.Ok)
        {
            await Task.Delay(50, timeout.Token);
        }

        Assert.Equal(SourceStatus.DeviceNotFound, _bridge.Status);
        _bridge.Read(readings);
        Assert.All(readings, r => Assert.Null(r.Value));
        Assert.Equal(2, _bridge.Catalog.Sensors.Count);
    }

    [Fact]
    public async Task RejectedClient_IsDisconnected()
    {
        _verifier.Accept = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(timeout.Token);

        Assert.True(await IsClosedByServerAsync(pipe, timeout.Token));
        Assert.Equal(Environment.ProcessId, _verifier.LastProcessId);
        Assert.Equal(SourceStatus.DeviceNotFound, _bridge.Status);
    }

    [Fact]
    public async Task ProtocolViolation_Disconnects()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(timeout.Token);

        await SendAsync(pipe, Hello, timeout.Token);
        await SendAsync(pipe, """{"t":"values","values":[1,2]}""", timeout.Token); // values before a catalog

        Assert.True(await IsClosedByServerAsync(pipe, timeout.Token));
        Assert.Equal(SourceStatus.DeviceNotFound, _bridge.Status);
    }

    [Fact]
    public async Task LineOver64KiB_Disconnects()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = await ConnectAsync(timeout.Token);

        await SendAsync(pipe, Hello, timeout.Token);
        try
        {
            // No newline: the server must give up at 64 KiB rather than keep buffering.
            await pipe.WriteAsync(new byte[200 * 1024], timeout.Token);
        }
        catch (IOException)
        {
            // The server closed the pipe while the write was still going: also a disconnect.
        }

        Assert.True(await IsClosedByServerAsync(pipe, timeout.Token));
    }

    [Fact]
    public async Task NewerConnection_ReplacesTheOlder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var older = await ConnectAsync(timeout.Token);
        await SendAsync(older, Hello, timeout.Token);
        await SendAsync(older, Catalog, timeout.Token);
        while (!_bridge.IsConnected)
        {
            await Task.Delay(50, timeout.Token);
        }

        await using var newer = await ConnectAsync(timeout.Token);
        await SendAsync(newer, Hello, timeout.Token);
        await SendAsync(newer, Catalog, timeout.Token);

        Assert.True(await IsClosedByServerAsync(older, timeout.Token));
        Assert.True(_bridge.IsConnected);

        await SendAsync(newer, """{"t":"values","values":[28,2500]}""", timeout.Token);
        var readings = new SensorReading[2];
        while (readings[0].Value is null)
        {
            await Task.Delay(50, timeout.Token);
            _bridge.TryRefreshCatalog();
            _bridge.Read(readings);
        }

        Assert.Equal(28f, readings[0].Value);
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, token);
        return pipe;
    }

    private static async Task SendAsync(NamedPipeClientStream pipe, string line, CancellationToken token)
    {
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), token);
        await pipe.FlushAsync(token);
    }

    /// <summary>True once a read reports the server end closed (the server never writes on this pipe).</summary>
    private static async Task<bool> IsClosedByServerAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        var buffer = new byte[16];
        try
        {
            return await pipe.ReadAsync(buffer, token) == 0;
        }
        catch (IOException)
        {
            return true; // broken pipe
        }
    }

    private sealed class TestVerifier : IPluginClientVerifier
    {
        private int _lastProcessId;

        /// <summary>Set before connecting; read on the server's connection task.</summary>
        public bool Accept { get; set; } = true;

        public int LastProcessId => Volatile.Read(ref _lastProcessId);

        public string? Check(int processId, string? imagePath)
        {
            Volatile.Write(ref _lastProcessId, processId);
            return Accept && processId == Environment.ProcessId ? null : "test verifier rejects this client";
        }
    }
}
