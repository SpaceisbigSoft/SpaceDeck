using System.Diagnostics;
using System.Globalization;
using System.Text;
using SpaceDeck.Sources.LianLi;
using SpaceDeck.Sources.LianLi.Hid;
using SpaceDeck.Sources.Lhm.SystemChecks;

namespace SpaceDeck.Cli;

/// <summary>
/// <c>spacedeck-cli lianli-listen</c>: a read-only experiment. Opens the Lian Li Galahad II cooler on SpaceDeck's own
/// handle, sends nothing, and records the reports that arrive while FanControl's Lian Li plugin polls the cooler.
/// Answers two questions: does a second, passive reader see the replies, and does this cooler's status reply carry
/// the coolant temperature.
/// </summary>
internal static class LianLiListenCommand
{
    public static int Run(int seconds, string? outPath)
    {
        using var output = new Output(outPath);
        var clock = Stopwatch.StartNew();

        output.Line("spacedeck-cli lianli-listen — read-only: SpaceDeck sends nothing to the device.");
        output.Line($"Started  : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        output.Line($"Admin    : {(Elevation.IsElevated() ? "yes" : "no")}");
        output.Line($"Duration : {seconds} s");
        output.Line("");
        output.Line("Programs that may poll Lian Li devices:");
        WriteProcesses(output);
        output.Line("");

        IReadOnlyList<HidCollection> all;
        try
        {
            all = HidDevices.Find(GalahadProtocol.VendorId);
        }
        catch (Exception ex)
        {
            output.Line($"Couldn't list HID devices: {ex.Message}");
            return 2;
        }

        output.Line($"Lian Li HID collections (vendor 0x{GalahadProtocol.VendorId:X4}): {all.Count}");
        foreach (var c in all)
        {
            output.Line($"  PID 0x{c.ProductId:X4} v{c.VersionNumber:X4}  usage page 0x{c.UsagePage:X4} usage 0x{c.Usage:X4}  " +
                $"in {c.InputReportLength} / out {c.OutputReportLength} / feature {c.FeatureReportLength} bytes  " +
                $"\"{c.Product ?? "?"}\"{(GalahadProtocol.IsGalahad2(c.ProductId) ? "  <- Galahad II" : "")}");
        }

        output.Line("");

        var galahad = all.Where(c => GalahadProtocol.IsGalahad2(c.ProductId) && c.InputReportLength > 0).ToList();
        var targets = galahad.Where(c => c.IsVendorDefined).ToList();
        if (targets.Count == 0)
        {
            targets = galahad; // no vendor-defined collection: listen to whatever the cooler has
        }

        if (targets.Count == 0)
        {
            output.Line("No Galahad II cooler found (product ids 0x7391, 0x7395, 0x7371, 0x7373). Is it plugged in by USB?");
            return 1;
        }

        var listeners = targets.Select(c => new Listener(c, output, clock)).ToList();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        ConsoleCancelEventHandler onCtrlC = (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += onCtrlC;

        output.Line($"Listening on {listeners.Count} collection(s) for {seconds} s… (Ctrl+C stops early)");
        output.Line("");
        var threads = listeners.Select(l => new Thread(() => l.Run(stop.Token)) { IsBackground = true, Name = "lianli-listen" }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Console.CancelKeyPress -= onCtrlC;

        output.Line("");
        output.Line("Summary");
        foreach (var l in listeners)
        {
            l.WriteSummary();
        }

        output.Line("");
        var replies = listeners.Sum(l => l.StatusReplies);
        var coolant = listeners.SelectMany(l => l.CoolantReadings).ToList();
        var lengths = listeners.SelectMany(l => l.PayloadLengths).Distinct().Order().ToList();
        if (replies > 0)
        {
            output.Line($"RESULT: passive listening works — {replies} status replies arrived on SpaceDeck's own read-only handle.");
            if (coolant.Count > 0)
            {
                output.Line(string.Create(CultureInfo.InvariantCulture,
                    $"RESULT: the reply carries the coolant temperature: {coolant.Min():0.0}–{coolant.Max():0.0} °C. Compare with the pump's screen."));
            }
            else
            {
                output.Line($"RESULT: no valid coolant temperature in the replies (payload lengths seen: {string.Join(", ", lengths)}; 7 would carry it).");
            }
        }
        else if (listeners.Sum(l => l.Reports) > 0)
        {
            output.Line("RESULT: reports arrived, but none was a status reply (01 81 …). Send this file to Claude.");
        }
        else
        {
            output.Line("RESULT: no reports reached SpaceDeck's handle. Either nothing was polling the cooler (is FanControl running and");
            output.Line("        showing a changing pump RPM?), or Windows doesn't give a second handle a copy of the replies.");
        }

        output.Line("Also check: did FanControl keep showing changing pump and fan RPM during the test?");
        if (outPath is not null)
        {
            Console.WriteLine($"Saved to {Path.GetFullPath(outPath)}");
        }

        return replies > 0 ? 0 : 3;
    }

    private static void WriteProcesses(Output output)
    {
        string[] names = ["FanControl", "FanControl.Service", "LConnectService", "LConnectServiceWatcher", "L-Connect 3", "SignalRgb", "OpenRGB"];
        var any = false;
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    string session;
                    try
                    {
                        session = p.SessionId.ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception)
                    {
                        session = "?";
                    }

                    output.Line($"  {p.ProcessName} (pid {p.Id}, session {session})");
                    any = true;
                }
            }
        }

        if (!any)
        {
            output.Line("  none found — nothing will be polling the cooler, so expect no reports");
        }
    }

    /// <summary>Reads one collection and keeps its numbers.</summary>
    private sealed class Listener(HidCollection collection, Output output, Stopwatch clock)
    {
        private readonly Lock _lock = new();
        private readonly List<double> _coolant = [];
        private readonly SortedSet<int> _lengths = [];
        private TimeSpan? _first;
        private TimeSpan? _last;
        private string? _error;

        public int Reports { get; private set; }

        public int StatusReplies { get; private set; }

        public IReadOnlyList<double> CoolantReadings
        {
            get
            {
                lock (_lock)
                {
                    return [.. _coolant];
                }
            }
        }

        public IReadOnlyCollection<int> PayloadLengths
        {
            get
            {
                lock (_lock)
                {
                    return [.. _lengths];
                }
            }
        }

        private string Label => $"0x{collection.ProductId:X4}/0x{collection.UsagePage:X4}";

        public void Run(CancellationToken token)
        {
            try
            {
                using var reader = HidPassiveReader.Open(collection);
                reader.Run(OnReport, token);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                output.Line($"[{Label}] stopped: {ex.Message}");
            }
        }

        public void WriteSummary()
        {
            lock (_lock)
            {
                var span = _first is { } f && _last is { } l && Reports > 1 ? (l - f).TotalSeconds / (Reports - 1) : (double?)null;
                output.Line(
                    Inv($"  [{Label}] {Reports} reports, {StatusReplies} status replies") +
                    (span is { } s ? Inv($", one every {s:0.00} s") : "") +
                    (_lengths.Count > 0 ? $", payload lengths {string.Join("/", _lengths)}" : "") +
                    (_coolant.Count > 0 ? Inv($", coolant {_coolant.Min():0.0}–{_coolant.Max():0.0} °C") : "") +
                    (_error is not null ? $", error: {_error}" : ""));
            }
        }

        private void OnReport(byte[] report)
        {
            var at = clock.Elapsed;
            string decoded;
            lock (_lock)
            {
                Reports++;
                _first ??= at;
                _last = at;
                if (GalahadProtocol.TryDecodeStatus(report, out var status))
                {
                    StatusReplies++;
                    _lengths.Add(status.PayloadLength);
                    if (status.CoolantCelsius is { } c)
                    {
                        _coolant.Add(c);
                    }

                    decoded = Inv($"   status: fan {status.FanRpm} rpm, pump {status.PumpRpm} rpm, payload {status.PayloadLength}") +
                        (status.PayloadLength >= 7
                            ? $", coolant flag {(status.CoolantValid ? "valid" : "not valid")}" +
                              (status.CoolantCelsius is { } t ? Inv($", coolant {t:0.0} °C") : "")
                            : ", no coolant field");
                }
                else
                {
                    decoded = "";
                }
            }

            output.Line(string.Create(CultureInfo.InvariantCulture, $"{at.TotalSeconds,8:0.000}s [{Label}] {Hex(report)}{decoded}"));
        }
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>Hex bytes up to the last non-zero one (at least 16), then how many zero bytes follow.</summary>
    internal static string Hex(byte[] report)
    {
        var last = Array.FindLastIndex(report, b => b != 0);
        var shown = Math.Min(report.Length, Math.Max(16, last + 1));
        var text = string.Join(' ', report.Take(shown).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        return shown < report.Length ? $"{text} +{report.Length - shown}×00" : text;
    }

    /// <summary>Writes each line to the console and, if asked, to a file (UTF-8, flushed per line).</summary>
    private sealed class Output : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly StreamWriter? _file;

        public Output(string? path)
        {
            if (path is null)
            {
                return;
            }

            var full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
            }

            _file = new StreamWriter(full, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public void Line(string text)
        {
            lock (_lock)
            {
                Console.WriteLine(text);
                _file?.WriteLine(text);
            }
        }

        public void Dispose() => _file?.Dispose();
    }
}
