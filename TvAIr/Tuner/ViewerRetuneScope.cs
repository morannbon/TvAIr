namespace TvAIr.Tuner;

/// <summary>
/// Evaluates whether a profile-owned managed Viewer process is the intended
/// receiver for a retune after a lease has been resolved. This is read-only;
/// callers retain logging, window-state, retune, restart, and rollback duties.
/// </summary>
public static class ViewerRetuneScope
{
    public static ViewerRetuneScopeDecision Evaluate(
        ExternalTunerLeaseDto? existingManagedLease,
        ExternalTunerLeaseDto requestedLease)
    {
        ArgumentNullException.ThrowIfNull(requestedLease);

        var processId = existingManagedLease?.ProcessId.GetValueOrDefault() ?? 0;
        var preferred = existingManagedLease is not null && processId > 0;
        var existingGroup = NormalizeAllocationGroup(existingManagedLease?.Group);
        var requestedGroup = NormalizeAllocationGroup(requestedLease.Group);
        var sameGroup = preferred && string.Equals(existingGroup, requestedGroup, StringComparison.OrdinalIgnoreCase);
        var sameDid = preferred && string.Equals(
            (existingManagedLease?.Did ?? string.Empty).Trim(),
            (requestedLease.Did ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
        var sameBonDriver = preferred && string.Equals(
            Path.GetFileName(existingManagedLease?.BonDriverFileName ?? string.Empty),
            Path.GetFileName(requestedLease.BonDriverFileName ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);

        // The existing Viewer process is addressed by the exact profile-owned PID.
        // DID/BonDriver/group equality remains diagnostic and does not by itself
        // deny an in-process retune.
        var allowed = preferred;
        var reason = preferred
            ? "profile_owned_pid_exact"
            : "no_alive_tvair_managed_viewer";

        return new ViewerRetuneScopeDecision(
            Preferred: preferred,
            Allowed: allowed,
            GuardReason: reason,
            ExistingProcessId: processId,
            ExistingGroup: existingGroup,
            RequestedGroup: requestedGroup,
            SameGroup: sameGroup,
            SameDid: sameDid,
            SameBonDriver: sameBonDriver,
            ScopeStable: preferred && sameDid && sameBonDriver,
            PidScopedRetuneAvailable: preferred);
    }

    private static string NormalizeAllocationGroup(string? group)
    {
        var normalized = (group ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "BS" or "CS" or "BS/CS" or "BSCS" => "BSCS",
            "地上波" or "GR" or "GROUND" => "GR",
            _ => string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized
        };
    }
}

public sealed record ViewerRetuneScopeDecision(
    bool Preferred,
    bool Allowed,
    string GuardReason,
    int ExistingProcessId,
    string ExistingGroup,
    string RequestedGroup,
    bool SameGroup,
    bool SameDid,
    bool SameBonDriver,
    bool ScopeStable,
    bool PidScopedRetuneAvailable);
