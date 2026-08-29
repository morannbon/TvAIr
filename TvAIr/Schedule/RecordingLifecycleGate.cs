using System.Collections.Concurrent;


namespace TvAIr.Schedule;

/// <summary>
/// 録画前EPG確認が本番録画の安全境界へ食い込まないための共通timeline gate。
/// 通常／定時EPGは開始前Plannerまたは開始Admissionで録画占有を判断し、開始後はwaveを固定するため、
/// 録画都合のnormal EPG suppression/preemptはここでは扱わない。
/// </summary>
public static class RecordingLifecycleGate
{
    private sealed record SuppressionLogState(DateTime Until, string Reason, string Owner);

    private sealed record RecordingTimelineBoundary(DateTime BoundaryAt, DateTime BlockNewWorkerAfter, DateTime ProtectUntil, string Reason, string Owner, string Label);

    private static readonly ConcurrentDictionary<string, SuppressionLogState> PreRecordEpgSuppressionLogStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, RecordingTimelineBoundary> RecordingTimelineBoundaries = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// 現在の予約一覧から算出した最短録画境界を正本として置換する。
    /// 予約取消・無効化・時刻変更で古い境界を保持しないため、監視側の再計算結果にだけ使用する。
    /// </summary>
    public static void ReplaceRecordingTimelineBoundary(string group, DateTime boundaryAt, DateTime protectUntil, string reason, string owner, string label, DateTime? blockNewWorkerAfter = null)
    {
        group = NormalizeGroup(group);
        RecordingTimelineBoundaries[group] = new RecordingTimelineBoundary(
            boundaryAt,
            blockNewWorkerAfter ?? boundaryAt.AddSeconds(-30),
            protectUntil,
            Safe(reason),
            Safe(owner),
            Safe(label));
    }

    public static void ClearRecordingTimelineBoundary(string group)
    {
        group = NormalizeGroup(group);
        RecordingTimelineBoundaries.TryRemove(group, out _);
    }

    /// <summary>
    /// 録画前EPG確認専用の新規worker抑止判定。
    /// 確認worker自身の終了予定が最短録画境界の安全領域へ食い込む場合だけtimelineで抑止する。
    /// 親予約自身の境界を理由に通常EPGを制御する用途には使用しない。物理競合はTunerPool leaseが排除する。
    /// </summary>
    public static bool IsPreRecordEpgSuppressed(string group, DateTime proposedWorkerEnd, out DateTime until, out string reason, out string owner, out string label)
    {
        group = NormalizeGroup(group);

        // PRE_RECORD_EPG_RUNTIME_TUNER_INVARIANT:
        // 録画前EPG確認は物理Tunerを将来固定しない。実際の録画境界へworker終了予定が
        // 食い込む場合だけtimelineで抑止し、物理競合はTunerPool leaseを正本とする。
        if (IsPreRecordTimelineBoundaryBlocking(group, proposedWorkerEnd, out until, out reason, out owner, out label))
            return true;

        until = default;
        reason = string.Empty;
        owner = string.Empty;
        label = string.Empty;
        return false;
    }

    private static bool IsPreRecordTimelineBoundaryBlocking(string group, DateTime proposedWorkerEnd, out DateTime until, out string reason, out string owner, out string label)
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

        // 録画開始処理が使う最終10秒の安全領域へ、確認workerの予定終了が到達する場合だけ止める。
        // RunDuePreRecordEpgEntriesAsync側も30秒前までにcancelするが、共通ゲート側でも独立に守る。
        if (proposedWorkerEnd < boundary.BoundaryAt.AddSeconds(-10))
            return false;

        until = boundary.ProtectUntil;
        reason = string.IsNullOrWhiteSpace(boundary.Reason) ? "recording_timeline_due" : boundary.Reason;
        owner = boundary.Owner;
        label = boundary.Label;
        return true;
    }

    /// <summary>
    /// 同一放送波・同一抑止期限・同一理由・同一所有者のPreRec抑止ログを1回だけ出す。
    /// </summary>
    public static bool ShouldLogPreRecordEpgSuppression(string group, DateTime until, string reason, string owner)
    {
        group = NormalizeGroup(group);
        var next = new SuppressionLogState(until, Safe(reason), Safe(owner));

        while (true)
        {
            if (!PreRecordEpgSuppressionLogStates.TryGetValue(group, out var current))
            {
                if (PreRecordEpgSuppressionLogStates.TryAdd(group, next)) return true;
                continue;
            }

            if (current.Until == next.Until &&
                string.Equals(current.Reason, next.Reason, StringComparison.Ordinal) &&
                string.Equals(current.Owner, next.Owner, StringComparison.Ordinal))
            {
                return false;
            }

            if (PreRecordEpgSuppressionLogStates.TryUpdate(group, next, current)) return true;
        }
    }

    private static string NormalizeGroup(string? group)
        => string.IsNullOrWhiteSpace(group) ? "-" : group.Trim().ToUpperInvariant();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace('\r', ' ').Replace('\n', ' ');
}
