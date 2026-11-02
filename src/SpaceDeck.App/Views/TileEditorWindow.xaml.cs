using System.Windows;
using SpaceDeck.App.ViewModels;
using SpaceDeck.Core.Dashboards;

namespace SpaceDeck.App.Views;

/// <summary>Add a tile, or change one: widget, sensors and settings.</summary>
public partial class TileEditorWindow : Window
{
    private readonly TileEditorViewModel _model;

    public TileEditorWindow(TileEditorViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        DataContext = model;
        InitializeComponent();
        SpaceDeck.App.Services.Appearance.Attach(this);
        model.RequestClose += OnRequestClose;
        Closed += (_, _) => model.RequestClose -= OnRequestClose;
    }

    public TileSpec? Result => _model.Result;

    private void OnRequestClose() => DialogResult = true;
}
