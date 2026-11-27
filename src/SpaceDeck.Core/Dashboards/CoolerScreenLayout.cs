using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Dashboards;

/// <summary>What the cooler screen layout needs to know about a sensor (the app maps its live sensors to this).</summary>
/// <param name="OriginalName">The name the hardware reports (never the user's rename), so the picks recognize it.</param>
public sealed record ScreenSensor(string Id, string HardwareId, HardwareKind HardwareKind, SensorKind Kind, string OriginalName, bool IsAdvanced);

/// <summary>The readings the built-in cooler screen layout shows, most important first; any may be missing.</summary>
public sealed record CoolerScreenPicks(ScreenSensor? Cpu, ScreenSensor? Gpu, ScreenSensor? Coolant, ScreenSensor? Pump);

/// <summary>
/// The built-in "Cooler screen" dashboard: a screen layout (<see cref="ScreenLayoutGrid"/>) whose tiles fill the whole
/// screen with large numbers, readable at arm's length on a 480 × 480 LCD: the CPU temperature biggest, then the GPU
/// and coolant temperatures and the pump speed. Readings that aren't there leave no gap: the others grow.
/// </summary>
public static class CoolerScreenLayout
{
    public const string Name = "Cooler screen";

    /// <summary>Intel first, then AMD (Zen: Tctl/Tdie), then generic names.</summary>
    public static IReadOnlyList<string> CpuTemperatureNames { get; } =
        ["CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "CCDs Max (Tdie)", "Core Max", "Package", "CPU Core"];

    public static IReadOnlyList<string> GpuTemperatureNames { get; } = ["GPU Core", "GPU Temperature", "GPU Edge"];

    /// <summary>
    /// Picks the readings by original name and kind: the CPU and (discrete first) GPU temperature as on the main
    /// dashboard; coolant and pump from the cooler with the screen when it has them, else from any hardware.
    /// </summary>
    public static CoolerScreenPicks Pick(IReadOnlyList<ScreenSensor> sensors, string? coolerHardwareId)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        var cooler = sensors.Where(s => s.HardwareId == coolerHardwareId).ToList();
        var gpu = sensors.Where(s => s.HardwareKind == HardwareKind.Gpu)
            .GroupBy(s => s.HardwareId)
            .OrderByDescending(g => g.Any(s => s.Kind == SensorKind.Temperature))
            .ThenByDescending(g => g.Count())
            .Select(g => g.ToList())
            .FirstOrDefault() ?? [];

        var coolant = cooler.FirstOrDefault(s => s.Kind == SensorKind.Temperature && Has(s, "Coolant"))
            ?? cooler.FirstOrDefault(s => s.Kind == SensorKind.Temperature)
            ?? sensors.FirstOrDefault(s => s.Kind == SensorKind.Temperature && Has(s, "Coolant"))
            ?? PickNamed(sensors, HardwareKind.Cooler, SensorKind.Temperature, ["Coolant", "Liquid", "Water"]);
        var pump = cooler.FirstOrDefault(s => s.Kind == SensorKind.Fan && Has(s, "Pump"))
            ?? sensors.FirstOrDefault(s => s.Kind == SensorKind.Fan && Has(s, "Pump"));

        return new CoolerScreenPicks(
            PickNamed(sensors, HardwareKind.Cpu, SensorKind.Temperature, CpuTemperatureNames),
            PickNamed(gpu, HardwareKind.Gpu, SensorKind.Temperature, GpuTemperatureNames),
            coolant,
            pump);
    }

    /// <summary>
    /// Where the tiles go for <paramref name="count"/> readings (most important first) on <paramref name="grid"/>: the
    /// first takes the top half or so, the rest share what's left; together they cover the whole area.
    /// </summary>
    public static IReadOnlyList<CellRect> Arrange(int count, ScreenLayoutGrid grid)
    {
        var columns = grid.Columns;
        var rows = grid.Rows;
        var half = columns / 2;
        var top = Math.Max(1, (int)Math.Round(rows * 5 / 11.0)); // 5 of 11 rows on the GA II LCD
        return count switch
        {
            <= 0 => [],
            1 => [new CellRect(0, 0, columns, rows)],
            2 => [new CellRect(0, 0, columns, rows - (rows / 2)), new CellRect(0, rows - (rows / 2), columns, rows / 2)],
            3 => [new CellRect(0, 0, columns, top), new CellRect(0, top, half, rows - top), new CellRect(half, top, columns - half, rows - top)],
            _ => ArrangeFour(columns, rows, top, half),
        };
    }

    /// <summary>The built-in layout for these readings, as a screen layout for a <paramref name="width"/> × <paramref name="height"/> screen.</summary>
    public static DashboardDocument Create(CoolerScreenPicks picks, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(picks);
        var screen = new DashboardScreen
        {
            Width = Math.Clamp(width, 1, DashboardScreen.MaxSide),
            Height = Math.Clamp(height, 1, DashboardScreen.MaxSide),
        };
        var doc = new DashboardDocument { Name = Name, Screen = screen };

        var readings = new List<(string Title, ScreenSensor Sensor)>();
        if (picks.Cpu is { } cpu)
        {
            readings.Add((Has(cpu, "Package") ? "CPU package" : "CPU", cpu));
        }

        if (picks.Gpu is { } gpu)
        {
            readings.Add(("GPU", gpu));
        }

        if (picks.Coolant is { } coolant)
        {
            readings.Add(("Coolant", coolant));
        }

        if (picks.Pump is { } pump)
        {
            readings.Add(("Pump", pump));
        }

        var cells = Arrange(readings.Count, ScreenLayoutGrid.For(screen));
        for (var i = 0; i < readings.Count && i < cells.Count; i++)
        {
            doc.Tiles.Add(new TileSpec
            {
                Type = WidgetType.BigNumber,
                Title = readings[i].Title,
                Col = cells[i].Col,
                Row = cells[i].Row,
                ColSpan = cells[i].ColSpan,
                RowSpan = cells[i].RowSpan,
                Sensors = [readings[i].Sensor.Id],
                ShowMinMax = false, // a small screen reads better with just the numbers
            });
        }

        return doc;
    }

    /// <summary>First: the top rows across. Then two side by side, then the last across the bottom.</summary>
    private static CellRect[] ArrangeFour(int columns, int rows, int top, int half)
    {
        var rest = rows - top;
        var middle = rest - (rest / 2);
        return
        [
            new CellRect(0, 0, columns, top),
            new CellRect(0, top, half, middle),
            new CellRect(half, top, columns - half, middle),
            new CellRect(0, top + middle, columns, rest - middle),
        ];
    }

    private static bool Has(ScreenSensor sensor, string text) => sensor.OriginalName.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first sensor matching a name in order; otherwise any non-advanced one of that kind.</summary>
    private static ScreenSensor? PickNamed(IReadOnlyList<ScreenSensor> sensors, HardwareKind hardware, SensorKind kind, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (sensors.FirstOrDefault(s => s.HardwareKind == hardware && s.Kind == kind && s.OriginalName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match;
            }
        }

        return sensors.FirstOrDefault(s => s.HardwareKind == hardware && s.Kind == kind && !s.IsAdvanced);
    }
}
