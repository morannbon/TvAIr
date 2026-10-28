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
/// 本Coordinatorは共通AllocationRoute coreからのみ呼ばれるhelperとして、
/// topology commit → active runtime reconcile の順序を維持する。割当/PreRec/Wakeは同じcoreへ戻る。
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

    // Only the existing allocation core calls this after checking the latest INI.
    internal ChainFeatureTransitionResult ConvergeDisabledFeature(string source)
    {
        var runtimeAttachedIds = _scheduler.GetRuntimeAttachedChainReservationIds(DateTime.Now);
        var detach = _store.DetachScheduledUserChainsForFeatureDisable(source, runtimeAttachedIds);
        if (!detach.Applied)
        {
            _log.Add("CHAIN_LIFECYCLE_TRANSITION", "FeatureDisable",
                $"result=DETACH_FAILED source={source} latestFeatureEnabled=False reason={detach.Reason} " +
                "activeRuntimeMutation=none allocationAction=keep_persisted_topology_until_retry rule=chain_lifecycle_single_source");
            return new ChainFeatureTransitionResult(true, false, 0, detach.Reason);
        }

        // 正本の継ぎ目: topology commitが先。runtime control/lifetimeはそのcommit済みDBだけから再構築する。
        _scheduler.ReconcileActiveContinuousChainTopology($"FeatureDisabled:{source}");

        _log.Add("CHAIN_LIFECYCLE_TRANSITION", "FeatureDisable",
            $"result=APPLIED source={source} latestFeatureEnabled=False detachedScheduled={detach.DetachedReservations.Count} protectedRuntimeSegments={runtimeAttachedIds.Count} " +
            "reservationDelete=False activeAttachedSegmentsPreserved=True runtimeReconcileRequested=True next=allocation_in_same_core conflictDecision=user_reservation_list rule=chain_lifecycle_single_source");

        return new ChainFeatureTransitionResult(true, true, detach.DetachedReservations.Count, detach.Reason);
    }
}
