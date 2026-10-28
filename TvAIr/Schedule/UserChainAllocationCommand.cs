using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>
/// One explicit manual-chain intent carried by the existing allocation route.
/// Null Result means no mutation has completed; allocation success is reported separately.
/// This is an in-process receipt, not a persisted job or permission to edit runtime state.
/// </summary>
public sealed class UserChainAllocationCommand : ReservationTopologyCommand
{
    private readonly object _gate = new();
    private readonly Reservation _requested;
    private readonly int _predecessorId;
    private UserChainMutationResult? _result;
    private readonly ReservationProjectionMetadata? _metadata;
    private bool _metadataSaved;
    internal override bool RequiresProjectionMetadata => _metadata is not null;

    public UserChainAllocationCommand(Reservation requested, int predecessorId, ReservationProjectionMetadata? projectionMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(requested);
        _requested = requested.SnapshotForChainRequest();
        _predecessorId = predecessorId;
        _metadata = projectionMetadata;
    }

    public UserChainMutationResult? Result
    {
        get { lock (_gate) return _result; }
    }

    internal override bool IsRejected => Result is { Applied: false };

    internal override void Apply(ReservationStore store, bool featureEnabled, ReservationProjectionMetadataStore? metadataStore = null)
    {
        lock (_gate)
        {
            if (_metadata is not null && metadataStore is null)
                throw new InvalidOperationException("Projection metadata store is unavailable.");
            if (_result is null)
            {
                var predecessor = store.GetById(_predecessorId);
                if (predecessor is null)
                    _result = new(false, false, "predecessor_missing", 0, null);
                else
                    _result = store.AddOrPromoteUserChain(_requested.SnapshotForChainRequest(),
                        _predecessorId, predecessor.UserChainRootId ?? predecessor.Id, featureEnabled, committed => _result = committed);
            }
            // Metadata precedes allocation/PreRec, as on the original program-guide path.
            // A metadata fault must not hide the already committed reservation ID.
            if (_result.Applied && _metadata is not null && !_metadataSaved)
            {
                metadataStore!.Upsert(_metadata with { ReservationId = _result.ReservationId });
                _metadataSaved = true;
            }
        }
    }
}
