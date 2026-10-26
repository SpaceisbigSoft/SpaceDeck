using SensorDeck.Core.Model;
using SensorDeck.Core.Replay;
using SensorDeck.Sources.Lhm;

namespace SensorDeck.Collector.Polling;

/// <summary>Creates the sensor sources for the current settings. Swappable so tests can use fakes.</summary>
public interface ISensorSourceFactory
{
    IReadOnlyList<ISensorSource> Create(CollectorOptions options);
}

/// <summary>
/// Production sources: LibreHardwareMonitor (on PawnIO). Lian Li is on hold — FanControl.LianLi owns those devices.
/// In replay mode only the recording is played; no hardware is touched.
/// </summary>
public sealed class DefaultSensorSourceFactory : ISensorSourceFactory
{
    public IReadOnlyList<ISensorSource> Create(CollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IsReplay)
        {
            return [new ReplaySource(options.ReplayFile!)];
        }

        return
        [
            new LhmSource(new LhmOptions
            {
                Storage = options.StorageEnabled,
                Network = options.NetworkEnabled,
                Controller = options.ControllersEnabled,
            }),
        ];
    }
}
