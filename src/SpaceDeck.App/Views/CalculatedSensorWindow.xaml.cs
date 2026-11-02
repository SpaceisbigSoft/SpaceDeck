using System.Windows;
using SpaceDeck.App.ViewModels;

namespace SpaceDeck.App.Views;

/// <summary>Add or edit a calculated sensor.</summary>
public partial class CalculatedSensorWindow : Window
{
    public CalculatedSensorWindow(CalculatedSensorViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        InitializeComponent();
        SpaceDeck.App.Services.Appearance.Attach(this);
        model.RequestClose += () => DialogResult = true;
    }
}
