namespace SpaceDeck.Collector;

/// <summary>
/// Collector settings. Bound from the "Collector" section of
/// <c>%ProgramData%\SpaceDeck\collector.json</c> (optional; defaults apply when missing).
/// </summary>
public sealed class CollectorOptions
{
    public const string SectionName = "Collector";
    public const int MinPollIntervalMs = 250;
    public const int MaxPollIntervalMs = 5000;

    /// <summary>How often sensors are read. Clamped to 250–5000 ms.</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>How much history to keep in memory, in seconds of samples at the configured interval.</summary>
    public int HistorySeconds { get; set; } = 3600;

    /// <summary>Keep history on disk (24 h of every sample + 7 days per minute) in %ProgramData%\SpaceDeck\history.</summary>
    public bool DiskHistoryEnabled { get; set; } = true;

    /// <summary>Hours of full-rate history on disk.</summary>
    public int DiskRawHours { get; set; } = 24;

    /// <summary>Days of per-minute history on disk.</summary>
    public int DiskMinuteDays { get; set; } = 7;

    /// <summary>Size cap for the history folder, in MB (oldest files are deleted first).</summary>
    public int DiskHistoryMaxMB { get; set; } = 1024;

    /// <summary>Include drives (SMART polling can wake sleeping HDDs).</summary>
    public bool StorageEnabled { get; set; }

    /// <summary>Include network adapters (throughput, totals).</summary>
    public bool NetworkEnabled { get; set; }

    /// <summary>
    /// Include USB fan/AIO controllers LHM supports. Off by default: FanControl (and its plugins) may own them,
    /// and SpaceDeck must never compete with FanControl for a device.
    /// </summary>
    public bool ControllersEnabled { get; set; }

    /// <summary>Named-pipe name. Change only for testing or running two collectors side by side.</summary>
    public string PipeName { get; set; } = Core.Protocol.ProtocolInfo.PipeName;

    /// <summary>
    /// Demo/replay mode: play back this recording (<c>.sdrec</c>) or hardware report instead of reading hardware.
    /// Set from the command line only (<c>--replay file</c>); the key in collector.json is ignored, so the file can't
    /// switch the service into replay mode. Never saved. Disk history and saving settings are off.
    /// </summary>
    public string? ReplayFile { get; set; }

    /// <summary>Exit when this process ends (the app that started a replay collector). Command line only: <c>--parent-pid</c>.</summary>
    public int? ParentProcessId { get; set; }

    public bool IsReplay => !string.IsNullOrWhiteSpace(ReplayFile);

    public static int ClampInterval(int ms) => Math.Clamp(ms, MinPollIntervalMs, MaxPollIntervalMs);

    /// <summary>Ring-buffer length that covers <see cref="HistorySeconds"/> at the configured interval.</summary>
    public int HistoryCapacity()
    {
        var samples = (long)Math.Max(HistorySeconds, 60) * 1000 / ClampInterval(PollIntervalMs);
        return (int)Math.Clamp(samples, 60, 86_400);
    }
}
