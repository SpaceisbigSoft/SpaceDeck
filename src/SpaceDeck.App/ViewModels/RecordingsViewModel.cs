using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.ViewModels;

/// <summary>One saved recording in the list.</summary>
public sealed record RecordingRow(RecordingSummary Summary, string Title, string When, string Highlights, string Alerts, bool HasAlerts);

/// <summary>Recordings page: start/stop a session, and review, open or delete saved ones.</summary>
public sealed partial class RecordingsViewModel : ObservableObject
{
    private readonly RecordingService _recorder;
    private readonly SensorStore _store;
    private readonly DialogService _dialogs;

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private string? _message;

    public RecordingsViewModel(RecordingService recorder, SensorStore store, DialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        _recorder = recorder;
        _store = store;
        _dialogs = dialogs;
        recorder.RecordingsChanged += Refresh;
        Refresh();
    }

    public RecordingService Recorder => _recorder;

    public ObservableCollection<RecordingRow> Items { get; } = [];

    public bool HasNoItems => Items.Count == 0;

    [RelayCommand]
    public void Refresh()
    {
        Items.Clear();
        foreach (var summary in RecordingService.List())
        {
            Items.Add(Describe(summary, _store.TemperatureUnit));
        }

        OnPropertyChanged(nameof(HasNoItems));
    }

    [RelayCommand]
    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            _recorder.AutomaticStoppedByUser = _recorder.IsAutomatic;
            var summary = _recorder.Stop();
            if (summary is not null)
            {
                _dialogs.ShowRecordingSummary(summary);
            }

            return;
        }

        Message = _recorder.Start(NewName);
        if (Message is null)
        {
            NewName = "";
        }
    }

    [RelayCommand]
    private void ShowSummary(RecordingRow? row)
    {
        if (row is not null)
        {
            _dialogs.ShowRecordingSummary(row.Summary);
        }
    }

    [RelayCommand]
    private void OpenCsv(RecordingRow? row)
    {
        if (row is null)
        {
            return;
        }

        Message = SafeCsvPath(row.Summary) is not { } csv ? "The CSV file isn't in the recordings folder, so it wasn't opened."
            : !File.Exists(csv) ? "The CSV file is missing."
            : CollectorServiceStatus.OpenUrl(csv) ? null
            : "Couldn't open the CSV (is a spreadsheet app installed?).";
    }

    /// <summary>
    /// The recording's CSV, but only if it is a .csv file inside the recordings folder. Its name comes from the
    /// summary JSON, and opening it goes through the shell, so an edited summary must not make "Open CSV" start
    /// some other file (an .exe, a script, a file elsewhere on disk). Null otherwise.
    /// </summary>
    internal static string? SafeCsvPath(RecordingSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        try
        {
            var folder = Path.GetFullPath(RecordingService.Directory) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(summary.CsvPath);
            return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [RelayCommand]
    private void Delete(RecordingRow? row)
    {
        if (row is not null && _dialogs.Confirm($"Delete the recording \"{row.Title}\" and its CSV?"))
        {
            _recorder.Delete(row.Summary);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(RecordingService.Directory);
        CollectorServiceStatus.OpenUrl(RecordingService.Directory);
    }

    internal static string FormatValue(SensorKind kind, double? value, TemperatureUnit unit) =>
        value is { } v ? UnitFormatter.Format(kind, (float)v, unit) : "–";

    private static RecordingRow Describe(RecordingSummary summary, TemperatureUnit unit)
    {
        var hottest = summary.Sensors
            .Where(s => s.Kind == SensorKind.Temperature && s.Max is not null)
            .OrderByDescending(s => s.Max)
            .Take(3)
            .Select(s => $"{s.Name} {FormatValue(s.Kind, s.Max, unit)}");
        var highlights = string.Join(" · ", hottest);

        var crossed = summary.Sensors.Where(s => s.WarningSeconds + s.CriticalSeconds > 0).ToList();
        var alerts = crossed.Count == 0
            ? "No limits crossed"
            : $"{crossed.Count} sensor{(crossed.Count == 1 ? "" : "s")} past limits"
              + (crossed.Any(s => s.CriticalSeconds > 0) ? $" (critical for {RecordingService.Format(TimeSpan.FromSeconds(crossed.Max(s => s.CriticalSeconds)))})" : "");

        var when = $"{summary.Start.LocalDateTime:g} · {RecordingService.Format(summary.Duration)}{(summary.Automatic ? " · automatic" : "")}";
        return new RecordingRow(summary, summary.Name, when, highlights.Length == 0 ? "No temperatures recorded" : $"Peaks: {highlights}", alerts, crossed.Count > 0);
    }
}

/// <summary>One sensor's line in a recording summary.</summary>
public sealed record RecordingStatRow(string Name, string Group, string Min, string Avg, string Max, string Warning, string Critical, bool HasAlert);

/// <summary>The summary window of one recording.</summary>
public sealed partial class RecordingSummaryViewModel : ObservableObject
{
    [ObservableProperty]
    private string _searchText = "";

    public RecordingSummaryViewModel(RecordingSummary summary, TemperatureUnit unit)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Summary = summary;
        Title = summary.Name;
        Subtitle = $"{summary.Start.LocalDateTime:g} – {summary.End.LocalDateTime:t} · {RecordingService.Format(summary.Duration)} · {summary.Samples:N0} samples";
        Rows =
        [
            .. summary.Sensors
                .OrderByDescending(s => s.CriticalSeconds)
                .ThenByDescending(s => s.WarningSeconds)
                .ThenBy(s => s.Group, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(s => new RecordingStatRow(
                    s.Name,
                    s.Group,
                    RecordingsViewModel.FormatValue(s.Kind, s.Min, unit),
                    RecordingsViewModel.FormatValue(s.Kind, s.Avg, unit),
                    RecordingsViewModel.FormatValue(s.Kind, s.Max, unit),
                    s.WarningSeconds > 0 ? RecordingService.Format(TimeSpan.FromSeconds(s.WarningSeconds)) : "",
                    s.CriticalSeconds > 0 ? RecordingService.Format(TimeSpan.FromSeconds(s.CriticalSeconds)) : "",
                    s.WarningSeconds + s.CriticalSeconds > 0)),
        ];
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is RecordingStatRow r && (SearchText.Length == 0
            || r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || r.Group.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    public RecordingSummary Summary { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<RecordingStatRow> Rows { get; }

    public ICollectionView View { get; }

    partial void OnSearchTextChanged(string value) => View.Refresh();

    [RelayCommand]
    private void OpenCsv()
    {
        if (RecordingsViewModel.SafeCsvPath(Summary) is { } csv && File.Exists(csv))
        {
            CollectorServiceStatus.OpenUrl(csv);
        }
    }
}
