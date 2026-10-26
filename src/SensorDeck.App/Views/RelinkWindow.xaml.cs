using System.Windows;
using SensorDeck.App.ViewModels;

namespace SensorDeck.App.Views;

/// <summary>Choose the sensor that replaces a missing one.</summary>
public partial class RelinkWindow : Window
{
    private readonly RelinkViewModel _model;

    public RelinkWindow(RelinkViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        DataContext = model;
        InitializeComponent();
        SensorDeck.App.Services.Appearance.Attach(this);
        model.RequestClose += () => DialogResult = true;
    }

    public string? Result => _model.Result;
}
