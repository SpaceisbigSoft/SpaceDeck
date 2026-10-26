using System.Windows;
using SensorDeck.App.ViewModels;

namespace SensorDeck.App.Views;

/// <summary>Checklist that builds a sidebar dashboard from hardware sections.</summary>
public partial class SidebarBuilderWindow : Window
{
    public SidebarBuilderWindow(SidebarBuilderViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        InitializeComponent();
        SensorDeck.App.Services.Appearance.Attach(this);
        model.RequestClose += () => DialogResult = true;
    }
}
