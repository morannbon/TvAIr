using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>Closed Host command family; only the existing allocation core executes these intents.</summary>
public abstract class ReservationTopologyCommand
{
    private protected ReservationTopologyCommand() { }
    internal abstract bool RequiresProjectionMetadata { get; }
    internal abstract bool IsRejected { get; }
    internal virtual bool RequiresRuleRefreshAfterCommit => false;
    internal virtual IReadOnlyList<EpgEvent>? CommittedKeywordEvents => null;
    internal bool RequiresKeywordRefreshAfterCommit => RequiresRuleRefreshAfterCommit || CommittedKeywordEvents is { Count: > 0 };
    internal virtual bool RequiresProgramRuleRefreshAfterCommit => RequiresRuleRefreshAfterCommit;
    // Automatic maintenance may park only within the same canonical queue.
    internal virtual object? NonBlockingDeferralKey => null;
    internal virtual bool BlocksRuleRefreshWhenDeferred => false;
    internal virtual bool RetainIfUnexecuted => false;
    internal virtual string? DeferredReason => null;
    internal virtual bool RequiresAllocationAfterCommit => true;
    internal virtual bool RequiresRuntimeReconciliation => false;
    internal virtual bool RequiresSchedulerContext => RequiresRuntimeReconciliation;
    internal virtual void PrepareSchedulerContext(ReservationScheduler scheduler) { }
    internal virtual void ReconcileRuntime(ReservationScheduler scheduler) { }
    internal abstract void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null);
}
