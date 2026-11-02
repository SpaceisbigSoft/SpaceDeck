using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.Core.Calculated;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Protocol;

namespace SpaceDeck.App.Services;

/// <summary>
/// The UI-thread view of the collector: connection state, source health and every sensor as a bindable
/// <see cref="SensorItem"/>. Value frames are coalesced so the UI updates at most once per dispatcher pass.
/// </summary>
public sealed partial class SensorStore : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly CollectorClient _client;
    private readonly SensorCustomizationService _customizations;
    private SensorItem[] _ordered = [];
    private SensorItem[] _calcItems = [];
    private CalculatedSensor[] _calcDefs = [];
    private (float Min, float Max)[] _calcRange = [];
    private readonly List<double?> _calcInputs = [];
    private Dictionary<string, SensorItem> _byId = new(StringComparer.Ordinal);
    private int _catalogVersion = -1;
    private ValuesMessage? _pendingValues;
    private int _valuesScheduled;

    [ObservableProperty]
    private ConnectionState _connectionState = ConnectionState.Connecting;

    [ObservableProperty]
    private string? _serverVersion;

    /// <summary>Why the last connection was refused (another program answered on the pipe); null otherwise.</summary>
    [ObservableProperty]
    private string? _connectionProblem;

    [ObservableProperty]
    private int _pollIntervalMs = 1000;

    [ObservableProperty]
    private IReadOnlyList<SourceStatusEntry> _sourceStatuses = [];

    [ObservableProperty]
    private DateTime? _lastUpdate;

    [ObservableProperty]
    private int _hardwareCount;

    /// <summary>The collector's settings and run mode (null until a v2+ collector reports it).</summary>
    [ObservableProperty]
    private CollectorStatusMessage? _collectorStatus;

    /// <summary>Protocol version the collector reported in Hello (0 before connecting).</summary>
    [ObservableProperty]
    private int _collectorProtocolVersion;

    public SensorStore(CollectorClient client, AppSettings settings, SensorCustomizationService customizations)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(customizations);
        _customizations = customizations;
        customizations.Changed += OnCustomizationChanged;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settings = settings;
        _client = client;
        Display = new DisplayOptions { TemperatureUnit = settings.TemperatureUnit };

        client.StateChanged += state => Post(() =>
        {
            ConnectionState = state;
            if (state != ConnectionState.Connected)
            {
                CollectorStatus = null; // stale once disconnected; a fresh one follows the next Hello
            }
        });
        client.ConnectionProblemChanged += problem => Post(() => ConnectionProblem = problem);
        client.CollectorStatusReceived += status => Post(() =>
        {
            CollectorStatus = status;
            PollIntervalMs = status.PollIntervalMs;
            // Raise even when equal to the last one, so the Settings switches resync after a rejected change.
            OnPropertyChanged(nameof(CollectorStatus));
            NotifyCollectorFeatures();
        });
        client.HelloReceived += hello => Post(() =>
        {
            CollectorProtocolVersion = hello.ProtocolVersion;
            CollectorStatus = null; // a newer one follows (or never, from an older collector)
            ServerVersion = hello.ServerVersion;
            PollIntervalMs = hello.PollIntervalMs;
        });
        client.StatusReceived += statuses => Post(() => SourceStatuses = statuses);
        client.CatalogReceived += catalog => Post(() => ApplyCatalog(catalog));
        client.ValuesReceived += OnValues;
        Themes.SkinManager.SkinChanged += () =>
        {
            foreach (var item in _ordered.Concat(_calcItems))
            {
                item.RefreshDisplay();
            }

            OnPropertyChanged(nameof(ConnectionState)); // re-color the connection dot
        };
    }

    /// <summary>The connection to the collector (history queries, exports).</summary>
    public CollectorClient Client => _client;

    /// <summary>The collector keeps history on disk (6 h – 7 d ranges, exports beyond the last hour).</summary>
    public bool SupportsLongHistory => CollectorProtocolVersion >= ProtocolInfo.LongHistoryVersion && CollectorStatus?.DiskHistory == true;

    /// <summary>The collector can restart min/max (protocol 3).</summary>
    public bool SupportsMinMaxReset => CollectorProtocolVersion >= ProtocolInfo.LongHistoryVersion && ConnectionState == ConnectionState.Connected;

    /// <summary>"Min/max since 10:42 PM (reset)" / "… since the collector started".</summary>
    public string MinMaxSinceText => CollectorStatus switch
    {
        { MinMaxResetUnixMs: { } reset } => $"Min/max since {Local(reset):g} (reset)",
        { StartedUnixMs: > 0 } s => $"Min/max since the collector started, {Local(s.StartedUnixMs):g}",
        _ => "",
    };

    /// <summary>The user's calculated sensors (definitions).</summary>
    public IReadOnlyList<CalculatedSensor> CalculatedSensors => _settings.CalculatedSensors;

    /// <summary>Adds or replaces a calculated sensor and shows it everywhere.</summary>
    public void SaveCalculated(CalculatedSensor sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        var index = _settings.CalculatedSensors.FindIndex(c => c.Id == sensor.Id);
        if (index >= 0)
        {
            _settings.CalculatedSensors[index] = sensor;
        }
        else
        {
            _settings.CalculatedSensors.Add(sensor);
        }

        _settings.Save();
        RebuildCalculated();
    }

    public void DeleteCalculated(string id)
    {
        if (_settings.CalculatedSensors.RemoveAll(c => c.Id == id) > 0)
        {
            _settings.Save();
            RebuildCalculated();
        }
    }

    /// <summary>Re-creates the calculated sensors and tells pages to re-resolve their sensors.</summary>
    private void RebuildCalculated()
    {
        BuildCalculated();
        CatalogApplied?.Invoke();
    }

    /// <summary>Adds a sensor item per calculated sensor (group "Calculated") after the collector's sensors.</summary>
    private void BuildCalculated()
    {
        // Unchanged definitions keep their item (history, min/max); edited or new ones start fresh.
        var previous = new Dictionary<string, (CalculatedSensor Def, SensorItem Item, (float, float) Range)>(StringComparer.Ordinal);
        for (var i = 0; i < _calcItems.Length; i++)
        {
            previous[_calcItems[i].Id] = (_calcDefs[i], _calcItems[i], _calcRange[i]);
            Sensors.Remove(_calcItems[i]);
            _byId.Remove(_calcItems[i].Id);
        }

        _calcDefs = [.. _settings.CalculatedSensors];
        var items = new SensorItem[_calcDefs.Length];
        var ranges = new (float, float)[_calcDefs.Length];
        for (var i = 0; i < _calcDefs.Length; i++)
        {
            var def = _calcDefs[i];
            if (previous.TryGetValue(def.SensorId, out var old) && Same(old.Def, def))
            {
                items[i] = old.Item;
                ranges[i] = old.Range;
            }
            else
            {
                items[i] = new SensorItem(new SensorInfo(def.SensorId, "calc", def.Name, def.Kind, def.Unit, "calc", null), "Calculated", HardwareKind.Other, Display);
                ranges[i] = (float.NaN, float.NaN);
            }
        }

        _calcItems = items;
        _calcRange = ranges;
        foreach (var item in _calcItems)
        {
            item.ApplyOverride(_customizations.Get(item.Id));
            _byId[item.Id] = item;
            Sensors.Add(item);
        }
    }

    private static bool Same(CalculatedSensor a, CalculatedSensor b) =>
        a.Name == b.Name && a.Operation == b.Operation && a.Factor.Equals(b.Factor) && a.Offset.Equals(b.Offset)
        && a.Kind == b.Kind && a.Unit == b.Unit && a.Inputs.SequenceEqual(b.Inputs, StringComparer.Ordinal);

    /// <summary>Restarts min/max of calculated sensors (they're tracked in the app, not the collector).</summary>
    public void ResetCalculatedMinMax(IReadOnlyList<string>? sensorIds)
    {
        for (var i = 0; i < _calcItems.Length; i++)
        {
            if (sensorIds is null || sensorIds.Contains(_calcItems[i].Id))
            {
                _calcRange[i] = (float.NaN, float.NaN);
            }
        }
    }

    /// <summary>Computes every calculated sensor from the readings just applied (min/max tracked here).</summary>
    private void UpdateCalculated(long timestampUnixMs)
    {
        for (var i = 0; i < _calcItems.Length; i++)
        {
            var def = _calcDefs[i];

            // One reused buffer: this runs for every calculated sensor on every frame.
            _calcInputs.Clear();
            foreach (var id in def.Inputs)
            {
                _calcInputs.Add(_byId.TryGetValue(id, out var s) ? s.Value : null);
            }

            float? value = def.Evaluate(_calcInputs) is { } v && double.IsFinite(v) ? (float)v : null;
            var (min, max) = _calcRange[i];
            if (value is { } f)
            {
                min = float.IsNaN(min) ? f : Math.Min(min, f);
                max = float.IsNaN(max) ? f : Math.Max(max, f);
                _calcRange[i] = (min, max);
            }

            _calcItems[i].Update(value, float.IsNaN(min) ? null : min, float.IsNaN(max) ? null : max, timestampUnixMs);
        }
    }

    /// <summary>Restarts min/max for these sensors (null = all). False when the collector can't be reached.</summary>
    public async Task<bool> ResetMinMaxAsync(IReadOnlyList<string>? sensorIds)
    {
        ResetCalculatedMinMax(sensorIds);

        try
        {
            await _client.ResetMinMaxAsync(sensorIds);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    partial void OnCollectorStatusChanged(CollectorStatusMessage? value) => NotifyCollectorFeatures();

    partial void OnCollectorProtocolVersionChanged(int value) => NotifyCollectorFeatures();

    partial void OnConnectionStateChanged(ConnectionState value) => OnPropertyChanged(nameof(SupportsMinMaxReset));

    private void NotifyCollectorFeatures()
    {
        OnPropertyChanged(nameof(SupportsLongHistory));
        OnPropertyChanged(nameof(SupportsMinMaxReset));
        OnPropertyChanged(nameof(MinMaxSinceText));
    }

    private static DateTime Local(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;

    /// <summary>All sensors in catalog order. Rebuilt when the collector sends a new catalog.</summary>
    public ObservableCollection<SensorItem> Sensors { get; } = [];

    public DisplayOptions Display { get; }

    /// <summary>The motherboard's name from the catalog (board profiles are keyed by it), or null.</summary>
    public string? MotherboardName { get; private set; }

    public SensorCustomizationService Customizations => _customizations;

    /// <summary>Sensors by their stable collector id (dashboards reference sensors this way).</summary>
    public IReadOnlyDictionary<string, SensorItem> SensorsById => _byId;

    public TemperatureUnit TemperatureUnit
    {
        get => Display.TemperatureUnit;
        set
        {
            if (Display.TemperatureUnit == value)
            {
                return;
            }

            Display.TemperatureUnit = value;
            _settings.TemperatureUnit = value;
            _settings.Save();
            foreach (var item in _ordered.Concat(_calcItems))
            {
                item.RefreshDisplay();
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Raised on the UI thread after <see cref="Sensors"/> has been rebuilt.</summary>
    public event Action? CatalogApplied;

    /// <summary>Raised on the UI thread after a set of readings was applied, with its timestamp (Unix ms).</summary>
    public event Action<long>? ValuesApplied;

    /// <summary>Raised on the UI thread after a sensor's rename/hidden/correction changed (null id = several).</summary>
    public event Action<string?>? CustomizationApplied;

    private void OnCustomizationChanged(string? sensorId)
    {
        if (sensorId is null)
        {
            foreach (var item in _ordered.Concat(_calcItems))
            {
                item.ApplyOverride(_customizations.Get(item.Id));
            }
        }
        else if (_byId.TryGetValue(sensorId, out var item))
        {
            item.ApplyOverride(_customizations.Get(sensorId));
        }

        CustomizationApplied?.Invoke(sensorId);
    }

    private void Post(Action action) => _dispatcher.InvokeAsync(action);

    private void OnValues(ValuesMessage values)
    {
        Interlocked.Exchange(ref _pendingValues, values);
        if (Interlocked.Exchange(ref _valuesScheduled, 1) == 0)
        {
            _dispatcher.InvokeAsync(ApplyPendingValues, DispatcherPriority.Background);
        }
    }

    private void ApplyPendingValues()
    {
        Interlocked.Exchange(ref _valuesScheduled, 0);
        var values = Interlocked.Exchange(ref _pendingValues, null);
        if (values is null || values.CatalogVersion != _catalogVersion)
        {
            return;
        }

        var count = Math.Min(_ordered.Length, values.Values.Length);
        for (var i = 0; i < count; i++)
        {
            _ordered[i].Update(values.Values[i], At(values.Min, i), At(values.Max, i), values.TimestampUnixMs);
        }

        UpdateCalculated(values.TimestampUnixMs);
        LastUpdate = DateTimeOffset.FromUnixTimeMilliseconds(values.TimestampUnixMs).LocalDateTime;
        ValuesApplied?.Invoke(values.TimestampUnixMs);
    }

    private static float? At(float?[] array, int index) => index < array.Length ? array[index] : null;

    private void ApplyCatalog(SensorCatalog catalog)
    {
        var hardware = catalog.Hardware.GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First());
        string GroupName(string hardwareId)
        {
            if (!hardware.TryGetValue(hardwareId, out var hw))
            {
                return hardwareId;
            }

            return hw.ParentId is { } parentId && hardware.TryGetValue(parentId, out var parent)
                ? $"{parent.Name} · {hw.Name}"
                : hw.Name;
        }

        var items = catalog.Sensors
            .Select(s => new SensorItem(
                s,
                GroupName(s.HardwareId),
                hardware.TryGetValue(s.HardwareId, out var hw) ? hw.Kind : HardwareKind.Other,
                Display))
            .ToArray();

        foreach (var item in items)
        {
            item.ApplyOverride(_customizations.Get(item.Id));
        }

        _customizations.RecordKnown(items);
        MotherboardName = catalog.Hardware.FirstOrDefault(h => h.Kind == HardwareKind.Motherboard)?.Name;

        _catalogVersion = catalog.Version;
        _ordered = items;
        _byId = items.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Sensors.Clear();
        foreach (var item in items)
        {
            Sensors.Add(item);
        }

        _calcItems = []; // Sensors was cleared
        BuildCalculated();
        HardwareCount = catalog.Hardware.Count;
        CatalogApplied?.Invoke();
        _ = BackfillHistoryAsync(catalog.Version, items);
    }

    /// <summary>
    /// Loads the collector's recorded history (up to an hour) into every sensor, so sparklines and charts are
    /// full as soon as the app opens. Requested in batches to keep each reply small.
    /// </summary>
    private async Task BackfillHistoryAsync(int catalogVersion, SensorItem[] items)
    {
        const int BatchSize = 64;
        for (var start = 0; start < items.Length; start += BatchSize)
        {
            var batch = items.Skip(start).Take(BatchSize).ToArray();
            HistoryResponse response;
            try
            {
                response = await _client.RequestHistoryAsync([.. batch.Select(i => i.Id)], SensorItem.HistoryCapacity);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                return; // Disconnected; the next catalog triggers a fresh backfill.
            }

            // Continuations run on the UI thread (no ConfigureAwait(false)); bail if the catalog moved on meanwhile.
            if (catalogVersion != _catalogVersion)
            {
                return;
            }

            for (var i = 0; i < batch.Length && i < response.Values.Length; i++)
            {
                batch[i].History.Backfill(response.TimestampsUnixMs, batch[i].CorrectRaw(response.Values[i]));
            }
        }
    }
}
