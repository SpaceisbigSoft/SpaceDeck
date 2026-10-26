using System.ComponentModel;
using System.Windows;
using SensorDeck.App.Services;
using SensorDeck.App.ViewModels;

namespace SensorDeck.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly TrayService _tray;
    private WindowState _lastShownState = WindowState.Normal;

    public MainWindow(MainViewModel viewModel, AppSettings settings, TrayService tray)
    {
        _viewModel = viewModel;
        _settings = settings;
        _tray = tray;
        InitializeComponent();
        DataContext = viewModel;
        Appearance.Attach(this);

        // Pages that poll pause while the window is hidden in the tray (or was never shown: started with Windows).
        _viewModel.SetWindowShown(false);
        IsVisibleChanged += (_, e) => _viewModel.SetWindowShown(e.NewValue is true);
    }

    // Re-read FanControl's saved configs each time the picker opens, so new configs show up without a restart.
    private void FanProfiles_DropDownOpened(object? sender, EventArgs e) => _viewModel.RefreshFanProfiles();

    /// <summary>Ctrl+1–9 opens that dashboard tab.</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewKeyDown(e);
        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
            && e.Key is >= System.Windows.Input.Key.D1 and <= System.Windows.Input.Key.D9)
        {
            e.Handled = _viewModel.ShowDashboard(e.Key - System.Windows.Input.Key.D1);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AppLifetime.IsExiting && _settings.CloseToTray)
        {
            // Keep running in the tray (alerts keep working). Exit from the tray menu.
            e.Cancel = true;
            HideToTray();
            return;
        }

        AppLifetime.IsExiting = true;
        base.OnClosing(e);
    }

    /// <summary>Shows the window again from the tray, as it was (maximized stays maximized).</summary>
    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _lastShownState;
        }

        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized)
        {
            _lastShownState = WindowState;
        }

        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        Hide();
        if (!_settings.TrayHintShown)
        {
            _settings.TrayHintShown = true;
            _settings.Save();
            _tray.Notify("SensorDeck is still running", "It's in the tray, watching your sensors. Right-click the icon to open or exit.", critical: false);
        }
    }
}
