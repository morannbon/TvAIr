namespace TvAIr.Tuner;

/// <summary>
/// Host-owned one-shot Viewer Reservation contract.
/// ViewerProfileId + ScheduledStart is the execution-slot identity; ScheduledEnd is programme
/// metadata only and never extends ViewerProfile occupancy.
/// </summary>
internal static class ViewerReservationContract
{
    internal static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(10);

    internal static bool OwnsExecutionSlot(ViewerReservationState state)
        => state is ViewerReservationState.Scheduled or ViewerReservationState.Completed;

    internal static bool ConflictsExecutionSlot(
        ViewerReservationRecord row,
        string viewerProfileId,
        DateTimeOffset scheduledStart)
        => OwnsExecutionSlot(row.State)
           && string.Equals(row.ViewerProfileId, viewerProfileId, StringComparison.OrdinalIgnoreCase)
           && row.ScheduledStart == scheduledStart;
}
