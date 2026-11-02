using System.ComponentModel;
using System.Diagnostics;

namespace SpaceDeck.Collector.Polling;

/// <summary>Stops the collector when the process that started it exits (a replay collector started by the app).</summary>
public sealed class ParentProcessWatcher(int processId, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            lifetime.StopApplication(); // already gone
            return;
        }

        using (parent)
        {
            // A process started after us can't be the one that started us: the parent is gone and its id was reused.
            if (StartedAfterUs(parent))
            {
                lifetime.StopApplication();
                return;
            }

            try
            {
                await parent.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // the collector is stopping anyway
            }
        }

        lifetime.StopApplication();
    }

    private static bool StartedAfterUs(Process parent)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return parent.StartTime > self.StartTime;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false; // can't tell (e.g. access denied): keep watching it
        }
    }
}
