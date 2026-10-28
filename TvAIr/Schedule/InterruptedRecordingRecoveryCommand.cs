using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>One observed interrupted run; the existing allocation core owns its execution.</summary>
internal sealed class InterruptedRecordingRecoveryCommand : ReservationTopologyCommand
{
    private readonly Reservation source;
    private readonly ReservationScheduler.InterruptedRecordingRecoveryTrigger trigger;
    private ReservationScheduler? scheduler;
    internal InterruptedRecordingRecoveryCommand(Reservation source,
        ReservationScheduler.InterruptedRecordingRecoveryTrigger trigger)
    {
        this.source = source.SnapshotForChainRequest();
        this.trigger = trigger;
    }
    internal bool? Result { get; private set; }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool IsRejected => Result is false;
    internal override bool RetainIfUnexecuted => Result is null;
    internal override string? DeferredReason => Result is null ? "recovery_runtime_publication_busy" : null;
    internal override bool RequiresSchedulerContext => true;
    internal override void PrepareSchedulerContext(ReservationScheduler scheduler) => this.scheduler = scheduler;
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        if (Result.HasValue) return;
        Result = (scheduler ?? throw new InvalidOperationException("Recovery scheduler context is unavailable."))
            .ExecuteInterruptedRecordingRecoveryCore(store, source, trigger);
    }
}

// A verification has no queued worker or lifecycle authority. Its read handle can be
// transferred once; deferred commands lose eligibility when the producer disposes it.
internal sealed class ContinuousChainStopVerification(
    ContinuousChainStopEvidence evidence, RecordingTsVerifier.VerificationResult verification,
    FileStream outputRead, string runtimeDirectory, CancellationToken cancellation) : IDisposable
{
    private FileStream? heldRead = outputRead;
    internal ContinuousChainStopEvidence Evidence { get; } = evidence;
    internal RecordingTsVerifier.VerificationResult Verification { get; } = verification;
    internal string RuntimeDirectory { get; } = runtimeDirectory;
    internal CancellationToken Cancellation { get; } = cancellation;
    internal bool IsActive => Volatile.Read(ref heldRead) is not null;
    internal FileStream? TakeRead() => Interlocked.Exchange(ref heldRead, null);
    public void Dispose() => Interlocked.Exchange(ref heldRead, null)?.Dispose();
}

internal sealed class ContinuousChainStopRecoveryCommand(ContinuousChainStopVerification verified)
    : ReservationTopologyCommand
{
    private ReservationScheduler? scheduler;
    internal ReservationLifecycleTransitionResult? Result { get; private set; }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool RequiresSchedulerContext => true;
    internal override bool IsRejected => Result is { Applied: false };
    internal override bool RetainIfUnexecuted => Result is null && verified.IsActive && !verified.Cancellation.IsCancellationRequested;
    internal override object? NonBlockingDeferralKey => verified;
    internal override string? DeferredReason => Result is null && verified.IsActive && !verified.Cancellation.IsCancellationRequested ? "chain_stop_runtime_busy" : null;
    internal override void PrepareSchedulerContext(ReservationScheduler value) => scheduler = value;
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        if (Result is not null) return;
        (scheduler ?? throw new InvalidOperationException("Stop recovery scheduler is unavailable."))
            .ExecuteContinuousChainStopRecoveryCore(store, verified, committed => Result = committed);
    }
}
