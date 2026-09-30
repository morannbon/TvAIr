using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>
/// Reservation lifecycle state classification single source of truth.
/// Transition ownership remains in ReservationStore / ReservationScheduler; this contract only
/// centralizes semantic state groups so presentation, power, chain topology and recovery do not
/// re-declare the same status sets independently.
/// </summary>
internal static class ReservationLifecycleContract
{
    internal static bool IsTerminal(ReservationStatus status)
        => status is ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed;

    internal static bool IsRuntimeOwned(ReservationStatus status)
        => status is ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping;

    internal static bool IsScheduledOrRuntimeOwned(ReservationStatus status)
        => status == ReservationStatus.Scheduled || IsRuntimeOwned(status);

    internal static bool IsChainTopologyActive(ReservationStatus status)
        => status is ReservationStatus.Scheduled or ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping;
}
