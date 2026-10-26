using SensorDeck.Core.Model;
using SensorDeck.Core.Replay;

namespace SensorDeck.Core.Tests;

public class ReplayTests
{
    private static SensorCatalog Catalog(params string[] names)
    {
        HardwareInfo[] hardware = [new("lhm:/cpu/0", "CPU", HardwareKind.Cpu, null, "lhm")];
        var sensors = names.Select((n, i) => new SensorInfo($"lhm:/cpu/0/temperature/{i}", "lhm:/cpu/0", n, SensorKind.Temperature, "°C", "lhm", null)).ToArray();
        return new SensorCatalog(1, hardware, sensors);
    }

    private static SensorRecording RoundTrip(Action<SensorRecordingWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new SensorRecordingWriter(new NonClosingStream(stream), "test machine", 1000))
        {
            write(writer);
        }

        stream.Position = 0;
        return SensorRecording.Read(stream);
    }

    [Fact]
    public void Recording_round_trips_catalogs_values_and_nulls()
    {
        var first = Catalog("Package", "Core Max");
        var second = Catalog("Package", "Core Max", "Core Average");
        var recording = RoundTrip(w =>
        {
            w.WriteCatalog(first);
            w.WriteValues(10_000, new float?[] { 50.5f, float.NaN });
            w.WriteValues(11_000, new float?[] { 51f, null });
            w.WriteCatalog(second);
            w.WriteValues(12_000, new float?[] { 52f, 60f, 48f });
        });

        Assert.Equal("test machine", recording.Description);
        Assert.Equal(2, recording.Catalogs.Count);
        Assert.Equal(3, recording.Catalogs[1].Sensors.Count);
        Assert.Equal("Core Average", recording.Catalogs[1].Sensors[2].Name);
        Assert.Equal(SensorKind.Temperature, recording.Catalogs[0].Sensors[0].Kind);
        Assert.Equal(3, recording.Frames.Count);
        Assert.Equal(0, recording.Frames[0].OffsetMs);
        Assert.Equal(2000, recording.Frames[2].OffsetMs);
        Assert.Equal(50.5f, recording.Frames[0].Values[0]);
        Assert.Null(recording.Frames[0].Values[1]); // NaN is stored as "no value"
        Assert.Equal(1, recording.Frames[2].CatalogIndex);
        Assert.Equal(2000, recording.DurationMs);
        Assert.False(recording.IsSnapshot);
    }

    private static MemoryStream Gzip(string text)
    {
        var stream = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip))
        {
            writer.Write(text);
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Catalog_sensor_without_id_is_rejected()
    {
        using var stream = Gzip("""
            {"format":"sensordeck-recording","version":1}
            {"catalog":{"version":1,"hardware":[],"sensors":[{"name":"x","kind":"Temperature"}]}}
            {"t":0,"v":[1]}

            """);
        Assert.Throws<InvalidDataException>(() => SensorRecording.Read(stream));
    }

    [Fact]
    public void Catalog_missing_names_and_null_hardware_are_repaired()
    {
        using var stream = Gzip("""
            {"format":"sensordeck-recording","version":1}
            {"catalog":{"version":1,"hardware":[null],"sensors":[{"id":"a"}]}}
            {"t":0,"v":[1]}

            """);
        var recording = SensorRecording.Read(stream);
        Assert.Empty(recording.Catalogs[0].Hardware);
        Assert.Equal("a", recording.Catalogs[0].Sensors[0].Name);
        Assert.Equal("", recording.Catalogs[0].Sensors[0].Unit);
    }

    [Fact]
    public void Values_before_the_first_catalog_are_ignored()
    {
        Assert.Throws<InvalidDataException>(() => RoundTrip(w => w.WriteValues(1, new float?[] { 1f })));
    }

    [Fact]
    public void Garbage_is_rejected_clearly()
    {
        using var stream = new MemoryStream("hello"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => SensorRecording.Read(stream));
        Assert.Throws<InvalidDataException>(() => HardwareReport.Parse("""{ "nothing": 1 }"""));
        Assert.Throws<InvalidDataException>(() => HardwareReport.Parse("not json"));
    }

    [Fact]
    public void Cli_hardware_report_loads_as_one_frame()
    {
        var recording = HardwareReport.Parse("""
            {
              "generatedAt": "2026-10-23T10:00:00-04:00",
              "hardware": [
                { "id": "lhm:/amdcpu/0", "name": "AMD Ryzen 7 7840HS", "kind": "Cpu", "parentId": null,
                  "sensors": [
                    { "id": "lhm:/amdcpu/0/temperature/0", "name": "Core (Tctl/Tdie)", "kind": "Temperature", "unit": "°C", "value": 68.5, "min": 40, "max": 90, "nominalValue": null },
                    { "id": "lhm:/amdcpu/0/voltage/0", "name": "Core (SVI2 TFN)", "kind": "Voltage", "unit": "V", "value": "NaN", "nominalValue": 1.2 }
                  ] }
              ]
            }
            """);

        Assert.True(recording.IsSnapshot);
        var catalog = recording.Catalogs[0];
        Assert.Equal(HardwareKind.Cpu, catalog.Hardware[0].Kind);
        Assert.Equal("lhm", catalog.Sensors[0].Source);
        Assert.Equal(68.5f, recording.Frames[0].Values[0]);
        Assert.Null(recording.Frames[0].Values[1]);
        Assert.Equal(1.2, catalog.Sensors[1].NominalValue);
    }

    [Fact]
    public void App_hardware_report_derives_hardware_and_skips_calculated_sensors()
    {
        var recording = HardwareReport.Parse("""
            {
              "Generated": "2026-10-23T10:00:00-04:00",
              "Board": "MSI PRO Z790-A",
              "Sensors": [
                { "Id": "lhm:/lpc/nct6798d/0/voltage/0", "Name": "Vcore", "Hardware": "Nuvoton NCT6798D", "HardwareKind": "SuperIo", "Kind": "Voltage", "Value": 1.21, "Nominal": null },
                { "Id": "lhm:/lpc/nct6798d/0/fan/1", "Name": "CPU Fan", "Hardware": "Nuvoton NCT6798D", "HardwareKind": "SuperIo", "Kind": "Fan", "Value": 900 },
                { "Id": "calc:abc", "Name": "Delta", "Hardware": "Calculated", "HardwareKind": "Other", "Kind": "TemperatureDifference", "Value": 3 }
              ]
            }
            """);

        var catalog = recording.Catalogs[0];
        var hardware = Assert.Single(catalog.Hardware);
        Assert.Equal("lhm:/lpc/nct6798d/0", hardware.Id);
        Assert.Equal("Nuvoton NCT6798D", hardware.Name);
        Assert.Equal(HardwareKind.SuperIo, hardware.Kind);
        Assert.Equal(2, catalog.Sensors.Count);
        Assert.Equal("RPM", catalog.Sensors[1].Unit);
        Assert.Contains("MSI PRO Z790-A", recording.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_follows_the_clock_and_loops()
    {
        var catalog = Catalog("Package");
        var recording = new SensorRecording(
            [catalog],
            [new RecordingFrame(0, 0, [10f]), new RecordingFrame(1000, 0, [20f]), new RecordingFrame(2000, 0, [30f])]);
        var time = new ManualTime();
        using var source = new ReplaySource(recording, time);
        source.Open();
        Assert.Equal(SourceStatus.Ok, source.Status);
        Assert.Single(source.Catalog.Sensors);

        var buffer = new SensorReading[1];
        float? At(long ms)
        {
            time.Now = ms;
            source.Update();
            source.TryRefreshCatalog();
            source.Read(buffer);
            return buffer[0].Value;
        }

        Assert.Equal(10f, At(0));
        Assert.Equal(10f, At(999));
        Assert.Equal(20f, At(1500));
        Assert.Equal(30f, At(2500));
        Assert.Equal(10f, At(3000)); // 2 s recording + 1 s pause, then from the start
        Assert.Equal(10f, buffer[0].Min);
        Assert.Equal(30f, buffer[0].Max);
    }

    [Fact]
    public void Replay_switches_catalogs_mid_recording()
    {
        var recording = new SensorRecording(
            [Catalog("A"), Catalog("A", "B")],
            [new RecordingFrame(0, 0, [1f]), new RecordingFrame(1000, 1, [2f, 3f])]);
        var time = new ManualTime();
        using var source = new ReplaySource(recording, time);
        source.Open();
        time.Now = 1200;
        source.Update();
        Assert.True(source.TryRefreshCatalog());
        Assert.Equal(2, source.Catalog.Sensors.Count);
        var buffer = new SensorReading[2];
        source.Read(buffer);
        Assert.Equal(3f, buffer[1].Value);
        Assert.False(source.TryRefreshCatalog());
    }

    [Fact]
    public void Snapshot_wobble_stays_close_and_keeps_stopped_fans_stopped()
    {
        for (long t = 0; t < 40_000; t += 700)
        {
            var temp = ReplaySource.Wobble(SensorKind.Temperature, 60, 3, t);
            Assert.InRange(temp, 58.1f, 61.9f);
            Assert.InRange(ReplaySource.Wobble(SensorKind.Load, 99, 1, t), 0f, 100f);
        }

        Assert.Equal(0f, ReplaySource.Wobble(SensorKind.Fan, 0, 2, 5000));
        Assert.Equal(512f, ReplaySource.Wobble(SensorKind.Data, 512, 2, 5000));
    }

    [Fact]
    public void Missing_replay_file_reports_an_error_instead_of_throwing()
    {
        using var source = new ReplaySource(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".sdrec"));
        source.Open();
        Assert.Equal(SourceStatus.Error, source.Status);
        Assert.NotNull(source.StatusMessage);
        var buffer = new SensorReading[0];
        source.Update();
        source.Read(buffer);
    }

    [Fact]
    public void Diff_finds_removed_renamed_and_rescaled_sensors()
    {
        HardwareInfo[] hw = [new("lhm:/lpc/x/0", "Super I/O", HardwareKind.SuperIo, null, "lhm")];
        SensorInfo V(string id, string name, SensorKind kind = SensorKind.Voltage, double? nominal = null) =>
            new(id, "lhm:/lpc/x/0", name, kind, "V", "lhm", nominal);

        var before = new SensorRecording(
            [new SensorCatalog(1, hw, [V("v/0", "Vcore"), V("v/1", "Voltage #2", nominal: 12), V("v/2", "+5V", nominal: 5), V("t/0", "System", SensorKind.Temperature)])],
            [new RecordingFrame(0, 0, [1.2f, 12.1f, 5.0f, 35f])]);
        var after = new SensorRecording(
            [new SensorCatalog(1, hw, [V("v/0", "Vcore"), V("v/1", "+12V", nominal: 12), V("v/2", "+5V", nominal: 5), V("f/0", "CPU Fan", SensorKind.Fan)])],
            [new RecordingFrame(0, 0, [1.21f, 12.1f, 4.2f, 800f])]);

        var diff = HardwareDiff.Compare(before, after);
        Assert.Equal("t/0", Assert.Single(diff.RemovedSensors).Id);
        Assert.Equal("f/0", Assert.Single(diff.AddedSensors).Id);
        Assert.Equal("+12V", Assert.Single(diff.Renamed).After.Name);
        Assert.Equal("v/2", Assert.Single(diff.ValueShifts).Sensor.Id); // 5.0 → 4.2 V: scaling changed
        Assert.True(diff.HasBreakingChanges);
        Assert.Contains("Sensors removed", diff.ToText(), StringComparison.Ordinal);

        Assert.True(HardwareDiff.Compare(before, before).IsEmpty);
    }

    private sealed class ManualTime : TimeProvider
    {
        public long Now { get; set; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Now;
    }

    /// <summary>Lets the test read the MemoryStream after the writer closes its gzip stream.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
