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
        var command=new ProjectionPromotionCommand(this,runAllocationRoute);
        try
        {
            _allocationRoute.Run(new("ReservationProjectionPromotion",source,SyncProgramRuleReservations:false,
                ReevaluateAllocations:runAllocationRoute,RefreshPreRecordEpgEntries:runAllocationRoute,
                RefreshWakeTask:runAllocationRoute,TopologyCommands:new[]{command}));
        }
        catch(Exception ex) when(command.Result is not null)
        {
            _log.Add("RESERVATION_PROJECTION_PROMOTE","Notification",
                $"result=COMMITTED_FOLLOWUP_ERROR source={Safe(source)} promoted={command.Result.PromotedIds.Count} pendingRetry={command.RetainIfUnexecuted} error={Safe(ex.Message)} rule=release_contract");
        }
        var result=command.Result;
        _log.Add("RESERVATION_PROJECTION_PROMOTE","Summary",
            $"result={(command.RetainIfUnexecuted ? "PENDING" : "OK")} source={Safe(source)} promoted={result?.PromotedIds.Count??0} skipped={result?.Skipped??0} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
        return result?.PromotedIds.Count??0;
    }

    internal EpgCaptureCommitCommand SubmitCapturedEpg(TvAIr.Epg.EpgStore epg, IReadOnlyList<EpgEvent> upsert,
        IReadOnlyList<EpgEvent>? retire, IReadOnlyList<EpgEvent>? preserve, SemaphoreSlim captureLane,
        CancellationToken cancellation, IReadOnlyList<EpgEvent> other, EpgCaptureImportContext context)
    {
        cancellation.ThrowIfCancellationRequested();
        var command = new EpgCaptureCommitCommand(epg, upsert, retire, preserve, captureLane,
            cancellation, other, this, context);
        try
        {
            _allocationRoute.Run(new("EpgCapture", context.Source, SyncProgramRuleReservations: false,
                TopologyCommands: new[] { command }, WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce),
                waitForActiveSingleFlight: false);
        }
        catch (Exception ex)
        {
            _log.Add("EPG_IMPORT_SUBMIT", "CommonRoute",
                $"source={Safe(context.Source)} rawCommitted={command.Result?.Applied == true} complete={command.IsComplete} retryRetained={command.RetainIfUnexecuted} error={Safe(ex.Message)} rule=epg_import_commit_contract");
            if (command.Result is null && cancellation.IsCancellationRequested) cancellation.ThrowIfCancellationRequested();
            if (!command.RetainIfUnexecuted && !command.IsComplete) throw;
        }
        if (command.Result?.Applied != true && cancellation.IsCancellationRequested)
            cancellation.ThrowIfCancellationRequested();
        return command;
    }

    internal bool UsesReservationStore(ReservationStore store) => ReferenceEquals(_reservations, store);

    internal ProjectionPromotionResult PromotePendingAtomicCore(IReadOnlySet<int> protectedIds,bool automatic,
        Action<ProjectionPromotionResult> recordCommitted)
    {
        var pending = _metadata.GetPendingPromotionCandidates();
        var candidates = pending.Select(metadata => (Metadata: metadata, Event: FindDbEvent(metadata)))
            .Where(pair => pair.Event is not null)
            .Select(pair => new ProjectionPromotionCandidate(pair.Metadata, pair.Event!)).ToArray();
        if (candidates.Length == 0)
        {
            var empty = new ProjectionPromotionResult(Array.Empty<int>(), pending.Count);
            recordCommitted(empty);
            return empty;
        }
        ProjectionPromotionResult CountUnmatched(ProjectionPromotionResult result)
            => result with { Skipped = result.Skipped + pending.Count - candidates.Length };
        return CountUnmatched(_reservations.PromoteProjectionReservationsAtomic(candidates, _metadata,
            protectedIds, value => recordCommitted(CountUnmatched(value)),automatic));
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
