using TvAIr.Core;
using TvAIr.Epg;

namespace TvAIr.Schedule;

/// <summary>
/// ReservationStore が確定した domain mutation からユーザー運用ログを投影し、
/// 共通割当を通らなかった未処理MutationだけにWake補完要求を発行する。
/// 共通割当を通る変更のWake所有者は ReservationAllocationRouteService に固定する。
/// </summary>
public sealed class ReservationMutationSideEffectProjection : IDisposable
{
    private readonly ReservationMutationJournal journal;
    private readonly UserEventLogService userEvents;
    private readonly TaskSchedulerService taskScheduler;
    private readonly EpgScheduler epgScheduler;
    private readonly LogRepository log;
    private readonly object wakeFallbackGate = new();
    private CancellationTokenSource? wakeFallbackCts;
    private long pendingWakeVersion;
    private string pendingWakeSource = string.Empty;
    private string pendingWakeAction = string.Empty;
    private int disposed;

    public ReservationMutationSideEffectProjection(
        ReservationMutationJournal journal,
        UserEventLogService userEvents,
        TaskSchedulerService taskScheduler,
        EpgScheduler epgScheduler,
        LogRepository log)
    {
        this.journal = journal;
        this.userEvents = userEvents;
        this.taskScheduler = taskScheduler;
        this.epgScheduler = epgScheduler;
        this.log = log;
        journal.Recorded += OnRecorded;
        journal.WakeRefreshBatchCompleted += OnWakeRefreshBatchCompleted;
        taskScheduler.WakeRefreshCompleted += OnWakeRefreshCompleted;
    }

    private void OnRecorded(ReservationMutationResult mutation)
    {
        try
        {
            ProjectUserEvent(mutation);
        }
        catch (Exception ex)
        {
            log.Add("RESERVATION_MUTATION_PROJECTION", $"R{mutation.ReservationId}",
                $"result=USER_EVENT_FAILED kind={mutation.Kind} error={ex.Message} rule=release_contract");
        }

        if (!ReservationMutationWakeImpact.AffectsWakePlan(mutation)) return;

        if (AffectsScheduledEpgPlan(mutation))
        {
            // Reservation timelineの確定をDaily Plannerへ先に通知する。
            // Wake再構築完了をPlanner起動条件にしてはならない。Planner commit後のProjection/Wakeが
            // 新しいPlannedStartを外部へ収束させる。
            epgScheduler.NotifyReservationTimelineChanged(
                journal.WakePlanVersion,
                "ReservationMutation",
                $"{mutation.Kind}:R{mutation.ReservationId}");
        }

        if (journal.IsWakeRefreshBatchActive) return;
        QueueWakeFallback(
            journal.WakePlanVersion,
            "ReservationMutationFallback",
            $"{mutation.Kind}:R{mutation.ReservationId}");
    }


    private void OnWakeRefreshBatchCompleted(ReservationWakeRefreshBatchResult batch)
    {
        // batch自身が生成した世代だけを扱う。global WakePlanVersionを読むと、
        // 同時発生した別Mutationまでこのbatchの所有物としてclaim/handledしてしまう。
        var version = batch.WakePlanVersion;
        if (version <= 0) return;
        if (!batch.QueueWakeFallbackWhenCompleted)
        {
            // ALLOCATION_WAKE_SUPPRESSION_INVARIANT:
            // RefreshWakeTask=false の共通Allocation Routeが生成したMutationは、
            // ReservationMutation fallbackからWake要求へ昇格させない。
            // Daily Plannerはmutation確定時点ですでに独立してinvalidateされている。
            log.Add("RESERVATION_MUTATION_PROJECTION", "WakeBatch",
                $"result=FALLBACK_SUPPRESSED source={batch.Source} action={batch.Action} version={version} wakeRelevantMutations={batch.WakeRelevantMutationCount} reason=allocation_route_owns_or_suppresses_wake rule=allocation_mutation_wake_owner_contract");
            return;
        }

        QueueWakeFallback(
            version,
            "ReservationMutationFallback",
            $"{batch.Source}:{batch.Action}:batch={batch.WakeRelevantMutationCount}");
        log.Add("RESERVATION_MUTATION_PROJECTION", "WakeBatch",
            $"result=FALLBACK_QUEUED source={batch.Source} action={batch.Action} wakeRelevantMutations={batch.WakeRelevantMutationCount} owner=allocation_route_unless_unhandled rule=allocation_mutation_wake_owner_contract");
    }


    private void OnWakeRefreshCompleted(long version, string reason)
    {
        if (version <= 0) return;

        journal.MarkWakePlanHandled(version);
        log.Add("RESERVATION_MUTATION_PROJECTION", "WakeOwner",
            $"result=HANDLED_AFTER_REBUILD version={version} handledVersion={journal.WakePlanHandledVersion} reason={reason} owner=task_scheduler_deferred_wake rule=allocation_mutation_wake_owner_contract");
    }

    private static bool AffectsScheduledEpgPlan(ReservationMutationResult mutation)
    {
        var reservation = mutation.After ?? mutation.Before;
        if (reservation is null || reservation.Source == ReservationSource.Epg) return false;

        return mutation.Kind is ReservationMutationKind.Added
            or ReservationMutationKind.Removed
            or ReservationMutationKind.Enabled
            or ReservationMutationKind.Disabled
            or ReservationMutationKind.ConflictChanged
            or ReservationMutationKind.RecordingStarted
            or ReservationMutationKind.RecordingCompleted
            or ReservationMutationKind.RecordingFailed
            || (mutation.Kind == ReservationMutationKind.Updated
                && mutation.ChangedFields.Any(field => field is
                    nameof(Reservation.StartTime)
                    or nameof(Reservation.EndTime)
                    or nameof(Reservation.Status)
                    or nameof(Reservation.IsEnabled)
                    or nameof(Reservation.IsConflicted)));
    }

    private void QueueWakeFallback(long version, string source, string action)
    {
        CancellationTokenSource cts;
        lock (wakeFallbackGate)
        {
            if (version > pendingWakeVersion) pendingWakeVersion = version;
            pendingWakeSource = source;
            pendingWakeAction = action;
            wakeFallbackCts?.Cancel();
            cts = wakeFallbackCts = new CancellationTokenSource();
        }

        _ = RunWakeFallbackAsync(cts);
    }

    private async Task RunWakeFallbackAsync(CancellationTokenSource ownerCts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ownerCts.Token).ConfigureAwait(false);

            long version;
            string source;
            string action;
            lock (wakeFallbackGate)
            {
                if (!ReferenceEquals(wakeFallbackCts, ownerCts)) return;
                version = pendingWakeVersion;
                source = pendingWakeSource;
                action = pendingWakeAction;
            }

            if (journal.IsWakePlanHandled(version))
            {
                log.Add("RESERVATION_MUTATION_PROJECTION", "WakeFallback",
                    $"result=SKIPPED version={version} claimedVersion={journal.WakePlanClaimedVersion} handledVersion={journal.WakePlanHandledVersion} owner=allocation_route rule=allocation_mutation_wake_owner_contract");
                return;
            }

            // 共通割当がこの世代を取得済みなら、直列派生キューの完了を待つ。
            // APIスレッドへWake I/Oを戻さず、かつキュー待ち中の二重Wakeを禁止する。
            for (var wait = 0; wait < 30 && journal.IsWakePlanClaimed(version) && !journal.IsWakePlanHandled(version); wait++)
                await Task.Delay(TimeSpan.FromSeconds(1), ownerCts.Token).ConfigureAwait(false);

            if (journal.IsWakePlanHandled(version))
            {
                log.Add("RESERVATION_MUTATION_PROJECTION", "WakeFallback",
                    $"result=SKIPPED_AFTER_CLAIM_WAIT version={version} claimedVersion={journal.WakePlanClaimedVersion} handledVersion={journal.WakePlanHandledVersion} owner=allocation_route rule=allocation_mutation_wake_owner_contract");
                return;
            }

            taskScheduler.RequestWakeTaskRefreshSoon(source, action, TimeSpan.FromSeconds(1));
            journal.MarkWakePlanHandled(version);
            log.Add("RESERVATION_MUTATION_PROJECTION", "WakeFallback",
                $"result=APPLIED version={version} handledVersion={journal.WakePlanHandledVersion} reason=no_allocation_route_consumed_mutation rule=allocation_mutation_wake_owner_contract");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Add("RESERVATION_MUTATION_PROJECTION", "WakeFallback",
                $"result=FAILED error={ex.Message} rule=allocation_mutation_wake_owner_contract");
        }
        finally
        {
            lock (wakeFallbackGate)
            {
                if (ReferenceEquals(wakeFallbackCts, ownerCts))
                    wakeFallbackCts = null;
            }

            // Each fallback task owns exactly one CTS. A newer mutation may replace the shared
            // reference before this task exits, but ownership of this CTS does not transfer.
            ownerCts.Dispose();
        }
    }

    private void ProjectUserEvent(ReservationMutationResult mutation)
    {
        var before = mutation.Before;
        var after = mutation.After;
        var id = mutation.ReservationId;

        if (mutation.Metadata.ContainsKey("suppressUserEvent"))
            return;

        if (mutation.Metadata.TryGetValue("userEvent", out var special)
            && string.Equals(special, "recording_interrupted", StringComparison.Ordinal))
        {
            var createdAt = mutation.Metadata.TryGetValue("finishedAt", out var rawAt)
                && DateTime.TryParse(rawAt, out var parsedAt)
                    ? parsedAt
                    : after?.RecordingFinishedAt ?? after?.UpdatedAt ?? before?.UpdatedAt ?? DateTime.Now;
            mutation.Metadata.TryGetValue("reason", out var reason);
            mutation.Metadata.TryGetValue("fileEvidence", out var fileEvidence);
            mutation.Metadata.TryGetValue("trigger", out var trigger);
            userEvents.AddRecordingInterrupted(before, id, reason, fileEvidence, createdAt, trigger ?? "InterruptedRecordingRecovery");
            return;
        }

        switch (mutation.Kind)
        {
            case ReservationMutationKind.Added when after is not null:
                // RESERVATION_USER_EVENT_TIMESTAMP_SINGLE_SOURCE:
                // Creation time is committed with the reservation row; the projection must not mint a later wall-clock time.
                userEvents.AddReservationAdded(after, id, after.CreatedAt);
                break;
            case ReservationMutationKind.Removed when before is not null:
                userEvents.AddReservationDeleted(before, id);
                break;
            case ReservationMutationKind.Enabled when before is not null:
                userEvents.AddReservationEnabledChanged(before, id, true, after?.UpdatedAt);
                break;
            case ReservationMutationKind.Disabled when before is not null:
                userEvents.AddReservationEnabledChanged(before, id, false, after?.UpdatedAt);
                break;
            case ReservationMutationKind.RecordingStarted:
                // RECORDING_USER_EVENT_LIFECYCLE_SINGLE_SOURCE:
                // User-event timestamps are a projection of the committed reservation lifecycle.
                // Do not create a later DateTime.Now in the side-effect layer.
                userEvents.AddReservationStatusChanged(
                    before,
                    id,
                    ReservationStatus.Recording,
                    after?.RecordingStartedAt);
                break;
            case ReservationMutationKind.RecordingCompleted:
                userEvents.AddReservationStatusChanged(
                    before,
                    id,
                    ReservationStatus.Completed,
                    after?.RecordingFinishedAt);
                break;
            case ReservationMutationKind.RecordingFailed:
                mutation.Metadata.TryGetValue("reason", out var canonicalFailureReason);
                userEvents.AddReservationStatusChanged(
                    before,
                    id,
                    ReservationStatus.Failed,
                    after?.RecordingFinishedAt,
                    canonicalFailureReason);
                break;
            case ReservationMutationKind.Updated when before is not null && after is not null && before.Status != after.Status:
                userEvents.AddReservationStatusChanged(
                    before,
                    id,
                    after.Status,
                    after.Status == ReservationStatus.Cancelled && before.Status == ReservationStatus.Stopping
                        ? after.RecordingFinishedAt ?? after.UpdatedAt
                        : after.UpdatedAt);
                break;
        }
    }


    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        journal.Recorded -= OnRecorded;
        journal.WakeRefreshBatchCompleted -= OnWakeRefreshBatchCompleted;
        taskScheduler.WakeRefreshCompleted -= OnWakeRefreshCompleted;
        lock (wakeFallbackGate)
        {
            wakeFallbackCts?.Cancel();
            wakeFallbackCts = null;
        }
    }
}
