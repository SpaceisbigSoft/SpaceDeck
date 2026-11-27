using System.Diagnostics.CodeAnalysis;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.PluginPipe;

namespace SpaceDeck.Collector.Plugins;

/// <summary>
/// The sensors SpaceDeck's FanControl plugin (Lian Li) sends over the plugin pipe (<c>docs/PLUGIN-PIPE.md</c>), as a
/// source the poll loop reads like any other.
/// </summary>
/// <remarks>
/// <para>Two threads: <see cref="PluginPipeServer"/> feeds catalogs and values from its connection tasks; the poll thread
/// calls the <see cref="ISensorSource"/> members. Feeding builds a new immutable <see cref="Snapshot"/> under a short lock
/// and publishes it with one volatile write; the poll thread only reads the current snapshot, so it never waits on a
/// pipe and never sees half an update.</para>
/// <para>One instance lives as long as the collector (a DI singleton shared by the pipe server and the source factory):
/// the poll loop disposes and recreates its sources when sensor groups change, and this source must keep its catalog
/// (dashboard ids) and stay the one the pipe server feeds. <see cref="Dispose"/> therefore releases nothing and the
/// instance can be opened again; it holds no unmanaged resources.</para>
/// <para>The catalog only grows: sensors a newer catalog drops stay listed (and read blank), so dashboards keep their
/// layout across plugin restarts. The first definition of an id is kept, except a hardware entry's screen
/// (<c>docs/SCREEN-PIPE.md</c>), which follows the feeding connection's latest catalog.</para>
/// </remarks>
public sealed class PluginBridgeSource : ISensorSource
{
    public const string SourceName = "lianli";

    public const string NotConnectedMessage = "FanControl's Lian Li plugin isn't connected";

    /// <summary>Values go blank this long after the last <c>values</c> line (the plugin sends one a second).</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Lifetime caps: each catalog is limited by the protocol, but the kept catalog collects every id ever sent, so a
    /// plugin that keeps changing ids can't grow the collector's catalog (and every client's) without bound.
    /// </summary>
    internal const int MaxHardware = 4 * PluginPipeProtocol.MaxHardware;

    /// <inheritdoc cref="MaxHardware"/>
    internal const int MaxSensors = 4 * PluginPipeProtocol.MaxSensors;

    private const string IdPrefix = SourceName + ":/";

    private readonly TimeProvider _time;

    // Feeding side, guarded by _gate.
    private readonly Lock _gate = new();
    private readonly List<HardwareInfo> _hardware = [];
    private readonly Dictionary<string, int> _hardwareIndex = new(StringComparer.Ordinal);
    private readonly List<SensorInfo> _sensors = [];
    private readonly Dictionary<string, int> _sensorIndex = new(StringComparer.Ordinal);

    /// <summary>For each sensor of the feeding connection's catalog, its index in <see cref="_sensors"/>, or -1 if over the cap.</summary>
    private int[] _map = [];

    /// <summary>The connection whose catalog and values are shown; 0 = none.</summary>
    private long _feeder;

    private volatile Snapshot _snapshot = new(SensorCatalog.Empty, Connected: false, Values: [], ValuesTimestamp: 0);
    private volatile bool _opened;

    // Poll-thread side.
    private SensorCatalog _published = SensorCatalog.Empty;
    private float?[] _min = [];
    private float?[] _max = [];

    public PluginBridgeSource(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>What the poll thread sees. Values are in kept-catalog order; a shorter array means "the rest are blank".</summary>
    private sealed record Snapshot(SensorCatalog Catalog, bool Connected, float?[] Values, long ValuesTimestamp);

    public string Name => SourceName;

    public SourceStatus Status => !_opened ? SourceStatus.NotOpened
        : _snapshot.Connected ? SourceStatus.Ok
        : SourceStatus.DeviceNotFound;

    public string? StatusMessage => _opened && !_snapshot.Connected ? NotConnectedMessage : null;

    /// <summary>The catalog the poll loop works with; changes only in <see cref="Open"/> and <see cref="TryRefreshCatalog"/>.</summary>
    public SensorCatalog Catalog => _published;

    /// <summary>True while a plugin connection has sent a valid catalog and is still connected.</summary>
    public bool IsConnected => _snapshot.Connected;

    /// <summary>True when some kept hardware has a screen (any thread).</summary>
    public bool HasScreens => _snapshot.Catalog.Hardware.Any(h => h.Screen is not null);

    /// <summary>
    /// The screen of the kept hardware <paramref name="hardwareId"/> (a published id, <c>lianli:/LianLi/0</c>), if it has a
    /// valid one. Any thread.
    /// </summary>
    public bool TryGetScreen(string? hardwareId, [NotNullWhen(true)] out ScreenInfo? screen)
    {
        foreach (var hardware in _snapshot.Catalog.Hardware)
        {
            if (string.Equals(hardware.Id, hardwareId, StringComparison.Ordinal) && hardware.Screen is { IsValid: true } found)
            {
                screen = found;
                return true;
            }
        }

        screen = null;
        return false;
    }

    /// <summary>The plugin's own id (<c>LianLi/0</c>) for a published id (<c>lianli:/LianLi/0</c>); null for another source's.</summary>
    public static string? PluginIdOf(string? hardwareId) =>
        hardwareId is not null && hardwareId.StartsWith(IdPrefix, StringComparison.Ordinal) && hardwareId.Length > IdPrefix.Length
            ? hardwareId[IdPrefix.Length..]
            : null;

    public void Open()
    {
        Publish(_snapshot.Catalog);
        _opened = true;
    }

    public void Update()
    {
        // Nothing to poll: readings arrive from the pipe.
    }

    /// <summary>True when a plugin catalog added sensors or hardware since the last call (the kept catalog never shrinks).</summary>
    public bool TryRefreshCatalog()
    {
        var latest = _snapshot.Catalog;
        if (latest.Version == _published.Version)
        {
            return false;
        }

        Publish(latest);
        return true;
    }

    public void Read(Span<SensorReading> into)
    {
        if (into.Length != _published.Sensors.Count)
        {
            throw new ArgumentException($"Expected a span of {_published.Sensors.Count} readings, got {into.Length}.", nameof(into));
        }

        var snapshot = _snapshot;
        var fresh = snapshot.Connected && snapshot.Values.Length > 0 && _time.GetElapsedTime(snapshot.ValuesTimestamp) <= StaleAfter;
        for (var i = 0; i < into.Length; i++)
        {
            float? value = fresh && i < snapshot.Values.Length ? snapshot.Values[i] : null;
            if (value is { } v)
            {
                _min[i] = _min[i] is { } min ? Math.Min(min, v) : v;
                _max[i] = _max[i] is { } max ? Math.Max(max, v) : v;
            }

            into[i] = new SensorReading(value, _min[i], _max[i]);
        }
    }

    /// <summary>
    /// Shows <paramref name="catalog"/> from connection <paramref name="connectionId"/>, which becomes the feeding connection
    /// (the newest plugin wins). New ids are added to the kept catalog; values blank until that connection's next
    /// <c>values</c> line, since earlier values followed another sensor order.
    /// </summary>
    public void ApplyCatalog(long connectionId, PluginCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (_gate)
        {
            var grew = false;
            foreach (var hardware in catalog.Hardware)
            {
                var id = IdPrefix + hardware.Id;
                if (_hardwareIndex.TryGetValue(id, out var existing))
                {
                    // A plugin update can add (or drop) a screen without a collector restart.
                    if (_hardware[existing].Screen != hardware.Screen)
                    {
                        _hardware[existing] = _hardware[existing] with { Screen = hardware.Screen };
                        grew = true;
                    }
                }
                else if (_hardwareIndex.Count < MaxHardware)
                {
                    _hardwareIndex[id] = _hardware.Count;
                    _hardware.Add(new HardwareInfo(id, hardware.Name, MapKind(hardware.Kind), null, SourceName, hardware.Screen));
                    grew = true;
                }
            }

            var map = new int[catalog.Sensors.Count];
            for (var i = 0; i < map.Length; i++)
            {
                var sensor = catalog.Sensors[i];
                var id = IdPrefix + sensor.Id;
                if (_sensorIndex.TryGetValue(id, out var index))
                {
                    map[i] = index;
                    continue;
                }

                var hardwareId = IdPrefix + sensor.HardwareId;
                if (_sensors.Count >= MaxSensors || !_hardwareIndex.ContainsKey(hardwareId))
                {
                    map[i] = -1; // over a cap: not shown
                    continue;
                }

                var kind = MapKind(sensor.Kind);
                map[i] = _sensors.Count;
                _sensorIndex[id] = _sensors.Count;
                _sensors.Add(new SensorInfo(id, hardwareId, sensor.Name, kind, Units.For(kind), SourceName, null));
                grew = true;
            }

            _map = map;
            _feeder = connectionId;
            var current = _snapshot;
            var kept = grew ? new SensorCatalog(current.Catalog.Version + 1, [.. _hardware], [.. _sensors]) : current.Catalog;
            _snapshot = new Snapshot(kept, Connected: true, Values: [], ValuesTimestamp: 0);
        }
    }

    /// <summary>Stores a <c>values</c> line (already checked against that connection's catalog). Ignored unless it is the feeding connection.</summary>
    public void ApplyValues(long connectionId, IReadOnlyList<double?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_gate)
        {
            if (connectionId != _feeder || values.Count != _map.Length)
            {
                return;
            }

            var current = _snapshot;
            var into = new float?[current.Catalog.Sensors.Count];
            for (var i = 0; i < _map.Length; i++)
            {
                if (_map[i] >= 0 && values[i] is { } value)
                {
                    into[_map[i]] = (float)value;
                }
            }

            _snapshot = current with { Values = into, ValuesTimestamp = _time.GetTimestamp() };
        }
    }

    /// <summary>Connection <paramref name="connectionId"/> ended: if it was feeding, values go blank and the status says so.</summary>
    public void Disconnect(long connectionId)
    {
        lock (_gate)
        {
            if (connectionId != _feeder)
            {
                return;
            }

            _feeder = 0;
            _map = [];
            _snapshot = _snapshot with { Connected = false, Values = [], ValuesTimestamp = 0 };
        }
    }

    /// <summary>
    /// Releases nothing: the instance outlives the poll loop's source re-opens (see remarks), and the DI container
    /// disposes it at shutdown, when there is nothing left to release either.
    /// </summary>
    public void Dispose()
    {
    }

    private void Publish(SensorCatalog catalog)
    {
        _published = catalog;
        if (_min.Length != catalog.Sensors.Count)
        {
            // The kept catalog only appends, so existing min/max stay with their sensors.
            Array.Resize(ref _min, catalog.Sensors.Count);
            Array.Resize(ref _max, catalog.Sensors.Count);
        }
    }

    internal static HardwareKind MapKind(PluginHardwareKind kind) => kind == PluginHardwareKind.Cooler ? HardwareKind.Cooler : HardwareKind.Other;

    internal static SensorKind MapKind(PluginSensorKind kind) => kind switch
    {
        PluginSensorKind.Temperature => SensorKind.Temperature,
        PluginSensorKind.Fan => SensorKind.Fan,
        PluginSensorKind.Control => SensorKind.Control,
        _ => SensorKind.Other,
    };
}
