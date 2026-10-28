namespace TvAIr.Schedule;

/// <summary>Host-only observation source; the caller must hold shared edit admission.</summary>
public interface IScheduledChainRuntimeProtectionSource
{
    ScheduledChainRuntimeProtectionSnapshot CaptureScheduledChainJoinRuntimeProtection(DateTime observedAt);
}


public sealed record ScheduledChainRuntimeProtectionEntry(
    int ReservationId, int OwnerReservationId, string OwnerCommitState,
    string Reason, string Title, int? ControlRootReservationId,
    DateTime? OpenAt, bool OpenTimeReached);

public sealed record ScheduledChainRuntimeProtectionIssue(int OwnerReservationId, string Reason);

/// <summary>
/// Immutable observation of session/control evidence. ReadSucceeded means no read/consistency issues were reported;
/// it never means that a reservation can be edited or that the runtime stayed unchanged after capture.
/// </summary>
public sealed record ScheduledChainRuntimeProtectionSnapshot(
    DateTime ObservedAt,
    IReadOnlyList<int> ProtectedReservationIds,
    IReadOnlyList<ScheduledChainRuntimeProtectionEntry> Entries,
    IReadOnlyList<ScheduledChainRuntimeProtectionIssue> Issues)
{
    public bool ReadSucceeded => Issues.Count == 0;
}

internal static class ScheduledChainRuntimeProtectionReader
{
    internal static ScheduledChainRuntimeProtectionSnapshot Capture(
        IEnumerable<RecordingSession> sessions, IEnumerable<int> startingOwners, DateTime observedAt)
    {
        var protectedIds = new HashSet<int>();
        var entries = new List<ScheduledChainRuntimeProtectionEntry>();
        var issues = new List<ScheduledChainRuntimeProtectionIssue>();
        // A worker can be launched before its provisional session is registered. The existing
        // start owner marks that unresolved membership; a session-only scan is insufficient.
        foreach (var owner in startingOwners.Distinct())
        {
            if (owner > 0)
            {
                protectedIds.Add(owner);
                entries.Add(new(owner, owner, "", "start_owner", "", null, null, false));
            }
            issues.Add(new(owner, "start_owner_membership_pending"));
        }
        foreach (var session in sessions)
        {
            var owner = session.ReservationId;
            var lifecycle = session.RecordingCommitState;
            if (owner > 0)
            {
                protectedIds.Add(owner);
                entries.Add(new(owner, owner, lifecycle, "session_owner", "",
                    session.SharedCaptureRootReservationId, null, false));
            }
            else
                issues.Add(new(owner, "session_owner_invalid"));

            // A single-sink worker also has an upgrade-ready control. IsSharedChainCapture
            // alone would miss it. Retain provisional/cleanup owners until normal removal.
            var control = session.ChainControl;
            var expectsControl = control is not null
                || !string.IsNullOrWhiteSpace(session.ChainControlPath)
                || session.SharedCaptureRootReservationId.HasValue;
            if (!expectsControl) continue;
            if (control is null)
            {
                issues.Add(new(owner, "chain_control_unavailable"));
                continue;
            }

            try
            {
                var snapshot = control.ReadSnapshot();
                if (snapshot.ChainRootReservationId <= 0)
                    issues.Add(new(owner, "chain_control_root_invalid"));
                if (session.SharedCaptureRootReservationId is { } expectedRoot
                    && snapshot.ChainRootReservationId != expectedRoot)
                    issues.Add(new(owner, "chain_control_root_mismatch"));
                var segments = snapshot.Segments.ToArray();
                if (segments.Length == 0)
                    issues.Add(new(owner, "chain_control_empty"));
                var seen = new HashSet<int>();
                foreach (var segment in segments.Where(x => x.Enabled))
                {
                    if (segment.ReservationId <= 0)
                    {
                        issues.Add(new(owner, "chain_segment_id_invalid"));
                        continue;
                    }
                    if (!seen.Add(segment.ReservationId))
                        issues.Add(new(owner, "chain_segment_duplicate"));
                    if (segment.CloseAt <= segment.OpenAt)
                        issues.Add(new(owner, "chain_segment_range_invalid"));
                    protectedIds.Add(segment.ReservationId);
                    entries.Add(new(segment.ReservationId, owner, lifecycle, "control_segment",
                        segment.Title, snapshot.ChainRootReservationId, segment.OpenAt,
                        segment.OpenAt <= observedAt));
                }
            }
            catch (Exception)
            {
                // A partial known set is diagnostic evidence, never an empty/safe edit set.
                issues.Add(new(owner, "chain_control_unreadable"));
            }
        }

        return new(observedAt,
            Array.AsReadOnly(protectedIds.OrderBy(x => x).ToArray()),
            Array.AsReadOnly(entries.ToArray()), Array.AsReadOnly(issues.ToArray()));
    }
}
