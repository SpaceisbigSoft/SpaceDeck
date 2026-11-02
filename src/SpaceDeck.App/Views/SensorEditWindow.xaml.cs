using System.Windows;
using SpaceDeck.App.ViewModels;

namespace SpaceDeck.App.Views;

/// <summary>Rename, hide or scale-correct one sensor.</summary>
public partial class SensorEditWindow : Window
{
    public SensorEditWindow(SensorEditViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        InitializeComponent();
        SpaceDeck.App.Services.Appearance.Attach(this);
        model.RequestClose += () => DialogResult = true;
    }
}
