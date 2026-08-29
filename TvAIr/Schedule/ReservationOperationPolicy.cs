using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>
/// Reservation operation availability derived from the persisted reservation identity.
/// UI surfaces consume this projection instead of reinterpreting ReservationSource.
/// </summary>
internal static class ReservationOperationPolicy
{
    public static bool IsPluginOwned(Reservation reservation)
        => string.Equals(reservation.CreatedThrough, "Plugin", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(reservation.CreatedByPluginId);

    public static bool CanToggleEnabled(Reservation reservation)
    {
        if (reservation.Source == ReservationSource.Epg)
            return false;
        // ENABLED_TOGGLE_LIFECYCLE_SINGLE_SOURCE:
        // 有効/無効の変更はScheduledだけで受け付ける。Starting以降は録画ライフサイクルが
        // 予約状態の正本を所有しているため、UI/APIからis_enabledを後段変更しない。
        if (reservation.Status != ReservationStatus.Scheduled)
            return false;
        if (reservation.Source == ReservationSource.Program && !IsPluginOwned(reservation))
            return false;
        return true;
    }

    public static bool CanCancel(Reservation reservation)
    {
        if (reservation.Status != ReservationStatus.Scheduled)
            return false;
        return reservation.Source == ReservationSource.Manual || IsPluginOwned(reservation);
    }
}
