using System.ServiceProcess;

namespace SensorDeck.Sources.Lhm.SystemChecks;

public enum PawnIoState
{
    NotInstalled,
    Stopped,
    Running,
    Unknown,
}

public sealed record PawnIoInfo(PawnIoState State, string? ServiceName, string? Detail);

/// <summary>
/// Finds the PawnIO kernel driver among installed driver services.
/// </summary>
/// <remarks>
/// Matches any driver service whose name contains "PawnIO", so we don't depend on the exact name
/// (to be confirmed on a real install and recorded in docs/HANDOFF.md).
/// </remarks>
public static class PawnIoDetector
{
    public const string DownloadUrl = "https://pawnio.eu";

    public static PawnIoInfo Detect()
    {
        try
        {
            var drivers = ServiceController.GetDevices();
            try
            {
                var pawn = drivers.FirstOrDefault(d =>
                    d.ServiceName.Contains("PawnIO", StringComparison.OrdinalIgnoreCase) ||
                    d.DisplayName.Contains("PawnIO", StringComparison.OrdinalIgnoreCase));

                if (pawn is null)
                {
                    return new PawnIoInfo(PawnIoState.NotInstalled, null, $"PawnIO driver not found. Install it from {DownloadUrl}.");
                }

                // Driver services often show Stopped until a client first opens the device; LHM can start it on demand.
                return pawn.Status == ServiceControllerStatus.Running
                    ? new PawnIoInfo(PawnIoState.Running, pawn.ServiceName, null)
                    : new PawnIoInfo(PawnIoState.Stopped, pawn.ServiceName, $"PawnIO driver is installed but {pawn.Status}.");
            }
            finally
            {
                foreach (var d in drivers)
                {
                    d.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            return new PawnIoInfo(PawnIoState.Unknown, null, $"Couldn't query driver services: {ex.Message}");
        }
    }
}
