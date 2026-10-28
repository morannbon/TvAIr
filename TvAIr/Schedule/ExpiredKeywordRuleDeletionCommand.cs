namespace TvAIr.Schedule;

internal sealed class ExpiredKeywordRuleDeletionCommand(DateOnly today) : ReservationTopologyCommand
{
    private ReservationScheduler? scheduler;
    internal KeywordRuleMutationResult? Result { get; private set; }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool RequiresSchedulerContext => true;
    internal override bool IsRejected => Result is { Applied: false };
    internal override bool RetainIfUnexecuted => Result is null;
    internal override string? DeferredReason => Result is null ? "keyword_expiry_runtime_unavailable" : null;
    internal override bool RequiresRuleRefreshAfterCommit => Result is { Applied: true };
    internal override void PrepareSchedulerContext(ReservationScheduler value) => scheduler = value;
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        if (Result is not null) return;
        (scheduler ?? throw new InvalidOperationException("Rule expiry scheduler is unavailable."))
            .ExecuteExpiredKeywordRuleDeletionCore(store, today, result => Result = result);
    }
}
