namespace TvAIr.Schedule;

public sealed record TerminalLogDeletionResult(bool Applied, string Reason, IReadOnlyList<int> RemovedIds);

/// <summary>Clear user terminal history and repair chains in a single Store transaction.</summary>
public sealed class TerminalLogDeletionCommand : ReservationTopologyCommand
{
    private readonly object _gate = new();
    private TerminalLogDeletionResult? _result;
    public TerminalLogDeletionResult? Result { get { lock (_gate) return _result; } }
    internal override bool RequiresProjectionMetadata => false;
    internal override bool IsRejected => Result is { Applied: false };
    internal override void Apply(ReservationStore store, bool featureEnabled,
        ReservationProjectionMetadataStore? metadataStore = null)
    {
        lock (_gate)
        {
            if (_result is not null) return;
            _result = store.DeleteTerminalLogEntriesAtomic(committed => _result = committed);
        }
    }
}
