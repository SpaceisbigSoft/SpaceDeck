using SensorDeck.Core.Dashboards;

namespace SensorDeck.Core.Tests;

public class GridLayoutTests
{
    private static TileSpec Tile(int col, int row, int colSpan, int rowSpan, WidgetType type = WidgetType.BigNumber) =>
        new() { Type = type, Col = col, Row = row, ColSpan = colSpan, RowSpan = rowSpan };

    [Fact]
    public void CellRect_Overlap_IsExclusiveOfEdges()
    {
        var a = new CellRect(0, 0, 3, 4);
        Assert.True(a.Overlaps(new CellRect(2, 3, 3, 3)));
        Assert.False(a.Overlaps(new CellRect(3, 0, 3, 4))); // touching on the right
        Assert.False(a.Overlaps(new CellRect(0, 4, 3, 4))); // touching below
    }

    [Fact]
    public void Clamp_KeepsTileInsideGridAndAboveMinimumSize()
    {
        var tile = Tile(11, -2, 5, 1);
        GridLayout.Clamp(tile);
        Assert.Equal(7, tile.Col);
        Assert.Equal(0, tile.Row);
        Assert.Equal(5, tile.ColSpan);
        Assert.Equal(TileSpec.MinimumSize(WidgetType.BigNumber).RowSpan, tile.RowSpan);
    }

    [Fact]
    public void ResolveCollisions_PushesOverlappedTilesDown_AndCascades()
    {
        var moved = Tile(0, 0, 6, 4);
        var below = Tile(0, 2, 3, 4); // overlapped by moved → pushed to row 4
        var further = Tile(0, 6, 3, 4); // overlapped by the pushed tile → row 8
        var aside = Tile(6, 0, 6, 4); // untouched
        var tiles = new List<TileSpec> { below, further, aside, moved };

        GridLayout.ResolveCollisions(tiles, moved);

        Assert.Equal(0, moved.Row);
        Assert.Equal(4, below.Row);
        Assert.Equal(8, further.Row);
        Assert.Equal(0, aside.Row);
        AssertNoOverlaps(tiles);
    }

    [Fact]
    public void FindFreeSpot_FillsGapsBeforeAddingRows()
    {
        var tiles = new List<TileSpec> { Tile(0, 0, 6, 4), Tile(9, 0, 3, 4) };
        Assert.Equal((6, 0), GridLayout.FindFreeSpot(tiles, 3, 4));
        Assert.Equal((0, 4), GridLayout.FindFreeSpot(tiles, 12, 2));
    }

    [Fact]
    public void Reflow_ToSevenColumns_KeepsSizesAndReadingOrder()
    {
        var layout = new List<CellRect>
        {
            new(0, 0, 3, 4), new(3, 0, 3, 4), new(6, 0, 3, 4), new(9, 0, 3, 4), new(0, 4, 12, 2),
        };

        var flowed = GridLayout.Reflow(layout, 7);

        Assert.Equal(new CellRect(0, 0, 3, 4), flowed[0]);
        Assert.Equal(new CellRect(3, 0, 3, 4), flowed[1]);
        Assert.Equal(new CellRect(0, 4, 3, 4), flowed[2]);
        Assert.Equal(new CellRect(3, 4, 3, 4), flowed[3]);
        Assert.Equal(new CellRect(0, 8, 7, 2), flowed[4]); // wider than the grid: clipped to 7 columns
        for (var i = 0; i < flowed.Count; i++)
        {
            for (var j = i + 1; j < flowed.Count; j++)
            {
                Assert.False(flowed[i].Overlaps(flowed[j]));
            }
        }
    }

    private static void AssertNoOverlaps(IList<TileSpec> tiles)
    {
        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                Assert.False(CellRect.Of(tiles[i]).Overlaps(CellRect.Of(tiles[j])), $"tiles {i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void Clamp_LimitsHugeRows()
    {
        var tile = Tile(0, int.MaxValue, 3, 4);
        GridLayout.Clamp(tile);
        Assert.Equal(GridLayout.MaxRow, tile.Row);
    }
}

public sealed class DashboardRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sensordeck-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTilesInTabOrder()
    {
        var repo = new DashboardRepository(_dir);
        var second = new DashboardDocument { Name = "Gaming", Order = 1 };
        var first = new DashboardDocument
        {
            Name = "Main",
            Order = 0,
            Tiles =
            [
                new TileSpec { Type = WidgetType.Chart, Col = 0, Row = 0, ColSpan = 6, RowSpan = 5, Sensors = ["/a/0", "/b/1"], WindowSeconds = 900, Warning = 80 },
            ],
        };
        repo.Save(second);
        repo.Save(first);

        var loaded = repo.LoadAll();

        Assert.Equal(new[] { "Main", "Gaming" }, loaded.Select(d => d.Name).ToArray());
        var tile = Assert.Single(loaded[0].Tiles);
        Assert.Equal(WidgetType.Chart, tile.Type);
        Assert.Equal(new[] { "/a/0", "/b/1" }, tile.Sensors.ToArray());
        Assert.Equal(900, tile.WindowSeconds);
        Assert.Equal(80, tile.Warning);
    }

    [Fact]
    public void LoadAll_SkipsCorruptFiles()
    {
        var repo = new DashboardRepository(_dir);
        repo.Save(new DashboardDocument { Name = "Good" });
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");

        Assert.Equal("Good", Assert.Single(repo.LoadAll()).Name);
    }

    [Fact]
    public void Deserialize_RepairsMissingAndOutOfRangeValues()
    {
        const string json = """{ "name": " ", "tiles": [ { "type": "Gauge", "col": 20, "colSpan": 4, "rowSpan": 0, "sensors": null, "accent": 9 }, null ] }""";

        var doc = DashboardRepository.Deserialize(json)!;

        Assert.Equal("Dashboard", doc.Name);
        var tile = Assert.Single(doc.Tiles);
        Assert.Equal(8, tile.Col);
        Assert.Empty(tile.Sensors);
        Assert.Equal(5, tile.Accent);
        Assert.True(tile.RowSpan >= TileSpec.MinimumSize(WidgetType.Gauge).RowSpan);
    }

    [Fact]
    public void Import_AssignsNewId()
    {
        var path = Path.Combine(Path.GetTempPath(), "sensordeck-export-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var doc = new DashboardDocument { Name = "Shared" };
            DashboardRepository.Export(doc, path);
            var imported = DashboardRepository.Import(path);
            Assert.Equal("Shared", imported.Name);
            Assert.NotEqual(doc.Id, imported.Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Import_RejectsNonDashboards()
    {
        var path = Path.Combine(Path.GetTempPath(), "sensordeck-bad-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "[1,2,3]");
            Assert.Throws<InvalidDataException>(() => DashboardRepository.Import(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAll_MovesFilesWithInvalidNamesToProperIds()
    {
        var repo = new DashboardRepository(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Main Copy.json"), DashboardRepository.Serialize(new DashboardDocument { Name = "Copied" }));

        var doc = Assert.Single(repo.LoadAll());

        Assert.True(DashboardRepository.IsValidId(doc.Id));
        Assert.False(File.Exists(Path.Combine(_dir, "Main Copy.json")));
        Assert.Equal("Copied", Assert.Single(repo.LoadAll()).Name);
    }

    [Theory]
    [InlineData("abc123", true)]
    [InlineData("..\\evil", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void IsValidId_RejectsPaths(string id, bool expected) => Assert.Equal(expected, DashboardRepository.IsValidId(id));

    [Fact]
    public void Clone_CopiesSectionSettings()
    {
        var tile = new TileSpec { Type = WidgetType.Section, Section = "cpu", Hardware = "lhm:/intelcpu/0", Rows = ["core_clock", "core_temp"] };
        var copy = tile.Clone();
        Assert.Equal("cpu", copy.Section);
        Assert.Equal("lhm:/intelcpu/0", copy.Hardware);
        Assert.Equal(tile.Rows, copy.Rows);
        Assert.NotSame(tile.Rows, copy.Rows);
        Assert.True(TileSpec.NeedsNoSensor(WidgetType.Section));
    }
}
