using System.Windows;
using System.Windows.Controls;
using SpaceDeck.App.Services;

namespace SpaceDeck.App.Widgets;

/// <summary>A voltage rail row. Used on the Voltages page and in the dashboard's Rails tile.</summary>
public partial class RailRowView : UserControl
{
    public static readonly DependencyProperty SensorProperty = DependencyProperty.Register(
        nameof(Sensor), typeof(SensorItem), typeof(RailRowView), new PropertyMetadata(null));

    public static readonly DependencyProperty BandWidthProperty = DependencyProperty.Register(
        nameof(BandWidth), typeof(GridLength), typeof(RailRowView), new PropertyMetadata(new GridLength(110)));

    public RailRowView()
    {
        InitializeComponent();
    }

    public SensorItem? Sensor
    {
        get => (SensorItem?)GetValue(SensorProperty);
        set => SetValue(SensorProperty, value);
    }

    /// <summary>Width of the tolerance band column (0 hides it in tight layouts).</summary>
    public GridLength BandWidth
    {
        get => (GridLength)GetValue(BandWidthProperty);
        set => SetValue(BandWidthProperty, value);
    }
}
