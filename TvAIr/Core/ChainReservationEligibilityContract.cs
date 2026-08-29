namespace TvAIr.Core;

/// <summary>
/// ユーザー明示チェーンの成立条件正本。
/// UI/Pluginの候補投影とAPI/Storeの再検証はこの判定を共有し、
/// Store Transactionだけがroot/cycle/successor一意性など保存トポロジーを追加検証する。
///
/// CHAIN_DEVELOPER_APPROVAL_REQUIRED — 変更禁止:
/// 判定条件または結果の意味を変更する場合はTvAIr開発者の明示承認を必須とする。
/// </summary>
public static class ChainReservationEligibilityContract
{
    public enum FailureReason
    {
        None = 0,
        FeatureDisabled,
        PredecessorMissing,
        PredecessorNotActive,
        PredecessorDisabled,
        PredecessorConflicted,
        TargetNotFuture,
        TargetUnavailable,
        TargetAlreadyReserved,
        NotSameService,
        NotAdjacent,
    }

    public readonly record struct Result(bool IsEligible, FailureReason Reason)
    {
        public string ReasonToken => Reason switch
        {
            FailureReason.None => "ok",
            FailureReason.FeatureDisabled => "feature_disabled",
            FailureReason.PredecessorMissing => "predecessor_missing",
            FailureReason.PredecessorNotActive => "predecessor_not_active",
            FailureReason.PredecessorDisabled => "predecessor_disabled",
            FailureReason.PredecessorConflicted => "predecessor_conflicted",
            FailureReason.TargetNotFuture => "target_not_future",
            FailureReason.TargetUnavailable => "target_unavailable",
            FailureReason.TargetAlreadyReserved => "target_already_reserved",
            FailureReason.NotSameService => "not_same_sid",
            FailureReason.NotAdjacent => "not_adjacent",
            _ => "unknown",
        };
    }

    public static bool IsActivePredecessorStatus(ReservationStatus status)
        => status is ReservationStatus.Scheduled or ReservationStatus.Starting or ReservationStatus.Recording;

    public static bool IsSameService(
        Reservation predecessor,
        ushort successorNetworkId,
        ushort successorTransportStreamId,
        ushort successorServiceId)
        => predecessor.ServiceId != 0
            && successorServiceId != 0
            && predecessor.NetworkId == successorNetworkId
            && predecessor.TransportStreamId == successorTransportStreamId
            && predecessor.ServiceId == successorServiceId;

    public static bool IsSameServiceAndAdjacent(Reservation predecessor, Reservation successor)
        => IsSameService(predecessor, successor.NetworkId, successor.TransportStreamId, successor.ServiceId)
            && ChainReservationContract.IsAdjacent(predecessor.EndTime, successor.StartTime);

    public static Result EvaluatePair(
        Reservation? predecessor,
        ushort successorNetworkId,
        ushort successorTransportStreamId,
        ushort successorServiceId,
        DateTime successorStart,
        bool featureEnabled = true)
    {
        if (!featureEnabled)
            return new Result(false, FailureReason.FeatureDisabled);
        if (predecessor is null)
            return new Result(false, FailureReason.PredecessorMissing);
        if (!IsActivePredecessorStatus(predecessor.Status))
            return new Result(false, FailureReason.PredecessorNotActive);
        if (!predecessor.IsEnabled)
            return new Result(false, FailureReason.PredecessorDisabled);
        if (predecessor.IsConflicted)
            return new Result(false, FailureReason.PredecessorConflicted);
        if (!IsSameService(predecessor, successorNetworkId, successorTransportStreamId, successorServiceId))
            return new Result(false, FailureReason.NotSameService);
        if (!ChainReservationContract.IsAdjacent(predecessor.EndTime, successorStart))
            return new Result(false, FailureReason.NotAdjacent);
        return new Result(true, FailureReason.None);
    }

    public static Result EvaluateOffer(
        Reservation? predecessor,
        ushort successorNetworkId,
        ushort successorTransportStreamId,
        ushort successorServiceId,
        DateTime successorStart,
        DateTime successorEnd,
        bool targetAvailable,
        bool targetAlreadyReserved,
        DateTime now,
        bool featureEnabled)
    {
        var pair = EvaluatePair(
            predecessor,
            successorNetworkId,
            successorTransportStreamId,
            successorServiceId,
            successorStart,
            featureEnabled);
        if (!pair.IsEligible)
            return pair;
        if (!targetAvailable)
            return new Result(false, FailureReason.TargetUnavailable);
        if (targetAlreadyReserved)
            return new Result(false, FailureReason.TargetAlreadyReserved);
        if (successorStart <= now || successorEnd <= now)
            return new Result(false, FailureReason.TargetNotFuture);
        return new Result(true, FailureReason.None);
    }
}
