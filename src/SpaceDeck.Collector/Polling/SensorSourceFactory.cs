using SpaceDeck.Collector.Plugins;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Replay;
using SpaceDeck.Sources.Lhm;

namespace SpaceDeck.Collector.Polling;

/// <summary>Creates the sensor sources for the current settings. Swappable so tests can use fakes.</summary>
public interface ISensorSourceFactory
{
    IReadOnlyList<ISensorSource> Create(CollectorOptions options);
}

/// <summary>
/// Production sources: LibreHardwareMonitor (on PawnIO), then the Lian Li readings FanControl's plugin sends over the
/// plugin pipe (SpaceDeck never opens those devices itself: FanControl.LianLi owns them). In replay mode only the
/// recording is played; no hardware is touched and no plugin is listened to.
/// </summary>
/// <param name="pluginBridge">
/// The collector's one plugin bridge, fed by <see cref="PluginPipeServer"/>; null for none. Every <see cref="Create"/>
/// returns this same instance, so re-opening the sources (sensor groups changed) keeps its catalog and its pipe feed.
/// </param>
public sealed class DefaultSensorSourceFactory(PluginBridgeSource? pluginBridge = null) : ISensorSourceFactory
{
    public IReadOnlyList<ISensorSource> Create(CollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IsReplay)
        {
            return [new ReplaySource(options.ReplayFile!)];
        }

        var lhm = new LhmSource(new LhmOptions
        {
            Storage = options.StorageEnabled,
            Network = options.NetworkEnabled,
            Controller = options.ControllersEnabled,
        });
        if (pluginBridge is null)
        {
            return [lhm];
        }

        return [lhm, pluginBridge];
    }
}
