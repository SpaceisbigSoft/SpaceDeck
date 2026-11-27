using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceDeck.Core.Dashboards;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.Core.Tests;

/// <summary>Pictures, GIFs and videos on the cooler screen: settings, geometry, composition, pacing, the default layout.</summary>
public class ScreenMediaTests
{
    /// <summary>The app's settings.json options (AppSettings).</summary>
    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ---- Settings ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Settings_RoundTripTheMediaFields()
    {
        var settings = new CoolerScreenSettings
        {
            HardwareId = "lianli:/LianLi/0",
            Content = ScreenContentMode.Animation,
            MediaPath = @"C:\Users\me\AppData\Roaming\SpaceDeck\screens\0123456789abcdef.gif",
            MediaName = "cat.gif",
            SlideshowFolder = @"D:\Pictures\Cooler",
            SlideSeconds = 30,
            Fit = ImageFit.Fit,
            Background = "#102030",
            MaxFramesPerSecond = 30,
        };

        var json = JsonSerializer.Serialize(settings, SettingsJson);
        Assert.Contains("\"Animation\"", json, StringComparison.Ordinal); // stored by name
        Assert.Contains("\"Fit\": \"Fit\"", json, StringComparison.Ordinal);
        var loaded = JsonSerializer.Deserialize<CoolerScreenSettings>(json, SettingsJson)!;
        loaded.Normalize();
        Assert.Equal(ScreenContentMode.Animation, loaded.Content);
        Assert.Equal(settings.MediaPath, loaded.MediaPath);
        Assert.Equal("cat.gif", loaded.MediaName);
        Assert.Equal(@"D:\Pictures\Cooler", loaded.SlideshowFolder);
        Assert.Equal(30, loaded.SlideSeconds);
        Assert.Equal(ImageFit.Fit, loaded.Fit);
        Assert.Equal("#102030", loaded.Background);
        Assert.Equal(0x102030, loaded.GetBackgroundRgb());
        Assert.Equal(30, loaded.MaxFramesPerSecond);
    }

    [Fact]
    public void Settings_WithoutTheMediaFields_ShowADashboard_At24FramesASecond()
    {
        // A settings.json from before pictures and videos.
        var loaded = JsonSerializer.Deserialize<CoolerScreenSettings>("""{"HardwareId":"lianli:/LianLi/0","Enabled":true,"DashboardId":"abc"}""", SettingsJson)!;
        loaded.Normalize();
        Assert.Equal(ScreenContentMode.Dashboard, loaded.Content);
        Assert.Null(loaded.MediaPath);
        Assert.Null(loaded.SlideshowFolder);
        Assert.Equal(10, loaded.SlideSeconds);
        Assert.Equal(ImageFit.Fill, loaded.Fit);
        Assert.Equal("#000000", loaded.Background);
        Assert.Equal(24, loaded.MaxFramesPerSecond);
    }

    [Fact]
    public void Settings_Clean_ClampsTheMediaFields()
    {
        var json = """
            [{"HardwareId":"lianli:/LianLi/0","Content":7,"Fit":9,"MaxFramesPerSecond":0,"SlideSeconds":1,
              "Background":"red","MediaPath":"  ","MediaName":" cat.gif ","SlideshowFolder":""},
             {"HardwareId":"lianli:/LianLi/1","MaxFramesPerSecond":500,"SlideSeconds":99999,"Background":"abcdef"}]
            """;
        var cleaned = CoolerScreenSettings.Clean(JsonSerializer.Deserialize<List<CoolerScreenSettings?>>(json, SettingsJson));
        Assert.Equal(2, cleaned.Count);
        Assert.Equal(ScreenContentMode.Dashboard, cleaned[0].Content);
        Assert.Equal(ImageFit.Fill, cleaned[0].Fit);
        Assert.Equal(1, cleaned[0].MaxFramesPerSecond);
        Assert.Equal(2, cleaned[0].SlideSeconds);
        Assert.Equal("#000000", cleaned[0].Background);
        Assert.Null(cleaned[0].MediaPath);
        Assert.Equal("cat.gif", cleaned[0].MediaName);
        Assert.Null(cleaned[0].SlideshowFolder);
        Assert.Equal(30, cleaned[1].MaxFramesPerSecond);
        Assert.Equal(3600, cleaned[1].SlideSeconds);
        Assert.Equal("#ABCDEF", cleaned[1].Background);
    }

    // ---- Media files ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a.png", MediaKind.Picture)]
    [InlineData(@"C:\x\B.JPG", MediaKind.Picture)]
    [InlineData("loop.GIF", MediaKind.Gif)]
    [InlineData("clip.mp4", MediaKind.Video)]
    [InlineData("clip.MOV", MediaKind.Video)]
    [InlineData("clip.wmv", MediaKind.Video)]
    [InlineData("clip.avi", MediaKind.Video)]
    [InlineData("notes.txt", MediaKind.Unknown)]
    [InlineData("", MediaKind.Unknown)]
    [InlineData(null, MediaKind.Unknown)]
    public void MediaFiles_KnowTheKindByExtension(string? path, MediaKind expected) => Assert.Equal(expected, MediaFiles.KindOf(path));

    [Fact]
    public void MediaFiles_StoredName_IsTheHashAndTheExtension_Lowercase()
    {
        Assert.Equal("0123456789abcdef.gif", MediaFiles.StoredName("0123456789ABCDEF0011", @"C:\Pictures\Cat.GIF"));
        Assert.Equal("0123456789abcdef.mp4", MediaFiles.StoredName("0123456789abcdef", "clip.mp4"));
        Assert.Equal("0123456789abcdef.bin", MediaFiles.StoredName("0123456789abcdef", "no-extension"));
        Assert.Equal("0123456789abcdef.bin", MediaFiles.StoredName("0123456789abcdef", "odd.ex~t"));
        Assert.Throws<ArgumentException>(() => MediaFiles.StoredName("0123", "a.png"));
        Assert.Throws<ArgumentException>(() => MediaFiles.StoredName("0123456789abcdeg", "a.png"));
    }

    [Fact]
    public void MediaFiles_Slideshow_TakesPicturesAndGifs_ByName()
    {
        var order = MediaFiles.SlideshowOrder([@"D:\s\b.png", @"D:\s\A.gif", @"D:\s\c.mp4", @"D:\s\readme.txt", @"D:\s\a2.JPG"]);
        Assert.Equal(new[] { @"D:\s\A.gif", @"D:\s\a2.JPG", @"D:\s\b.png" }, order);
    }

    [Theory]
    [InlineData("#000000", true, 0x000000)]
    [InlineData("#FF8800", true, 0xFF8800)]
    [InlineData("ff8800", true, 0xFF8800)]
    [InlineData(" #102030 ", true, 0x102030)]
    [InlineData("#FFF", false, 0)]
    [InlineData("red", false, 0)]
    [InlineData("#GG0000", false, 0)]
    [InlineData(null, false, 0)]
    public void MediaFiles_ReadColors(string? text, bool ok, int rgb)
    {
        Assert.Equal(ok, MediaFiles.TryParseColor(text, out var parsed));
        Assert.Equal(rgb, parsed);
    }

    // ---- Fit, crop and rotation -------------------------------------------------------------------------------------

    [Fact]
    public void ImageRect_Fill_CoversTheBox_AndIsCentred()
    {
        Assert.Equal(new PixelRect(-50, 0, 200, 100), ScreenGeometry.ImageRect(400, 200, 100, 100, ImageFit.Fill));
        Assert.Equal(new PixelRect(0, -120, 480, 720), ScreenGeometry.ImageRect(200, 300, 480, 480, ImageFit.Fill));
    }

    [Fact]
    public void ImageRect_Fit_FitsInside_WithBars()
    {
        Assert.Equal(new PixelRect(0, 25, 100, 50), ScreenGeometry.ImageRect(400, 200, 100, 100, ImageFit.Fit));
        Assert.Equal(new PixelRect(80, 0, 320, 480), ScreenGeometry.ImageRect(200, 300, 480, 480, ImageFit.Fit));
        Assert.Equal(new PixelRect(0, 0, 480, 480), ScreenGeometry.ImageRect(10, 10, 480, 480, ImageFit.Fit)); // enlarged
        Assert.Equal(new PixelRect(0, 0, 480, 480), ScreenGeometry.ImageRect(0, 10, 480, 480, ImageFit.Fit)); // nothing to place
    }

    [Fact]
    public void VisibleSource_IsTheCroppedMiddle_ForFill_AndEverything_ForFit()
    {
        Assert.Equal(new PixelRect(100, 0, 200, 200), ScreenGeometry.VisibleSource(400, 200, 100, 100, ImageFit.Fill));
        Assert.Equal(new PixelRect(0, 50, 200, 200), ScreenGeometry.VisibleSource(200, 300, 480, 480, ImageFit.Fill));
        Assert.Equal(new PixelRect(0, 0, 400, 200), ScreenGeometry.VisibleSource(400, 200, 100, 100, ImageFit.Fit));
    }

    [Theory]
    [InlineData(0, 480, 272)]
    [InlineData(90, 272, 480)]
    [InlineData(180, 480, 272)]
    [InlineData(270, 272, 480)]
    [InlineData(-90, 272, 480)]
    public void DrawSize_SwapsForAQuarterTurn(int rotation, int width, int height) =>
        Assert.Equal((width, height), ScreenGeometry.DrawSize(480, 272, rotation));

    [Fact]
    public void Rotate_TurnsClockwise_LikeWpf()
    {
        // An upright 3 × 2 picture; corners: top-left (0,0), top-right (2,0), bottom-left (0,1).
        Assert.Equal((0, 0), ScreenGeometry.Rotate(0, 0, 3, 2, 0));
        Assert.Equal((1, 0), ScreenGeometry.Rotate(0, 0, 3, 2, 90)); // top-left goes to the top-right of the 2 × 3 result
        Assert.Equal((1, 2), ScreenGeometry.Rotate(2, 0, 3, 2, 90));
        Assert.Equal((0, 0), ScreenGeometry.Rotate(0, 1, 3, 2, 90));
        Assert.Equal((2, 1), ScreenGeometry.Rotate(0, 0, 3, 2, 180)); // upside down
        Assert.Equal((0, 1), ScreenGeometry.Rotate(2, 0, 3, 2, 180));
        Assert.Equal((0, 2), ScreenGeometry.Rotate(0, 0, 3, 2, 270));
        Assert.Equal((0, 0), ScreenGeometry.Rotate(2, 0, 3, 2, 270));
    }

    [Fact]
    public void Compositor_Rotate_MovesEveryPixel_AndFourQuarterTurnsAreNone()
    {
        var image = Numbered(3, 2);
        var upsideDown = ScreenCompositor.Rotate(image, 180);
        Assert.Equal((3, 2), (upsideDown.Width, upsideDown.Height));
        Assert.Equal(Pixel(image, 0, 0), Pixel(upsideDown, 2, 1));
        Assert.Equal(Pixel(image, 2, 1), Pixel(upsideDown, 0, 0));

        var quarter = ScreenCompositor.Rotate(image, 90);
        Assert.Equal((2, 3), (quarter.Width, quarter.Height));
        Assert.Equal(Pixel(image, 0, 0), Pixel(quarter, 1, 0));
        Assert.Equal(Pixel(image, 0, 1), Pixel(quarter, 0, 0));

        var round = ScreenCompositor.Rotate(ScreenCompositor.Rotate(ScreenCompositor.Rotate(quarter, 90), 90), 90);
        Assert.Equal(image.Pixels, round.Pixels);
        Assert.Equal(image.Pixels, ScreenCompositor.Rotate(image, 0).Pixels);
        Assert.Throws<ArgumentException>(() => ScreenCompositor.Rotate(image, 90, new BgraImage(3, 2)));
    }

    [Fact]
    public void Compositor_Fit_DrawsBarsInTheBackground()
    {
        var source = Solid(40, 20, 0x20, 0x40, 0x80, 255); // wide: bars above and below on a square screen
        var screen = new BgraImage(10, 10);
        ScreenCompositor.Compose(source, opaque: false, screen, ImageFit.Fit, 0xFF0000);

        Assert.Equal(Bgra(0x00, 0x00, 0xFF, 0xFF), Pixel(screen, 5, 0)); // red bar (B, G, R, A)
        Assert.Equal(Bgra(0x00, 0x00, 0xFF, 0xFF), Pixel(screen, 5, 9));
        Assert.Equal(Bgra(0x20, 0x40, 0x80, 0xFF), Pixel(screen, 5, 5)); // the picture, colour kept through the scaling
        Assert.Equal(Bgra(0x20, 0x40, 0x80, 0xFF), Pixel(screen, 0, 3));
    }

    [Fact]
    public void Compositor_Fill_CropsTheMiddle()
    {
        // Left third red, middle third green, right third blue; a square screen filled from it shows only green.
        var source = new BgraImage(30, 10);
        for (var y = 0; y < 10; y++)
        {
            for (var x = 0; x < 30; x++)
            {
                var i = (y * source.Stride) + (x * 4);
                source.Pixels[i + (x < 10 ? 2 : x < 20 ? 1 : 0)] = 255;
                source.Pixels[i + 3] = 255;
            }
        }

        var screen = new BgraImage(10, 10);
        ScreenCompositor.Compose(source, opaque: false, screen, ImageFit.Fill, 0x000000);
        Assert.Equal(Bgra(0, 255, 0, 255), Pixel(screen, 5, 5));
        Assert.Equal(Bgra(0, 255, 0, 255), Pixel(screen, 2, 2));
        Assert.Equal(Bgra(0, 255, 0, 255), Pixel(screen, 7, 7));
    }

    [Fact]
    public void Compositor_ShowsTheBackgroundThroughTransparency_UnlessTheSourceIsOpaque()
    {
        var clear = Solid(4, 4, 0x10, 0x20, 0x30, 0); // fully transparent
        var screen = new BgraImage(4, 4);
        ScreenCompositor.Compose(clear, opaque: false, screen, ImageFit.Fill, 0x112233);
        Assert.Equal(Bgra(0x33, 0x22, 0x11, 0xFF), Pixel(screen, 1, 1));

        // A video frame's fourth byte means nothing: drawn as it is.
        ScreenCompositor.Compose(clear, opaque: true, screen, ImageFit.Fill, 0x112233);
        Assert.Equal(Bgra(0x10, 0x20, 0x30, 0xFF), Pixel(screen, 1, 1));
    }

    [Fact]
    public void Compositor_Shrinks_ByAveraging()
    {
        // A one-pixel checkerboard of black and white shrunk 4 times is mid grey, not black or white (no aliasing).
        var source = new BgraImage(16, 16);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var i = (y * source.Stride) + (x * 4);
                var level = (byte)(((x + y) % 2) * 255);
                source.Pixels[i] = source.Pixels[i + 1] = source.Pixels[i + 2] = level;
                source.Pixels[i + 3] = 255;
            }
        }

        var screen = new BgraImage(4, 4);
        ScreenCompositor.Compose(source, opaque: true, screen, ImageFit.Fill, 0);
        var (b, _, _, _) = Pixel(screen, 1, 2);
        Assert.InRange(b, 100, 155);
    }

    // ---- GIF composition --------------------------------------------------------------------------------------------

    private static readonly (byte B, byte G, byte R, byte A) Clear = (0, 0, 0, 0);
    private static readonly (byte B, byte G, byte R, byte A) Red = (0, 0, 255, 255);
    private static readonly (byte B, byte G, byte R, byte A) Green = (0, 255, 0, 255);
    private static readonly (byte B, byte G, byte R, byte A) Blue = (255, 0, 0, 255);

    /// <summary>A frame of one colour (transparent = <see cref="Clear"/>).</summary>
    private static AnimationFrame Frame(int left, int top, int width, int height, (byte B, byte G, byte R, byte A) color, GifDisposal disposal, int delayMs = 100)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = color;
        }

        return new AnimationFrame(left, top, width, height, pixels, TimeSpan.FromMilliseconds(delayMs), disposal);
    }

    [Fact]
    public void Gif_FramesDrawAtTheirOffsets_OnTopOfWhatWasThere()
    {
        var clip = new AnimationClip(4, 4, [Frame(0, 0, 4, 4, Red, GifDisposal.Keep), Frame(1, 1, 2, 2, Green, GifDisposal.Keep)]);
        var composer = new GifComposer(clip);
        composer.MoveTo(0);
        Assert.Equal(Red, Pixel(composer.Canvas, 1, 1));

        composer.MoveTo(1);
        Assert.Equal(Green, Pixel(composer.Canvas, 1, 1));
        Assert.Equal(Green, Pixel(composer.Canvas, 2, 2));
        Assert.Equal(Red, Pixel(composer.Canvas, 0, 0)); // kept from the first frame
        Assert.Equal(Red, Pixel(composer.Canvas, 3, 3));
    }

    [Fact]
    public void Gif_TransparentPixels_LeaveTheCanvasShowing()
    {
        var hole = Frame(0, 0, 2, 1, Blue, GifDisposal.Keep);
        hole.Pixels[3] = 0; // the left pixel is transparent
        var clip = new AnimationClip(2, 1, [Frame(0, 0, 2, 1, Red, GifDisposal.Keep), hole]);
        var composer = new GifComposer(clip);
        composer.MoveTo(1);
        Assert.Equal(Red, Pixel(composer.Canvas, 0, 0));
        Assert.Equal(Blue, Pixel(composer.Canvas, 1, 0));
    }

    [Fact]
    public void Gif_RestoreToBackground_ClearsOnlyThatFramesRectangle()
    {
        var clip = new AnimationClip(4, 4,
        [
            Frame(0, 0, 4, 4, Red, GifDisposal.Keep),
            Frame(1, 1, 2, 2, Green, GifDisposal.Background),
            Frame(0, 0, 1, 1, Blue, GifDisposal.Keep),
        ]);
        var composer = new GifComposer(clip);
        composer.MoveTo(2);
        Assert.Equal(Clear, Pixel(composer.Canvas, 1, 1)); // the green rectangle went back to transparent
        Assert.Equal(Clear, Pixel(composer.Canvas, 2, 2));
        Assert.Equal(Red, Pixel(composer.Canvas, 3, 3)); // the rest is untouched
        Assert.Equal(Blue, Pixel(composer.Canvas, 0, 0));
    }

    [Fact]
    public void Gif_RestoreToPrevious_PutsBackTheCanvasFromBeforeTheFrame()
    {
        var clip = new AnimationClip(4, 4,
        [
            Frame(0, 0, 4, 4, Red, GifDisposal.Keep),
            Frame(0, 0, 4, 4, Green, GifDisposal.Previous),
            Frame(3, 3, 1, 1, Blue, GifDisposal.Keep),
        ]);
        var composer = new GifComposer(clip);
        composer.MoveTo(1);
        Assert.Equal(Green, Pixel(composer.Canvas, 0, 0));

        composer.MoveTo(2);
        Assert.Equal(Red, Pixel(composer.Canvas, 0, 0)); // green undone
        Assert.Equal(Blue, Pixel(composer.Canvas, 3, 3));
    }

    [Fact]
    public void Gif_FramesOutsideTheCanvas_AreClipped_AndGoingBackStartsOver()
    {
        var clip = new AnimationClip(2, 2, [Frame(-1, -1, 2, 2, Red, GifDisposal.Keep), Frame(1, 1, 5, 5, Green, GifDisposal.Keep)]);
        var composer = new GifComposer(clip);
        composer.MoveTo(1);
        Assert.Equal(Red, Pixel(composer.Canvas, 0, 0));
        Assert.Equal(Green, Pixel(composer.Canvas, 1, 1));
        Assert.Equal(Clear, Pixel(composer.Canvas, 1, 0));

        composer.MoveTo(0); // a new loop: the canvas is cleared first
        Assert.Equal(0, composer.Index);
        Assert.Equal(Clear, Pixel(composer.Canvas, 1, 1));
        composer.MoveTo(99);
        Assert.Equal(1, composer.Index);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 100)]
    [InlineData(2, 20)]
    [InlineData(4, 40)]
    [InlineData(50, 500)]
    [InlineData(-3, 100)]
    public void Gif_Delays_UnderTwentyMilliseconds_PlayAtAHundred(int hundredths, int milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), AnimationTimeline.GifDelay(hundredths));

    [Fact]
    public void Timeline_FindsTheFrame_AndLoops()
    {
        var clip = new AnimationClip(1, 1,
        [
            Frame(0, 0, 1, 1, Red, GifDisposal.Keep, 100),
            Frame(0, 0, 1, 1, Green, GifDisposal.Keep, 50),
            Frame(0, 0, 1, 1, Blue, GifDisposal.Keep, 250),
        ]);
        Assert.Equal(TimeSpan.FromMilliseconds(400), clip.Duration);
        Assert.True(clip.IsAnimated);

        Assert.Equal((0, 0L, (TimeSpan?)TimeSpan.FromMilliseconds(100)), AnimationTimeline.At(clip, TimeSpan.Zero));
        Assert.Equal((1, 0L, (TimeSpan?)TimeSpan.FromMilliseconds(150)), AnimationTimeline.At(clip, TimeSpan.FromMilliseconds(100)));
        Assert.Equal((2, 0L, (TimeSpan?)TimeSpan.FromMilliseconds(400)), AnimationTimeline.At(clip, TimeSpan.FromMilliseconds(399)));
        Assert.Equal((0, 1L, (TimeSpan?)TimeSpan.FromMilliseconds(500)), AnimationTimeline.At(clip, TimeSpan.FromMilliseconds(400)));
        Assert.Equal((1, 2L, (TimeSpan?)TimeSpan.FromMilliseconds(950)), AnimationTimeline.At(clip, TimeSpan.FromMilliseconds(920)));
        Assert.Equal(0, AnimationTimeline.At(clip, TimeSpan.FromSeconds(-1)).Index);

        var still = AnimationClip.Still(new BgraImage(2, 2));
        Assert.False(still.IsAnimated);
        Assert.Equal((0, 0L, (TimeSpan?)null), AnimationTimeline.At(still, TimeSpan.FromSeconds(10)));
    }

    // ---- Pacing -----------------------------------------------------------------------------------------------------

    /// <summary>Runs a pacer on a manual clock in 1 ms steps, as the player does; returns the send times.</summary>
    private static List<TimeSpan> Run(FramePacer pacer, TimeSpan length, Func<TimeSpan, long> version)
    {
        var sent = new List<TimeSpan>();
        for (var now = TimeSpan.Zero; now < length; now += TimeSpan.FromMilliseconds(1))
        {
            if (pacer.ShouldSend(now, version(now)))
            {
                sent.Add(now);
            }
        }

        return sent;
    }

    [Fact]
    public void Pacer_ContentFasterThanTheCap_IsSentAtTheCap()
    {
        var pacer = new FramePacer(24, TimeSpan.FromSeconds(5));
        var sent = Run(pacer, TimeSpan.FromSeconds(10), now => (long)now.TotalMilliseconds / 10); // 100 changes a second
        Assert.InRange(sent.Count, 239, 241);
        Assert.All(sent.Zip(sent.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromMilliseconds(41)));
    }

    [Fact]
    public void Pacer_ContentSlowerThanTheCap_IsSentAtEachChange()
    {
        var pacer = new FramePacer(24, TimeSpan.FromSeconds(5));
        var sent = Run(pacer, TimeSpan.FromSeconds(1), now => (long)now.TotalMilliseconds / 100); // a GIF at 10 frames a second
        Assert.Equal(Enumerable.Range(0, 10).Select(i => TimeSpan.FromMilliseconds(i * 100)), sent);
    }

    [Fact]
    public void Pacer_AStillPicture_IsSentAgainEveryFiveSeconds()
    {
        var pacer = new FramePacer(24, TimeSpan.FromSeconds(5));
        var sent = Run(pacer, TimeSpan.FromSeconds(16), _ => 1);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15) }, sent);
    }

    [Fact]
    public void Pacer_StaysOnItsGrid_WhenTheTimerWakesLate()
    {
        // The player wakes on a 15.6 ms system tick: frames at 30 a second come out at 30 a second on average.
        var pacer = new FramePacer(30, TimeSpan.FromSeconds(5));
        var sent = 0;
        long version = 0;
        for (var tick = 0; tick < 640; tick++) // 10 s
        {
            var now = TimeSpan.FromTicks(tick * 156_250L);
            if (pacer.ShouldSend(now, ++version))
            {
                sent++;
            }
        }

        Assert.InRange(sent, 290, 301);
    }

    [Fact]
    public void Pacer_NextCheck_WaitsForTheSlot_TheChange_OrTheKeepAlive()
    {
        var pacer = new FramePacer(20, TimeSpan.FromSeconds(5)); // 50 ms slots
        Assert.Equal(TimeSpan.Zero, pacer.NextCheck(TimeSpan.Zero, 0, null)); // nothing sent yet
        Assert.True(pacer.ShouldSend(TimeSpan.Zero, 1));

        Assert.Equal(TimeSpan.FromMilliseconds(50), pacer.NextCheck(TimeSpan.FromMilliseconds(10), 2, null)); // changed: next slot
        Assert.Equal(TimeSpan.FromMilliseconds(200), pacer.NextCheck(TimeSpan.FromMilliseconds(10), 1, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(TimeSpan.FromMilliseconds(50), pacer.NextCheck(TimeSpan.FromMilliseconds(10), 1, TimeSpan.FromMilliseconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(5), pacer.NextCheck(TimeSpan.FromMilliseconds(10), 1, null)); // still: keep-alive
        Assert.Equal(TimeSpan.FromSeconds(5), pacer.NextCheck(TimeSpan.FromMilliseconds(10), 1, TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(6), pacer.NextCheck(TimeSpan.FromSeconds(6), 1, null)); // overdue: now

        pacer.Reset();
        Assert.True(pacer.ShouldSend(TimeSpan.FromMilliseconds(11), 1)); // after a reset the next frame goes at once
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(24, 24)]
    [InlineData(31, 30)]
    public void Pacer_ClampsTheRate(int asked, int used) => Assert.Equal(used, new FramePacer(asked, TimeSpan.FromSeconds(5)).MaxFramesPerSecond);

    [Fact]
    public void FrameRateMeter_CountsTheLastTwoSeconds()
    {
        var meter = new FrameRateMeter();
        Assert.Equal(0, meter.FramesPerSecond(TimeSpan.Zero));
        for (var i = 0; i <= 96; i++)
        {
            meter.Add(TimeSpan.FromMilliseconds(i * 1000.0 / 24)); // 4 s at 24 a second
        }

        Assert.Equal(24, meter.FramesPerSecond(TimeSpan.FromSeconds(4)), 1);
        Assert.Equal(0, meter.FramesPerSecond(TimeSpan.FromSeconds(10))); // nothing for a while
    }

    // ---- Screen layouts and the built-in cooler screen dashboard ----------------------------------------------------

    [Fact]
    public void ScreenGrid_ForTheGa2Lcd_IsSixColumnsByElevenRows()
    {
        var grid = ScreenLayoutGrid.For(480, 480);
        Assert.Equal(new ScreenLayoutGrid(6, 11), grid);
        Assert.Equal((504, 516), (grid.WidthPx, grid.HeightPx));
        Assert.InRange(grid.WidthPx / (double)grid.HeightPx, 0.97, 1.03); // square within 3 %

        var wide = ScreenLayoutGrid.For(320, 240);
        Assert.InRange(wide.WidthPx / (double)wide.HeightPx, 4 / 3.0 * 0.9, 4 / 3.0 * 1.1);
    }

    [Fact]
    public void ScreenGrid_Contains_OnlyTilesInsideTheArea()
    {
        var grid = ScreenLayoutGrid.For(480, 480);
        Assert.True(grid.Contains([]));
        Assert.True(grid.Contains([new TileSpec { Col = 0, Row = 0, ColSpan = 6, RowSpan = 11 }]));
        Assert.False(grid.Contains([new TileSpec { Col = 3, Row = 0, ColSpan = 4, RowSpan = 2 }]));
        Assert.False(grid.Contains([new TileSpec { Col = 0, Row = 10, ColSpan = 2, RowSpan = 2 }]));
    }

    [Fact]
    public void FillNumberSize_GrowsWithTheTile()
    {
        var big = ScreenLayoutGrid.FillNumberSize(6, 5);
        var small = ScreenLayoutGrid.FillNumberSize(3, 3);
        Assert.InRange(big, 120, 160); // ≈ 117 px on the LCD (the 504 × 516 area drawn at about 0.86)
        Assert.InRange(small, 50, 70);
        Assert.Equal(24, ScreenLayoutGrid.FillNumberSize(1, 1));
        Assert.Equal(200, ScreenLayoutGrid.FillNumberSize(12, 40));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CoolerLayout_Arrange_CoversTheWholeScreen_WithoutOverlaps(int count)
    {
        var grid = ScreenLayoutGrid.For(480, 480);
        var cells = CoolerScreenLayout.Arrange(count, grid);
        Assert.Equal(count, cells.Count);
        Assert.Equal(count == 0 ? 0 : grid.Columns * grid.Rows, cells.Sum(c => c.ColSpan * c.RowSpan));
        for (var i = 0; i < cells.Count; i++)
        {
            Assert.True(cells[i].Col >= 0 && cells[i].Row >= 0 && cells[i].Right <= grid.Columns && cells[i].Bottom <= grid.Rows, $"{cells[i]}");
            var (minCols, minRows) = TileSpec.MinimumSize(WidgetType.BigNumber);
            Assert.True(cells[i].ColSpan >= minCols && cells[i].RowSpan >= minRows, $"{cells[i]}");
            for (var j = i + 1; j < cells.Count; j++)
            {
                Assert.False(cells[i].Overlaps(cells[j]), $"{cells[i]} / {cells[j]}");
            }
        }

        if (count > 1)
        {
            Assert.True(cells[0].ColSpan * cells[0].RowSpan > cells[1].ColSpan * cells[1].RowSpan); // the first is the biggest
        }
    }

    private static ScreenSensor Sensor(string id, string hardware, HardwareKind hardwareKind, SensorKind kind, string name, bool advanced = false) =>
        new(id, hardware, hardwareKind, kind, name, advanced);

    [Fact]
    public void CoolerLayout_Picks_CpuGpuCoolantAndPump_ByOriginalName()
    {
        List<ScreenSensor> sensors =
        [
            Sensor("cpu-core", "cpu", HardwareKind.Cpu, SensorKind.Temperature, "Core #1"),
            Sensor("cpu-pkg", "cpu", HardwareKind.Cpu, SensorKind.Temperature, "CPU Package"),
            Sensor("igpu", "igpu", HardwareKind.Gpu, SensorKind.Load, "D3D 3D"),
            Sensor("gpu-hot", "dgpu", HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot"),
            Sensor("gpu-core", "dgpu", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core"),
            Sensor("other-pump", "mb", HardwareKind.SuperIo, SensorKind.Fan, "Pump Fan"),
            Sensor("coolant", "lianli:/LianLi/0", HardwareKind.Cooler, SensorKind.Temperature, "Coolant"),
            Sensor("pump", "lianli:/LianLi/0", HardwareKind.Cooler, SensorKind.Fan, "Pump"),
        ];

        var picks = CoolerScreenLayout.Pick(sensors, "lianli:/LianLi/0");
        Assert.Equal("cpu-pkg", picks.Cpu?.Id);
        Assert.Equal("gpu-core", picks.Gpu?.Id); // the discrete GPU (it has a temperature), by name
        Assert.Equal("coolant", picks.Coolant?.Id);
        Assert.Equal("pump", picks.Pump?.Id); // the cooler's own pump beats the board's pump header

        var elsewhere = CoolerScreenLayout.Pick(sensors, "lianli:/LianLi/9");
        Assert.Equal("other-pump", elsewhere.Pump?.Id); // no such cooler: any pump
        Assert.Equal("coolant", elsewhere.Coolant?.Id); // any coolant
    }

    [Fact]
    public void CoolerLayout_Create_IsAScreenLayout_WithTheCpuBiggest()
    {
        var cpu = Sensor("cpu", "cpu", HardwareKind.Cpu, SensorKind.Temperature, "CPU Package");
        var pump = Sensor("pump", "c", HardwareKind.Cooler, SensorKind.Fan, "Pump");
        var doc = CoolerScreenLayout.Create(new CoolerScreenPicks(cpu, null, null, pump), 480, 480);

        Assert.Equal("Cooler screen", doc.Name);
        Assert.Equal(480, doc.Screen?.Width);
        Assert.Equal(480, doc.Screen?.Height);
        Assert.Equal(2, doc.Tiles.Count);
        Assert.Equal(new[] { "cpu" }, doc.Tiles[0].Sensors);
        Assert.Equal("CPU package", doc.Tiles[0].Title);
        Assert.Equal("Pump", doc.Tiles[1].Title);
        Assert.All(doc.Tiles, t => Assert.False(t.ShowMinMax));
        Assert.True(ScreenLayoutGrid.For(480, 480).Contains(doc.Tiles));
        Assert.True(doc.Tiles[0].RowSpan >= doc.Tiles[1].RowSpan);

        var none = CoolerScreenLayout.Create(new CoolerScreenPicks(null, null, null, null), 480, 480);
        Assert.Empty(none.Tiles);
    }

    // ---- Dashboard files: backgrounds, screen layouts, picture tiles ------------------------------------------------

    [Fact]
    public void Dashboard_RoundTripsBackgroundScreenAndPictureTiles()
    {
        var doc = new DashboardDocument
        {
            Name = "Cooler",
            Background = new DashboardBackground { Path = @"C:\b.gif", Fit = ImageFit.Fit, Opacity = 0.5 },
            Screen = new DashboardScreen { Width = 480, Height = 480 },
            Tiles = [new TileSpec { Type = WidgetType.Image, Media = @"C:\a.png", Fit = ImageFit.Fit, Opacity = 0.8, ColSpan = 4, RowSpan = 4 }],
        };

        var json = DashboardRepository.Serialize(doc);
        Assert.Contains("\"Image\"", json, StringComparison.Ordinal); // the widget by name
        var loaded = DashboardRepository.Deserialize(json)!;
        Assert.Equal(@"C:\b.gif", loaded.Background?.Path);
        Assert.Equal(ImageFit.Fit, loaded.Background?.Fit);
        Assert.Equal(0.5, loaded.Background?.Opacity);
        Assert.Equal(480, loaded.Screen?.Width);
        var tile = Assert.Single(loaded.Tiles);
        Assert.Equal(WidgetType.Image, tile.Type);
        Assert.Equal(@"C:\a.png", tile.Media);
        Assert.Equal(ImageFit.Fit, tile.Fit);
        Assert.Equal(0.8, tile.Opacity);

        var copy = loaded.Clone("Copy");
        Assert.NotSame(loaded.Background, copy.Background);
        Assert.Equal(@"C:\b.gif", copy.Background?.Path);
        Assert.Equal(480, copy.Screen?.Height);
        Assert.Equal(@"C:\a.png", copy.Tiles[0].Media);
        Assert.Equal(0.8, copy.Tiles[0].Opacity);
    }

    [Fact]
    public void Dashboard_Deserialize_RepairsBackgroundScreenAndOpacity()
    {
        var json = """
            {"name":"x","background":{"path":"  ","opacity":3},"screen":{"width":0,"height":480},
             "tiles":[{"type":"Image","media":"","opacity":-1,"colSpan":4,"rowSpan":4}]}
            """;
        var doc = DashboardRepository.Deserialize(json)!;
        Assert.Null(doc.Background); // no picture: no background
        Assert.Null(doc.Screen); // not a screen size
        Assert.Null(doc.Tiles[0].Media);
        Assert.Equal(DashboardBackground.MinOpacity, doc.Tiles[0].Opacity);

        var dim = DashboardRepository.Deserialize("""{"name":"x","background":{"path":"a.png","opacity":7}}""")!;
        Assert.Equal(1, dim.Background?.Opacity);
        Assert.Null(DashboardRepository.Deserialize("""{"name":"x"}""")!.Screen); // ordinary dashboards stay ordinary
    }

    [Fact]
    public void FindFreeSpot_StaysInsideAScreenLayoutsColumns()
    {
        var tiles = new List<TileSpec> { new() { Col = 0, Row = 0, ColSpan = 6, RowSpan = 4 } };
        Assert.Equal((0, 4), GridLayout.FindFreeSpot(tiles, 3, 3, 6));
        Assert.Equal((6, 0), GridLayout.FindFreeSpot(tiles, 3, 3));
        Assert.Equal((0, 4), GridLayout.FindFreeSpot(tiles, 12, 3, 6)); // too wide: as wide as the layout
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private static BgraImage Solid(int width, int height, byte b, byte g, byte r, byte a)
    {
        var image = new BgraImage(width, height);
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]) = (b, g, r, a);
        }

        return image;
    }

    /// <summary>Every pixel different: B = x, G = y, R = 100, A = 255.</summary>
    private static BgraImage Numbered(int width, int height)
    {
        var image = new BgraImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * image.Stride) + (x * 4);
                (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]) = ((byte)x, (byte)y, 100, 255);
            }
        }

        return image;
    }

    private static (byte B, byte G, byte R, byte A) Bgra(int b, int g, int r, int a) => ((byte)b, (byte)g, (byte)r, (byte)a);

    private static (byte B, byte G, byte R, byte A) Pixel(BgraImage image, int x, int y)
    {
        var i = (y * image.Stride) + (x * 4);
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
    }
}
