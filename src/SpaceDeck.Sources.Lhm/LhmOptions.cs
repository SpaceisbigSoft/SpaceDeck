namespace SpaceDeck.Sources.Lhm;

/// <summary>Which hardware groups LibreHardwareMonitor should open.</summary>
public sealed record LhmOptions
{
    public bool Cpu { get; init; } = true;
    public bool Gpu { get; init; } = true;
    public bool Motherboard { get; init; } = true;
    public bool Memory { get; init; } = true;
    /// <summary>
    /// USB fan/pump controllers and AIOs LHM knows about (Corsair, NZXT, Aquacomputer…).
    /// Off by default: these are USB devices that FanControl (or vendor software) may already own,
    /// and two programs talking to the same controller can interfere. Motherboard fan headers are
    /// read through <see cref="Motherboard"/> and are safe to share.
    /// </summary>
    public bool Controller { get; init; }
    public bool Psu { get; init; } = true;
    public bool Battery { get; init; } = true;
    /// <summary>Off by default: SMART polling can wake sleeping HDDs.</summary>
    public bool Storage { get; init; }
    public bool Network { get; init; }
}
