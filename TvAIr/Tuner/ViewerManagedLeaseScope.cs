using System.Diagnostics;
using TvAIr.Core;

namespace TvAIr.Tuner;

/// <summary>
/// Resolves the profile-owned managed Viewer lease scope used by all
/// Viewer actions and the generic Viewer operation API. This method is read-only;
/// callers remain responsible for logging, stale lease release, and ownership mutation.
/// </summary>
public static class ViewerManagedLeaseScope
{
    public static ViewerManagedLeaseScopeSnapshot Resolve(
        IEnumerable<ExternalTunerLeaseDto> activeLeases,
        ViewerProfileContractDto profile,
        string clientId,
        string? requiredLeaseId = null)
    {
        ArgumentNullException.ThrowIfNull(activeLeases);
        ArgumentNullException.ThrowIfNull(profile);

        var scoped = activeLeases
            .Where(lease => string.Equals(lease.ClientId ?? string.Empty, clientId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .Where(lease => string.IsNullOrWhiteSpace(requiredLeaseId) || string.Equals(lease.LeaseId, requiredLeaseId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(lease => lease.AcquiredAt)
            .ToList();

        ExternalTunerLeaseDto? aliveManaged = null;
        var staleDead = new List<ExternalTunerLeaseDto>();
        var staleOwnership = new List<ExternalTunerLeaseDto>();

        foreach (var lease in scoped)
        {
            var processId = lease.ProcessId.GetValueOrDefault();
            if (processId <= 0)
                continue;

            if (!IsProcessAlive(processId))
            {
                staleDead.Add(lease);
                continue;
            }

            if (!lease.PoolLeaseCurrent)
            {
                staleOwnership.Add(lease);
                continue;
            }

            if (aliveManaged is null &&
                ViewerProfileContract.LeaseMatchesProfile(lease, profile) &&
                TvAirManagedProcessRegistry.TryGet(processId, out var managed) &&
                managed.IsViewer &&
                string.Equals(managed.OwnershipId, lease.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                var observed = TvAirManagedProcessRegistry.CaptureIdentity(processId);
                if (observed.IsAvailable &&
                    TvAirManagedProcessRegistry.IdentityMatches(managed.Identity, observed))
                {
                    aliveManaged = lease;
                }
            }
        }

        return new ViewerManagedLeaseScopeSnapshot(scoped, aliveManaged, staleDead, staleOwnership);
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record ViewerManagedLeaseScopeSnapshot(
    IReadOnlyList<ExternalTunerLeaseDto> ScopedLeases,
    ExternalTunerLeaseDto? AliveManagedLease,
    IReadOnlyList<ExternalTunerLeaseDto> StaleDeadLeases,
    IReadOnlyList<ExternalTunerLeaseDto> StaleOwnershipLeases);
