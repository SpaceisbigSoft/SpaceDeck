namespace SpaceDeck.Core.Dashboards;

/// <summary>
/// The part of the dashboard grid a screen layout uses (<see cref="DashboardDocument.Screen"/>): whole cells, as close
/// to the screen's shape as the grid allows, about one grid pixel per screen pixel. For the GA II LCD's 480 × 480 that is
/// 6 columns × 11 rows (504 × 516, within 2.5 % of square); the area is drawn scaled to fit the screen.
/// </summary>
public readonly record struct ScreenLayoutGrid(int Columns, int Rows)
{
    /// <summary>Width of the area in grid pixels (at 100 %).</summary>
    public int WidthPx => (Columns * GridLayout.ColumnPitchPx) - GridLayout.GapPx;

    /// <summary>Height of the area in grid pixels (at 100 %).</summary>
    public int HeightPx => (Rows * GridLayout.RowPitchPx) - GridLayout.GapPx;

    /// <summary>The cells, for checking where tiles are.</summary>
    public CellRect Cells => new(0, 0, Columns, Rows);

    /// <summary>The grid area for a <paramref name="width"/> × <paramref name="height"/> screen.</summary>
    public static ScreenLayoutGrid For(int width, int height)
    {
        width = Math.Clamp(width, 1, DashboardScreen.MaxSide);
        height = Math.Clamp(height, 1, DashboardScreen.MaxSide);

        // About 5 % more grid pixels than screen pixels across, so a 480-wide screen gets 6 columns (504 px).
        var columns = Math.Clamp((int)Math.Round(((width * 1.05) + GridLayout.GapPx) / GridLayout.ColumnPitchPx), 2, GridLayout.Columns);
        var areaWidth = (columns * GridLayout.ColumnPitchPx) - GridLayout.GapPx;
        var rows = Math.Clamp((int)Math.Round(((areaWidth * (double)height / width) + GridLayout.GapPx) / GridLayout.RowPitchPx), 2, 40);
        return new ScreenLayoutGrid(columns, rows);
    }

    /// <summary>The grid area for <paramref name="screen"/>.</summary>
    public static ScreenLayoutGrid For(DashboardScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return For(screen.Width, screen.Height);
    }

    /// <summary>True when every tile lies inside the area (an empty dashboard does too).</summary>
    public bool Contains(IEnumerable<TileSpec> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        // A lambda in a struct can't read 'this', so the bounds are copied first (CS1673).
        var columns = Columns;
        var rows = Rows;
        return tiles.All(t => t.Col >= 0 && t.Row >= 0 && t.Col + t.ColSpan <= columns && t.Row + t.RowSpan <= rows);
    }

    /// <summary>
    /// The size for a tile's main number on a screen layout, filling the tile: as large as its width allows for about
    /// three digits and a unit, and its height for one line under the title (and above the bar). 24–200.
    /// </summary>
    public static double FillNumberSize(int colSpan, int rowSpan)
    {
        var width = (Math.Max(1, colSpan) * GridLayout.ColumnPitchPx) - GridLayout.GapPx - 32; // the card's padding
        var height = (Math.Max(1, rowSpan) * GridLayout.RowPitchPx) - GridLayout.GapPx - 56; // title, bar, padding
        return Math.Clamp(Math.Floor(Math.Min(width / 2.4, height / 1.25)), 24, 200);
    }
}
