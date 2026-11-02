using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.Controls;

/// <summary>
/// One line on a <see cref="HistoryChart"/>. <c>Series</c> holds base-unit values (°C);
/// <c>Kind</c> and <c>Display</c> let the chart show temperatures in °F when the app does.
/// </summary>
public sealed record ChartSeries(string Label, TimeSeries Series, Brush Brush, SensorKind Kind = SensorKind.Other, DisplayOptions? Display = null);

/// <summary>
/// A time-series chart for one or more sensors over the last N seconds: hairline grid, Y labels on the left,
/// relative-time labels along the bottom. Custom-drawn (no charting library) so it matches the skin exactly.
/// </summary>
public sealed class HistoryChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(HistoryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(int), typeof(HistoryChart),
        new FrameworkPropertyMetadata(300, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelFontProperty = DependencyProperty.Register(
        nameof(LabelFont), typeof(FontFamily), typeof(HistoryChart),
        new FrameworkPropertyMetadata(new FontFamily("Consolas"), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineThicknessProperty = DependencyProperty.Register(
        nameof(LineThickness), typeof(double), typeof(HistoryChart),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double LabelSize = 11;
    private const double LeftAxis = 48;
    private const double BottomAxis = 20;

    public HistoryChart()
    {
        // Low-resource: don't redraw while hidden (in the tray, hidden overlay, another page); catch up when shown.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _stale)
            {
                _stale = false;
                InvalidateVisual();
            }
        };
        Loaded += (_, _) => Attach(Series);
        Unloaded += (_, _) => Detach(Series);
    }

    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public int WindowSeconds
    {
        get => (int)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public FontFamily LabelFont
    {
        get => (FontFamily)GetValue(LabelFontProperty);
        set => SetValue(LabelFontProperty, value);
    }

    public double LineThickness
    {
        get => (double)GetValue(LineThicknessProperty);
        set => SetValue(LineThicknessProperty, value);
    }

    /// <summary>A value in the unit the app shows (temperatures follow the °C/°F setting).</summary>
    private static double Shown(ChartSeries s, float v) =>
        s.Display?.TemperatureUnit != TemperatureUnit.Fahrenheit ? v
        : s.Kind == SensorKind.Temperature ? (v * 9.0 / 5.0) + 32
        : s.Kind == SensorKind.TemperatureDifference ? v * 9.0 / 5.0
        : v;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : 0, double.IsInfinity(availableSize.Height) ? 200 : 0);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var plot = new Rect(LeftAxis, 6, Math.Max(0, width - LeftAxis - 8), Math.Max(0, height - BottomAxis - 6));
        if (plot.Width < 20 || plot.Height < 20)
        {
            return;
        }

        // Transparent fill so the whole area is hit-testable (tooltips later).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var series = Series ?? [];
        var now = series.Select(s => s.Series.LastTime).DefaultIfEmpty(0).Max();
        if (now == 0)
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        var since = now - (Math.Max(1, WindowSeconds) * 1000L); // a zero window would divide by zero in X()

        // Shared Y range across all series.
        var lo = double.NaN;
        var hi = double.NaN;
        foreach (var s in series)
        {
            var (min, max) = s.Series.Range(since);
            if (!float.IsNaN(min))
            {
                var a = Shown(s, min); // monotonic, so min and max stay ordered
                var b = Shown(s, max);
                lo = double.IsNaN(lo) ? a : Math.Min(lo, a);
                hi = double.IsNaN(hi) ? b : Math.Max(hi, b);
            }
        }

        if (double.IsNaN(lo))
        {
            lo = 0;
            hi = 1;
        }

        var (ticks, axisLo, axisHi) = NiceTicks(lo, hi, 4);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(LabelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();

        double Y(double v) => plot.Bottom - ((v - axisLo) / (axisHi - axisLo) * plot.Height);
        double X(long t) => plot.Left + ((t - since) / (double)(now - since) * plot.Width);

        // Horizontal grid + Y labels.
        foreach (var tick in ticks)
        {
            var y = Math.Round(Y(tick)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = Text(FormatTick(tick), typeface, dpi);
            dc.DrawText(text, new Point(plot.Left - text.Width - 8, y - (text.Height / 2)));
        }

        // Relative-time labels: 4 divisions.
        for (var i = 0; i <= 4; i++)
        {
            var secondsAgo = WindowSeconds * (4 - i) / 4;
            var x = plot.Left + (plot.Width * i / 4);
            var label = secondsAgo == 0 ? "now" : FormatAgo(secondsAgo);
            var text = Text(label, typeface, dpi);
            // Max/Min rather than Clamp: Clamp throws if a label is wider than a very narrow plot.
            var tx = Math.Max(plot.Left, Math.Min(x - (text.Width / 2), plot.Right - text.Width));
            dc.DrawText(text, new Point(tx, plot.Bottom + 5));
        }

        // Lines.
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4)));
        foreach (var s in series)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var inFigure = false;
                s.Series.ForEach((t, v) =>
                {
                    if (t < since)
                    {
                        return;
                    }

                    if (float.IsNaN(v))
                    {
                        inFigure = false;
                        return;
                    }

                    var p = new Point(X(t), Y(Shown(s, v)));
                    if (!inFigure)
                    {
                        ctx.BeginFigure(p, isFilled: false, isClosed: false);
                        inFigure = true;
                    }
                    else
                    {
                        ctx.LineTo(p, isStroked: true, isSmoothJoin: true);
                    }
                });
            }

            geometry.Freeze();
            var pen = new Pen(s.Brush, LineThickness) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        dc.Pop();
    }

    private FormattedText Text(string text, Typeface typeface, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, LabelSize, LabelBrush, dpi);

    private static string FormatTick(double value) =>
        Math.Abs(value) >= 1000 ? value.ToString("N0", CultureInfo.CurrentCulture)
        : Math.Abs(value) >= 10 ? value.ToString("0.#", CultureInfo.CurrentCulture)
        : value.ToString("0.##", CultureInfo.CurrentCulture);

    private static string FormatAgo(int seconds) => seconds switch
    {
        >= 86_400 => $"−{seconds / 86_400.0:0.#}d",
        >= 3600 => $"−{seconds / 3600.0:0.#}h",
        >= 60 when seconds % 60 == 0 => $"−{seconds / 60}m",
        >= 60 => $"−{seconds / 60.0:0.#}m",
        _ => $"−{seconds}s",
    };

    /// <summary>Evenly spaced "nice" ticks (1/2/2.5/5 × 10^n) covering [lo, hi].</summary>
    internal static (double[] Ticks, double Lo, double Hi) NiceTicks(double lo, double hi, int count)
    {
        if (!double.IsFinite(lo) || !double.IsFinite(hi))
        {
            lo = 0;
            hi = 1;
        }

        // A flat (or, for huge values, nearly flat) range gets room around it. The padding grows with the magnitude
        // so the step stays representable: at 1e20, "± 1" is below double precision and the range would stay empty.
        if (hi - lo < Math.Max(1e-9, Math.Abs(hi) * 1e-9))
        {
            var pad = Math.Max(1, Math.Abs(hi) * 1e-6);
            lo -= pad;
            hi += pad;
        }

        var step = SensorItem.NiceCeiling((hi - lo) / Math.Max(1, count));
        var start = Math.Floor(lo / step) * step;
        var end = Math.Ceiling(hi / step) * step;
        var ticks = new List<double>();

        // Index-based (not "v += step") so the loop always ends, even if the step is tiny next to the values.
        for (var k = 0; k <= MaxTicks; k++)
        {
            var v = start + (k * step);
            if (v > end + (step / 2))
            {
                break;
            }

            ticks.Add(Math.Round(v, 10));
        }

        double[] result = [.. ticks];
        return end > start ? (result, start, end) : (result, lo, hi);
    }

    /// <summary>Upper bound on Y ticks; nice steps give count + 2 at most, this only guards against bad input.</summary>
    private const int MaxTicks = 64;

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (HistoryChart)d;
        chart.Detach(e.OldValue as IReadOnlyList<ChartSeries>);
        if (chart.IsLoaded)
        {
            chart.Attach(e.NewValue as IReadOnlyList<ChartSeries>);
        }
    }

    private void Attach(IReadOnlyList<ChartSeries>? series)
    {
        foreach (var s in series ?? [])
        {
            s.Series.Changed -= OnDataChanged;
            s.Series.Changed += OnDataChanged;
        }

        InvalidateVisual();
    }

    private void Detach(IReadOnlyList<ChartSeries>? series)
    {
        foreach (var s in series ?? [])
        {
            s.Series.Changed -= OnDataChanged;
        }
    }

    private bool _stale;

    private void OnDataChanged()
    {
        if (IsVisible)
        {
            InvalidateVisual();
        }
        else
        {
            _stale = true;
        }
    }
}
