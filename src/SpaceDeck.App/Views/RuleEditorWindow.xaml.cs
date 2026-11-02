using System.Windows;
using SpaceDeck.App.ViewModels;

namespace SpaceDeck.App.Views;

/// <summary>Add or edit one automation rule.</summary>
public partial class RuleEditorWindow : Window
{
    public RuleEditorWindow(RuleEditorViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        InitializeComponent();
        SpaceDeck.App.Services.Appearance.Attach(this);
        model.RequestClose += () => DialogResult = true;
    }
}
