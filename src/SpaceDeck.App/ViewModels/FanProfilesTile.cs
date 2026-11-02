using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Integrations.FanControl;

namespace SpaceDeck.App.ViewModels;

/// <summary>One profile button on a fan-profile tile.</summary>
public sealed partial class FanProfileChoice : ObservableObject
{
    [ObservableProperty]
    private bool _isCurrent;

    public FanProfileChoice(string name, bool isCurrent)
    {
        Name = name;
        IsCurrent = isCurrent;
    }

    public string Name { get; }
}

/// <summary>Dashboard tile that switches FanControl configs, like the header picker.</summary>
public sealed partial class FanProfilesTile : ObservableObject, IDisposable
{
    private readonly FanProfileService _service;

    [ObservableProperty]
    private string? _status;

    public FanProfilesTile(FanProfileService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        if (service.Install is null)
        {
            service.Refresh();
        }

        foreach (var config in service.Configs)
        {
            Choices.Add(new FanProfileChoice(config.Name, string.Equals(config.Name, service.LastProfile, StringComparison.OrdinalIgnoreCase)));
        }

        Status = service.Install is null ? "FanControl not found" : Choices.Count == 0 ? "No saved FanControl configs" : null;
        service.ProfileSwitched += OnSwitched;
    }

    public ObservableCollection<FanProfileChoice> Choices { get; } = [];

    [RelayCommand]
    private void Switch(FanProfileChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        var result = _service.Switch(choice.Name);
        Status = result.Outcome is SwitchOutcome.Sent or SwitchOutcome.StartedWithConfig ? null : result.Message;
    }

    private void OnSwitched(string name)
    {
        foreach (var choice in Choices)
        {
            choice.IsCurrent = string.Equals(choice.Name, name, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose() => _service.ProfileSwitched -= OnSwitched;
}
