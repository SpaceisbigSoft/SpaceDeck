using System.Windows;
using System.Windows.Media;

namespace SensorDeck.App.Controls;

/// <summary>
/// Shows where a voltage rail sits relative to nominal: a track spanning ±<see cref="Span"/>, a band for the
/// allowed ±<see cref="Tolerance"/>, a tick at nominal and a marker at the current deviation.
/// </summary>
public sealed class ToleranceBar : FrameworkElement
{
    public static readonly DependencyProperty DeviationProperty = DependencyProperty.Register(
        nameof(Deviation), typeof(double), typeof(ToleranceBar),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToleranceProperty = DependencyProperty.Register(
        nameof(Tolerance), typeof(double), typeof(ToleranceBar),
        new FrameworkPropertyMetadata(0.05, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpanProperty = DependencyProperty.Register(
        nameof(Span), typeof(double), typeof(ToleranceBar),
        new FrameworkPropertyMetadata(0.10, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(ToleranceBar),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BandProperty = DependencyProperty.Register(
        nameof(Band), typeof(Brush), typeof(ToleranceBar),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerProperty = DependencyProperty.Register(
        nameof(Marker), typeof(Brush), typeof(ToleranceBar),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutOfToleranceProperty = DependencyProperty.Register(
        nameof(OutOfTolerance), typeof(Brush), typeof(ToleranceBar),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Signed fractional deviation from nominal (0.026 = +2.6 %); NaN hides the marker.</summary>
    public double Deviation
    {
        get => (double)GetValue(DeviationProperty);
        set => SetValue(DeviationProperty, value);
    }

    public double Tolerance
    {
        get => (double)GetValue(ToleranceProperty);
        set => SetValue(ToleranceProperty, value);
    }

    public double Span
    {
        get => (double)GetValue(SpanProperty);
        set => SetValue(SpanProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public Brush Band
    {
        get => (Brush)GetValue(BandProperty);
        set => SetValue(BandProperty, value);
    }

    public Brush Marker
    {
        get => (Brush)GetValue(MarkerProperty);
        set => SetValue(MarkerProperty, value);
    }

    public Brush OutOfTolerance
    {
        get => (Brush)GetValue(OutOfToleranceProperty);
        set => SetValue(OutOfToleranceProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : 0, 12);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var span = Span;
        if (width <= 0 || height <= 0 || !double.IsFinite(span) || span <= 0)
        {
            return;
        }

        // A negative or NaN tolerance would make the band's width negative, and Rect throws on that.
        var tolerance = double.IsFinite(Tolerance) ? Math.Abs(Tolerance) : 0;
        var mid = height / 2;
        double X(double deviation) => (Math.Clamp(deviation, -span, span) + span) / (2 * span) * width;

        dc.DrawRectangle(Track, null, new Rect(0, mid - 1, width, 2));
        var bandLeft = X(-tolerance);
        dc.DrawRectangle(Band, null, new Rect(bandLeft, mid - 3, X(tolerance) - bandLeft, 6));
        dc.DrawRectangle(Marker, null, new Rect(X(0) - 0.5, mid - 4, 1, 8));

        var deviation = Deviation;
        if (!double.IsNaN(deviation))
        {
            var brush = Math.Abs(deviation) > tolerance ? OutOfTolerance : Marker;
            dc.DrawRectangle(brush, null, new Rect(X(deviation) - 1.5, 0, 3, height));
        }
    }
}
