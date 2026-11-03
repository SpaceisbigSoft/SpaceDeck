using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpaceDeck.Core.Updates;

/// <summary>A file attached to a release (GitHub "asset").</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>A published release from the update feed.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Title, string Notes, string Url, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The installer (<c>SpaceDeck-&lt;version&gt;-x64.msi</c>), when the release has one with a safe name and address.</summary>
    public ReleaseAsset? Installer => Assets.FirstOrDefault(a => UpdateDownloadPolicy.IsInstallerName(a.Name));

    /// <summary>The checksum file (<c>SHA256SUMS.txt</c>) that the installer download is checked against.</summary>
    public ReleaseAsset? Checksums => Assets.FirstOrDefault(a => a.Name.Equals(UpdateDownloadPolicy.ChecksumFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the release carries both files that "Download and install" needs.</summary>
    public bool CanInstall => Installer is not null && Checksums is not null;
}

/// <summary>Reads GitHub's "latest release" answer.</summary>
public static partial class ReleaseFeed
{
    private const int MaxNotesLength = 4000;

    /// <summary>
    /// Reads a GitHub "release" object. Drafts and pre-releases are ignored (null), as are tags without a version.
    /// Assets are kept only when their download address passes <see cref="UpdateDownloadPolicy.IsAllowedAddress"/>.
    /// </summary>
    public static ReleaseInfo? Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            || (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True))
        {
            return null;
        }

        var tag = GetString(root, "tag_name");
        if (tag is null || ParseVersion(tag) is not { } version)
        {
            return null;
        }

        var url = GetString(root, "html_url");
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps)
        {
            url = ""; // only https links are opened
        }

        var title = GetString(root, "name") is { Length: > 0 } name ? name : $"SpaceDeck {version}";
        var notes = GetString(root, "body") ?? "";
        if (notes.Length > MaxNotesLength)
        {
            notes = notes[..MaxNotesLength] + "…";
        }

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || GetString(item, "name") is not { Length: > 0 } assetName
                    || GetString(item, "browser_download_url") is not { } address
                    || !Uri.TryCreate(address, UriKind.Absolute, out var assetUrl)
                    || !UpdateDownloadPolicy.IsAllowedAddress(assetUrl))
                {
                    continue;
                }

                var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) && n > 0 ? n : 0;
                assets.Add(new ReleaseAsset(assetName, assetUrl, size));
            }
        }

        return new ReleaseInfo(version, tag, title, notes.Trim(), url, assets);
    }

    /// <summary>"v1.2.3", "1.2", "1.2.3-beta.1" → 1.2.3 (suffixes dropped).</summary>
    public static Version? ParseVersion(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(['-', '+', ' ']);
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

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Reads a <c>SHA256SUMS.txt</c> file: lines of "&lt;64 hex digits&gt;  &lt;file name&gt;" (sha256sum format).</summary>
public static partial class Sha256Sums
{
    /// <summary>The lower-case SHA-256 listed for <paramref name="fileName"/>, or null when it isn't listed.</summary>
    public static string? Find(string text, string fileName)
    {
        foreach (var raw in text.Split('\n'))
        {
            var match = LineRegex().Match(raw.Trim());
            if (match.Success && match.Groups["name"].Value.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["hash"].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    // "<hash>  name" (text mode) or "<hash> *name" (binary mode).
    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64}) [ *](?<name>\S.*)$")]
    private static partial Regex LineRegex();
}

/// <summary>What the updater may download, from where, and how much.</summary>
public static partial class UpdateDownloadPolicy
{
    public const string ChecksumFileName = "SHA256SUMS.txt";

    /// <summary>Upper bound for the installer (the real one is about 90 MB).</summary>
    public const long MaxInstallerBytes = 600L * 1024 * 1024;

    /// <summary>Upper bound for the checksum file.</summary>
    public const int MaxChecksumBytes = 64 * 1024;

    // github.com serves the download link and redirects to one of its file hosts.
    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
    ];

    /// <summary>https on GitHub's release hosts only (checked for the link and again for where a redirect ends up).</summary>
    public static bool IsAllowedAddress(Uri address) =>
        address.IsAbsoluteUri
        && address.Scheme == Uri.UriSchemeHttps
        && address.IsDefaultPort
        && string.IsNullOrEmpty(address.UserInfo)
        && AllowedHosts.Contains(address.IdnHost, StringComparer.OrdinalIgnoreCase);

    /// <summary>"SpaceDeck-0.2.0-x64.msi": a plain file name the updater can save without any path in it.</summary>
    public static bool IsInstallerName(string name) => InstallerNameRegex().IsMatch(name);

    [GeneratedRegex(@"^SpaceDeck-[0-9A-Za-z.\-]{1,40}-x64\.msi$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallerNameRegex();
}
