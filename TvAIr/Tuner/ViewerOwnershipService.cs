using TvAIr.Core;

namespace TvAIr.Tuner;

/// <summary>
/// Viewer process, managed-process registry, and tuner lease ownership are finalized as one operation.
/// API routing and user-facing response formatting remain outside this service.
/// </summary>
public sealed class ViewerOwnershipService
{
    private readonly ExternalTunerLeaseService _leases;
    private readonly TvTestLauncher _launcher;
    private readonly LogRepository _log;

    public ViewerOwnershipService(
        ExternalTunerLeaseService leases,
        TvTestLauncher launcher,
        LogRepository log)
    {
        _leases = leases;
        _launcher = launcher;
        _log = log;
    }


    public ViewerOwnershipStartResult StartNew(
        string ownershipId,
        string bonDriverFileName,
        string did,
        string tunerGroup,
        string channelArgument,
        bool preserveViewerWindowState,
        string? viewerActivation,
        ViewerWindowStateSnapshot? restoreWindowState,
        ushort? networkId,
        ushort? transportStreamId,
        ushort? serviceId,
        int? channelSpace,
        int? channelIndex)
    {
        var launch = _launcher.StartViewer(
            ownershipId,
            bonDriverFileName,
            did,
            tunerGroup,
            channelArgument,
            preserveViewerWindowState,
            viewerActivation,
            restoreWindowState);
        if (!launch.Success)
        {
            var released = _leases.Release(ownershipId, "viewer_start_launch_failed");
            _log.Add("VIEWER_OWNERSHIP_START", "Viewer",
                $"result=FAILED leaseId={Safe(ownershipId)} reason=launch_failed pid={launch.ProcessId} leaseReleased={released} message={Safe(launch.Message)} rule=release_contract");
            return ViewerOwnershipStartResult.Failed(ViewerOwnershipStartFailureKind.LaunchFailed, "viewerLaunchFailed", launch, false, released);
        }

        var attached = _leases.AttachViewerProcess(
            ownershipId,
            launch.ProcessId,
            channelArgument,
            "launched",
            "started",
            "argumentPassed",
            "requested",
            networkId,
            transportStreamId,
            serviceId,
            channelSpace,
            channelIndex);
        if (attached)
        {
            _log.Add("VIEWER_OWNERSHIP_START", "Viewer",
                $"result=OK leaseId={Safe(ownershipId)} pid={launch.ProcessId} rule=release_contract");
            return ViewerOwnershipStartResult.Succeeded(launch);
        }

        _leases.SetViewerState(ownershipId, "failed", "started", "argumentPassed", "notAttached", "rollback_pending");
        var rollback = RollbackNewOwnership(ownershipId, launch.ProcessId, "viewer_attach_failed_rollback");
        _log.Add("VIEWER_OWNERSHIP_START", "Viewer",
            $"result=FAILED leaseId={Safe(ownershipId)} pid={launch.ProcessId} reason=ownership_attach_failed rollbackSuccess={rollback.Success} rule=release_contract");
        return ViewerOwnershipStartResult.Failed(ViewerOwnershipStartFailureKind.AttachFailed, "viewerOwnershipAttachFailed", launch, false, rollback.LeaseReleased, rollback);
    }

    public ViewerOwnershipRetuneResult RetuneExisting(
        string ownershipId,
        int processId,
        string bonDriverFileName,
        string did,
        string channelArgument,
        bool preserveViewerWindowState,
        string? viewerActivation,
        ushort? networkId,
        ushort? transportStreamId,
        ushort? serviceId,
        int? channelSpace,
        int? channelIndex)
    {
        if (!TvAirManagedProcessRegistry.TryGet(processId, out var managed) ||
            !managed.IsViewer ||
            !string.Equals(managed.OwnershipId, ownershipId, StringComparison.OrdinalIgnoreCase))
        {
            _log.Add("VIEWER_OWNERSHIP_RETUNE", "Viewer",
                $"result=FAILED leaseId={Safe(ownershipId)} pid={processId} reason=managed_ownership_mismatch action=preserve rule=release_contract");
            return ViewerOwnershipRetuneResult.Failed("managed_ownership_mismatch", new LaunchResult(false, processId, "managed viewer ownership mismatch"), false, false);
        }

        var observed = TvAirManagedProcessRegistry.CaptureIdentity(processId);
        if (!TvAirManagedProcessRegistry.IdentityMatches(managed.Identity, observed))
        {
            _log.Add("VIEWER_OWNERSHIP_RETUNE", "Viewer",
                $"result=FAILED leaseId={Safe(ownershipId)} pid={processId} reason=process_identity_mismatch action=preserve rule=release_contract");
            return ViewerOwnershipRetuneResult.Failed("process_identity_mismatch", new LaunchResult(false, processId, "viewer process identity mismatch"), false, false);
        }

        var retune = _launcher.RetuneExistingViewer(
            ownershipId,
            processId,
            bonDriverFileName,
            did,
            channelArgument,
            preserveViewerWindowState,
            viewerActivation);
        if (!retune.Success)
        {
            _log.Add("VIEWER_OWNERSHIP_RETUNE", "Viewer",
                $"result=FAILED leaseId={Safe(ownershipId)} pid={processId} reason=retune_failed message={Safe(retune.Message)} action=preserve rule=release_contract");
            return ViewerOwnershipRetuneResult.Failed("internalRetuneFailed", retune, false, false);
        }

        var attach = _leases.AttachViewerProcessDetailed(
            ownershipId,
            processId,
            channelArgument,
            "retuned",
            "reused",
            "pidTargetedRetuneCommandAccepted",
            "preserved",
            networkId,
            transportStreamId,
            serviceId,
            channelSpace,
            channelIndex);
        if (!attach.Success)
        {
            _leases.SetViewerState(ownershipId, "ownership_sync_failed", "reused", "pidTargetedRetuneCommandAccepted", "preserved", "lease_kept_for_reconcile");
            _log.Add("VIEWER_OWNERSHIP_RETUNE", "Viewer",
                $"result=FAILED leaseId={Safe(ownershipId)} pid={processId} reason=ownership_attach_failed attachFailureReason={Safe(attach.Reason)} action=preserve rule=release_contract");
            return ViewerOwnershipRetuneResult.Failed("viewerOwnershipAttachFailed", retune, false, true, attach.Reason);
        }

        _log.Add("VIEWER_OWNERSHIP_RETUNE", "Viewer",
            $"result=OK method=pid_targeted_retune_contract leaseId={Safe(ownershipId)} pid={processId} did={Safe(did)} bonDriver={Safe(bonDriverFileName)} processRestarted=False tuneConfirmation=receiver_command_accepted actualServiceIdentityVerified=False viewerActivation={Safe(viewerActivation)} foregroundAppliedByHost=False rule=release_contract");
        return ViewerOwnershipRetuneResult.Succeeded(retune);
    }

    public ViewerWindowStateSnapshot CaptureWindowState(int processId, string reason)
        => _launcher.CaptureViewerWindowState(processId, reason);

    public LaunchResult ActivateExisting(string leaseId, int processId)
    {
        if (!TvAirManagedProcessRegistry.TryGet(processId, out var managed) ||
            !managed.IsViewer ||
            !string.Equals(managed.OwnershipId, leaseId, StringComparison.OrdinalIgnoreCase))
            return new LaunchResult(false, processId, "Managed viewer ownership mismatch.");
        return _launcher.ActivateExistingViewer(leaseId, processId);
    }

    public ViewerOwnershipStopResult Stop(ExternalTunerLeaseDto lease, string reason)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var ownershipId = lease.LeaseId;
        var processIds = new SortedSet<int>();
        if (lease.ProcessId is > 0)
            processIds.Add(lease.ProcessId.Value);

        foreach (var viewer in TvAirManagedProcessRegistry.GetViewers()
                     .Where(x => string.Equals(x.OwnershipId, ownershipId, StringComparison.OrdinalIgnoreCase)))
        {
            if (viewer.ProcessId > 0)
                processIds.Add(viewer.ProcessId);
        }

        var details = new List<string>();
        foreach (var processId in processIds)
        {
            if (TvAirManagedProcessRegistry.TryGet(processId, out var managed))
            {
                if (!managed.IsViewer ||
                    !string.Equals(managed.OwnershipId, ownershipId, StringComparison.OrdinalIgnoreCase))
                {
                    details.Add($"pid={processId}:ownership_mismatch");
                    _log.Add("VIEWER_OWNERSHIP_STOP", "Viewer",
                        $"result=FAILED leaseId={ownershipId} pid={processId} reason=managed_ownership_mismatch action=preserve_lease rule=release_contract");
                    return ViewerOwnershipStopResult.Failed("managed_ownership_mismatch", details);
                }

                var observed = TvAirManagedProcessRegistry.CaptureIdentity(processId);
                if (!TvAirManagedProcessRegistry.IdentityMatches(managed.Identity, observed))
                {
                    details.Add($"pid={processId}:process_identity_mismatch");
                    _log.Add("VIEWER_OWNERSHIP_STOP", "Viewer",
                        $"result=FAILED leaseId={ownershipId} pid={processId} reason=process_identity_mismatch action=preserve_lease rule=release_contract");
                    return ViewerOwnershipStopResult.Failed("process_identity_mismatch", details);
                }
            }

            var stop = _launcher.StopManagedViewerProcess(processId, reason);
            details.Add($"pid={processId}:{stop.Message}");
            if (!stop.Success)
            {
                _leases.SetViewerState(ownershipId, "stop_failed", "preserved", "preserved", "preserved", "lease_preserved");
                _log.Add("VIEWER_OWNERSHIP_STOP", "Viewer",
                    $"result=FAILED leaseId={ownershipId} pid={processId} reason=process_stop_failed action=preserve_lease_and_registry rule=release_contract");
                return ViewerOwnershipStopResult.Failed("process_stop_failed", details);
            }
        }

        foreach (var processId in processIds)
            TvAirManagedProcessRegistry.Unregister(processId);

        if (!_leases.Release(ownershipId, reason))
        {
            _log.Add("VIEWER_OWNERSHIP_STOP", "Viewer",
                $"result=FAILED leaseId={ownershipId} reason=lease_release_failed processesStopped={processIds.Count} rule=release_contract");
            return ViewerOwnershipStopResult.Failed("lease_release_failed", details);
        }

        _log.Add("VIEWER_OWNERSHIP_STOP", "Viewer",
            $"result=OK leaseId={ownershipId} processesStopped={processIds.Count} processIds={Safe(string.Join(",", processIds))} rule=release_contract");
        return ViewerOwnershipStopResult.Succeeded(details);
    }

    public ViewerOwnershipRollbackResult RollbackNewOwnership(string leaseId, int processId, string reason)
    {
        var stop = processId > 0
            ? _launcher.StopManagedViewerProcess(processId, reason)
            : new LaunchResult(true, 0, "no pid");

        // VIEWER_ROLLBACK_OWNERSHIP_INVARIANT:
        // Never turn a still-running TvAIr-owned Viewer into an unmanaged process.
        // If process termination fails, keep both the managed-process registration and tuner lease
        // so the normal process-exit/reconciliation path can still close the exact ownership.
        if (!stop.Success)
        {
            _leases.SetViewerState(leaseId, "rollback_stop_failed", "preserved", "preserved", "preserved", "ownership_preserved");
            _log.Add("VIEWER_OWNERSHIP_ROLLBACK", "Viewer",
                $"result=FAILED leaseId={Safe(leaseId)} pid={processId} processStop={Safe(stop.Message)} leaseReleased=False managedOwnershipPreserved=True reason=process_stop_failed rule=viewer_session_contract");
            return new ViewerOwnershipRollbackResult(false, stop.Message, false);
        }

        if (processId > 0)
            TvAirManagedProcessRegistry.Unregister(processId);

        var released = _leases.Release(leaseId, reason);
        _log.Add("VIEWER_OWNERSHIP_ROLLBACK", "Viewer",
            $"result={(released ? "OK" : "FAILED")} leaseId={Safe(leaseId)} pid={processId} processStop={Safe(stop.Message)} leaseReleased={released} managedOwnershipPreserved=False rule=viewer_session_contract");
        return new ViewerOwnershipRollbackResult(released, stop.Message, released);
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public sealed record ViewerOwnershipStopResult(bool Success, string Reason, IReadOnlyList<string> Details)
{
    public static ViewerOwnershipStopResult Succeeded(IReadOnlyList<string> details)
        => new(true, "ok", details);

    public static ViewerOwnershipStopResult Failed(string reason, IReadOnlyList<string> details)
        => new(false, reason, details);
}

public sealed record ViewerOwnershipRollbackResult(bool Success, string ProcessStopResult, bool LeaseReleased);


public enum ViewerOwnershipStartFailureKind
{
    None,
    LaunchFailed,
    AttachFailed
}

public sealed record ViewerOwnershipStartResult(
    bool Success,
    ViewerOwnershipStartFailureKind FailureKind,
    string Reason,
    LaunchResult Launch,
    bool Attached,
    bool LeaseReleased,
    ViewerOwnershipRollbackResult? Rollback)
{
    public static ViewerOwnershipStartResult Succeeded(LaunchResult launch)
        => new(true, ViewerOwnershipStartFailureKind.None, "ok", launch, true, false, null);

    public static ViewerOwnershipStartResult Failed(
        ViewerOwnershipStartFailureKind failureKind,
        string reason,
        LaunchResult launch,
        bool attached,
        bool leaseReleased,
        ViewerOwnershipRollbackResult? rollback = null)
        => new(false, failureKind, reason, launch, attached, leaseReleased, rollback);
}

public sealed record ViewerOwnershipRetuneResult(
    bool Success,
    string Reason,
    LaunchResult Retune,
    bool Attached,
    bool CommandAccepted,
    string AttachFailureReason)
{
    public static ViewerOwnershipRetuneResult Succeeded(LaunchResult retune)
        => new(true, "ok", retune, true, true, string.Empty);

    public static ViewerOwnershipRetuneResult Failed(
        string reason,
        LaunchResult retune,
        bool attached,
        bool commandAccepted,
        string attachFailureReason = "")
        => new(false, reason, retune, attached, commandAccepted, attachFailureReason);
}
