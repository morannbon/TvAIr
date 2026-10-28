namespace TvAIr.Schedule;

/// <summary>One startup retention cutoff; only the existing allocation core executes this intent.</summary>
internal sealed class ExpiredTerminalLogDeletionCommand(DateTime cutoff) : ReservationTopologyCommand
{
    private ReservationScheduler? scheduler;
    internal TerminalLogDeletionResult? Result { get; private set; }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool RequiresSchedulerContext => true;
    internal override bool IsRejected => Result is { Applied: false };
    internal override bool RetainIfUnexecuted => Result is null;
    internal override string? DeferredReason => Result is null ? "history_retention_runtime_unavailable" : null;
    internal override void PrepareSchedulerContext(ReservationScheduler value) => scheduler = value;
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        if (Result is not null) return;
        (scheduler ?? throw new InvalidOperationException("Retention scheduler context is unavailable."))
            .ExecuteExpiredHistoryRetentionCore(store, cutoff, committed => Result = committed);
    }
}
