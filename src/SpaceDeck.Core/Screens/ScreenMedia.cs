using System.Globalization;
using System.Text.Json.Serialization;

namespace SpaceDeck.Core.Screens;

/// <summary>What a cooler screen shows. Stored by name in settings.json, so members can be reordered but not renamed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScreenContentMode>))]
public enum ScreenContentMode
{
    /// <summary>A dashboard, drawn live.</summary>
    Dashboard,

    /// <summary>One still picture.</summary>
    Picture,

    /// <summary>One animated GIF or video file, looping.</summary>
    Animation,

    /// <summary>The pictures and GIFs of a folder, one after another.</summary>
    Slideshow,
}

/// <summary>How a picture fills a box of another shape. Stored by name, so members can be reordered but not renamed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImageFit>))]
public enum ImageFit
{
    /// <summary>Covers the whole box; what sticks out is cut off (centred).</summary>
    Fill,

    /// <summary>All of the picture, as large as fits; the rest of the box is the background colour (letterbox).</summary>
    Fit,
}

/// <summary>What a media file is, by its extension.</summary>
public enum MediaKind
{
    Unknown,

    /// <summary>A still picture (PNG, JPEG, BMP, TIFF, …).</summary>
    Picture,

    /// <summary>A GIF (animated or not).</summary>
    Gif,

    /// <summary>A video Windows' Media Foundation plays (MP4, MOV, WMV, AVI, …).</summary>
    Video,
}

/// <summary>Media file rules shared by the cooler screen and picture tiles: which files, how copies are named.</summary>
public static class MediaFiles
{
    private static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".jxr", ".wdp", ".ico"];

    private static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".3gp", ".mpg", ".mpeg", ".ts"];

    /// <summary>For an open-file dialog: pictures and GIFs.</summary>
    public const string PictureFilter = "Pictures and GIFs|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff|All files (*.*)|*.*";

    /// <summary>For an open-file dialog: GIFs and videos.</summary>
    public const string AnimationFilter = "GIFs and videos|*.gif;*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm|All files (*.*)|*.*";

    /// <summary>The kind of file <paramref name="path"/> is, from its extension (case does not matter).</summary>
    public static MediaKind KindOf(string? path)
    {
        var extension = Path.GetExtension(path ?? "");
        return extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ? MediaKind.Gif
            : PictureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? MediaKind.Picture
            : VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? MediaKind.Video
            : MediaKind.Unknown;
    }

    /// <summary>Pictures and GIFs: what a slideshow, a picture tile and a dashboard background show.</summary>
    public static bool IsImage(string? path) => KindOf(path) is MediaKind.Picture or MediaKind.Gif;

    /// <summary>
    /// The slideshow's files in <paramref name="files"/>: pictures and GIFs only, in name order (ordinal, ignoring
    /// case), so the order is the same on every run.
    /// </summary>
    public static IReadOnlyList<string> SlideshowOrder(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return [.. files.Where(IsImage).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ThenBy(f => f, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The file name of a copy kept in SpaceDeck's own folder: the start of the content's hash (lowercase hex) and the
    /// original's extension, lowercased. The same file chosen twice is copied once; a changed file gets a new name.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="hashHex"/> isn't at least 16 hex digits.</exception>
    public static string StoredName(string hashHex, string originalPath)
    {
        ArgumentNullException.ThrowIfNull(hashHex);
        if (hashHex.Length < 16 || !hashHex.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("Expected at least 16 hex digits.", nameof(hashHex));
        }

        var extension = Path.GetExtension(originalPath ?? "");
        if (extension.Length is < 2 or > 6 || !extension[1..].All(char.IsAsciiLetterOrDigit))
        {
            extension = ".bin";
        }

        return AsciiLower(hashHex[..16] + extension);
    }

    private static string AsciiLower(string text) =>
        new string(text.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c).ToArray());

    /// <summary>Reads "#RRGGBB" (or "RRGGBB") as 0xRRGGBB; false for anything else.</summary>
    public static bool TryParseColor(string? text, out int rgb)
    {
        rgb = 0;
        var hex = (text ?? "").Trim();
        if (hex.StartsWith('#'))
        {
            hex = hex[1..];
        }

        return hex.Length == 6 && hex.All(char.IsAsciiHexDigit)
            && int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }

    /// <summary>0xRRGGBB as "#RRGGBB".</summary>
    public static string FormatColor(int rgb) =>
        "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
}
