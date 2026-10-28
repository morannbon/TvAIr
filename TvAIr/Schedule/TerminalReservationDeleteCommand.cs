using TvAIr.Core;

namespace TvAIr.Schedule;

public sealed record TerminalReservationDeleteResult(bool Applied, string Reason, int ReservationId);

/// <summary>Delete a terminal row and repair its chain in the original Store transaction.</summary>
public sealed class TerminalReservationDeleteCommand : ReservationTopologyCommand
{
    private readonly object _gate = new();
    private readonly Reservation _expected;
    private TerminalReservationDeleteResult? _result;
    public TerminalReservationDeleteCommand(Reservation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        _expected = expected.SnapshotForChainRequest();
    }
    public TerminalReservationDeleteResult? Result { get { lock (_gate) return _result; } }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool IsRejected => Result is { Applied: false };
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        lock (_gate)
        {
            if (_result is not null) return;
            // Foreign-key cascade removes provenance in this same Store transaction.
            // Capture commit before observers, so their errors cannot mean "not deleted".
            var applied = store.TryDeleteTerminalReservationAtomicCas(_expected, out _,
                () => _result = new(true, "applied", _expected.Id));
            _result = new(applied, applied ? "applied" : "terminal_delete_cas_rejected", _expected.Id);
        }
    }
}
