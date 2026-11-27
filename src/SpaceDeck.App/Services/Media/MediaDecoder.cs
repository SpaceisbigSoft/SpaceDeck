using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.App.Services.Media;

/// <summary>
/// Decodes pictures and GIFs with WPF's (WIC's) decoders into <see cref="AnimationClip"/>s: plain pixels, no WPF objects,
/// so the result can be used on any thread. A GIF keeps its frames as stored (rectangles, delays, disposal);
/// <see cref="GifComposer"/> composes them. Any thread.
/// </summary>
internal static class MediaDecoder
{
    /// <summary>Still pictures are shrunk to this many pixels on their longer side (enough for a tile or a 480 px screen, twice over).</summary>
    public const int MaxPictureSide = 1024;

    /// <summary>A GIF's logical screen may be at most this large.</summary>
    private const int MaxGifSide = 2048;

    /// <summary>Frames kept from a GIF, and the pixel bytes they may take: a longer GIF loops over its first part.</summary>
    private const int MaxGifFrames = 2000;

    private const long MaxGifBytes = 96L * 1024 * 1024;

    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="NotSupportedException">Not a picture WIC can decode.</exception>
    /// <exception cref="InvalidDataException">The picture is empty or too large.</exception>
    public static AnimationClip Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("The file holds no picture.");
        }

        return decoder is GifBitmapDecoder && decoder.Frames.Count > 1 ? LoadGif(decoder) : LoadStill(decoder.Frames[0]);
    }

    private static AnimationClip LoadStill(BitmapFrame frame)
    {
        BitmapSource source = frame;

        // Phone and camera photos are stored sideways with an EXIF orientation; WPF doesn't apply it.
        var turn = Query(MetadataOf(frame), "System.Photo.Orientation", 1) switch
        {
            3 => 180,
            6 => 90,
            8 => 270,
            _ => 0,
        };
        if (turn != 0)
        {
            source = new TransformedBitmap(source, new RotateTransform(turn));
        }

        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > MaxPictureSide)
        {
            var scale = MaxPictureSide / (double)longest;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        var pixels = Pixels(source, out var width, out var height);
        return AnimationClip.Still(new BgraImage(width, height, pixels));
    }

    private static AnimationClip LoadGif(BitmapDecoder decoder)
    {
        BitmapMetadata? global = null;
        try
        {
            global = decoder.Metadata;
        }
        catch (Exception)
        {
            // No container metadata (WIC throws a variety of exceptions for that): the size comes from the frames.
        }

        var frames = new List<AnimationFrame>();
        long bytes = 0;
        var right = 1;
        var bottom = 1;
        foreach (var frame in decoder.Frames)
        {
            var metadata = MetadataOf(frame);
            var left = Query(metadata, "/imgdesc/Left", 0);
            var top = Query(metadata, "/imgdesc/Top", 0);
            var disposal = Query(metadata, "/grctlext/Disposal", 0);
            var delay = AnimationTimeline.GifDelay(Query(metadata, "/grctlext/Delay", 10));
            var pixels = Pixels(frame, out var width, out var height);
            bytes += pixels.Length;
            if (frames.Count >= MaxGifFrames || bytes > MaxGifBytes)
            {
                break;
            }

            frames.Add(new AnimationFrame(left, top, width, height, pixels, delay, disposal is >= 0 and <= 3 ? (GifDisposal)disposal : GifDisposal.None));
            right = Math.Max(right, left + width);
            bottom = Math.Max(bottom, top + height);
        }

        if (frames.Count == 0)
        {
            throw new InvalidDataException("The GIF's first frame is too large.");
        }

        var canvasWidth = Query(global, "/logscrdesc/Width", right);
        var canvasHeight = Query(global, "/logscrdesc/Height", bottom);
        if (canvasWidth < 1 || canvasHeight < 1)
        {
            (canvasWidth, canvasHeight) = (right, bottom);
        }

        if (canvasWidth > MaxGifSide || canvasHeight > MaxGifSide)
        {
            throw new InvalidDataException($"The GIF is larger than {MaxGifSide} × {MaxGifSide} pixels.");
        }

        return new AnimationClip(canvasWidth, canvasHeight, frames);
    }

    /// <summary>The picture as BGRA bytes (straight alpha).</summary>
    private static byte[] Pixels(BitmapSource source, out int width, out int height)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;
        if (width < 1 || height < 1)
        {
            throw new InvalidDataException("The picture is empty.");
        }

        var pixels = new byte[checked(width * height * 4)];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>A frame's metadata, or null for formats without (BMP) or when it can't be read.</summary>
    private static BitmapMetadata? MetadataOf(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata as BitmapMetadata;
        }
        catch (Exception)
        {
            return null; // as in Query: absent metadata is reported with various exceptions
        }
    }

    /// <summary>A number from the picture's metadata, or <paramref name="fallback"/> when it isn't there.</summary>
    private static int Query(BitmapMetadata? metadata, string query, int fallback)
    {
        if (metadata is null)
        {
            return fallback;
        }

        try
        {
            return metadata.GetQuery(query) is { } value and not string ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : fallback;
        }
        catch (Exception)
        {
            // Metadata readers throw a variety of exceptions for absent or odd entries; none of it is worth more than the default.
            return fallback;
        }
    }
}

/// <summary>
/// Decoded pictures and GIFs by path, shared by every tile, background and cooler screen that shows them (a GIF is
/// decoded once however many places show it). Reloaded when the file changes. Thread-safe.
/// </summary>
internal static class MediaCache
{
    private const int Capacity = 12;

    private static readonly Lock Gate = new();

    /// <summary>Least recently used first.</summary>
    private static readonly List<Entry> Entries = [];

    /// <summary>The clip for <paramref name="path"/>, decoded on a worker thread the first time (a faulted task when it can't be).</summary>
    public static Task<AnimationClip> LoadAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var (stamp, length) = Stamp(path);
        lock (Gate)
        {
            var index = Entries.FindIndex(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var entry = Entries[index];
                Entries.RemoveAt(index);
                if (entry.Stamp == stamp && entry.Length == length)
                {
                    Entries.Add(entry);
                    return entry.Load;
                }
            }

            var load = Task.Run(() => MediaDecoder.Load(path));
            Entries.Add(new Entry(path, stamp, length, load));
            if (Entries.Count > Capacity)
            {
                Entries.RemoveAt(0);
            }

            return load;
        }
    }

    /// <summary>The clip if it has been decoded already; null otherwise (not loaded yet, or it failed).</summary>
    public static AnimationClip? TryGetLoaded(string path)
    {
        lock (Gate)
        {
            return Entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))?.Load is { IsCompletedSuccessfully: true } load
                ? load.Result
                : null;
        }
    }

    private static (DateTime Stamp, long Length) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (DateTime.MinValue, -1);
        }
    }

    private sealed record Entry(string Path, DateTime Stamp, long Length, Task<AnimationClip> Load);
}

/// <summary>
/// SpaceDeck's own copies of the pictures, GIFs and videos chosen for cooler screens, picture tiles and backgrounds, in
/// <c>%AppData%\SpaceDeck\screens</c>, so moving or deleting the original changes nothing. A copy is named by its
/// content (<see cref="MediaFiles.StoredName"/>): the same file chosen twice is kept once. Copies are never deleted
/// automatically (a dashboard or a backup may still use one); they are small next to what they replace.
/// </summary>
internal static class MediaLibrary
{
    /// <summary>Files larger than this (long videos) are used where they are instead of copied.</summary>
    private const long MaxCopyBytes = 1L << 30;

    public static string Folder => Path.Combine(AppSettings.Directory, "screens");

    /// <summary>
    /// The path to keep in settings for <paramref name="source"/>: SpaceDeck's copy (made now if there isn't one), or
    /// the original when it already is a copy or is too large to copy. Slow for big files: call off the UI thread.
    /// </summary>
    /// <exception cref="IOException">The file can't be read or copied.</exception>
    /// <exception cref="UnauthorizedAccessException">The file can't be read.</exception>
    public static string Import(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        var full = Path.GetFullPath(source);
        var folder = Path.GetFullPath(Folder);
        if (full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        if (new FileInfo(full).Length > MaxCopyBytes)
        {
            return full;
        }

        string hash;
        using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        var target = Path.Combine(folder, MediaFiles.StoredName(hash, full));
        if (!File.Exists(target))
        {
            System.IO.Directory.CreateDirectory(folder);
            var temp = target + ".tmp";
            File.Copy(full, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }

        return target;
    }
}
