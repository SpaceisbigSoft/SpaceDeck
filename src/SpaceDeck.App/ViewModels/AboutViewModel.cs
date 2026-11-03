using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;

namespace SpaceDeck.App.ViewModels;

/// <summary>A third-party component SpaceDeck uses or works with.</summary>
public sealed record Component(string Name, string Use, string License, string Url);

/// <summary>About page: version, where things are stored, and the third-party components with their licenses.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly SensorStore _store;
    private readonly AppSettings _settings;

    public AboutViewModel(SensorStore store, AppSettings settings, UpdateService updates)
    {
        _store = store;
        _settings = settings;
        Updates = updates;
    }

    public UpdateService Updates { get; }

    /// <summary>Opt-in daily check (off by default).</summary>
    public bool UpdateCheckEnabled
    {
        get => _settings.UpdateCheckEnabled;
        set
        {
            _settings.UpdateCheckEnabled = value;
            _settings.Save();
            if (value)
            {
                Updates.StartAutomaticCheck();
            }
        }
    }

    public bool CanCheckForUpdates => !AppMode.IsReplay;

    public string AppVersion => $"SpaceDeck {DiagnosticsViewModel.AppVersion}";

    public SensorStore Store => _store;

    public string DataFolder => AppSettings.Directory;

    public IReadOnlyList<Component> Components { get; } =
    [
        new("LibreHardwareMonitor", "Reads the hardware sensors (in the collector)", "MPL-2.0", "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor"),
        new("DiskInfoToolkit, RAMSPDToolkit, BlackSharp", "Drive and memory-module details, used by LibreHardwareMonitor", "MPL-2.0", "https://github.com/Blacktempel/RAMSPDToolkit"),
        new("HidSharp", "USB device access, used by LibreHardwareMonitor", "Apache-2.0", "https://www.zer7.com/software/hidsharp"),
        new("PawnIO modules", "Small signed programs inside LibreHardwareMonitor that the PawnIO driver runs", "LGPL-2.1", "https://github.com/namazso/PawnIO.Modules"),
        new("PawnIO", "Signed driver for low-level sensor access; installed separately, not included", "See pawnio.eu", "https://pawnio.eu"),
        new("MessagePack for C#", "App ↔ collector messages", "MIT", "https://github.com/MessagePack-CSharp/MessagePack-CSharp"),
        new("CommunityToolkit.Mvvm", "App structure (view models)", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new(".NET, WPF, ASP.NET Core, Microsoft.Extensions", "Runtime, windows, network dashboard server, collector service", "MIT", "https://github.com/dotnet"),
        new("FanControl", "Fan control; SpaceDeck only switches its saved configs. Not included", "FanControl's own terms", "https://getfancontrol.com"),
        new("ntfy", "Optional phone notifications (a service you choose to use)", "Apache-2.0 / GPL-2.0", "https://ntfy.sh"),
    ];

    [RelayCommand]
    private void Open(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            CollectorServiceStatus.OpenWebUrl(url);
        }
    }

    [RelayCommand]
    private Task CheckForUpdates() => Updates.CheckAsync();

    [RelayCommand]
    private Task InstallUpdate() => Updates.DownloadAndInstallAsync();

    [RelayCommand]
    private void OpenRelease()
    {
        if (Updates.Available is { Url.Length: > 0 } release)
        {
            CollectorServiceStatus.OpenWebUrl(release.Url); // from the release feed: web addresses only
        }
    }

    public string LicenseText => "SpaceDeck is free software under the MIT License, provided \"as is\" without warranty. " +
        "It shows what your sensors report but is not a safety device: readings can be wrong and alerts can be late, so " +
        "don't rely on it to protect hardware, people or property. No telemetry; see the privacy note.";

    /// <summary>Opens LICENSE.txt, THIRD-PARTY-NOTICES.txt or PRIVACY.txt from the program folder.</summary>
    [RelayCommand]
    private void OpenDocument(string? name)
    {
        if (name is not ("LICENSE.txt" or "THIRD-PARTY-NOTICES.txt" or "PRIVACY.txt"))
        {
            return;
        }

        var path = System.IO.Path.Combine(AppContext.BaseDirectory, name);
        DocumentMessage = System.IO.File.Exists(path)
            ? (CollectorServiceStatus.OpenUrl(path) ? null : $"Couldn't open {name}.")
            : $"{name} wasn't found next to SpaceDeck (rebuild, or reinstall).";
    }

    [ObservableProperty]
    private string? _documentMessage;

    [RelayCommand]
    private void OpenDataFolder() => CollectorServiceStatus.OpenUrl(DataFolder);
}
