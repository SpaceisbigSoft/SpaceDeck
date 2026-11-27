using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.PluginPipe;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.Core.Tests;

public class ScreenTests
{
    private const string Hello = """{"t":"hello","v":1,"plugin":"Lian Li","version":"2.1.0","instance":"a"}""";

    private static readonly ScreenInfo Screen480 = new(480, 480);

    /// <summary>SOI, an APP0, a baseline frame header for 480×480 (one component), EOI.</summary>
    private static readonly byte[] MinimalJpeg480 =
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x01, 0xE0, 0x01, 0xE0, 0x01, 0x01, 0x11, 0x00,
        0xFF, 0xD9,
    ];

    private static PluginHardware? CatalogHardware(string hardwareJson)
    {
        var session = new PluginPipeSession();
        Assert.False(session.Accept(Encoding.UTF8.GetBytes(Hello)).IsError);
        var line = $$"""{"t":"catalog","hardware":[{{hardwareJson}}],"sensors":[]}""";
        var result = session.Accept(Encoding.UTF8.GetBytes(line));
        Assert.False(result.IsError, result.Error);
        return Assert.IsType<PluginCatalog>(result.Message).Hardware.Single();
    }

    // ---- Catalog "screen" field -------------------------------------------------------------------------------------

    [Fact]
    public void Catalog_ScreenField_IsRead()
    {
        var hardware = CatalogHardware("""{"id":"LianLi/0","name":"Lian Li GA II LCD","kind":"cooler","screen":{"width":480,"height":480,"format":"jpeg"}}""");
        Assert.Equal(new ScreenInfo(480, 480), hardware!.Screen);
        Assert.Equal(PluginHardwareKind.Cooler, hardware.Kind);
    }

    [Fact]
    public void Catalog_WithoutScreen_HasNone()
    {
        Assert.Null(CatalogHardware("""{"id":"LianLi/0","name":"Cooler","kind":"cooler"}""")!.Screen);
    }

    [Theory]
    [InlineData("""{"width":480,"height":480,"format":"png"}""")]
    [InlineData("""{"width":480,"height":480}""")]
    [InlineData("""{"width":0,"height":480,"format":"jpeg"}""")]
    [InlineData("""{"width":1025,"height":480,"format":"jpeg"}""")]
    [InlineData("""{"width":480,"height":-1,"format":"jpeg"}""")]
    [InlineData("""{"width":480.5,"height":480,"format":"jpeg"}""")]
    [InlineData("""{"width":"480","height":480,"format":"jpeg"}""")]
    [InlineData("""{"width":480,"height":480,"format":"JPEG"}""")]
    [InlineData("""{"width":99999999999,"height":480,"format":"jpeg"}""")]
    [InlineData("""{"height":480,"format":"jpeg"}""")]
    [InlineData("\"480x480\"")]
    [InlineData("null")]
    [InlineData("[480,480]")]
    public void Catalog_InvalidScreen_MeansNoScreen_NotADisconnect(string screen)
    {
        var hardware = CatalogHardware($$"""{"id":"LianLi/0","name":"Cooler","kind":"cooler","screen":{{screen}}}""");
        Assert.Null(hardware!.Screen);
    }

    [Fact]
    public void Catalog_LargestScreen_IsAccepted()
    {
        var hardware = CatalogHardware("""{"id":"x","name":"Panel","kind":"fans","screen":{"width":1024,"height":1,"format":"jpeg","fps":30}}""");
        Assert.Equal(new ScreenInfo(1024, 1), hardware!.Screen);
    }

    // ---- Frame validation -------------------------------------------------------------------------------------------

    [Fact]
    public void Validate_AcceptsAJpegOfTheScreensSize()
    {
        Assert.Null(ScreenFrameRules.Validate(MinimalJpeg480, Screen480));
        Assert.NotNull(ScreenFrameRules.Validate(MinimalJpeg480, new ScreenInfo(480, 320)));
    }

    [Fact]
    public void Validate_RejectsBadMarkersSizesAndHeaders()
    {
        Assert.NotNull(ScreenFrameRules.Validate([], Screen480));
        Assert.NotNull(ScreenFrameRules.Validate(MinimalJpeg480[..^1], Screen480)); // no EOI
        Assert.NotNull(ScreenFrameRules.Validate([0x00, .. MinimalJpeg480[1..]], Screen480)); // no SOI
        Assert.NotNull(ScreenFrameRules.Validate([0xFF, 0xD8, 0xFF, 0xD9], Screen480)); // no frame header

        var tooBig = new byte[ScreenFrameRules.MaxJpegBytes + 1];
        MinimalJpeg480.AsSpan(0, MinimalJpeg480.Length - 2).CopyTo(tooBig);
        tooBig[^2] = 0xFF;
        tooBig[^1] = 0xD9;
        Assert.NotNull(ScreenFrameRules.Validate(tooBig, Screen480));

        var largest = new byte[ScreenFrameRules.MaxJpegBytes]; // exactly 256 KiB is fine
        MinimalJpeg480.AsSpan(0, MinimalJpeg480.Length - 2).CopyTo(largest);
        largest[^2] = 0xFF;
        largest[^1] = 0xD9;
        Assert.Null(ScreenFrameRules.Validate(largest, Screen480));
    }

    [Fact]
    public void TryReadSize_WalksSegmentsFillBytesAndProgressiveHeaders()
    {
        byte[] progressive =
        [
            0xFF, 0xD8, 0xFF, 0xFF, 0xE1, 0x00, 0x05, 1, 2, 3, 0xFF, 0xD0,
            0xFF, 0xC2, 0x00, 0x0B, 0x08, 0x01, 0x40, 0x00, 0xF0, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9,
        ];
        Assert.True(ScreenFrameRules.TryReadSize(progressive, out var width, out var height));
        Assert.Equal(240, width);
        Assert.Equal(320, height);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9 })] // scan before a frame header
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x40, 0x00, 0xFF, 0xD9 })] // segment longer than the data
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01, 0xFF, 0xD9 })] // length below 2
    [InlineData(new byte[] { 0xFF, 0xD8, 0x12, 0x34, 0xFF, 0xD9 })] // not a marker
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x05, 0x08, 0x01, 0xE0, 0xFF, 0xD9 })] // frame header too short
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC4, 0x00, 0x02, 0xFF })] // ends in fill bytes
    public void TryReadSize_RejectsMalformedSegments(byte[] jpeg)
    {
        Assert.False(ScreenFrameRules.TryReadSize(jpeg, out _, out _));
    }

    // ---- Rate limit: token buckets ------------------------------------------------------------------------------------

    private const string Cli = "spacedeck-cli";

    private const string App = "SpaceDeck";

    /// <summary>
    /// 600 frame times from 1 s on at <paramref name="framesPerSecond"/>, each early or late by up to 16 ms (a timer tick and
    /// more): alternately 16 ms early and late, or spread over −16…+16 ms. Never before the frame ahead of it (at 60 a
    /// second the jitter is larger than the gap).
    /// </summary>
    private static List<long> JitteredFrames(int framesPerSecond, bool alternating)
    {
        var times = new List<long>();
        for (var i = 0; i < 600; i++)
        {
            var jitterMs = alternating ? (i % 2 == 0 ? -16 : 16) : ((i * 13) % 33) - 16;
            var at = TimeSpan.TicksPerSecond + (i * TimeSpan.TicksPerSecond / framesPerSecond) + (jitterMs * TimeSpan.TicksPerMillisecond);
            times.Add(times.Count == 0 ? at : Math.Max(at, times[^1]));
        }

        return times;
    }

    [Fact]
    public void Bucket_GivesItsCapacityAtOnce_ThenOneTokenEachThirtiethOfASecond()
    {
        var time = new ManualClock();
        var bucket = new ScreenFrameBucket(6, 30, time);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(TimeSpan.Zero, bucket.Wait());
            Assert.True(bucket.TryTake());
        }

        Assert.False(bucket.TryTake());
        Assert.Equal(TimeSpan.FromTicks(333_334), bucket.Wait()); // a thirtieth of a second, rounded up to a whole tick
        time.Advance(0.033);
        Assert.False(bucket.TryTake());
        Assert.Equal(TimeSpan.FromTicks(3_334), bucket.Wait());
        time.Advance(0.001);
        Assert.True(bucket.TryTake());

        time.Advance(10); // a quiet spell fills it, but no further than its capacity
        for (var i = 0; i < 6; i++)
        {
            Assert.True(bucket.TryTake());
        }

        Assert.False(bucket.TryTake());
    }

    [Fact]
    public void Bucket_TakeGoesBeyondTheTokens_AndWaitSaysWhenTheyArePaidBack()
    {
        var time = new ManualClock();
        var bucket = new ScreenFrameBucket(1, 30, time);
        bucket.Take();
        bucket.Take();
        Assert.Equal(TimeSpan.FromTicks(666_667), bucket.Wait());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenFrameBucket(0, 30, time));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenFrameBucket(6, 0, time));
    }

    [Fact]
    public void Gate_LetsSixFramesAtOnce_ThenThirtyASecond_PerScreen()
    {
        Assert.Equal(30, ScreenFrameRules.MaxFramesPerSecond);
        Assert.Equal(6, ScreenFrameRules.BurstFrames);
        var time = new ManualClock();
        var gate = new ScreenFrameGate(time);

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("LianLi/0", 1, Cli));
        }

        Assert.Equal(ScreenFrameVerdict.OverLimit, gate.TryAccept("LianLi/0", 1, Cli)); // a 7th at once
        Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("LianLi/1", 1, Cli)); // another screen has its own bucket
        time.Advance(0.033);
        Assert.Equal(ScreenFrameVerdict.OverLimit, gate.TryAccept("LianLi/0", 1, Cli)); // a thirtieth of a second is 33.3 ms
        time.Advance(0.001);
        Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("LianLi/0", 1, Cli));
        Assert.Equal(ScreenFrameVerdict.OverLimit, gate.TryAccept("LianLi/0", 1, Cli));
    }

    [Fact]
    public void Gate_RefusedFramesDontUseTheBudget()
    {
        var time = new ManualClock();
        var gate = new ScreenFrameGate(time);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("s", 1, Cli));
        }

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ScreenFrameVerdict.OverLimit, gate.TryAccept("s", 1, Cli));
        }

        time.Advance(0.2); // six thirtieths of a second: the bucket is full again, however many were refused
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("s", 1, Cli));
        }

        Assert.Equal(ScreenFrameVerdict.OverLimit, gate.TryAccept("s", 1, Cli));
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(30, false)]
    [InlineData(24, true)]
    [InlineData(30, true)]
    public void Gate_AcceptsEveryFrameOfAStreamOfUpToThirtyASecond_HoweverItJitters(int framesPerSecond, bool alternating)
    {
        var time = new ManualClock();
        var gate = new ScreenFrameGate(time);
        foreach (var at in JitteredFrames(framesPerSecond, alternating))
        {
            time.Set(at);
            Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("LianLi/0", 1, Cli));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate_HoldsSixtyASecondToThirty(bool alternating)
    {
        var time = new ManualClock();
        var gate = new ScreenFrameGate(time);
        var accepted = 0;
        foreach (var at in JitteredFrames(60, alternating))
        {
            time.Set(at);
            accepted += gate.TryAccept("LianLi/0", 1, Cli) == ScreenFrameVerdict.Accepted ? 1 : 0;
        }

        Assert.InRange(accepted, 300, 306); // ten seconds at 30, and the first burst of 6
    }

    [Fact]
    public void Gate_TwoInterleavedSenders_OnlyTheFirstOnesFramesPass()
    {
        var time = new ManualClock();
        var gate = new ScreenFrameGate(time);
        Assert.Empty(gate.Senders());

        for (var i = 0; i < 100; i++)
        {
            var (sender, name) = i % 2 == 0 ? (1L, Cli) : (2L, App);
            Assert.Equal(i % 2 == 0 ? ScreenFrameVerdict.Accepted : ScreenFrameVerdict.AnotherSender, gate.TryAccept("LianLi/0", sender, name));
            time.Advance(0.02);
        }

        Assert.Equal(new[] { Cli }, gate.Senders());
        Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("LianLi/1", 2, App)); // another screen is free for it
        Assert.Equal(new[] { App, Cli }, gate.Senders());
    }

    [Fact]
    public void Gate_TheNextSenderTakesOver_AfterTheOwnerSentNothingForThreeSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), ScreenFrameRules.SenderIdle);
        var time = new ManualClock();
        var gate = new ScreenFrameGate(time);
        Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("LianLi/0", 1, Cli));
        time.Advance(2.999);
        Assert.Equal(ScreenFrameVerdict.AnotherSender, gate.TryAccept("LianLi/0", 2, App)); // doesn't keep the screen busy
        time.Advance(0.001);
        Assert.Empty(gate.Senders());
        Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("LianLi/0", 2, App));
        Assert.Equal(ScreenFrameVerdict.AnotherSender, gate.TryAccept("LianLi/0", 1, Cli));
        Assert.Equal(new[] { App }, gate.Senders());

        // Frames over the rate still count as sending: the owner keeps the screen while it sends at all.
        for (var i = 0; i < 40; i++)
        {
            gate.TryAccept("LianLi/0", 2, App);
            time.Advance(0.1);
            Assert.Equal(ScreenFrameVerdict.AnotherSender, gate.TryAccept("LianLi/0", 1, Cli));
        }
    }

    [Fact]
    public void Gate_TheNextSenderTakesOver_WhenTheOwnerDisconnects()
    {
        var time = new ManualClock();
        var gate = new ScreenFrameGate(time);
        Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("LianLi/0", 1, Cli));
        gate.Forget(3); // a client that holds nothing
        Assert.Equal(ScreenFrameVerdict.AnotherSender, gate.TryAccept("LianLi/0", 2, App));

        gate.Forget(1);
        Assert.Empty(gate.Senders());
        Assert.Equal(ScreenFrameVerdict.Accepted, gate.TryAccept("LianLi/0", 2, App));
        Assert.Equal(ScreenFrameVerdict.AnotherSender, gate.TryAccept("LianLi/0", 1, Cli));
    }

    // ---- Screen pipe pacing -------------------------------------------------------------------------------------------

    /// <summary>
    /// The screen pipe's writer on a manual clock: each frame is put in the mailbox as it comes and written at once unless
    /// the writer sleeps; a writer without a token sleeps until one is due, rounded up to Windows' 15.625 ms timer tick (the
    /// wake a real Task.Delay gets). Returns the frames written.
    /// </summary>
    private static int RunWriter(IEnumerable<long> arrivals, ManualClock time, ScreenFrameMailbox mailbox)
    {
        const long TimerTick = 156_250;
        var pacer = new ScreenPipePacer(mailbox, time);
        var written = 0;
        long? wake = null; // when the sleeping writer wakes; null while it waits for a frame

        long? Drain()
        {
            while (true)
            {
                if (pacer.TryTake(out var wait) is not null)
                {
                    written++;
                    continue;
                }

                if (wait == TimeSpan.Zero)
                {
                    return null;
                }

                var dueAt = time.Ticks + wait.Ticks;
                return (dueAt + TimerTick - 1) / TimerTick * TimerTick;
            }
        }

        foreach (var at in arrivals)
        {
            while (wake is { } due && due <= at)
            {
                time.Set(due);
                wake = Drain();
            }

            time.Set(at);
            mailbox.Put("LianLi/0", [1]);
            wake ??= Drain();
        }

        while (wake is { } due)
        {
            time.Set(due);
            wake = Drain();
        }

        return written;
    }

    [Fact]
    public void Pacer_WritesAFrameTheMomentItIsThere_SixAtOnce_ThenWhenATokenIsDue()
    {
        Assert.Equal(30, ScreenPipePacer.MessagesPerSecond);
        Assert.Equal(6, ScreenPipePacer.BurstMessages);
        var time = new ManualClock();
        var mailbox = new ScreenFrameMailbox(time);
        var pacer = new ScreenPipePacer(mailbox, time);
        Assert.Null(pacer.TryTake(out var wait));
        Assert.Equal(TimeSpan.Zero, wait); // nothing waiting

        for (var i = 0; i < 7; i++)
        {
            mailbox.Put($"s{i}", [(byte)i]);
            time.Advance(0.001); // the oldest goes first
        }

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal($"s{i}", pacer.TryTake(out wait)?.ScreenId);
            Assert.Equal(TimeSpan.Zero, wait);
        }

        Assert.Null(pacer.TryTake(out wait));
        Assert.Equal(TimeSpan.FromTicks(333_334), wait);
        mailbox.Put("s6", [42]); // replaced while the writer waits: the newest goes out
        time.Advance(wait.TotalSeconds);
        var frame = pacer.TryTake(out wait);
        Assert.Equal(new byte[] { 42 }, frame?.Jpeg);
        Assert.Equal(TimeSpan.Zero, wait);
        Assert.Null(pacer.TryTake(out wait));
        Assert.True(wait > TimeSpan.Zero); // no token yet: the writer comes back, finds nothing and waits for a frame
        Assert.Throws<ArgumentNullException>(() => new ScreenPipePacer(null!, time));
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(30, false)]
    [InlineData(24, true)]
    [InlineData(30, true)]
    public void Pacer_LosesNoFrameOfAStreamOfUpToThirtyASecond_HoweverItJitters(int framesPerSecond, bool alternating)
    {
        var time = new ManualClock();
        var mailbox = new ScreenFrameMailbox(time);
        Assert.Equal(600, RunWriter(JitteredFrames(framesPerSecond, alternating), time, mailbox));
        Assert.Equal(0, mailbox.Dropped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pacer_HoldsSixtyASecondToThirty_TheNewestWaiting(bool alternating)
    {
        var time = new ManualClock();
        var mailbox = new ScreenFrameMailbox(time);
        var written = RunWriter(JitteredFrames(60, alternating), time, mailbox);
        Assert.InRange(written, 300, 307); // ten seconds at 30, the first burst of 6, and the last one waiting
        Assert.Equal(600, written + mailbox.Dropped);
    }

    // ---- Frames a second, and the status line's counts -----------------------------------------------------------------

    [Fact]
    public void Rate_CountsTheFramesOfTheLastFiveSeconds()
    {
        var time = new ManualClock();
        var rate = new ScreenFrameRate(time);
        Assert.Equal(0, rate.PerSecond());

        for (var i = 0; i < 100; i++)
        {
            rate.Count();
            time.Advance(0.04); // 25 a second
        }

        Assert.Equal(TimeSpan.FromSeconds(5), ScreenFrameRate.Window);
        Assert.Equal(20, rate.PerSecond(), 3); // 100 frames over 4 s, counted against the 5 s window
        time.Advance(1); // the first frame is now 5 s old
        Assert.Equal(19.8, rate.PerSecond(), 3); // and drops out
        time.Advance(4);
        Assert.Equal(0, rate.PerSecond());
    }

    [Fact]
    public void Rate_KeepsABoundedNumberOfTimes()
    {
        var rate = new ScreenFrameRate(new ManualClock());
        for (var i = 0; i < 5000; i++)
        {
            rate.Count();
        }

        Assert.Equal(1024 / 5.0, rate.PerSecond(), 3);
    }

    [Fact]
    public void Counts_AreReadBackFromTheStatusLine()
    {
        var line = "Screen pipe connected · " + new ScreenPipeCounts(1234, 56, 29.84).Format();
        Assert.Equal("Screen pipe connected · 1234 frames sent · 56 dropped · 29.8 frames/s", line);

        var counts = ScreenPipeCounts.TryFind(line);
        Assert.NotNull(counts);
        Assert.Equal(1234, counts.Sent);
        Assert.Equal(56, counts.Dropped);
        Assert.Equal(29.8, counts.FramesPerSecond, 3);
        Assert.Equal(0, counts.RefusedOtherSender);
        Assert.Null(counts.Sender);
    }

    [Fact]
    public void Counts_WithFramesRefusedForAnotherSender_AndTheSenders_AreReadBack()
    {
        var line = "Screen pipe connected · " + new ScreenPipeCounts(10, 2, 30, 45, "SpaceDeck, spacedeck-cli").Format();
        Assert.Equal("Screen pipe connected · 10 frames sent · 2 dropped · 30.0 frames/s · 45 refused (another sender) · from SpaceDeck, spacedeck-cli", line);
        var counts = ScreenPipeCounts.TryFind(line);
        Assert.NotNull(counts);
        Assert.Equal(45, counts.RefusedOtherSender);
        Assert.Equal("SpaceDeck, spacedeck-cli", counts.Sender);

        var fromOnly = ScreenPipeCounts.TryFind(new ScreenPipeCounts(1, 0, 1, sender: "SpaceDeck").Format());
        Assert.NotNull(fromOnly);
        Assert.Equal(0, fromOnly.RefusedOtherSender);
        Assert.Equal("SpaceDeck", fromOnly.Sender);

        var tooMany = ScreenPipeCounts.TryFind("1 frames sent · 0 dropped · 1.0 frames/s · 9999999999999999999 refused (another sender)");
        Assert.NotNull(tooMany);
        Assert.Equal(0, tooMany.RefusedOtherSender); // past a long: not a count
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Screen pipe connected · 12 frames sent · 3 dropped")] // an older collector, without the rate
    [InlineData("Screen pipe connected · 99999999999999999999 frames sent · 3 dropped · 1.0 frames/s")] // too many digits
    public void Counts_AreNotFoundInALineWithout(string? line)
    {
        Assert.Null(ScreenPipeCounts.TryFind(line));
    }

    // ---- spacedeck-cli screen-test settings ---------------------------------------------------------------------------

    [Fact]
    public void TestSettings_Default_To30SecondsAtOneFrameASecond()
    {
        Assert.Equal(new ScreenTestSettings(30, 1, 85), ScreenTestSettings.Parse(null, null, null, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("24", 24)]
    [InlineData("30", 30)]
    public void TestSettings_TakeAFrameRateFromOneToThirty(string fps, int expected)
    {
        Assert.Equal(expected, ScreenTestSettings.Parse("60", fps, null, out var error)?.FramesPerSecond);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("31")]
    [InlineData("-5")]
    [InlineData("2.5")]
    [InlineData("fast")]
    [InlineData("")]
    public void TestSettings_RefuseAFrameRateOutsideOneToThirty(string fps)
    {
        Assert.Null(ScreenTestSettings.Parse(null, fps, null, out var error));
        Assert.Equal($"--fps must be a whole number from 1 to 30 (frames a second), not '{fps}'.", error);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("abc", 30)]
    [InlineData("1", 5)]
    [InlineData("120", 120)]
    [InlineData("100000", 600)]
    public void TestSettings_KeepTheSecondsWithinFiveToSixHundred(string? seconds, int expected)
    {
        Assert.Equal(expected, ScreenTestSettings.Parse(seconds, "10", null, out _)?.Seconds);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("60", 60)]
    [InlineData("100", 100)]
    public void TestSettings_TakeAJpegQualityFromThirtyToAHundred(string quality, int expected)
    {
        Assert.Equal(expected, ScreenTestSettings.Parse(null, "24", quality, out var error)?.Quality);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("29")]
    [InlineData("101")]
    [InlineData("0")]
    [InlineData("85.5")]
    [InlineData("high")]
    [InlineData("")]
    public void TestSettings_RefuseAJpegQualityOutsideThirtyToAHundred(string quality)
    {
        Assert.Null(ScreenTestSettings.Parse(null, "24", quality, out var error));
        Assert.Equal($"--quality must be a whole number from 30 to 100 (JPEG quality), not '{quality}'.", error);
    }

    // ---- Newest frame only, never older than 5 s ---------------------------------------------------------------------

    [Fact]
    public void Mailbox_KeepsTheNewestFramePerScreen_AndHandsItOutOnce()
    {
        var time = new ManualClock();
        var mailbox = new ScreenFrameMailbox(time);
        var ready = 0;
        mailbox.FrameReady += () => ready++;

        Assert.True(mailbox.Put("LianLi/0", [1]));
        Assert.True(mailbox.Put("LianLi/0", [2]));
        Assert.Equal(2, ready);
        Assert.Equal(1, mailbox.Dropped); // the first was replaced unsent

        var frame = mailbox.Take();
        Assert.NotNull(frame);
        Assert.Equal("LianLi/0", frame.ScreenId);
        Assert.Equal(new byte[] { 2 }, frame.Jpeg);
        Assert.Null(mailbox.Take());
    }

    [Fact]
    public void Mailbox_NeverHandsOutAFrameOlderThanFiveSeconds()
    {
        var time = new ManualClock();
        var mailbox = new ScreenFrameMailbox(time);
        mailbox.Put("LianLi/0", [1]);
        time.Advance(5);
        Assert.NotNull(mailbox.Take()); // exactly 5 s is still in time

        mailbox.Put("LianLi/0", [2]);
        time.Advance(5.01);
        Assert.Null(mailbox.Take());
        Assert.Equal(1, mailbox.Dropped);

        mailbox.Put("LianLi/0", [3]);
        time.Advance(6);
        Assert.Equal(1, mailbox.DropStale());
        Assert.Equal(2, mailbox.Dropped);
        Assert.Null(mailbox.Take());
    }

    [Fact]
    public void Mailbox_HandsOutTheOldestWaitingFrameFirst()
    {
        var time = new ManualClock();
        var mailbox = new ScreenFrameMailbox(time);
        mailbox.Put("b", [1]);
        time.Advance(0.1);
        mailbox.Put("a", [2]);

        Assert.Equal("b", mailbox.Take()!.ScreenId);
        Assert.Equal("a", mailbox.Take()!.ScreenId);
    }

    [Fact]
    public void Mailbox_HasABoundedNumberOfSlots()
    {
        var mailbox = new ScreenFrameMailbox(new ManualClock());
        for (var i = 0; i < ScreenFrameMailbox.MaxScreens; i++)
        {
            Assert.True(mailbox.Put($"s{i}", [1]));
        }

        Assert.False(mailbox.Put("one-too-many", [1]));
        Assert.True(mailbox.Put("s0", [2])); // an existing screen still gets its newest frame
    }

    // ---- Screen pipe framing ----------------------------------------------------------------------------------------

    [Fact]
    public void Encode_ProducesTheDocumentedBytes()
    {
        var message = ScreenPipeProtocol.Encode("LianLi/0", [0xFF, 0xD8, 0xFF, 0xD9]);
        byte[] expected =
        [
            0x53, 0x44, 0x53, 0x46, // "SDSF"
            0x01, // version
            0x01, // kind: JPEG frame
            0x08, 0x00, // id length 8
            0x4C, 0x69, 0x61, 0x6E, 0x4C, 0x69, 0x2F, 0x30, // "LianLi/0"
            0x04, 0x00, 0x00, 0x00, // payload length 4
            0xFF, 0xD8, 0xFF, 0xD9,
        ];
        Assert.Equal(expected, message);
    }

    [Fact]
    public void Encode_LargestFrame_HasLittleEndianLengths()
    {
        var jpeg = new byte[ScreenPipeProtocol.MaxPayloadBytes];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        jpeg[^2] = 0xFF;
        jpeg[^1] = 0xD9;
        var id = new string('a', 128);

        var message = ScreenPipeProtocol.Encode(id, jpeg);
        Assert.Equal(8 + 128 + 4 + jpeg.Length, message.Length);
        Assert.Equal(new byte[] { 0x80, 0x00 }, message[6..8]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x04, 0x00 }, message[136..140]); // 262144
        Assert.Equal(jpeg, message[140..]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Lian Li/0")]
    [InlineData("LianLi/é")]
    public void Encode_RejectsIdsTheCatalogWouldReject(string id)
    {
        Assert.Throws<ArgumentException>(() => ScreenPipeProtocol.Encode(id, [0xFF, 0xD8, 0xFF, 0xD9]));
    }

    [Fact]
    public void Encode_RejectsPayloadsThePluginWouldReject()
    {
        Assert.Throws<ArgumentException>(() => ScreenPipeProtocol.Encode(new string('a', 129), [0xFF, 0xD8, 0xFF, 0xD9]));
        Assert.Throws<ArgumentException>(() => ScreenPipeProtocol.Encode("LianLi/0", []));
        Assert.Throws<ArgumentException>(() => ScreenPipeProtocol.Encode("LianLi/0", [0xFF, 0xD8, 0x00, 0xD9]));
        Assert.Throws<ArgumentException>(() => ScreenPipeProtocol.Encode("LianLi/0", [0x00, 0xD8, 0xFF, 0xD9]));
        Assert.Throws<ArgumentException>(() => ScreenPipeProtocol.Encode("LianLi/0", new byte[ScreenPipeProtocol.MaxPayloadBytes + 1]));
    }

    // ---- Settings ---------------------------------------------------------------------------------------------------

    /// <summary>The app's settings.json options (AppSettings).</summary>
    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Settings_RoundTripThroughJson()
    {
        var settings = new CoolerScreenSettings
        {
            HardwareId = "lianli:/LianLi/0",
            Enabled = true,
            DashboardId = "abc123",
            IntervalSeconds = 3,
            Rotation = 270,
            JpegQuality = 70,
        };

        var json = JsonSerializer.Serialize(new List<CoolerScreenSettings> { settings }, SettingsJson);
        var loaded = Assert.Single(JsonSerializer.Deserialize<List<CoolerScreenSettings>>(json, SettingsJson)!);
        Assert.Equal("lianli:/LianLi/0", loaded.HardwareId);
        Assert.True(loaded.Enabled);
        Assert.Equal("abc123", loaded.DashboardId);
        Assert.Equal(3, loaded.IntervalSeconds);
        Assert.Equal(270, loaded.Rotation);
        Assert.Equal(70, loaded.JpegQuality);
    }

    [Fact]
    public void Settings_DefaultToOff_TwoSeconds_Quality85()
    {
        var loaded = JsonSerializer.Deserialize<CoolerScreenSettings>("""{"HardwareId":"lianli:/LianLi/0"}""", SettingsJson)!;
        Assert.False(loaded.Enabled);
        Assert.Null(loaded.DashboardId);
        Assert.Equal(2, loaded.IntervalSeconds);
        Assert.Equal(0, loaded.Rotation);
        Assert.Equal(85, loaded.JpegQuality);
    }

    [Fact]
    public void Settings_Clean_DropsNullsBlanksAndDuplicates_AndClampsValues()
    {
        var json = """
            [null,
             {"HardwareId":""},
             {"HardwareId":"lianli:/LianLi/0","IntervalSeconds":0,"JpegQuality":500,"Rotation":-90,"DashboardId":" "},
             {"HardwareId":"lianli:/LianLi/0","Enabled":true}]
            """;
        var cleaned = CoolerScreenSettings.Clean(JsonSerializer.Deserialize<List<CoolerScreenSettings?>>(json, SettingsJson));
        var screen = Assert.Single(cleaned);
        Assert.False(screen.Enabled); // the first entry for an id wins
        Assert.Equal(1, screen.IntervalSeconds);
        Assert.Equal(100, screen.JpegQuality);
        Assert.Equal(270, screen.Rotation);
        Assert.Null(screen.DashboardId);
        Assert.Empty(CoolerScreenSettings.Clean(null));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(100, 90)]
    [InlineData(450, 90)]
    [InlineData(330, 0)]
    public void Settings_RotationIsAQuarterTurn(int degrees, int expected)
    {
        Assert.Equal(expected, CoolerScreenSettings.NormalizeRotation(degrees));
    }

    // ---- Test pattern and JPEG encoder ------------------------------------------------------------------------------

    private static byte[] Gradient(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                rgb[i] = (byte)((x * 16) & 255);
                rgb[i + 1] = (byte)((y * 16) & 255);
                rgb[i + 2] = (byte)(((x + y) * 8) & 255);
            }
        }

        return rgb;
    }

    /// <summary>
    /// The entropy-coded data after the SOS header, against a reference encoder (a line-by-line Python port decoded and
    /// checked with Pillow: largest error 5 levels at quality 85). Covers edge padding (13×11) and FF 00 stuffing.
    /// </summary>
    [Theory]
    [InlineData(16, 16, "f0cf0e7843eefeebf4a29550c933bdb53d27c39e10fbbfbafd2bd1a554fd7b24cef6d4eefc39e10fbbfbafd2be769553f90f24cef6d4f49f0e7843eefeebf4af469553f5ec933bdb53ffd9")]
    [InlineData(13, 11, "f0cf0e7843eefeebf4a29550c933bdb53d0b4af087fa3ffaafd2bbe15743f55cbf3bfdd6e747e1cb5b7f97f74b5e1d26cfe62c92b4f4d4f42d2ad6dfecff00ea96bbe0dd8fd572fad3f65b9fffd9")]
    public void JpegEncoder_MatchesTheReferenceEncoder(int width, int height, string scanHex)
    {
        var jpeg = BaselineJpegEncoder.Encode(Gradient(width, height), width, height, 85);
        ReadOnlySpan<byte> startOfScan = [0xFF, 0xDA];
        var sos = jpeg.AsSpan().IndexOf(startOfScan);
        Assert.True(sos > 0);
        Assert.Equal(scanHex, Convert.ToHexStringLower(jpeg.AsSpan(sos + 14)));
        Assert.True(ScreenFrameRules.TryReadSize(jpeg, out var w, out var h));
        Assert.Equal((width, height), (w, h));
    }

    [Fact]
    public void TestPattern_EncodesToAValidScreenFrame()
    {
        var rgb = ScreenTestPattern.Render(480, 480, new TimeOnly(12, 34, 56), 7);
        Assert.Equal(480 * 480 * 3, rgb.Length);

        var jpeg = BaselineJpegEncoder.Encode(rgb, 480, 480, 85);
        Assert.Null(ScreenFrameRules.Validate(jpeg, Screen480));
        Assert.True(jpeg.Length < 100 * 1024, $"{jpeg.Length} bytes");

        Assert.NotEqual(rgb, ScreenTestPattern.Render(480, 480, new TimeOnly(12, 34, 56), 8)); // the marker moves
        Assert.NotEqual(rgb, ScreenTestPattern.Render(480, 480, new TimeOnly(12, 34, 57), 7)); // the time changes
    }

    [Fact]
    public void TestPattern_ForAFrameRateTest_SweepsABarByTheTime_AndWritesTheRate()
    {
        var time = new TimeOnly(12, 34, 56);
        var plain = ScreenTestPattern.Render(480, 480, time, 7);
        Assert.Equal(plain, ScreenTestPattern.Render(480, 480, time, 7, null, null));

        var atStart = ScreenTestPattern.Render(480, 480, time, 7, TimeSpan.Zero, null);
        Assert.NotEqual(plain, atStart);
        Assert.NotEqual(atStart, ScreenTestPattern.Render(480, 480, time, 7, TimeSpan.FromMilliseconds(500), null)); // the bar moves
        Assert.Equal(atStart, ScreenTestPattern.Render(480, 480, time, 7, TimeSpan.FromSeconds(2), null)); // once across in 2 s
        Assert.Equal(atStart, ScreenTestPattern.Render(480, 480, time, 7, TimeSpan.FromSeconds(-2), null));

        var at29 = ScreenTestPattern.Render(480, 480, time, 7, TimeSpan.Zero, 29.8);
        Assert.NotEqual(atStart, at29);
        Assert.NotEqual(at29, ScreenTestPattern.Render(480, 480, time, 7, TimeSpan.Zero, 12.0));
        Assert.Null(ScreenFrameRules.Validate(BaselineJpegEncoder.Encode(at29, 480, 480, 85), Screen480));
    }

    [Fact]
    public void TestPattern_WorksForOddSizes()
    {
        foreach (var (w, h) in new[] { (1, 1), (7, 3), (320, 240), (1024, 1) })
        {
            var rgb = ScreenTestPattern.Render(w, h, new TimeOnly(23, 59, 59), 12345, TimeSpan.FromSeconds(1.3), 29.8);
            var jpeg = BaselineJpegEncoder.Encode(rgb, w, h, 85);
            Assert.Null(ScreenFrameRules.Validate(jpeg, new ScreenInfo(w, h)));
        }
    }

    [Fact]
    public void JpegEncoder_RejectsAWrongBufferSize()
    {
        Assert.Throws<ArgumentException>(() => BaselineJpegEncoder.Encode(new byte[10], 2, 2, 85));
        Assert.Throws<ArgumentException>(() => BaselineJpegEncoder.Encode([], 0, 0, 85));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public long Ticks => _ticks;

        public void Advance(double seconds) => _ticks += (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

        public void Set(long ticks) => _ticks = ticks;
    }
}
