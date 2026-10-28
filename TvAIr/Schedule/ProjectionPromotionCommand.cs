namespace TvAIr.Schedule;

// The existing allocation queue owns promotion; no separate startup writer exists.
internal sealed class ProjectionPromotionCommand(ReservationProjectionPromotionService promotion,bool runAllocationRoute) : ReservationTopologyCommand
{
    private ReservationScheduler? scheduler;
    private ProjectionPromotionResult? result;
    private volatile bool completed;
    internal ProjectionPromotionResult? Result => Volatile.Read(ref result);
    internal override bool RequiresAllocationAfterCommit => runAllocationRoute;
    internal override bool RequiresProjectionMetadata => false;
    internal override bool RequiresSchedulerContext => true;
    internal override bool IsRejected => false;
    internal override bool RetainIfUnexecuted => !completed;
    internal override bool BlocksRuleRefreshWhenDeferred => !completed;
    internal override object? NonBlockingDeferralKey => promotion;
    internal override string? DeferredReason => !completed ? "projection_promotion_admission_pending" : null;
    internal override void PrepareSchedulerContext(ReservationScheduler value) => scheduler=value;
    internal override void Apply(ReservationStore store,bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore=null)
    {
        if(completed)return;
        (scheduler??throw new InvalidOperationException("Projection promotion scheduler is unavailable."))
            .ExecuteProjectionPromotionCore(store,promotion,value=>
            {
                var ids=(Result?.PromotedIds??Array.Empty<int>()).Concat(value.PromotedIds).Distinct().OrderBy(id=>id).ToArray();
                Volatile.Write(ref result,value with { PromotedIds=Array.AsReadOnly(ids) });
                completed=!value.RetryRequired;
            });
    }
}
