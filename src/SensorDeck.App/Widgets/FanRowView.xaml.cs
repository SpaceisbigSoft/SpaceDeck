using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SensorDeck.App.ViewModels;

namespace SensorDeck.App.Widgets;

/// <summary>A fan or pump header row. Used on the Fans page and in the dashboard's Fans tile.</summary>
public partial class FanRowView : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(FanRow), typeof(FanRowView), new PropertyMetadata(null, OnRowChanged));

    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(
        nameof(BarWidth), typeof(GridLength), typeof(FanRowView), new PropertyMetadata(new GridLength(1, GridUnitType.Star)));

    public FanRowView()
    {
        InitializeComponent();

        // Don't keep listening to a sensor after the row leaves the screen (and resume if it comes back).
        Unloaded += (_, _) =>
        {
            if (Row is { } row)
            {
                row.Fan.PropertyChanged -= OnFanChanged;
            }
        };
        Loaded += (_, _) =>
        {
            if (Row is { } row)
            {
                row.Fan.PropertyChanged -= OnFanChanged;
                row.Fan.PropertyChanged += OnFanChanged;
                UpdateStopped();
            }
        };
    }

    public FanRow? Row
    {
        get => (FanRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public GridLength BarWidth
    {
        get => (GridLength)GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (FanRowView)d;
        if (e.OldValue is FanRow old)
        {
            old.Fan.PropertyChanged -= view.OnFanChanged;
        }

        // Only while on screen: a row set on a view that is never loaded must not keep the view alive through the
        // sensor's event (Loaded subscribes later).
        if (e.NewValue is FanRow row && view.IsLoaded)
        {
            row.Fan.PropertyChanged -= view.OnFanChanged;
            row.Fan.PropertyChanged += view.OnFanChanged;
        }

        view.UpdateStopped();
    }

    private void OnFanChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.SensorItem.Value))
        {
            UpdateStopped();
        }
    }

    // Headers reading 0 RPM (stopped or nothing connected) fade back.
    private void UpdateStopped() =>
        RowGrid.Opacity = Row is { Fan.Value: 0 } ? 0.4 : 1.0;
}
