using TvAIr.Core;
using TvAIr.Epg;

namespace TvAIr.Schedule;

// Capture-owned input is frozen before entering the existing allocation queue.
// Callers must release the capture lane before submitting this command.
internal sealed class EpgCaptureCommitCommand : ReservationTopologyCommand
{
    private readonly EpgStore epg;
    private readonly EpgEvent[] upsert;
    private readonly EpgEvent[]? retire;
    private readonly EpgEvent[]? preserve;
    private readonly EpgEvent[] otherSupplementCandidates;
    private readonly SemaphoreSlim captureLane;
    private readonly CancellationToken cancellation;
    private ReservationScheduler? scheduler;
    private readonly ReservationProjectionPromotionService? promotion;
    private volatile bool promotionCompleted = true;
    private volatile bool promotionCompletedThisApply;
    private readonly EpgCaptureImportContext? importContext;
    private IReadOnlyList<EpgEvent>? keywordEvents;
    private bool commitReported;
    private ProjectionPromotionResult? promotionResult;
    internal ProjectionPromotionResult? PromotionResult { get => Volatile.Read(ref promotionResult); private set => Volatile.Write(ref promotionResult, value); }
    internal ReservationProjectionPromotionService? CompletedPromotionService =>
        promotionCompletedThisApply && IsComplete && PromotionResult is not null ? promotion : null;
    internal ReservationProjectionPromotionService? PendingPromotionRetry =>
        IsComplete && PromotionResult is { RetryRequired: true } ? promotion : null;
    internal bool IsComplete => Result is { Applied: false } || Result is { Applied: true } && promotionCompleted;
    private readonly TaskCompletionSource<EpgReservationCommitResult> committedReceipt =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EpgReservationCommitResult? result;
    internal EpgReservationCommitResult? Result
    {
        get => Volatile.Read(ref result);
        private set
        {
            Volatile.Write(ref result, value);
            if (value is not null) committedReceipt.TrySetResult(value);
        }
    }
    // The capture task awaits its own canonical transaction receipt, never the
    // allocation flight or a caller-owned gate. Cancelling a waiter cannot erase
    // a committed result or cancel another consumer of the same evidence.
    internal Task<EpgReservationCommitResult> WaitForCommitAsync(CancellationToken cancellation)
        => Result is { } receipt ? Task.FromResult(receipt) : committedReceipt.Task.WaitAsync(cancellation);
    internal override bool RequiresProjectionMetadata => false;
    internal override bool RequiresSchedulerContext => true;
    internal override bool IsRejected => Result is { Applied: false };
    internal override object? NonBlockingDeferralKey => captureLane;
    internal override bool BlocksRuleRefreshWhenDeferred => Result is { Applied: true } && !promotionCompleted;
    internal override bool RetainIfUnexecuted => Result is null && !cancellation.IsCancellationRequested
        || Result is { Applied: true } && !promotionCompleted;
    internal override string? DeferredReason => Result is null ? "epg_capture_commit_pending"
        : Result is { Applied: true } && !promotionCompleted ? "epg_projection_promotion_pending" : null;
    internal override bool RequiresRuleRefreshAfterCommit => Result is { Applied: true } && promotionCompleted && importContext is null;
    internal override bool RequiresProgramRuleRefreshAfterCommit => RequiresRuleRefreshAfterCommit;
    internal override IReadOnlyList<EpgEvent>? CommittedKeywordEvents => Result is { Applied: true } && promotionCompleted
        && importContext is { MatchKeywords: true }
        ? keywordEvents ??= Array.AsReadOnly(upsert.Concat(Result.Capture?.OtherSupplement?.InsertedEvents ?? Array.Empty<EpgEvent>())
            .GroupBy(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId)).Select(g => g.First()).ToArray()) : null;

    internal EpgCaptureCommitCommand(EpgStore epg, IEnumerable<EpgEvent> upsert,
        IEnumerable<EpgEvent>? retire, IEnumerable<EpgEvent>? preserve,
        SemaphoreSlim captureLane, CancellationToken cancellation)
        : this(epg, upsert, retire, preserve, captureLane, cancellation, Array.Empty<EpgEvent>()) { }

    internal EpgCaptureCommitCommand(EpgStore epg, IEnumerable<EpgEvent> upsert,
        IEnumerable<EpgEvent>? retire, IEnumerable<EpgEvent>? preserve,
        SemaphoreSlim captureLane, CancellationToken cancellation, IEnumerable<EpgEvent> otherSupplementCandidates)
    {
        this.epg = epg;
        this.upsert = Freeze(upsert);
        this.retire = retire is null ? null : Freeze(retire);
        this.preserve = preserve is null ? null : Freeze(preserve);
        this.otherSupplementCandidates = Freeze(otherSupplementCandidates);
        this.captureLane = captureLane;
        this.cancellation = cancellation;
    }

    internal EpgCaptureCommitCommand(EpgStore epg, IEnumerable<EpgEvent> upsert,
        IEnumerable<EpgEvent>? retire, IEnumerable<EpgEvent>? preserve,
        SemaphoreSlim captureLane, CancellationToken cancellation, IEnumerable<EpgEvent> otherSupplementCandidates,
        ReservationProjectionPromotionService promotion)
        : this(epg, upsert, retire, preserve, captureLane, cancellation, otherSupplementCandidates)
    {
        this.promotion = promotion;
        promotionCompleted = false;
    }

    internal EpgCaptureCommitCommand(EpgStore epg, IEnumerable<EpgEvent> upsert,
        IEnumerable<EpgEvent>? retire, IEnumerable<EpgEvent>? preserve,
        SemaphoreSlim captureLane, CancellationToken cancellation, IEnumerable<EpgEvent> otherSupplementCandidates,
        ReservationProjectionPromotionService promotion, EpgCaptureImportContext importContext)
        : this(epg, upsert, retire, preserve, captureLane, cancellation, otherSupplementCandidates, promotion)
        => this.importContext = importContext;

    internal override void PrepareSchedulerContext(ReservationScheduler value) => scheduler = value;
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        // A receipt from a prior Apply must not acknowledge newly queued semantic work.
        promotionCompletedThisApply = false;
        var runtime = scheduler ?? throw new InvalidOperationException("EPG capture scheduler is unavailable.");
        try
        {
            if (Result is null)
                runtime.ExecuteEpgCaptureCommitCore(store, epg, upsert, retire, preserve, captureLane, cancellation,
                    result => Result = result, otherSupplementCandidates);
        }
        finally
        {
            if (!commitReported && Result is { Applied: true } && importContext is not null)
            {
                commitReported = true;
                runtime.RecordEpgImportCommit(importContext.Source, Result);
            }
        }
        // Once EPG is committed, cancellation must not erase its remaining convergence
        // work. A retry enters this phase directly and never writes captured EPG again.
        if (Result is { Applied: true } && !promotionCompleted)
            runtime.ExecuteProjectionPromotionCore(store, promotion!, result =>
            {
                PromotionResult = result;
                promotionCompletedThisApply = true;
                promotionCompleted = true;
            });
    }

    private static EpgEvent[] Freeze(IEnumerable<EpgEvent> events) => events.Select(source => new EpgEvent
    {
        NetworkId = source.NetworkId, TransportStreamId = source.TransportStreamId,
        ServiceId = source.ServiceId, EventId = source.EventId,
        ServiceName = source.ServiceName, Title = source.Title, Description = source.Description,
        Genre = source.Genre, GenreCodes = source.GenreCodes,
        TableId = source.TableId, SectionNumber = source.SectionNumber, VersionNumber = source.VersionNumber,
        RawDescriptorLoopHex = source.RawDescriptorLoopHex,
        RawShortEventDescriptorHex = source.RawShortEventDescriptorHex,
        RawExtendedEventDescriptorHex = source.RawExtendedEventDescriptorHex,
        RawContentDescriptorHex = source.RawContentDescriptorHex,
        DurationSeconds = source.DurationSeconds, Start = source.Start, End = source.End, UpdatedAt = source.UpdatedAt
    }).ToArray();
}
