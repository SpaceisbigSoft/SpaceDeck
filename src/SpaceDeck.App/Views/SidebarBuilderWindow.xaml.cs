using System.Windows;
using SpaceDeck.App.ViewModels;

namespace SpaceDeck.App.Views;

/// <summary>Checklist that builds a sidebar dashboard from hardware sections.</summary>
public partial class SidebarBuilderWindow : Window
{
    public SidebarBuilderWindow(SidebarBuilderViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        InitializeComponent();
        SpaceDeck.App.Services.Appearance.Attach(this);
        model.RequestClose += () => DialogResult = true;
    }
}
