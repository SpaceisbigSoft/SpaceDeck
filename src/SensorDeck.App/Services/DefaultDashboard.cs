using SensorDeck.App.ViewModels;
using SensorDeck.Core.Dashboards;
using SensorDeck.Core.Model;

namespace SensorDeck.App.Services;

/// <summary>
/// Builds the first dashboard from whatever hardware was detected, so it's useful before any editing: CPU/GPU
/// temperatures and loads up top, power/pump/system/memory/battery trends, then rails, fans and a temperature chart.
/// </summary>
/// <remarks>
/// Names differ by vendor and chip (Intel "CPU Package" vs AMD "Core (Tctl/Tdie)"; Nuvoton "Vcore" vs ITE
/// "CPU Vcore"), so each tile tries a list of known names, then falls back to any sensor of the right kind. With two
/// or more GPUs the discrete one leads (it has a temperature; integrated ones usually don't) and a second gets its own
/// tile. Laptops get battery tiles; boards without a Super I/O chip simply have no rails or fan tiles.
/// </remarks>
public static class DefaultDashboard
{
    public static DashboardDocument Create(IReadOnlyList<SensorItem> sensors, string name = "Main")
    {
        ArgumentNullException.ThrowIfNull(sensors);
        var doc = new DashboardDocument { Name = name };

        SensorItem? Find(HardwareKind hardware, SensorKind kind, string sensorName) =>
            sensors.FirstOrDefault(s => s.HardwareKind == hardware && s.Kind == kind && s.OriginalName.Equals(sensorName, StringComparison.OrdinalIgnoreCase));

        var gpus = GpusByImportance(sensors);
        var gpu = gpus.FirstOrDefault() ?? new List<SensorItem>();
        var gpu2 = gpus.Skip(1).FirstOrDefault(g => g.Any(s => s.Kind == SensorKind.Temperature));

        var cpuTemp = Pick(sensors, HardwareKind.Cpu, SensorKind.Temperature, CpuTemperatureNames);
        var gpuTemp = Pick(gpu, HardwareKind.Gpu, SensorKind.Temperature, GpuTemperatureNames);

        // Row 0: headline numbers, 3 columns × 4 rows each.
        var col = 0;
        foreach (var (title, sensor) in new[]
                 {
                     (cpuTemp?.OriginalName.Contains("Package", StringComparison.OrdinalIgnoreCase) == true ? "CPU package" : "CPU", cpuTemp),
                     ("GPU core", gpuTemp),
                     ("CPU load", Pick(sensors, HardwareKind.Cpu, SensorKind.Load, ["CPU Total"])),
                     ("GPU load", Pick(gpu, HardwareKind.Gpu, SensorKind.Load, ["GPU Core", "D3D 3D"])),
                 })
        {
            if (sensor is not null)
            {
                doc.Tiles.Add(Tile(WidgetType.BigNumber, title, col, 0, 3, 4, sensor));
                col += 3;
            }
        }

        // Row 4: secondary numbers with trends, 2 × 4 each.
        var memory = sensors.FirstOrDefault(s => s.HardwareKind == HardwareKind.Memory && s.Kind == SensorKind.Load && s.GroupName.Contains("Total", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => s.HardwareKind == HardwareKind.Memory && s.Kind == SensorKind.Load);
        col = 0;
        var battery = sensors.FirstOrDefault(s => s.HardwareKind == HardwareKind.Battery && s.Kind == SensorKind.Level && s.OriginalName.Contains("Charge", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => s.HardwareKind == HardwareKind.Battery && s.Kind == SensorKind.Level);
        var batteryRate = sensors.FirstOrDefault(s => s.HardwareKind == HardwareKind.Battery && s.Kind == SensorKind.Power);
        foreach (var (title, sensor) in new[]
                 {
                     ("CPU power", Pick(sensors, HardwareKind.Cpu, SensorKind.Power, ["CPU Package", "Package"], anyFallback: false)),
                     ("GPU power", Pick(gpu, HardwareKind.Gpu, SensorKind.Power, ["GPU Package", "GPU Power", "GPU Core"])),
                     ("GPU hot spot", Pick(gpu, HardwareKind.Gpu, SensorKind.Temperature, ["GPU Hot Spot"], anyFallback: false)),
                     ("GPU 2", gpu2 is null ? null : Pick(gpu2, HardwareKind.Gpu, SensorKind.Temperature, GpuTemperatureNames)),
                     ("Battery", battery),
                     ("Battery power", batteryRate),
                     ("Pump", sensors.FirstOrDefault(s => s.Kind == SensorKind.Fan && s.OriginalName.Contains("Pump", StringComparison.OrdinalIgnoreCase))),
                     ("System", Pick(sensors, HardwareKind.SuperIo, SensorKind.Temperature, ["System", "Motherboard", "MB"], anyFallback: false)),
                     ("Memory used", memory),
                 })
        {
            if (sensor is not null && col < GridLayout.Columns)
            {
                var tile = Tile(WidgetType.Sparkline, title, col, 4, 2, 4, sensor);
                tile.ShowMinMax = false;
                doc.Tiles.Add(tile);
                col += 2;
            }
        }

        // Row 8: rails · fans · temperature chart, 4 columns each.
        var rails = sensors.Where(s => s is { Kind: SensorKind.Voltage, HasNominal: true }).ToList();
        var vcore = Pick(sensors, HardwareKind.SuperIo, SensorKind.Voltage, ["Vcore", "CPU Vcore", "CPU Core", "VCORE"], anyFallback: false)
            ?? sensors.FirstOrDefault(s => s is { Kind: SensorKind.Voltage, HardwareKind: HardwareKind.SuperIo }
                && s.OriginalName.Contains("Vcore", StringComparison.OrdinalIgnoreCase) && !s.OriginalName.Contains("SoC", StringComparison.OrdinalIgnoreCase))
            ?? Find(HardwareKind.Cpu, SensorKind.Voltage, "CPU Core");
        if (vcore is not null && !rails.Contains(vcore))
        {
            rails.Add(vcore);
        }

        // Built before the first readings arrive, so every header is included; remove idle ones in edit mode.
        var fans = FanRow.Build(sensors)
            .OrderBy(r => r.Fan.HardwareKind == HardwareKind.Gpu)
            .Take(8)
            .Select(r => r.Fan)
            .ToList();

        col = 0;
        if (rails.Count > 0)
        {
            doc.Tiles.Add(Tile(WidgetType.Rails, null, col, 8, 4, TileSpec.DefaultSize(WidgetType.Rails, rails.Count).RowSpan, [.. rails]));
            col += 4;
        }

        if (fans.Count > 0)
        {
            doc.Tiles.Add(Tile(WidgetType.Fans, null, col, 8, 4, TileSpec.DefaultSize(WidgetType.Fans, fans.Count).RowSpan, [.. fans]));
            col += 4;
        }

        var chartSensors = new[] { cpuTemp, gpuTemp }.OfType<SensorItem>().ToArray();
        if (chartSensors.Length > 0)
        {
            doc.Tiles.Add(Tile(WidgetType.Chart, "Temperatures", col, 8, GridLayout.Columns - col, 6, chartSensors));
        }

        return doc;
    }

    /// <summary>GPUs in order of importance: discrete (has a temperature) first, then by how many sensors they report.</summary>
    internal static List<List<SensorItem>> GpusByImportance(IReadOnlyList<SensorItem> sensors) =>
        sensors.Where(s => s.HardwareKind == HardwareKind.Gpu)
            .GroupBy(s => s.HardwareId)
            .OrderByDescending(g => g.Any(s => s.Kind == SensorKind.Temperature))
            .ThenByDescending(g => g.Count())
            .Select(g => g.ToList())
            .ToList();

    /// <summary>Intel first, then AMD (Zen: Tctl/Tdie), then generic names.</summary>
    internal static readonly string[] CpuTemperatureNames =
        ["CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "CCDs Max (Tdie)", "Core Max", "Package", "CPU Core"];

    internal static readonly string[] GpuTemperatureNames = ["GPU Core", "GPU Temperature", "GPU Edge"];

    /// <summary>The first sensor matching a name in order; otherwise (with <paramref name="anyFallback"/>) any of that kind.</summary>
    internal static SensorItem? Pick(IReadOnlyList<SensorItem> sensors, HardwareKind hardware, SensorKind kind, string[] names, bool anyFallback = true)
    {
        foreach (var name in names)
        {
            if (sensors.FirstOrDefault(s => s.HardwareKind == hardware && s.Kind == kind && s.OriginalName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match;
            }
        }

        return anyFallback ? sensors.FirstOrDefault(s => s.HardwareKind == hardware && s.Kind == kind && !s.IsAdvanced) : null;
    }

    private static TileSpec Tile(WidgetType type, string? title, int col, int row, int colSpan, int rowSpan, params SensorItem[] sensors) => new()
    {
        Type = type,
        Title = title,
        Col = col,
        Row = row,
        ColSpan = colSpan,
        RowSpan = rowSpan,
        Sensors = [.. sensors.Select(s => s.Id)],
    };
}
