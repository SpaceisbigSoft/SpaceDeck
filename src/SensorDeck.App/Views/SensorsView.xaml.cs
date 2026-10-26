using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SensorDeck.App.Services;
using SensorDeck.App.ViewModels;

namespace SensorDeck.App.Views;

public partial class SensorsView : UserControl
{
    public SensorsView()
    {
        InitializeComponent();
    }

    // Double-click a row: rename / hide / correct that sensor.
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only rows (not the header or empty space).
        if (ItemsControl.ContainerFromElement(SensorTable, e.OriginalSource as DependencyObject) is not DataGridRow)
        {
            return;
        }

        if (SensorTable.SelectedItem is SensorItem sensor && DataContext is SensorsViewModel model)
        {
            if (SensorDeck.Core.Calculated.CalculatedSensor.IsCalculated(sensor.Id))
            {
                model.EditCalculatedCommand.Execute(sensor);
            }
            else
            {
                model.CustomizeCommand.Execute(sensor);
            }
            e.Handled = true;
        }
    }
}
