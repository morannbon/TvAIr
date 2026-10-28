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
    private readonly LogRepository _log;
    private readonly ConcurrentDictionary<string,ExternalProjectionCommand> _pendingExternalSourceChanges=new(StringComparer.OrdinalIgnoreCase);

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
        _log = log;
    }


    public bool HasPendingExternalSourceChange(string sourcePluginId)
        => _pendingExternalSourceChanges.TryGetValue(sourcePluginId,out var command) && command.RetainIfUnexecuted;

    public ProgramProjectionSourceChangeResult ApplyExternalSourceChange(string sourcePluginId,string action,bool runKeywordMatcher)
        => Submit(sourcePluginId,action,runKeywordMatcher,true);

    public ProgramProjectionReservationSyncResult ReconcileExternalSource(string sourcePluginId)
        => Submit(sourcePluginId,"Reconcile",false,false).Reconciled;

    private ProgramProjectionSourceChangeResult Submit(string sourcePluginId,string action,bool match,bool promote)
    {
        if(string.IsNullOrWhiteSpace(sourcePluginId))throw new ArgumentException("External program source id is required.",nameof(sourcePluginId));
        sourcePluginId=TvAIr.Plugin.PluginIdentity.Normalize(sourcePluginId);
        var command=new ExternalProjectionCommand(this,sourcePluginId,match,promote);
        _pendingExternalSourceChanges[sourcePluginId]=command;
        var route=_allocationRoute.Run(new("ExternalProgramSourceProjection",action,SyncProgramRuleReservations:false,
            TopologyCommands:new[]{command},WakeRefreshMode:ReservationAllocationWakeRefreshMode.BoundedCoalesce),
            waitForActiveSingleFlight:false);
        var result=command.Result?.Summary??new(0,0,0,0,0);
        var deferred=route.Deferred||route.RetryRequired||command.RetainIfUnexecuted;
        _log.Add("PROGRAM_PROJECTION_SOURCE_CHANGE_SYNC",sourcePluginId,
            $"result={(deferred ? "PENDING" : "OK")} action={action} promoted={command.Promoted} rebound={result.Rebound} sourceMissing={result.SourceMissing} ambiguous={result.Ambiguous} runKeywordMatcher={match} allocationDeferred={deferred} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
        return new(command.Promoted,result,deferred);
    }

    internal ReservationProjectionPromotionService Promotion=>_promotion;
    internal bool UsesStore(ReservationStore store)=>ReferenceEquals(store,_reservations);
    internal ExternalProjectionCommitResult Commit(ReservationStore store,string source,IReadOnlySet<int> protection,bool automatic,
        Action<ExternalProjectionCommitResult> receipt)
    {
        if(!ReferenceEquals(store,_reservations))throw new InvalidOperationException("External projection reservation owner differs.");
        return store.CommitExternalProjectionRebind(_metadata,_externalEvents,source,protection,automatic,receipt);
    }
}
