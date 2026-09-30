using TvAIr.Core;

namespace TvAIr.Schedule;

public sealed record ChainFeatureTransitionResult(
    bool TransitionedToDisabled,
    bool DetachApplied,
    int DetachedScheduledReservations,
    string Reason);

/// <summary>
/// ユーザー明示チェーンの設定遷移とruntime収束をつなぐ単一Coordinator。
/// ReservationStoreは永続topologyだけ、ReservationSchedulerはactive physical captureだけを所有する。
/// 本Coordinatorは topology commit → active runtime reconcile の順序だけを所有し、
/// FinalConflictPlan/PreRec/Wake再評価は呼出元の共通AllocationRouteへ引き渡す。
/// </summary>
public sealed class ChainLifecycleCoordinator
{
    private readonly ReservationStore _store;
    private readonly ReservationScheduler _scheduler;
    private readonly LogRepository _log;

    public ChainLifecycleCoordinator(
        ReservationStore store,
        ReservationScheduler scheduler,
        LogRepository log)
    {
        _store = store;
        _scheduler = scheduler;
        _log = log;
    }

    public ChainFeatureTransitionResult ApplyFeatureTransition(
        bool beforeLaterProgramPriority,
        bool beforeChainRecording,
        bool afterLaterProgramPriority,
        bool afterChainRecording,
        string source)
    {
        var beforeEnabled = ChainReservationContract.IsFeatureEnabled(beforeLaterProgramPriority, beforeChainRecording);
        var afterEnabled = ChainReservationContract.IsFeatureEnabled(afterLaterProgramPriority, afterChainRecording);
        if (!beforeEnabled || afterEnabled)
        {
            return new ChainFeatureTransitionResult(false, true, 0, "no_enabled_to_disabled_transition");
        }

        var runtimeAttachedIds = _scheduler.GetRuntimeAttachedChainReservationIds(DateTime.Now);
        var detach = _store.DetachScheduledUserChainsForFeatureDisable(source, runtimeAttachedIds);
        if (!detach.Applied)
        {
            _log.Add("CHAIN_LIFECYCLE_TRANSITION", "FeatureDisable",
                $"result=DETACH_FAILED source={source} beforeEnabled={beforeEnabled} afterEnabled={afterEnabled} reason={detach.Reason} " +
                "activeRuntimeMutation=none allocationAction=keep_persisted_topology_until_retry rule=chain_lifecycle_single_source");
            return new ChainFeatureTransitionResult(true, false, 0, detach.Reason);
        }

        // 正本の継ぎ目: topology commitが先。runtime control/lifetimeはそのcommit済みDBだけから再構築する。
        _scheduler.ReconcileActiveContinuousChainTopology($"FeatureDisabled:{source}");

        _log.Add("CHAIN_LIFECYCLE_TRANSITION", "FeatureDisable",
            $"result=APPLIED source={source} beforeEnabled={beforeEnabled} afterEnabled={afterEnabled} detachedScheduled={detach.DetachedReservations.Count} protectedRuntimeSegments={runtimeAttachedIds.Count} " +
            "reservationDelete=False activeAttachedSegmentsPreserved=True runtimeReconciled=True next=common_allocation_route conflictDecision=user_reservation_list rule=chain_lifecycle_single_source");

        return new ChainFeatureTransitionResult(true, true, detach.DetachedReservations.Count, detach.Reason);
    }
}
