using MessagePack;
using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Protocol;

/// <summary>
/// Messages exchanged between the collector service and UI clients over the named pipe.
/// Serialized with MessagePack as a union, so each frame says which message it carries.
/// </summary>
/// <remarks>
/// Union keys and <see cref="KeyAttribute"/> numbers are wire format: never renumber, only append.
/// Bump <see cref="ProtocolInfo.Version"/> on incompatible changes.
/// </remarks>
[Union(0, typeof(HelloMessage))]
[Union(1, typeof(CatalogMessage))]
[Union(2, typeof(ValuesMessage))]
[Union(3, typeof(HistoryRequest))]
[Union(4, typeof(HistoryResponse))]
[Union(5, typeof(SetConfigRequest))]
[Union(6, typeof(SourceStatusMessage))]
[Union(7, typeof(CollectorStatusMessage))]
[Union(8, typeof(ResetMinMaxRequest))]
public interface IProtocolMessage;

public static class ProtocolInfo
{
    /// <summary>
    /// 2: SetConfigRequest gained ControllersEnabled/Persist; CollectorStatusMessage added.
    /// 3: long history (HistoryRequest range/MaxPoints, HistoryResponse Min/Max/NextFrom), ResetMinMaxRequest,
    ///    CollectorStatusMessage StartedUnixMs/MinMaxResetUnixMs/DiskHistory.
    /// </summary>
    public const int Version = 3;

    /// <summary>First version with CollectorStatusMessage and SetConfigRequest Persist/ControllersEnabled.</summary>
    public const int SettingsVersion = 2;

    /// <summary>First version with long history (range/MaxPoints), ResetMinMaxRequest and the disk-history status.</summary>
    public const int LongHistoryVersion = 3;

    public const string PipeName = "SpaceDeck";
}

/// <summary>Server → client, first message after connecting.</summary>
[MessagePackObject]
public sealed record HelloMessage(
    [property: Key(0)] int ProtocolVersion,
    [property: Key(1)] string ServerVersion,
    [property: Key(2)] int PollIntervalMs) : IProtocolMessage;

/// <summary>Server → client: the sensor list. Sent after Hello and again whenever it changes.</summary>
[MessagePackObject]
public sealed record CatalogMessage(
    [property: Key(0)] SensorCatalog Catalog) : IProtocolMessage;

/// <summary>Server → client, every poll: values in catalog order. Compact by design.</summary>
[MessagePackObject]
public sealed record ValuesMessage(
    [property: Key(0)] int CatalogVersion,
    [property: Key(1)] long Sequence,
    [property: Key(2)] long TimestampUnixMs,
    [property: Key(3)] float?[] Values,
    [property: Key(4)] float?[] Min,
    [property: Key(5)] float?[] Max) : IProtocolMessage;

/// <summary>
/// Client → server: history of some sensors. Either the last <see cref="WindowSeconds"/>, or (v3) the range
/// <see cref="FromUnixMs"/>–<see cref="ToUnixMs"/>. With <see cref="MaxPoints"/> &gt; 0 the collector averages into at most
/// that many points (and fills Min/Max); 0 = every sample.
/// </summary>
[MessagePackObject]
public sealed record HistoryRequest(
    [property: Key(0)] int RequestId,
    [property: Key(1)] string[] SensorIds,
    [property: Key(2)] int WindowSeconds,
    [property: Key(3)] long? FromUnixMs = null,
    [property: Key(4)] long? ToUnixMs = null,
    [property: Key(5)] int MaxPoints = 0) : IProtocolMessage;

/// <summary>
/// Server → client: history samples, oldest first. <see cref="Values"/> is parallel to <see cref="HistoryRequest.SensorIds"/>.
/// <see cref="Min"/>/<see cref="Max"/> are set when samples were combined (per point). When the answer was too big for
/// one frame, <see cref="NextFromUnixMs"/> says where to continue.
/// </summary>
[MessagePackObject]
public sealed record HistoryResponse(
    [property: Key(0)] int RequestId,
    [property: Key(1)] long[] TimestampsUnixMs,
    [property: Key(2)] string[] SensorIds,
    [property: Key(3)] float?[][] Values,
    [property: Key(4)] float?[][]? Min = null,
    [property: Key(5)] float?[][]? Max = null,
    [property: Key(6)] long? NextFromUnixMs = null) : IProtocolMessage;

/// <summary>Client → server (v3): restart min/max tracking for these sensors, or all when <see cref="SensorIds"/> is null.</summary>
[MessagePackObject]
public sealed record ResetMinMaxRequest(
    [property: Key(0)] string[]? SensorIds) : IProtocolMessage;

/// <summary>
/// Client → server: change collector settings. Null fields are left unchanged. With <see cref="Persist"/> the
/// collector also writes the result to collector.json, so it survives a restart.
/// </summary>
[MessagePackObject]
public sealed record SetConfigRequest(
    [property: Key(0)] int? PollIntervalMs,
    [property: Key(1)] bool? StorageEnabled,
    [property: Key(2)] bool? NetworkEnabled,
    [property: Key(3)] bool? ControllersEnabled = null,
    [property: Key(4)] bool Persist = false) : IProtocolMessage;

/// <summary>
/// Server → client: the collector's current settings and how it's running. Sent after Hello and after every
/// settings change. <see cref="ConfigError"/> is set when saving collector.json failed.
/// </summary>
[MessagePackObject]
public sealed record CollectorStatusMessage(
    [property: Key(0)] int PollIntervalMs,
    [property: Key(1)] bool StorageEnabled,
    [property: Key(2)] bool NetworkEnabled,
    [property: Key(3)] bool ControllersEnabled,
    [property: Key(4)] int HistorySeconds,
    [property: Key(5)] bool IsService,
    [property: Key(6)] bool IsElevated,
    [property: Key(7)] string ConfigPath,
    [property: Key(8)] string? ConfigError,
    [property: Key(9)] long StartedUnixMs = 0,
    [property: Key(10)] long? MinMaxResetUnixMs = null,
    [property: Key(11)] bool DiskHistory = false) : IProtocolMessage;

/// <summary>Server → client: health of each source, so the UI can show "PawnIO missing" etc.</summary>
[MessagePackObject]
public sealed record SourceStatusMessage(
    [property: Key(0)] SourceStatusEntry[] Sources) : IProtocolMessage;

[MessagePackObject]
public sealed record SourceStatusEntry(
    [property: Key(0)] string Source,
    [property: Key(1)] SourceStatus Status,
    [property: Key(2)] string? Message);
