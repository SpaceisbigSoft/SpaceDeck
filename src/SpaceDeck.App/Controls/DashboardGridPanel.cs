using System.Windows;
using System.Windows.Controls;
using SpaceDeck.Core.Dashboards;

namespace SpaceDeck.App.Controls;

/// <summary>
/// Lays tiles out on a 12-column grid of fixed-size cells (see <see cref="GridLayout"/>), so tiles keep their size
/// whatever the window width; extra width stays empty. Each child says where it goes with the attached
/// Col/Row/ColSpan/RowSpan properties. When the window is too narrow for 12 columns: with <see cref="Reflow"/> on
/// (viewing), tiles keep their size and wrap into as many columns as fit; with it off (editing), the grid shrinks to fit.
/// </summary>
public sealed class DashboardGridPanel : Panel
{
    public static readonly DependencyProperty ColProperty = DependencyProperty.RegisterAttached(
        "Col", typeof(int), typeof(DashboardGridPanel), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty RowProperty = DependencyProperty.RegisterAttached(
        "Row", typeof(int), typeof(DashboardGridPanel), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty ColSpanProperty = DependencyProperty.RegisterAttached(
        "ColSpan", typeof(int), typeof(DashboardGridPanel), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty RowSpanProperty = DependencyProperty.RegisterAttached(
        "RowSpan", typeof(int), typeof(DashboardGridPanel), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty ReflowProperty = DependencyProperty.Register(
        nameof(Reflow), typeof(bool), typeof(DashboardGridPanel), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Empty rows added below the last tile (room to drag into while editing).</summary>
    public static readonly DependencyProperty ExtraRowsProperty = DependencyProperty.Register(
        nameof(ExtraRows), typeof(int), typeof(DashboardGridPanel), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Stacked layout (the sidebar): one column, tiles in reading order, no grid snapping.</summary>
    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(DashboardGridPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>In the stacked layout, the tile takes its content's height instead of its row span.</summary>
    public static readonly DependencyProperty FitsContentProperty = DependencyProperty.RegisterAttached(
        "FitsContent", typeof(bool), typeof(DashboardGridPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    private CellRect[] _cells = [];
    private Rect[] _stacked = [];
    private int _columns = GridLayout.Columns;
    private double _pitch = GridLayout.ColumnPitchPx;

    public bool Reflow
    {
        get => (bool)GetValue(ReflowProperty);
        set => SetValue(ReflowProperty, value);
    }

    public bool Stacked
    {
        get => (bool)GetValue(StackedProperty);
        set => SetValue(StackedProperty, value);
    }

    public static bool GetFitsContent(DependencyObject element) => (bool)element.GetValue(FitsContentProperty);

    public static void SetFitsContent(DependencyObject element, bool value) => element.SetValue(FitsContentProperty, value);

    public int ExtraRows
    {
        get => (int)GetValue(ExtraRowsProperty);
        set => SetValue(ExtraRowsProperty, value);
    }

    /// <summary>Horizontal distance from one column's left edge to the next.</summary>
    public double ColumnPitch => _pitch;

    public static double RowPitch => GridLayout.RowPitchPx;

    public static int GetCol(DependencyObject element) => (int)element.GetValue(ColProperty);

    public static void SetCol(DependencyObject element, int value) => element.SetValue(ColProperty, value);

    public static int GetRow(DependencyObject element) => (int)element.GetValue(RowProperty);

    public static void SetRow(DependencyObject element, int value) => element.SetValue(RowProperty, value);

    public static int GetColSpan(DependencyObject element) => (int)element.GetValue(ColSpanProperty);

    public static void SetColSpan(DependencyObject element, int value) => element.SetValue(ColSpanProperty, value);

    public static int GetRowSpan(DependencyObject element) => (int)element.GetValue(RowSpanProperty);

    public static void SetRowSpan(DependencyObject element, int value) => element.SetValue(RowSpanProperty, value);

    /// <summary>Pixel rectangle of a cell range, in panel coordinates.</summary>
    public Rect CellBounds(CellRect cells)
    {
        var pitch = ColumnPitch;
        return new Rect(
            cells.Col * pitch,
            cells.Row * RowPitch,
            Math.Max(0, (cells.ColSpan * pitch) - GridLayout.GapPx),
            Math.Max(0, (cells.RowSpan * RowPitch) - GridLayout.GapPx));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Stacked)
        {
            return MeasureStacked(availableSize);
        }

        var width = double.IsInfinity(availableSize.Width) ? GridLayout.FullWidthPx : availableSize.Width;
        if (width >= GridLayout.FullWidthPx)
        {
            _columns = GridLayout.Columns;
            _pitch = GridLayout.ColumnPitchPx;
        }
        else if (!Reflow)
        {
            // Editing in a narrow window: keep all 12 columns visible, smaller.
            _columns = GridLayout.Columns;
            _pitch = Math.Max(8, (width + GridLayout.GapPx) / GridLayout.Columns);
        }
        else
        {
            // Viewing in a narrow window: same size tiles, fewer columns.
            _columns = Math.Clamp((int)((width + GridLayout.GapPx) / GridLayout.ColumnPitchPx), 1, GridLayout.Columns);
            _pitch = GridLayout.ColumnPitchPx;
        }

        var children = InternalChildren;
        var layout = new CellRect[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            layout[i] = new CellRect(GetCol(child), GetRow(child), Math.Max(1, GetColSpan(child)), Math.Max(1, GetRowSpan(child)));
        }

        _cells = _columns == GridLayout.Columns ? layout : [.. GridLayout.Reflow(layout, _columns)];

        var rows = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var bounds = CellBounds(_cells[i]);
            children[i].Measure(bounds.Size);
            rows = Math.Max(rows, _cells[i].Bottom);
        }

        rows += ExtraRows;
        return new Size(Math.Min(width, (_columns * _pitch) - GridLayout.GapPx), rows == 0 ? 0 : (rows * RowPitch) - GridLayout.GapPx);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        if (Stacked)
        {
            for (var i = 0; i < children.Count && i < _stacked.Length; i++)
            {
                children[i].Arrange(_stacked[i]);
            }

            return finalSize;
        }

        for (var i = 0; i < children.Count && i < _cells.Length; i++)
        {
            children[i].Arrange(CellBounds(_cells[i]));
        }

        return finalSize;
    }

    /// <summary>
    /// One column the full width, tiles top to bottom in their dashboard order (row, then column), a gap apart.
    /// Content-sized tiles (sections) take exactly their height; the rest keep their row-span height.
    /// </summary>
    private Size MeasureStacked(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? (3 * GridLayout.ColumnPitchPx) - GridLayout.GapPx : availableSize.Width;
        var children = InternalChildren;
        var order = Enumerable.Range(0, children.Count)
            .OrderBy(i => GetRow(children[i]))
            .ThenBy(i => GetCol(children[i]))
            .ToList();

        _stacked = new Rect[children.Count];
        var y = 0.0;
        foreach (var i in order)
        {
            var child = children[i];
            double height;
            if (GetFitsContent(child) || (child as FrameworkElement)?.DataContext is SpaceDeck.App.ViewModels.TileViewModel { FitsContent: true })
            {
                child.Measure(new Size(width, double.PositiveInfinity));
                height = Math.Ceiling(child.DesiredSize.Height);
            }
            else
            {
                height = Math.Max(0, (Math.Max(1, GetRowSpan(child)) * RowPitch) - GridLayout.GapPx);
                child.Measure(new Size(width, height));
            }

            _stacked[i] = new Rect(0, y, width, height);
            y += height + GridLayout.GapPx;
        }

        return new Size(width, Math.Max(0, y - GridLayout.GapPx));
    }
}
