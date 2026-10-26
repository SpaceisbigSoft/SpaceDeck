using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SensorDeck.App.Services;

/// <summary>A newer release found by the update check.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Title, string Notes, string Url);

/// <summary>
/// Opt-in update check: asks the release feed (GitHub's "latest release" API) whether a newer SensorDeck exists and
/// shows what's new. It never downloads or installs anything, and sends nothing but a plain request.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    /// <summary>
    /// The public release feed. Empty until the first public release (Phase 17/11); until then a feed can be set
    /// with "UpdateFeedUrl" in settings.json.
    /// </summary>
    public const string DefaultFeedUrl = "";

    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly char[] VersionSuffixStart = ['-', '+', ' '];

    private readonly AppSettings _settings;
    private readonly TrayService _tray;
    private bool _checking; // UI thread only (both callers are on it, and the check never leaves it)
    private bool _automaticRunning; // UI thread only

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private UpdateInfo? _available;

    public UpdateService(AppSettings settings, TrayService tray)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _tray = tray;
    }

    public string FeedUrl => string.IsNullOrWhiteSpace(_settings.UpdateFeedUrl) ? DefaultFeedUrl : _settings.UpdateFeedUrl.Trim();

    public bool HasFeed => FeedUrl.Length > 0;

    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0))
        : new Version(0, 0, 0);

    /// <summary>
    /// At startup (or when turned on): checks when the last check is older than <see cref="CheckEvery"/>, then keeps
    /// checking that way while the app runs (it can sit in the tray for weeks); a failed check is retried hourly.
    /// Call on the UI thread.
    /// </summary>
    public void StartAutomaticCheck()
    {
        if (!_settings.UpdateCheckEnabled || !HasFeed || AppMode.IsReplay || _automaticRunning)
        {
            return;
        }

        _automaticRunning = true;
        _ = AutomaticCheckLoopAsync();
    }

    /// <summary>Runs on the UI thread (no ConfigureAwait(false)): settings, Status and the tray are touched there only.
    /// Ends when the check is turned off.</summary>
    private async Task AutomaticCheckLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30)); // let startup finish first
            while (_settings.UpdateCheckEnabled && HasFeed)
            {
                if (_settings.LastUpdateCheckUtc is not { } last || DateTime.UtcNow - last >= CheckEvery)
                {
                    await CheckAsync(notify: true);
                }

                await Task.Delay(TimeSpan.FromHours(1));
            }
        }
        finally
        {
            _automaticRunning = false;
        }
    }

    /// <summary>Checks now. Never throws; the outcome is in <see cref="Status"/> and <see cref="Available"/>.</summary>
    public async Task CheckAsync(bool notify = false)
    {
        if (_checking)
        {
            return;
        }

        if (!HasFeed)
        {
            Status = "There's no release feed yet; it's added with the first public release.";
            return;
        }

        if (!Uri.TryCreate(FeedUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Status = "The update feed must be an https:// address.";
            return;
        }

        _checking = true;
        Status = "Checking…";
        try
        {
            // The client buffers the answer and fails past the limit, so a feed can't stream an endless body into memory.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = MaxResponseBytes };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SensorDeck", CurrentVersion.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request); // resume on the UI thread: Save, Status and the tray need it
            if (!response.IsSuccessStatusCode)
            {
                Status = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "No releases published yet."
                    : $"The update server answered {(int)response.StatusCode}; try again later.";
                return;
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                Status = "The update server's answer was unexpectedly large.";
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > MaxResponseBytes)
            {
                Status = "The update server's answer was unexpectedly large.";
                return;
            }

            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settings.Save();

            var release = Parse(bytes);
            if (release is null)
            {
                Status = "Couldn't read the release feed.";
                return;
            }

            if (release.Version > CurrentVersion)
            {
                Available = release;
                Status = $"SensorDeck {release.Version} is available (you have {CurrentVersion}).";
                if (notify)
                {
                    _tray.Notify("SensorDeck update available", $"Version {release.Version} is out. See About for what's new.", critical: false);
                }
            }
            else
            {
                Available = null;
                Status = $"You're up to date ({CurrentVersion}). Checked {DateTime.Now.ToString("g", CultureInfo.CurrentCulture)}.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or System.IO.IOException or InvalidOperationException)
        {
            Status = "Couldn't reach the update server. Check your connection and try again.";
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Reads a GitHub "release" object. Drafts and pre-releases are ignored (null).</summary>
    internal static UpdateInfo? Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            || (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True))
        {
            return null;
        }

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (tag is null || ParseVersion(tag) is not { } version)
        {
            return null;
        }

        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps)
        {
            url = ""; // only https links are opened
        }

        var title = root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name ? name : $"SensorDeck {version}";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        if (notes.Length > 4000)
        {
            notes = notes[..4000] + "…";
        }

        return new UpdateInfo(version, tag, title, notes.Trim(), url);
    }

    /// <summary>"v1.2.3", "1.2", "1.2.3-beta.1" → 1.2.3 (suffixes dropped).</summary>
    internal static Version? ParseVersion(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(VersionSuffixStart);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        if (!Version.TryParse(text.Contains('.', StringComparison.Ordinal) ? text : text + ".0", out var v))
        {
            return null;
        }

        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }
}
