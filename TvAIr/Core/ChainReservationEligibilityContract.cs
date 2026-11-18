namespace TvAIr.Core;

/// <summary>
/// ユーザー明示チェーンの成立条件正本。
/// UI/Pluginの候補投影とAPI/Storeの再検証はこの判定を共有し、
/// Store Transactionだけがroot/cycle/successor一意性など保存トポロジーを追加検証する。
///
/// INVARIANT:
/// 候補表示と保存前再検証はこの判定を共有し、別の成立条件を追加しない。
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

    /// <summary>
    /// Existing-edge structural contract, independent of predecessor lifecycle or enabled state.
    /// A cancelled/completed historical predecessor is still part of persisted ancestry; it is
    /// not a new recording admission and cannot itself consume a tuner or start a worker.
    /// A successor must independently qualify as an active executable reservation in its caller.
    /// New chain creation still uses EvaluatePair / EvaluateTopologyPair and requires an active
    /// predecessor. After persistence, predecessor-only time-follow may extend negative overlap;
    /// positive gaps outside the normal adjacency window remain invalid.
    /// </summary>
    public static bool IsPersistedExplicitRuntimePair(Reservation predecessor, Reservation successor)
    {
        // Persisted identity is mandatory even at an exactly adjacent boundary. Lifecycle
        // state of the historical predecessor is deliberately not a structural input.
        var predecessorRoot = predecessor.UserChainRootId ?? predecessor.Id;
        if (!successor.IsUserChain || successor.UserChainPreviousId != predecessor.Id
            || successor.UserChainRootId != predecessorRoot
            || !IsSameService(predecessor, successor.NetworkId, successor.TransportStreamId, successor.ServiceId))
            return false;

        if (ChainReservationContract.IsAdjacent(predecessor.EndTime, successor.StartTime))
            return true;

        // Persisted explicit time-follow may extend overlap into a negative gap.
        // A positive break outside the normal adjacency window is never accepted.
        return (successor.StartTime - predecessor.EndTime).TotalSeconds
            < ChainReservationContract.AdjacentMinGapSeconds;
    }

    /// <summary>
    /// The persisted lineage is the sole source for a usable chain edge. A former root may
    /// be Cancelled or Completed: status is not a structural property. Every stored edge,
    /// including historical ancestors, must still agree on predecessor, root, service and
    /// boundary. No new edge is inferred from adjacent times or a common tuner.
    /// </summary>
    public static bool TryResolvePersistedLineage(
        Reservation reservation,
        IReadOnlyDictionary<int, Reservation> allById,
        out int rootId,
        out string failureReason,
        out int depth)
    {
        rootId = reservation.UserChainRootId
            ?? (reservation.UserChainPreviousId.HasValue ? 0 : reservation.Id);
        failureReason = string.Empty;
        depth = 0;

        var visited = new HashSet<int>();
        var cursor = reservation;
        while (cursor.UserChainPreviousId is int predecessorId)
        {
            if (!visited.Add(cursor.Id))
            {
                failureReason = "chain_cycle_detected";
                return false;
            }
            depth++;
            if (!allById.TryGetValue(predecessorId, out var predecessor))
            {
                failureReason = "chain_broken_predecessor";
                return false;
            }
            if (!IsPersistedExplicitRuntimePair(predecessor, cursor))
            {
                failureReason = "chain_invalid_persisted_edge";
                return false;
            }
            cursor = predecessor;
        }

        var lineageRootId = cursor.UserChainRootId ?? cursor.Id;
        if (reservation.UserChainPreviousId.HasValue && !reservation.UserChainRootId.HasValue)
        {
            failureReason = "chain_root_missing";
            return false;
        }
        if (rootId == 0) rootId = lineageRootId;
        if (rootId != lineageRootId)
        {
            failureReason = "chain_root_mismatch";
            return false;
        }
        return true;
    }

    /// <summary>
    /// All consumers (allocation, recording admission, physical boundary, EPG, Wake and
    /// presentation) project the same verified persisted edges. Settings and active capture
    /// ownership select execution, without modifying the stored graph. A branched active
    /// predecessor is rejected as a whole, including its downstream descendants.
    /// </summary>
    public static Dictionary<int, int> ProjectPersistedPredecessors(
        IReadOnlyDictionary<int, Reservation> allById,
        bool includeTerminal = false,
        bool includeDisabled = false,
        bool operationalOnly = false,
        bool featureEnabled = true,
        IReadOnlySet<int>? activeSharedCaptureRootIds = null)
    {
        if (operationalOnly && !featureEnabled
            && (activeSharedCaptureRootIds is null || activeSharedCaptureRootIds.Count == 0))
            return new Dictionary<int, int>();

        var eligible = allById.Values
            .Where(r => r.Source != ReservationSource.Epg
                && ChainReservationContract.IsStoredSuccessor(r)
                && r.Status != ReservationStatus.Cancelled
                && (includeTerminal || r.Status is not (ReservationStatus.Completed or ReservationStatus.Failed))
                && (includeDisabled || r.IsEnabled)
                && (!operationalOnly || ChainReservationContract.IsOperationalRoot(
                    featureEnabled, r.UserChainRootId ?? r.Id, activeSharedCaptureRootIds)))
            .Where(r => TryResolvePersistedLineage(r, allById, out _, out _, out _))
            .ToArray();

        var rejected = eligible.GroupBy(r => r.UserChainPreviousId!.Value)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(r => r.Id))
            .ToHashSet();

        // Reject descendants of any ambiguous branch, rather than accepting a tail whose
        // immediate predecessor cannot be represented by one physical chain owner.
        bool changed;
        do
        {
            changed = false;
            foreach (var row in eligible)
            {
                if (row.UserChainPreviousId is int previousId && rejected.Contains(previousId))
                    changed |= rejected.Add(row.Id);
            }
        } while (changed);

        return eligible.Where(r => !rejected.Contains(r.Id))
            .ToDictionary(r => r.Id, r => r.UserChainPreviousId!.Value);
    }

    private static Result EvaluatePairCore(
        Reservation? predecessor,
        ushort successorNetworkId,
        ushort successorTransportStreamId,
        ushort successorServiceId,
        DateTime successorStart,
        bool featureEnabled,
        bool rejectCurrentConflict)
    {
        if (!featureEnabled)
            return new Result(false, FailureReason.FeatureDisabled);
        if (predecessor is null)
            return new Result(false, FailureReason.PredecessorMissing);
        if (!IsActivePredecessorStatus(predecessor.Status))
            return new Result(false, FailureReason.PredecessorNotActive);
        if (!predecessor.IsEnabled)
            return new Result(false, FailureReason.PredecessorDisabled);
        if (rejectCurrentConflict && predecessor.IsConflicted)
            return new Result(false, FailureReason.PredecessorConflicted);
        if (!IsSameService(predecessor, successorNetworkId, successorTransportStreamId, successorServiceId))
            return new Result(false, FailureReason.NotSameService);
        if (!ChainReservationContract.IsAdjacent(predecessor.EndTime, successorStart))
            return new Result(false, FailureReason.NotAdjacent);
        return new Result(true, FailureReason.None);
    }

    /// <summary>
    /// UI/APIの1回の明示チェーン操作に使う成立条件。現在競合中の前番組から新しい後続を
    /// 追加する操作は拒否する。競合はあくまで現在の共通割当結果であり、保存トポロジー
    /// そのものの構造条件ではないため、複数の既存Scheduled予約を同一Transactionで
    /// 連続列へ昇格する場合は EvaluateTopologyPair を使い、列全体確定後に共通割当で
    /// 競合を再評価する。
    /// </summary>
    public static Result EvaluatePair(
        Reservation? predecessor,
        ushort successorNetworkId,
        ushort successorTransportStreamId,
        ushort successorServiceId,
        DateTime successorStart,
        bool featureEnabled = true)
        => EvaluatePairCore(
            predecessor,
            successorNetworkId,
            successorTransportStreamId,
            successorServiceId,
            successorStart,
            featureEnabled,
            rejectCurrentConflict: true);

    /// <summary>
    /// 保存トポロジーを原子的に構築する際の構造条件正本。
    /// 現在の IsConflicted はFinalConflictPlanの派生状態なのでここでは成立条件に含めない。
    /// 優先順位・前後マージン・競合判定を緩和するものではなく、トポロジー確定後に
    /// 必ず既存の共通割当/FinalConflictPlanへ戻して再評価する。
    /// </summary>
    public static Result EvaluateTopologyPair(
        Reservation? predecessor,
        ushort successorNetworkId,
        ushort successorTransportStreamId,
        ushort successorServiceId,
        DateTime successorStart,
        bool featureEnabled = true)
        => EvaluatePairCore(
            predecessor,
            successorNetworkId,
            successorTransportStreamId,
            successorServiceId,
            successorStart,
            featureEnabled,
            rejectCurrentConflict: false);

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
