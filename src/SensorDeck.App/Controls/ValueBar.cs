using System.Windows;
using System.Windows.Media;

namespace SensorDeck.App.Controls;

/// <summary>A thin horizontal bar: track plus a fill for the value's share of the scale.</summary>
public sealed class ValueBar : AnimatedValueElement
{
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 100 : 0, Thickness);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = Math.Min(Thickness, ActualHeight > 0 ? ActualHeight : Thickness);
        var top = (ActualHeight - height) / 2;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        dc.DrawRectangle(Track, null, new Rect(0, top, width, height));

        var fraction = Fraction;
        if (!double.IsNaN(fraction) && fraction > 0)
        {
            dc.DrawRectangle(Fill, null, new Rect(0, top, width * fraction, height));
        }
    }
}
