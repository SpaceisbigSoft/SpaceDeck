using System.Management;
using System.Runtime.InteropServices;
using SpaceDeck.Core.Formatting;

namespace SpaceDeck.App.Services;

/// <summary>An installed memory module as Windows reports it (from the SPD, via the firmware).</summary>
/// <param name="Slot">Motherboard slot number (1-based), when the slot name could be read.</param>
/// <param name="Locator">The firmware's slot name, e.g. "Controller0-DIMMA2".</param>
public sealed record MemoryModule(int? Slot, string Locator, string PartNumber, string Manufacturer, long CapacityBytes)
{
    /// <summary>"Slot 2", or the firmware's slot name when it isn't numbered.</summary>
    public string SlotLabel => Slot is { } n ? $"Slot {n}" : Locator;
}

/// <summary>
/// Reads the installed memory modules (part numbers and slots) from Windows once. LibreHardwareMonitor's own module
/// names can come through garbled, and it doesn't say which slot a module is in.
/// </summary>
public static class MemoryModules
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private static readonly Lazy<IReadOnlyList<MemoryModule>> Installed = new(Query, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The modules, in slot order (empty if Windows didn't say, or in a replay of another PC).</summary>
    public static IReadOnlyList<MemoryModule> All => AppMode.IsReplay ? Array.Empty<MemoryModule>() : Installed.Value;

    /// <summary>"F5-8000J4048F24G", or "A + B" for mixed modules; null when unknown.</summary>
    public static string? PartNumbers(IReadOnlyList<MemoryModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var parts = modules.Select(m => m.PartNumber).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return parts.Count == 0 ? null : string.Join(" + ", parts);
    }

    private static IReadOnlyList<MemoryModule> Query()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceLocator, PartNumber, Manufacturer, Capacity FROM Win32_PhysicalMemory");

            // Normally warmed off the UI thread at startup, but a dashboard section's title may still ask first (on the UI
            // thread); a stuck WMI service must not hang the app.
            // A timeout surfaces as a ManagementException (caught below) and the modules are simply unknown.
            searcher.Options.Timeout = QueryTimeout;
            using var results = searcher.Get();
            var modules = new List<MemoryModule>();
            foreach (var item in results.Cast<ManagementBaseObject>())
            {
                using (item)
                {
                    var locator = (item["DeviceLocator"] as string)?.Trim() ?? "";
                    modules.Add(new MemoryModule(
                        MemorySlots.SlotNumber(locator),
                        locator,
                        (item["PartNumber"] as string)?.Trim() ?? "",
                        (item["Manufacturer"] as string)?.Trim() ?? "",
                        item["Capacity"] is ulong capacity ? (long)Math.Min(capacity, long.MaxValue) : 0));
                }
            }

            return [.. modules.OrderBy(m => m.Slot ?? int.MaxValue).ThenBy(m => m.Locator, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or TypeInitializationException or PlatformNotSupportedException)
        {
            return [];
        }
    }
}
