using System.Windows;
using SpaceDeck.App.ViewModels;

namespace SpaceDeck.App.Views;

/// <summary>Min/avg/max and time past limits for every sensor in one recording.</summary>
public partial class RecordingSummaryWindow : Window
{
    public RecordingSummaryWindow(RecordingSummaryViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        InitializeComponent();
        SpaceDeck.App.Services.Appearance.Attach(this);
    }
}
