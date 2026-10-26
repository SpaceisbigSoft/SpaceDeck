using System.Windows;
using SensorDeck.App.ViewModels;

namespace SensorDeck.App.Views;

/// <summary>Rename, hide or scale-correct one sensor.</summary>
public partial class SensorEditWindow : Window
{
    public SensorEditWindow(SensorEditViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        InitializeComponent();
        SensorDeck.App.Services.Appearance.Attach(this);
        model.RequestClose += () => DialogResult = true;
    }
}
