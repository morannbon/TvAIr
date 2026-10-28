namespace TvAIr.Core;

// Immutable read projection of committed INI values needed by OS integrations.
// The INI remains authoritative; no reservation state or work queue is stored here.
internal sealed record SettingsOsPolicySnapshot(bool StartupEnabled, bool NetworkUsageEnabled,
    bool LanAccessEnabled, bool HasNetworkPassword, int RuntimePort);
internal sealed record SettingsOsProjectionResult(bool StartupApplied, bool NetworkApplied);

// Synchronous caller-owned OS work, after releasing the common allocation owner.
// Delegates read current committed policy under this gate, never a saved request DTO.
internal sealed class SettingsOsProjectionBoundary(Func<bool> applyStartup, Action applyNetwork, LogRepository log)
{
    private readonly object networkGate = new();
    private readonly object startupGate = new();
    private bool networkPending;
    internal SettingsOsProjectionResult Apply(bool refreshNetwork)
    {
        bool networkApplied;
        lock (networkGate)
        {
            networkPending |= refreshNetwork;
            if (networkPending)
            {
                try { applyNetwork(); networkPending = false; }
                catch (Exception ex)
                {
                    log.Add("SETTINGS_POST_COMMIT_WARNING", "NetworkBinding",
                        $"result=FAILED stage=network_endpoint_projection persisted=True exception={ex.GetType().Name} reason={ex.Message} rule=settings_post_commit_side_effect_contract");
                }
            }
            networkApplied = !networkPending;
        }
        // Closing external endpoints must not wait behind an unrelated COM startup call.
        var startupApplied = false;
        lock (startupGate)
        {
            try { startupApplied = applyStartup(); }
            catch (Exception ex)
            {
                log.Add("SETTINGS_POST_COMMIT_WARNING", "Startup",
                    $"result=FAILED stage=startup_projection persisted=True exception={ex.GetType().Name} reason={ex.Message} rule=settings_post_commit_side_effect_contract");
            }
        }
        return new(startupApplied, networkApplied);
    }
}
