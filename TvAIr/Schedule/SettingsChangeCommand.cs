using TvAIr.Core;

namespace TvAIr.Schedule;

// Settings publication shares the existing allocation owner. Failed or unexecuted UI
// requests are never replayed after the caller has received an unsaved response.
internal sealed class SettingsChangeCommand(Func<SettingsChangeResult> apply) : ReservationTopologyCommand
{
    private Func<SettingsChangeResult>? pendingApply = apply;
    private int osProjectionsRequested;
    private int networkProjectionRequested;
    internal void RequestOsProjections(bool refreshNetwork)
    {
        if (refreshNetwork) Volatile.Write(ref networkProjectionRequested, 1);
        Volatile.Write(ref osProjectionsRequested, 1);
    }
    internal bool TryTakeOsProjections(out bool refreshNetwork)
    {
        var requested = Interlocked.Exchange(ref osProjectionsRequested, 0) != 0;
        refreshNetwork = Volatile.Read(ref networkProjectionRequested) != 0;
        return requested;
    }
    private bool completed;
    private bool followupCompleted;
    internal ReservationAllocationRouteRequest? Followup { get; private set; }
    internal void SetFollowup(ReservationAllocationRouteRequest request) => Followup = request;
    internal void CompleteFollowup() => followupCompleted = true;
    internal override bool RetainIfUnexecuted => completed && Followup is not null && !followupCompleted;
    internal override object? NonBlockingDeferralKey => completed ? this : null;
    private SettingsChangeResult? result;
    private Exception? error;
    internal SettingsChangeResult? Result => Volatile.Read(ref result);
    internal Exception? Error => Volatile.Read(ref error);
    internal override bool RequiresProjectionMetadata => false;
    internal override bool RequiresAllocationAfterCommit => false;
    internal override bool IsRejected => Error is not null && Followup is null;
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        if (completed) return;
        try { Volatile.Write(ref result, pendingApply!()); }
        catch (Exception ex) { Volatile.Write(ref error, ex); }
        finally { pendingApply = null; completed = true; }
    }
}
