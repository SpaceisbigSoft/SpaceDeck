using System.Windows;
using SensorDeck.App.ViewModels;

namespace SensorDeck.App.Views;

/// <summary>Add or edit a calculated sensor.</summary>
public partial class CalculatedSensorWindow : Window
{
    public CalculatedSensorWindow(CalculatedSensorViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        InitializeComponent();
        SensorDeck.App.Services.Appearance.Attach(this);
        model.RequestClose += () => DialogResult = true;
    }
}
