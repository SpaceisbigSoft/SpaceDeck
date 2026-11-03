using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.Core.Updates;

namespace SpaceDeck.App.Services;

/// <summary>
/// Update check and install. The check (opt-in daily, or "Check now") asks the release feed (GitHub's "latest release"
/// API) whether a newer SpaceDeck exists and shows what's new; it sends nothing but a plain request.
/// <para>
/// "Download and install" is only ever started by the user: it downloads the release's MSI from GitHub, checks its size
/// and SHA-256 against the release's SHA256SUMS.txt, asks once more, then closes SpaceDeck and opens the installer
/// (which asks Windows for administrator approval). The MSI upgrades in place and keeps settings and history.
/// </para>
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    /// <summary>The public release feed. "UpdateFeedUrl" in settings.json overrides it (for testing).</summary>
    public const string DefaultFeedUrl = "https://api.github.com/repos/SpaceisbigSoft/SpaceDeck/releases/latest";

    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);
    private const int MaxResponseBytes = 1024 * 1024;

    private readonly AppSettings _settings;
    private readonly TrayService _tray;
    private bool _checking; // UI thread only (both callers are on it, and the check never leaves it)
    private bool _automaticRunning; // UI thread only
    private FileStream? _heldInstaller; // kept open (no writes, no delete) from the hash check until SpaceDeck exits

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private ReleaseInfo? _available;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalling;

    [ObservableProperty]
    private string? _installStatus;

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

    /// <summary>A build running from the source tree updates with git, not with the installer.</summary>
    public static bool IsDevelopmentBuild => CollectorServiceStatus.SourceTreeRoot() is not null;

    /// <summary>"Download and install" is offered: a newer release with an installer and checksums, in an installed SpaceDeck.</summary>
    public bool CanInstall => Available is { CanInstall: true } && !IsInstalling && !IsDevelopmentBuild && !AppMode.IsReplay;

    /// <summary>Where downloaded installers go; emptied at every start.</summary>
    public static string DownloadFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceDeck", "updates");

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

    /// <summary>Deletes installers left from earlier updates (Windows keeps its own copy of what it installed).</summary>
    public static void CleanUpDownloads()
    {
        try
        {
            var folder = new DirectoryInfo(DownloadFolder);
            if (!folder.Exists || folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            foreach (var file in folder.EnumerateFiles())
            {
                try
                {
                    file.Delete(); // a link is deleted itself, never what it points to
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // in use or locked: next start
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // nothing to clean up
        }
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
            Status = "There's no release feed set.";
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
            using var http = CreateClient(TimeSpan.FromSeconds(15), MaxResponseBytes);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
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

            var release = ReleaseFeed.Parse(bytes);
            if (release is null)
            {
                Status = "Couldn't read the release feed.";
                return;
            }

            if (release.Version > CurrentVersion)
            {
                Available = release;
                Status = $"SpaceDeck {release.Version} is available (you have {CurrentVersion}).";
                if (notify)
                {
                    _tray.Notify("SpaceDeck update available", $"Version {release.Version} is out. Open About to see what's new and install it.", critical: false);
                }
            }
            else
            {
                Available = null;
                Status = $"You're up to date ({CurrentVersion}). Checked {DateTime.Now.ToString("g", CultureInfo.CurrentCulture)}.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            Status = "Couldn't reach the update server. Check your connection and try again.";
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// Downloads the available release's installer, checks it against SHA256SUMS.txt, asks to confirm, then opens it and
    /// closes SpaceDeck. Never throws; progress and problems are in <see cref="InstallStatus"/>. UI thread.
    /// </summary>
    public async Task DownloadAndInstallAsync()
    {
        if (!CanInstall || Available is not { } release || release.Installer is not { } installer || release.Checksums is not { } sums)
        {
            return;
        }

        IsInstalling = true;
        string? path = null;
        try
        {
            InstallStatus = "Getting the checksum…";
            var expected = await DownloadChecksumAsync(sums.Url, installer.Name);
            if (expected is null)
            {
                InstallStatus = "This release doesn't list a checksum for its installer, so it can't be installed from here. Use the download page instead.";
                return;
            }

            Directory.CreateDirectory(DownloadFolder);
            path = Path.Combine(DownloadFolder, installer.Name); // a plain file name (UpdateDownloadPolicy.IsInstallerName)
            var actual = await DownloadInstallerAsync(installer, path);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                TryDelete(path);
                InstallStatus = "The download didn't match the release's checksum, so it was deleted. Try again later, or use the download page.";
                return;
            }

            // Hold the file open (others may read, nobody may change or delete it) and check it once more through that
            // handle, so what Windows installs is exactly what was checked.
            _heldInstaller = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(_heldInstaller)), expected, StringComparison.Ordinal))
            {
                ReleaseHeldInstaller();
                TryDelete(path);
                InstallStatus = "The downloaded installer changed after it was checked, so it was deleted.";
                return;
            }

            InstallStatus = $"SpaceDeck {release.Version} is downloaded and checked.";
            var answer = System.Windows.MessageBox.Show(
                $"SpaceDeck {release.Version} is ready to install.\n\nSpaceDeck will close and the installer will open; Windows asks for administrator approval. Your settings, dashboards and history are kept.\n\nInstall now?",
                "Install SpaceDeck update", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.OK)
            {
                ReleaseHeldInstaller();
                InstallStatus = $"SpaceDeck {release.Version} is downloaded. Click Download and install again when you're ready.";
                return;
            }

            // msiexec from System32 by full path (never whatever "msiexec" a search path finds); full installer UI, so the
            // user sees what happens and can reopen SpaceDeck from the last page.
            var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            var start = new ProcessStartInfo(msiexec) { UseShellExecute = false };
            start.ArgumentList.Add("/i");
            start.ArgumentList.Add(path);
            using (Process.Start(start))
            {
            }

            AppLifetime.IsExiting = true;
            System.Windows.Application.Current?.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ReleaseHeldInstaller();
            if (path is not null)
            {
                TryDelete(path + ".partial");
            }

            InstallStatus = ex is System.ComponentModel.Win32Exception
                ? "Couldn't open the installer. It's in " + DownloadFolder + " if you want to run it yourself."
                : "The download failed (" + ex.Message + "). Check your connection and try again.";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>The SHA-256 the release lists for <paramref name="fileName"/>, or null.</summary>
    private static async Task<string?> DownloadChecksumAsync(Uri address, string fileName)
    {
        using var http = CreateClient(TimeSpan.FromSeconds(30), UpdateDownloadPolicy.MaxChecksumBytes);
        using var response = await http.GetAsync(address);
        EnsureAllowedResponse(response);
        var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        return Sha256Sums.Find(text, fileName);
    }

    /// <summary>Streams the installer to <paramref name="path"/> (via a .partial file) and returns its lower-case SHA-256.</summary>
    private async Task<string> DownloadInstallerAsync(ReleaseAsset installer, string path)
    {
        using var http = CreateClient(TimeSpan.FromMinutes(30), null);
        using var response = await http.GetAsync(installer.Url, HttpCompletionOption.ResponseHeadersRead);
        EnsureAllowedResponse(response);

        var length = response.Content.Headers.ContentLength;
        if (length > UpdateDownloadPolicy.MaxInstallerBytes || (installer.Size > 0 && length is { } l && l != installer.Size))
        {
            throw new InvalidOperationException("the installer's size doesn't match the release");
        }

        var total = length ?? installer.Size;
        var partial = path + ".partial";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            var lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                received += read;
                if (received > UpdateDownloadPolicy.MaxInstallerBytes || (installer.Size > 0 && received > installer.Size))
                {
                    throw new InvalidOperationException("the installer is larger than the release says");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read));
                var percent = total > 0 ? (int)(received * 100 / total) : -1;
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    InstallStatus = percent >= 0 ? $"Downloading… {percent} %" : $"Downloading… {received / (1024 * 1024)} MB";
                }
            }

            if (installer.Size > 0 && received != installer.Size)
            {
                throw new InvalidOperationException("the download ended early");
            }
        }

        File.Move(partial, path, overwrite: true);
        InstallStatus = "Checking the download…";
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The request must succeed and end on one of GitHub's release hosts (after any redirects).</summary>
    private static void EnsureAllowedResponse(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } final || !UpdateDownloadPolicy.IsAllowedAddress(final))
        {
            throw new InvalidOperationException("the download was sent to an unexpected address");
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout, long? maxBuffer)
    {
        var http = new HttpClient { Timeout = timeout };
        if (maxBuffer is { } max)
        {
            http.MaxResponseContentBufferSize = max;
        }

        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpaceDeck", CurrentVersion.ToString()));
        return http;
    }

    private void ReleaseHeldInstaller()
    {
        _heldInstaller?.Dispose();
        _heldInstaller = null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // cleaned up at the next start
        }
    }
}
