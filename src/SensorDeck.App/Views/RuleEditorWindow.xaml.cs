using System.Windows;
using SensorDeck.App.ViewModels;

namespace SensorDeck.App.Views;

/// <summary>Add or edit one automation rule.</summary>
public partial class RuleEditorWindow : Window
{
    public RuleEditorWindow(RuleEditorViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        InitializeComponent();
        SensorDeck.App.Services.Appearance.Attach(this);
        model.RequestClose += () => DialogResult = true;
    }
}
