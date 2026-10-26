using System.IO;
using System.Windows;
using Microsoft.Win32;
using SensorDeck.App.ViewModels;
using SensorDeck.App.Views;
using SensorDeck.Core.Dashboards;

namespace SensorDeck.App.Services;

/// <summary>Opens the app's dialogs and windows so view models don't touch WPF windows directly.</summary>
public sealed class DialogService
{
    private readonly SensorStore _store;
    private readonly AppSettings _settings;
    private readonly List<DashboardWindow> _dashboardWindows = [];

    /// <summary>Every dashboard (set by the dashboard page) — dashboard windows can switch between them.</summary>
    public Func<IReadOnlyList<DashboardDocumentViewModel>> AllDashboards { get; set; } = () => [];

    /// <summary>The dashboard selected on the dashboard page (opened by the overlay hotkey when no window is open).</summary>
    public Func<DashboardDocumentViewModel?> SelectedDashboard { get; set; } = () => null;

    /// <summary>True when any dashboard window lets clicks through.</summary>
    public bool AnyClickThrough => _dashboardWindows.Any(w => w.ClickThrough);

    /// <summary>Tray/hotkey: turns click-through off everywhere if any window has it, else on for all open windows.</summary>
    public void ToggleClickThrough()
    {
        var on = !AnyClickThrough;
        foreach (var window in _dashboardWindows)
        {
            window.ClickThrough = on; // turning it on also keeps the window on top
        }
    }

    /// <summary>Global hotkey: hides all dashboard windows if any is visible, else shows them (or opens the current one).</summary>
    public void ToggleDashboardWindows()
    {
        if (_dashboardWindows.Count == 0)
        {
            if (SelectedDashboard() is { } dashboard)
            {
                OpenDashboardWindow(dashboard);
            }

            return;
        }

        var anyVisible = _dashboardWindows.Any(w => w.IsVisible);
        foreach (var window in _dashboardWindows)
        {
            if (anyVisible)
            {
                window.Hide();
            }
            else
            {
                window.Show();
            }
        }
    }

    public DialogService(SensorStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;
    }

    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    /// <summary>Add (<paramref name="existing"/> null) or edit a tile. Returns the new spec, or null if cancelled.</summary>
    public TileSpec? EditTile(TileSpec? existing)
    {
        var window = new TileEditorWindow(new TileEditorViewModel(_store, existing)) { Owner = Owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    /// <summary>The "Build sidebar" checklist. Returns the layout and whether it replaces <paramref name="existing"/>; null if cancelled.</summary>
    /// <param name="existing">The sidebar dashboard to update, or null for a new one.</param>
    public (SensorDeck.Core.Dashboards.DashboardDocument Document, bool Replace)? BuildSidebar(int columns, SensorDeck.Core.Dashboards.DashboardDocument? existing)
    {
        var model = new SidebarBuilderViewModel(_store, columns, existing);
        return new SidebarBuilderWindow(model) { Owner = Owner }.ShowDialog() == true && model.Result is { } doc
            ? (doc, model.ReplaceExisting)
            : null;
    }

    /// <summary>Rename / hide / scale-correct a sensor.</summary>
    public void EditSensor(SensorItem sensor)
    {
        using var model = new SensorEditViewModel(sensor, _store.Customizations, _store.TemperatureUnit);
        new SensorEditWindow(model) { Owner = Owner }.ShowDialog();
    }

    /// <summary>Add (<paramref name="existing"/> null) or edit a calculated sensor; saves or deletes it in the store.</summary>
    public void EditCalculatedSensor(SensorDeck.Core.Calculated.CalculatedSensor? existing)
    {
        using var model = new CalculatedSensorViewModel(_store, existing);
        if (new CalculatedSensorWindow(model) { Owner = Owner }.ShowDialog() != true)
        {
            return;
        }

        if (model.Deleted && existing is not null)
        {
            if (Confirm($"Delete the calculated sensor \"{existing.Name}\"? Tiles and alerts using it will show it as missing."))
            {
                _store.DeleteCalculated(existing.Id);
            }
        }
        else if (model.Result is { } result)
        {
            _store.SaveCalculated(result);
        }
    }

    /// <summary>Shows a recording's per-sensor statistics.</summary>
    public void ShowRecordingSummary(RecordingSummary summary) =>
        new RecordingSummaryWindow(new RecordingSummaryViewModel(summary, _store.TemperatureUnit)) { Owner = Owner }.ShowDialog();

    /// <summary>Add (<paramref name="existing"/> null) or edit an automation rule. Null if cancelled.</summary>
    public SensorDeck.Core.Automation.AutomationRule? EditAutomationRule(SensorDeck.Core.Automation.AutomationRule? existing)
    {
        var fans = new FanProfileService(_settings);
        fans.Refresh();
        IReadOnlyList<string> running;
        try
        {
            running = AutomationService.RunningProgramNames();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            running = [];
        }

        var model = new RuleEditorViewModel(_store, fans.Configs.Select(c => c.Name), running, existing);
        var window = new RuleEditorWindow(model) { Owner = Owner };
        return window.ShowDialog() == true ? model.Result : null;
    }

    /// <summary>Asks which sensor replaces a missing one. Returns its id, or null if cancelled.</summary>
    public string? PickReplacement(string missingId)
    {
        var window = new RelinkWindow(new RelinkViewModel(_store, missingId)) { Owner = Owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    /// <summary>Asks for a line of text. Null if cancelled or left blank.</summary>
    public string? Prompt(string title, string label, string initial)
    {
        var window = new PromptWindow(title, label, initial) { Owner = Owner };
        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.Value) ? window.Value.Trim() : null;
    }

    public bool Confirm(string message) =>
        Show(message, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public void ShowError(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, "SensorDeck", buttons, image)
            : MessageBox.Show(message, "SensorDeck", buttons, image);

    public string? PickOpenFile() => PickOpenFile("Import dashboard", "SensorDeck dashboard (*.json)|*.json|All files (*.*)|*.*");

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return ShowFileDialog(dialog) ? dialog.FileName : null;
    }

    private static bool ShowFileDialog(CommonDialog dialog) =>
        (Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;

    public string? PickSaveFile(string suggestedName) =>
        PickSaveFile("Export dashboard", suggestedName, "SensorDeck dashboard (*.json)|*.json");

    public string? PickSaveFile(string title, string suggestedName, string filter)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. suggestedName.Select(c => invalid.Contains(c) ? '_' : c)]);
        // Extension from the filter's first pattern ("CSV file (*.csv)|*.csv" → ".csv").
        var pattern = filter.Split('|').ElementAtOrDefault(1) ?? "*.json";
        var extension = Path.GetExtension(pattern.Split(';')[0]);
        if (string.IsNullOrEmpty(extension) || extension.Contains('*', StringComparison.Ordinal))
        {
            extension = ".json";
        }

        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = safe + extension,
            Filter = filter,
            DefaultExt = extension,
        };
        return ShowFileDialog(dialog) ? dialog.FileName : null;
    }

    /// <summary>Windows is signing out: remember every open dashboard window so it reopens next time.</summary>
    public void SaveDashboardWindowsForExit()
    {
        foreach (var window in _dashboardWindows)
        {
            window.RememberForExit();
        }

        _settings.Save();
    }

    /// <summary>Closes the window showing this dashboard, if any (e.g. when it's deleted).</summary>
    public void CloseDashboardWindow(DashboardDocumentViewModel dashboard)
    {
        foreach (var window in _dashboardWindows.ToList())
        {
            if (ReferenceEquals(window.Home, dashboard))
            {
                window.Close();
            }
            else if (ReferenceEquals(window.DataContext, dashboard))
            {
                window.ShowDashboard(window.Home); // was showing it while rotating
            }
        }

        if (_settings.DashboardWindows.Remove(dashboard.Id))
        {
            _settings.Save();
        }
    }

    /// <summary>Shows a dashboard in its own borderless window (full screen, or on a second monitor).</summary>
    public void OpenDashboardWindow(DashboardDocumentViewModel dashboard)
    {
        var existing = _dashboardWindows.FirstOrDefault(w => ReferenceEquals(w.Home, dashboard));
        if (existing is not null)
        {
            existing.Show();
            existing.Activate();
            return;
        }

        if (!_settings.DashboardWindows.TryGetValue(dashboard.Id, out var state))
        {
            state = new DashboardWindowState();
            _settings.DashboardWindows[dashboard.Id] = state;
        }

        state.Open = true;
        _settings.Save();

        var window = new DashboardWindow(state, _settings.Save, dashboard, AllDashboards);
        window.Closed += (_, _) => _dashboardWindows.Remove(window);
        _dashboardWindows.Add(window);
        window.Show();
    }
}
