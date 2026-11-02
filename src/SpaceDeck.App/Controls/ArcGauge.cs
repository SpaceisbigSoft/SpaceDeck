using System.Windows;
using System.Windows.Media;

namespace SpaceDeck.App.Controls;

/// <summary>A 270° ring gauge (open at the bottom). Content such as the number is overlaid by the template that hosts it.</summary>
public sealed class ArcGauge : AnimatedValueElement
{
    private const double StartAngle = 135;
    private const double Sweep = 270;

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Math.Min(
            double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 120 : availableSize.Height);
        return new Size(size, size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;

        var trackPen = new Pen(Track, Thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        dc.DrawGeometry(null, trackPen, Arc(center, radius, StartAngle, Sweep));

        var fraction = Fraction;
        if (!double.IsNaN(fraction) && fraction > 0.001)
        {
            var valuePen = new Pen(Fill, Thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            dc.DrawGeometry(null, valuePen, Arc(center, radius, StartAngle, Sweep * fraction));
        }
    }

    private static Geometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var start = PointOn(center, radius, startDegrees);
        var end = PointOn(center, radius, startDegrees + sweepDegrees);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointOn(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }
}
