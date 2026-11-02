using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Customization;

namespace SpaceDeck.App.ViewModels;

/// <summary>Pick the sensor that replaces one that disappeared; best matches first, or search everything.</summary>
public sealed partial class RelinkViewModel : ObservableObject
{
    private readonly SensorStore _store;

    [ObservableProperty]
    private string _searchText = "";

    public RelinkViewModel(SensorStore store, string missingId)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        MissingId = missingId;
        var known = store.Customizations.Known(missingId);
        MissingDescription = known is not null ? $"{known.Name} — {known.Hardware}" : missingId;

        var candidates = store.Sensors.Select(s => new SensorCandidate(s.Id, s.OriginalName, s.Kind, s.GroupName));
        foreach (var match in SensorMatcher.Suggest(missingId, known, candidates))
        {
            if (store.SensorsById.TryGetValue(match.Id, out var sensor))
            {
                Suggestions.Add(sensor);
            }
        }
    }

    public string MissingId { get; }

    /// <summary>"GPU Core — NVIDIA GeForce RTX 3060 Ti", or the id if it was never seen by this version.</summary>
    public string MissingDescription { get; }

    public ObservableCollection<SensorItem> Suggestions { get; } = [];

    public ObservableCollection<SensorItem> SearchResults { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    public string? Result { get; private set; }

    public event Action? RequestClose;

    partial void OnSearchTextChanged(string value)
    {
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var sensor in _store.Sensors
                     .Where(s => words.All(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                || s.OriginalName.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                || s.GroupName.Contains(w, StringComparison.OrdinalIgnoreCase)))
                     .Take(30))
        {
            SearchResults.Add(sensor);
        }
    }

    [RelayCommand]
    private void Choose(SensorItem? sensor)
    {
        if (sensor is null)
        {
            return;
        }

        Result = sensor.Id;
        RequestClose?.Invoke();
    }
}
