namespace TvAIr.Schedule;

internal sealed record ExternalProjectionCommitResult(ProgramProjectionReservationSyncResult Summary,bool RetryRequired,bool TopologyChanged=false);

internal sealed class ExternalProjectionCommand(ProgramProjectionReservationSyncService owner,string source,bool match,bool promote) : ReservationTopologyCommand
{
    private ReservationScheduler? scheduler;
    private volatile bool promotionComplete=!promote;
    private volatile bool complete;
    private ExternalProjectionCommitResult? result;
    private readonly HashSet<int> promotedIds=new();
    internal int Promoted=>promotedIds.Count;
    internal ExternalProjectionCommitResult? Result=>Volatile.Read(ref result);
    internal override bool RequiresProjectionMetadata=>false;
    internal override bool RequiresSchedulerContext=>true;
    internal override bool IsRejected=>false;
    internal override bool RetainIfUnexecuted=>!complete;
    internal override bool BlocksRuleRefreshWhenDeferred=>!complete;
    internal override object? NonBlockingDeferralKey=>owner;
    internal override string? DeferredReason=>!complete ? "external_projection_pending" : null;
    internal override bool RequiresRuleRefreshAfterCommit=>complete && match;
    internal override bool RequiresProgramRuleRefreshAfterCommit=>false;
    internal override void PrepareSchedulerContext(ReservationScheduler value)=>scheduler=value;
    internal override void Apply(ReservationStore store,bool enabled,ReservationProjectionMetadataStore? metadata=null)
    {
        if(complete)return;
        var runtime=scheduler??throw new InvalidOperationException("External projection scheduler unavailable.");
        if(!promotionComplete)
            runtime.ExecuteProjectionPromotionCore(store,owner.Promotion,value=>
            {foreach(var id in value.PromotedIds)promotedIds.Add(id);promotionComplete=!value.RetryRequired;});
        if(!promotionComplete)return;
        runtime.ExecuteExternalProjectionCore(store,owner,source,value=>
        {Volatile.Write(ref result,value);complete=!value.RetryRequired;});
    }
}
