using LibreHardwareMonitor.Hardware;
using SensorDeck.Core.Formatting;
using SensorDeck.Core.Model;
using SensorDeck.Sources.Lhm.SystemChecks;

namespace SensorDeck.Sources.Lhm;

/// <summary>
/// Sensor source backed by LibreHardwareMonitorLib, which reaches the hardware through the PawnIO driver.
/// </summary>
/// <remarks>Not thread-safe: call everything except the status properties from one thread.</remarks>
public sealed class LhmSource : ISensorSource
{
    private readonly LhmOptions _options;
    private Computer? _computer;
    private ISensor[] _sensors = [];
    private volatile bool _catalogDirty;
    private int _catalogVersion;
    private bool _disposed;

    // Status after Open, restored when Update works again after a failure (a device that glitched once).
    private SourceStatus _openStatus = SourceStatus.NotOpened;
    private string? _openStatusMessage;
    private bool _updateFailed;

    public LhmSource(LhmOptions? options = null)
    {
        _options = options ?? new LhmOptions();
    }

    public string Name => LhmMapping.SourceName;

    public SourceStatus Status { get; private set; } = SourceStatus.NotOpened;

    public string? StatusMessage { get; private set; }

    public SensorCatalog Catalog { get; private set; } = SensorCatalog.Empty;

    /// <summary>Result of the PawnIO check made in <see cref="Open"/>.</summary>
    public PawnIoInfo? PawnIo { get; private set; }

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_computer is not null)
        {
            return;
        }

        // Check prerequisites first, but open regardless: GPU (vendor APIs) and some other
        // sensors still work without PawnIO or admin rights, and a partial view beats none.
        PawnIo = PawnIoDetector.Detect();
        var elevated = Elevation.IsElevated();

        if (!elevated)
        {
            SetStatus(SourceStatus.NotElevated, "Not running as administrator: motherboard, CPU and memory sensors will be missing.");
        }
        else if (PawnIo.State == PawnIoState.NotInstalled)
        {
            SetStatus(SourceStatus.DriverMissing, PawnIo.Detail);
        }
        else
        {
            SetStatus(SourceStatus.Ok, PawnIo.State == PawnIoState.Unknown ? PawnIo.Detail : null);
        }

        _openStatus = Status;
        _openStatusMessage = StatusMessage;
        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = _options.Cpu,
                IsGpuEnabled = _options.Gpu,
                IsMotherboardEnabled = _options.Motherboard,
                IsMemoryEnabled = _options.Memory,
                IsControllerEnabled = _options.Controller,
                IsPsuEnabled = _options.Psu,
                IsBatteryEnabled = _options.Battery,
                IsStorageEnabled = _options.Storage,
                IsNetworkEnabled = _options.Network,
            };

            computer.HardwareAdded += OnHardwareChanged;
            computer.HardwareRemoved += OnHardwareChanged;
            computer.Open();
            _computer = computer;

            // Prime values so the first Read isn't all nulls.
            UpdateAll(computer.Hardware);
            RebuildCatalog();
        }
        catch (Exception ex)
        {
            if (_computer is null && computer is not null)
            {
                // Open itself failed: release whatever it did open (driver handles) rather than leak it.
                computer.HardwareAdded -= OnHardwareChanged;
                computer.HardwareRemoved -= OnHardwareChanged;
                try
                {
                    computer.Close();
                }
                catch (Exception)
                {
                    // Already failing; the open error below is the one worth reporting.
                }
            }

            SetStatus(SourceStatus.Error, $"LibreHardwareMonitor failed to open: {ex.Message}");
        }
    }

    public void Update()
    {
        if (_computer is null)
        {
            return;
        }

        try
        {
            UpdateAll(_computer.Hardware);
            if (_updateFailed)
            {
                _updateFailed = false;
                SetStatus(_openStatus, _openStatusMessage);
            }
        }
        catch (Exception ex)
        {
            // One misbehaving device shouldn't stop polling; keep last values and report until it works again.
            _updateFailed = true;
            SetStatus(SourceStatus.Error, $"Update failed: {ex.Message}");
        }
    }

    public bool TryRefreshCatalog()
    {
        if (!_catalogDirty || _computer is null)
        {
            return false;
        }

        RebuildCatalog();
        return true;
    }

    public void Read(Span<SensorReading> into)
    {
        if (into.Length != _sensors.Length)
        {
            throw new ArgumentException($"Expected a span of {_sensors.Length} readings, got {into.Length}.", nameof(into));
        }

        for (var i = 0; i < _sensors.Length; i++)
        {
            var s = _sensors[i];
            into[i] = new SensorReading(s.Value, s.Min, s.Max);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_computer is not null)
        {
            _computer.HardwareAdded -= OnHardwareChanged;
            _computer.HardwareRemoved -= OnHardwareChanged;
            try
            {
                _computer.Close();
            }
            catch
            {
                // Closing during shutdown; nothing useful to do with a failure.
            }

            _computer = null;
        }
    }

    private void OnHardwareChanged(IHardware hardware) => _catalogDirty = true;

    private void SetStatus(SourceStatus status, string? message)
    {
        Status = status;
        StatusMessage = message;
    }

    private static void UpdateAll(IEnumerable<IHardware> hardware)
    {
        foreach (var hw in hardware)
        {
            hw.Update();
            // Super I/O chips (motherboard voltages, temps, fans) live under the motherboard as sub-hardware.
            UpdateAll(hw.SubHardware);
        }
    }

    private void RebuildCatalog()
    {
        _catalogDirty = false;
        var hardware = new List<HardwareInfo>();
        var sensorInfos = new List<SensorInfo>();
        var sensors = new List<ISensor>();

        void Walk(IHardware hw, string? parentId)
        {
            var hwId = LhmMapping.HardwareId(hw);
            var hwKind = LhmMapping.ToHardwareKind(hw.HardwareType);
            var hwName = DisplayNames.Sanitize(hw.Name, LhmMapping.FallbackName(hwKind));
            hardware.Add(new HardwareInfo(hwId, hwName, hwKind, parentId, Name));

            foreach (var sensor in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
            {
                // LHM keeps 24 h of every sensor's values by default (~0.5 MB per sensor at 1 Hz, shifted with
                // RemoveAt(0) once full). SensorDeck keeps its own history, so turn LHM's off (zero also clears it).
                sensor.ValuesTimeWindow = TimeSpan.Zero;
                var kind = LhmMapping.ToSensorKind(sensor.SensorType);
                var nominal = kind == SensorKind.Voltage ? VoltageRails.InferNominal(sensor.Name) : null;
                var sensorName = DisplayNames.Sanitize(sensor.Name, $"{kind} #{sensor.Index + 1}");
                sensorInfos.Add(new SensorInfo(LhmMapping.SensorId(sensor), hwId, sensorName, kind, Units.For(kind), Name, nominal));
                sensors.Add(sensor);
            }

            foreach (var sub in hw.SubHardware)
            {
                Walk(sub, hwId);
            }
        }

        foreach (var hw in _computer!.Hardware)
        {
            Walk(hw, null);
        }

        _sensors = [.. sensors];
        Catalog = new SensorCatalog(++_catalogVersion, hardware, sensorInfos);
    }
}
