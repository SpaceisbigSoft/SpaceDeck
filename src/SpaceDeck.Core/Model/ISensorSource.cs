namespace SpaceDeck.Core.Model;

/// <summary>
/// A provider of hardware sensors (LibreHardwareMonitor, the Lian Li cooler, …).
/// </summary>
/// <remarks>
/// <para>Threading: every member except <see cref="Name"/>, <see cref="Status"/> and <see cref="StatusMessage"/>
/// must be called from a single thread (the collector's poll thread). LibreHardwareMonitor is not thread-safe.</para>
/// <para>Poll cycle: <see cref="Update"/> → <see cref="TryRefreshCatalog"/> → <see cref="Read"/>.</para>
/// </remarks>
public interface ISensorSource : IDisposable
{
    /// <summary>Short source key used as the id prefix, e.g. <c>lhm</c>.</summary>
    string Name { get; }

    SourceStatus Status { get; }

    /// <summary>User-facing explanation when <see cref="Status"/> isn't <see cref="SourceStatus.Ok"/>.</summary>
    string? StatusMessage { get; }

    SensorCatalog Catalog { get; }

    /// <summary>Opens the source and builds the first catalog. Never throws for "hardware missing" cases; sets <see cref="Status"/> instead.</summary>
    void Open();

    /// <summary>Refreshes hardware readings.</summary>
    void Update();

    /// <summary>Rebuilds the catalog if hardware was added or removed. Returns true if <see cref="Catalog"/> changed.</summary>
    bool TryRefreshCatalog();

    /// <summary>Copies current readings into <paramref name="into"/>, whose length must equal <c>Catalog.Sensors.Count</c>.</summary>
    void Read(Span<SensorReading> into);
}
