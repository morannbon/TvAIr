using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Schedule;

namespace TvAIr.Tuner;

public enum TunerOwnershipReconcileKind
{
    Startup,
    Periodic,
    PowerSuspend,
    PowerResume,
    PowerResumeVerification
}

public sealed record TunerOwnershipReconcileContext(
    string CycleId,
    TunerOwnershipReconcileKind Kind,
    DateTime StartedAt)
{
    public string Source => Kind.ToString();
}

public sealed record TunerOwnershipReconcileResult(
    string Owner,
    int Checked,
    int Kept,
    int Finalized,
    int Unavailable,
    int Failed,
    string Detail);

public sealed class TunerOwnershipReconciliationCoordinator
{
    private readonly ReservationScheduler _recording;
    private readonly EpgCapture _epg;
    private readonly ExternalTunerLeaseService _viewer;
    private readonly TunerPool _tunerPool;
    private readonly LogRepository _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private Task? _activeReconcileTask;
    private bool _stopping;
    private static readonly TimeSpan StablePeriodicLogInterval = TimeSpan.FromMinutes(10);
    private string? _lastPeriodicReconcileSignature;
    private DateTime _lastPeriodicReconcileLogAt = DateTime.MinValue;
    private string? _lastPeriodicOwnershipMatrixSignature;
    private DateTime _lastPeriodicOwnershipMatrixLogAt = DateTime.MinValue;

    public TunerOwnershipReconciliationCoordinator(
        ReservationScheduler recording,
        EpgCapture epg,
        ExternalTunerLeaseService viewer,
        TunerPool tunerPool,
        LogRepository log)
    {
        _recording = recording;
        _epg = epg;
        _viewer = viewer;
        _tunerPool = tunerPool;
        _log = log;
    }

    public Task<IReadOnlyList<TunerOwnershipReconcileResult>> ReconcileAsync(
        TunerOwnershipReconcileContext context,
        CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_stopping)
                return Task.FromCanceled<IReadOnlyList<TunerOwnershipReconcileResult>>(
                    cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));

            var task = ReconcileCoreAsync(context, cancellationToken);
            _activeReconcileTask = task;
            return task;
        }
    }

    private async Task<IReadOnlyList<TunerOwnershipReconcileResult>> ReconcileCoreAsync(
        TunerOwnershipReconcileContext context,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<TunerOwnershipReconcileResult>(3);
            results.Add(await _recording.ReconcileOwnedProcessesAfterResumeAsync(context, cancellationToken).ConfigureAwait(false));
            results.Add(await _epg.ReconcileOwnedWorkersAfterResumeAsync(context, cancellationToken).ConfigureAwait(false));

            var viewer = _viewer.ReconcileProcessState($"{context.Source}:{context.CycleId}", context.Kind == TunerOwnershipReconcileKind.Periodic);
            results.Add(new TunerOwnershipReconcileResult(
                "Viewer",
                viewer.Inspected,
                viewer.KeptAlive,
                viewer.Released,
                viewer.PendingWithoutProcess + viewer.StalePoolLease,
                0,
                $"pendingWithoutProcess={viewer.PendingWithoutProcess} stalePoolLease={viewer.StalePoolLease}"));

            LogOwnershipMatrixPreAudit(context);

            var checkedCount = results.Sum(x => x.Checked);
            var finalizedCount = results.Sum(x => x.Finalized);
            var unavailableCount = results.Sum(x => x.Unavailable);
            var failedCount = results.Sum(x => x.Failed);
            var tunerState = _tunerPool.GetStatusSummary();
            var reconcileSignature =
                $"owners={results.Count}|checked={checkedCount}|finalized={finalizedCount}|" +
                $"unavailable={unavailableCount}|failed={failedCount}|tunerState={tunerState}";
            if (ShouldLogPeriodicStableState(
                    context,
                    reconcileSignature,
                    hasFinding: finalizedCount > 0 || unavailableCount > 0 || failedCount > 0,
                    ref _lastPeriodicReconcileSignature,
                    ref _lastPeriodicReconcileLogAt))
            {
                _log.Add("TUNER_RECONCILE", context.CycleId,
                    $"result=COMPLETED source={context.Source} owners={results.Count} " +
                    $"checked={checkedCount} finalized={finalizedCount} " +
                    $"unavailable={unavailableCount} failed={failedCount} " +
                    $"tunerState={tunerState} rule=release_contract");
            }
            return results;
        }
        finally
        {
            _gate.Release();
            lock (_lifecycleGate)
            {
                if (_activeReconcileTask?.IsCompleted != false)
                    _activeReconcileTask = null;
            }
        }
    }


    private void LogOwnershipMatrixPreAudit(TunerOwnershipReconcileContext context)
    {
        var pool = _tunerPool.GetStatus().Where(slot => slot.UsageKind != TunerUsageKind.Free).ToList();
        var recording = _recording.GetActiveRecordingSessions();
        var epg = _epg.GetActiveEpgWorkerSnapshots();
        var viewer = _viewer.GetActiveLeases();

        var owners = new List<OwnershipProjection>(recording.Count + epg.Count + viewer.Count);
        owners.AddRange(recording.Select(x => new OwnershipProjection(
            "Recording",
            x.PoolLeaseId,
            x.OccupancyGeneration,
            x.PoolLeaseCurrent,
            TunerUsageKind.Recording,
            x.ReservationId,
            x.ProcessId,
            x.TunerName,
            x.Did,
            x.BonDriverFileName,
            x.OperationId.ToString("N"))));
        owners.AddRange(epg.Where(x => x.PoolLeaseId.HasValue).Select(x => new OwnershipProjection(
            "EPG",
            x.PoolLeaseId!.Value,
            x.OccupancyGeneration,
            x.PoolLeaseCurrent,
            TunerUsageKind.Epg,
            null,
            x.ProcessId > 0 ? x.ProcessId : null,
            x.TunerName,
            x.Did,
            x.BonDriverFileName,
            x.RunId)));
        owners.AddRange(viewer.Where(x => x.PoolLeaseId.HasValue).Select(x => new OwnershipProjection(
            "Viewer",
            x.PoolLeaseId!.Value,
            x.OccupancyGeneration,
            x.PoolLeaseCurrent,
            TunerUsageKind.Viewing,
            null,
            x.ProcessId,
            x.TunerName,
            x.Did,
            x.BonDriverFileName,
            x.LeaseId)));

        var findings = new List<OwnershipMatrixFinding>();
        findings.AddRange(epg.Where(x => !x.PoolLeaseId.HasValue).Select(x => new OwnershipMatrixFinding(
            "UNKNOWN", x.TunerName, null, "EPG", $"runId={SafeValue(x.RunId)} owner_has_no_pool_lease_identity")));
        findings.AddRange(viewer.Where(x => !x.PoolLeaseId.HasValue).Select(x => new OwnershipMatrixFinding(
            "UNKNOWN", x.TunerName, null, "Viewer", $"externalLeaseId={SafeValue(x.LeaseId)} owner_has_no_pool_lease_identity")));

        var ownersByLease = owners.GroupBy(x => x.PoolLeaseId).ToDictionary(x => x.Key, x => x.ToList());
        var poolLeaseIds = pool.Where(x => x.PoolLeaseId.HasValue).Select(x => x.PoolLeaseId!.Value).ToHashSet();

        foreach (var slot in pool)
        {
            if (!slot.PoolLeaseId.HasValue)
            {
                findings.Add(new OwnershipMatrixFinding("UNKNOWN", slot.Name, null, "Pool", "active_slot_without_pool_lease_id"));
                continue;
            }

            var leaseId = slot.PoolLeaseId.Value;
            if (!ownersByLease.TryGetValue(leaseId, out var matchedOwners) || matchedOwners.Count == 0)
            {
                findings.Add(new OwnershipMatrixFinding("POOL_ONLY", slot.Name, leaseId, "Pool", "no_owner_claim"));
                continue;
            }

            if (matchedOwners.Count > 1)
            {
                findings.Add(new OwnershipMatrixFinding(
                    "MULTIPLE_OWNERS",
                    slot.Name,
                    leaseId,
                    string.Join("+", matchedOwners.Select(x => x.OwnerKind).Distinct(StringComparer.OrdinalIgnoreCase)),
                    $"claims={matchedOwners.Count}"));
                continue;
            }

            var owner = matchedOwners[0];
            var classification = ClassifyOwnership(slot, owner, out var detail);
            findings.Add(new OwnershipMatrixFinding(classification, slot.Name, leaseId, owner.OwnerKind, detail));
        }

        foreach (var owner in owners)
        {
            if (poolLeaseIds.Contains(owner.PoolLeaseId)) continue;
            findings.Add(new OwnershipMatrixFinding(
                owner.PoolLeaseCurrent ? "OWNER_ONLY" : "STALE_LEASE",
                owner.TunerName,
                owner.PoolLeaseId,
                owner.OwnerKind,
                owner.PoolLeaseCurrent ? "owner_claim_without_pool_slot" : "owner_reports_pool_lease_not_current"));
        }

        var counts = findings.GroupBy(x => x.Classification, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var inconsistent = findings.Where(x => !string.Equals(x.Classification, "CONSISTENT", StringComparison.OrdinalIgnoreCase)).ToList();

        var consistentCount = CountOf(counts, "CONSISTENT");
        var poolOnlyCount = CountOf(counts, "POOL_ONLY");
        var ownerOnlyCount = CountOf(counts, "OWNER_ONLY");
        var multipleOwnersCount = CountOf(counts, "MULTIPLE_OWNERS");
        var staleLeaseCount = CountOf(counts, "STALE_LEASE");
        var usageMismatchCount = CountOf(counts, "USAGE_KIND_MISMATCH");
        var reservationMismatchCount = CountOf(counts, "RESERVATION_ID_MISMATCH");
        var pidMismatchCount = CountOf(counts, "PID_IDENTITY_MISMATCH");
        var physicalMismatchCount = CountOf(counts, "PHYSICAL_IDENTITY_MISMATCH");
        var generationMismatchCount = CountOf(counts, "LEASE_GENERATION_MISMATCH");
        var unknownCount = CountOf(counts, "UNKNOWN");
        var matrixSignature =
            $"poolActive={pool.Count}|ownerClaims={owners.Count}|findings={findings.Count}|consistent={consistentCount}|" +
            $"poolOnly={poolOnlyCount}|ownerOnly={ownerOnlyCount}|multipleOwners={multipleOwnersCount}|" +
            $"staleLease={staleLeaseCount}|usageMismatch={usageMismatchCount}|reservationMismatch={reservationMismatchCount}|" +
            $"pidMismatch={pidMismatchCount}|physicalMismatch={physicalMismatchCount}|" +
            $"generationMismatch={generationMismatchCount}|unknown={unknownCount}";

        if (ShouldLogPeriodicStableState(
                context,
                matrixSignature,
                hasFinding: inconsistent.Count > 0,
                ref _lastPeriodicOwnershipMatrixSignature,
                ref _lastPeriodicOwnershipMatrixLogAt))
        {
            _log.Add("TUNER_OWNERSHIP_MATRIX", context.CycleId,
                $"source={context.Source} poolActive={pool.Count} ownerClaims={owners.Count} findings={findings.Count} " +
                $"consistent={consistentCount} poolOnly={poolOnlyCount} ownerOnly={ownerOnlyCount} " +
                $"multipleOwners={multipleOwnersCount} staleLease={staleLeaseCount} " +
                $"usageMismatch={usageMismatchCount} reservationMismatch={reservationMismatchCount} " +
                $"pidMismatch={pidMismatchCount} physicalMismatch={physicalMismatchCount} " +
                $"generationMismatch={generationMismatchCount} unknown={unknownCount} " +
                "action=observe_only_no_automatic_repair rule=release_contract");
        }

        foreach (var finding in inconsistent.Take(64))
        {
            _log.Add("TUNER_OWNERSHIP_MATRIX_DETAIL", context.CycleId,
                $"classification={finding.Classification} owner={SafeValue(finding.OwnerKind)} tuner={SafeValue(finding.TunerName)} " +
                $"poolLeaseId={finding.PoolLeaseId?.ToString("N") ?? "-"} detail={SafeValue(finding.Detail)} " +
                "action=observe_only_no_automatic_repair rule=release_contract");
        }
    }


    private static bool ShouldLogPeriodicStableState(
        TunerOwnershipReconcileContext context,
        string signature,
        bool hasFinding,
        ref string? lastSignature,
        ref DateTime lastLoggedAt)
    {
        var now = DateTime.Now;
        var isPeriodic = context.Kind == TunerOwnershipReconcileKind.Periodic;
        var changed = !string.Equals(lastSignature, signature, StringComparison.Ordinal);
        var summaryDue = now - lastLoggedAt >= StablePeriodicLogInterval;
        if (!isPeriodic || hasFinding || changed || summaryDue)
        {
            lastSignature = signature;
            lastLoggedAt = now;
            return true;
        }

        return false;
    }

    private static string ClassifyOwnership(TunerSlotStatus slot, OwnershipProjection owner, out string detail)
    {
        if (!owner.PoolLeaseCurrent)
        {
            detail = "owner_reports_pool_lease_not_current";
            return "STALE_LEASE";
        }

        if (slot.OccupancyGeneration != owner.OccupancyGeneration)
        {
            detail = $"poolGeneration={slot.OccupancyGeneration} ownerGeneration={owner.OccupancyGeneration}";
            return "LEASE_GENERATION_MISMATCH";
        }

        if (slot.UsageKind != owner.ExpectedUsageKind)
        {
            detail = $"poolUsage={slot.UsageKind} ownerUsage={owner.ExpectedUsageKind}";
            return "USAGE_KIND_MISMATCH";
        }

        if (owner.ExpectedUsageKind == TunerUsageKind.Recording && slot.ReservationId != owner.ReservationId)
        {
            detail = $"poolReservation={slot.ReservationId?.ToString() ?? "-"} ownerReservation={owner.ReservationId?.ToString() ?? "-"}";
            return "RESERVATION_ID_MISMATCH";
        }

        if (owner.ProcessId.HasValue && slot.ProcessId != owner.ProcessId)
        {
            detail = $"poolPid={slot.ProcessId?.ToString() ?? "-"} ownerPid={owner.ProcessId.Value}";
            return "PID_IDENTITY_MISMATCH";
        }

        if (!SameIdentity(slot.Name, owner.TunerName)
            || !SameIdentity(slot.Did, owner.Did)
            || !SameIdentity(slot.BonDriverFileName, owner.BonDriverFileName))
        {
            detail = $"pool={SafeValue(slot.Name)}/{SafeValue(slot.Did)}/{SafeValue(slot.BonDriverFileName)} " +
                     $"owner={SafeValue(owner.TunerName)}/{SafeValue(owner.Did)}/{SafeValue(owner.BonDriverFileName)}";
            return "PHYSICAL_IDENTITY_MISMATCH";
        }

        detail = $"ownerId={SafeValue(owner.OwnerId)}";
        return "CONSISTENT";
    }

    private static bool SameIdentity(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int CountOf(IReadOnlyDictionary<string, int> counts, string key)
        => counts.TryGetValue(key, out var value) ? value : 0;

    private static string SafeValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace(' ', '_');

    private sealed record OwnershipProjection(
        string OwnerKind,
        Guid PoolLeaseId,
        long OccupancyGeneration,
        bool PoolLeaseCurrent,
        TunerUsageKind ExpectedUsageKind,
        int? ReservationId,
        int? ProcessId,
        string TunerName,
        string Did,
        string BonDriverFileName,
        string OwnerId);

    private sealed record OwnershipMatrixFinding(
        string Classification,
        string TunerName,
        Guid? PoolLeaseId,
        string OwnerKind,
        string Detail);

    public void CapturePowerSuspendSnapshot(TunerOwnershipReconcileContext context)
    {
        var recordingSessions = _recording.ObservePowerSuspendRecordingState(context.StartedAt, context.CycleId);
        var epgWorkers = _epg.CapturePowerSuspendSnapshot(context);

        _log.Add("TUNER_SUSPEND_SNAPSHOT", context.CycleId,
            $"result=OBSERVED source={context.Source} recordingSessions={recordingSessions} epgWorkers={epgWorkers} tunerState={_tunerPool.GetStatusSummary()} " +
            "action=observation_only_no_worker_interrupt_decision rule=power_notification_observation_only_contract");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? active;
        lock (_lifecycleGate)
        {
            _stopping = true;
            active = _activeReconcileTask;
        }

        if (active is null) return;
        try
        {
            await active.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The owner result already records individual reconciliation failures.
        }
    }
}
