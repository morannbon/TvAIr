using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>
/// 予約が「まだ実行責務を持ち得る時間帯か」を判定する時間軸の正本。
/// runtime owner の実在判定そのものは ReservationScheduler が行うが、
/// Due / End+PostMargin / PowerGuard の時間境界を入口ごとに重複実装しない。
/// </summary>
internal static class ReservationRuntimeResponsibilityPolicy
{
    internal static DateTime GetDueAt(DateTime startTime, int preStartMarginSeconds)
        => startTime.AddSeconds(-Math.Max(0, preStartMarginSeconds));

    internal static DateTime GetDueAt(Reservation reservation, int preStartMarginSeconds)
        => GetDueAt(reservation.StartTime, preStartMarginSeconds);

    internal static DateTime GetExecutionDeadline(Reservation reservation, int postEndMarginSeconds)
        => reservation.EndTime.AddSeconds(Math.Max(10, Math.Max(0, postEndMarginSeconds)));

    internal static bool IsPastExecutionDeadline(Reservation reservation, DateTime now, int postEndMarginSeconds)
        => now >= GetExecutionDeadline(reservation, postEndMarginSeconds);

    internal static bool IsTerminal(ReservationStatus status)
        => ReservationLifecycleContract.IsTerminal(status);

    internal static bool IsPowerResponsibilityRelevant(
        Reservation reservation,
        DateTime now,
        int postEndMarginSeconds)
    {
        if (reservation.Source == ReservationSource.Epg || IsTerminal(reservation.Status))
            return false;

        // Recording / Stopping は runtime owner の終端処理が正本。
        // Starting は終了+post-marginを越えた時点で owner が無ければ Scheduler がterminalへ収束させるため、
        // PowerGuard単独で永久holdしてはならない。
        if (reservation.Status is ReservationStatus.Recording or ReservationStatus.Stopping)
            return true;

        if (reservation.Status == ReservationStatus.Starting)
            return !IsPastExecutionDeadline(reservation, now, postEndMarginSeconds);

        return reservation.Status == ReservationStatus.Scheduled
            && reservation.IsEnabled
            && !IsPastExecutionDeadline(reservation, now, postEndMarginSeconds);
    }

    internal static bool ShouldHoldPower(
        Reservation reservation,
        DateTime now,
        int preStartMarginSeconds,
        int postEndMarginSeconds,
        int lookAheadSeconds)
    {
        if (!IsPowerResponsibilityRelevant(reservation, now, postEndMarginSeconds))
            return false;

        if (ReservationLifecycleContract.IsRuntimeOwned(reservation.Status))
            return true;

        if (reservation.Status != ReservationStatus.Scheduled || !reservation.IsEnabled)
            return false;

        var due = GetDueAt(reservation, preStartMarginSeconds);
        return due <= now.AddSeconds(Math.Max(0, lookAheadSeconds));
    }
}
