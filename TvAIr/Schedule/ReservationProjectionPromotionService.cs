using TvAIr.Core;
using TvAIr.Epg.Projection;

namespace TvAIr.Schedule;

/// <summary>
/// OverlayOnly 由来予約について、後から同一番組の TvAIr DB イベントが現れた場合に
/// 予約本体を DB イベント identity へ寄せる。epg_events には書かない。
/// </summary>
public sealed class ReservationProjectionPromotionService
{
    private readonly ReservationStore _reservations;
    private readonly ReservationProjectionMetadataStore _metadata;
    private readonly DbProgramEventSource _dbEvents;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly LogRepository _log;

    public ReservationProjectionPromotionService(
        ReservationStore reservations,
        ReservationProjectionMetadataStore metadata,
        DbProgramEventSource dbEvents,
        ReservationAllocationRouteService allocationRoute,
        LogRepository log)
    {
        _reservations = reservations;
        _metadata = metadata;
        _dbEvents = dbEvents;
        _allocationRoute = allocationRoute;
        _log = log;
    }

    public int PromotePending(string source, bool runAllocationRoute = true)
    {
        var candidates = _metadata.GetPendingPromotionCandidates();
        if (candidates.Count == 0) return 0;

        var promoted = 0;
        var skipped = 0;
        foreach (var metadata in candidates)
        {
            var reservation = _reservations.GetById(metadata.ReservationId);
            if (reservation is null || reservation.Status != ReservationStatus.Scheduled)
            {
                skipped++;
                continue;
            }

            var dbEvent = FindDbEvent(metadata);
            if (dbEvent is null)
            {
                skipped++;
                continue;
            }

            if (_reservations.PromoteProjectionToDbEvent(metadata.ReservationId, dbEvent))
            {
                _metadata.MarkPromoted(
                    metadata.ReservationId,
                    dbEvent.NetworkId,
                    dbEvent.TransportStreamId,
                    dbEvent.ServiceId,
                    dbEvent.EventId,
                    DateTime.Now);
                promoted++;
            }
            else
            {
                skipped++;
            }
        }

        _log.Add("RESERVATION_PROJECTION_PROMOTE", "Summary",
            $"result=OK source={Safe(source)} candidates={candidates.Count} promoted={promoted} skipped={skipped} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");

        if (promoted > 0 && runAllocationRoute)
            RunAllocationRoute(source);

        return promoted;
    }

    public void RunAllocationRoute(string source, ReservationAllocationWakeRefreshMode wakeRefreshMode = ReservationAllocationWakeRefreshMode.Immediate)
    {
        _allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "ReservationProjectionPromotion",
            Action: source,
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: false,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: true,
            RefreshWakeTask: true,
            EmitConflictLogs: true,
            ConflictLogCategory: "ReservationProjectionPromotion",
            ConflictLogTitle: "Conflict",
            ExecutionMode: "Promotion",
            WakeRefreshMode: wakeRefreshMode));
    }

    private ProjectedProgramEvent? FindDbEvent(ReservationProjectionMetadata metadata)
    {
        if (metadata.OverlayNetworkId == 0 || metadata.OverlayTransportStreamId == 0 || metadata.OverlayServiceId == 0)
            return null;

        if (metadata.OverlayEventId == 0)
            return null;

        var exact = _dbEvents.GetByEventKey(
            metadata.OverlayNetworkId,
            metadata.OverlayTransportStreamId,
            metadata.OverlayServiceId,
            metadata.OverlayEventId);

        return exact?.DbEvent is not null
            && exact.NetworkId == metadata.OverlayNetworkId
            && exact.TransportStreamId == metadata.OverlayTransportStreamId
            && exact.ServiceId == metadata.OverlayServiceId
            && exact.EventId == metadata.OverlayEventId
                ? exact
                : null;
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
