using System.Windows;
using SensorDeck.App.ViewModels;

namespace SensorDeck.App.Views;

/// <summary>Min/avg/max and time past limits for every sensor in one recording.</summary>
public partial class RecordingSummaryWindow : Window
{
    public RecordingSummaryWindow(RecordingSummaryViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        InitializeComponent();
        SensorDeck.App.Services.Appearance.Attach(this);
    }
}
