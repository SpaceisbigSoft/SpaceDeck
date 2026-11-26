using System.Diagnostics;
using System.Globalization;
using System.Text;
using SpaceDeck.Sources.LianLi;
using SpaceDeck.Sources.LianLi.Hid;
using SpaceDeck.Sources.Lhm.SystemChecks;

namespace SpaceDeck.Cli;

/// <summary>
/// <c>spacedeck-cli lianli-listen</c>: an experiment with the Lian Li Galahad II cooler. By default read-only: opens
/// the cooler on SpaceDeck's own handle, sends nothing, and records the reports that arrive while another program
/// polls it. With <c>--query</c> it asks the cooler itself, once for its firmware and then for its status every
/// second, but only after 3 s of silence (no other program polling) and never while L-Connect runs; it sends nothing
/// but those two queries (<see cref="GalahadProtocol.IsQuery"/>). Answers: does this cooler's status reply carry the
/// coolant temperature, and does a second reader see another program's replies.
/// </summary>
internal static class LianLiListenCommand
{
    private static readonly TimeSpan QuietBeforeQuery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(1);

    public static int Run(int seconds, string? outPath, bool query)
    {
        using var output = new Output(outPath);
        var clock = Stopwatch.StartNew();

        output.Line(query
            ? "spacedeck-cli lianli-listen --query — SpaceDeck sends only the status and firmware queries, after 3 s of silence."
            : "spacedeck-cli lianli-listen — read-only: SpaceDeck sends nothing to the device.");
        output.Line($"Started  : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        output.Line($"Admin    : {(Elevation.IsElevated() ? "yes" : "no")}");
        output.Line($"Duration : {seconds} s");
        output.Line("");
        output.Line("Programs that may poll Lian Li devices:");
        var lConnectRunning = WriteProcesses(output);
        if (query && lConnectRunning)
        {
            output.Line("L-Connect is running: it drives the cooler, so SpaceDeck won't send queries. Listening only.");
            query = false;
        }

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

        var listeners = targets.Select(c => new Listener(c, output, clock, query && c.OutputReportLength >= GalahadProtocol.CommandPacketLength)).ToList();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        ConsoleCancelEventHandler onCtrlC = (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += onCtrlC;

        output.Line($"Listening on {listeners.Count} collection(s) for {seconds} s… (Ctrl+C stops early)");
        output.Line("");
        var threads = listeners.Select(l => new Thread(() => l.Run(stop.Token)) { IsBackground = true, Name = "lianli-listen" })
            .Concat(listeners.Where(l => l.Queries).Select(l => new Thread(() => l.Ask(stop.Token)) { IsBackground = true, Name = "lianli-query" }))
            .ToList();
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
        var asked = listeners.Sum(l => l.QueriesSent);
        if (replies > 0 && asked > 0)
        {
            output.Line($"RESULT: the cooler answered SpaceDeck's own status queries ({replies} replies to {asked} queries).");
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
        else if (replies > 0)
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
        else if (asked > 0)
        {
            output.Line($"RESULT: SpaceDeck sent {asked} queries and the cooler didn't answer on this channel. Send this file to Claude.");
        }
        else
        {
            output.Line("RESULT: no reports reached SpaceDeck's handle. Either nothing was polling the cooler (is FanControl running and");
            output.Line("        showing a changing pump RPM?), or Windows doesn't give a second handle a copy of the replies.");
            if (!query)
            {
                output.Line("        If nothing else talks to the cooler, run again with --query to ask it directly.");
            }
        }

        listeners.ForEach(l => l.Dispose());
        output.Line(query
            ? "Also note the coolant temperature on the pump's screen, to compare."
            : "Also check: did FanControl keep showing changing pump and fan RPM during the test?");
        if (outPath is not null)
        {
            Console.WriteLine($"Saved to {Path.GetFullPath(outPath)}");
        }

        return replies > 0 ? 0 : 3;
    }

    /// <summary>Lists programs that may drive Lian Li devices. Returns true if L-Connect is among them.</summary>
    private static bool WriteProcesses(Output output)
    {
        string[] names = ["FanControl", "FanControl.Service", "LConnectService", "LConnectServiceWatcher", "L-Connect 3", "SignalRgb", "OpenRGB"];
        var any = false;
        var lConnect = false;
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
                    lConnect |= name.StartsWith('L');
                }
            }
        }

        if (!any)
        {
            output.Line("  none found");
        }

        return lConnect;
    }

    /// <summary>Reads one collection and keeps its numbers.</summary>
    private sealed class Listener(HidCollection collection, Output output, Stopwatch clock, bool queries) : IDisposable
    {
        private readonly ManualResetEventSlim _open = new(false);
        private volatile HidChannel? _channel;
        private readonly Lock _lock = new();
        private readonly List<double> _coolant = [];
        private readonly SortedSet<int> _lengths = [];
        private TimeSpan? _first;
        private TimeSpan? _last;
        private string? _error;

        public int Reports { get; private set; }

        public int StatusReplies { get; private set; }

        /// <summary>True when this listener may send the status and firmware queries.</summary>
        public bool Queries => queries;

        public int QueriesSent { get; private set; }

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
                using var channel = queries ? HidChannel.OpenForQueries(collection) : HidChannel.OpenReadOnly(collection);
                _channel = channel;
                _open.Set();
                channel.Run(OnReport, token);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                output.Line($"[{Label}] stopped: {ex.Message}");
            }
            finally
            {
                _channel = null;
                _open.Set();
            }
        }

        /// <summary>
        /// Waits <see cref="QuietBeforeQuery"/>; if no other program's replies arrived in that time, asks the firmware
        /// once and then the status every <see cref="QueryInterval"/> until stopped.
        /// </summary>
        public void Ask(CancellationToken token)
        {
            if (!_open.Wait(TimeSpan.FromSeconds(5)) || _channel is not { } channel)
            {
                return;
            }

            if (token.WaitHandle.WaitOne(QuietBeforeQuery))
            {
                return;
            }

            lock (_lock)
            {
                if (Reports > 0)
                {
                    output.Line($"[{Label}] another program is already talking to the cooler: SpaceDeck won't send queries.");
                    return;
                }
            }

            output.Line($"[{Label}] 3 s of silence: asking the cooler (firmware once, then status every second).");
            try
            {
                if (!Send(channel, GalahadProtocol.FirmwareCommand))
                {
                    return;
                }

                while (!token.WaitHandle.WaitOne(QueryInterval))
                {
                    if (!Send(channel, GalahadProtocol.StatusCommand))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // The channel closed under us (end of the run) or the device went away.
                if (!token.IsCancellationRequested)
                {
                    output.Line($"[{Label}] query stopped: {ex.Message}");
                }
            }
        }

        private bool Send(HidChannel channel, byte command)
        {
            if (!channel.Send(GalahadProtocol.Query(command, collection.OutputReportLength), TimeSpan.FromSeconds(1)))
            {
                output.Line($"[{Label}] the cooler didn't take the query within 1 s; stopping queries.");
                return false;
            }

            lock (_lock)
            {
                QueriesSent++;
            }

            return true;
        }

        public void Dispose() => _open.Dispose();

        public void WriteSummary()
        {
            lock (_lock)
            {
                var span = _first is { } f && _last is { } l && Reports > 1 ? (l - f).TotalSeconds / (Reports - 1) : (double?)null;
                output.Line(
                    Inv($"  [{Label}] {Reports} reports, {StatusReplies} status replies") +
                    (span is { } s ? Inv($", one every {s:0.00} s") : "") +
                    (QueriesSent > 0 ? $", {QueriesSent} queries sent" : "") +
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
                else if (GalahadProtocol.TryDecodeFirmwareText(report) is { } firmware)
                {
                    decoded = $"   firmware text: \"{firmware}\"";
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
