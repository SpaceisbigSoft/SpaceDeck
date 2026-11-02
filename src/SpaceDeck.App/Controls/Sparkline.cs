using System.Windows;
using System.Windows.Media;
using SpaceDeck.App.Services;

namespace SpaceDeck.App.Controls;

/// <summary>
/// A small trend line of a sensor's recent history. Redraws when the series changes, only while on screen.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(TimeSeries), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(300, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1.25, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndDotProperty = DependencyProperty.Register(
        nameof(EndDot), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Sparkline()
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

    public TimeSeries? Series
    {
        get => (TimeSeries?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public int WindowSeconds
    {
        get => (int)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Optional dot at the newest point (e.g. the accent color).</summary>
    public Brush? EndDot
    {
        get => (Brush?)GetValue(EndDotProperty);
        set => SetValue(EndDotProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : 0, double.IsInfinity(availableSize.Height) ? 32 : 0);

    protected override void OnRender(DrawingContext dc)
    {
        var series = Series;
        var width = ActualWidth;
        var height = ActualHeight;
        if (series is null || series.Count < 2 || width <= 0 || height <= 0)
        {
            return;
        }

        // Use the full window once there's that much history; until then stretch what exists across the width
        // (right after the collector starts, a true 5-minute axis would squeeze everything into the right edge).
        var now = series.LastTime;
        var since = Math.Max(now - (WindowSeconds * 1000L), series.FirstTime);
        if (now - since < 2000)
        {
            return;
        }
        var (min, max) = series.Range(since);
        if (float.IsNaN(min))
        {
            return;
        }

        // Flat lines sit in the middle; otherwise pad the range a little so the line never touches the edges.
        // The padding grows with the magnitude: for huge readings "± 1" is below float/double precision, the range
        // would stay zero and every point would land on NaN.
        double lo = min, hi = max;
        if (hi - lo < Math.Max(1e-6, Math.Abs(hi) * 1e-9))
        {
            var flat = Math.Max(1, Math.Abs(hi) * 1e-6);
            lo -= flat;
            hi += flat;
        }

        var pad = (hi - lo) * 0.1;
        lo -= pad;
        hi += pad;
        var inset = StrokeThickness;

        double X(long t) => (t - since) / (double)(now - since) * (width - inset) + (inset / 2);
        double Y(float v) => height - inset - ((v - lo) / (hi - lo) * (height - (2 * inset)));

        var geometry = new StreamGeometry();
        Point last = default;
        var hasLast = false;
        using (var ctx = geometry.Open())
        {
            var inFigure = false;
            series.ForEach((t, v) =>
            {
                if (t < since)
                {
                    return;
                }

                if (float.IsNaN(v))
                {
                    inFigure = false; // gap
                    return;
                }

                var p = new Point(X(t), Y(v));
                if (!inFigure)
                {
                    ctx.BeginFigure(p, isFilled: false, isClosed: false);
                    inFigure = true;
                }
                else
                {
                    ctx.LineTo(p, isStroked: true, isSmoothJoin: true);
                }

                last = p;
                hasLast = true;
            });
        }

        geometry.Freeze();
        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);

        if (hasLast && EndDot is { } dot)
        {
            dc.DrawEllipse(dot, null, last, StrokeThickness + 1.5, StrokeThickness + 1.5);
        }
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sparkline = (Sparkline)d;
        sparkline.Detach(e.OldValue as TimeSeries);
        if (sparkline.IsLoaded)
        {
            sparkline.Attach(e.NewValue as TimeSeries);
        }
    }

    private void Attach(TimeSeries? series)
    {
        if (series is not null)
        {
            series.Changed -= OnDataChanged;
            series.Changed += OnDataChanged;
        }

        InvalidateVisual();
    }

    private void Detach(TimeSeries? series)
    {
        if (series is not null)
        {
            series.Changed -= OnDataChanged;
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
