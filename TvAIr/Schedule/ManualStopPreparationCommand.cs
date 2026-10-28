using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>Stop topology intent, executed only inside the existing allocation core.</summary>
public sealed class ManualStopPreparationCommand : ReservationTopologyCommand
{
    private readonly int _predecessorId;
    public ManualStopPreparationCommand(int predecessorId)
    {
        if (predecessorId <= 0) throw new ArgumentOutOfRangeException(nameof(predecessorId));
        _predecessorId = predecessorId;
    }
    private bool _includeOccurrencePolicy;
    private bool _occurrencePolicyPrepared;
    internal static ManualStopPreparationCommand ForManualStopRequest(int id)
        => new(id) { _includeOccurrencePolicy = true };
    internal IReadOnlyList<RecordingSession> SiblingStops { get; set; } = Array.Empty<RecordingSession>();
    internal Reservation? PolicySnapshot { get; set; }
    internal int PostEndMarginSeconds { get; set; }
    internal int SiblingScheduledCancelled => Result?.CancelledSiblings.Count ?? 0;
    internal void PrepareOccurrencePolicy(ReservationScheduler scheduler)
    {
        if (!_includeOccurrencePolicy || _occurrencePolicyPrepared || Result is not null) return;
        scheduler.PrepareManualStopOccurrencePolicy(this);
        _occurrencePolicyPrepared = true;
    }
    public int ReservationId => _predecessorId;
    public ManualStopChainDetachResult? Result { get; private set; }
    private bool _runtimeReconcileRequested;
    internal override bool RequiresProjectionMetadata => false;
    internal override bool IsRejected => Result is { Applied: false };
    internal override bool RequiresRuntimeReconciliation => true;
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        if (Result is not null) return;
        if (_includeOccurrencePolicy)
            store.TryPrepareManualStopAtomicCas(_predecessorId, PolicySnapshot!, PostEndMarginSeconds,
                out _, committed => Result = committed);
        else
            store.TryDetachUserChainSuccessorsForManualStopAtomicCas(_predecessorId, out _,
                committed => Result = committed);
    }
    internal override void ReconcileRuntime(ReservationScheduler scheduler)
    {
        if (Result is not { Applied: true } result
            || (result.DetachedSuccessors.Count == 0 && result.CancelledSiblings.Count == 0)
            || _runtimeReconcileRequested) return;
        scheduler.ReconcileActiveContinuousChainTopology($"ManualStopDetach:R{_predecessorId}");
        _runtimeReconcileRequested = true;
    }
}
