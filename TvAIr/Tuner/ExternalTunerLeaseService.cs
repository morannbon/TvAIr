using TvAIr.Core;

namespace TvAIr.Tuner;

/// <summary>
/// 外部プラグイン／外部プロセス向けの一時視聴チューナー貸出管理。
///
/// 方針:
/// ・録画・チェーン予約を最優先する。
/// ・空きチューナーがある場合だけ Viewing として貸し出す。
/// ・空きがなければ録画/EPG/他視聴を奪わず拒否する。
/// ・実際の視聴プロセス起動・選局・表示はTvAIr本体が行う。
/// ・録画が接近した場合は既存の ReservationScheduler 側 ForceReleaseViewing で解放対象になる。
/// </summary>
public sealed class ExternalTunerLeaseService
{
    private readonly TunerPool _tunerPool;
    private readonly LogRepository _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, ExternalTunerLeaseEntry> _leases = new(StringComparer.OrdinalIgnoreCase);
    private ViewerActionResultSnapshot? _lastViewerActionResult;

    public ExternalTunerLeaseService(TunerPool tunerPool, LogRepository log)
    {
        _tunerPool = tunerPool;
        _log = log;
    }

    public ExternalTunerLeaseResult Request(ExternalTunerLeaseRequest request)
    {
        var group = NormalizeGroup(request.Group);
        var requiredGroup = NormalizeGroup(string.IsNullOrWhiteSpace(request.RequiredGroup) ? request.Group : request.RequiredGroup);
        var source = string.IsNullOrWhiteSpace(request.Source) ? "External" : request.Source.Trim();
        var clientId = string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId.Trim();
        var viewerProfileId = NormalizeViewerProfileId(request.ViewerProfileId);
        var viewerProfileName = string.IsNullOrWhiteSpace(request.ViewerProfileName) ? ViewerProfileDisplayName(viewerProfileId) : request.ViewerProfileName.Trim();
        var tvTestPathKey = NormalizeProfilePathKey(request.TvTestPathKey);

        lock (_gate)
        {
            CleanupReleasedUnsafe();

            if (!string.IsNullOrWhiteSpace(clientId))
            {
                var existing = _leases.Values.FirstOrDefault(x =>
                    !x.IsReleased && string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    var compatibility = EvaluateReuseCompatibility(existing, request, requiredGroup);
                    if (compatibility.CanReuse)
                    {
                        _log.Add("EXTERNAL_TUNER", "Reuse",
                            $"source={source} clientId={clientId} leaseId={existing.LeaseId} group={existing.Group} requestedGroup={requiredGroup} viewerProfile={existing.ViewerProfileId} requestedViewerProfile={viewerProfileId} viewerProfileFrame={request.ViewerProfileFrameIndex} tvTestPathKey={Safe(existing.TvTestPathKey)} tuner={existing.TunerName} did={existing.Did} bonDriver={existing.BonDriverFileName} rule=release_contract");
                        return ExternalTunerLeaseResult.Ok(existing.ToDto(), reused: true);
                    }

                    _log.Add("EXTERNAL_TUNER", "ReuseBlocked",
                        $"source={source} clientId={clientId} leaseId={existing.LeaseId} existingGroup={existing.Group} requestedGroup={requiredGroup} existingViewerProfile={existing.ViewerProfileId} requestedViewerProfile={viewerProfileId} viewerProfileFrame={request.ViewerProfileFrameIndex} existingTvTestPathKey={Safe(existing.TvTestPathKey)} requestedTvTestPathKey={Safe(tvTestPathKey)} existingBonDriver={existing.BonDriverFileName} requiredBonDriver={Safe(request.RequiredBonDriverFileName)} reason={compatibility.Reason} action=release_and_reassign rule=release_contract");
                    entryReleaseUnsafe(existing);
                    _leases.Remove(existing.LeaseId);
                }
            }

            var lease = _tunerPool.AcquireForViewing(group, request.ProcessId, request.ViewerProfileFrameIndex);
            if (lease is null)
            {
                var status = _tunerPool.GetStatusSummary();
                _log.Add("EXTERNAL_TUNER", "Denied",
                    $"source={source} clientId={clientId ?? "-"} group={group} reason=no_free_tuner status={status} rule=release_contract");
                return ExternalTunerLeaseResult.Denied("tunerUnavailable", status);
            }

            var id = Guid.NewGuid().ToString("N");
            var entry = new ExternalTunerLeaseEntry(
                id,
                source,
                clientId,
                group,
                lease.Name,
                lease.BonDriverFileName,
                lease.Did,
                lease.SlotIndex,
                lease.ElapsedSinceReleaseMs,
                DateTime.Now,
                request.ProcessId,
                request.Note,
                request.NetworkId,
                request.TransportStreamId,
                request.ServiceId,
                request.ChannelSpace,
                request.ChannelIndex,
                request.RequiredBonDriverFileName,
                viewerProfileId,
                viewerProfileName,
                tvTestPathKey,
                lease);
            _leases[id] = entry;

            _log.Add("EXTERNAL_TUNER", "Granted",
                $"source={source} clientId={clientId ?? "-"} leaseId={id} group={group} viewerProfile={viewerProfileId} viewerProfileName={Safe(viewerProfileName)} viewerProfileFrame={request.ViewerProfileFrameIndex} tvTestPathKey={Safe(tvTestPathKey)} tuner={lease.Name} did={lease.Did} slot={lease.SlotIndex} bonDriver={lease.BonDriverFileName} rule=release_contract");
            return ExternalTunerLeaseResult.Ok(entry.ToDto(), reused: false);
        }
    }

    public bool AttachViewerProcess(string? leaseId, int processId, string? channelArgument, string state, string launchResult, string tuneResult, string activateResult, ushort? networkId = null, ushort? transportStreamId = null, ushort? serviceId = null, int? channelSpace = null, int? channelIndex = null)
    {
        if (string.IsNullOrWhiteSpace(leaseId) || processId <= 0) return false;
        lock (_gate)
        {
            if (!_leases.TryGetValue(leaseId.Trim(), out var entry) || entry.IsReleased)
                return false;
            entry.AttachViewerProcess(processId, channelArgument ?? string.Empty, state, launchResult, tuneResult, activateResult);
            entry.UpdateTarget(networkId, transportStreamId, serviceId, channelSpace, channelIndex);
            _log.Add("EXTERNAL_TUNER", "ViewerProcessAttached",
                $"leaseId={entry.LeaseId} pid={processId} viewerProfile={entry.ViewerProfileId} viewerProfileName={Safe(entry.ViewerProfileName)} tvTestPathKey={Safe(entry.TvTestPathKey)} tuner={entry.TunerName} did={entry.Did} state={state} nid={(networkId.HasValue ? networkId.Value.ToString() : "-")} tsid={(transportStreamId.HasValue ? transportStreamId.Value.ToString() : "-")} sid={(serviceId.HasValue ? serviceId.Value.ToString() : "-")} channelArgument={Safe(channelArgument)} rule=release_contract");
            return true;
        }
    }

    public bool SetViewerState(string? leaseId, string state, string launchResult, string tuneResult, string activateResult, string rollbackResult)
    {
        if (string.IsNullOrWhiteSpace(leaseId)) return false;
        lock (_gate)
        {
            if (!_leases.TryGetValue(leaseId.Trim(), out var entry) || entry.IsReleased)
                return false;
            entry.SetViewerState(state, launchResult, tuneResult, activateResult, rollbackResult);
            return true;
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
        lock (_gate)
        {
            if (!_leases.TryGetValue(leaseId.Trim(), out var entry) || entry.IsReleased)
                return false;

            entryReleaseUnsafe(entry);
            _leases.Remove(entry.LeaseId);
            _log.Add("EXTERNAL_TUNER", "Released",
                $"source={(source ?? entry.Source)} clientId={entry.ClientId ?? "-"} leaseId={entry.LeaseId} group={entry.Group} tuner={entry.TunerName}");
            return true;
        }
    }

    public IReadOnlyList<ExternalTunerLeaseDto> GetActiveLeases()
    {
        lock (_gate)
        {
            CleanupReleasedUnsafe();
            return _leases.Values
                .Where(x => !x.IsReleased)
                .OrderBy(x => x.AcquiredAt)
                .Select(x => x.ToDto())
                .ToList();
        }
    }

    private void CleanupReleasedUnsafe()
    {
        foreach (var key in _leases.Where(kv => kv.Value.IsReleased).Select(kv => kv.Key).ToList())
            _leases.Remove(key);
    }

    private void entryReleaseUnsafe(ExternalTunerLeaseEntry entry)
    {
        entry.Release();
    }

    private static ExternalTunerLeaseCompatibility EvaluateReuseCompatibility(ExternalTunerLeaseEntry existing, ExternalTunerLeaseRequest request, string requiredGroup)
    {
        if (!string.Equals(NormalizeGroup(existing.Group), requiredGroup, StringComparison.OrdinalIgnoreCase))
            return ExternalTunerLeaseCompatibility.Blocked("group_mismatch");

        var requestedProfileId = NormalizeViewerProfileId(request.ViewerProfileId);
        if (!string.Equals(NormalizeViewerProfileId(existing.ViewerProfileId), requestedProfileId, StringComparison.OrdinalIgnoreCase))
            return ExternalTunerLeaseCompatibility.Blocked("viewer_profile_mismatch");

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

    private static string NormalizeViewerProfileId(string? value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "tvtest1" : value.Trim().ToLowerInvariant();
        if (v is "default" or "auto" or "") return "tvtest1";
        if (v is "tvtest") return "tvtest1";
        if (int.TryParse(v, out var n) && n > 0) return $"tvtest{n}";
        if (v.StartsWith("tvtest", StringComparison.OrdinalIgnoreCase) && int.TryParse(v[6..], out var ordinal) && ordinal > 0)
            return $"tvtest{ordinal}";
        return "tvtest1";
    }

    internal static string ViewerProfileDisplayName(string id)
    {
        var normalized = NormalizeViewerProfileId(id);
        if (normalized.StartsWith("tvtest", StringComparison.OrdinalIgnoreCase) && normalized.Length > 6)
            return "TVTest" + normalized[6..];
        return "TVTest1";
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
    // reused for BS/CS viewerStart requests that require a BSCS tuner/BonDriver.
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

    // release_contract: viewerProfile はautoを内部状態へ残さず、放送波を跨いだTVTest枠IDとして扱う。
    // tvtest1 + GR はGR側1番目のViewingチューナー、tvtest1 + BSCS はBSCS側1番目のViewingチューナーを使う。
    public int ViewerProfileFrameIndex { get; set; }
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
    string ViewerProfileId = "tvtest1",
    string ViewerProfileName = "TVTest1",
    string? TvTestPathKey = null);

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
        ViewerProfileId = string.IsNullOrWhiteSpace(viewerProfileId) ? "tvtest1" : viewerProfileId;
        ViewerProfileName = string.IsNullOrWhiteSpace(viewerProfileName)
            ? ExternalTunerLeaseService.ViewerProfileDisplayName(ViewerProfileId)
            : viewerProfileName;
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
    public string? Note { get; }
    public ushort? NetworkId { get; private set; }
    public ushort? TransportStreamId { get; private set; }
    public ushort? ServiceId { get; private set; }
    public int? ChannelSpace { get; private set; }
    public int? ChannelIndex { get; private set; }
    public string? RequiredBonDriverFileName { get; }
    public string ViewerProfileId { get; }
    public string ViewerProfileName { get; }
    public string? TvTestPathKey { get; }
    public string ViewerState { get; private set; }
    public string LaunchResult { get; private set; }
    public string TuneResult { get; private set; }
    public string ActivateResult { get; private set; }
    public string RollbackResult { get; private set; }
    public string? ChannelArgument { get; private set; }
    public bool IsReleased { get; private set; }

    public void UpdateTarget(ushort? networkId, ushort? transportStreamId, ushort? serviceId, int? channelSpace, int? channelIndex)
    {
        if (networkId.HasValue) NetworkId = networkId;
        if (transportStreamId.HasValue) TransportStreamId = transportStreamId;
        if (serviceId.HasValue) ServiceId = serviceId;
        if (channelSpace.HasValue) ChannelSpace = channelSpace;
        if (channelIndex.HasValue) ChannelIndex = channelIndex;
    }

    public void AttachViewerProcess(int processId, string channelArgument, string state, string launchResult, string tuneResult, string activateResult)
    {
        ProcessId = processId;
        ChannelArgument = channelArgument;
        ViewerState = state;
        LaunchResult = launchResult;
        TuneResult = tuneResult;
        ActivateResult = activateResult;
        RollbackResult = "notNeeded";
        _lease.SetProcessId(processId);
    }

    public void SetViewerState(string state, string launchResult, string tuneResult, string activateResult, string rollbackResult)
    {
        ViewerState = state;
        LaunchResult = launchResult;
        TuneResult = tuneResult;
        ActivateResult = activateResult;
        RollbackResult = rollbackResult;
    }

    public void Release()
    {
        if (IsReleased) return;
        IsReleased = true;
        _lease.Dispose();
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
        TvTestPathKey);
}
