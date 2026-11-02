using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceDeck.Core.Alerts;
using SpaceDeck.Core.Automation;
using SpaceDeck.Core.Calculated;
using SpaceDeck.Core.Formatting;

namespace SpaceDeck.App.Services;

/// <summary>Per-user UI settings, stored in <c>%AppData%\SpaceDeck\settings.json</c>.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>%AppData%\SpaceDeck.</summary>
    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceDeck");

    /// <summary>Where this instance keeps its files: <see cref="DefaultDirectory"/>, or a replay's own profile folder.</summary>
    public static string Directory { get; private set; } = DefaultDirectory;

    /// <summary>Use another folder (replay mode). Call before anything loads or saves.</summary>
    internal static void UseDirectory(string directory) => Directory = directory;

    private static string FilePath => Path.Combine(Directory, "settings.json");

    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;

    /// <summary>Visual skin id (one of <c>SkinManager.Available</c>, see Themes/Skins); "Monochrome" by default.</summary>
    public string Skin { get; set; } = "Monochrome";

    /// <summary>Sensor ids picked on the History page.</summary>
    public List<string> HistorySensors { get; set; } = [];

    public int HistoryWindowSeconds { get; set; } = 300;

    /// <summary>Optional override when FanControl isn't found automatically.</summary>
    public string? FanControlExePath { get; set; }

    /// <summary>The FanControl config SpaceDeck last switched to (FanControl doesn't report its active config).</summary>
    public string? LastFanProfile { get; set; }

    public string? LastPage { get; set; }

    /// <summary>Id of the dashboard tab shown last.</summary>
    public string? LastDashboard { get; set; }

    /// <summary>Master switch for alert notifications (levels still color the UI when off).</summary>
    public bool AlertsEnabled { get; set; } = true;

    /// <summary>Also notify for warnings (critical always notifies).</summary>
    public bool AlertNotifyWarnings { get; set; } = true;

    /// <summary>Notify when a sensor is back to normal.</summary>
    public bool AlertNotifyCleared { get; set; }

    /// <summary>User rules, overriding the built-in defaults for the same sensor (a disabled rule silences it).</summary>
    public List<AlertRule> AlertRules { get; set; } = [];

    /// <summary>The window's close button hides SpaceDeck to the tray instead of quitting.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Minimizing hides SpaceDeck to the tray instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>False until the user finishes the first-run setup page (Diagnostics).</summary>
    /// <remarks>Defaults to true so existing settings files (without the key) don't re-run setup; only a brand-new
    /// install starts with it false (see <see cref="Load"/>).</remarks>
    public bool SetupCompleted { get; set; } = true;

    /// <summary>Sensors computed from others (All sensors → Add calculated sensor).</summary>
    public List<CalculatedSensor> CalculatedSensors { get; set; } = [];

    /// <summary>Text and layout size, 0.8–1.6 (1 = normal).</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>Accent color "#RRGGBB" over the skin's own; null = the skin's.</summary>
    public string? AccentColor { get; set; }

    /// <summary>"system", "12" or "24": how clocks show the time.</summary>
    public string ClockFormat { get; set; } = "system";

    /// <summary>Network dashboard (Steam Deck, phones…): serve dashboards to browsers on the home network.</summary>
    public RemoteSettings Remote { get; set; } = new();

    /// <summary>Highest RPM seen per fan sensor id (100 % for fans FanControl hasn't calibrated).</summary>
    public Dictionary<string, float> FanMaxRpm { get; set; } = new();

    /// <summary>The screen-edge sidebar (off by default).</summary>
    public SidebarSettings Sidebar { get; set; } = new();

    /// <summary>Phone notifications through ntfy.</summary>
    public NtfySettings Ntfy { get; set; } = new();

    /// <summary>Global shortcut that shows/hides all dashboard windows, e.g. "Ctrl+Alt+D". Empty = off.</summary>
    public string OverlayHotkey { get; set; } = "Ctrl+Alt+D";

    /// <summary>Global shortcut that turns click-through on/off for dashboard windows. Empty = off.</summary>
    public string ClickThroughHotkey { get; set; } = "Ctrl+Shift+Alt+C";

    /// <summary>Opt-in: look for a newer SpaceDeck about once a day (never downloads or installs).</summary>
    public bool UpdateCheckEnabled { get; set; }

    /// <summary>Release feed override (GitHub "latest release" API URL); empty = the built-in one.</summary>
    public string? UpdateFeedUrl { get; set; }

    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Automatic fan profiles and alert actions (Automation page).</summary>
    public AutomationSettings Automation { get; set; } = new();

    /// <summary>No notifications or sounds between <see cref="QuietFrom"/> and <see cref="QuietTo"/>.</summary>
    public bool QuietHoursEnabled { get; set; }

    public string QuietFrom { get; set; } = "23:00";

    public string QuietTo { get; set; } = "07:00";

    /// <summary>Critical alerts still notify during quiet hours.</summary>
    public bool QuietAllowCritical { get; set; } = true;

    /// <summary>True when a notification or sound of this severity should stay silent right now.</summary>
    public bool IsQuietNow(bool critical) =>
        QuietHoursEnabled && !(critical && QuietAllowCritical) && TimeWindow.Contains(QuietFrom, QuietTo, TimeOnly.FromDateTime(DateTime.Now));

    /// <summary>Set once the "still running in the tray" hint has been shown.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>Where each dashboard's own window was, keyed by dashboard id (restored next time, reopened at startup).</summary>
    public Dictionary<string, DashboardWindowState> DashboardWindows { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
                loaded.HistorySensors = [.. (loaded.HistorySensors ?? []).Where(id => id is not null)]; // hand-edited "null"
                // JSON null inside a list or dictionary is kept by the serializer even for non-nullable types, and would
                // throw later on the UI thread (hand edits, restored backups): dropped here.
                loaded.DashboardWindows = new Dictionary<string, DashboardWindowState>(
                    (loaded.DashboardWindows ?? new()).Where(kv => kv.Value is not null));
                loaded.AlertRules = [.. (loaded.AlertRules ?? []).Where(r => r is not null && !string.IsNullOrEmpty(r.SensorId))];
                loaded.Skin ??= "Monochrome";
                loaded.Automation ??= new();
                loaded.Automation.Rules = [.. (loaded.Automation.Rules ?? []).Where(r => r is not null)];
                loaded.Automation.Alerts ??= new();
                foreach (var rule in loaded.Automation.Rules)
                {
                    rule.Processes = [.. (rule.Processes ?? []).Where(p => !string.IsNullOrWhiteSpace(p))];
                    rule.Profile ??= "";
                    rule.Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id;
                }

                loaded.CalculatedSensors = [.. (loaded.CalculatedSensors ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id))];
                foreach (var calc in loaded.CalculatedSensors)
                {
                    calc.Inputs = [.. (calc.Inputs ?? []).Where(id => id is not null)]; // positions matter (e.g. a difference): only nulls go
                    calc.Name ??= "Calculated";
                    calc.Unit ??= "";
                }

                loaded.OverlayHotkey ??= "";
                loaded.ClickThroughHotkey ??= "";
                loaded.ClockFormat ??= "system";
                loaded.UiScale = double.IsFinite(loaded.UiScale) ? Math.Clamp(loaded.UiScale, 0.8, 1.6) : 1.0;
                loaded.Remote ??= new();
                loaded.Sidebar ??= new();
                loaded.FanMaxRpm ??= new();
                loaded.Sidebar.Opacity = Math.Clamp(loaded.Sidebar.Opacity, SidebarSettings.MinOpacity, 1);
                loaded.Sidebar.Columns = Math.Clamp(loaded.Sidebar.Columns, SidebarSettings.MinColumns, SidebarSettings.MaxColumns);
                loaded.Remote.Devices = [.. (loaded.Remote.Devices ?? []).Where(d => d is not null && !string.IsNullOrEmpty(d.Token))];
                if (loaded.Remote.Pin is not { Length: 6 } pin || !pin.All(char.IsAsciiDigit))
                {
                    loaded.Remote.Pin = RemoteSettings.NewPin(); // hand-edited: an empty or short PIN must never pair
                }

                loaded.Ntfy ??= new();
                loaded.Ntfy.Server ??= NtfySettings.DefaultServer;
                loaded.Ntfy.Topic ??= NtfySettings.NewTopic();
                loaded.QuietFrom ??= "23:00";
                loaded.QuietTo ??= "07:00";
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: start fresh rather than fail to launch. The first save would overwrite a
            // corrupt file, so keep a copy for recovery.
            if (ex is JsonException)
            {
                KeepCorruptCopy();
            }
        }

        return new AppSettings { SetupCompleted = File.Exists(FilePath) }; // no file at all = first run
    }

    /// <summary>
    /// Writes settings.json. Atomic (temp file, then rename), so a crash or full disk mid-write never leaves a cut-off
    /// file that would reset every setting; serialized, because background work (update check, remote server) saves too.
    /// </summary>
    public void Save()
    {
        try
        {
            lock (SaveLock)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(temp, FilePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting the user over.
        }
    }

    private static readonly Lock SaveLock = new();

    private static void KeepCorruptCopy()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

/// <summary>A dashboard window's placement and options.</summary>
public sealed class DashboardWindowState
{
    /// <summary>False until the window has been shown once (then Left/Top are real, even 0,0).</summary>
    public bool HasPosition { get; set; }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 800;

    public bool Maximized { get; set; }

    public bool Topmost { get; set; }

    /// <summary>0.4–1.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>Open when the app last exited: reopen at startup.</summary>
    public bool Open { get; set; }

    /// <summary>Show each dashboard in turn, this many seconds apart; 0 = stay on one.</summary>
    public int RotateSeconds { get; set; }

    /// <summary>Mouse clicks go through the window to whatever is under it (a game); turned off from the tray or hotkey.</summary>
    public bool ClickThrough { get; set; }

    /// <summary>Shrink the content so all of it fits the window height (down to 70 %; see <see cref="ContentSize"/>).</summary>
    public bool FitToHeight { get; set; }

    /// <summary>Content size while <see cref="FitToHeight"/> is off: 0.7–1.5 (1 = 100 %); clamped where it's applied.</summary>
    public double ContentScale { get; set; } = 1;
}

public enum SidebarEdge
{
    Left,
    Right,
}

/// <summary>A dashboard docked to the left or right edge of one monitor, like a second taskbar.</summary>
public sealed class SidebarSettings
{
    /// <summary>Never fully see-through.</summary>
    public const double MinOpacity = 0.4;

    /// <summary>The widths the sidebar menu offers; a hand-edited value can't fill the screen.</summary>
    public const int MinColumns = 2;

    /// <summary>See <see cref="MinColumns"/>.</summary>
    public const int MaxColumns = 6;

    public bool Enabled { get; set; }

    /// <summary>The dashboard it shows (null = the first one).</summary>
    public string? DashboardId { get; set; }

    /// <summary>Monitor device name (e.g. \\.\DISPLAY2); null or unplugged = the primary monitor.</summary>
    public string? Monitor { get; set; }

    public SidebarEdge Edge { get; set; } = SidebarEdge.Right;

    /// <summary>Reserve the strip like the taskbar does (maximized windows stop at it); off = float on top.</summary>
    public bool ReserveSpace { get; set; } = true;

    /// <summary>Get out of the way while a game or video is fullscreen on the same monitor.</summary>
    public bool HideWhenFullscreen { get; set; } = true;

    /// <summary>0.4–1.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>Width in dashboard columns (2–6; 3 ≈ 250 px). Wider dashboards reflow to fit.</summary>
    public int Columns { get; set; } = 3;

    /// <summary>Shrink the content so every row fits the screen height (down to 70 %, then it scrolls; see <see cref="ContentSize"/>).</summary>
    public bool FitToHeight { get; set; } = true;

    /// <summary>Content size while <see cref="FitToHeight"/> is off: 0.7–1.5 (1 = 100 %); clamped where it's applied.</summary>
    public double ContentScale { get; set; } = 1;
}

/// <summary>Network dashboard settings. Off by default; devices pair once with the PIN.</summary>
public sealed class RemoteSettings
{
    public const int DefaultPort = 8187;

    public bool Enabled { get; set; }

    public int Port { get; set; } = DefaultPort;

    /// <summary>Paired devices may switch the fan profile.</summary>
    public bool AllowControl { get; set; }

    /// <summary>Shown in SpaceDeck; typed on a new device to pair it.</summary>
    public string Pin { get; set; } = NewPin();

    public List<RemoteDevice> Devices { get; set; } = [];

    public static string NewPin() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A browser that paired with the PIN; it sends <see cref="Token"/> with every request.</summary>
public sealed class RemoteDevice
{
    public string Name { get; set; } = "";

    public string Token { get; set; } = "";

    public DateTimeOffset Paired { get; set; }

    public DateTimeOffset? LastSeen { get; set; }
}

/// <summary>ntfy (ntfy.sh or a self-hosted server): alerts also go to the ntfy phone app.</summary>
public sealed class NtfySettings
{
    public const string DefaultServer = "https://ntfy.sh";

    public bool Enabled { get; set; }

    public string Server { get; set; } = DefaultServer;

    /// <summary>Anyone who knows the topic can read it on a public server, so it's long and random by default.</summary>
    public string Topic { get; set; } = NewTopic();

    /// <summary>Optional access token for protected topics / self-hosted servers.</summary>
    public string? AccessToken { get; set; }

    public static string NewTopic() =>
        "spacedeck-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
