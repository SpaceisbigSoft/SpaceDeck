using System.Security.Principal;

namespace SensorDeck.Sources.Lhm.SystemChecks;

public static class Elevation
{
    /// <summary>True when running as an administrator or LocalSystem (the collector service).</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem)
        {
            return true;
        }

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
