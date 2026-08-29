using TvAIr.Core;

namespace TvAIr.Tuner;

/// <summary>
/// 外部プラグイン／外部プロセス向けの一時視聴チューナー貸出管理。
///
/// 方針:
/// ・Viewing Roleとして割り当てられた視聴専用チューナーだけを貸し出す。
/// ・空きがなければ録画/EPG/他視聴を奪わず拒否する。
/// ・実際の視聴プロセス起動・選局・表示はTvAIr本体が行う。
/// </summary>
public sealed class ExternalTunerLeaseService
{
    private readonly TunerPool _tunerPool;
    private readonly LogRepository _log;
    private readonly ApplicationOperationGate _applicationGate;
    private readonly object _gate = new();
    private readonly object _requestGate = new();
    private readonly Dictionary<string, ExternalTunerLeaseEntry> _leases = new(StringComparer.OrdinalIgnoreCase);
    private ViewerActionResultSnapshot? _lastViewerActionResult;
    private static readonly TimeSpan StablePeriodicLogInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ViewerAttachStaleTimeout = TimeSpan.FromSeconds(10);
    private string? _lastPeriodicReconcileSignature;
    private DateTime _lastPeriodicReconcileLogAt = DateTime.MinValue;

    public ExternalTunerLeaseService(TunerPool tunerPool, LogRepository log, ApplicationOperationGate applicationGate)
    {
        _tunerPool = tunerPool;
        _log = log;
        _applicationGate = applicationGate;
    }

    public ExternalTunerLeaseResult Request(ExternalTunerLeaseRequest request)
    {
        var group = NormalizeGroup(request.Group);
        var requiredGroup = NormalizeGroup(string.IsNullOrWhiteSpace(request.RequiredGroup) ? request.Group : request.RequiredGroup);
        var source = string.IsNullOrWhiteSpace(request.Source) ? "External" : request.Source.Trim();
        if (!_applicationGate.TryAdmit("viewer_lease_request", source, out var shutdownReason))
        {
            var status = _tunerPool.GetStatusSummary();
            _log.Add("EXTERNAL_TUNER", "Denied",
                $"source={source} reason={shutdownReason} action=reject_new_viewer_lease status={status} rule=shutdown_admission_contract");
            return ExternalTunerLeaseResult.Denied("applicationQuiescing", status);
        }

        var clientId = string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId.Trim();
        var viewerProfileId = (request.ViewerProfileId ?? string.Empty).Trim();
        var logicalViewerSlotId = (request.LogicalViewerSlotId ?? string.Empty).Trim();
        var viewerProfileName = (request.ViewerProfileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(viewerProfileId) || string.IsNullOrWhiteSpace(logicalViewerSlotId) || string.IsNullOrWhiteSpace(viewerProfileName))
        {
            var status = _tunerPool.GetStatusSummary();
            _log.Add("EXTERNAL_TUNER", "Denied",
                $"source={source} clientId={clientId ?? "-"} reason=viewer_profile_contract_incomplete viewerProfile={Safe(viewerProfileId)} logicalViewerSlotId={Safe(logicalViewerSlotId)} viewerProfileName={Safe(viewerProfileName)} status={status} rule=release_contract");
            return ExternalTunerLeaseResult.Denied("viewerProfileUnavailable", status);
        }
        var tvTestPathKey = NormalizeProfilePathKey(request.TvTestPathKey);

        // EXTERNAL_LEASE_REQUEST_LOCK_SCOPE_INVARIANT:
        // Request同士だけを専用gateで直列化し、TunerPool acquireと診断ログをservice lockへ持ち込まない。
        lock (_requestGate)
        {
            ExternalTunerLeaseEntry? existing = null;
            ExternalTunerLeaseCompatibility? compatibility = null;
            ExternalTunerLeaseDto? reusableDto = null;
            lock (_gate)
            {
                CleanupReleasedUnsafe();
                if (!string.IsNullOrWhiteSpace(clientId))
                {
                    existing = _leases.Values.FirstOrDefault(x =>
                        x.IsActive && string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                    {
                        compatibility = EvaluateReuseCompatibility(existing, request, requiredGroup);
                        if (compatibility.CanReuse && existing.IsActive)
                            reusableDto = existing.ToDto();
                    }
                }
            }

            if (existing is not null && reusableDto is not null)
            {
                _log.Add("EXTERNAL_TUNER", "Reuse",
                    $"source={source} clientId={clientId} leaseId={existing.LeaseId} group={existing.Group} requestedGroup={requiredGroup} viewerProfile={existing.ViewerProfileId} requestedViewerProfile={viewerProfileId} logicalViewerSlotId={logicalViewerSlotId} tvTestPathKey={Safe(existing.TvTestPathKey)} tuner={existing.TunerName} did={existing.Did} bonDriver={existing.BonDriverFileName} rule=release_contract");
                return ExternalTunerLeaseResult.Ok(reusableDto, reused: true);
            }

            if (existing is not null)
            {
                var status = _tunerPool.GetStatusSummary();
                _log.Add("EXTERNAL_TUNER", "ReuseBlocked",
                    $"source={source} clientId={clientId} leaseId={existing.LeaseId} existingGroup={existing.Group} requestedGroup={requiredGroup} existingViewerProfile={existing.ViewerProfileId} requestedViewerProfile={viewerProfileId} logicalViewerSlotId={logicalViewerSlotId} existingTvTestPathKey={Safe(existing.TvTestPathKey)} requestedTvTestPathKey={Safe(tvTestPathKey)} existingBonDriver={existing.BonDriverFileName} requiredBonDriver={Safe(request.RequiredBonDriverFileName)} reason={compatibility?.Reason ?? "lease_changed"} action=keep_existing_and_deny rule=release_contract");
                return ExternalTunerLeaseResult.Denied("viewerSwitchUnavailable", status);
            }

            var lease = _tunerPool.AcquireForViewing(logicalViewerSlotId, group, request.ProcessId);
            if (lease is null)
            {
                var status = _tunerPool.GetStatusSummary();
                _log.Add("EXTERNAL_TUNER", "Denied",
                    $"source={source} clientId={clientId ?? "-"} group={group} reason=no_free_tuner status={status} rule=release_contract");
                return ExternalTunerLeaseResult.Denied("tunerUnavailable", status);
            }

            var id = Guid.NewGuid().ToString("N");
            var entry = new ExternalTunerLeaseEntry(
                id, source, clientId, group, lease.Name, lease.BonDriverFileName, lease.Did,
                lease.SlotIndex, lease.ElapsedSinceReleaseMs, DateTime.Now, request.ProcessId, request.Note,
                request.NetworkId, request.TransportStreamId, request.ServiceId, request.ChannelSpace,
                request.ChannelIndex, request.RequiredBonDriverFileName, logicalViewerSlotId,
                viewerProfileId, viewerProfileName, tvTestPathKey, lease);
            ExternalTunerLeaseDto dto;
            lock (_gate)
            {
                _leases[id] = entry;
                dto = entry.ToDto();
            }

            _log.Add("EXTERNAL_TUNER", "Granted",
                $"source={source} clientId={clientId ?? "-"} leaseId={id} group={group} viewerProfile={viewerProfileId} viewerProfileName={Safe(viewerProfileName)} logicalViewerSlotId={logicalViewerSlotId} tvTestPathKey={Safe(tvTestPathKey)} tuner={lease.Name} did={lease.Did} slot={lease.SlotIndex} bonDriver={lease.BonDriverFileName} rule=release_contract");
            return ExternalTunerLeaseResult.Ok(dto, reused: false);
        }
    }

    public bool AttachViewerProcess(string? leaseId, int processId, string? channelArgument, string state, string launchResult, string tuneResult, string activateResult, ushort? networkId = null, ushort? transportStreamId = null, ushort? serviceId = null, int? channelSpace = null, int? channelIndex = null)
        => AttachViewerProcessDetailed(leaseId, processId, channelArgument, state, launchResult, tuneResult, activateResult, networkId, transportStreamId, serviceId, channelSpace, channelIndex).Success;

    public ViewerProcessAttachResult AttachViewerProcessDetailed(string? leaseId, int processId, string? channelArgument, string state, string launchResult, string tuneResult, string activateResult, ushort? networkId = null, ushort? transportStreamId = null, ushort? serviceId = null, int? channelSpace = null, int? channelIndex = null)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return ViewerProcessAttachResult.Failed("lease_id_missing");
        if (processId <= 0)
            return ViewerProcessAttachResult.Failed("process_id_invalid");

        var normalizedLeaseId = leaseId.Trim();
        ExternalTunerLeaseEntry entry;
        lock (_gate)
        {
            if (!_leases.TryGetValue(normalizedLeaseId, out var found) || !found.TryBeginViewerAttach())
                return ViewerProcessAttachResult.Failed("lease_not_active");
            entry = found;
        }

        try
        {
            if (!TvAirManagedProcessRegistry.TryGet(processId, out var managed))
                return ViewerProcessAttachResult.Failed("managed_process_not_found");
            if (!managed.IsViewer)
                return ViewerProcessAttachResult.Failed("managed_process_not_viewer");
            if (!string.Equals(managed.OwnershipId, entry.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                _log.Add("EXTERNAL_TUNER", "ViewerProcessAttachDenied",
                    $"leaseId={entry.LeaseId} pid={processId} reason=managed_ownership_mismatch rule=release_contract");
                return ViewerProcessAttachResult.Failed("managed_ownership_mismatch");
            }

            var observedIdentity = TvAirManagedProcessRegistry.CaptureIdentity(processId);
            if (!TvAirManagedProcessRegistry.IdentityMatches(managed.Identity, observedIdentity))
            {
                _log.Add("EXTERNAL_TUNER", "ViewerProcessAttachDenied",
                    $"leaseId={entry.LeaseId} pid={processId} reason=process_identity_mismatch rule=release_contract");
                return ViewerProcessAttachResult.Failed("process_identity_mismatch");
            }

            // VIEWER_ATTACH_LOCK_SCOPE_INVARIANT:
            // TunerPool PID反映をservice lockへ持ち込まない。Attaching状態がReleaseとの排他を担う。
            entry.ApplyViewerProcessToPool(processId);

            lock (_gate)
            {
                if (!_leases.TryGetValue(normalizedLeaseId, out var current) ||
                    !ReferenceEquals(current, entry) ||
                    !entry.CompleteViewerAttach(processId, managed.Identity, channelArgument ?? string.Empty,
                        state, launchResult, tuneResult, activateResult,
                        networkId, transportStreamId, serviceId, channelSpace, channelIndex))
                {
                    return ViewerProcessAttachResult.Failed("lease_changed_during_attach");
                }
            }

            _log.Add("EXTERNAL_TUNER", "ViewerProcessAttached",
                $"leaseId={entry.LeaseId} pid={processId} viewerProfile={entry.ViewerProfileId} viewerProfileName={Safe(entry.ViewerProfileName)} tvTestPathKey={Safe(entry.TvTestPathKey)} tuner={entry.TunerName} did={entry.Did} state={state} nid={(networkId.HasValue ? networkId.Value.ToString() : "-")} tsid={(transportStreamId.HasValue ? transportStreamId.Value.ToString() : "-")} sid={(serviceId.HasValue ? serviceId.Value.ToString() : "-")} channelArgument={Safe(channelArgument)} rule=release_contract");
            return ViewerProcessAttachResult.Succeeded();
        }
        finally
        {
            entry.CancelViewerAttach();
        }
    }

    public bool SetViewerState(string? leaseId, string state, string launchResult, string tuneResult, string activateResult, string rollbackResult)
    {
        if (string.IsNullOrWhiteSpace(leaseId)) return false;
        lock (_gate)
        {
            if (!_leases.TryGetValue(leaseId.Trim(), out var entry) || !entry.IsActive)
                return false;
            return entry.TrySetViewerState(state, launchResult, tuneResult, activateResult, rollbackResult);
        }
    }

    public void SetLastViewerActionResult(string pluginName, string action, bool success, string state, string errorCode, string message, string? leaseId = null, string? tunerName = null, string? did = null, string? bonDriverFileName = null, int? processId = null, ushort? networkId = null, ushort? transportStreamId = null, ushort? serviceId = null, string? diagnostics = null)
    {
        lock (_gate)
        {
            _lastViewerActionResult = new ViewerActionResultSnapshot(
                DateTime.Now, pluginName, action, success, state, errorCode, message, leaseId, tunerName, did, bonDriverFileName, processId, networkId, transportStreamId, serviceId, diagnostics);
        }
    }

    public ViewerActionResultSnapshot? GetLastViewerActionResult()
    {
        lock (_gate) return _lastViewerActionResult;
    }

    public bool Release(string leaseId, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(leaseId)) return false;
        ExternalTunerLeaseEntry entry;
        lock (_gate)
        {
            if (!_leases.TryGetValue(leaseId.Trim(), out var found) || !found.TryBeginRelease())
                return false;
            entry = found;
            _leases.Remove(entry.LeaseId);
        }

        try
        {
            entry.CompleteRelease();
            _log.Add("EXTERNAL_TUNER", "Released",
                $"source={(source ?? entry.Source)} clientId={entry.ClientId ?? "-"} leaseId={entry.LeaseId} group={entry.Group} tuner={entry.TunerName}");
            return true;
        }
        catch (Exception ex)
        {
            entry.CancelRelease();
            lock (_gate)
            {
                if (!_leases.ContainsKey(entry.LeaseId) && entry.IsActive)
                    _leases[entry.LeaseId] = entry;
            }
            _log.Add("EXTERNAL_TUNER", "ReleaseFailed",
                $"source={(source ?? entry.Source)} leaseId={entry.LeaseId} exceptionType={ex.GetType().Name} message={Safe(ex.Message)} action=restore_active_entry_for_reconciliation rule=release_contract");
            return false;
        }
    }

    public IReadOnlyList<ExternalTunerLeaseDto> GetActiveLeases()
    {
        lock (_gate)
        {
            CleanupReleasedUnsafe();
            return _leases.Values
                .Where(x => x.IsActive)
                .OrderBy(x => x.AcquiredAt)
                .Select(x => x.ToDto())
                .ToList();
        }
    }

    /// <summary>
    /// TvAIrが貸し出した視聴leaseと実プロセスを再照合し、
    /// 終了済みプロセスに紐づく占有だけを共通Release経路へ戻す。
    /// DID、BonDriver名、チューナー本数には依存しない。
    /// </summary>
    public ExternalTunerLeaseReconcileResult ReconcileProcessState(string source, bool isPeriodic)
    {
        List<ExternalTunerLeaseEntry> snapshot;
        lock (_gate)
        {
            CleanupReleasedUnsafe();
            snapshot = _leases.Values
                .Where(x => x.IsActive || x.IsViewerAttachStale(DateTime.UtcNow, ViewerAttachStaleTimeout))
                .ToList();
        }

        var inspected = 0;
        var released = 0;
        var keptAlive = 0;
        var pendingWithoutProcess = 0;
        var stalePoolLease = 0;
        var releasedEntries = new List<string>();

        foreach (var entry in snapshot)
        {
            string? releaseReason = null;
            int? unregisterPid = null;
            var staleViewerAttach = entry.IsViewerAttachStale(DateTime.UtcNow, ViewerAttachStaleTimeout);

            if (staleViewerAttach)
            {
                releaseReason = "stale_viewer_attach";
                unregisterPid = entry.ProcessId;
            }
            else if (!entry.PoolLeaseCurrent)
            {
                stalePoolLease++;
                releaseReason = "stale_pool_lease";
                unregisterPid = entry.ProcessId;
            }
            else if (!entry.ProcessId.HasValue || entry.ProcessId.Value <= 0)
            {
                pendingWithoutProcess++;
                continue;
            }
            else
            {
                inspected++;
                var processId = entry.ProcessId.Value;
                var processExists = TryCaptureObservedIdentity(processId, out var observedIdentity);
                var registryMatches = TvAirManagedProcessRegistry.TryGet(processId, out var managed) &&
                    managed.IsViewer &&
                    string.Equals(managed.OwnershipId, entry.LeaseId, StringComparison.OrdinalIgnoreCase);
                var identityMatches = registryMatches &&
                    TvAirManagedProcessRegistry.IdentityMatches(managed.Identity, observedIdentity) &&
                    TvAirManagedProcessRegistry.IdentityMatches(entry.ProcessIdentity, observedIdentity);

                if ((processExists && registryMatches && identityMatches) ||
                    (processExists && !observedIdentity.IsAvailable))
                {
                    keptAlive++;
                    continue;
                }

                releaseReason = "process_not_owned_or_exited";
                unregisterPid = processId;
            }

            var claimed = false;
            lock (_gate)
            {
                claimed = _leases.TryGetValue(entry.LeaseId, out var current) &&
                    ReferenceEquals(current, entry) &&
                    (staleViewerAttach
                        ? entry.TryBeginStaleViewerAttachRelease(DateTime.UtcNow, ViewerAttachStaleTimeout)
                        : entry.TryBeginRelease());
                if (claimed)
                    _leases.Remove(entry.LeaseId);
            }
            if (!claimed)
                continue;

            if (unregisterPid is int pid && pid > 0 &&
                TvAirManagedProcessRegistry.TryGet(pid, out var registered) &&
                string.Equals(registered.OwnershipId, entry.LeaseId, StringComparison.OrdinalIgnoreCase))
            {
                TvAirManagedProcessRegistry.Unregister(pid);
            }

            try
            {
                entry.CompleteRelease();
                released++;
                releasedEntries.Add($"{entry.LeaseId}:{entry.ProcessId?.ToString() ?? "-"}:{entry.TunerName}:{releaseReason}");
            }
            catch (Exception ex)
            {
                entry.CancelRelease();
                lock (_gate)
                {
                    // STALE_ATTACH_RELEASE_FAILURE_RESTORE_INVARIANT:
                    // stale Attaching回収の解放失敗時はCancelReleaseでAttachingへ戻る。
                    // Activeだけを再登録対象にすると、Pool leaseを保持したentryが管理辞書から消失するため、
                    // ActiveまたはAttachingの管理継続状態を同じentryとして復帰させる。
                    if (!_leases.ContainsKey(entry.LeaseId) && entry.RequiresServiceTracking)
                        _leases[entry.LeaseId] = entry;
                }
                _log.Add("EXTERNAL_TUNER_RECONCILE", source,
                    $"result=RELEASE_FAILED leaseId={entry.LeaseId} tuner={entry.TunerName} reason={releaseReason} restoredState={(entry.IsAttaching ? "Attaching" : "Active")} exceptionType={ex.GetType().Name} message={Safe(ex.Message)} action=restore_tracked_entry_for_reconciliation rule=release_contract");
            }
        }

        var result = new ExternalTunerLeaseReconcileResult(inspected, released, keptAlive, pendingWithoutProcess, stalePoolLease);
        var signature =
            $"inspected={inspected}|released={released}|keptAlive={keptAlive}|" +
            $"pendingWithoutProcess={pendingWithoutProcess}|stalePoolLease={stalePoolLease}";
        var now = DateTime.Now;
        var hasFinding = released > 0 || pendingWithoutProcess > 0 || stalePoolLease > 0;
        bool writeSummary;
        lock (_gate)
        {
            var changed = !string.Equals(_lastPeriodicReconcileSignature, signature, StringComparison.Ordinal);
            var summaryDue = now - _lastPeriodicReconcileLogAt >= StablePeriodicLogInterval;
            writeSummary = !isPeriodic || hasFinding || changed || summaryDue;
            if (writeSummary)
            {
                _lastPeriodicReconcileSignature = signature;
                _lastPeriodicReconcileLogAt = now;
            }
        }
        if (writeSummary)
        {
            _log.Add("EXTERNAL_TUNER_RECONCILE", source,
                $"result=OK inspected={inspected} released={released} keptAlive={keptAlive} pendingWithoutProcess={pendingWithoutProcess} stalePoolLease={stalePoolLease} releasedEntries=[{string.Join(",", releasedEntries)}] policy=managed_viewer_lease_process_identity rule=release_contract");
        }
        return result;
    }

    private static bool TryCaptureObservedIdentity(int processId, out ManagedProcessIdentity identity)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited)
            {
                identity = ManagedProcessIdentity.Unavailable(processId);
                return false;
            }
            identity = TvAirManagedProcessRegistry.CaptureIdentity(processId);
            return true;
        }
        catch (ArgumentException)
        {
            identity = ManagedProcessIdentity.Unavailable(processId);
            return false;
        }
        catch
        {
            identity = ManagedProcessIdentity.Unavailable(processId);
            return true;
        }
    }

    private void CleanupReleasedUnsafe()
    {
        foreach (var key in _leases.Where(kv => kv.Value.IsReleased).Select(kv => kv.Key).ToList())
            _leases.Remove(key);
    }

    private static ExternalTunerLeaseCompatibility EvaluateReuseCompatibility(ExternalTunerLeaseEntry existing, ExternalTunerLeaseRequest request, string requiredGroup)
    {
        if (!string.Equals(NormalizeGroup(existing.Group), requiredGroup, StringComparison.OrdinalIgnoreCase))
            return ExternalTunerLeaseCompatibility.Blocked("group_mismatch");

        var requestedProfileId = (request.ViewerProfileId ?? string.Empty).Trim();
        var requestedLogicalId = (request.LogicalViewerSlotId ?? string.Empty).Trim();
        if (!string.Equals(existing.ViewerProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase))
            return ExternalTunerLeaseCompatibility.Blocked("viewer_profile_mismatch");
        if (!string.Equals(existing.LogicalViewerSlotId, requestedLogicalId, StringComparison.OrdinalIgnoreCase))
            return ExternalTunerLeaseCompatibility.Blocked("logical_viewer_slot_mismatch");

        var requestedPathKey = NormalizeProfilePathKey(request.TvTestPathKey);
        if (!string.IsNullOrWhiteSpace(requestedPathKey) &&
            !string.Equals(NormalizeProfilePathKey(existing.TvTestPathKey), requestedPathKey, StringComparison.OrdinalIgnoreCase))
            return ExternalTunerLeaseCompatibility.Blocked("tvtest_path_key_mismatch");

        if (!string.IsNullOrWhiteSpace(request.RequiredBonDriverFileName) &&
            !string.Equals(NormalizeBonDriverName(existing.BonDriverFileName), NormalizeBonDriverName(request.RequiredBonDriverFileName), StringComparison.OrdinalIgnoreCase))
            return ExternalTunerLeaseCompatibility.Blocked("bondriver_mismatch");

        return ExternalTunerLeaseCompatibility.Reusable();
    }

    private static string NormalizeBonDriverName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFileName(value.Trim());
    }

    private static string NormalizeProfilePathKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Path.GetFullPath(value.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant(); }
        catch { return value.Trim().ToUpperInvariant(); }
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string NormalizeGroup(string? group)
    {
        var g = (group ?? string.Empty).Trim().ToUpperInvariant();
        return g switch
        {
            "BS" or "CS" or "BS/CS" or "BSCS" => "BSCS",
            "地上波" or "GR" or "GROUND" => "GR",
            _ => g
        };
    }
}

public sealed record ViewerActionResultSnapshot(
    DateTime At,
    string PluginName,
    string Action,
    bool Success,
    string State,
    string ErrorCode,
    string Message,
    string? LeaseId = null,
    string? TunerName = null,
    string? Did = null,
    string? BonDriverFileName = null,
    int? ProcessId = null,
    ushort? NetworkId = null,
    ushort? TransportStreamId = null,
    ushort? ServiceId = null,
    string? Diagnostics = null);

public sealed class ExternalTunerLeaseRequest
{
    public ExternalTunerLeaseRequest()
    {
    }

    public ExternalTunerLeaseRequest(string? group, string? source, string? clientId, int? processId, string? note)
    {
        Group = group;
        Source = source;
        ClientId = clientId;
        ProcessId = processId;
        Note = note;
    }

    public string? Group { get; set; }
    public string? Source { get; set; }
    public string? ClientId { get; set; }
    public int? ProcessId { get; set; }
    public string? Note { get; set; }

    // release_contract: Viewer lease reuse compatibility contract.
    // Existing leases are reusable only when the requested viewing target is compatible
    // with the already leased tuner class.  This prevents a GR viewer lease from being
    // reused for BS/CS viewer operation requests that require a BSCS tuner/BonDriver.
    public string? RequiredGroup { get; set; }
    public string? RequiredBonDriverFileName { get; set; }
    public ushort? NetworkId { get; set; }
    public ushort? TransportStreamId { get; set; }
    public ushort? ServiceId { get; set; }
    public int? ChannelSpace { get; set; }
    public int? ChannelIndex { get; set; }

    // release_contract: Viewer profile identity is part of the viewer reuse contract.
    // Different viewer profiles must not share an existing viewer process or lease.
    public string? ViewerProfileId { get; set; }
    public string? ViewerProfileName { get; set; }
    public string? TvTestPathKey { get; set; }

    // TvAIr本体で解決済みの設定視聴枠identity。物理Viewingスロットの直接取得に使用する。
    public string? LogicalViewerSlotId { get; set; }
}

internal sealed record ExternalTunerLeaseCompatibility(bool CanReuse, string Reason)
{
    public static ExternalTunerLeaseCompatibility Reusable() => new(true, "compatible");
    public static ExternalTunerLeaseCompatibility Blocked(string reason) => new(false, reason);
}

public sealed record ExternalTunerReleaseRequest(
    string? LeaseId,
    string? Source);

public sealed record ExternalTunerLeaseDto(
    string LeaseId,
    string Source,
    string? ClientId,
    string Group,
    string TunerName,
    string BonDriverFileName,
    string Did,
    int SlotIndex,
    DateTime AcquiredAt,
    int? ProcessId,
    string? Note,
    double? ElapsedSinceReleaseMs,
    ushort? NetworkId = null,
    ushort? TransportStreamId = null,
    ushort? ServiceId = null,
    int? ChannelSpace = null,
    int? ChannelIndex = null,
    string? RequiredBonDriverFileName = null,
    string ViewerState = "leaseOnly",
    string LaunchResult = "notStarted",
    string TuneResult = "notStarted",
    string ActivateResult = "notStarted",
    string RollbackResult = "notNeeded",
    string? ChannelArgument = null,
    string ViewerProfileId = "",
    string ViewerProfileName = "",
    string? TvTestPathKey = null,
    string LogicalViewerSlotId = "",
    Guid? PoolLeaseId = null,
    long OccupancyGeneration = 0,
    bool PoolLeaseCurrent = false);

public sealed record ExternalTunerLeaseResult(
    bool Success,
    bool Reused,
    string? Reason,
    string? Status,
    ExternalTunerLeaseDto? Lease)
{
    public static ExternalTunerLeaseResult Ok(ExternalTunerLeaseDto lease, bool reused)
        => new(true, reused, null, null, lease);

    public static ExternalTunerLeaseResult Denied(string reason, string status)
        => new(false, false, reason, status, null);
}

internal sealed class ExternalTunerLeaseEntry
{
    private readonly TunerLease _lease;

    public ExternalTunerLeaseEntry(
        string leaseId,
        string source,
        string? clientId,
        string group,
        string tunerName,
        string bonDriverFileName,
        string did,
        int slotIndex,
        double? elapsedSinceReleaseMs,
        DateTime acquiredAt,
        int? processId,
        string? note,
        ushort? networkId,
        ushort? transportStreamId,
        ushort? serviceId,
        int? channelSpace,
        int? channelIndex,
        string? requiredBonDriverFileName,
        string logicalViewerSlotId,
        string viewerProfileId,
        string viewerProfileName,
        string? tvTestPathKey,
        TunerLease lease,
        string viewerState = "leaseOnly",
        string launchResult = "notStarted",
        string tuneResult = "notStarted",
        string activateResult = "notStarted",
        string rollbackResult = "notNeeded",
        string? channelArgument = null)
    {
        LeaseId = leaseId;
        Source = source;
        ClientId = clientId;
        Group = group;
        TunerName = tunerName;
        BonDriverFileName = bonDriverFileName;
        Did = did;
        SlotIndex = slotIndex;
        ElapsedSinceReleaseMs = elapsedSinceReleaseMs;
        AcquiredAt = acquiredAt;
        ProcessId = processId;
        Note = note;
        NetworkId = networkId;
        TransportStreamId = transportStreamId;
        ServiceId = serviceId;
        ChannelSpace = channelSpace;
        ChannelIndex = channelIndex;
        RequiredBonDriverFileName = requiredBonDriverFileName;
        LogicalViewerSlotId = logicalViewerSlotId;
        ViewerProfileId = viewerProfileId;
        ViewerProfileName = viewerProfileName;
        TvTestPathKey = tvTestPathKey;
        ViewerState = viewerState;
        LaunchResult = launchResult;
        TuneResult = tuneResult;
        ActivateResult = activateResult;
        RollbackResult = rollbackResult;
        ChannelArgument = channelArgument;
        _lease = lease;
    }

    public string LeaseId { get; }
    public string Source { get; }
    public string? ClientId { get; }
    public string Group { get; }
    public string TunerName { get; }
    public string BonDriverFileName { get; }
    public string Did { get; }
    public int SlotIndex { get; }
    public double? ElapsedSinceReleaseMs { get; }
    public DateTime AcquiredAt { get; }
    public int? ProcessId { get; private set; }
    public ManagedProcessIdentity ProcessIdentity { get; private set; } = ManagedProcessIdentity.Unavailable(0);
    public string? Note { get; }
    public ushort? NetworkId { get; private set; }
    public ushort? TransportStreamId { get; private set; }
    public ushort? ServiceId { get; private set; }
    public int? ChannelSpace { get; private set; }
    public int? ChannelIndex { get; private set; }
    public string? RequiredBonDriverFileName { get; }
    public string LogicalViewerSlotId { get; }
    public string ViewerProfileId { get; }
    public string ViewerProfileName { get; }
    public string? TvTestPathKey { get; }
    public string ViewerState { get; private set; }
    public string LaunchResult { get; private set; }
    public string TuneResult { get; private set; }
    public string ActivateResult { get; private set; }
    public string RollbackResult { get; private set; }
    public string? ChannelArgument { get; private set; }
    private const int ReleaseStateActive = 0;
    private const int ReleaseStateReleasing = 1;
    private const int ReleaseStateReleased = 2;
    private const int ReleaseStateAttaching = 3;
    private int _releaseState = ReleaseStateActive;
    private int _releaseReturnState = ReleaseStateActive;
    private long _viewerAttachStartedAtUtcTicks;

    public bool IsActive => Volatile.Read(ref _releaseState) == ReleaseStateActive;
    public bool IsReleasing => Volatile.Read(ref _releaseState) == ReleaseStateReleasing;
    public bool IsReleased => Volatile.Read(ref _releaseState) == ReleaseStateReleased;
    public bool IsAttaching => Volatile.Read(ref _releaseState) == ReleaseStateAttaching;
    public bool RequiresServiceTracking
    {
        get
        {
            var state = Volatile.Read(ref _releaseState);
            return state is ReleaseStateActive or ReleaseStateAttaching;
        }
    }
    public Guid PoolLeaseId => _lease.PoolLeaseId;
    public long OccupancyGeneration => _lease.OccupancyGeneration;
    public bool PoolLeaseCurrent => IsActive && _lease.IsCurrent;

    public bool TryBeginViewerAttach()
    {
        if (Interlocked.CompareExchange(ref _releaseState, ReleaseStateAttaching, ReleaseStateActive) != ReleaseStateActive)
            return false;
        Volatile.Write(ref _viewerAttachStartedAtUtcTicks, DateTime.UtcNow.Ticks);
        return true;
    }

    public bool IsViewerAttachStale(DateTime utcNow, TimeSpan timeout)
    {
        if (!IsAttaching) return false;
        var startedTicks = Volatile.Read(ref _viewerAttachStartedAtUtcTicks);
        return startedTicks > 0 && utcNow - new DateTime(startedTicks, DateTimeKind.Utc) >= timeout;
    }

    public void ApplyViewerProcessToPool(int processId)
    {
        if (!IsAttaching || !_lease.IsCurrent)
            throw new InvalidOperationException($"stale_pool_lease leaseId={LeaseId} poolLeaseId={_lease.PoolLeaseId} generation={_lease.OccupancyGeneration}");
        _lease.SetProcessId(processId);
    }

    public bool CompleteViewerAttach(
        int processId,
        ManagedProcessIdentity processIdentity,
        string channelArgument,
        string state,
        string launchResult,
        string tuneResult,
        string activateResult,
        ushort? networkId,
        ushort? transportStreamId,
        ushort? serviceId,
        int? channelSpace,
        int? channelIndex)
    {
        if (!IsAttaching) return false;

        // VIEWER_ATTACH_COMMIT_ORDER_INVARIANT:
        // 呼出元はservice lockを保持する。全フィールド反映後にActiveを公開する。
        ProcessId = processId;
        ProcessIdentity = processIdentity;
        ChannelArgument = channelArgument;
        ViewerState = state;
        LaunchResult = launchResult;
        TuneResult = tuneResult;
        ActivateResult = activateResult;
        RollbackResult = "notNeeded";
        if (networkId.HasValue) NetworkId = networkId;
        if (transportStreamId.HasValue) TransportStreamId = transportStreamId;
        if (serviceId.HasValue) ServiceId = serviceId;
        if (channelSpace.HasValue) ChannelSpace = channelSpace;
        if (channelIndex.HasValue) ChannelIndex = channelIndex;
        var completed = Interlocked.CompareExchange(ref _releaseState, ReleaseStateActive, ReleaseStateAttaching) == ReleaseStateAttaching;
        if (completed) Volatile.Write(ref _viewerAttachStartedAtUtcTicks, 0);
        return completed;
    }

    public void CancelViewerAttach()
    {
        if (Interlocked.CompareExchange(ref _releaseState, ReleaseStateActive, ReleaseStateAttaching) == ReleaseStateAttaching)
            Volatile.Write(ref _viewerAttachStartedAtUtcTicks, 0);
    }

    public bool TrySetViewerState(string state, string launchResult, string tuneResult, string activateResult, string rollbackResult)
    {
        if (!IsActive) return false;
        ViewerState = state;
        LaunchResult = launchResult;
        TuneResult = tuneResult;
        ActivateResult = activateResult;
        RollbackResult = rollbackResult;
        return true;
    }

    // EXTERNAL_LEASE_RELEASE_COMMIT_INVARIANT:
    // wrapperを先にReleasedへすると、TunerLease.Dispose失敗時にPool identityが残っていても
    // 後続Reconciliationが再解放できない。Active→ReleasingをCASし、Pool解放成功後だけReleasedへ確定する。
    public bool TryBeginRelease()
    {
        // EXTERNAL_LEASE_RELEASE_RETURN_STATE_INVARIANT:
        // CAS失敗した非ownerが復帰先を書き換えてはならない。stale Attaching回収中に通常Releaseが重なると、
        // 解放失敗後の復帰先がAttachingからActiveへ破壊され、未確定Viewer情報を外部公開するため禁止する。
        if (Interlocked.CompareExchange(ref _releaseState, ReleaseStateReleasing, ReleaseStateActive) != ReleaseStateActive)
            return false;

        Volatile.Write(ref _releaseReturnState, ReleaseStateActive);
        return true;
    }

    // VIEWER_ATTACH_STALE_RECOVERY_INVARIANT:
    // PID反映後・entry確定前に呼出元が中断するとAttachingが永久残留する。
    // 通常の短いattach中は触らず、一定時間を超えた場合だけAttaching→ReleasingへCASし、
    // 既存leaseの共通Release経路へ戻す。Release失敗時はActiveではなくAttachingへ戻す。
    public bool TryBeginStaleViewerAttachRelease(DateTime utcNow, TimeSpan timeout)
    {
        if (!IsViewerAttachStale(utcNow, timeout)) return false;
        if (Interlocked.CompareExchange(ref _releaseState, ReleaseStateReleasing, ReleaseStateAttaching) != ReleaseStateAttaching)
            return false;

        Volatile.Write(ref _releaseReturnState, ReleaseStateAttaching);
        return true;
    }

    public void CompleteRelease()
    {
        _lease.Dispose();
        Interlocked.Exchange(ref _releaseState, ReleaseStateReleased);
        Volatile.Write(ref _viewerAttachStartedAtUtcTicks, 0);
    }

    public void CancelRelease()
    {
        var returnState = Volatile.Read(ref _releaseReturnState);
        Interlocked.CompareExchange(ref _releaseState, returnState, ReleaseStateReleasing);
    }

    public ExternalTunerLeaseDto ToDto() => new(
        LeaseId,
        Source,
        ClientId,
        Group,
        TunerName,
        BonDriverFileName,
        Did,
        SlotIndex,
        AcquiredAt,
        ProcessId,
        Note,
        ElapsedSinceReleaseMs,
        NetworkId,
        TransportStreamId,
        ServiceId,
        ChannelSpace,
        ChannelIndex,
        RequiredBonDriverFileName,
        ViewerState,
        LaunchResult,
        TuneResult,
        ActivateResult,
        RollbackResult,
        ChannelArgument,
        ViewerProfileId,
        ViewerProfileName,
        TvTestPathKey,
        LogicalViewerSlotId,
        _lease.PoolLeaseId,
        _lease.OccupancyGeneration,
        IsActive && _lease.IsCurrent);
}

public sealed record ViewerProcessAttachResult(bool Success, string Reason)
{
    public static ViewerProcessAttachResult Succeeded() => new(true, "ok");
    public static ViewerProcessAttachResult Failed(string reason) => new(false, reason);
}

public sealed record ExternalTunerLeaseReconcileResult(
    int Inspected,
    int Released,
    int KeptAlive,
    int PendingWithoutProcess,
    int StalePoolLease = 0);
