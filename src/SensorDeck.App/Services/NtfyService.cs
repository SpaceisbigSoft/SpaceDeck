using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace SensorDeck.App.Services;

/// <summary>
/// Sends alerts to a phone through ntfy (https://ntfy.sh, or a self-hosted server): a plain HTTP POST to
/// <c>server/topic</c>; the ntfy app subscribed to the topic shows it. Opt-in: alert text leaves the PC.
/// </summary>
public sealed class NtfyService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public NtfyService(AppSettings settings)
    {
        _settings = settings;
    }

    public NtfySettings Settings => _settings.Ntfy;

    /// <summary>The topic's web address (also what the phone app subscribes to).</summary>
    public string TopicUrl => $"{Settings.Server.TrimEnd('/')}/{Uri.EscapeDataString(Settings.Topic)}";

    /// <summary>Sends if enabled; never throws (failures are returned for the Settings test button).</summary>
    public Task<string?> PublishAsync(string title, string message, bool critical, bool force = false) =>
        !force && !Settings.Enabled ? Task.FromResult<string?>(null) : SendAsync(title, message, critical);

    public void Dispose() => _http.Dispose();

    private async Task<string?> SendAsync(string title, string message, bool critical)
    {
        if (string.IsNullOrWhiteSpace(Settings.Topic) || !Uri.TryCreate(TopicUrl, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return "Set a server address (https://…) and a topic.";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // ntfy turns bodies over 4 KB into attachments: keep it a message.
            Content = new StringContent(message.Length > 3500 ? message[..3500] + "…" : message, Encoding.UTF8, "text/plain"),
        };

        // Header values must be ASCII-safe: ntfy reads RFC 2047 encoded titles.
        request.Headers.TryAddWithoutValidation("Title", $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(title))}?=");
        request.Headers.TryAddWithoutValidation("Priority", critical ? "urgent" : "high");
        request.Headers.TryAddWithoutValidation("Tags", critical ? "rotating_light" : "warning");
        if (!string.IsNullOrWhiteSpace(Settings.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.AccessToken.Trim());
        }

        try
        {
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? null : $"ntfy answered {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return $"Couldn't reach {url.Host}: {ex.Message}";
        }
    }
}
