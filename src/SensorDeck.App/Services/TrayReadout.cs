using SensorDeck.Core.Model;

namespace SensorDeck.App.Services;

/// <summary>Keeps the tray icon's hover text current: CPU/GPU temperatures, pump, and any active alerts.</summary>
public sealed class TrayReadout
{
    private const long MinIntervalMs = 2000;

    private readonly SensorStore _store;
    private readonly TrayService _tray;
    private readonly AlertService _alerts;
    private SensorItem? _cpu;
    private SensorItem? _gpu;
    private SensorItem? _pump;
    private long _lastUpdate;

    public TrayReadout(SensorStore store, TrayService tray, AlertService alerts)
    {
        _store = store;
        _tray = tray;
        _alerts = alerts;
        store.CatalogApplied += FindSensors;
        store.ValuesApplied += OnValues;
        store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SensorStore.ConnectionState) && store.ConnectionState != ConnectionState.Connected)
            {
                _tray.SetTooltip("SensorDeck — collector offline");
            }
        };
    }

    private void FindSensors()
    {
        // Same names as the default dashboard (Intel and AMD CPUs), and the most important GPU (the discrete one), not
        // whichever comes first in the catalog.
        _cpu = DefaultDashboard.Pick(_store.Sensors, HardwareKind.Cpu, SensorKind.Temperature, DefaultDashboard.CpuTemperatureNames, anyFallback: false);
        _gpu = DefaultDashboard.GpusByImportance(_store.Sensors).FirstOrDefault() is { } gpu
            ? DefaultDashboard.Pick(gpu, HardwareKind.Gpu, SensorKind.Temperature, DefaultDashboard.GpuTemperatureNames, anyFallback: false)
            : null;
        _pump = _store.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Fan && s.OriginalName.Contains("Pump", StringComparison.OrdinalIgnoreCase));
        _lastUpdate = 0;
    }

    private void OnValues(long timestampUnixMs)
    {
        // A timestamp going backwards (a replay looping, the clock set back) updates at once instead of freezing the
        // tooltip until time catches up with the last one.
        var elapsed = timestampUnixMs - _lastUpdate;
        if (elapsed is >= 0 and < MinIntervalMs)
        {
            return;
        }

        _lastUpdate = timestampUnixMs;
        var lines = new List<string> { "SensorDeck" };
        var temps = new List<string>();
        if (_cpu is not null)
        {
            temps.Add($"CPU {_cpu.DisplayValue}");
        }

        if (_gpu is not null)
        {
            temps.Add($"GPU {_gpu.DisplayValue}");
        }

        if (temps.Count > 0)
        {
            lines.Add(string.Join(" · ", temps));
        }

        if (_pump is not null)
        {
            lines.Add($"Pump {_pump.DisplayValue}");
        }

        if (_alerts.Active.Count > 0)
        {
            lines.Add(_alerts.Active.Count == 1 ? $"Alert: {_alerts.Active[0].Sensor.Name}" : $"{_alerts.Active.Count} alerts");
        }

        _tray.SetTooltip(string.Join("\n", lines));
    }
}
