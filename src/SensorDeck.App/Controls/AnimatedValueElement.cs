using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SensorDeck.App.Controls;

/// <summary>
/// Base for value marks (bars, gauges): eases the drawn value toward the real one so 1 Hz updates look smooth,
/// and skips the animation when Windows "Show animations" is off.
/// </summary>
public abstract class AnimatedValueElement : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AnimatedValueElement),
        new FrameworkPropertyMetadata(double.NaN, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(AnimatedValueElement),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(AnimatedValueElement),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(AnimatedValueElement),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(AnimatedValueElement),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(AnimatedValueElement),
        new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The value currently drawn (animates toward <see cref="Value"/>).</summary>
    protected static readonly DependencyProperty DrawnValueProperty = DependencyProperty.Register(
        nameof(DrawnValue), typeof(double), typeof(AnimatedValueElement),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Duration EaseDuration = new(TimeSpan.FromMilliseconds(450));
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected double DrawnValue => (double)GetValue(DrawnValueProperty);

    /// <summary>Drawn value as a 0–1 fraction of the scale; NaN when there's no value.</summary>
    protected double Fraction
    {
        get
        {
            var v = DrawnValue;
            var span = Maximum - Minimum;
            if (double.IsNaN(v) || !double.IsFinite(span) || span <= 0)
            {
                return double.NaN;
            }

            return Math.Clamp((v - Minimum) / span, 0, 1);
        }
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var element = (AnimatedValueElement)d;
        var target = (double)e.NewValue;
        var current = element.DrawnValue;

        // Jump (no animation) for the first value, missing or infinite values (DoubleAnimation can't animate to or
        // from ±Infinity), or when the user turned animations off. Also when nobody sees it (hidden, or its window
        // minimized: a minimized window is still IsVisible), and when the mark would move less than about a pixel:
        // every animated frame re-renders the whole window, which for the layered sidebar/dashboard windows is costly.
        if (!double.IsFinite(target) || !double.IsFinite(current) || !SystemParameters.ClientAreaAnimation || !element.IsVisible
            || Window.GetWindow(element) is { WindowState: WindowState.Minimized } || MovesLessThanAPixel(element, current, target))
        {
            element.BeginAnimation(DrawnValueProperty, null);
            element.SetValue(DrawnValueProperty, target);
            return;
        }

        var animation = new DoubleAnimation(current, target, EaseDuration) { EasingFunction = Ease };
        Timeline.SetDesiredFrameRate(animation, AnimationFrameRate);
        animation.Freeze();
        element.BeginAnimation(DrawnValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Easing frames per second, instead of the display's 60–144 Hz (smooth enough for a 450 ms ease).</summary>
    private const int AnimationFrameRate = 30;

    /// <summary>True when the mark would move by less than about a pixel: not worth a 450 ms animation.</summary>
    private static bool MovesLessThanAPixel(AnimatedValueElement element, double from, double to)
    {
        var span = element.Maximum - element.Minimum;
        if (!double.IsFinite(span) || span <= 0)
        {
            return false;
        }

        // Compared on the drawn (clamped) scale, so changes beyond either end don't animate a mark that can't move.
        var moved = Math.Abs(Math.Clamp((to - element.Minimum) / span, 0, 1) - Math.Clamp((from - element.Minimum) / span, 0, 1));

        // An arc gauge draws along a curve about 2.4 × its size; 3 × leans toward animating.
        var length = 3 * Math.Max(element.ActualWidth, element.ActualHeight);
        return moved * length < 1;
    }
}
