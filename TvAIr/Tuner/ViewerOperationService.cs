using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Plugin;

namespace TvAIr.Tuner;

/// <summary>
/// Viewer profile/session/channel/lease ownership orchestration.
/// HTTP action parsing, plugin-window refresh, and response rendering stay outside this service.
/// </summary>
public sealed class ViewerOperationService
{
    private readonly ChannelFileLoader _channels;
    private readonly ExternalTunerLeaseService _leases;
    private readonly TunerPool _tunerPool;
    private readonly ViewerSessionRegistry _sessions;
    private readonly ViewerOwnershipService _ownership;
    private readonly IOptions<TvTestSettings> _tvTestOptions;
    private readonly IniSettingsService _ini;
    private readonly IReadOnlyList<TunerProfile> _tunerProfiles;
    private readonly LogRepository _log;
    private readonly ApplicationOperationGate _applicationGate;
    private readonly ConcurrentDictionary<string, PendingViewerWindowState> _pendingWindowStateByProfile = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _operationGateByProfile = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ViewerProcessExitWatch> _viewerProcessExitWatches = new(StringComparer.OrdinalIgnoreCase);

    public ViewerOperationService(
        ChannelFileLoader channels,
        ExternalTunerLeaseService leases,
        TunerPool tunerPool,
        ViewerSessionRegistry sessions,
        ViewerOwnershipService ownership,
        IOptions<TvTestSettings> tvTestOptions,
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> tunerProfiles,
        LogRepository log,
        ApplicationOperationGate applicationGate)
    {
        _channels = channels;
        _leases = leases;
        _tunerPool = tunerPool;
        _sessions = sessions;
        _ownership = ownership;
        _tvTestOptions = tvTestOptions;
        _ini = ini;
        _tunerProfiles = tunerProfiles;
        _log = log;
        _applicationGate = applicationGate;
    }

    /// <summary>
    /// Resolves and validates the common start/retune inputs without changing viewer state.
    /// The returned plan is the sole input to the later ownership mutation step.
    /// </summary>
    public ViewerOperationPreparationResult Prepare(ViewerOperationPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryNormalizeViewerActivation(request.ViewerActivation, out var viewerActivation))
            return ViewerOperationPreparationResult.Denied("viewerActivationInvalid", "ViewerActivation must be activate or preserve.");

        var profile = ViewerProfileContract.ResolveRequestedProfile(
            request.ViewerProfileId,
            request.GroupHint,
            _tvTestOptions.Value,
            _ini,
            _tunerProfiles);

        _sessions.Synchronize(_leases.GetActiveLeases());

        if (request.ExpectedGeneration.HasValue && string.IsNullOrWhiteSpace(request.ViewerSessionId))
            return ViewerOperationPreparationResult.Denied("viewerSessionRequired", "ExpectedGeneration requires ViewerSessionId.");

        ViewerSessionState? session = null;
        if (!string.IsNullOrWhiteSpace(request.ViewerSessionId))
        {
            session = _sessions.GetBySessionId(request.ViewerSessionId);
            if (session is null)
                return ViewerOperationPreparationResult.Denied("viewerSessionNotFound", "Requested viewer session was not found.");

            if (!string.Equals(session.ViewerProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
                return ViewerOperationPreparationResult.Denied("viewerSessionProfileMismatch", "Viewer session does not belong to the requested profile.");

            if (request.ExpectedGeneration.HasValue && request.ExpectedGeneration.Value != session.Generation)
                return ViewerOperationPreparationResult.Denied("staleViewerGeneration", "Viewer session generation is stale.");
        }

        if (!profile.Enabled)
        {
            var errorCode = string.IsNullOrWhiteSpace(profile.ErrorCode)
                ? "viewerProfileUnavailable"
                : profile.ErrorCode;
            return ViewerOperationPreparationResult.Denied(errorCode, "Requested viewer profile is not configured.");
        }

        if (request.NetworkId == 0 || request.TransportStreamId == 0 || request.ServiceId == 0)
            return ViewerOperationPreparationResult.Denied("missingViewerPayload", "ViewerStart payload is incomplete.");

        var channelMap = _channels.Load();
        var channel = channelMap.Targets.FirstOrDefault(c =>
            c.OriginalNetworkId == request.NetworkId &&
            c.TransportStreamId == request.TransportStreamId &&
            c.ServiceId == request.ServiceId);
        if (channel is null)
            return ViewerOperationPreparationResult.Denied("channelNotFound", "Viewer target channel was not found in the TvAIr channel map.");

        var group = string.IsNullOrWhiteSpace(channel.Group)
            ? (request.GroupHint ?? string.Empty).Trim()
            : channel.Group.Trim();
        if (!ViewerProfileContract.ProfileSupportsGroup(profile, group))
            return ViewerOperationPreparationResult.Denied("viewerProfileGroupUnavailable", "Requested viewer profile has no viewer tuner for this broadcast group.");

        var pluginId = PluginIdentity.Normalize(request.PluginId, "Plugin");
        var clientId = ViewerProfileContract.BuildViewerClientId(pluginId, profile.Id);
        var pathKey = ViewerProfileContract.TvTestPathKeyForResolvedProfile(profile);
        var channelArgument = $"/chspace {channel.ResolvedSpace} /chi {channel.ResolvedChannelIndex} /sid {request.ServiceId}";
        var identityArgument = $"/nid {request.NetworkId} /tsid {request.TransportStreamId} /sid {request.ServiceId}";
        var sameTransportServices = channelMap.Targets
            .Where(c => c.OriginalNetworkId == request.NetworkId && c.TransportStreamId == request.TransportStreamId)
            .OrderBy(c => c.ServiceId)
            .ToArray();
        var sameTransportServiceIds = string.Join(",", sameTransportServices.Select(c => c.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        const string selectedChannelSource = "ProgramGuideProjectionTriplet(.ch2/chset resolved)";

        var plan = new ViewerOperationPlan(
            profile,
            session,
            channel,
            group,
            clientId,
            pathKey,
            channelArgument,
            identityArgument,
            // SERVICE_IDENTITY_CONTRACT: caller-supplied ServiceName is display metadata only.
            // Once the exact NID/TSID/SID resolves to the current ChannelTarget, the Host owns
            // the current display name and must not preserve a stale plugin-supplied station name.
            channel.Name,
            request.PreserveViewerWindowState,
            viewerActivation,
            sameTransportServices.Length,
            sameTransportServiceIds,
            selectedChannelSource);

        if (request.EmitDiagnosticLog)
        {
            _log.Add("VIEWER_OPERATION_PREPARE", "Viewer",
                $"result=OK mode={Safe(request.Mode)} viewerProfile={Safe(profile.Id)} viewerSession={Safe(session?.ViewerSessionId)} generation={(session?.Generation.ToString() ?? "-")} group={Safe(group)} nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId} rule=viewer_session_contract");
        }
        return ViewerOperationPreparationResult.Prepared(plan);
    }


    /// <summary>
    /// Starts a new Viewer Session for the resolved profile and channel.
    /// Existing sessions are not silently retuned; callers must use Retune with the current generation.
    /// </summary>
    public ViewerOperationResult Start(ViewerOperationStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_applicationGate.TryAdmit("viewer_start", request.PluginId, out var shutdownReason))
            return ViewerOperationResult.Denied("applicationQuiescing", shutdownReason);
        var profileId = ResolveOperationProfileId(request.ViewerProfileId, request.GroupHint);
        lock (GetOperationGate(profileId))
            return StartCore(request);
    }

    private ViewerOperationResult StartCore(
        ViewerOperationStartRequest request,
        ViewerWindowStateSnapshot? restoreWindowState = null,
        Action<ViewerWindowRestoreResult?>? windowRestoreObserver = null,
        ViewerRestartSourceIdentity? restartSourceIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = Prepare(new ViewerOperationPreparationRequest(
            request.PluginId,
            "start",
            request.ViewerProfileId,
            null,
            null,
            request.NetworkId,
            request.TransportStreamId,
            request.ServiceId,
            request.ServiceName,
            request.GroupHint,
            request.PreserveViewerWindowState,
            request.ViewerActivation));
        if (!prepared.Success || prepared.Plan is null)
            return ViewerOperationResult.Denied(prepared.ErrorCode, prepared.Message);

        var plan = prepared.Plan;

        var leaseResult = _leases.Request(new ExternalTunerLeaseRequest
        {
            Group = plan.Group,
            RequiredGroup = plan.Group,
            Source = $"Plugin:{request.PluginId}",
            ClientId = plan.ClientId,
            Note = $"viewerEnsureTuned service={plan.ServiceName} nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId}",
            NetworkId = request.NetworkId,
            TransportStreamId = request.TransportStreamId,
            ServiceId = request.ServiceId,
            ChannelSpace = plan.Channel.ResolvedSpace,
            ChannelIndex = plan.Channel.ResolvedChannelIndex,
            ViewerProfileId = plan.Profile.Id,
            ViewerProfileName = plan.Profile.Name,
            LogicalViewerSlotId = plan.Profile.LogicalViewerSlotId,
            TvTestPathKey = plan.TvTestPathKey,
            RequiredBonDriverFileName = restartSourceIdentity?.BonDriverFileName
        });

        if (!leaseResult.Success || leaseResult.Lease is null)
        {
            var errorCode = string.IsNullOrWhiteSpace(leaseResult.Reason) ? "tunerUnavailable" : leaseResult.Reason;
            return ViewerOperationResult.Denied(errorCode!, "No viewer tuner is available.");
        }

        var lease = leaseResult.Lease;
        if (restartSourceIdentity is not null && !restartSourceIdentity.Matches(lease))
        {
            _leases.Release(lease.LeaseId, "viewer_restart_identity_mismatch");
            var mismatch = restartSourceIdentity.DescribeMismatch(lease);
            _log.Add("VIEWER_OPERATION_RESTART", "Viewer",
                $"result=FAILED stage=lease_reacquire viewerProfile={Safe(plan.Profile.Id)} leaseId={Safe(lease.LeaseId)} expectedDid={Safe(restartSourceIdentity.Did)} actualDid={Safe(lease.Did)} expectedBonDriver={Safe(restartSourceIdentity.BonDriverFileName)} actualBonDriver={Safe(lease.BonDriverFileName)} expectedLogicalViewerSlotId={Safe(restartSourceIdentity.LogicalViewerSlotId)} actualLogicalViewerSlotId={Safe(lease.LogicalViewerSlotId)} expectedGroup={Safe(restartSourceIdentity.Group)} actualGroup={Safe(lease.Group)} reason={Safe(mismatch)} rule=viewer_session_contract");
            return ViewerOperationResult.Failed(
                "viewerRestartIdentityMismatch",
                "The replacement Viewer could not reacquire the same Viewer tuner identity.",
                null,
                lease,
                mismatch);
        }

        if (leaseResult.Reused)
        {
            _sessions.Synchronize(_leases.GetActiveLeases());
            var existing = _sessions.GetByLeaseId(lease.LeaseId);
            return ViewerOperationResult.Denied(
                "viewerSessionAlreadyActive",
                "The requested viewer profile already has an active session.",
                existing);
        }

        var viewingCapacity = _tunerPool.GetViewingCapacity();
        var activeViewingCount = _tunerPool.GetActiveViewingCount();
        if (viewingCapacity <= 0 || activeViewingCount > viewingCapacity)
        {
            _leases.Release(lease.LeaseId, "viewer_capacity_guard");
            _log.Add("VIEWER_CAPACITY_GUARD", "Viewer",
                $"result=DENIED viewerProfile={Safe(plan.Profile.Id)} activeViewing={activeViewingCount} capacity={viewingCapacity} reason=viewer_tuner_capacity_exceeded processStarted=False rule=viewer_session_contract");
            return ViewerOperationResult.Denied("viewerCapacityExceeded", "Configured viewer tuner capacity has been reached.");
        }

        var ownership = _ownership.StartNew(
            lease.LeaseId,
            lease.BonDriverFileName,
            lease.Did,
            lease.Group,
            plan.ChannelArgument,
            plan.PreserveViewerWindowState,
            plan.ViewerActivation,
            restoreWindowState,
            request.NetworkId,
            request.TransportStreamId,
            request.ServiceId,
            plan.Channel.ResolvedSpace,
            plan.Channel.ResolvedChannelIndex);
        windowRestoreObserver?.Invoke(ownership.Launch.WindowRestore);
        if (!ownership.Success)
        {
            return ViewerOperationResult.Failed(
                ownership.Reason,
                "Viewer ownership start failed.",
                null,
                lease,
                ownership.Launch.Message);
        }

        var sessions = _sessions.Synchronize(_leases.GetActiveLeases());
        var session = sessions.FirstOrDefault(x => string.Equals(x.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            var rollback = _ownership.RollbackNewOwnership(lease.LeaseId, ownership.Launch.ProcessId, "viewer_session_create_failed_rollback");
            return ViewerOperationResult.Failed(
                "viewerSessionCreateFailed",
                "Viewer started but its session could not be established.",
                null,
                lease,
                $"rollbackSuccess={rollback.Success};processStop={rollback.ProcessStopResult};leaseReleased={rollback.LeaseReleased}");
        }

        var activeLease = _leases.GetActiveLeases().FirstOrDefault(x =>
            string.Equals(x.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase)) ?? lease;
        if (session.ProcessId is > 0)
            ArmViewerProcessExitWatch(session.ViewerProfileId, session.LeaseId, session.ProcessId.Value);
        _log.Add("VIEWER_OPERATION_START", "Viewer",
            $"result=OK viewerProfile={Safe(session.ViewerProfileId)} viewerSession={Safe(session.ViewerSessionId)} generation={session.Generation} leaseId={Safe(session.LeaseId)} pid={(session.ProcessId?.ToString() ?? "-")} nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId} rule=viewer_session_contract");
        return ViewerOperationResult.Succeeded("launched", session, activeLease);
    }

    /// <summary>
    /// Ensures the requested viewer profile is tuned to the requested service.
    /// This preserves the viewer operation orchestration: launch when absent,
    /// retune the current managed viewer when alive, and restart only when the
    /// previously managed process has disappeared.
    /// </summary>
    public ViewerOperationResult EnsureTuned(ViewerOperationEnsureTunedRequest request)
        => EnsureTunedDetailed(request).Operation;

    /// <summary>
    /// Executes a Host-owned explicit Viewer Operation under the existing ViewerProfile operation gate.
    /// The preemption callback runs after the profile gate is acquired and immediately before the normal
    /// EnsureTuned core, preventing timer/manual operations from interleaving between preemption and retune/start.
    /// </summary>
    internal ViewerOperationResult EnsureTunedHostManaged(
        ViewerOperationEnsureTunedRequest request,
        Func<ViewerOperationPreemptionResult> preempt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preempt);
        if (!_applicationGate.TryAdmit("viewer_ensure_tuned_host_managed", request.PluginId, out var shutdownReason))
            return ViewerOperationResult.Denied("applicationQuiescing", shutdownReason);
        var profileId = ResolveOperationProfileId(request.ViewerProfileId, request.GroupHint);
        lock (GetOperationGate(profileId))
        {
            var preemption = preempt();
            if (!preemption.Success)
                return ViewerOperationResult.Denied("viewerOperationPreemptionFailed",
                    string.IsNullOrWhiteSpace(preemption.Message) ? "Viewer automatic state preemption failed." : preemption.Message);
            return EnsureTunedDetailedCore(request).Operation;
        }
    }

    internal ViewerOperationEnsureTunedResult EnsureTunedDetailed(ViewerOperationEnsureTunedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_applicationGate.TryAdmit("viewer_ensure_tuned", request.PluginId, out var shutdownReason))
            return new ViewerOperationEnsureTunedResult(
                ViewerOperationResult.Denied("applicationQuiescing", shutdownReason),
                null, null, null, null, false, false, "application_quiescing",
                false, false, false, false, shutdownReason, null, null);
        var profileId = ResolveOperationProfileId(request.ViewerProfileId, request.GroupHint);
        lock (GetOperationGate(profileId))
            return EnsureTunedDetailedCore(request);
    }

    private ViewerOperationEnsureTunedResult EnsureTunedDetailedCore(ViewerOperationEnsureTunedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ViewerOperationPlan? detailPlan = null;
        ViewerManagedLeaseScopeSnapshot? detailManagedScope = null;
        ViewerRetuneScopeDecision? detailRetuneScope = null;
        ExternalTunerLeaseDto? detailLease = null;
        bool detailLeaseReused = false;
        bool detailProcessRestarted = false;
        string detailRecoveryMode = "none";
        bool detailRetuneAttempted = false;
        bool detailRetuneTargetPrepared = false;
        bool detailRetuneSucceeded = false;
        bool detailRetuneProcessLost = false;
        string detailRetuneMessage = string.Empty;
        ViewerOwnershipStartResult? detailOwnershipStart = null;
        ViewerSessionState? detailSession = null;

        ViewerOperationEnsureTunedResult Finish(ViewerOperationResult operation)
            => new(
                operation,
                detailPlan,
                detailManagedScope,
                detailRetuneScope,
                detailLease,
                detailLeaseReused,
                detailProcessRestarted,
                detailRecoveryMode,
                detailRetuneAttempted,
                detailRetuneTargetPrepared,
                detailRetuneSucceeded,
                detailRetuneProcessLost,
                detailRetuneMessage,
                detailOwnershipStart,
                detailSession);

        var prepared = Prepare(new ViewerOperationPreparationRequest(
            request.PluginId,
            "ensureTuned",
            request.ViewerProfileId,
            null,
            null,
            request.NetworkId,
            request.TransportStreamId,
            request.ServiceId,
            request.ServiceName,
            request.GroupHint,
            request.PreserveViewerWindowState,
            request.ViewerActivation,
            !request.SuppressPrepareDiagnosticLog));
        if (!prepared.Success || prepared.Plan is null)
            return Finish(ViewerOperationResult.Denied(prepared.ErrorCode, prepared.Message));

        var plan = prepared.Plan;
        detailPlan = plan;
        if (!plan.PreserveViewerWindowState)
            _pendingWindowStateByProfile.TryRemove(plan.Profile.Id, out _);
        var managedLeaseScope = ViewerManagedLeaseScope.Resolve(
            _leases.GetActiveLeases(),
            plan.Profile,
            plan.ClientId);
        detailManagedScope = managedLeaseScope;
        DiscardPendingWindowStateOutsideCurrentLeaseScope(plan.Profile.Id, managedLeaseScope);
        var aliveManagedLease = managedLeaseScope.AliveManagedLease;
        PendingViewerWindowState? restartWindowState = null;

        foreach (var staleOwnership in managedLeaseScope.StaleOwnershipLeases)
        {
            if (staleOwnership.ProcessId is > 0 &&
                TvAirManagedProcessRegistry.TryGet(staleOwnership.ProcessId.Value, out var staleManaged) &&
                string.Equals(staleManaged.OwnershipId, staleOwnership.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                TvAirManagedProcessRegistry.Unregister(staleOwnership.ProcessId.Value);
            }
            _leases.Release(staleOwnership.LeaseId, "viewerEnsureTuned_stale_pool_lease_detach");
            if (!request.SuppressStaleDiagnosticLog)
            {
                _log.Add("VIEWER_STALE_POOL_LEASE_DETACHED", request.PluginId,
                    $"result=DETACHED leaseId={Safe(staleOwnership.LeaseId)} pid={staleOwnership.ProcessId?.ToString() ?? "-"} tuner={Safe(staleOwnership.TunerName)} " +
                    $"poolLeaseId={(staleOwnership.PoolLeaseId.HasValue ? staleOwnership.PoolLeaseId.Value.ToString("D") : "-")} occupancyGeneration={staleOwnership.OccupancyGeneration} " +
                    "processAction=none action=release_external_owner_only rule=release_contract");
            }
        }

        foreach (var stale in managedLeaseScope.StaleDeadLeases)
        {
            if (stale.ProcessId is > 0)
            {
                var stalePid = stale.ProcessId.Value;
                var delayedDeath = ViewerRetuneDelayedDeathAudit.MarkDetectedDead(stalePid);
                request.BeforeStaleLeaseRelease?.Invoke(new ViewerOperationEnsureTunedStaleLease(stale, delayedDeath));
                if (!request.SuppressStaleDiagnosticLog)
                {
                    _log.Add("VIEWER_RETUNE_DELAYED_DEATH_AUDIT", request.PluginId,
                        $"result={Safe(delayedDeath.Result)} stalePid={stalePid} leaseId={Safe(stale.LeaseId)} lastRetuneAt={Safe(delayedDeath.LastRetuneAtText)} detectedDeadAt={Safe(delayedDeath.DetectedDeadAtText)} elapsedSinceRetuneMs={Safe(delayedDeath.ElapsedMsText)} lastRetuneNid={Safe(delayedDeath.NetworkIdText)} lastRetuneTsid={Safe(delayedDeath.TransportStreamIdText)} lastRetuneSid={Safe(delayedDeath.ServiceIdText)} lastRetuneGroup={Safe(delayedDeath.Group)} lastRetuneDid={Safe(delayedDeath.Did)} lastRetuneBonDriver={Safe(delayedDeath.BonDriver)} reason=existing_process_not_alive_before_ensure_tuned rule=release_contract");
                }
                if (plan.PreserveViewerWindowState &&
                    _pendingWindowStateByProfile.TryGetValue(plan.Profile.Id, out var pendingForStale) &&
                    pendingForStale.SourceProcessId == stalePid &&
                    string.Equals(pendingForStale.SourceLeaseId, stale.LeaseId, StringComparison.OrdinalIgnoreCase) &&
                    _pendingWindowStateByProfile.TryRemove(plan.Profile.Id, out var consumedForStale))
                {
                    restartWindowState = consumedForStale;
                    _log.Add("VIEWER_PROFILE_WINDOW_STATE", request.PluginId,
                        $"viewerProfile={Safe(plan.Profile.Id)} previousPid={stalePid} newPid=- preserveViewerWindowState=True viewerActivation={Safe(plan.ViewerActivation)} windowStateCaptured={consumedForStale.Snapshot.Captured} capturedShowState={Safe(consumedForStale.Snapshot.State)} capturedBounds={consumedForStale.Snapshot.Left},{consumedForStale.Snapshot.Top},{consumedForStale.Snapshot.Width}x{consumedForStale.Snapshot.Height} capturedMonitor={consumedForStale.Snapshot.MonitorLeft},{consumedForStale.Snapshot.MonitorTop},{consumedForStale.Snapshot.MonitorWidth}x{consumedForStale.Snapshot.MonitorHeight} restoreWindowStateRequested=True restoreWindowStateApplied=False activationChanged=False stage=stale_process_restart_confirmed rule=viewer_window_state_contract");
                }
                TvAirManagedProcessRegistry.Unregister(stalePid);
                var staleReleaseReason = "viewerEnsureTuned_stale_viewer_lease_cleanup";
                _leases.Release(stale.LeaseId, staleReleaseReason);
                request.AfterStaleLeaseRelease?.Invoke(new ViewerOperationEnsureTunedStaleLease(stale, delayedDeath));
            }
        }

        request.BeforeLeaseRequest?.Invoke(new ViewerOperationEnsureTunedBeforeLeaseRequest(plan, managedLeaseScope));

        var leaseResult = _leases.Request(new ExternalTunerLeaseRequest
        {
            Group = plan.Group,
            RequiredGroup = plan.Group,
            Source = $"Plugin:{request.SourceDisplayName ?? request.PluginId}",
            ClientId = plan.ClientId,
            Note = $"{request.ActionName ?? "viewerEnsureTuned"} service={plan.ServiceName} nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId}",
            NetworkId = request.NetworkId,
            TransportStreamId = request.TransportStreamId,
            ServiceId = request.ServiceId,
            ChannelSpace = plan.Channel.ResolvedSpace,
            ChannelIndex = plan.Channel.ResolvedChannelIndex,
            ViewerProfileId = plan.Profile.Id,
            ViewerProfileName = plan.Profile.Name,
            LogicalViewerSlotId = plan.Profile.LogicalViewerSlotId,
            TvTestPathKey = plan.TvTestPathKey
        });
        if (!leaseResult.Success || leaseResult.Lease is null)
        {
            if (restartWindowState is not null && restartWindowState.Snapshot.Captured)
                _pendingWindowStateByProfile[plan.Profile.Id] = restartWindowState;
            var errorCode = string.IsNullOrWhiteSpace(leaseResult.Reason) ? "tunerUnavailable" : leaseResult.Reason;
            return Finish(ViewerOperationResult.Denied(errorCode!, "No viewer tuner is available."));
        }

        var lease = leaseResult.Lease;
        detailLease = lease;
        detailLeaseReused = leaseResult.Reused;
        var retuneScope = ViewerRetuneScope.Evaluate(aliveManagedLease, lease);
        detailRetuneScope = retuneScope;
        if (retuneScope.Preferred && retuneScope.ExistingProcessId > 0)
        {
            var processId = retuneScope.ExistingProcessId;
            if (plan.PreserveViewerWindowState)
            {
                var captured = _ownership.CaptureWindowState(processId, $"viewerProfile={plan.Profile.Id};before_retune");
                if (captured.Captured)
                {
                    _pendingWindowStateByProfile[plan.Profile.Id] = new PendingViewerWindowState(
                        plan.Profile.Id,
                        lease.LeaseId,
                        processId,
                        captured,
                        DateTimeOffset.Now);
                }
                else
                {
                    _pendingWindowStateByProfile.TryRemove(plan.Profile.Id, out _);
                }
                _log.Add("VIEWER_PROFILE_WINDOW_STATE", "Viewer",
                    $"viewerProfile={Safe(plan.Profile.Id)} previousPid={processId} newPid=- preserveViewerWindowState={plan.PreserveViewerWindowState} viewerActivation={Safe(plan.ViewerActivation)} windowStateCaptured={captured.Captured} capturedShowState={Safe(captured.State)} capturedBounds={captured.Left},{captured.Top},{captured.Width}x{captured.Height} capturedMonitor={captured.MonitorLeft},{captured.MonitorTop},{captured.MonitorWidth}x{captured.MonitorHeight} restoreWindowStateRequested=False restoreWindowStateApplied=False activationChanged=False stage=before_retune_restart_fallback_capture rule=viewer_window_state_contract");
            }
            detailRetuneAttempted = true;
            request.BeforeRetune?.Invoke(new ViewerOperationEnsureTunedBeforeRetune(
                plan,
                lease,
                aliveManagedLease!,
                retuneScope,
                leaseResult.Reused));
            detailRetuneTargetPrepared = true; // Exact profile-owned PID targeting needs no foreground preparation.
            request.AfterRetuneTargetPrepare?.Invoke(new ViewerOperationEnsureTunedAfterRetuneTargetPrepare(
                plan,
                lease,
                aliveManagedLease!,
                retuneScope,
                detailRetuneTargetPrepared,
                leaseResult.Reused));
            var retune = _ownership.RetuneExisting(
                lease.LeaseId,
                processId,
                lease.BonDriverFileName,
                lease.Did,
                plan.ChannelArgument,
                true,
                string.IsNullOrWhiteSpace(plan.ViewerActivation) ? "preserve" : plan.ViewerActivation,
                request.NetworkId,
                request.TransportStreamId,
                request.ServiceId,
                plan.Channel.ResolvedSpace,
                plan.Channel.ResolvedChannelIndex);
            detailRetuneMessage = retune.Retune.Message ?? string.Empty;
            if (retune.Success)
            {
                detailRetuneSucceeded = true;
                if (plan.PreserveViewerWindowState)
                {
                    var postRetuneCapture = _ownership.CaptureWindowState(processId, $"viewerProfile={plan.Profile.Id};after_retune");
                    if (postRetuneCapture.Captured)
                    {
                        _pendingWindowStateByProfile[plan.Profile.Id] = new PendingViewerWindowState(
                            plan.Profile.Id,
                            lease.LeaseId,
                            processId,
                            postRetuneCapture,
                            DateTimeOffset.Now);
                    }
                    else if (!_pendingWindowStateByProfile.TryGetValue(plan.Profile.Id, out var existingPending) ||
                             existingPending.SourceProcessId != processId ||
                             !string.Equals(existingPending.SourceLeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase))
                    {
                        _pendingWindowStateByProfile.TryRemove(plan.Profile.Id, out _);
                    }

                    var retained = _pendingWindowStateByProfile.TryGetValue(plan.Profile.Id, out var armedPending) &&
                                   armedPending.SourceProcessId == processId &&
                                   string.Equals(armedPending.SourceLeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase);
                    _log.Add("VIEWER_PROFILE_WINDOW_STATE", "Viewer",
                        $"viewerProfile={Safe(plan.Profile.Id)} previousPid={processId} newPid={processId} preserveViewerWindowState=True viewerActivation={Safe(plan.ViewerActivation)} windowStateCaptured={retained} capturedShowState={Safe(retained ? armedPending!.Snapshot.State : null)} capturedBounds={(retained ? $"{armedPending!.Snapshot.Left},{armedPending.Snapshot.Top},{armedPending.Snapshot.Width}x{armedPending.Snapshot.Height}" : "-")} capturedMonitor={(retained ? $"{armedPending!.Snapshot.MonitorLeft},{armedPending.Snapshot.MonitorTop},{armedPending.Snapshot.MonitorWidth}x{armedPending.Snapshot.MonitorHeight}" : "-")} restoreWindowStateRequested=False restoreWindowStateApplied=False activationChanged=False stage=retune_succeeded_delayed_death_snapshot_armed rule=viewer_window_state_contract");
                }
                ViewerRetuneDelayedDeathAudit.MarkRetuned(
                    processId,
                    request.NetworkId,
                    request.TransportStreamId,
                    request.ServiceId,
                    plan.Group,
                    lease.Did,
                    lease.BonDriverFileName);
                var sessions = _sessions.Synchronize(_leases.GetActiveLeases());
                var session = sessions.FirstOrDefault(x => string.Equals(x.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase));
                detailSession = session;
                var retuneDiagnostics = $"state=retuned;leaseId={lease.LeaseId};viewerProfileId={plan.Profile.Id};viewerSessionId={session?.ViewerSessionId ?? string.Empty};generation={session?.Generation ?? 0};tuner={lease.TunerName};did={lease.Did};bonDriver={lease.BonDriverFileName};viewerProcessId={processId};channelArgument={plan.ChannelArgument};identityArgument={plan.IdentityArgument};selectedChannelSource={plan.SelectedChannelSource};sameTransportServiceIds={plan.SameTransportServiceIds};launchResult=reused;tuneResult=pidTargetedRetuneCommandAccepted;tuneConfirmation=receiver_command_accepted;actualServiceIdentityVerified=False;activateResult=preserved;processRestarted=False;preserveViewerWindowState={plan.PreserveViewerWindowState};viewerActivation={plan.ViewerActivation};internalRetune=True;retuneScope=profileOwnedPidExact";
                var retuneOperation = ViewerOperationResult.Succeeded("retuned", session, lease) with
                {
                    Message = "Viewer accepted the in-process retune request.",
                    Diagnostics = retuneDiagnostics
                };
                request.AfterRetune?.Invoke(new ViewerOperationEnsureTunedAfterRetune(
                    plan,
                    lease,
                    aliveManagedLease!,
                    retuneScope,
                    leaseResult.Reused,
                    true,
                    false,
                    detailRetuneMessage,
                    session,
                    retuneOperation));
                return Finish(retuneOperation);
            }

            var retuneMessage = detailRetuneMessage;
            if (retune.CommandAccepted)
            {
                var activeLeasesAfterSyncFailure = _leases.GetActiveLeases();
                var retainedLease = activeLeasesAfterSyncFailure.FirstOrDefault(x =>
                    string.Equals(x.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase)) ?? lease;
                var sessionsAfterSyncFailure = _sessions.Synchronize(activeLeasesAfterSyncFailure);
                var retainedSession = sessionsAfterSyncFailure.FirstOrDefault(x =>
                    string.Equals(x.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase));
                detailLease = retainedLease;
                detailSession = retainedSession;

                var syncFailureOperation = ViewerOperationResult.Failed(
                    "viewerOwnershipAttachFailed",
                    "TVTest accepted the in-process retune request, but TvAIr could not synchronize the resulting viewer ownership state.",
                    retainedSession,
                    retainedLease,
                    $"state=partial_failure;errorCode=viewerOwnershipAttachFailed;attachFailureReason={retune.AttachFailureReason};viewerProcessId={processId};message={retuneMessage};retuneCommandAccepted=True;processRestarted=False;leasePreserved=True;viewerSessionPreserved={(retainedSession is not null)};targetProjectionApplied=False;actualServiceIdentityVerified=False;generationAdvanced=False;stateConsistency=uncertain;activationChanged=False");
                request.AfterRetune?.Invoke(new ViewerOperationEnsureTunedAfterRetune(
                    plan,
                    lease,
                    aliveManagedLease!,
                    retuneScope,
                    leaseResult.Reused,
                    false,
                    false,
                    retuneMessage,
                    retainedSession,
                    syncFailureOperation));
                return Finish(syncFailureOperation);
            }

            var processLost = ViewerRetuneFailureClassifier.IsProcessLost(retune.Retune.ErrorCode);
            detailRetuneProcessLost = processLost;
            if (!processLost)
            {
                if (plan.PreserveViewerWindowState &&
                    _pendingWindowStateByProfile.TryGetValue(plan.Profile.Id, out var pendingNoRestart) &&
                    pendingNoRestart.SourceProcessId == processId)
                    _pendingWindowStateByProfile.TryRemove(plan.Profile.Id, out _);
                if (!leaseResult.Reused)
                    _leases.Release(lease.LeaseId, "viewerEnsureTuned_retune_failed_no_restart");

                var preservedLeases = _leases.GetActiveLeases();
                var preservedLease = preservedLeases.FirstOrDefault(x =>
                    string.Equals(x.LeaseId, aliveManagedLease!.LeaseId, StringComparison.OrdinalIgnoreCase));
                var preservedSessions = _sessions.Synchronize(preservedLeases);
                var preservedSession = preservedSessions.FirstOrDefault(x =>
                    string.Equals(x.LeaseId, aliveManagedLease!.LeaseId, StringComparison.OrdinalIgnoreCase));
                detailLease = preservedLease ?? aliveManagedLease;
                detailSession = preservedSession;

                var failedRetuneOperation = ViewerOperationResult.Failed(
                    "internalRetuneFailed",
                    "Existing TVTest retune failed. TvAIr kept the existing viewer process and its previous viewer state unchanged.",
                    preservedSession,
                    preservedLease ?? aliveManagedLease,
                    $"state=failed;errorCode=internalRetuneFailed;viewerProcessId={processId};message={retuneMessage};processRestarted=False;leasePreserved=True;viewerSessionPreserved={(preservedSession is not null)};targetProjectionApplied=False;generationAdvanced=False;activationChanged=False");
                request.AfterRetune?.Invoke(new ViewerOperationEnsureTunedAfterRetune(
                    plan,
                    lease,
                    aliveManagedLease!,
                    retuneScope,
                    leaseResult.Reused,
                    false,
                    false,
                    retuneMessage,
                    preservedSession,
                    failedRetuneOperation));
                return Finish(failedRetuneOperation);
            }

            detailProcessRestarted = true;
            detailRecoveryMode = "processLostRestart";
            var processLostOperation = ViewerOperationResult.Failed(
                "internalRetuneFailed",
                "Existing TVTest retune process was lost; restart recovery will be attempted.",
                null,
                lease,
                $"state=denied;errorCode=internalRetuneFailed;viewerProcessId={processId};message={retuneMessage}");
            request.AfterRetune?.Invoke(new ViewerOperationEnsureTunedAfterRetune(
                plan,
                lease,
                aliveManagedLease!,
                retuneScope,
                leaseResult.Reused,
                false,
                true,
                retuneMessage,
                null,
                processLostOperation));
            if (plan.PreserveViewerWindowState &&
                _pendingWindowStateByProfile.TryGetValue(plan.Profile.Id, out var pendingForRestart) &&
                pendingForRestart.SourceProcessId == processId &&
                _pendingWindowStateByProfile.TryRemove(plan.Profile.Id, out var consumedForRestart))
            {
                restartWindowState = consumedForRestart;
            }
            TvAirManagedProcessRegistry.Unregister(processId);
        }

        request.BeforeStartNew?.Invoke(new ViewerOperationEnsureTunedBeforeStart(
            plan,
            lease,
            aliveManagedLease,
            detailProcessRestarted,
            detailRecoveryMode));

        var restoreWindowState = plan.PreserveViewerWindowState
            ? restartWindowState?.Snapshot
            : null;

        var ownership = _ownership.StartNew(
            lease.LeaseId,
            lease.BonDriverFileName,
            lease.Did,
            lease.Group,
            plan.ChannelArgument,
            plan.PreserveViewerWindowState,
            plan.ViewerActivation,
            restoreWindowState,
            request.NetworkId,
            request.TransportStreamId,
            request.ServiceId,
            plan.Channel.ResolvedSpace,
            plan.Channel.ResolvedChannelIndex);
        detailOwnershipStart = ownership;
        if (!ownership.Success)
        {
            if (restartWindowState is not null && restartWindowState.Snapshot.Captured)
                _pendingWindowStateByProfile[plan.Profile.Id] = restartWindowState;
            var failureMessage = ownership.FailureKind == ViewerOwnershipStartFailureKind.LaunchFailed
                ? "Failed to launch TVTest viewer."
                : "Failed to establish TVTest viewer ownership.";
            var failedStartOperation = ViewerOperationResult.Failed(
                ownership.Reason,
                failureMessage,
                null,
                lease,
                $"state=failed;errorCode={ownership.Reason};leaseId={lease.LeaseId};viewerProcessId={ownership.Launch.ProcessId};message={ownership.Launch.Message};leaseRelease={ownership.LeaseReleased}");
            request.AfterStartNew?.Invoke(new ViewerOperationEnsureTunedAfterStart(
                plan,
                lease,
                aliveManagedLease,
                detailProcessRestarted,
                detailRecoveryMode,
                ownership,
                null,
                failedStartOperation));
            return Finish(failedStartOperation);
        }

        var windowRestore = ownership.Launch.WindowRestore ?? ViewerWindowRestoreResult.NotRequested("launch_result_without_restore_result");
        _log.Add("VIEWER_PROFILE_WINDOW_STATE", "Viewer",
            $"viewerProfile={Safe(plan.Profile.Id)} previousPid={(restoreWindowState?.ProcessId.ToString() ?? "-")} newPid={ownership.Launch.ProcessId} preserveViewerWindowState={plan.PreserveViewerWindowState} viewerActivation={Safe(plan.ViewerActivation)} windowStateCaptured={restoreWindowState?.Captured == true} capturedShowState={Safe(restoreWindowState?.State)} capturedBounds={(restoreWindowState is null ? "-" : $"{restoreWindowState.Left},{restoreWindowState.Top},{restoreWindowState.Width}x{restoreWindowState.Height}")} capturedMonitor={(restoreWindowState is null ? "-" : $"{restoreWindowState.MonitorLeft},{restoreWindowState.MonitorTop},{restoreWindowState.MonitorWidth}x{restoreWindowState.MonitorHeight}")} restoreWindowStateRequested={windowRestore.Requested} restoreWindowStateApplied={windowRestore.Applied} restoredShowState={Safe(windowRestore.ShowState)} restoredBounds={(windowRestore.Applied ? $"{windowRestore.Left},{windowRestore.Top},{windowRestore.Width}x{windowRestore.Height}" : "-")} activationChanged={(string.Equals(plan.ViewerActivation, "activate", StringComparison.OrdinalIgnoreCase) && windowRestore.Applied && string.Equals(windowRestore.ShowState, "fullscreen", StringComparison.OrdinalIgnoreCase))} restoreMethod={Safe(windowRestore.Method)} restoreDiagnostics={Safe(windowRestore.Diagnostics)} stage=after_start rule=viewer_window_state_contract");

        var synchronized = _sessions.Synchronize(_leases.GetActiveLeases());
        var launchedSession = synchronized.FirstOrDefault(x => string.Equals(x.LeaseId, lease.LeaseId, StringComparison.OrdinalIgnoreCase));
        detailSession = launchedSession;
        if (launchedSession?.ProcessId is > 0)
            ArmViewerProcessExitWatch(launchedSession.ViewerProfileId, launchedSession.LeaseId, launchedSession.ProcessId.Value);

        // Viewer operation reports both a first launch and process-loss recovery as state=launched.
        // Keep the recovery fact in diagnostics instead of changing the externally observed state.
        var processRestarted = aliveManagedLease is not null;
        var diagnostics = $"state=launched;leaseId={lease.LeaseId};viewerProfileId={plan.Profile.Id};viewerSessionId={launchedSession?.ViewerSessionId ?? string.Empty};generation={launchedSession?.Generation ?? 0};tuner={lease.TunerName};did={lease.Did};bonDriver={lease.BonDriverFileName};viewerProcessId={ownership.Launch.ProcessId};channelArgument={plan.ChannelArgument};identityArgument={plan.IdentityArgument};selectedChannelSource={plan.SelectedChannelSource};sameTransportServiceIds={plan.SameTransportServiceIds};launchResult=started;tuneResult=argumentPassed;activateResult=requested;preserveViewerWindowState={plan.PreserveViewerWindowState};viewerActivation={plan.ViewerActivation};viewerProfile={plan.Profile.Id};viewerProfileName={plan.Profile.Name};tvTestPathKey={plan.TvTestPathKey};processRestarted={processRestarted};restoreWindowStateRequested={windowRestore.Requested};restoreWindowStateApplied={windowRestore.Applied};previousPid={(restoreWindowState?.ProcessId.ToString() ?? "-")};newPid={ownership.Launch.ProcessId};activationChanged={(string.Equals(plan.ViewerActivation, "activate", StringComparison.OrdinalIgnoreCase) && windowRestore.Applied && string.Equals(windowRestore.ShowState, "fullscreen", StringComparison.OrdinalIgnoreCase))}";
        var launchedOperation = ViewerOperationResult.Succeeded("launched", launchedSession, lease) with
        {
            Message = "Viewer launch requested by TvAIr host.",
            Diagnostics = diagnostics
        };
        request.AfterStartNew?.Invoke(new ViewerOperationEnsureTunedAfterStart(
            plan,
            lease,
            aliveManagedLease,
            detailProcessRestarted,
            detailRecoveryMode,
            ownership,
            launchedSession,
            launchedOperation));
        return Finish(launchedOperation);
    }


    /// <summary>
    /// Restarts the requested current Viewer Session while preserving its current service identity.
    /// The old session is closed and a new session is created for the same Viewer Profile.
    /// </summary>
    public ViewerOperationResult Restart(ViewerOperationRestartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_applicationGate.TryAdmit("viewer_restart", request.PluginId, out var shutdownReason))
            return ViewerOperationResult.Denied("applicationQuiescing", shutdownReason);
        var profileId = ResolveProfileIdForSession(request.ViewerSessionId);
        lock (GetOperationGate(profileId))
            return RestartCore(request);
    }

    private ViewerOperationResult RestartCore(ViewerOperationRestartRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ViewerSessionId))
            return ViewerOperationResult.Denied("viewerSessionRequired", "ViewerSessionId is required.");
        if (request.ExpectedGeneration <= 0)
            return ViewerOperationResult.Denied("viewerGenerationInvalid", "ExpectedGeneration must be greater than zero.");
        if (!TryNormalizeViewerActivation(request.ViewerActivation, out var viewerActivation))
            return ViewerOperationResult.Denied("viewerActivationInvalid", "ViewerActivation must be activate or preserve.");

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length > 256)
            return ViewerOperationResult.Denied("viewerRestartReasonTooLong", "Reason must be 256 characters or fewer.");

        _sessions.Synchronize(_leases.GetActiveLeases());
        var before = _sessions.GetBySessionId(request.ViewerSessionId.Trim());
        if (before is null)
            return ViewerOperationResult.Denied("viewerSessionNotFound", "Requested viewer session was not found.");
        if (before.Generation != request.ExpectedGeneration)
            return ViewerOperationResult.Denied("staleViewerGeneration", "Viewer session generation is stale.", before);

        var lease = _leases.GetActiveLeases().FirstOrDefault(x =>
            string.Equals(x.LeaseId, before.LeaseId, StringComparison.OrdinalIgnoreCase));
        if (lease is null)
            return ViewerOperationResult.Denied("viewerSessionLeaseNotActive", "Viewer session lease is not active.", before);
        if (lease.ProcessId is not > 0)
            return ViewerOperationResult.Failed("viewerProcessNotActive", "Viewer session has no active managed process.", before, lease);
        if (!lease.NetworkId.HasValue || !lease.TransportStreamId.HasValue || !lease.ServiceId.HasValue || !lease.ChannelSpace.HasValue || !lease.ChannelIndex.HasValue)
            return ViewerOperationResult.Failed("viewerServiceIdentityUnavailable", "Viewer session has no complete service identity.", before, lease);

        var restartPreflight = Prepare(new ViewerOperationPreparationRequest(
            request.PluginId,
            "restart",
            before.ViewerProfileId,
            before.ViewerSessionId,
            before.Generation,
            lease.NetworkId.Value,
            lease.TransportStreamId.Value,
            lease.ServiceId.Value,
            null,
            lease.Group,
            request.PreserveViewerWindowState,
            viewerActivation,
            EmitDiagnosticLog: false));
        if (!restartPreflight.Success || restartPreflight.Plan is null)
            return ViewerOperationResult.Failed(
                string.IsNullOrWhiteSpace(restartPreflight.ErrorCode) ? "viewerRestartPreflightFailed" : restartPreflight.ErrorCode,
                "The replacement Viewer could not be prepared before stopping the current Viewer.",
                before,
                lease,
                restartPreflight.Message);

        var preflightPlan = restartPreflight.Plan;
        var preflightMismatch = ViewerRestartSourceIdentity.DescribePlanMismatch(lease, preflightPlan);
        if (!string.Equals(preflightMismatch, "identity_match", StringComparison.Ordinal))
        {
            _log.Add("VIEWER_OPERATION_RESTART", "Viewer",
                $"result=FAILED stage=preflight viewerProfile={Safe(before.ViewerProfileId)} previousPid={lease.ProcessId.Value} reason={Safe(preflightMismatch)} rule=viewer_session_contract");
            return ViewerOperationResult.Failed(
                "viewerRestartPreflightIdentityMismatch",
                "The replacement Viewer could not preserve the current Viewer identity.",
                before,
                lease,
                preflightMismatch);
        }

        ViewerWindowStateSnapshot? captured = null;
        if (request.PreserveViewerWindowState)
        {
            var snapshot = _ownership.CaptureWindowState(lease.ProcessId.Value, "viewer_explicit_restart");
            if (snapshot.Captured)
                captured = snapshot;

            _log.Add("VIEWER_PROFILE_WINDOW_STATE", "Viewer",
                $"viewerProfile={Safe(before.ViewerProfileId)} previousPid={lease.ProcessId.Value} newPid=- preserveViewerWindowState=True viewerActivation={Safe(viewerActivation)} windowStateCaptured={snapshot.Captured} capturedShowState={Safe(snapshot.State)} capturedBounds={(snapshot.Captured ? $"{snapshot.Left},{snapshot.Top},{snapshot.Width}x{snapshot.Height}" : "-")} capturedMonitor={(snapshot.Captured ? $"{snapshot.MonitorLeft},{snapshot.MonitorTop},{snapshot.MonitorWidth}x{snapshot.MonitorHeight}" : "-")} restoreWindowStateRequested=False restoreWindowStateApplied=False activationChanged=False stage=explicit_restart_capture reason={Safe(snapshot.Diagnostics)} rule=viewer_window_state_contract");
        }

        var previousPid = lease.ProcessId.Value;
        DisarmViewerProcessExitWatch(lease.LeaseId, previousPid);
        var stopped = _ownership.Stop(lease, reason.Length == 0
            ? "ViewerOperationService.Restart"
            : reason);
        if (!stopped.Success)
        {
            if (IsViewerProcessAlive(previousPid))
                ArmViewerProcessExitWatch(before.ViewerProfileId, lease.LeaseId, previousPid);
            var stopDiagnostics = $"operation=restart;stage=stop_failed;previousViewerSessionId={Safe(before.ViewerSessionId)};previousGeneration={before.Generation};previousLeaseId={Safe(before.LeaseId)};previousPid={previousPid};windowStateCaptured={captured?.Captured == true};restoreWindowStateRequested=False;restoreWindowStateApplied=False;viewerActivation={Safe(viewerActivation)};activationChanged=False;stopReason={Safe(stopped.Reason)};stopDetails={Safe(string.Join(",", stopped.Details))}";
            _log.Add("VIEWER_OPERATION_RESTART", "Viewer",
                $"result=FAILED viewerProfile={Safe(before.ViewerProfileId)} previousViewerSession={Safe(before.ViewerSessionId)} previousPid={previousPid} stage=stop reason={Safe(stopped.Reason)} rule=viewer_session_contract");
            return ViewerOperationResult.Failed(stopped.Reason, "Viewer ownership stop failed.", before, lease, stopDiagnostics);
        }
        ViewerRetuneDelayedDeathAudit.MarkProcessEnded(previousPid);

        _sessions.Synchronize(_leases.GetActiveLeases());
        var lingeringSession = _sessions.GetBySessionId(before.ViewerSessionId);
        if (lingeringSession is not null)
        {
            var consistencyDiagnostics = $"operation=restart;stage=old_session_not_removed;previousViewerSessionId={Safe(before.ViewerSessionId)};previousGeneration={before.Generation};previousLeaseId={Safe(before.LeaseId)};previousPid={previousPid};windowStateCaptured={captured?.Captured == true};restoreWindowStateRequested=False;restoreWindowStateApplied=False;viewerActivation={Safe(viewerActivation)};activationChanged=False";
            _log.Add("VIEWER_OPERATION_RESTART", "Viewer",
                $"result=FAILED viewerProfile={Safe(before.ViewerProfileId)} previousViewerSession={Safe(before.ViewerSessionId)} previousPid={previousPid} stage=session_cleanup reason=old_session_not_removed rule=viewer_session_contract");
            return ViewerOperationResult.Failed("viewerSessionStillActiveAfterStop", "The previous Viewer Session remained after the Viewer was stopped.", lingeringSession, null, consistencyDiagnostics);
        }

        var restartSourceIdentity = new ViewerRestartSourceIdentity(
            before.ViewerProfileId,
            lease.LogicalViewerSlotId,
            lease.Group,
            lease.Did,
            lease.BonDriverFileName,
            lease.TvTestPathKey,
            lease.NetworkId.Value,
            lease.TransportStreamId.Value,
            lease.ServiceId.Value,
            lease.ChannelSpace.Value,
            lease.ChannelIndex.Value);

        ViewerWindowRestoreResult? restoreResult = null;
        var started = StartCore(new ViewerOperationStartRequest(
            request.PluginId,
            before.ViewerProfileId,
            lease.NetworkId.Value,
            lease.TransportStreamId.Value,
            lease.ServiceId.Value,
            null,
            lease.Group,
            request.PreserveViewerWindowState,
            viewerActivation),
            captured,
            result => restoreResult = result,
            restartSourceIdentity);

        var restoredBounds = restoreResult is { Applied: true } appliedRestore
            ? $"{appliedRestore.Left},{appliedRestore.Top},{appliedRestore.Width}x{appliedRestore.Height}"
            : "-";
        var activationChanged = string.Equals(viewerActivation, "activate", StringComparison.OrdinalIgnoreCase);
        var diagnostics = $"operation=restart;previousViewerSessionId={Safe(before.ViewerSessionId)};previousGeneration={before.Generation};previousLeaseId={Safe(before.LeaseId)};previousPid={previousPid};windowStateCaptured={captured?.Captured == true};capturedShowState={Safe(captured?.State)};capturedBounds={(captured is null ? "-" : $"{captured.Left},{captured.Top},{captured.Width}x{captured.Height}")};capturedMonitor={(captured is null ? "-" : $"{captured.MonitorLeft},{captured.MonitorTop},{captured.MonitorWidth}x{captured.MonitorHeight}")};restoreWindowStateRequested={restoreResult?.Requested == true};restoreWindowStateApplied={restoreResult?.Applied == true};restoredShowState={Safe(restoreResult?.ShowState)};restoredBounds={restoredBounds};restoreMethod={Safe(restoreResult?.Method)};restoreDiagnostics={Safe(restoreResult?.Diagnostics)};newViewerSessionId={Safe(started.ViewerSessionId)};newGeneration={started.Generation};newLeaseId={Safe(started.LeaseId)};newPid={(started.ProcessId?.ToString() ?? "-")};viewerActivation={Safe(viewerActivation)};activationChanged={activationChanged};startDiagnostics={Safe(started.Diagnostics)}";

        if (!started.Success)
        {
            _log.Add("VIEWER_OPERATION_RESTART", "Viewer",
                $"result=FAILED viewerProfile={Safe(before.ViewerProfileId)} previousViewerSession={Safe(before.ViewerSessionId)} previousPid={previousPid} newPid={(started.ProcessId?.ToString() ?? "-")} preserveViewerWindowState={request.PreserveViewerWindowState} viewerActivation={Safe(viewerActivation)} windowStateCaptured={captured?.Captured == true} restoreWindowStateRequested={restoreResult?.Requested == true} restoreWindowStateApplied={restoreResult?.Applied == true} activationChanged={activationChanged} stage=start reason={Safe(started.ErrorCode)} rule=viewer_session_contract");
            return started with
            {
                Message = "Viewer stopped, but the replacement Viewer Session could not be started.",
                Diagnostics = diagnostics
            };
        }

        var activeReplacementLease = _leases.GetActiveLeases().FirstOrDefault(x =>
            string.Equals(x.LeaseId, started.LeaseId, StringComparison.OrdinalIgnoreCase));
        var resultMismatch = restartSourceIdentity.DescribeResultMismatch(started, activeReplacementLease, before.ViewerSessionId);
        if (!string.Equals(resultMismatch, "identity_match", StringComparison.Ordinal))
        {
            ViewerOwnershipRollbackResult? rollback = null;
            if (started.ProcessId is > 0 && !string.IsNullOrWhiteSpace(started.LeaseId))
                rollback = _ownership.RollbackNewOwnership(started.LeaseId, started.ProcessId.Value, "viewer_restart_result_identity_mismatch");

            _sessions.Synchronize(_leases.GetActiveLeases());
            var mismatchDiagnostics = $"{diagnostics};resultIdentity={Safe(resultMismatch)};rollbackSuccess={rollback?.Success == true};rollbackProcessStop={Safe(rollback?.ProcessStopResult)};rollbackLeaseReleased={rollback?.LeaseReleased == true}";
            _log.Add("VIEWER_OPERATION_RESTART", "Viewer",
                $"result=FAILED viewerProfile={Safe(before.ViewerProfileId)} previousViewerSession={Safe(before.ViewerSessionId)} newViewerSession={Safe(started.ViewerSessionId)} previousPid={previousPid} newPid={(started.ProcessId?.ToString() ?? "-")} stage=result_validation reason={Safe(resultMismatch)} rollbackSuccess={rollback?.Success == true} rule=viewer_session_contract");
            return ViewerOperationResult.Failed(
                "viewerRestartResultIdentityMismatch",
                "The replacement Viewer started, but its resulting Session identity did not match the restarted Viewer.",
                null,
                activeReplacementLease,
                mismatchDiagnostics);
        }

        _log.Add("VIEWER_OPERATION_RESTART", "Viewer",
            $"result=OK viewerProfile={Safe(started.ViewerProfileId)} previousViewerSession={Safe(before.ViewerSessionId)} newViewerSession={Safe(started.ViewerSessionId)} previousPid={previousPid} newPid={(started.ProcessId?.ToString() ?? "-")} preserveViewerWindowState={request.PreserveViewerWindowState} viewerActivation={Safe(viewerActivation)} windowStateCaptured={captured?.Captured == true} restoreWindowStateRequested={restoreResult?.Requested == true} restoreWindowStateApplied={restoreResult?.Applied == true} restoredShowState={Safe(restoreResult?.ShowState)} restoredBounds={restoredBounds} activationChanged={activationChanged} rule=viewer_session_contract");

        return started with
        {
            State = "restarted",
            Message = "Viewer Session restarted.",
            Diagnostics = diagnostics
        };
    }

    /// <summary>
    /// Retunes exactly the requested current Viewer Session and advances its generation.
    /// </summary>
    public ViewerOperationResult Retune(ViewerOperationRetuneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var profileId = ResolveOperationProfileId(request.ViewerProfileId, request.GroupHint);
            lock (GetOperationGate(profileId))
                return RetuneCore(request);
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_OPERATION_EXCEPTION", "Viewer",
                $"stage=retune exceptionType={Safe(ex.GetType().FullName)} message={Safe(ex.Message)} viewerSessionId={Safe(request.ViewerSessionId)} viewerProfileId={Safe(request.ViewerProfileId)} expectedGeneration={request.ExpectedGeneration} nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId} rule=viewer_session_contract");
            return ViewerOperationResult.Failed("viewerOperationInternalError", "Viewer retune failed inside the host operation.", null, null,
                $"stage=retune;exceptionType={ex.GetType().FullName};message={ex.Message};viewerSessionId={request.ViewerSessionId};viewerProfileId={request.ViewerProfileId};expectedGeneration={request.ExpectedGeneration};nid={request.NetworkId};tsid={request.TransportStreamId};sid={request.ServiceId}");
        }
    }

    private ViewerOperationResult RetuneCore(ViewerOperationRetuneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = Prepare(new ViewerOperationPreparationRequest(
            request.PluginId,
            "retune",
            request.ViewerProfileId,
            request.ViewerSessionId,
            request.ExpectedGeneration,
            request.NetworkId,
            request.TransportStreamId,
            request.ServiceId,
            request.ServiceName,
            request.GroupHint,
            request.PreserveViewerWindowState,
            request.ViewerActivation));
        if (!prepared.Success || prepared.Plan is null)
            return ViewerOperationResult.Denied(prepared.ErrorCode, prepared.Message);

        var plan = prepared.Plan;
        var before = plan.Session!;
        var lease = _leases.GetActiveLeases().FirstOrDefault(x =>
            string.Equals(x.LeaseId, before.LeaseId, StringComparison.OrdinalIgnoreCase));
        if (lease is null)
            return ViewerOperationResult.Denied("viewerSessionLeaseNotActive", "Viewer session lease is not active.", before);
        if (!string.Equals(lease.ViewerProfileId, plan.Profile.Id, StringComparison.OrdinalIgnoreCase))
            return ViewerOperationResult.Denied("viewerSessionProfileMismatch", "Viewer session lease does not belong to the requested profile.", before);
        if (lease.ProcessId is not > 0)
            return RecoverStaleRetuneAndStart(request, before, lease, "missing_process_id");

        if (!IsViewerProcessAlive(lease.ProcessId.Value))
            return RecoverStaleRetuneAndStart(request, before, lease, "process_not_found");

        // The Viewer Session and active lease are the authoritative service identity.
        // Re-selecting the already tuned service is not a retune: it must not rewrite
        // TunerPool ownership, republish the lease, or advance the session generation.
        // ViewerActivation is the single source for foreground behavior on every path.
        // preserve (including null after normalization) must remain silent even when the
        // requested service is already tuned; only an explicit activate request may focus it.
        var alreadyTunedSameService =
            before.NetworkId == request.NetworkId &&
            before.TransportStreamId == request.TransportStreamId &&
            before.ServiceId == request.ServiceId &&
            lease.NetworkId == request.NetworkId &&
            lease.TransportStreamId == request.TransportStreamId &&
            lease.ServiceId == request.ServiceId;
        if (alreadyTunedSameService)
        {
            var activateRequested = string.Equals(plan.ViewerActivation, "activate", StringComparison.OrdinalIgnoreCase);
            if (activateRequested)
            {
                var activated = _ownership.ActivateExisting(lease.LeaseId, lease.ProcessId.Value);
                if (!activated.Success)
                {
                    _log.Add("VIEWER_OPERATION_NO_CHANGE", "Viewer",
                        $"result=FAILED reason=already_tuned_same_service_activate_failed viewerProfile={Safe(before.ViewerProfileId)} viewerSession={Safe(before.ViewerSessionId)} generation={before.Generation} expectedGeneration={request.ExpectedGeneration} leaseId={Safe(lease.LeaseId)} pid={lease.ProcessId} nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId} viewerActivation=activate diagnostics={Safe(activated.Message)} retune=False generationChanged=False rule=viewer_session_contract");
                    return ViewerOperationResult.Failed("viewerWindowActivateFailed", activated.Message, before, lease);
                }

                _log.Add("VIEWER_OPERATION_NO_CHANGE", "Viewer",
                    $"result=OK reason=already_tuned_same_service viewerProfile={Safe(before.ViewerProfileId)} viewerSession={Safe(before.ViewerSessionId)} generation={before.Generation} expectedGeneration={request.ExpectedGeneration} leaseId={Safe(lease.LeaseId)} pid={lease.ProcessId} nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId} viewerActivation=activate action=activate_only retune=False tunerPoolWrite=False ownershipRepublish=False generationChanged=False rule=viewer_session_contract");
                return ViewerOperationResult.Succeeded("no_change", before, lease) with
                {
                    Message = "Viewer is already tuned to the requested service; the existing window was activated.",
                    Diagnostics = "reason=already_tuned_same_service;viewerActivation=activate;action=activate_only;retune=False;generationChanged=False"
                };
            }

            _log.Add("VIEWER_OPERATION_NO_CHANGE", "Viewer",
                $"result=OK reason=already_tuned_same_service viewerProfile={Safe(before.ViewerProfileId)} viewerSession={Safe(before.ViewerSessionId)} generation={before.Generation} expectedGeneration={request.ExpectedGeneration} leaseId={Safe(lease.LeaseId)} pid={lease.ProcessId} nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId} viewerActivation=preserve action=none retune=False foregroundApplied=False tunerPoolWrite=False ownershipRepublish=False generationChanged=False rule=viewer_session_contract");
            return ViewerOperationResult.Succeeded("no_change", before, lease) with
            {
                Message = "Viewer is already tuned to the requested service.",
                Diagnostics = "reason=already_tuned_same_service;viewerActivation=preserve;action=none;retune=False;foregroundApplied=False;generationChanged=False"
            };
        }

        var ownership = _ownership.RetuneExisting(
            lease.LeaseId,
            lease.ProcessId.Value,
            lease.BonDriverFileName,
            lease.Did,
            plan.ChannelArgument,
            plan.PreserveViewerWindowState,
            plan.ViewerActivation,
            request.NetworkId,
            request.TransportStreamId,
            request.ServiceId,
            plan.Channel.ResolvedSpace,
            plan.Channel.ResolvedChannelIndex);
        if (!ownership.Success)
        {
            if (ViewerRetuneFailureClassifier.IsProcessLost(ownership.Retune.ErrorCode))
                return RecoverStaleRetuneAndStart(request, before, lease, "retune_process_lost");

            return ViewerOperationResult.Failed(ownership.Reason, "Viewer retune failed.", before, lease, ownership.Retune.Message);
        }

        var sessions = _sessions.Synchronize(_leases.GetActiveLeases());
        var after = sessions.FirstOrDefault(x =>
            string.Equals(x.ViewerSessionId, before.ViewerSessionId, StringComparison.OrdinalIgnoreCase));
        if (after is null)
            return ViewerOperationResult.Failed("viewerSessionLostAfterRetune", "Viewer session disappeared after retune.", before, lease);
        if (after.Generation <= before.Generation)
            return ViewerOperationResult.Failed("viewerGenerationNotAdvanced", "Viewer session generation did not advance after retune.", after, lease);

        var activeLease = _leases.GetActiveLeases().FirstOrDefault(x =>
            string.Equals(x.LeaseId, after.LeaseId, StringComparison.OrdinalIgnoreCase)) ?? lease;
        _log.Add("VIEWER_OPERATION_RETUNE", "Viewer",
            $"result=OK viewerProfile={Safe(after.ViewerProfileId)} viewerSession={Safe(after.ViewerSessionId)} previousGeneration={before.Generation} generation={after.Generation} leaseId={Safe(after.LeaseId)} pid={(after.ProcessId?.ToString() ?? "-")} nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId} viewerActivation={Safe(request.ViewerActivation)} foregroundAppliedByHost=False rule=viewer_session_contract");
        return ViewerOperationResult.Succeeded("retuned", after, activeLease);
    }

    private ViewerOperationResult RecoverStaleRetuneAndStart(
        ViewerOperationRetuneRequest request,
        ViewerSessionState staleSession,
        ExternalTunerLeaseDto staleLease,
        string reason)
    {
        var stalePid = staleLease.ProcessId.GetValueOrDefault();
        _log.Add("VIEWER_OPERATION_STALE_RECOVERY", "Viewer",
            $"stage=detected action=retune_to_start viewerProfile={Safe(staleSession.ViewerProfileId)} viewerSession={Safe(staleSession.ViewerSessionId)} generation={staleSession.Generation} leaseId={Safe(staleLease.LeaseId)} pid={(stalePid > 0 ? stalePid.ToString() : "-")} reason={Safe(reason)} rule=viewer_session_contract");

        if (stalePid > 0)
        {
            // The process is already proven absent on this path. Remove the exact exit watch first
            // so a dead Process/EventHandler cannot survive after its lease/session is terminal.
            DisarmViewerProcessExitWatch(staleLease.LeaseId, stalePid);
            TvAirManagedProcessRegistry.Unregister(stalePid);
            ViewerRetuneDelayedDeathAudit.MarkProcessEnded(stalePid);
        }

        var released = _leases.Release(staleLease.LeaseId, $"viewer_operation_stale_recovery:{reason}");
        var synchronized = _sessions.Synchronize(_leases.GetActiveLeases());
        var staleStillActive = synchronized.Any(x =>
            string.Equals(x.ViewerSessionId, staleSession.ViewerSessionId, StringComparison.OrdinalIgnoreCase));

        if (!released || staleStillActive)
        {
            _log.Add("VIEWER_OPERATION_STALE_RECOVERY", "Viewer",
                $"stage=release result=FAILED action=stop viewerProfile={Safe(staleSession.ViewerProfileId)} viewerSession={Safe(staleSession.ViewerSessionId)} leaseId={Safe(staleLease.LeaseId)} pid={(stalePid > 0 ? stalePid.ToString() : "-")} leaseReleased={released} sessionStillActive={staleStillActive} reason={Safe(reason)} rule=viewer_session_contract");
            return ViewerOperationResult.Failed(
                "viewerStaleRecoveryFailed",
                "The exited Viewer could not be released safely.",
                staleStillActive ? staleSession : null,
                released ? null : staleLease,
                $"reason={reason};leaseReleased={released};sessionStillActive={staleStillActive}");
        }

        _log.Add("VIEWER_OPERATION_STALE_RECOVERY", "Viewer",
            $"stage=release result=OK action=start_same_operation viewerProfile={Safe(staleSession.ViewerProfileId)} previousViewerSession={Safe(staleSession.ViewerSessionId)} previousGeneration={staleSession.Generation} previousLeaseId={Safe(staleLease.LeaseId)} previousPid={(stalePid > 0 ? stalePid.ToString() : "-")} reason={Safe(reason)} rule=viewer_session_contract");

        var started = StartCore(new ViewerOperationStartRequest(
            request.PluginId,
            request.ViewerProfileId,
            request.NetworkId,
            request.TransportStreamId,
            request.ServiceId,
            request.ServiceName,
            request.GroupHint,
            request.PreserveViewerWindowState,
            request.ViewerActivation));

        _log.Add("VIEWER_OPERATION_STALE_RECOVERY", "Viewer",
            $"stage=start result={(started.Success ? "OK" : "FAILED")} viewerProfile={Safe(request.ViewerProfileId)} previousViewerSession={Safe(staleSession.ViewerSessionId)} newViewerSession={Safe(started.ViewerSessionId)} previousPid={(stalePid > 0 ? stalePid.ToString() : "-")} newPid={(started.ProcessId?.ToString() ?? "-")} state={Safe(started.State)} errorCode={Safe(started.ErrorCode)} rule=viewer_session_contract");

        if (!started.Success)
            return started;

        return started with
        {
            State = "recovered_and_started",
            Message = "Exited Viewer ownership was recovered and a replacement Viewer was started in the same operation.",
            Diagnostics = $"recoveryReason={reason};previousViewerSessionId={staleSession.ViewerSessionId};previousLeaseId={staleLease.LeaseId};previousPid={(stalePid > 0 ? stalePid.ToString() : string.Empty)};{started.Diagnostics}"
        };
    }

    private static bool IsViewerProcessAlive(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public ViewerOperationResult Activate(ViewerOperationActivateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profileId = ResolveProfileIdForSession(request.ViewerSessionId);
        if (profileId.Length == 0)
            return ActivateCore(request);
        lock (GetOperationGate(profileId))
            return ActivateCore(request);
    }

    private ViewerOperationResult ActivateCore(ViewerOperationActivateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ViewerSessionId))
            return ViewerOperationResult.Denied("viewerSessionRequired", "ViewerSessionId is required.");
        var leases = _leases.GetActiveLeases().ToList();
        _sessions.Synchronize(leases);
        var session = _sessions.GetBySessionId(request.ViewerSessionId);
        if (session is null)
            return ViewerOperationResult.Denied("viewerSessionNotFound", "Requested viewer session was not found.");
        if (request.ExpectedGeneration != session.Generation)
            return ViewerOperationResult.Denied("staleViewerGeneration", "Viewer session generation is stale.", session);
        var lease = leases.FirstOrDefault(x => string.Equals(x.LeaseId, session.LeaseId, StringComparison.OrdinalIgnoreCase));
        if (lease is null)
            return ViewerOperationResult.Denied("viewerSessionLeaseNotActive", "Viewer session lease is not active.", session);
        if (lease.ProcessId is not > 0 || !IsViewerProcessAlive(lease.ProcessId.Value))
        {
            var stalePid = lease.ProcessId.GetValueOrDefault();
            if (stalePid > 0)
            {
                // Activation discovered an already-dead owned Viewer. Finalize the watcher together
                // with registry/lease/session state instead of leaving an event subscription behind.
                DisarmViewerProcessExitWatch(lease.LeaseId, stalePid);
                TvAirManagedProcessRegistry.Unregister(stalePid);
                ViewerRetuneDelayedDeathAudit.MarkProcessEnded(stalePid);
            }
            var released = _leases.Release(lease.LeaseId, "viewer_activate_stale_recovery");
            _sessions.Synchronize(_leases.GetActiveLeases());
            _log.Add("VIEWER_OPERATION_ACTIVATE", "Viewer",
                $"result=SKIPPED reason=viewer_process_exited_recovered viewerProfile={Safe(session.ViewerProfileId)} viewerSession={Safe(session.ViewerSessionId)} generation={session.Generation} leaseId={Safe(lease.LeaseId)} pid={(stalePid > 0 ? stalePid.ToString() : "-")} leaseReleased={released} retune=False generationChanged=False rule=viewer_window_activation_contract");
            return ViewerOperationResult.Denied("viewerProcessExitedRecovered", "The exited Viewer was released; there is no Viewer window to activate.");
        }
        var activated = _ownership.ActivateExisting(lease.LeaseId, lease.ProcessId.Value);
        if (!activated.Success)
        {
            var activationError = activated.Message switch
            {
                "viewerProcessExited" => "viewerProcessExited",
                "viewerWindowUnavailable" => "viewerWindowUnavailable",
                "foregroundActivationRejected" => "foregroundActivationRejected",
                _ => "viewerWindowActivateFailed"
            };
            _log.Add("VIEWER_OPERATION_ACTIVATE", "Viewer",
                $"result=FAILED viewerProfile={Safe(session.ViewerProfileId)} viewerSession={Safe(session.ViewerSessionId)} generation={session.Generation} leaseId={Safe(session.LeaseId)} pid={lease.ProcessId} error={Safe(activationError)} diagnostics={Safe(activated.Message)} retune=False generationChanged=False rule=viewer_window_activation_contract");
            return ViewerOperationResult.Failed(activationError, activated.Message, session, lease);
        }
        _log.Add("VIEWER_OPERATION_ACTIVATE", "Viewer",
            $"result=OK viewerProfile={Safe(session.ViewerProfileId)} viewerSession={Safe(session.ViewerSessionId)} generation={session.Generation} leaseId={Safe(session.LeaseId)} pid={lease.ProcessId} retune=False generationChanged=False rule=viewer_window_activation_contract");
        return ViewerOperationResult.Succeeded("activated", session, lease) with { Message = "Viewer window activated.", Diagnostics = "viewer_window_activated" };
    }

    /// <summary>
    /// Stops exactly the requested current Viewer Session. No profile/client/active-window fallback is used.
    /// </summary>
    public ViewerOperationResult Stop(ViewerOperationStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profileId = ResolveProfileIdForSession(request.ViewerSessionId);
        if (profileId.Length == 0)
            return StopCore(request);
        lock (GetOperationGate(profileId))
            return StopCore(request);
    }

    private ViewerOperationResult StopCore(ViewerOperationStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ViewerSessionId))
            return ViewerOperationResult.Denied("viewerSessionRequired", "ViewerSessionId is required.");

        var activeLeases = _leases.GetActiveLeases().ToList();
        _sessions.Synchronize(activeLeases);
        var session = _sessions.GetBySessionId(request.ViewerSessionId);
        if (session is null)
            return ViewerOperationResult.Denied("viewerSessionNotFound", "Requested viewer session was not found.");

        if (!string.IsNullOrWhiteSpace(request.ViewerProfileId) &&
            !string.Equals(session.ViewerProfileId, request.ViewerProfileId.Trim(), StringComparison.OrdinalIgnoreCase))
            return ViewerOperationResult.Denied("viewerSessionProfileMismatch", "Viewer session does not belong to the requested profile.");

        if (request.ExpectedGeneration.HasValue && request.ExpectedGeneration.Value != session.Generation)
            return ViewerOperationResult.Denied("staleViewerGeneration", "Viewer session generation is stale.", session);

        var lease = activeLeases.FirstOrDefault(x =>
            string.Equals(x.LeaseId, session.LeaseId, StringComparison.OrdinalIgnoreCase));
        if (lease is null)
            return ViewerOperationResult.Denied("viewerSessionLeaseNotActive", "Viewer session lease is not active.", session);

        var stopPid = lease.ProcessId.GetValueOrDefault();
        DisarmViewerProcessExitWatch(lease.LeaseId, stopPid);
        var stopped = _ownership.Stop(lease, string.IsNullOrWhiteSpace(request.Reason) ? "ViewerOperationService.Stop" : request.Reason.Trim());
        if (!stopped.Success)
        {
            if (stopPid > 0 && IsViewerProcessAlive(stopPid))
                ArmViewerProcessExitWatch(session.ViewerProfileId, lease.LeaseId, stopPid);
            return ViewerOperationResult.Failed(stopped.Reason, "Viewer ownership stop failed.", session, lease, string.Join(",", stopped.Details));
        }

        if (lease.ProcessId is > 0)
            ViewerRetuneDelayedDeathAudit.MarkProcessEnded(lease.ProcessId.Value);

        var after = _sessions.Synchronize(_leases.GetActiveLeases());
        var stillActive = after.Any(x => string.Equals(x.ViewerSessionId, session.ViewerSessionId, StringComparison.OrdinalIgnoreCase));
        if (stillActive)
            return ViewerOperationResult.Failed("viewerSessionStillActive", "Viewer session remained active after stop.", session, lease);

        _pendingWindowStateByProfile.TryRemove(session.ViewerProfileId, out _);
        _log.Add("VIEWER_OPERATION_STOP", "Viewer",
            $"result=OK viewerProfile={Safe(session.ViewerProfileId)} viewerSession={Safe(session.ViewerSessionId)} generation={session.Generation} leaseId={Safe(session.LeaseId)} rule=viewer_session_contract");
        return ViewerOperationResult.Succeeded("stopped", session, lease);
    }


    /// <summary>
    /// Stops a viewer by resolving the current target in session, lease, then profile order.
    /// Active-window and generic-client fallbacks are not used.
    /// </summary>
    public ViewerOperationResult StopCompatible(ViewerOperationCompatibleStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profileId = ResolveProfileIdForCompatibleStop(request);
        if (profileId.Length == 0)
            return StopCompatibleCore(request);
        lock (GetOperationGate(profileId))
            return StopCompatibleCore(request);
    }

    private ViewerOperationResult StopCompatibleCore(ViewerOperationCompatibleStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pluginId = PluginIdentity.Normalize(request.PluginId, "Plugin");
        var pluginName = string.IsNullOrWhiteSpace(request.PluginDisplayName) ? pluginId : request.PluginDisplayName.Trim();
        var requestedLeaseId = (request.LeaseId ?? string.Empty).Trim();
        var requestedProfileId = (request.ViewerProfileId ?? string.Empty).Trim();
        var requestedSessionId = (request.ViewerSessionId ?? string.Empty).Trim();
        var hasProfile = requestedProfileId.Length > 0;
        var activeBefore = _leases.GetActiveLeases().ToList();
        _sessions.Synchronize(activeBefore);
        ExternalTunerLeaseDto? target = null;
        ViewerSessionState? session = null;
        var resolveMode = "none";
        var deniedReason = string.Empty;

        if (request.ExpectedGeneration.HasValue && requestedSessionId.Length == 0)
            return ViewerOperationResult.Denied("viewerSessionRequired", "ExpectedGeneration requires ViewerSessionId.");

        if (requestedSessionId.Length > 0)
        {
            session = _sessions.GetBySessionId(requestedSessionId);
            if (session is null)
                return ViewerOperationResult.Denied("viewerSessionNotFound", "Requested viewer session was not found.");
            if (hasProfile && !string.Equals(session.ViewerProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase))
                return ViewerOperationResult.Denied("viewerSessionProfileMismatch", "Viewer session does not belong to the requested profile.", session);
            if (request.ExpectedGeneration.HasValue && request.ExpectedGeneration.Value != session.Generation)
                return ViewerOperationResult.Denied("staleViewerGeneration", "Viewer session generation is stale.", session);

            target = activeBefore.FirstOrDefault(x => string.Equals(x.LeaseId, session.LeaseId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return ViewerOperationResult.Denied("viewerSessionLeaseNotActive", "Viewer session lease is no longer active.", session);
            if (requestedLeaseId.Length > 0 && !string.Equals(requestedLeaseId, session.LeaseId, StringComparison.OrdinalIgnoreCase))
                return ViewerOperationResult.Denied("viewerSessionLeaseMismatch", "Viewer session does not own the requested lease.", session);
            resolveMode = "viewerSession_generation_verified";
        }

        if (target is null && requestedLeaseId.Length > 0)
        {
            var byId = activeBefore.FirstOrDefault(x => string.Equals(x.LeaseId, requestedLeaseId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null && hasProfile && !string.Equals(byId.ViewerProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase))
            {
                resolveMode = "leaseId_profile_mismatch_denied";
                deniedReason = $"lease_profile_mismatch requestedProfile={requestedProfileId} leaseProfile={byId.ViewerProfileId}";
            }
            else if (byId is not null)
            {
                target = byId;
                resolveMode = hasProfile ? "leaseId_profile_verified" : "leaseId_no_profile_legacy";
            }
            else if (hasProfile)
            {
                resolveMode = "stale_lease_profile_fallback";
            }
            else
            {
                resolveMode = "stale_lease_no_profile_denied";
                deniedReason = "stale_lease_without_viewer_profile";
            }
        }

        if (target is null && deniedReason.Length == 0 && hasProfile)
        {
            var expectedClientId = ViewerProfileContract.BuildViewerClientId(pluginId, requestedProfileId);
            target = activeBefore
                .Where(x => string.Equals(x.ViewerProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.Equals(x.ClientId, expectedClientId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.AcquiredAt)
                .FirstOrDefault();
            if (target is not null)
                resolveMode = resolveMode == "stale_lease_profile_fallback" ? "stale_lease_profile_resolved" : "viewerProfile";
        }

        if (target is null)
        {
            if (deniedReason.Length == 0)
                deniedReason = hasProfile ? "viewer_profile_session_not_found" : "missing_viewer_profile_and_lease";
            var failed = ViewerOperationResult.Failed(
                "viewerLeaseNotFoundOrProfileMismatch",
                "Viewer lease not found or viewerProfile mismatch.",
                session,
                null,
                $"lease_not_found_or_profile_mismatch;leaseResolveMode={resolveMode};reason={deniedReason};viewerProfile={requestedProfileId};viewerSessionId={requestedSessionId}");
            return failed;
        }

        var compatibleStopPid = target.ProcessId.GetValueOrDefault();
        DisarmViewerProcessExitWatch(target.LeaseId, compatibleStopPid);
        var stopped = _ownership.Stop(target, string.IsNullOrWhiteSpace(request.Reason)
            ? $"Plugin:{pluginName}:viewerOperationStop:{resolveMode}"
            : request.Reason.Trim());
        var stopDiag = stopped.Details.Count > 0 ? string.Join(",", stopped.Details) : stopped.Reason;
        if (!stopped.Success)
        {
            if (compatibleStopPid > 0 && IsViewerProcessAlive(compatibleStopPid))
                ArmViewerProcessExitWatch(target.ViewerProfileId, target.LeaseId, compatibleStopPid);
            return ViewerOperationResult.Failed(stopped.Reason, "Viewer ownership stop failed.", session, target, stopDiag);
        }
        if (target.ProcessId is > 0)
            ViewerRetuneDelayedDeathAudit.MarkProcessEnded(target.ProcessId.Value);

        var activeAfterLeases = _leases.GetActiveLeases().ToList();
        var after = _sessions.Synchronize(activeAfterLeases);
        var stoppedSessionId = session?.ViewerSessionId ?? _sessions.GetByLeaseId(target.LeaseId)?.ViewerSessionId ?? string.Empty;
        var stillActive = stoppedSessionId.Length > 0 && after.Any(x => string.Equals(x.ViewerSessionId, stoppedSessionId, StringComparison.OrdinalIgnoreCase));
        if (stillActive)
            return ViewerOperationResult.Failed("viewerSessionStillActive", "Viewer session remained active after stop.", session, target,
                $"state=stopped;processStop={stopDiag};leaseResolveMode={resolveMode};viewerProfileId={requestedProfileId};viewerSessionId={stoppedSessionId};generation=0;sessionClosed=False");

        var stoppedProfileId = !string.IsNullOrWhiteSpace(target.ViewerProfileId) ? target.ViewerProfileId : requestedProfileId;
        if (!string.IsNullOrWhiteSpace(stoppedProfileId))
            _pendingWindowStateByProfile.TryRemove(stoppedProfileId, out _);
        var resultSession = session ?? ViewerSessionState.FromLease(stoppedSessionId, 0, target);
        var success = ViewerOperationResult.Succeeded("stopped", resultSession, target) with
        {
            Message = "Viewer lease released.",
            Diagnostics = $"state=stopped;processStop={stopDiag};leaseResolveMode={resolveMode};viewerProfileId={requestedProfileId};viewerSessionId={stoppedSessionId};generation=0;sessionClosed=True"
        };
        return success;
    }

    /// <summary>
    /// A TvAIr-owned Viewer must release its logical tuner ownership as soon as its exact process exits.
    /// Periodic reconciliation remains a recovery audit, not the normal process-termination path.
    /// </summary>
    private void ArmViewerProcessExitWatch(string viewerProfileId, string leaseId, int processId)
    {
        if (string.IsNullOrWhiteSpace(leaseId) || processId <= 0)
            return;

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(processId);
        }
        catch
        {
            HandleViewerProcessExited(new ViewerProcessExitWatch(viewerProfileId, leaseId, processId, null, null));
            return;
        }

        ViewerProcessExitWatch? watch = null;
        EventHandler handler = (_, _) =>
        {
            var captured = watch;
            if (captured is not null)
                HandleViewerProcessExited(captured);
        };
        watch = new ViewerProcessExitWatch(viewerProfileId, leaseId, processId, process, handler);

        var previous = _viewerProcessExitWatches.AddOrUpdate(
            leaseId,
            watch,
            (_, existing) =>
            {
                DisposeViewerProcessExitWatch(existing);
                return watch;
            });
        if (!ReferenceEquals(previous, watch))
            return;

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += handler;
            if (process.HasExited)
                HandleViewerProcessExited(watch);
        }
        catch
        {
            if (((ICollection<KeyValuePair<string, ViewerProcessExitWatch>>)_viewerProcessExitWatches).Remove(new KeyValuePair<string, ViewerProcessExitWatch>(leaseId, watch)))
                DisposeViewerProcessExitWatch(watch);
            HandleViewerProcessExited(new ViewerProcessExitWatch(viewerProfileId, leaseId, processId, null, null));
        }
    }

    private void HandleViewerProcessExited(ViewerProcessExitWatch watch)
    {
        if (string.IsNullOrWhiteSpace(watch.LeaseId) || watch.ProcessId <= 0)
            return;

        lock (GetOperationGate(watch.ViewerProfileId))
        {
            if (watch.Process is not null)
            {
                if (!_viewerProcessExitWatches.TryGetValue(watch.LeaseId, out var current) || !ReferenceEquals(current, watch))
                    return;
                ((ICollection<KeyValuePair<string, ViewerProcessExitWatch>>)_viewerProcessExitWatches).Remove(new KeyValuePair<string, ViewerProcessExitWatch>(watch.LeaseId, watch));
            }

            var activeLease = _leases.GetActiveLeases().FirstOrDefault(x =>
                string.Equals(x.LeaseId, watch.LeaseId, StringComparison.OrdinalIgnoreCase) &&
                x.ProcessId == watch.ProcessId);
            if (activeLease is null)
            {
                DisposeViewerProcessExitWatch(watch);
                return;
            }

            if (TvAirManagedProcessRegistry.TryGet(watch.ProcessId, out var managed) &&
                managed.IsViewer &&
                string.Equals(managed.OwnershipId, watch.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                TvAirManagedProcessRegistry.Unregister(watch.ProcessId);
            }

            var released = _leases.Release(watch.LeaseId, "viewer_process_exited");
            _sessions.Synchronize(_leases.GetActiveLeases());
            if (_pendingWindowStateByProfile.TryGetValue(watch.ViewerProfileId, out var pending) &&
                pending.SourceProcessId == watch.ProcessId &&
                string.Equals(pending.SourceLeaseId, watch.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                _pendingWindowStateByProfile.TryRemove(watch.ViewerProfileId, out _);
            }

            _log.Add("VIEWER_PROCESS_EXIT_RELEASE", "Viewer",
                $"result={(released ? "RELEASED" : "NO_ACTIVE_LEASE")} viewerProfile={Safe(watch.ViewerProfileId)} leaseId={Safe(watch.LeaseId)} pid={watch.ProcessId} action=release_exact_managed_viewer_ownership periodicReconcile=backup_only rule=viewer_session_contract");
            DisposeViewerProcessExitWatch(watch);
        }
    }

    private void DisarmViewerProcessExitWatch(string leaseId, int processId)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return;
        if (!_viewerProcessExitWatches.TryGetValue(leaseId, out var current))
            return;
        if (processId > 0 && current.ProcessId != processId)
            return;
        if (((ICollection<KeyValuePair<string, ViewerProcessExitWatch>>)_viewerProcessExitWatches).Remove(new KeyValuePair<string, ViewerProcessExitWatch>(leaseId, current)))
            DisposeViewerProcessExitWatch(current);
    }

    private static void DisposeViewerProcessExitWatch(ViewerProcessExitWatch watch)
    {
        if (watch.Process is null)
            return;
        try
        {
            if (watch.Handler is not null)
                watch.Process.Exited -= watch.Handler;
            watch.Process.EnableRaisingEvents = false;
        }
        catch { }
        try { watch.Process.Dispose(); } catch { }
    }

    private object GetOperationGate(string viewerProfileId)
    {
        var key = string.IsNullOrWhiteSpace(viewerProfileId) ? "__unresolved_viewer_profile__" : viewerProfileId.Trim();
        return _operationGateByProfile.GetOrAdd(key, static _ => new object());
    }

    private string ResolveOperationProfileId(string? viewerProfileId, string? groupHint)
        => ViewerProfileContract.ResolveRequestedProfile(
            viewerProfileId,
            groupHint,
            _tvTestOptions.Value,
            _ini,
            _tunerProfiles).Id;

    private string ResolveProfileIdForSession(string? viewerSessionId)
    {
        if (string.IsNullOrWhiteSpace(viewerSessionId))
            return string.Empty;
        _sessions.Synchronize(_leases.GetActiveLeases());
        return _sessions.GetBySessionId(viewerSessionId.Trim())?.ViewerProfileId ?? string.Empty;
    }

    private string ResolveProfileIdForCompatibleStop(ViewerOperationCompatibleStopRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ViewerProfileId))
            return request.ViewerProfileId.Trim();

        var activeLeases = _leases.GetActiveLeases().ToList();
        _sessions.Synchronize(activeLeases);
        if (!string.IsNullOrWhiteSpace(request.ViewerSessionId))
        {
            var session = _sessions.GetBySessionId(request.ViewerSessionId.Trim());
            if (session is not null)
                return session.ViewerProfileId;
        }

        if (!string.IsNullOrWhiteSpace(request.LeaseId))
        {
            var lease = activeLeases.FirstOrDefault(x => string.Equals(x.LeaseId, request.LeaseId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (lease is not null)
                return lease.ViewerProfileId;
        }

        return string.Empty;
    }

    private void DiscardPendingWindowStateOutsideCurrentLeaseScope(
        string viewerProfileId,
        ViewerManagedLeaseScopeSnapshot managedLeaseScope)
    {
        if (!_pendingWindowStateByProfile.TryGetValue(viewerProfileId, out var pending))
            return;

        var stillOwnedByCurrentScope = managedLeaseScope.ScopedLeases.Any(lease =>
            string.Equals(lease.LeaseId, pending.SourceLeaseId, StringComparison.OrdinalIgnoreCase) &&
            lease.ProcessId == pending.SourceProcessId &&
            string.Equals(lease.ViewerProfileId, viewerProfileId, StringComparison.OrdinalIgnoreCase));
        if (stillOwnedByCurrentScope)
            return;

        if (!_pendingWindowStateByProfile.TryRemove(viewerProfileId, out var discarded))
            return;

        _log.Add("VIEWER_PROFILE_WINDOW_STATE", "Viewer",
            $"viewerProfile={Safe(viewerProfileId)} previousPid={discarded.SourceProcessId} newPid=- preserveViewerWindowState=True viewerActivation=- windowStateCaptured={discarded.Snapshot.Captured} capturedShowState={Safe(discarded.Snapshot.State)} capturedBounds={discarded.Snapshot.Left},{discarded.Snapshot.Top},{discarded.Snapshot.Width}x{discarded.Snapshot.Height} capturedMonitor={discarded.Snapshot.MonitorLeft},{discarded.Snapshot.MonitorTop},{discarded.Snapshot.MonitorWidth}x{discarded.Snapshot.MonitorHeight} restoreWindowStateRequested=False restoreWindowStateApplied=False activationChanged=False stage=orphan_snapshot_discarded reason=source_lease_not_in_current_profile_scope rule=viewer_window_state_contract");
    }

    private static bool TryNormalizeViewerActivation(string? value, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value)
            ? "preserve"
            : value.Trim().ToLowerInvariant();
        return normalized is "activate" or "preserve";
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace('\r', ' ').Replace('\n', ' ');
}


internal sealed record ViewerRestartSourceIdentity(
    string ViewerProfileId,
    string LogicalViewerSlotId,
    string Group,
    string Did,
    string BonDriverFileName,
    string? TvTestPathKey,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    int ChannelSpace,
    int ChannelIndex)
{
    public bool Matches(ExternalTunerLeaseDto lease)
        => string.Equals(ViewerProfileId, lease.ViewerProfileId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(LogicalViewerSlotId, lease.LogicalViewerSlotId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(NormalizeGroup(Group), NormalizeGroup(lease.Group), StringComparison.OrdinalIgnoreCase)
           && string.Equals(Did, lease.Did, StringComparison.OrdinalIgnoreCase)
           && string.Equals(NormalizeBonDriver(BonDriverFileName), NormalizeBonDriver(lease.BonDriverFileName), StringComparison.OrdinalIgnoreCase)
           && string.Equals(NormalizePath(TvTestPathKey), NormalizePath(lease.TvTestPathKey), StringComparison.OrdinalIgnoreCase)
           && NetworkId == lease.NetworkId
           && TransportStreamId == lease.TransportStreamId
           && ServiceId == lease.ServiceId
           && ChannelSpace == lease.ChannelSpace
           && ChannelIndex == lease.ChannelIndex;

    public string DescribeMismatch(ExternalTunerLeaseDto lease)
    {
        var mismatches = new List<string>();
        if (!string.Equals(ViewerProfileId, lease.ViewerProfileId, StringComparison.OrdinalIgnoreCase)) mismatches.Add("viewer_profile_mismatch");
        if (!string.Equals(LogicalViewerSlotId, lease.LogicalViewerSlotId, StringComparison.OrdinalIgnoreCase)) mismatches.Add("logical_viewer_slot_mismatch");
        if (!string.Equals(NormalizeGroup(Group), NormalizeGroup(lease.Group), StringComparison.OrdinalIgnoreCase)) mismatches.Add("group_mismatch");
        if (!string.Equals(Did, lease.Did, StringComparison.OrdinalIgnoreCase)) mismatches.Add("did_mismatch");
        if (!string.Equals(NormalizeBonDriver(BonDriverFileName), NormalizeBonDriver(lease.BonDriverFileName), StringComparison.OrdinalIgnoreCase)) mismatches.Add("bondriver_mismatch");
        if (!string.Equals(NormalizePath(TvTestPathKey), NormalizePath(lease.TvTestPathKey), StringComparison.OrdinalIgnoreCase)) mismatches.Add("tvtest_path_mismatch");
        if (NetworkId != lease.NetworkId) mismatches.Add("network_id_mismatch");
        if (TransportStreamId != lease.TransportStreamId) mismatches.Add("transport_stream_id_mismatch");
        if (ServiceId != lease.ServiceId) mismatches.Add("service_id_mismatch");
        if (ChannelSpace != lease.ChannelSpace) mismatches.Add("channel_space_mismatch");
        if (ChannelIndex != lease.ChannelIndex) mismatches.Add("channel_index_mismatch");
        return mismatches.Count == 0 ? "identity_match" : string.Join(",", mismatches);
    }

    public string DescribeResultMismatch(
        ViewerOperationResult result,
        ExternalTunerLeaseDto? activeLease,
        string previousViewerSessionId)
    {
        var mismatches = new List<string>();
        if (string.IsNullOrWhiteSpace(result.ViewerSessionId)) mismatches.Add("viewer_session_missing");
        else if (string.Equals(result.ViewerSessionId, previousViewerSessionId, StringComparison.OrdinalIgnoreCase)) mismatches.Add("viewer_session_reused");
        if (result.Generation <= 0) mismatches.Add("generation_invalid");
        if (string.IsNullOrWhiteSpace(result.LeaseId)) mismatches.Add("lease_missing");
        if (result.ProcessId is not > 0) mismatches.Add("process_missing");
        if (!string.Equals(ViewerProfileId, result.ViewerProfileId, StringComparison.OrdinalIgnoreCase)) mismatches.Add("viewer_profile_mismatch");
        if (!string.Equals(LogicalViewerSlotId, result.LogicalViewerSlotId, StringComparison.OrdinalIgnoreCase)) mismatches.Add("logical_viewer_slot_mismatch");
        if (NetworkId != result.NetworkId) mismatches.Add("network_id_mismatch");
        if (TransportStreamId != result.TransportStreamId) mismatches.Add("transport_stream_id_mismatch");
        if (ServiceId != result.ServiceId) mismatches.Add("service_id_mismatch");
        if (ChannelSpace != result.ChannelSpace) mismatches.Add("channel_space_mismatch");
        if (ChannelIndex != result.ChannelIndex) mismatches.Add("channel_index_mismatch");
        if (activeLease is null) mismatches.Add("active_lease_missing");
        else
        {
            if (!string.Equals(result.LeaseId, activeLease.LeaseId, StringComparison.OrdinalIgnoreCase)) mismatches.Add("result_lease_mismatch");
            if (result.ProcessId != activeLease.ProcessId) mismatches.Add("result_process_mismatch");
            var leaseMismatch = DescribeMismatch(activeLease);
            if (!string.Equals(leaseMismatch, "identity_match", StringComparison.Ordinal))
                mismatches.Add($"active_lease_{leaseMismatch}");
        }
        return mismatches.Count == 0 ? "identity_match" : string.Join(",", mismatches);
    }

    public static string DescribePlanMismatch(ExternalTunerLeaseDto lease, ViewerOperationPlan plan)
    {
        var mismatches = new List<string>();
        if (!string.Equals(lease.ViewerProfileId, plan.Profile.Id, StringComparison.OrdinalIgnoreCase)) mismatches.Add("viewer_profile_mismatch");
        if (!string.Equals(lease.LogicalViewerSlotId, plan.Profile.LogicalViewerSlotId, StringComparison.OrdinalIgnoreCase)) mismatches.Add("logical_viewer_slot_mismatch");
        if (!string.Equals(NormalizeGroup(lease.Group), NormalizeGroup(plan.Group), StringComparison.OrdinalIgnoreCase)) mismatches.Add("group_mismatch");
        if (!string.Equals(NormalizePath(lease.TvTestPathKey), NormalizePath(plan.TvTestPathKey), StringComparison.OrdinalIgnoreCase)) mismatches.Add("tvtest_path_mismatch");
        if (lease.ChannelSpace != plan.Channel.ResolvedSpace) mismatches.Add("channel_space_mismatch");
        if (lease.ChannelIndex != plan.Channel.ResolvedChannelIndex) mismatches.Add("channel_index_mismatch");
        return mismatches.Count == 0 ? "identity_match" : string.Join(",", mismatches);
    }

    private static string NormalizeGroup(string? value)
    {
        var group = (value ?? string.Empty).Trim().ToUpperInvariant();
        return group is "BS" or "CS" or "BS/CS" or "BSCS" ? "BSCS"
            : group is "地上波" or "GR" or "GROUND" ? "GR"
            : group;
    }

    private static string NormalizeBonDriver(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFileName(value.Trim());

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Path.GetFullPath(value.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return value.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }
}

internal sealed record ViewerProcessExitWatch(
    string ViewerProfileId,
    string LeaseId,
    int ProcessId,
    System.Diagnostics.Process? Process,
    EventHandler? Handler);

internal sealed record PendingViewerWindowState(
    string ViewerProfileId,
    string SourceLeaseId,
    int SourceProcessId,
    ViewerWindowStateSnapshot Snapshot,
    DateTimeOffset CapturedAt);

public sealed record ViewerOperationPreparationRequest(
    string PluginId,
    string Mode,
    string? ViewerProfileId,
    string? ViewerSessionId,
    long? ExpectedGeneration,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    string? ServiceName = null,
    string? GroupHint = null,
    bool PreserveViewerWindowState = false,
    string? ViewerActivation = null,
    bool EmitDiagnosticLog = true);

public sealed record ViewerOperationPlan(
    ViewerProfileContractDto Profile,
    ViewerSessionState? Session,
    ChannelTarget Channel,
    string Group,
    string ClientId,
    string TvTestPathKey,
    string ChannelArgument,
    string IdentityArgument,
    string ServiceName,
    bool PreserveViewerWindowState,
    string? ViewerActivation,
    int SameTransportServiceCount,
    string SameTransportServiceIds,
    string SelectedChannelSource);

public sealed record ViewerOperationPreparationResult(
    bool Success,
    string ErrorCode,
    string Message,
    ViewerOperationPlan? Plan)
{
    public static ViewerOperationPreparationResult Prepared(ViewerOperationPlan plan)
        => new(true, string.Empty, "Viewer operation prepared.", plan);

    public static ViewerOperationPreparationResult Denied(string errorCode, string message)
        => new(false, errorCode, message, null);
}

public sealed record ViewerOperationStartRequest(
    string PluginId,
    string ViewerProfileId,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    string? ServiceName = null,
    string? GroupHint = null,
    bool PreserveViewerWindowState = false,
    string? ViewerActivation = null);

public sealed record ViewerOperationEnsureTunedRequest(
    string PluginId,
    string ViewerProfileId,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    string? ServiceName = null,
    string? GroupHint = null,
    bool PreserveViewerWindowState = false,
    string? ViewerActivation = null,
    bool SuppressPrepareDiagnosticLog = false,
    Action<ViewerOperationEnsureTunedBeforeLeaseRequest>? BeforeLeaseRequest = null,
    Action<ViewerOperationEnsureTunedBeforeRetune>? BeforeRetune = null,
    Action<ViewerOperationEnsureTunedAfterRetuneTargetPrepare>? AfterRetuneTargetPrepare = null,
    Action<ViewerOperationEnsureTunedAfterRetune>? AfterRetune = null,
    Action<ViewerOperationEnsureTunedBeforeStart>? BeforeStartNew = null,
    Action<ViewerOperationEnsureTunedAfterStart>? AfterStartNew = null,
    bool SuppressStaleDiagnosticLog = false,
    Action<ViewerOperationEnsureTunedStaleLease>? BeforeStaleLeaseRelease = null,
    Action<ViewerOperationEnsureTunedStaleLease>? AfterStaleLeaseRelease = null,
    string? SourceDisplayName = null,
    string? ActionName = null);

public sealed record ViewerOperationEnsureTunedBeforeLeaseRequest(
    ViewerOperationPlan Plan,
    ViewerManagedLeaseScopeSnapshot ManagedLeaseScope);

public sealed record ViewerOperationEnsureTunedStaleLease(
    ExternalTunerLeaseDto Lease,
    DelayedDeathResult DelayedDeath);

public sealed record ViewerOperationEnsureTunedBeforeRetune(
    ViewerOperationPlan Plan,
    ExternalTunerLeaseDto Lease,
    ExternalTunerLeaseDto PreviousViewer,
    ViewerRetuneScopeDecision RetuneScope,
    bool LeaseReused);

public sealed record ViewerOperationEnsureTunedAfterRetuneTargetPrepare(
    ViewerOperationPlan Plan,
    ExternalTunerLeaseDto Lease,
    ExternalTunerLeaseDto PreviousViewer,
    ViewerRetuneScopeDecision RetuneScope,
    bool TargetPrepared,
    bool LeaseReused);

public sealed record ViewerOperationEnsureTunedAfterRetune(
    ViewerOperationPlan Plan,
    ExternalTunerLeaseDto Lease,
    ExternalTunerLeaseDto PreviousViewer,
    ViewerRetuneScopeDecision RetuneScope,
    bool LeaseReused,
    bool Success,
    bool ProcessLost,
    string Message,
    ViewerSessionState? Session,
    ViewerOperationResult Operation);

public sealed record ViewerOperationEnsureTunedBeforeStart(
    ViewerOperationPlan Plan,
    ExternalTunerLeaseDto Lease,
    ExternalTunerLeaseDto? PreviousViewer,
    bool ProcessRestarted,
    string RecoveryMode);

public sealed record ViewerOperationEnsureTunedAfterStart(
    ViewerOperationPlan Plan,
    ExternalTunerLeaseDto Lease,
    ExternalTunerLeaseDto? PreviousViewer,
    bool ProcessRestarted,
    string RecoveryMode,
    ViewerOwnershipStartResult Ownership,
    ViewerSessionState? Session,
    ViewerOperationResult Operation);

internal sealed record ViewerOperationEnsureTunedResult(
    ViewerOperationResult Operation,
    ViewerOperationPlan? Plan,
    ViewerManagedLeaseScopeSnapshot? ManagedLeaseScope,
    ViewerRetuneScopeDecision? RetuneScope,
    ExternalTunerLeaseDto? Lease,
    bool LeaseReused,
    bool ProcessRestarted,
    string RecoveryMode,
    bool RetuneAttempted,
    bool RetuneTargetPrepared,
    bool RetuneSucceeded,
    bool RetuneProcessLost,
    string RetuneMessage,
    ViewerOwnershipStartResult? OwnershipStart,
    ViewerSessionState? Session);

public sealed record ViewerOperationRetuneRequest(
    string PluginId,
    string ViewerProfileId,
    string ViewerSessionId,
    long ExpectedGeneration,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    string? ServiceName = null,
    string? GroupHint = null,
    bool PreserveViewerWindowState = false,
    string? ViewerActivation = null);



public sealed record ViewerOperationCompatibleStopRequest(
    string PluginId,
    string PluginDisplayName,
    string? LeaseId = null,
    string? ViewerProfileId = null,
    string? ViewerSessionId = null,
    long? ExpectedGeneration = null,
    string? Reason = null);

public sealed record ViewerOperationRestartRequest(
    string PluginId,
    string ViewerSessionId,
    long ExpectedGeneration,
    bool PreserveViewerWindowState = false,
    string? ViewerActivation = null,
    string? Reason = null);

public sealed record ViewerOperationActivateRequest(
    string ViewerSessionId,
    long ExpectedGeneration);

public sealed record ViewerOperationStopRequest(
    string ViewerSessionId,
    long? ExpectedGeneration,
    string? ViewerProfileId = null,
    string? Reason = null);

public sealed record ViewerOperationResult(
    bool Success,
    string State,
    string ErrorCode,
    string Message,
    string ViewerProfileId,
    string ViewerSessionId,
    long Generation,
    string LeaseId,
    int? ProcessId,
    ushort? NetworkId,
    ushort? TransportStreamId,
    ushort? ServiceId,
    string LogicalViewerSlotId,
    DateTime? AcquiredAt,
    int? ChannelSpace,
    int? ChannelIndex,
    string ViewerState,
    string Diagnostics)
{
    /// <summary>True when the requested Viewer state transition completed authoritatively.</summary>
    public bool OperationCompleted => Success;

    /// <summary>True when the operation completed but an ancillary quality condition produced a warning.</summary>
    public bool HasWarning => Success && !string.IsNullOrWhiteSpace(ErrorCode);

    /// <summary>Automatic workflows may continue only after an authoritative successful transition.</summary>
    public bool ContinuationRecommended => Success;

    public static ViewerOperationResult Denied(string errorCode, string message, ViewerSessionState? session = null)
        => From(false, "denied", errorCode, message, session, null, string.Empty);

    public static ViewerOperationResult Failed(string errorCode, string message, ViewerSessionState? session = null, ExternalTunerLeaseDto? lease = null, string diagnostics = "")
        => From(false, "failed", errorCode, message, session, lease, diagnostics);

    public static ViewerOperationResult Succeeded(string state, ViewerSessionState? session, ExternalTunerLeaseDto lease)
        => From(true, state, string.Empty, "Viewer operation completed.", session, lease, string.Empty);

    private static ViewerOperationResult From(bool success, string state, string errorCode, string message, ViewerSessionState? session, ExternalTunerLeaseDto? lease, string diagnostics)
        => new(
            success,
            state,
            errorCode,
            message,
            session?.ViewerProfileId ?? lease?.ViewerProfileId ?? string.Empty,
            session?.ViewerSessionId ?? string.Empty,
            session?.Generation ?? 0,
            session?.LeaseId ?? lease?.LeaseId ?? string.Empty,
            lease?.ProcessId ?? session?.ProcessId,
            lease?.NetworkId ?? session?.NetworkId,
            lease?.TransportStreamId ?? session?.TransportStreamId,
            lease?.ServiceId ?? session?.ServiceId,
            session?.LogicalViewerSlotId ?? lease?.LogicalViewerSlotId ?? string.Empty,
            session?.AcquiredAt ?? lease?.AcquiredAt,
            session?.ChannelSpace ?? lease?.ChannelSpace,
            session?.ChannelIndex ?? lease?.ChannelIndex,
            session?.ViewerState ?? lease?.ViewerState ?? string.Empty,
            diagnostics);
}
