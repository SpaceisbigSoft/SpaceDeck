using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SensorDeck.App.Remote;
using SensorDeck.App.Services;

namespace SensorDeck.App.ViewModels;

/// <summary>One paired browser in Settings.</summary>
public sealed record RemoteDeviceRow(RemoteDevice Device, string Name, string Seen);

/// <summary>Settings → Network dashboard and Phone notifications (ntfy).</summary>
public sealed partial class RemoteSettingsViewModel : ObservableObject
{
    private readonly RemoteServer _server;
    private readonly NtfyService _ntfy;
    private readonly AppSettings _settings;

    [ObservableProperty]
    private string _portText = "";

    [ObservableProperty]
    private string? _ntfyStatus;

    [ObservableProperty]
    private bool _isApplying;

    public RemoteSettingsViewModel(RemoteServer server, NtfyService ntfy, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
        _ntfy = ntfy;
        _settings = settings;
        PortText = settings.Remote.Port.ToString(CultureInfo.InvariantCulture);
        server.DevicesChanged += RefreshDevices;
        server.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemoteServer.Settings))
            {
                OnPropertyChanged(nameof(Pin));
            }
        };
        RefreshDevices();
    }

    public RemoteServer Server => _server;

    public bool Enabled
    {
        get => _settings.Remote.Enabled;
        set
        {
            if (_settings.Remote.Enabled != value)
            {
                _settings.Remote.Enabled = value;
                _settings.Save();
                OnPropertyChanged();
                _ = ApplyAsync();
            }
        }
    }

    public bool AllowControl
    {
        get => _settings.Remote.AllowControl;
        set
        {
            _settings.Remote.AllowControl = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string Pin => _settings.Remote.Pin;

    /// <summary>"http://192.168.1.20:8187" lines.</summary>
    public string AddressText => _server.Addresses.Count == 0 ? "No network connection found." : string.Join(Environment.NewLine, _server.Addresses);

    public ObservableCollection<RemoteDeviceRow> Devices { get; } = [];

    public bool HasNoDevices => Devices.Count == 0;

    [RelayCommand]
    private async Task ApplyPortAsync()
    {
        if (!int.TryParse(PortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            PortText = _settings.Remote.Port.ToString(CultureInfo.InvariantCulture);
            return;
        }

        _settings.Remote.Port = port;
        _settings.Save();
        OnPropertyChanged(nameof(AddressText));
        await ApplyAsync();
    }

    [RelayCommand]
    private void NewPin() => _server.NewPin();

    [RelayCommand]
    private void Forget(RemoteDeviceRow? row)
    {
        if (row is not null)
        {
            _server.Forget(row.Device);
        }
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (_server.Addresses.FirstOrDefault() is { } address)
        {
            CollectorServiceStatus.OpenWebUrl(address);
        }
    }

    // ── ntfy ──────────────────────────────────────────────────────────

    public bool NtfyEnabled
    {
        get => _settings.Ntfy.Enabled;
        set
        {
            _settings.Ntfy.Enabled = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string NtfyServer
    {
        get => _settings.Ntfy.Server;
        set
        {
            _settings.Ntfy.Server = string.IsNullOrWhiteSpace(value) ? NtfySettings.DefaultServer : value.Trim();
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NtfyTokenSentInClear));
        }
    }

    public string NtfyTopic
    {
        get => _settings.Ntfy.Topic;
        set
        {
            _settings.Ntfy.Topic = string.IsNullOrWhiteSpace(value) ? NtfySettings.NewTopic() : value.Trim();
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string? NtfyAccessToken
    {
        get => _settings.Ntfy.AccessToken;
        set
        {
            _settings.Ntfy.AccessToken = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NtfyTokenSentInClear));
        }
    }

    /// <summary>True when an access token is set and the server is plain http:// (the token travels unencrypted).</summary>
    public bool NtfyTokenSentInClear =>
        _settings.Ntfy.AccessToken is not null
        && _settings.Ntfy.Server.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task TestNtfyAsync()
    {
        NtfyStatus = "Sending…";
        NtfyStatus = await _ntfy.PublishAsync("SensorDeck", "Test notification: phone alerts are working.", critical: false, force: true)
                     ?? "Sent. It should appear in the ntfy app within a few seconds.";
    }

    /// <summary>The server address is free text (and can come from a restored backup): only ever opened as a web address.</summary>
    [RelayCommand]
    private void OpenTopic() => NtfyStatus = CollectorServiceStatus.OpenWebUrl(_ntfy.TopicUrl) ? null : "The server must be an http:// or https:// address.";

    private async Task ApplyAsync()
    {
        IsApplying = true;
        try
        {
            await _server.ApplyAsync();
        }
        finally
        {
            IsApplying = false;
            OnPropertyChanged(nameof(AddressText));
        }
    }

    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var d in _settings.Remote.Devices.OrderByDescending(d => d.LastSeen ?? d.Paired))
        {
            var seen = d.LastSeen is { } s ? $"last seen {s.LocalDateTime:g}" : $"paired {d.Paired.LocalDateTime:g}";
            Devices.Add(new RemoteDeviceRow(d, d.Name, seen));
        }

        OnPropertyChanged(nameof(HasNoDevices));
    }
}
