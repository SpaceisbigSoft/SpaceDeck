using SensorDeck.Core.Alerts;
using SensorDeck.Core.Formatting;
using SensorDeck.Core.Model;

namespace SensorDeck.App.Services;

/// <summary>
/// Built-in limits so alerts are useful before anything is configured. Users can override or disable any of them
/// on the Alerts page. Deliberately conservative: only sensors where crossing the line really means something.
/// </summary>
public static class DefaultAlertRules
{
    /// <summary>
    /// Set when the hardware includes a battery. Laptop CPUs routinely run at 95–100 °C under load (by design), so
    /// their limits are higher to avoid an alert in every game.
    /// </summary>
    public static bool IsLaptop { get; set; }

    /// <summary>The default rule for a sensor, or null if it has none.</summary>
    public static AlertRule? For(SensorItem sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        if (sensor.IsAdvanced)
        {
            return null;
        }

        return sensor.Kind switch
        {
            SensorKind.Temperature => Temperature(sensor),
            SensorKind.Voltage when sensor.Nominal is { } nominal => Voltage(sensor.Id, nominal),
            SensorKind.Fan when IsCritical(sensor.OriginalName) => new AlertRule { SensorId = sensor.Id, CriticalBelow = 200 },
            _ => null,
        };
    }

    private static AlertRule? Temperature(SensorItem sensor)
    {
        var name = sensor.OriginalName;
        (double Warning, double Critical)? limits = sensor.HardwareKind switch
        {
            // i9-14900K and most modern desktop CPUs throttle at 100 °C; laptop chips sit near it by design.
            HardwareKind.Cpu when (Is(name, "CPU Package") || Is(name, "Core Max")) && IsLaptop => (97, 100),
            HardwareKind.Cpu when Is(name, "CPU Package") || Is(name, "Core Max") => (85, 95),
            // AMD Ryzen: Zen 4 holds 95 °C under load on purpose (Zen 3: 90 °C), so only warn above that.
            HardwareKind.Cpu when Is(name, "Core (Tctl/Tdie)") || Is(name, "Core (Tdie)") || Is(name, "Core (Tctl)") || Is(name, "CCDs Max (Tdie)") => (96, 100),
            HardwareKind.Gpu when Is(name, "GPU Core") => (80, 88),
            HardwareKind.Gpu when Is(name, "GPU Hot Spot") => (95, 105),
            HardwareKind.Gpu when Is(name, "GPU Memory") => (95, 105), // AMD memory junction
            HardwareKind.Gpu when Is(name, "GPU Memory Junction") => (95, 105),
            // NVMe drives throttle around 70–80 °C.
            HardwareKind.Storage when Is(name, "Temperature") || Is(name, "Composite Temperature") => (60, 70),
            HardwareKind.SuperIo when Is(name, "VRM MOS") => (90, 105),
            _ => null,
        };

        return limits is { } l ? new AlertRule { SensorId = sensor.Id, WarningAbove = l.Warning, CriticalAbove = l.Critical } : null;
    }

    private static AlertRule? Voltage(string id, double nominal)
    {
        if (VoltageRails.IsBattery(nominal))
        {
            return new AlertRule { SensorId = id, WarningBelow = VoltageRails.BatteryWarningVolts, CriticalBelow = VoltageRails.BatteryCriticalVolts };
        }

        if (nominal <= 0)
        {
            return null; // -12 V is rarely monitored and has looser limits; leave it to the user.
        }

        // Same bands as the tolerance bars: warning outside ±5 %, critical outside ±8 %.
        return new AlertRule
        {
            SensorId = id,
            WarningBelow = Math.Round(nominal * (1 - VoltageRails.AtxTolerance), 3),
            WarningAbove = Math.Round(nominal * (1 + VoltageRails.AtxTolerance), 3),
            CriticalBelow = Math.Round(nominal * (1 - VoltageRails.CriticalTolerance), 3),
            CriticalAbove = Math.Round(nominal * (1 + VoltageRails.CriticalTolerance), 3),
        };
    }

    /// <summary>The pump and CPU fan stopping is an emergency; case fans at 0 RPM are often intentional.</summary>
    private static bool IsCritical(string name) =>
        name.Contains("Pump", StringComparison.OrdinalIgnoreCase) || Is(name, "CPU Fan");

    private static bool Is(string name, string expected) => name.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
