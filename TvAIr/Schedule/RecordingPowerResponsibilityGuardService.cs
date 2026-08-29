using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>
/// 目前の録画責務をWindowsスリープから保護する。
///
/// EPG等の先行ownerがPower Requestを解放する前に録画側が同じ時間軸で先取りし、
/// ScheduledからStarting/Recording/Stoppingを経てterminalへ到達するまで保持する。
/// 固定の後段猶予ではなく、予約ライフサイクルそのものを保護区間の正本とする。
///
/// 予約DB全件を短周期pollingせず、確定Reservation mutationで対象集合を更新し、
/// 次のPower取得境界またはmutationまで待機する。初回同期だけDB全件を読む。
/// </summary>
public sealed class RecordingPowerResponsibilityGuardService : BackgroundService
{
    // PreStartMargin変更はReservation mutationを伴わないため、DB再読込ではなく
    // メモリ上の対象集合だけを低コストで再評価する上限間隔を持つ。
    private static readonly TimeSpan InMemoryReevaluationInterval = TimeSpan.FromSeconds(5);

    private readonly ReservationStore _store;
    private readonly ReservationMutationJournal _mutationJournal;
    private readonly IniSettingsService _ini;
    private readonly SystemSleepInhibitionService _sleepInhibition;
    private readonly LogRepository _log;
    private readonly object _gate = new();
    private readonly Dictionary<int, Reservation> _tracked = new();
    private readonly Dictionary<int, PowerGuardEntry> _leases = new();
    private readonly HashSet<int> _mutatedDuringInitialLoad = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private bool _initialLoadInProgress;

    public RecordingPowerResponsibilityGuardService(
        ReservationStore store,
        ReservationMutationJournal mutationJournal,
        IniSettingsService ini,
        SystemSleepInhibitionService sleepInhibition,
        LogRepository log)
    {
        _store = store;
        _mutationJournal = mutationJournal;
        _ini = ini;
        _sleepInhibition = sleepInhibition;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Add("RECORDING_POWER_GUARD", "START",
            $"result=STARTED mode=mutation_signal initialDbRead=once inMemoryReevaluationMs={(int)InMemoryReevaluationInterval.TotalMilliseconds} " +
            $"lookAheadSeconds={RecordingResponsibilityTiming.DueLookAheadSeconds} " +
            "handoff=overlap_before_predecessor_release lifecycle=scheduled_due_to_terminal rule=recording_power_responsibility_guard_contract");

        _mutationJournal.Recorded += OnReservationMutation;
        try
        {
            InitializeTrackedReservations();

            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    wait = Synchronize(DateTime.Now);
                }
                catch (Exception ex)
                {
                    _log.Add("RECORDING_POWER_GUARD", "ERROR",
                        $"result=ERROR message={Safe(ex.Message)} rule=recording_power_responsibility_guard_contract");
                    wait = InMemoryReevaluationInterval;
                }

                await WaitForChangeOrBoundaryAsync(wait, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _mutationJournal.Recorded -= OnReservationMutation;
            ReleaseAll("host_stopping");
            _changed.Dispose();
        }
    }

    private void InitializeTrackedReservations()
    {
        lock (_gate)
        {
            _initialLoadInProgress = true;
            _mutatedDuringInitialLoad.Clear();
        }

        var initial = _store.GetAll();
        var now = DateTime.Now;

        lock (_gate)
        {
            foreach (var reservation in initial)
            {
                // Subscribe-before-loadで初回SELECT中のmutationを取りこぼさない。
                // 同じIDにmutationが来た場合は新しいsnapshotを優先する。
                if (_mutatedDuringInitialLoad.Contains(reservation.Id))
                    continue;
                TrackOrRemoveLocked(reservation, now);
            }

            _initialLoadInProgress = false;
            _mutatedDuringInitialLoad.Clear();
        }
    }

    private void OnReservationMutation(ReservationMutationResult mutation)
    {
        var reservationId = mutation.ReservationId;
        if (reservationId <= 0)
            return;

        var now = DateTime.Now;
        lock (_gate)
        {
            if (_initialLoadInProgress)
                _mutatedDuringInitialLoad.Add(reservationId);

            if (mutation.After is null)
            {
                _tracked.Remove(reservationId);
            }
            else if (IsRelevantResponsibility(mutation.After, now) || _leases.ContainsKey(reservationId))
            {
                // lease保持中のterminal/disable/time-moveも一度snapshotを残し、
                // Synchronize側で正しいrelease reasonを確定してから除去する。
                _tracked[reservationId] = mutation.After;
            }
            else
            {
                _tracked.Remove(reservationId);
            }
        }

        SignalChanged();
    }

    private void TrackOrRemoveLocked(Reservation reservation, DateTime now)
    {
        if (IsRelevantResponsibility(reservation, now))
            _tracked[reservation.Id] = reservation;
        else
            _tracked.Remove(reservation.Id);
    }

    private bool IsRelevantResponsibility(Reservation reservation, DateTime now)
    {
        if (reservation.Source == ReservationSource.Epg)
            return false;

        if (reservation.Status is ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping)
            return true;

        return reservation.Status == ReservationStatus.Scheduled
            && reservation.IsEnabled
            && reservation.EndTime.AddSeconds(Math.Max(10, _ini.PostEndMarginSeconds)) >= now;
    }

    private TimeSpan Synchronize(DateTime now)
    {
        Reservation[] reservations;
        lock (_gate)
            reservations = _tracked.Values.ToArray();

        var byId = reservations.ToDictionary(r => r.Id);

        foreach (var reservation in reservations)
        {
            if (!ShouldHold(reservation, now))
                continue;

            lock (_gate)
            {
                if (_leases.ContainsKey(reservation.Id))
                    continue;
            }

            if (!_sleepInhibition.TryAcquire(
                    "ReservationScheduler.PowerGuard",
                    $"Recording:R{reservation.Id}",
                    reservation.DataVersion,
                    out var lease,
                    out var error) || lease is null)
            {
                _log.Add("RECORDING_POWER_GUARD", $"R{reservation.Id}",
                    $"result=ACQUIRE_FAILED status={reservation.Status} start={reservation.StartTime:yyyy-MM-dd HH:mm:ss} " +
                    $"reason={Safe(error)} rule=recording_power_responsibility_guard_contract");
                continue;
            }

            var accepted = false;
            lock (_gate)
            {
                if (!_leases.ContainsKey(reservation.Id))
                {
                    _leases[reservation.Id] = new PowerGuardEntry(lease, reservation.StartTime, reservation.DataVersion);
                    accepted = true;
                }
            }

            if (!accepted)
            {
                lease.Dispose();
                continue;
            }

            _log.Add("RECORDING_POWER_GUARD", $"R{reservation.Id}",
                $"result=ACQUIRED status={reservation.Status} start={reservation.StartTime:yyyy-MM-dd HH:mm:ss} " +
                $"due={GetRecordingDue(reservation):yyyy-MM-dd HH:mm:ss} " +
                "release=terminal_or_responsibility_removed overlapWithPredecessor=True rule=recording_power_responsibility_guard_contract");
        }

        int[] leasedIds;
        lock (_gate)
            leasedIds = _leases.Keys.ToArray();

        foreach (var reservationId in leasedIds)
        {
            if (byId.TryGetValue(reservationId, out var reservation) && ShouldHold(reservation, now))
                continue;

            var reason = byId.TryGetValue(reservationId, out reservation)
                ? BuildReleaseReason(reservation, now)
                : "reservation_missing_or_terminal";
            Release(reservationId, reason);
        }

        lock (_gate)
        {
            foreach (var reservationId in _tracked.Keys.ToArray())
            {
                var reservation = _tracked[reservationId];
                if (!IsRelevantResponsibility(reservation, now))
                    _tracked.Remove(reservationId);
            }
        }

        return ComputeNextWait(now);
    }

    private TimeSpan ComputeNextWait(DateTime now)
    {
        DateTime? nextBoundary = null;
        lock (_gate)
        {
            foreach (var reservation in _tracked.Values)
            {
                if (reservation.Status is ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping)
                {
                    if (!_leases.ContainsKey(reservation.Id))
                        return TimeSpan.Zero;
                    continue;
                }

                if (reservation.Status != ReservationStatus.Scheduled || !reservation.IsEnabled || _leases.ContainsKey(reservation.Id))
                    continue;

                var boundary = GetRecordingDue(reservation).AddSeconds(-RecordingResponsibilityTiming.DueLookAheadSeconds);
                if (boundary <= now)
                    return TimeSpan.Zero;
                if (!nextBoundary.HasValue || boundary < nextBoundary.Value)
                    nextBoundary = boundary;
            }
        }

        if (!nextBoundary.HasValue)
            return InMemoryReevaluationInterval;

        var untilBoundary = nextBoundary.Value - now;
        return untilBoundary < InMemoryReevaluationInterval ? untilBoundary : InMemoryReevaluationInterval;
    }

    private async Task WaitForChangeOrBoundaryAsync(TimeSpan wait, CancellationToken stoppingToken)
    {
        if (wait <= TimeSpan.Zero)
            return;

        await _changed.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
    }

    private void SignalChanged()
    {
        try
        {
            if (_changed.CurrentCount == 0)
                _changed.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private bool ShouldHold(Reservation reservation, DateTime now)
    {
        if (reservation.Source == ReservationSource.Epg)
            return false;

        if (reservation.Status is ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping)
            return true;

        if (reservation.Status != ReservationStatus.Scheduled || !reservation.IsEnabled)
            return false;

        var due = GetRecordingDue(reservation);
        return due <= now.AddSeconds(RecordingResponsibilityTiming.DueLookAheadSeconds)
            && reservation.EndTime.AddSeconds(Math.Max(10, _ini.PostEndMarginSeconds)) >= now;
    }

    private DateTime GetRecordingDue(Reservation reservation)
        => reservation.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);

    private static string BuildReleaseReason(Reservation reservation, DateTime now)
    {
        if (reservation.Status is ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed)
            return $"terminal_{reservation.Status.ToString().ToLowerInvariant()}";
        if (!reservation.IsEnabled)
            return "disabled_before_start";
        if (reservation.Status == ReservationStatus.Scheduled)
            return "scheduled_responsibility_moved_outside_guard_window";
        return $"responsibility_removed_status_{reservation.Status.ToString().ToLowerInvariant()}";
    }

    private void Release(int reservationId, string reason)
    {
        PowerGuardEntry? entry;
        lock (_gate)
        {
            if (!_leases.Remove(reservationId, out entry))
                return;
        }

        try
        {
            entry.Lease.Dispose();
        }
        finally
        {
            _log.Add("RECORDING_POWER_GUARD", $"R{reservationId}",
                $"result=RELEASED reason={Safe(reason)} start={entry.StartTime:yyyy-MM-dd HH:mm:ss} " +
                $"acquiredDataVersion={entry.DataVersion} rule=recording_power_responsibility_guard_contract");
        }
    }

    private void ReleaseAll(string reason)
    {
        int[] reservationIds;
        lock (_gate)
            reservationIds = _leases.Keys.ToArray();
        foreach (var reservationId in reservationIds)
            Release(reservationId, reason);
    }

    private sealed record PowerGuardEntry(IDisposable Lease, DateTime StartTime, long DataVersion);

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '_');
}
