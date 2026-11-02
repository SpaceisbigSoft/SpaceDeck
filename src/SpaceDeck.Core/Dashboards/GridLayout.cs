namespace SpaceDeck.Core.Dashboards;

/// <summary>A tile's place on the grid, in cells.</summary>
public readonly record struct CellRect(int Col, int Row, int ColSpan, int RowSpan)
{
    public int Right => Col + ColSpan;

    public int Bottom => Row + RowSpan;

    public bool Overlaps(CellRect other) =>
        Col < other.Right && other.Col < Right && Row < other.Bottom && other.Row < Bottom;

    public static CellRect Of(TileSpec tile) => new(tile.Col, tile.Row, tile.ColSpan, tile.RowSpan);
}

/// <summary>
/// Pure layout rules for the dashboard grid: 12 columns, fixed-height rows growing downward, no overlaps.
/// Kept free of WPF so it can be unit-tested.
/// </summary>
public static class GridLayout
{
    public const int Columns = 12;

    /// <summary>Row height in device-independent pixels (the panel uses the same numbers).</summary>
    public const int RowHeightPx = 36;

    public const int GapPx = 12;

    public const int RowPitchPx = RowHeightPx + GapPx;

    /// <summary>Column width in device-independent pixels. Fixed, so tiles keep their size when the window grows.</summary>
    public const int ColumnWidthPx = 74;

    public const int ColumnPitchPx = ColumnWidthPx + GapPx;

    /// <summary>Width of the full 12-column grid.</summary>
    public const int FullWidthPx = (Columns * ColumnPitchPx) - GapPx;

    /// <summary>
    /// Lowest top row a tile may have. Far beyond any real dashboard; keeps <c>Row + RowSpan</c> from overflowing and the
    /// free-spot scans short when a file says <c>"row": 2147483647</c>.
    /// </summary>
    public const int MaxRow = 10_000;

    /// <summary>Keeps a tile inside the grid and at least its widget's minimum size.</summary>
    public static void Clamp(TileSpec tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var (minCols, minRows) = TileSpec.MinimumSize(tile.Type);
        tile.ColSpan = Math.Clamp(tile.ColSpan, Math.Min(minCols, Columns), Columns);
        tile.RowSpan = Math.Clamp(tile.RowSpan, minRows, 40);
        tile.Col = Math.Clamp(tile.Col, 0, Columns - tile.ColSpan);
        tile.Row = Math.Clamp(tile.Row, 0, MaxRow);
    }

    /// <summary>
    /// Makes room for <paramref name="anchor"/> (just moved or resized): every tile it now overlaps is pushed
    /// down, cascading to whatever those land on. The anchor itself never moves.
    /// </summary>
    public static void ResolveCollisions(IList<TileSpec> tiles, TileSpec anchor)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(anchor);
        Clamp(anchor);

        var placed = new List<TileSpec> { anchor };
        foreach (var tile in tiles.Where(t => !ReferenceEquals(t, anchor)).OrderBy(t => t.Row).ThenBy(t => t.Col).ToList())
        {
            while (placed.FirstOrDefault(p => CellRect.Of(p).Overlaps(CellRect.Of(tile))) is { } blocker)
            {
                tile.Row = blocker.Row + blocker.RowSpan;
            }

            placed.Add(tile);
        }
    }

    /// <summary>The first free spot for a tile of this size, scanning rows top to bottom, columns left to right.</summary>
    public static (int Col, int Row) FindFreeSpot(IEnumerable<TileSpec> tiles, int colSpan, int rowSpan)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var rects = tiles.Select(CellRect.Of).ToList();
        colSpan = Math.Clamp(colSpan, 1, Columns);
        var lastRow = rects.Count == 0 ? 0 : rects.Max(r => r.Bottom);
        for (var row = 0; row <= lastRow; row++)
        {
            for (var col = 0; col + colSpan <= Columns; col++)
            {
                var candidate = new CellRect(col, row, colSpan, rowSpan);
                if (!rects.Any(r => r.Overlaps(candidate)))
                {
                    return (col, row);
                }
            }
        }

        return (0, lastRow);
    }

    /// <summary>
    /// Re-packs tiles for a window too narrow for all 12 columns: tiles keep their size (only clipped to the
    /// available columns) and flow in reading order (top-to-bottom, left-to-right) into the first spot that fits.
    /// The saved 12-column layout is not changed.
    /// </summary>
    public static IReadOnlyList<CellRect> Reflow(IReadOnlyList<CellRect> layout, int columns)
    {
        ArgumentNullException.ThrowIfNull(layout);
        columns = Math.Max(1, columns);
        var result = new CellRect[layout.Count];
        var placed = new List<CellRect>();
        var order = Enumerable.Range(0, layout.Count).OrderBy(i => layout[i].Row).ThenBy(i => layout[i].Col);
        var cursorRow = 0;
        foreach (var i in order)
        {
            var source = layout[i];
            var colSpan = Math.Clamp(source.ColSpan, 1, columns);
            var rowSpan = Math.Max(1, source.RowSpan);

            // Never place a tile above one that came before it in reading order.
            var spot = new CellRect(0, cursorRow, colSpan, rowSpan);
            for (var row = cursorRow; ; row++)
            {
                var found = false;
                for (var col = 0; col + colSpan <= columns; col++)
                {
                    var candidate = new CellRect(col, row, colSpan, rowSpan);
                    if (!placed.Any(p => p.Overlaps(candidate)))
                    {
                        spot = candidate;
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    break;
                }
            }

            placed.Add(spot);
            result[i] = spot;
            cursorRow = spot.Row;
        }

        return result;
    }
}
