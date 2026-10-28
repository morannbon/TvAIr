using TvAIr.Epg.Projection;

namespace TvAIr.Schedule;

internal sealed record ProjectionPromotionCandidate(ReservationProjectionMetadata Metadata, ProjectedProgramEvent Event);
internal sealed record ProjectionPromotionResult(IReadOnlyList<int> PromotedIds, int Skipped)
{
    internal bool TopologyChanged { get; init; }
    internal bool RetryRequired { get; init; }
}
