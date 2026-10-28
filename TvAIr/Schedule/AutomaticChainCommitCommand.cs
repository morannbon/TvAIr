namespace TvAIr.Schedule;

// Internal intent; execution and reconciliation belong to the existing common core.
internal sealed class AutomaticChainCommitCommand(int addedId) : ReservationTopologyCommand
{
    private ReservationScheduler? scheduler;
    internal UserChainSequenceMutationResult? Result { get; private set; }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool RequiresSchedulerContext => true;
    internal override bool RequiresRuntimeReconciliation => true;
    internal override bool IsRejected => Result is { Applied: false };
    internal override bool RetainIfUnexecuted => Result is null;
    internal override string? DeferredReason => Result is null ? "automatic_chain_admission_pending" : null;
    internal override void PrepareSchedulerContext(ReservationScheduler value) => scheduler=value;
    internal override void Apply(ReservationStore store,bool enabled,ReservationProjectionMetadataStore? metadataStore=null)
    {
        if(Result is not null) return;
        (scheduler??throw new InvalidOperationException("Automatic chain scheduler is unavailable."))
            .ExecuteAutomaticChainCore(store,addedId,enabled,result=>Result=result);
    }
    internal override void ReconcileRuntime(ReservationScheduler value)
    {
        if(Result is { Applied:true }) value.ReconcileActiveContinuousChainTopology("AutomaticChain");
    }
}
