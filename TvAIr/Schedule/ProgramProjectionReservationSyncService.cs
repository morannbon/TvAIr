using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Epg.Projection;
using System.Collections.Concurrent;

namespace TvAIr.Schedule;

public sealed record ProgramProjectionReservationSyncResult(
    int Candidates,
    int Rebound,
    int SourceMissing,
    int Ambiguous,
    int Unchanged);

public sealed record ProgramProjectionSourceChangeResult(
    int Promoted,
    ProgramProjectionReservationSyncResult Reconciled,
    bool AllocationDeferred);

/// <summary>
/// ExternalEpg snapshot変更時に、既存OverlayOnly予約を同じReservationIdのまま再結合する。
/// 予約削除・無効化・推測一致は行わない。
/// </summary>
public sealed class ProgramProjectionReservationSyncService
{
    private readonly ReservationStore _reservations;
    private readonly ReservationProjectionMetadataStore _metadata;
    private readonly ExternalEpgSourceStore _externalEvents;
    private readonly ReservationProjectionPromotionService _promotion;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly KeywordMatcher _keywordMatcher;
    private readonly LogRepository _log;
    private readonly ConcurrentDictionary<string, string> _pendingExternalSourceChanges = new(StringComparer.OrdinalIgnoreCase);

    public ProgramProjectionReservationSyncService(
        ReservationStore reservations,
        ReservationProjectionMetadataStore metadata,
        ExternalEpgSourceStore externalEvents,
        ReservationProjectionPromotionService promotion,
        ReservationAllocationRouteService allocationRoute,
        KeywordMatcher keywordMatcher,
        LogRepository log)
    {
        _reservations = reservations;
        _metadata = metadata;
        _externalEvents = externalEvents;
        _promotion = promotion;
        _allocationRoute = allocationRoute;
        _keywordMatcher = keywordMatcher;
        _log = log;
    }


    public bool HasPendingExternalSourceChange(string sourcePluginId)
        => !string.IsNullOrWhiteSpace(sourcePluginId) && _pendingExternalSourceChanges.ContainsKey(sourcePluginId);

    public ProgramProjectionSourceChangeResult ApplyExternalSourceChange(
        string sourcePluginId,
        string action,
        bool runKeywordMatcher)
    {
        if (string.IsNullOrWhiteSpace(sourcePluginId))
            throw new ArgumentException("External program source id is required.", nameof(sourcePluginId));

        _pendingExternalSourceChanges[sourcePluginId] = action;
        try
        {
            var promoted = _promotion.PromotePending($"ExternalEpg:{action}:{sourcePluginId}", runAllocationRoute: false);
            var reconciled = ReconcileExternalSource(sourcePluginId);

            // External source replacement is the authoritative point at which the new snapshot is
            // already visible through IProgramEventSource.  Do not delegate this one-shot match to
            // an unrelated active allocation single-flight: a follower can receive the active
            // flight result while the semantic matcher request is no longer externally observable.
            // Execute exactly once here, then send all resulting reservation mutations through the
            // common allocation route.  No timer, delay, retry loop, or plugin-specific branch.
            var keywordAdded = runKeywordMatcher ? _keywordMatcher.RunMatching() : 0;

            var requiresAllocation = runKeywordMatcher || keywordAdded > 0 || promoted > 0 || reconciled.Rebound > 0;
            var allocationDeferred = false;
            if (requiresAllocation)
            {
                var allocationResult = _allocationRoute.Run(new ReservationAllocationRouteRequest(
                    Source: "ExternalProgramSourceProjection",
                    Action: action,
                    RunKeywordMatcher: false,
                    SyncProgramRuleReservations: false,
                    ReevaluateAllocations: true,
                    RefreshPreRecordEpgEntries: true,
                    RefreshWakeTask: true,
                    EmitConflictLogs: true,
                    ConflictLogCategory: "ExternalProgramSourceProjection",
                    ConflictLogTitle: "Conflict",
                    ExecutionMode: "ProjectionChanged",
                    WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));
                allocationDeferred = allocationResult.Deferred;
            }

            _pendingExternalSourceChanges.TryRemove(sourcePluginId, out _);
            _log.Add("PROGRAM_PROJECTION_SOURCE_CHANGE_SYNC", Safe(sourcePluginId),
                $"result=OK action={Safe(action)} promoted={promoted} rebound={reconciled.Rebound} sourceMissing={reconciled.SourceMissing} ambiguous={reconciled.Ambiguous} runKeywordMatcher={runKeywordMatcher} keywordMatcherExecuted={runKeywordMatcher} keywordAdded={keywordAdded} allocationRun={requiresAllocation} allocationDeferred={allocationDeferred} sourceContract=external_program_source rule=release_contract");

            return new ProgramProjectionSourceChangeResult(promoted, reconciled, allocationDeferred);
        }
        catch (Exception ex)
        {
            _log.Add("PROGRAM_PROJECTION_SOURCE_CHANGE_SYNC", Safe(sourcePluginId),
                $"result=FAILED action={Safe(action)} pendingRetry=True runKeywordMatcher={runKeywordMatcher} error={Safe(ex.Message)} sourceContract=external_program_source rule=release_contract");
            throw;
        }
    }

    public ProgramProjectionReservationSyncResult ReconcileExternalSource(string sourcePluginId)
    {
        var candidates = _metadata.GetPendingPromotionCandidates()
            .Where(x => string.Equals(x.ProjectionSourcePluginId, sourcePluginId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var events = _externalEvents.GetBySourcePlugin(sourcePluginId);
        var byIdentity = events.GroupBy(BuildIdentity).ToDictionary(x => x.Key, x => x.ToArray());

        var rebound = 0;
        var sourceMissing = 0;
        var ambiguous = 0;
        var unchanged = 0;
        foreach (var metadata in candidates)
        {
            var reservation = _reservations.GetById(metadata.ReservationId);
            if (reservation is null || reservation.Status != ReservationStatus.Scheduled)
            {
                unchanged++;
                continue;
            }

            var identity = BuildIdentity(metadata.OverlayNetworkId, metadata.OverlayTransportStreamId, metadata.OverlayServiceId, metadata.OverlayEventId);
            if (!byIdentity.TryGetValue(identity, out var matches) || matches.Length == 0)
            {
                if (!string.Equals(metadata.ProjectionState, "SourceMissing", StringComparison.Ordinal))
                {
                    _metadata.MarkSourceMissing(metadata.ReservationId, DateTime.Now);
                    sourceMissing++;
                }
                else
                {
                    unchanged++;
                }
                continue;
            }

            if (matches.Length != 1)
            {
                ambiguous++;
                _log.Add("RESERVATION_PROJECTION_REBIND", $"R{metadata.ReservationId}",
                    $"result=SKIPPED reason=ambiguous_exact_identity sourcePluginId={Safe(sourcePluginId)} matches={matches.Length} nid={metadata.OverlayNetworkId} tsid={metadata.OverlayTransportStreamId} sid={metadata.OverlayServiceId} eventId={metadata.OverlayEventId} reservationPreserved=True rule=release_contract");
                continue;
            }

            var externalEvent = matches[0];
            var reservationChanged = _reservations.RebindProjectionToExternalEvent(metadata.ReservationId, externalEvent);
            var metadataChanged = MetadataChanged(metadata, externalEvent);
            if (metadataChanged) _metadata.RebindExternalSource(metadata.ReservationId, externalEvent);

            if (reservationChanged || metadataChanged) rebound++;
            else unchanged++;
        }

        var result = new ProgramProjectionReservationSyncResult(candidates.Length, rebound, sourceMissing, ambiguous, unchanged);
        _log.Add("PROGRAM_PROJECTION_RESERVATION_SYNC", Safe(sourcePluginId),
            $"result=OK candidates={result.Candidates} rebound={result.Rebound} sourceMissing={result.SourceMissing} ambiguous={result.Ambiguous} unchanged={result.Unchanged} reservationIdentity=reservation_id eventIdentity=nid_tsid_sid_eventId guessing=disabled rule=release_contract");
        return result;
    }

    private static bool MetadataChanged(ReservationProjectionMetadata metadata, ExternalEpgEvent externalEvent)
        => !string.Equals(metadata.ProjectionState, "OverlayOnly", StringComparison.Ordinal)
            || metadata.SourceMissingAt.HasValue
            || !string.Equals(metadata.ProjectionSourceEventKey, externalEvent.SourceEventKey, StringComparison.Ordinal)
            || metadata.OverlayNetworkId != externalEvent.NetworkId
            || metadata.OverlayTransportStreamId != externalEvent.TransportStreamId
            || metadata.OverlayServiceId != externalEvent.ServiceId
            || metadata.OverlayEventId != externalEvent.EventId
            || metadata.OverlayStartTime != externalEvent.Start
            || metadata.OverlayEndTime != externalEvent.End
            || !string.Equals(metadata.OverlayTitle, externalEvent.Title, StringComparison.Ordinal);

    private static string BuildIdentity(ExternalEpgEvent e)
        => BuildIdentity(e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId);

    private static string BuildIdentity(ushort nid, ushort tsid, ushort sid, ushort eventId)
        => $"{nid}:{tsid}:{sid}:{eventId}";

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
