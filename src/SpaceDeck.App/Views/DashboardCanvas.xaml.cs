using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpaceDeck.App.Controls;
using SpaceDeck.App.Services;
using SpaceDeck.App.ViewModels;
using SpaceDeck.Core.Dashboards;

namespace SpaceDeck.App.Views;

/// <summary>Picks a tile's DataTemplate ("Tile.&lt;WidgetType&gt;", or "Tile.Missing") from the canvas resources.</summary>
public sealed class TileTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is TileViewModel tile && container is FrameworkElement element
            ? element.TryFindResource(tile.IsMissing ? "Tile.Missing" : $"Tile.{tile.Type}") as DataTemplate
            : null;
}

/// <summary>Visible when some (not all) of a tile's sensors are missing, outside edit mode.</summary>
public sealed class PartialMissingConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        values is [int missing, bool isMissing, bool waiting, bool editing] && missing > 0 && !isMissing && !waiting && !editing
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows one dashboard (DataContext: <see cref="DashboardDocumentViewModel"/>) and handles dragging and resizing
/// tiles in edit mode. Positions snap to grid cells; the drop is committed (with collision push-down) on mouse-up.
/// </summary>
public partial class DashboardCanvas : UserControl
{
    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable), typeof(bool), typeof(DashboardCanvas), new PropertyMetadata(true, OnEditStateChanged));

    private static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        "IsEditing", typeof(bool), typeof(DashboardCanvas), new PropertyMetadata(false, OnEditStateChanged));

    private static readonly DependencyPropertyKey EditModeKey = DependencyProperty.RegisterReadOnly(
        nameof(EditMode), typeof(bool), typeof(DashboardCanvas), new PropertyMetadata(false));

    public static readonly DependencyProperty EditModeProperty = EditModeKey.DependencyProperty;

    /// <summary>Right-clicking a section row offers "Hide …" (off where the host window builds its own menu, like the sidebar).</summary>
    public static readonly DependencyProperty RowMenusProperty = DependencyProperty.Register(
        nameof(RowMenus), typeof(bool), typeof(DashboardCanvas), new PropertyMetadata(true));

    /// <summary>Stack tiles in one column, each as tall as needed (the sidebar). Ignored while editing.</summary>
    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(DashboardCanvas), new PropertyMetadata(false, (d, _) => ((DashboardCanvas)d).ApplyEditMode()));

    private const int EditExtraRows = 6;
    private const double AutoScrollMargin = 36;
    private const double AutoScrollStep = 14;

    /// <summary>Wait for bursts (catalog rebuilds, monitor changes, several rows hidden) to settle before fitting again.</summary>
    private static readonly TimeSpan FitDelay = TimeSpan.FromMilliseconds(200);

    private readonly DispatcherTimer _fitTimer;
    private bool _fitToHeight;
    private double _manualScale = 1;
    private double _scale = 1;

    private DashboardGridPanel? _panel;
    private DragKind _drag;
    private TileViewModel? _tile;
    private FrameworkElement? _container;
    private UIElement? _captured;
    private Point _start;
    private CellRect _origin;
    private CellRect _target;

    public DashboardCanvas()
    {
        InitializeComponent();
        SetBinding(IsEditingProperty, new Binding(nameof(DashboardDocumentViewModel.IsEditing)) { FallbackValue = false });

        // Elements inside tiles (focus changes, inner lists, layout updates) ask the page to scroll them into view,
        // which pushed the top rows out of sight on smaller windows. Only the user (or drag auto-scroll) scrolls the page.
        Surface.AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) => e.Handled = true));

        // A different dashboard starts at the top.
        DataContextChanged += (_, _) => Scroller.ScrollToTop();
        ContextMenuOpening += OnContextMenuOpening;

        // Fit to height: re-fit when the room changes (window resize, monitor/DPI/taskbar, global UI size) or the
        // content's height does (rows hidden/shown, sections edited, tiles or catalog changed, skin fonts). Value updates
        // don't change heights, and a re-fit after our own scale change measures the same unscaled height again, so it
        // settles at once (see Fit).
        _fitTimer = new DispatcherTimer { Interval = FitDelay };
        _fitTimer.Tick += (_, _) =>
        {
            _fitTimer.Stop();
            Fit();
        };
        SizeChanged += (_, _) => ScheduleFit();
        Surface.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged)
            {
                ScheduleFit();
            }
        };
        Loaded += (_, _) => ScheduleFit();
        Unloaded += (_, _) => _fitTimer.Stop();
    }

    private enum DragKind
    {
        None,
        Move,
        Resize,
    }

    /// <summary>False for read-only views (the full-screen window): no edit chrome even while the page is editing.</summary>
    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public bool RowMenus
    {
        get => (bool)GetValue(RowMenusProperty);
        set => SetValue(RowMenusProperty, value);
    }

    public bool Stacked
    {
        get => (bool)GetValue(StackedProperty);
        set => SetValue(StackedProperty, value);
    }

    /// <summary>True when tiles show their edit chrome and can be dragged.</summary>
    public bool EditMode => (bool)GetValue(EditModeProperty);

    /// <summary>The content size in use now (the fitted one while fitting); 1 = 100 %.</summary>
    public double EffectiveScale => _scale;

    /// <summary>
    /// Sets the per-window content size (<see cref="ContentSize"/>): fit to height, or a manual 70–150 %. Off (the
    /// default, used by the main window) leaves the content at 100 %.
    /// </summary>
    public void SetContentSize(bool fitToHeight, double manualScale)
    {
        _fitToHeight = fitToHeight;
        _manualScale = ContentSize.Clamp(manualScale);
        if (fitToHeight)
        {
            Fit();
        }
        else
        {
            _fitTimer.Stop();
            ApplyScale(_manualScale, overflow: false);
        }
    }

    private void ScheduleFit()
    {
        if (!_fitToHeight)
        {
            return;
        }

        _fitTimer.Stop();
        _fitTimer.Start();
    }

    /// <summary>
    /// Scale = min(1, room ÷ content height at 100 %), floored at <see cref="ContentSize.Minimum"/> (then it scrolls and
    /// a hint shows). The content is measured unscaled — at the unscaled width with unlimited height — so the result
    /// depends only on the content and the room, never on the scale currently applied: no layout loop.
    /// </summary>
    private void Fit()
    {
        if (!_fitToHeight || !IsLoaded)
        {
            return;
        }

        var margin = ScaleHost.Margin;
        var width = Scroller.ActualWidth - margin.Left - margin.Right;

        // The whole control (hint row included), so showing the hint doesn't change the answer.
        var room = ActualHeight - margin.Top - margin.Bottom;
        if (width <= 0 || room <= 0)
        {
            return; // not laid out yet, or hidden; Loaded/SizeChanged fit again later
        }

        Surface.Measure(new Size(width, double.PositiveInfinity));
        var content = Surface.DesiredSize.Height;

        // That probe left Surface measured for the probe size; re-measure it for real in the next layout pass.
        ScaleHost.InvalidateMeasure();

        if (content <= 0 || double.IsNaN(content))
        {
            ApplyScale(1, overflow: false);
            return;
        }

        // A pixel spare for layout rounding (or the scrollbar shows); half-percent steps so tiny height changes don't
        // re-layout the whole page.
        var fit = Math.Floor((room - 1) / content * 200) / 200;
        ApplyScale(Math.Clamp(fit, ContentSize.Minimum, 1), overflow: fit < ContentSize.Minimum);
    }

    private void ApplyScale(double scale, bool overflow)
    {
        if (Math.Abs(scale - _scale) > 0.001)
        {
            _scale = scale;
            ScaleHost.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        }

        FitHint.Text = Stacked
            ? "Doesn't fit — hide some rows (right-click a row)"
            : "Doesn't fit — scroll for the rest, or make the window taller";
        FitHint.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
    }

    private DashboardDocumentViewModel? Document => DataContext as DashboardDocumentViewModel;

    private static void OnEditStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (DashboardCanvas)d;
        canvas.SetValue(EditModeKey, canvas.IsEditable && (bool)canvas.GetValue(IsEditingProperty));
        canvas.ApplyEditMode();
    }

    /// <summary>Right-click on a section row: hide it / show hidden rows / edit (the window's own menu shows elsewhere).</summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!RowMenus || EditMode || DataContext is not DashboardDocumentViewModel dashboard)
        {
            return;
        }

        var (tile, row) = SectionRowMenu.Find(e.OriginalSource);
        if (tile is null || tile.Type != WidgetType.Section)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = this };
        foreach (var item in SectionRowMenu.Items(dashboard, tile, row))
        {
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        _panel = (DashboardGridPanel)sender;
        ApplyEditMode();
    }

    private void ApplyEditMode()
    {
        if (!EditMode)
        {
            EndDrag(commit: false);
        }

        if (_panel is not null)
        {
            // While editing, show the true 12-column layout (no reflow) with spare rows to drag into.
            _panel.Reflow = !EditMode;
            _panel.ExtraRows = EditMode ? EditExtraRows : 0;
            _panel.Stacked = Stacked && !EditMode;
        }
    }

    private void Move_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is TileViewModel tile)
        {
            EndDrag(commit: false);
            Document?.EditTileCommand.Execute(tile);
            e.Handled = true;
            return;
        }

        BeginDrag(DragKind.Move, sender, e);
    }

    private void Resize_MouseDown(object sender, MouseButtonEventArgs e) => BeginDrag(DragKind.Resize, sender, e);

    private void BeginDrag(DragKind kind, object sender, MouseButtonEventArgs e)
    {
        if (!EditMode || _panel is null || _drag != DragKind.None
            || sender is not FrameworkElement element || element.DataContext is not TileViewModel tile)
        {
            return;
        }

        _drag = kind;
        _tile = tile;
        _container = TilesHost.ItemContainerGenerator.ContainerFromItem(tile) as FrameworkElement;
        _start = e.GetPosition(_panel);
        _origin = tile.Cells;
        _target = _origin;
        _captured = element;
        if (_container is not null)
        {
            Panel.SetZIndex(_container, 10);
        }

        ShowGhost(_target);
        element.CaptureMouse();
        e.Handled = true;
    }

    private void Drag_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragKind.None || _panel is null || _tile is null)
        {
            return;
        }

        AutoScroll(e);
        var position = e.GetPosition(_panel);
        var dx = position.X - _start.X;
        var dy = position.Y - _start.Y;
        var columns = GridLayout.Columns;
        var colSteps = (int)Math.Round(dx / _panel.ColumnPitch);
        var rowSteps = (int)Math.Round(dy / DashboardGridPanel.RowPitch);

        if (_drag == DragKind.Move)
        {
            if (_container is not null)
            {
                _container.RenderTransform = new TranslateTransform(dx, dy);
            }

            _target = _origin with
            {
                Col = Math.Clamp(_origin.Col + colSteps, 0, columns - _origin.ColSpan),
                Row = Math.Max(0, _origin.Row + rowSteps),
            };
        }
        else
        {
            var (minCols, minRows) = TileSpec.MinimumSize(_tile.Type);
            _target = _origin with
            {
                ColSpan = Math.Clamp(_origin.ColSpan + colSteps, Math.Min(minCols, columns - _origin.Col), columns - _origin.Col),
                RowSpan = Math.Clamp(_origin.RowSpan + rowSteps, minRows, 40),
            };

            // Resize live so the content reflows as you drag.
            _tile.ColSpan = _target.ColSpan;
            _tile.RowSpan = _target.RowSpan;
        }

        ShowGhost(_target);
    }

    private void Drag_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag != DragKind.None)
        {
            EndDrag(commit: true);
            e.Handled = true;
        }
    }

    private void Drag_LostCapture(object sender, MouseEventArgs e)
    {
        // Capture lost without a mouse-up (Alt+Tab, a dialog popping up): cancel.
        if (_drag != DragKind.None && ReferenceEquals(sender, _captured))
        {
            EndDrag(commit: false);
        }
    }

    private void EndDrag(bool commit)
    {
        if (_drag == DragKind.None)
        {
            return;
        }

        var tile = _tile;
        var target = _target;
        var changed = target != _origin;
        _drag = DragKind.None;
        _tile = null;

        if (_container is not null)
        {
            _container.RenderTransform = null;
            Panel.SetZIndex(_container, 0);
            _container = null;
        }

        Ghost.Visibility = Visibility.Collapsed;
        var captured = _captured;
        _captured = null;
        captured?.ReleaseMouseCapture();

        if (tile is null)
        {
            return;
        }

        if (commit && changed && Document is { } doc)
        {
            doc.CommitLayout(tile, target);
        }
        else
        {
            tile.LoadPosition(); // undo a live resize
        }
    }

    private void ShowGhost(CellRect cells)
    {
        if (_panel is null)
        {
            return;
        }

        var bounds = _panel.CellBounds(cells);
        var topLeft = _panel.TranslatePoint(bounds.TopLeft, GhostLayer);
        Canvas.SetLeft(Ghost, topLeft.X);
        Canvas.SetTop(Ghost, topLeft.Y);
        Ghost.Width = bounds.Width;
        Ghost.Height = bounds.Height;
        Ghost.Visibility = Visibility.Visible;
    }

    private void AutoScroll(MouseEventArgs e)
    {
        var y = e.GetPosition(Scroller).Y;
        if (y > Scroller.ActualHeight - AutoScrollMargin)
        {
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + AutoScrollStep);
        }
        else if (y < AutoScrollMargin)
        {
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - AutoScrollStep);
        }
    }
}
