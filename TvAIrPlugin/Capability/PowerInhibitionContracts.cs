namespace TvAIrPlugin;

/// <summary>Generic, plugin-directed system sleep inhibition.  The plugin owns the work boundary;
/// the Host owns the Windows power request and releases outstanding leases on plugin shutdown.</summary>
public interface ITvAirPowerInhibitionApi
{
    /// <summary>Acquire a Host-owned SystemRequired request until this lease is disposed.
    /// Throws if the permission is missing, the Host is stopping, or the OS request fails.
    /// Always acquire before starting work which requires the system to remain awake.
    /// Independent leases protect overlapping plugin and recording operations.</summary>
    IDisposable Acquire(string operation);
}
