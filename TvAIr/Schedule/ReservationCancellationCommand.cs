using TvAIr.Core;

namespace TvAIr.Schedule;

public sealed record ReservationCancellationResult(bool Applied, string Reason, int RequestedId,
    IReadOnlyList<Reservation> Cancelled, bool IsChain, int PreRecordChildrenCancelled);

/// <summary>Cancel the selected programme in automatic mode; retain manual suffix semantics.</summary>
public sealed class ReservationCancellationCommand : ReservationTopologyCommand
{
    public WithdrawalProtectionLabPermit? LabPermit { get; init; }
    public Func<bool>? LabPreparationReady { get; init; }
    private int labAbortRequested;
    private readonly object labOrderingKey = new();
    public void RequestLabAbort() => Interlocked.Exchange(ref labAbortRequested, 1);
    internal override object? NonBlockingDeferralKey => LabPreparationReady is null ? null : labOrderingKey;
    internal override bool RetainIfUnexecuted => LabPreparationReady is not null && Result is null;
    private readonly object _gate = new();
    private readonly int _requestedId;
    private readonly Reservation[] _expected;
    private ReservationCancellationResult? _result;
    private bool _runtimeReconcileRequested;
    private bool _automaticSingle;
    private ReservationScheduler? _scheduler;
    public bool IsAutomaticSingle { get { lock (_gate) return _automaticSingle; } }
    internal void PrepareCancellationPolicy(bool automatic) { lock (_gate) { if (_result is null) _automaticSingle = automatic; } }
    internal override void PrepareSchedulerContext(ReservationScheduler scheduler) => _scheduler = scheduler;
    internal override string? DeferredReason => _automaticSingle && Result is null ? "automatic_cancel_admission_pending" : null;

    public ReservationCancellationCommand(int requestedId, IReadOnlyList<Reservation> expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        _requestedId = requestedId;
        _expected = expected.Select(r => r.SnapshotForChainRequest()).ToArray();
    }
    public ReservationCancellationResult? Result { get { lock (_gate) return _result; } }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool IsRejected => Result is { Applied: false };
    internal override bool RequiresRuntimeReconciliation => _automaticSingle || _expected.Length > 1
        || _expected.Any(r => r.IsUserChain || r.UserChainPreviousId.HasValue || r.UserChainRootId.HasValue);
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        lock (_gate)
        {
            if (_result is not null) return;
            if (LabPreparationReady is not null)
            {
                if (Volatile.Read(ref labAbortRequested) != 0 || !_automaticSingle)
                {
                    _result = new(false, "withdrawal_aborted_or_mode_changed", _requestedId, Array.Empty<Reservation>(), false, 0);
                    return;
                }
                if (!LabPreparationReady()) return;
            }
            if (_expected.Length == 0)
            {
                _result = new(false, "cancel_scope_empty", _requestedId, Array.Empty<Reservation>(), false, 0);
                return;
            }
            if (_automaticSingle)
            {
                var selected = _expected.SingleOrDefault(r => r.Id == _requestedId);
                if (selected is null) { _result = new(false, "cancel_selected_snapshot_missing", _requestedId, Array.Empty<Reservation>(), false, 0); return; }
                (_scheduler ?? throw new InvalidOperationException("Cancellation scheduler is unavailable."))
                    .ExecuteAutomaticReservationCancellationCore(store, selected, committed => _result = committed, LabPermit);
                return;
            }
            var applied = store.TryCancelReservationsAtomicCas(_expected,
                new Dictionary<string, string?> { ["suppressUserEvent"] = RequiresRuntimeReconciliation
                    ? "chain_cancel_specialized" : "reservation_cancel_specialized" },
                out _, _requestedId, committed => _result = committed);
            if (!applied) _result = new(false, "cancel_scope_or_state_changed", _requestedId,
                Array.Empty<Reservation>(), false, 0);
        }
    }
    internal override void ReconcileRuntime(ReservationScheduler scheduler)
        => TryReconcileRuntime(scheduler, false);

    internal bool TryReconcileRuntime(ReservationScheduler scheduler, bool requireSuccess)
    {
        lock (_gate)
        {
            if (_result is not { Applied: true, IsChain: true } || _runtimeReconcileRequested) return true;
            if (!scheduler.TryReconcileActiveContinuousChainTopology($"ReservationCancel:R{_requestedId}", requireSuccess) && requireSuccess) return false;
            _runtimeReconcileRequested = true;
            return true;
        }
    }
}
