using System.Text;
using SpaceDeck.Core.Updates;
using Xunit;

namespace SpaceDeck.Core.Tests;

public class UpdateFeedTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static ReleaseInfo? Parse(string json) => ReleaseFeed.Parse(Encoding.UTF8.GetBytes(json));

    private static string Release(string assets, string tag = "v0.2.0", bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}", "name": "SpaceDeck 0.2.0", "body": "Notes",
          "html_url": "https://github.com/SpaceisbigSoft/SpaceDeck/releases/tag/{{tag}}",
          "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [ {{assets}} ]
        }
        """;

    private const string MsiAsset = """
        { "name": "SpaceDeck-0.2.0-x64.msi", "size": 93000000,
          "browser_download_url": "https://github.com/SpaceisbigSoft/SpaceDeck/releases/download/v0.2.0/SpaceDeck-0.2.0-x64.msi" }
        """;

    private const string SumsAsset = """
        { "name": "SHA256SUMS.txt", "size": 90,
          "browser_download_url": "https://github.com/SpaceisbigSoft/SpaceDeck/releases/download/v0.2.0/SHA256SUMS.txt" }
        """;

    [Fact]
    public void Release_with_installer_and_checksums_can_be_installed()
    {
        var release = Parse(Release(MsiAsset + "," + SumsAsset));

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.True(release.CanInstall);
        Assert.Equal("SpaceDeck-0.2.0-x64.msi", release.Installer!.Name);
        Assert.Equal(93000000, release.Installer.Size);
        Assert.Equal("SHA256SUMS.txt", release.Checksums!.Name);
    }

    [Fact]
    public void Release_without_checksums_cannot_be_installed()
    {
        var release = Parse(Release(MsiAsset));
        Assert.NotNull(release);
        Assert.False(release.CanInstall);
    }

    [Fact]
    public void Prereleases_and_drafts_are_ignored()
    {
        Assert.Null(Parse(Release(MsiAsset, "v0.3.0-beta.1", prerelease: true)));
        Assert.Null(Parse("""{ "tag_name": "v1.0.0", "draft": true }"""));
    }

    [Theory]
    [InlineData("http://github.com/x/SpaceDeck-0.2.0-x64.msi")]                  // not https
    [InlineData("https://evil.example.com/SpaceDeck-0.2.0-x64.msi")]           // other host
    [InlineData("https://github.com.evil.example/SpaceDeck-0.2.0-x64.msi")]    // look-alike host
    [InlineData("https://github.com:8443/SpaceDeck-0.2.0-x64.msi")]            // other port
    [InlineData("https://user@github.com/SpaceDeck-0.2.0-x64.msi")]            // credentials in the address
    public void Assets_from_other_addresses_are_dropped(string address)
    {
        var asset = $$"""{ "name": "SpaceDeck-0.2.0-x64.msi", "size": 1, "browser_download_url": "{{address}}" }""";
        var release = Parse(Release(asset + "," + SumsAsset));
        Assert.NotNull(release);
        Assert.Null(release.Installer);
        Assert.False(release.CanInstall);
    }

    [Theory]
    [InlineData("SpaceDeck-0.2.0-x64.msi", true)]
    [InlineData("SpaceDeck-0.2.0-beta.1-x64.msi", true)]
    [InlineData("..\\SpaceDeck-0.2.0-x64.msi", false)]
    [InlineData("SpaceDeck-0.2.0-x64.msi.exe", false)]
    [InlineData("Other-0.2.0-x64.msi", false)]
    [InlineData("SpaceDeck-0.2/0-x64.msi", false)]
    public void Installer_names_are_plain_file_names(string name, bool expected) =>
        Assert.Equal(expected, UpdateDownloadPolicy.IsInstallerName(name));

    [Theory]
    [InlineData("https://objects.githubusercontent.com/a/b", true)]
    [InlineData("https://release-assets.githubusercontent.com/a/b", true)]
    [InlineData("https://GitHub.com/a/b", true)]
    [InlineData("https://raw.githubusercontent.com/a/b", false)]
    public void Redirect_targets_are_checked(string address, bool expected) =>
        Assert.Equal(expected, UpdateDownloadPolicy.IsAllowedAddress(new Uri(address)));

    [Fact]
    public void Checksum_file_finds_the_installer_line()
    {
        var text = $"ffff  other.zip\r\n{Hash.ToUpperInvariant()}  SpaceDeck-0.2.0-x64.msi\r\n";
        Assert.Equal(Hash, Sha256Sums.Find(text, "SpaceDeck-0.2.0-x64.msi"));
        Assert.Equal(Hash, Sha256Sums.Find($"{Hash} *SpaceDeck-0.2.0-x64.msi", "SpaceDeck-0.2.0-x64.msi"));
        Assert.Null(Sha256Sums.Find(text, "SpaceDeck-0.3.0-x64.msi"));
        Assert.Null(Sha256Sums.Find("abc  SpaceDeck-0.2.0-x64.msi", "SpaceDeck-0.2.0-x64.msi")); // not a SHA-256
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("v0.2.0-beta.1", 0, 2, 0)]
    public void Versions_are_read_from_tags(string tag, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build), ReleaseFeed.ParseVersion(tag));
}
