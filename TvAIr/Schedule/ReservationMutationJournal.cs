using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>
/// 予約DB mutation の確定差分。Pluginイベント形式をStoreへ持ち込まず、
/// Application/Projection層が外部イベントへ変換するための正規結果。
/// </summary>
public enum ReservationMutationKind
{
    Added,
    Updated,
    Removed,
    Enabled,
    Disabled,
    ConflictChanged,
    RecordingStarted,
    RecordingCompleted,
    RecordingFailed
}

public sealed record ReservationMutationResult(
    ReservationMutationKind Kind,
    Reservation? Before,
    Reservation? After,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyDictionary<string, string?> Metadata)
{
    public int ReservationId => (After ?? Before)?.Id ?? 0;
}

/// <summary>
/// Storeが確定した予約差分だけを記録するdomain journal。
/// 外部イベントへの投影責務は購読側に置く。
/// </summary>
public static class ReservationMutationWakeImpact
{
    public static bool AffectsWakePlan(ReservationMutationResult mutation)
    {
        if (mutation.Kind is ReservationMutationKind.Added
            or ReservationMutationKind.Removed
            or ReservationMutationKind.Enabled
            or ReservationMutationKind.Disabled
            or ReservationMutationKind.ConflictChanged
            or ReservationMutationKind.RecordingStarted
            or ReservationMutationKind.RecordingCompleted
            or ReservationMutationKind.RecordingFailed)
            return true;

        if (mutation.Kind != ReservationMutationKind.Updated) return false;
        return mutation.ChangedFields.Any(field => field is
            nameof(Reservation.StartTime)
            or nameof(Reservation.EndTime)
            or nameof(Reservation.Status)
            or nameof(Reservation.IsEnabled)
            or nameof(Reservation.IsConflicted)
            or nameof(Reservation.TunerName)
            or nameof(Reservation.ActualTunerName)
            or nameof(Reservation.IsUserChain)
            or nameof(Reservation.UserChainPreviousId)
            or nameof(Reservation.UserChainRootId));
    }
}

public sealed record ReservationWakeRefreshBatchResult(
    string Source,
    string Action,
    int WakeRelevantMutationCount,
    long WakePlanVersion,
    bool QueueWakeFallbackWhenCompleted);

public sealed class ReservationMutationJournal
{
    private sealed class WakeRefreshBatchState
    {
        public required string Source { get; init; }
        public required string Action { get; init; }
        public int Depth { get; set; }
        public int WakeRelevantMutationCount { get; set; }
        public long WakePlanVersion { get; set; }
        public bool QueueWakeFallbackWhenCompleted { get; set; }
    }

    private sealed class WakeRefreshBatchScope : IDisposable
    {
        private readonly ReservationMutationJournal owner;
        private readonly WakeRefreshBatchState state;
        private int disposed;

        public WakeRefreshBatchScope(ReservationMutationJournal owner, WakeRefreshBatchState state)
        {
            this.owner = owner;
            this.state = state;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            owner.EndWakeRefreshBatch(state);
        }
    }

    private readonly AsyncLocal<WakeRefreshBatchState?> wakeRefreshBatch = new();
    private long wakePlanVersion;
    private long wakePlanClaimedVersion;
    private long wakePlanHandledVersion;

    public event Action<ReservationMutationResult>? Recorded;
    public event Action<ReservationWakeRefreshBatchResult>? WakeRefreshBatchCompleted;

    /// <summary>Wake計画へ影響する確定Mutationの単調増加version。</summary>
    public long WakePlanVersion => Interlocked.Read(ref wakePlanVersion);

    /// <summary>
    /// 共通割当・録画前EPG・Wakeの最終投影まで処理済みのMutation世代。
    /// Mutation投影側は、この世代を越えて未処理の差分だけを補完対象にする。
    /// </summary>
    public long WakePlanClaimedVersion => Interlocked.Read(ref wakePlanClaimedVersion);
    public long WakePlanHandledVersion => Interlocked.Read(ref wakePlanHandledVersion);

    public void MarkWakePlanClaimed(long version)
    {
        while (true)
        {
            var current = Interlocked.Read(ref wakePlanClaimedVersion);
            if (version <= current) return;
            if (Interlocked.CompareExchange(ref wakePlanClaimedVersion, version, current) == current) return;
        }
    }

    public bool IsWakePlanClaimed(long version)
        => version <= Interlocked.Read(ref wakePlanClaimedVersion);

    public void MarkWakePlanHandled(long version)
    {
        while (true)
        {
            var current = Interlocked.Read(ref wakePlanHandledVersion);
            if (version <= current) return;
            if (Interlocked.CompareExchange(ref wakePlanHandledVersion, version, current) == current) return;
        }
    }

    public bool IsWakePlanHandled(long version)
        => version <= Interlocked.Read(ref wakePlanHandledVersion);

    public bool IsWakeRefreshBatchActive => wakeRefreshBatch.Value is not null;

    /// <summary>現在のbatch自身が記録した最後のWake影響Mutation世代。batch外の同時Mutationは含めない。</summary>
    public long CurrentWakeRefreshBatchVersion => wakeRefreshBatch.Value?.WakePlanVersion ?? 0;

    /// <summary>
    /// 同一の割当commitから派生する多数の予約Mutationについて、Wake影響件数をbatch単位で集約する。
    /// 共通割当が処理予定世代を取得するため、Mutation側は未処理時の補完だけを1回要求する。
    /// Mutation自体とPlugin/UI通知の順序は維持する。
    /// </summary>
    public IDisposable BeginWakeRefreshBatch(
        string source,
        string action,
        bool queueWakeFallbackWhenCompleted = true)
    {
        var state = wakeRefreshBatch.Value;
        if (state is null)
        {
            state = new WakeRefreshBatchState
            {
                Source = source,
                Action = action,
                Depth = 1,
                QueueWakeFallbackWhenCompleted = queueWakeFallbackWhenCompleted
            };
            wakeRefreshBatch.Value = state;
        }
        else
        {
            state.Depth++;
            state.QueueWakeFallbackWhenCompleted |= queueWakeFallbackWhenCompleted;
        }

        return new WakeRefreshBatchScope(this, state);
    }

    private void EndWakeRefreshBatch(WakeRefreshBatchState state)
    {
        if (!ReferenceEquals(wakeRefreshBatch.Value, state)) return;
        state.Depth--;
        if (state.Depth > 0) return;

        wakeRefreshBatch.Value = null;
        if (state.WakeRelevantMutationCount > 0)
        {
            WakeRefreshBatchCompleted?.Invoke(new ReservationWakeRefreshBatchResult(
                state.Source,
                state.Action,
                state.WakeRelevantMutationCount,
                state.WakePlanVersion,
                state.QueueWakeFallbackWhenCompleted));
        }
    }

    public void Record(
        ReservationMutationKind kind,
        Reservation? before,
        Reservation? after,
        params string[] changedFields)
        => Record(kind, before, after, null, changedFields);

    public void Record(
        ReservationMutationKind kind,
        Reservation? before,
        Reservation? after,
        IReadOnlyDictionary<string, string?>? metadata,
        params string[] changedFields)
    {
        if (before is null && after is null) return;
        var mutation = new ReservationMutationResult(
            kind,
            before,
            after,
            changedFields.Length == 0 ? Array.Empty<string>() : changedFields,
            metadata ?? new Dictionary<string, string?>());
        if (ReservationMutationWakeImpact.AffectsWakePlan(mutation))
        {
            var version = Interlocked.Increment(ref wakePlanVersion);
            var batch = wakeRefreshBatch.Value;
            if (batch is not null)
            {
                batch.WakeRelevantMutationCount++;
                batch.WakePlanVersion = Math.Max(batch.WakePlanVersion, version);
            }
        }
        Recorded?.Invoke(mutation);
    }
}
