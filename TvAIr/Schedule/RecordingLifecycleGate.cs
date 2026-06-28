using System.Collections.Concurrent;


namespace TvAIr.Schedule;

/// <summary>
/// 録画開始・録画終了・EPG取得の衝突を抑える共通ライフサイクルゲート。
///
/// 目的:
/// - 本番録画の直前/停止中/停止直後に、同一放送波のEPG workerを再投入しない。
/// - 録画終了時のTVTest/BonDriver Close と次の Open/SetCh が背中合わせになる事故を避ける。
/// - 個別予約IDではなく、局名・番組名・放送波単位でログ追跡できるようにする。
///
/// 注意:
/// - ここではチューナーを直接解放しない。TunerPool の状態遷移は TunerPool 側で行う。
/// - このクラスは録画/EPG双方から参照される軽量な共通ゲートで、ピンポイント対策ではなく
///   TvAIr全体の録画ライフサイクル境界を明確にするためのもの。
/// </summary>
public static class RecordingLifecycleGate
{
    private sealed record Suppression(DateTime Until, string Reason, string Owner, string Label);

    private sealed record SuppressionLogState(DateTime Until, string Reason, string Owner);

    private sealed record StableRecordingAllowance(DateTime NotBefore, DateTime Until, string Owner, string Label);

    private sealed record RecordingTimelineBoundary(DateTime BoundaryAt, DateTime BlockNewWorkerAfter, DateTime ProtectUntil, string Reason, string Owner, string Label);

    private static readonly ConcurrentDictionary<string, Suppression> EpgSuppressions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SuppressionLogState> EpgSuppressionLogStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, StableRecordingAllowance> StableRecordingEpgAllowances = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, RecordingTimelineBoundary> RecordingTimelineBoundaries = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// 次の録画開始・チェーンhandoff・異局重複録画など、録画タイムライン上の境界を登録する。
    /// 通常EPG workerの予定終了がこの境界に食い込む場合は、安定録画中であっても新規投入を止める。
    /// </summary>
    public static void RegisterRecordingTimelineBoundary(string group, DateTime boundaryAt, DateTime protectUntil, string reason, string owner, string label, DateTime? blockNewWorkerAfter = null)
    {
        group = NormalizeGroup(group);
        var next = new RecordingTimelineBoundary(boundaryAt, blockNewWorkerAfter ?? boundaryAt.AddSeconds(-30), protectUntil, Safe(reason), Safe(owner), Safe(label));
        RecordingTimelineBoundaries.AddOrUpdate(group, next, (_, current) =>
        {
            var now = DateTime.Now;
            if (current.ProtectUntil <= now) return next;
            // 現在録画の過去due境界を保持し続けると、次予約の安全窓が登録されず、
            // 安定録画中EPG復帰後に次予約直前workerを投入してしまう。
            if (current.BoundaryAt <= now && next.BoundaryAt > now) return next;
            if (next.BoundaryAt <= now && current.BoundaryAt > now) return current;
            return current.BoundaryAt <= next.BoundaryAt ? current : next;
        });
    }

    /// <summary>
    /// 録画worker起動が完了した後、短いウォームアップを過ぎたら通常EPGだけを空き録画チューナーへ戻す。
    /// 予約録画のdue/preempt/cooldownを弱めるものではなく、安定録画中の過剰BLOCKEDを避けるための復帰条件。
    /// </summary>
    public static void AllowStableRecordingEpg(string group, DateTime notBefore, DateTime until, string owner, string label)
    {
        group = NormalizeGroup(group);
        var next = new StableRecordingAllowance(notBefore, until, Safe(owner), Safe(label));
        StableRecordingEpgAllowances.AddOrUpdate(group, next, (_, current) => current.Until >= until && current.NotBefore <= notBefore ? current : next);
    }

    public static void SuppressEpg(string group, DateTime until, string reason, string owner, string label)
    {
        group = NormalizeGroup(group);
        var next = new Suppression(until, reason, owner, label);
        EpgSuppressions.AddOrUpdate(group, next, (_, current) => current.Until >= until ? current : next);
    }

    /// <summary>
    /// 抑止中かどうかを返す。ログ出力判断は ShouldLogEpgSuppression() に分離し、
    /// TS単位巡回で同じ抑止理由が大量に通常ログへ流れることを防ぐ。
    /// </summary>
    public static bool IsEpgSuppressed(string group, out DateTime until, out string reason, out string owner, out string label)
    {
        group = NormalizeGroup(group);
        until = default;
        reason = string.Empty;
        owner = string.Empty;
        label = string.Empty;

        if (!EpgSuppressions.TryGetValue(group, out var s))
            return false;

        var now = DateTime.Now;
        if (s.Until <= now)
        {
            EpgSuppressions.TryRemove(group, out _);
            return false;
        }

        until = s.Until;
        reason = s.Reason;
        owner = s.Owner;
        label = s.Label;
        return true;
    }


    /// <summary>
    /// 通常EPGの新規起動抑止判定。録画開始待ち/EPGプリエンプト中は抑止するが、
    /// 録画worker起動後のウォームアップを過ぎた安定録画中は、空き録画チューナー利用を許可する。
    /// 録画前EPG確認はこの緩和を使わず IsEpgSuppressed を見る。
    /// </summary>
    public static bool IsNormalEpgSuppressed(string group, out DateTime until, out string reason, out string owner, out string label)
        => IsNormalEpgSuppressed(group, null, out until, out reason, out owner, out label);

    public static bool IsNormalEpgSuppressed(string group, DateTime? proposedWorkerEnd, out DateTime until, out string reason, out string owner, out string label)
    {
        group = NormalizeGroup(group);
        var now = DateTime.Now;
        if (IsTimelineBoundaryBlocking(group, proposedWorkerEnd, out until, out reason, out owner, out label))
            return true;

        if (!IsEpgSuppressed(group, out until, out reason, out owner, out label))
            return false;

        if (IsStableRecordingAllowanceActive(group, out var notBefore, out _, out var allowanceOwner, out var allowanceLabel))
        {
            // 安定録画中の空き録画チューナーEPGは許可する。
            // ただし上の timeline boundary はこの緩和より上位で、次録画/handoffへ食い込むworkerは通さない。
            if (now >= notBefore)
            {
                reason = "recording_stable_free_tuner_allowed";
                owner = allowanceOwner;
                label = allowanceLabel;
                return false;
            }

            reason = "recording_warmup";
            until = notBefore < until ? notBefore : until;
            owner = allowanceOwner;
            label = allowanceLabel;
            return true;
        }

        return true;
    }

    private static bool IsTimelineBoundaryBlocking(string group, DateTime? proposedWorkerEnd, out DateTime until, out string reason, out string owner, out string label)
    {
        until = default;
        reason = string.Empty;
        owner = string.Empty;
        label = string.Empty;

        if (!RecordingTimelineBoundaries.TryGetValue(group, out var boundary))
            return false;

        var now = DateTime.Now;
        if (boundary.ProtectUntil <= now || boundary.BoundaryAt <= now)
        {
            RecordingTimelineBoundaries.TryRemove(group, out _);
            return false;
        }

        // plannedEnd が取れるworker投入前は、次録画の安全窓に入った時点で新規workerを止める。
        // 以前は proposedWorkerEnd が境界へ直接食い込む場合だけ止めていたため、
        // 12:15録画に対して12:09台の新規worker投入を許していた。
        var blockAt = proposedWorkerEnd.HasValue
            ? now >= boundary.BlockNewWorkerAfter || proposedWorkerEnd.Value >= boundary.BoundaryAt.AddSeconds(-10)
            : now >= boundary.BlockNewWorkerAfter;
        if (!blockAt)
            return false;

        until = boundary.ProtectUntil;
        reason = string.IsNullOrWhiteSpace(boundary.Reason) ? "recording_timeline_due" : boundary.Reason;
        owner = boundary.Owner;
        label = boundary.Label;
        return true;
    }

    private static bool IsStableRecordingAllowanceActive(string group, out DateTime notBefore, out DateTime until, out string owner, out string label)
    {
        group = NormalizeGroup(group);
        notBefore = default;
        until = default;
        owner = string.Empty;
        label = string.Empty;

        if (!StableRecordingEpgAllowances.TryGetValue(group, out var allowance))
            return false;

        var now = DateTime.Now;
        if (allowance.Until <= now)
        {
            StableRecordingEpgAllowances.TryRemove(group, out _);
            return false;
        }

        notBefore = allowance.NotBefore;
        until = allowance.Until;
        owner = allowance.Owner;
        label = allowance.Label;
        return true;
    }


    /// <summary>
    /// 同一放送波・同一抑止期限・同一理由・同一所有者の抑止ログを1回だけ出す。
    /// 録画優先の判断自体は維持し、ログだけを集約する。
    /// </summary>
    public static bool ShouldLogEpgSuppression(string group, DateTime until, string reason, string owner)
    {
        group = NormalizeGroup(group);
        var next = new SuppressionLogState(until, Safe(reason), Safe(owner));

        while (true)
        {
            if (!EpgSuppressionLogStates.TryGetValue(group, out var current))
            {
                if (EpgSuppressionLogStates.TryAdd(group, next)) return true;
                continue;
            }

            if (current.Until == next.Until &&
                string.Equals(current.Reason, next.Reason, StringComparison.Ordinal) &&
                string.Equals(current.Owner, next.Owner, StringComparison.Ordinal))
            {
                return false;
            }

            if (EpgSuppressionLogStates.TryUpdate(group, next, current)) return true;
        }
    }

    public static string FormatSuppression(string group)
    {
        return IsEpgSuppressed(group, out var until, out var reason, out var owner, out var label)
            ? $"suppressed=True group={NormalizeGroup(group)} until={until:MM/dd HH:mm:ss} owner={Safe(owner)} reason={Safe(reason)} label={Safe(label)}"
            : $"suppressed=False group={NormalizeGroup(group)}";
    }

    private static string NormalizeGroup(string? group)
        => string.IsNullOrWhiteSpace(group) ? "-" : group.Trim().ToUpperInvariant();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace('\r', ' ').Replace('\n', ' ');
}
