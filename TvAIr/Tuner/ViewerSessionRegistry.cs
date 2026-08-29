using TvAIr.Core;
using TvAIr.Plugin;
using TvAIrPlugin;

namespace TvAIr.Tuner;

/// <summary>
/// TvAIr本体が所有するViewer Sessionの正本。
/// SessionIdとGenerationはLeaseIdとは別責務として管理する。
/// </summary>
public sealed class ViewerSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ViewerSessionState> _byLeaseId = new(StringComparer.OrdinalIgnoreCase);
    private readonly LogRepository _log;
    private readonly PluginTypedEventHub _typedEvents;

    public ViewerSessionRegistry(LogRepository log, PluginTypedEventHub typedEvents)
    {
        _log = log;
        _typedEvents = typedEvents;
    }

    /// <summary>
    /// 現在の管理対象Viewer leaseをSession正本へ同期する。
    /// 同じleaseではSessionIdを維持し、実状態が変わった場合だけGenerationを進める。
    /// </summary>
    public IReadOnlyList<ViewerSessionState> Synchronize(IReadOnlyList<ExternalTunerLeaseDto> activeLeases)
    {
        activeLeases ??= Array.Empty<ExternalTunerLeaseDto>();
        lock (_gate)
        {
            var activeLeaseIds = new HashSet<string>(
                activeLeases.Where(x => !string.IsNullOrWhiteSpace(x.LeaseId)).Select(x => x.LeaseId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var stale in _byLeaseId.Keys.Where(x => !activeLeaseIds.Contains(x)).ToList())
            {
                var removed = _byLeaseId[stale];
                _byLeaseId.Remove(stale);
                _log.Add("VIEWER_SESSION", "Closed",
                    $"viewerSessionId={removed.ViewerSessionId} viewerProfileId={Safe(removed.ViewerProfileId)} logicalViewerSlotId={Safe(removed.LogicalViewerSlotId)} leaseId={Safe(removed.LeaseId)} generation={removed.Generation} reason=lease_not_active rule=viewer_session_contract");
                PublishChanged("Closed", removed, "lease_not_active");
            }

            foreach (var lease in activeLeases)
            {
                if (string.IsNullOrWhiteSpace(lease.LeaseId))
                    continue;

                if (!_byLeaseId.TryGetValue(lease.LeaseId, out var current))
                {
                    current = ViewerSessionState.FromLease(Guid.NewGuid().ToString("N"), 1, lease);
                    _byLeaseId[lease.LeaseId] = current;
                    _log.Add("VIEWER_SESSION", "Created",
                        $"viewerSessionId={current.ViewerSessionId} viewerProfileId={Safe(current.ViewerProfileId)} logicalViewerSlotId={Safe(current.LogicalViewerSlotId)} leaseId={Safe(current.LeaseId)} generation={current.Generation} rule=viewer_session_contract");
                    PublishChanged("Created", current, "lease_active");
                    continue;
                }

                if (!current.HasSameObservableState(lease))
                {
                    var next = ViewerSessionState.FromLease(current.ViewerSessionId, checked(current.Generation + 1), lease);
                    _byLeaseId[lease.LeaseId] = next;
                    _log.Add("VIEWER_SESSION", "GenerationAdvanced",
                        $"viewerSessionId={next.ViewerSessionId} viewerProfileId={Safe(next.ViewerProfileId)} logicalViewerSlotId={Safe(next.LogicalViewerSlotId)} leaseId={Safe(next.LeaseId)} generation={next.Generation} previousGeneration={current.Generation} rule=viewer_session_contract");
                    PublishChanged("GenerationAdvanced", next, $"previousGeneration={current.Generation}");
                }
            }

            return _byLeaseId.Values
                .OrderBy(x => x.AcquiredAt)
                .Select(x => x with { })
                .ToList();
        }
    }

    public ViewerSessionState? GetBySessionId(string? viewerSessionId)
    {
        if (string.IsNullOrWhiteSpace(viewerSessionId)) return null;
        lock (_gate)
        {
            return _byLeaseId.Values.FirstOrDefault(x =>
                string.Equals(x.ViewerSessionId, viewerSessionId.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public ViewerSessionState? GetByLeaseId(string? leaseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId)) return null;
        lock (_gate)
        {
            return _byLeaseId.TryGetValue(leaseId.Trim(), out var session)
                ? session with { }
                : null;
        }
    }


    private void PublishChanged(string change, ViewerSessionState session, string reason)
    {
        _typedEvents.Publish(new TvAirEventDto
        {
            EventType = TvAirEventType.ViewerSessionChanged,
            EntityId = $"viewer-session:{session.ViewerSessionId}",
            EntityVersion = session.Generation,
            DataRevision = session.Generation,
            ChangedFields = new[] { change },
            Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["change"] = change,
                ["reason"] = reason,
                ["viewerProfileId"] = session.ViewerProfileId,
                ["viewerSessionId"] = session.ViewerSessionId,
                ["logicalViewerSlotId"] = session.LogicalViewerSlotId,
                ["leaseId"] = session.LeaseId,
                ["generation"] = session.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["viewerState"] = session.ViewerState,
                ["networkId"] = session.NetworkId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["transportStreamId"] = session.TransportStreamId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["serviceId"] = session.ServiceId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            }
        });
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace('\r', ' ').Replace('\n', ' ');
}

public sealed record ViewerSessionState(
    string ViewerSessionId,
    string ViewerProfileId,
    string LogicalViewerSlotId,
    string LeaseId,
    long Generation,
    DateTime AcquiredAt,
    int? ProcessId,
    ushort? NetworkId,
    ushort? TransportStreamId,
    ushort? ServiceId,
    int? ChannelSpace,
    int? ChannelIndex,
    string ViewerState,
    string ChannelArgument)
{
    public static ViewerSessionState FromLease(string sessionId, long generation, ExternalTunerLeaseDto lease)
        => new(
            sessionId,
            lease.ViewerProfileId ?? string.Empty,
            lease.LogicalViewerSlotId ?? string.Empty,
            lease.LeaseId,
            generation,
            lease.AcquiredAt,
            lease.ProcessId,
            lease.NetworkId,
            lease.TransportStreamId,
            lease.ServiceId,
            lease.ChannelSpace,
            lease.ChannelIndex,
            lease.ViewerState ?? string.Empty,
            lease.ChannelArgument ?? string.Empty);

    public bool HasSameObservableState(ExternalTunerLeaseDto lease)
        => string.Equals(ViewerProfileId, lease.ViewerProfileId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
           && string.Equals(LogicalViewerSlotId, lease.LogicalViewerSlotId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
           && ProcessId == lease.ProcessId
           && NetworkId == lease.NetworkId
           && TransportStreamId == lease.TransportStreamId
           && ServiceId == lease.ServiceId
           && ChannelSpace == lease.ChannelSpace
           && ChannelIndex == lease.ChannelIndex
           && string.Equals(ViewerState, lease.ViewerState ?? string.Empty, StringComparison.OrdinalIgnoreCase)
           && string.Equals(ChannelArgument, lease.ChannelArgument ?? string.Empty, StringComparison.Ordinal);
}
