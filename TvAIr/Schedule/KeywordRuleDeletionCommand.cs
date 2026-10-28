namespace TvAIr.Schedule;

internal sealed record KeywordRuleMutationResult(bool Applied, string Reason,
    int BeforeCount, int AfterCount, IReadOnlyList<int> RemovedIds,
    int Preserved = 0, int PreservedSourceMissing = 0, int RecordingOptionsUpdated = 0);

// An explicit UI intent: a rejected or unexecuted deletion is never replayed later.
internal sealed class KeywordRuleDeletionCommand(int ruleId) : ReservationTopologyCommand
{
    private ReservationScheduler? scheduler;
    internal KeywordRuleMutationResult? Result { get; private set; }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool RequiresSchedulerContext => true;
    internal override bool IsRejected => Result is { Applied: false };
    internal override bool RequiresRuleRefreshAfterCommit => Result is { Applied: true };
    internal override void PrepareSchedulerContext(ReservationScheduler value) => scheduler = value;
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        if (Result is not null) return;
        (scheduler ?? throw new InvalidOperationException("Rule deletion scheduler is unavailable."))
            .ExecuteKeywordRuleDeletionCore(store, ruleId, result => Result = result);
    }
}
