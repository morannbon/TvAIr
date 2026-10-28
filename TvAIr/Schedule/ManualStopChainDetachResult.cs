using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>Outcome of the committed manual-stop topology mutation, before observer delivery.</summary>
public sealed record ManualStopChainDetachResult(bool Applied, string Reason, int PredecessorId,
    IReadOnlyList<Reservation> DetachedSuccessors)
{
    public IReadOnlyList<Reservation> CancelledSiblings { get; init; } = Array.Empty<Reservation>();
}
