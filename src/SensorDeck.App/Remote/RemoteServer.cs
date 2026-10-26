using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SensorDeck.App.Services;

namespace SensorDeck.App.Remote;

/// <summary>
/// The network dashboard: a small web server (Kestrel) that shows the saved dashboards, live, in any browser on the
/// home network — a Steam Deck, a phone, another PC. Off by default; a device pairs once with the PIN shown in
/// Settings and then sends its token. Values are pushed every reading over Server-Sent Events.
/// </summary>
/// <remarks>
/// Hardening: only private/link-local/loopback clients, Host must be an IP or this PC's name (DNS rebinding), small
/// request bodies, a cap on connections and live streams, PIN attempts limited per client and overall (too many
/// failures lock pairing until "New PIN"; a successful pairing resets the counts), pairing only accepts JSON (a web page can't send that
/// cross-site without a CORS preflight, which this server never answers), and the PIN changes after each successful
/// pairing. Responses forbid framing and sniffing, and the page's CSP only lets it talk to this server. Request
/// handlers never wait on the UI thread for authorization.
/// </remarks>
public sealed partial class RemoteServer : ObservableObject, IDisposable
{
    private const int MaxFailuresPerClient = 5;
    private const int MaxFailuresTotal = 20;

    /// <summary>Kestrel connection cap: plenty for a few phones and tablets, bounded against floods.</summary>
    private const int MaxConnections = 100;

    /// <summary>Open event streams at once (every paired browser tab holds one).</summary>
    private const int MaxStreams = 32;

    /// <summary>Remote fan profile switches at most this often (each may show a UAC prompt on the PC).</summary>
    private static readonly TimeSpan MinSwitchInterval = TimeSpan.FromSeconds(2);

    /// <summary>The web client is one inline page; it only ever talks to this server.</summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self'; " +
        "manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private readonly AppSettings _settings;
    private readonly SensorStore _store;
    private readonly FanProfileService _fans;
    private readonly DialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _clientsLock = new();
    private readonly List<(RemoteDevice Device, Channel<string> Channel)> _clients = [];
    private readonly Lock _pairLock = new();
    private readonly Dictionary<string, (int Failures, DateTime Until)> _pairFailures = new(StringComparer.Ordinal);
    private int _totalFailures;
    private volatile IReadOnlyList<RemoteDevice> _devices = [];
    private volatile IReadOnlySet<string> _hostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastSwitchUtc; // UI thread only
    private WebApplication? _app;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _status = "Off";

    [ObservableProperty]
    private int _clientCount;

    [ObservableProperty]
    private bool _pairingLocked;

    public RemoteServer(AppSettings settings, SensorStore store, FanProfileService fans, DialogService dialogs)
    {
        _settings = settings;
        _store = store;
        _fans = fans;
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;
        store.ValuesApplied += OnValues;
        store.CatalogApplied += Reload;
        Themes.SkinManager.SkinChanged += Reload;
        RefreshDevices();
    }

    public RemoteSettings Settings => _settings.Remote;

    /// <summary>Raised when the paired devices change (Settings refreshes its list).</summary>
    public event Action? DevicesChanged;

    /// <summary>"http://192.168.1.20:8187" for every local IPv4 address (what to type on the Steam Deck / phone).</summary>
    public IReadOnlyList<string> Addresses
    {
        get
        {
            try
            {
                return
                [
                    .. NetworkInterface.GetAllNetworkInterfaces()
                        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
                                    && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        .Select(a => $"http://{a.Address}:{Settings.Port}"),
                ];
            }
            catch (NetworkInformationException)
            {
                return [];
            }
        }
    }

    /// <summary>Starts or stops the server to match the settings (call after changing them). Calls are serialized.</summary>
    public async Task ApplyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            if (!Settings.Enabled)
            {
                Status = "Off";
                return;
            }

            WebApplication? app = null;
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    ContentRootPath = AppContext.BaseDirectory,
                    EnvironmentName = Environments.Production, // never the developer exception page
                });
                builder.Logging.ClearProviders();
                builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
                builder.WebHost.ConfigureKestrel(o =>
                {
                    o.ListenAnyIP(Math.Clamp(Settings.Port, 1024, 65535));
                    o.Limits.MaxRequestBodySize = 8 * 1024;
                    o.Limits.MaxConcurrentConnections = MaxConnections;
                });
                _hostNames = HostNames();
                app = builder.Build();
                app.Use(Guard);
                Map(app);
                await app.StartAsync();
                _app = app;
                IsRunning = true;
                Status = RunningStatus;
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or UnauthorizedAccessException)
            {
                Status = $"Couldn't start on port {Settings.Port}: {ex.Message}";
                if (app is not null)
                {
                    await app.DisposeAsync();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string RunningStatus =>
        $"Running on port {Settings.Port}. Windows may ask to allow SensorDeck through the firewall: allow it on private networks.";

    /// <summary>A new PIN (paired devices stay paired); also unlocks pairing after too many wrong PINs.</summary>
    public void NewPin()
    {
        Settings.Pin = RemoteSettings.NewPin();
        _settings.Save();
        ResetPairingLimits();
        OnPropertyChanged(nameof(Settings));
    }

    /// <summary>A new PIN makes earlier wrong guesses meaningless, so the counters start over. UI thread.</summary>
    private void ResetPairingLimits()
    {
        lock (_pairLock)
        {
            _pairFailures.Clear();
            _totalFailures = 0;
        }

        if (PairingLocked)
        {
            PairingLocked = false;
            if (IsRunning)
            {
                Status = RunningStatus;
            }
        }
    }

    /// <summary>Unpairs a device and ends its open connections.</summary>
    public void Forget(RemoteDevice device)
    {
        Settings.Devices.Remove(device);
        _settings.Save();
        RefreshDevices();
        lock (_clientsLock)
        {
            foreach (var client in _clients.Where(c => ReferenceEquals(c.Device, device)))
            {
                client.Channel.Writer.TryComplete();
            }
        }

        DevicesChanged?.Invoke();
    }

    /// <summary>App exit: stop the server off the UI thread (handlers never wait for it) with a short deadline.</summary>
    public void Dispose()
    {
        CloseClients();
        if (_app is not { } app)
        {
            return;
        }

        _app = null;
        try
        {
            Task.Run(async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await app.StopAsync(deadline.Token).ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }).Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Exiting anyway.
        }
    }

    private async Task StopCoreAsync()
    {
        CloseClients();
        if (_app is { } app)
        {
            _app = null;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await app.StopAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                // Forced.
            }
            finally
            {
                await app.DisposeAsync();
            }
        }

        IsRunning = false;
    }

    // ── Routes ──────────────────────────────────────────────────────────

    private void Map(WebApplication app)
    {
        app.MapGet("/", ctx => ServeFile(ctx, "index.html", "text/html; charset=utf-8"));
        app.MapGet("/index.html", ctx => ServeFile(ctx, "index.html", "text/html; charset=utf-8"));
        app.MapGet("/manifest.webmanifest", ctx => ServeFile(ctx, "manifest.webmanifest", "application/manifest+json"));
        app.MapGet("/icon.svg", ctx => ServeFile(ctx, "icon.svg", "image/svg+xml"));
        app.MapPost("/api/pair", ctx => PairAsync(ctx));
        app.MapGet("/api/state", ctx => Authorized(ctx, StateAsync));
        app.MapGet("/api/history", ctx => Authorized(ctx, HistoryAsync));
        app.MapGet("/api/stream", ctx => Authorized(ctx, StreamAsync, allowQueryToken: true));
        app.MapPost("/api/profile", ctx => Authorized(ctx, ProfileAsync));
    }

    /// <summary>
    /// Home-network clients only, and a Host that is an IP address or one of this PC's own names (blocks DNS
    /// rebinding). Every response also forbids framing (clickjacking the profile buttons) and content sniffing.
    /// </summary>
    private Task Guard(HttpContext ctx, Func<Task> next)
    {
        var headers = ctx.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;

        if (ctx.Connection.RemoteIpAddress is not { } remote || !IsLocalNetwork(remote))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        var host = ctx.Request.Host.Host;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out _) && !_hostNames.Contains(host))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        return next();
    }

    /// <summary>
    /// "localhost", this PC's name, and the name with a home-network suffix ("mypc.local", "mypc.lan"). Not
    /// "mypc." + anything: "mypc.attacker.example" would let a web page rebind. The network's own DNS suffixes aren't
    /// added either: they come from DHCP, so a hostile DHCP server could pick one. IP addresses always work.
    /// </summary>
    private static HashSet<string> HostNames()
    {
        var machine = Environment.MachineName;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", machine };
        string[] suffixes = ["local", "lan", "home", "home.arpa", "localdomain", "internal"];
        foreach (var suffix in suffixes)
        {
            names.Add($"{machine}.{suffix}");
        }

        return names;
    }

    private static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
    }

    private static async Task ServeFile(HttpContext ctx, string name, string contentType)
    {
        await using var stream = typeof(RemoteServer).Assembly.GetManifestResourceStream($"SensorDeck.Web.{name}");
        if (stream is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.ContentType = contentType;
        ctx.Response.Headers.CacheControl = "no-cache";
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    /// <summary>Rate-limit key: the IPv4 address, or the /64 network of an IPv6 address (one LAN host owns many).</summary>
    private static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "?";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return Convert.ToHexString(address.GetAddressBytes(), 0, 8);
        }

        return address.ToString();
    }

    private async Task PairAsync(HttpContext ctx)
    {
        // JSON only: a cross-site form or no-cors fetch can't send it, so other web pages can't burn PIN attempts.
        if (!ctx.Request.HasJsonContentType())
        {
            ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        var key = ClientKey(ctx.Connection.RemoteIpAddress);

        // Count the attempt before reading anything, so parallel requests can't all slip past the limit.
        lock (_pairLock)
        {
            var now = DateTime.UtcNow;
            (int Failures, DateTime Until) entry = _pairFailures.TryGetValue(key, out var f) && now < f.Until ? f : (0, now);
            if (PairingLockedNow())
            {
                ctx.Response.StatusCode = StatusCodes.Status423Locked; // until "New PIN" on the PC: waiting won't help
                return;
            }

            if (entry.Failures >= MaxFailuresPerClient)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            _pairFailures[key] = (entry.Failures + 1, now.AddMinutes(1));
            _totalFailures++;
        }

        PairRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<PairRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            request = null;
        }

        var pin = Settings.Pin;
        if (request?.Pin is not { } given || pin.Length == 0
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given.Trim()), Encoding.UTF8.GetBytes(pin)))
        {
            if (PairingLockedNow())
            {
                _ = _dispatcher.BeginInvoke(() =>
                {
                    PairingLocked = true;
                    Status = "Pairing is locked after too many wrong PINs. Press \"New PIN\" to pair a device. Paired devices still work.";
                });
            }

            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var trimmed = request.Name?.Trim() ?? "";
        var name = trimmed.Length == 0 ? $"Device at {ctx.Connection.RemoteIpAddress}" : trimmed[..Math.Min(40, trimmed.Length)];
        var paired = await _dispatcher.InvokeAsync(
            () =>
            {
                // Two requests with the same PIN at once: only the first pairs (the PIN changes here, on the UI thread).
                if (!string.Equals(Settings.Pin, pin, StringComparison.Ordinal))
                {
                    return false;
                }

                Settings.Devices.Add(new RemoteDevice { Name = name, Token = token, Paired = DateTimeOffset.Now, LastSeen = DateTimeOffset.Now });
                Settings.Pin = RemoteSettings.NewPin(); // one PIN, one device
                _settings.Save();
                ResetPairingLimits();
                RefreshDevices();
                OnPropertyChanged(nameof(Settings));
                DevicesChanged?.Invoke();
                return true;
            },
            DispatcherPriority.Normal,
            ctx.RequestAborted);
        if (!paired)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { token }), ctx.RequestAborted);
    }

    private bool PairingLockedNow()
    {
        lock (_pairLock)
        {
            return _totalFailures >= MaxFailuresTotal;
        }
    }

    /// <summary>
    /// Runs <paramref name="handler"/> only for a paired device: token in the Authorization header (or ?token= for
    /// the event stream, which browsers can't give headers). Checked against a snapshot, without the UI thread.
    /// </summary>
    private async Task Authorized(HttpContext ctx, Func<HttpContext, RemoteDevice, Task> handler, bool allowQueryToken = false)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..]
            : allowQueryToken ? ctx.Request.Query["token"].ToString()
            : "";
        RemoteDevice? device = null;
        if (token.Length > 0)
        {
            var given = Encoding.UTF8.GetBytes(token);
            device = _devices.FirstOrDefault(d => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(d.Token), given));
        }

        if (device is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (device.LastSeen is not { } seen || DateTimeOffset.Now - seen > TimeSpan.FromMinutes(10))
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                device.LastSeen = DateTimeOffset.Now;
                _settings.Save();
                DevicesChanged?.Invoke();
            });
        }

        await handler(ctx, device);
    }

    private async Task StateAsync(HttpContext ctx, RemoteDevice device)
    {
        var json = await _dispatcher.InvokeAsync(
            () =>
            {
                if (_fans.Install is null)
                {
                    _fans.Refresh();
                }

                return RemoteSnapshot.State(_store, _dialogs.AllDashboards(), _fans, Settings);
            },
            DispatcherPriority.Normal,
            ctx.RequestAborted);
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsync(json, ctx.RequestAborted);
    }

    private async Task HistoryAsync(HttpContext ctx, RemoteDevice device)
    {
        var ids = ctx.Request.Query["ids"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        var seconds = int.TryParse(ctx.Request.Query["seconds"], out var s) ? s : 300;
        var json = await _dispatcher.InvokeAsync(() => RemoteSnapshot.History(_store, ids, seconds), DispatcherPriority.Normal, ctx.RequestAborted);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(json, ctx.RequestAborted);
    }

    /// <summary>Server-Sent Events: a "values" frame per reading, "reload" when dashboards/skin/sensors change.</summary>
    private async Task StreamAsync(HttpContext ctx, RemoteDevice device)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
        var stopping = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        lock (_clientsLock)
        {
            if (_clients.Count >= MaxStreams)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            _clients.Add((device, channel));
        }

        try
        {
            _ = _dispatcher.BeginInvoke(UpdateClientCount);
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            await ctx.Response.WriteAsync("retry: 3000\n\n", cancel.Token);
            await ctx.Response.Body.FlushAsync(cancel.Token);
            await foreach (var message in channel.Reader.ReadAllAsync(cancel.Token))
            {
                await ctx.Response.WriteAsync(message, cancel.Token);
                await ctx.Response.Body.FlushAsync(cancel.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Browser went away, or the server is stopping.
        }
        catch (IOException)
        {
            // Connection dropped.
        }
        finally
        {
            lock (_clientsLock)
            {
                _clients.RemoveAll(c => ReferenceEquals(c.Channel, channel));
            }

            _ = _dispatcher.BeginInvoke(UpdateClientCount);
        }
    }

    private async Task ProfileAsync(HttpContext ctx, RemoteDevice device)
    {
        if (!Settings.AllowControl)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        ProfileRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ProfileRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            request = null;
        }

        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var name = request.Name.Trim();
        var result = await _dispatcher.InvokeAsync<Integrations.FanControl.SwitchResult?>(
            () =>
            {
                // A paired device must not be able to flood the PC with FanControl starts / UAC prompts.
                var now = DateTime.UtcNow;
                if (now - _lastSwitchUtc < MinSwitchInterval)
                {
                    return null;
                }

                _lastSwitchUtc = now;
                return _fans.Switch(name);
            },
            DispatcherPriority.Normal,
            ctx.RequestAborted);
        ctx.Response.StatusCode = result is null ? StatusCodes.Status429TooManyRequests
            : result.Outcome is Integrations.FanControl.SwitchOutcome.Sent or Integrations.FanControl.SwitchOutcome.StartedWithConfig
                ? StatusCodes.Status204NoContent
                : StatusCodes.Status409Conflict;
    }

    // ── Push ────────────────────────────────────────────────────────────

    private void OnValues(long timestamp)
    {
        if (ClientCount > 0)
        {
            Broadcast($"event: values\ndata: {RemoteSnapshot.Values(_store, timestamp)}\n\n");
        }
    }

    private void Reload()
    {
        if (ClientCount > 0)
        {
            Broadcast("event: reload\ndata: {}\n\n");
        }
    }

    private void Broadcast(string message)
    {
        lock (_clientsLock)
        {
            foreach (var client in _clients)
            {
                client.Channel.Writer.TryWrite(message);
            }
        }
    }

    private void CloseClients()
    {
        lock (_clientsLock)
        {
            foreach (var client in _clients)
            {
                client.Channel.Writer.TryComplete();
            }

            _clients.Clear();
        }

        ClientCount = 0;
    }

    private void UpdateClientCount()
    {
        lock (_clientsLock)
        {
            ClientCount = _clients.Count;
        }
    }

    /// <summary>The token snapshot request handlers check (replaced on the UI thread when devices change).</summary>
    private void RefreshDevices() => _devices = [.. Settings.Devices];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record PairRequest(string? Pin, string? Name);

    private sealed record ProfileRequest(string? Name);
}
