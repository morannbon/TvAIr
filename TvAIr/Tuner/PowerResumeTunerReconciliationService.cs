using Microsoft.Win32;
using TvAIr.Core;

namespace TvAIr.Tuner;

public sealed class PowerResumeTunerReconciliationService : BackgroundService
{
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResumeVerificationDelay = TimeSpan.FromSeconds(2);

    private readonly TunerOwnershipReconciliationCoordinator _coordinator;
    private readonly LogRepository _log;
    private readonly PowerResumeSignalHub _powerSignals;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _cycleGate = new();
    private TunerOwnershipReconcileContext? _pendingResumeCycle;
    private string? _openPowerCycleId;
    private DateTime? _openPowerSuspendAt;
    private int _started;

    public PowerResumeTunerReconciliationService(
        TunerOwnershipReconciliationCoordinator coordinator,
        LogRepository log,
        PowerResumeSignalHub powerSignals)
    {
        _coordinator = coordinator;
        _log = log;
        _powerSignals = powerSignals;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _coordinator.ReconcileAsync(CreateContext(TunerOwnershipReconcileKind.Startup), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for either a resume signal or the periodic reconciliation deadline without
            // leaving a losing SemaphoreSlim waiter behind on every periodic tick.
            var resumeSignalled = await _signal
                .WaitAsync(PeriodicInterval, stoppingToken)
                .ConfigureAwait(false);

            if (resumeSignalled)
            {
                TunerOwnershipReconcileContext context;
                lock (_cycleGate)
                {
                    context = _pendingResumeCycle ?? CreateContext(TunerOwnershipReconcileKind.PowerResume);
                    _pendingResumeCycle = null;
                }

                // PowerModeChanged.Resume is the action signal. Do not delay the first ownership
                // reconciliation behind an arbitrary settle window. Reconcile immediately, then
                // run one bounded verification pass because Windows process/device visibility can
                // still converge shortly after resume. The delay is therefore an observation
                // interval, not a prerequisite for the primary action.
                await _coordinator.ReconcileAsync(context, stoppingToken).ConfigureAwait(false);

                await Task.Delay(ResumeVerificationDelay, stoppingToken).ConfigureAwait(false);
                var verificationContext = new TunerOwnershipReconcileContext(
                    context.CycleId,
                    TunerOwnershipReconcileKind.PowerResumeVerification,
                    context.StartedAt);
                await _coordinator.ReconcileAsync(verificationContext, stoppingToken).ConfigureAwait(false);

                // Daily等の上位業務判断はPower/Tuner層で行わない。
                // 即時＋verificationの物理owner整合が完了した事実だけを通知する。
                _powerSignals.PublishResumeReconciled(context.CycleId, DateTime.Now);
            }
            else
            {
                await _coordinator.ReconcileAsync(CreateContext(TunerOwnershipReconcileKind.Periodic), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            var now = DateTime.Now;
            string suspendCycleId;
            string? supersededCycle;
            lock (_cycleGate)
            {
                supersededCycle = _openPowerCycleId;
                suspendCycleId = $"PWR-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
                _openPowerCycleId = suspendCycleId;
                _openPowerSuspendAt = now;
            }

            if (!string.IsNullOrWhiteSpace(supersededCycle))
            {
                _log.Add("POWER_CYCLE", suspendCycleId,
                    $"result=SUSPEND_SUPERSEDES_OPEN previousCycle={supersededCycle} action=replace_observation_cycle_no_worker_action rule=power_notification_cycle_contract");
            }

            var suspendContext = new TunerOwnershipReconcileContext(suspendCycleId, TunerOwnershipReconcileKind.PowerSuspend, now);
            _coordinator.CapturePowerSuspendSnapshot(suspendContext);
            _powerSignals.PublishSuspendObserved(suspendCycleId, now);
            _log.Add("POWER_CYCLE", suspendCycleId,
                $"result=SUSPEND_OBSERVED at={now:MM/dd HH:mm:ss} action=observe_only_wait_for_optional_resume rule=power_notification_cycle_contract");
            return;
        }

        if (e.Mode != PowerModes.Resume) return;

        var resumeAt = DateTime.Now;
        string cycleId;
        DateTime? suspendAt;
        bool paired;
        bool queued;
        lock (_cycleGate)
        {
            paired = !string.IsNullOrWhiteSpace(_openPowerCycleId);
            cycleId = _openPowerCycleId ?? $"PWR-UNPAIRED-{resumeAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            suspendAt = _openPowerSuspendAt;
            _openPowerCycleId = null;
            _openPowerSuspendAt = null;

            var context = new TunerOwnershipReconcileContext(cycleId, TunerOwnershipReconcileKind.PowerResume, resumeAt);
            queued = _pendingResumeCycle is null;
            _pendingResumeCycle ??= context;
        }

        _log.Add("POWER_CYCLE", cycleId,
            $"result={(paired ? "RESUME_PAIRED" : "RESUME_UNPAIRED")} resume={resumeAt:MM/dd HH:mm:ss} suspend={(suspendAt.HasValue ? suspendAt.Value.ToString("MM/dd HH:mm:ss") : "-")} " +
            $"queued={queued} action=reconcile_observed_owner_state_no_direct_worker_interrupt rule=power_notification_cycle_contract");
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    private static TunerOwnershipReconcileContext CreateContext(TunerOwnershipReconcileKind kind)
        => new($"PRC-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}", kind, DateTime.Now);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 1)
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _started, 0) == 1)
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _signal.Dispose();
        base.Dispose();
    }
}
